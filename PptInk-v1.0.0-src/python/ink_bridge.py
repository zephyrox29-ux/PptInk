#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PptInk AI 桥（AI1.0：笔迹拟合与图形识别）
==========================================
- 传输：子进程 stdio 行式 JSON（与 C# PythonBridge 严格对齐：一行一个 JSON 对象）。
  审查修复：原实现用 multiprocessing.connection 的 pickle 二进制帧，与 C# 的文本行协议
  不兼容 → AI 形状规整从未真正工作。现改为 stdin/stdout 行式 JSON。
- 协议：请求 {"cmd":...}；响应 {"ok":true,...}。日志一律走 stderr（严禁污染 stdout 协议流）。
- 能力：
    * recognize：手绘图形自动规整
        - 直线 / 圆 / 椭圆 / 矩形 / 三角形 / 箭头
        - 算法：RDP 简化 + 几何特征（闭合度、圆度、角点）+ 最小二乘拟合
        - 离线、毫秒级（86 寸整笔 200 点 < 3ms）
- 依赖：numpy 必需；opencv-python 可选（角点与 minAreaRect 增强），缺失自动退化
- 运行形态：由 C# 主进程按需拉起的独立子进程（AI 未启用不驻留内存）
"""

import json
import math
import sys

import numpy as np

# 识别引擎（v2.1）：全部算法在 pptink_recog 模块内（形状开关 + 参数可调 + 回归门禁）。
# 本文件只负责协议服务循环，保持与 C# PythonBridge 的行式 JSON 契约不变。
from pptink_recog import (recognize as _recognize, RecognizerConfig,
                          DEFAULT_SHAPES, ALL_SHAPES)

try:
    import cv2  # noqa: F401  可选增强
    HAS_CV2 = True
except ImportError:
    HAS_CV2 = False


# ---------------------------------------------------------------- 历史实现（v1，已被 pptink_recog 取代）
# 保留占位以免删除大段代码时遗漏引用；实际识别一律走 pptink_recog.recognize。
_V1_OBSOLETE = True

# ---------------------------------------------------------------- 几何工具（v1 兼容层）

def rdp(points: np.ndarray, epsilon: float) -> np.ndarray:
    """Ramer-Douglas-Peucker 折线简化：epsilon 为最大垂距容差（px）。"""
    if len(points) < 3:
        return points
    start, end = points[0], points[-1]
    vec = end - start
    seg_len = np.hypot(*vec)
    if seg_len < 1e-9:
        dists = np.hypot(*(points - start).T)
    else:
        # 点到首尾连线的垂距
        cross = np.abs(vec[0] * (points[:, 1] - start[1]) - vec[1] * (points[:, 0] - start[0]))
        dists = cross / seg_len
    i = int(np.argmax(dists))
    if dists[i] <= epsilon:
        return np.array([start, end])
    left = rdp(points[: i + 1], epsilon)
    right = rdp(points[i:], epsilon)
    return np.vstack([left[:-1], right])


def polygon_area(pts: np.ndarray) -> float:
    x, y = pts[:, 0], pts[:, 1]
    return 0.5 * abs(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1)))


def polygon_perimeter(pts: np.ndarray) -> float:
    return float(np.hypot(*(np.diff(np.vstack([pts, pts[:1]]), axis=0).T)).sum())


def count_corners(simple: np.ndarray, turn_thresh: float = 55.0) -> list:
    """角点检测：顶点处转向角（0~180°）超过阈值即角点。矩形拐角≈90°可检出，直线段≈0°不误报。"""
    if len(simple) < 3:
        return []
    corners = []
    n = len(simple)
    for i in range(n):
        a = simple[i] - simple[(i - 1) % n]
        b = simple[(i + 1) % n] - simple[i]
        la, lb = np.hypot(*a), np.hypot(*b)
        if la < 4 or lb < 4:
            continue
        cosang = np.clip(np.dot(a, b) / (la * lb), -1.0, 1.0)
        ang = math.degrees(math.acos(cosang))
        if ang >= turn_thresh:
            corners.append(i)
    return corners


def fit_circle(pts: np.ndarray):
    """最小二乘圆拟合（Kasa 方法）：返回 (cx, cy, r, 最大径向残差)。"""
    x, y = pts[:, 0], pts[:, 1]
    A = np.c_[2 * x, 2 * y, np.ones(len(x))]
    b = x ** 2 + y ** 2
    try:
        c, *_ = np.linalg.lstsq(A, b, rcond=None)
    except np.linalg.LinAlgError:
        return None
    cx, cy = c[0], c[1]
    r = math.sqrt(max(c[2] + cx ** 2 + cy ** 2, 1e-9))
    resid = np.abs(np.hypot(x - cx, y - cy) - r).max()
    return cx, cy, r, float(resid)


def fit_line(pts: np.ndarray):
    """最小二乘直线：返回单位法向量与截距，及最大偏差。"""
    mean = pts.mean(axis=0)
    u, s, vt = np.linalg.svd(pts - mean)
    d = vt[0]                       # 方向向量
    n = np.array([-d[1], d[0]])     # 法向量
    dists = np.abs((pts - mean) @ n)
    return mean, d, float(dists.max())


# ---------------------------------------------------------------- 形状识别

def recognize(points: np.ndarray, closure_tol: float = 0.18):
    """主入口：识别并返回规整后的点序列。"""
    pts = np.asarray(points, dtype=np.float64)
    if len(pts) < 4:
        return None

    # 去重相邻重复点
    keep = np.ones(len(pts), bool)
    keep[1:] = np.hypot(*(np.diff(pts, axis=0).T)) > 0.5
    pts = pts[keep]
    if len(pts) < 4:
        return None

    span = max(pts.max(axis=0) - pts.min(axis=0))
    simple = rdp(pts, max(span * 0.012, 1.5))

    # ---- 直线：RDP 后 2 点，且全点最大垂距 < 1.5% 跨度 ----
    mean, d, max_dev = fit_line(pts)
    if len(simple) == 2 and max_dev < span * 0.015:
        return shape_ok("line", resample_line(pts[0], pts[-1]))

    # ---- 闭合判定：首尾距 < 周长 × 容差 ----
    perim = polygon_perimeter(np.vstack([pts, pts[:1]]))
    gap = float(np.hypot(*(pts[-1] - pts[0])))
    closed = gap < perim * closure_tol or gap < 96

    if closed:
        area = polygon_area(pts)
        circularity = 4 * math.pi * area / max(perim ** 2, 1e-9) if perim > 0 else 0

        # ---- 圆：圆度高 + 圆拟合残差小 ----
        circle = fit_circle(pts)
        if circle and circularity > 0.82:
            cx, cy, r, resid = circle
            if resid < r * 0.14:
                return shape_ok("circle", sample_circle(cx, cy, r))

        # ---- 椭圆：闭合、无尖角、PCA 长短轴显著 ----
        corners = count_corners(simple[:-1] if np.allclose(simple[0], simple[-1]) else simple)
        if not corners and circularity > 0.6:
            ell = fit_ellipse_pca(pts)
            if ell:
                (cx, cy), (ma, MA), ang = ell
                if 1.25 < MA / max(ma, 1e-9) < 4:
                    return shape_ok("ellipse", sample_ellipse(cx, cy, ma, MA, ang))

        # ---- 矩形：4 角点 ----
        if len(corners) == 4:
            rect = rectangle_from_corners(simple, corners)
            if rect is not None:
                return shape_ok("rectangle", rect)

        # ---- 三角形：3 角点 ----
        if len(corners) == 3:
            tri = triangle_from_corners(simple, corners)
            if tri is not None:
                return shape_ok("triangle", tri)

    # ---- 箭头：主轴直线 + 末端分叉（简化：末端 1/8 处两段短线） ----
    arrow = try_arrow(pts, simple, span)
    if arrow is not None:
        return shape_ok("arrow", arrow)

    return None  # 未识别：保持原笔迹


def shape_ok(name, pts, conf=None):
    conf = conf if conf is not None else {"line": 0.97, "circle": 0.95, "ellipse": 0.88,
                                          "rectangle": 0.9, "triangle": 0.9, "arrow": 0.85}[name]
    return {"shape": name, "points": np.round(pts, 1).tolist(), "confidence": conf}


def resample_line(a, b, n=24):
    t = np.linspace(0, 1, n)[:, None]
    return a * (1 - t) + b * t


def sample_circle(cx, cy, r, n=48):
    t = np.linspace(0, 2 * math.pi, n, endpoint=False)
    return np.c_[cx + r * np.cos(t), cy + r * np.sin(t)]


def fit_ellipse_pca(pts):
    mean = pts.mean(axis=0)
    cov = np.cov((pts - mean).T)
    vals, vecs = np.linalg.eigh(cov)
    order = vals.argsort()[::-1]
    vals, vecs = vals[order], vecs[:, order]
    ang = math.atan2(vecs[0, 1], vecs[0, 0])
    return mean, (2 * math.sqrt(vals[1] + 1e-9), 2 * math.sqrt(vals[0] + 1e-9)), math.degrees(ang)


def sample_ellipse(cx, cy, a, b, ang_deg, n=48):
    t = np.linspace(0, 2 * math.pi, n, endpoint=False)
    ang = math.radians(ang_deg)
    x = a * np.cos(t)
    y = b * np.sin(t)
    xr = x * math.cos(ang) - y * math.sin(ang) + cx
    yr = x * math.sin(ang) + y * math.cos(ang) + cy
    return np.c_[xr, yr]


def rectangle_from_corners(simple, corners):
    """取 4 角点构成矩形（相邻角点连线为边，输出 5 点闭合）。"""
    if len(simple) < 4:
        return None
    quad = simple[corners] if max(corners) < len(simple) else None
    if quad is None or len(quad) != 4:
        return None
    # 正交化：取最小面积旋转矩形（有 cv2 用 cv2，无则 PCA 近似）
    if HAS_CV2:
        (cx, cy), (w, h), ang = cv2.minAreaRect(quad.astype(np.float32))
        return np.array([
            [cx - w / 2, cy - h / 2], [cx + w / 2, cy - h / 2],
            [cx + w / 2, cy + h / 2], [cx - w / 2, cy + h / 2], [cx - w / 2, cy - h / 2],
        ])
    # PCA 近似：四点重心的主轴方向
    mean = quad.mean(axis=0)
    _, _, vt = np.linalg.svd(quad - mean)
    d = vt[0]
    proj = (quad - mean) @ d
    along = d * (proj.max() - proj.min()) / 2
    perp = np.array([-d[1], d[0]]) * np.hypot(*(quad[1] - quad[0])) / 2
    c = quad.mean(axis=0)
    return np.array([c - along - perp, c + along - perp, c + along + perp, c - along + perp, c - along - perp])


def triangle_from_corners(simple, corners):
    if max(corners) >= len(simple):
        return None
    tri = simple[corners]
    if len(tri) != 3:
        return None
    return np.vstack([tri, tri[0]])


def try_arrow(pts, simple, span):
    """箭头：主干近似直线 + 末端有分叉（倒数第二段与主干反向夹角小）。"""
    if len(simple) < 4:
        return None
    tip = pts[-1]
    # 主干 = 前半部分
    half = pts[: max(len(pts) // 2, 3)]
    mean, d, dev = fit_line(half)
    if dev > span * 0.03:
        return None
    # 末端两段是否"张开"（朝主干反方向）
    back = -d
    seg_a = pts[min(len(pts) - 3, len(pts) // 2 + 2)] - tip
    seg_b = pts[-3] - tip if len(pts) > 3 else seg_a
    for seg in (seg_a, seg_b):
        n = np.hypot(*seg)
        if n < span * 0.02:
            return None
        cosang = np.dot(seg / n, back)
        if cosang < 0.7:  # 与主干反向夹角 < 45°
            return None
    # 生成规整箭头：主干线 + 两条 45° 短线
    head = min(span * 0.08, 90)
    a = tip - back * head
    left = a + np.array([-back[1], back[0]]) * head * 0.55
    right = a - np.array([-back[1], back[0]]) * head * 0.55
    shaft_start = pts[0]
    out = np.vstack([
        resample_line(shaft_start, tip, 20),
        resample_line(left, tip, 4)[1:],
        resample_line(right, tip, 4)[1:],
    ])
    return out


# ---------------------------------------------------------------- 服务循环

# 会话级识别配置（可由 settings 面板下发；未下发时用 recognizer_config.json）
_session_cfg = RecognizerConfig()


def handle(msg: dict) -> dict:
    global _session_cfg
    cmd = msg.get("cmd")
    if cmd == "hello":
        return {"ok": True, "server": "pptink-bridge", "version": 2, "cv2": HAS_CV2,
                "shapes": list(DEFAULT_SHAPES), "optional_shapes": list(ALL_SHAPES)}
    if cmd == "recognize":
        # Local IPC is still an input boundary: reject malformed/oversized requests before
        # allocating algorithm work arrays. One bad request must not terminate the bridge.
        raw_points = msg.get("points", [])
        if not isinstance(raw_points, list) or len(raw_points) > 8192:
            return {"ok": False, "reason": "invalid_points"}
        try:
            pts = np.asarray(raw_points, dtype=np.float64)
        except (TypeError, ValueError, OverflowError):
            return {"ok": False, "reason": "invalid_points"}
        if pts.ndim != 2 or pts.shape[1] != 2 or not np.isfinite(pts).all():
            return {"ok": False, "reason": "invalid_points"}

        raw_options = msg.get("options") or {}
        if not isinstance(raw_options, dict):
            return {"ok": False, "reason": "invalid_options"}
        opts = dict(raw_options)
        if "closure_tolerance" in msg:
            try:
                opts["closure_tol"] = float(msg["closure_tolerance"])
            except (TypeError, ValueError, OverflowError):
                return {"ok": False, "reason": "invalid_options"}
        result = _recognize(pts, cfg=_session_cfg, options=opts or None)
        if result is None:
            return {"ok": False, "reason": "unrecognized"}
        return {"ok": True, **result}
    if cmd == "config":
        # 更新会话配置（形状开关 + 全部阈值）；enabled 传空表示使用默认集合
        raw_options = msg.get("options") or {}
        if not isinstance(raw_options, dict):
            return {"ok": False, "reason": "invalid_options"}
        opts = dict(raw_options)
        if not opts.get("enabled"):
            opts.pop("enabled", None)
        _session_cfg = _session_cfg.with_overrides(opts)
        return {"ok": True, "config": _session_cfg.to_dict()}
    if cmd == "shapes":
        return {"ok": True, "enabled": sorted(_session_cfg.enabled),
                "implemented": list(ALL_SHAPES)}
    if cmd == "shutdown":
        return {"ok": True, "bye": True}
    return {"ok": False, "reason": f"unknown cmd {cmd}"}


def main():
    # stdio 行式 JSON 服务循环（与 C# PythonBridge 严格对齐）。
    # 严禁向 stdout 打印任何非协议内容（日志一律 stderr），否则污染协议流。
    try:
        sys.stdin.reconfigure(encoding="utf-8")
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    print(f"[pptink-bridge] stdio ready (cv2={HAS_CV2})", file=sys.stderr)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except (json.JSONDecodeError, ValueError):
            resp = {"ok": False, "reason": "invalid_json"}
        else:
            if not isinstance(msg, dict):
                resp = {"ok": False, "reason": "invalid_request"}
            else:
                try:
                    resp = handle(msg)
                except Exception as exc:  # noqa: BLE001 - keep the long-lived IPC worker alive
                    print(f"[pptink-bridge] request failed: {type(exc).__name__}: {exc}", file=sys.stderr)
                    resp = {"ok": False, "reason": "request_failed"}
        try:
            encoded = json.dumps(resp, ensure_ascii=False, allow_nan=False)
        except (TypeError, ValueError, OverflowError) as exc:
            # A malformed/unexpected response must never terminate the long-lived stdio worker.
            print(f"[pptink-bridge] response serialization failed: {type(exc).__name__}: {exc}", file=sys.stderr)
            encoded = '{"ok":false,"reason":"response_serialization_failed"}'
        sys.stdout.write(encoded + "\n")
        sys.stdout.flush()
        if resp.get("bye"):
            return


if __name__ == "__main__":
    main()
