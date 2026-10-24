#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PptInk 手绘图形识别引擎 v2
==========================
目标：把手绘笔迹规整为「干净、几何正确、符合意图」的形状（对齐 Apple Freeform
自动形状手感：能认的形状认对、认出的形状规整、认不出的给更顺滑的自然曲线）。

六阶段流水线：
  1. 预处理   去重 → 高斯平滑（端点保位）→ 等弧长重采样（采样密度/速度无关化）
  2. 特征提取 闭合度 / 圆度（缺口计入周长）/ 简化顶点与角点（RDP+转向筛选）/
              凹槽聚类（凸包内侧深度 + 弧长连通聚簇）/ 镜像对称（弧长参数配对）/
              直线性 / 椭圆主轴比 / 末端分叉（主干坐标系 u 回退 + v 双侧）
  3. 假设生成 15 类：直线 / 箭头 / 对勾 / 叉 / 三角形族 / 四边形族 / 正多边形 /
              通用多边形 / 圆 / 椭圆 / 星形 / 心形 / 波浪 / 云朵 / 自由曲线（美化兜底）
  4. 几何拟合 直线（TLS）/ 圆（Taubin）/ 椭圆（DLS）/ 多边形（扇区顶点 + 正交化 + 规范化）
  5. 竞争评分 几何残差（按跨度归一化，指数衰减）+ 结构匹配度 → 统一置信度 + 备份候选
  6. 参数化重建 直线吸附 / 矩形正交化 / 顶点规范化 / 圆椭圆精确参数 / 箭头头部 / 星与心参数

协议兼容：recognize() 返回 {shape, points, confidence, params, alternatives}；
C# PythonBridge 仅依赖 shape/points/confidence。异常一律吞掉返回 None（保持原笔迹）。
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field

import numpy as np

# ============================================================ 配置（形状开关 + 全参数可调）

# 默认启用的形状：聚焦课堂批注高频图形（直线/箭头/虚线/点线/对勾/叉/三角形/四边形族/
# 多边形/圆/椭圆/波浪）。心形·星形·云朵等低频装饰图形默认关闭，可按需开启。
DEFAULT_SHAPES = ("line", "arrow", "dashed", "dotted", "check", "cross",
                  "triangle", "quad", "polygon", "circle", "ellipse", "wave")
ALL_SHAPES = DEFAULT_SHAPES + ("heart", "star", "cloud")


class RecognizerConfig:
    """识别器配置：所有判据阈值与形状开关都可调。
    来源优先级：调用参数 options > 会话 config > 配置文件 recognizer_config.json > 默认。
    配置文件示例（同目录 recognizer_config.json）：
        {"enabled": ["line", "arrow", "circle"],
         "score_threshold": 0.55, "snap_angle": 6.0,
         "tolerances": {"line": 0.022, "circle": 0.045}}
    """

    def __init__(self, d=None):
        d = d if isinstance(d, dict) else {}

        def finite_float(key, default, low, high):
            try:
                value = float(d.get(key, default))
            except (TypeError, ValueError, OverflowError):
                return float(default)
            if not math.isfinite(value):
                return float(default)
            return min(high, max(low, value))

        def bounded_int(key, default, low, high):
            try:
                value = int(d.get(key, default))
            except (TypeError, ValueError, OverflowError):
                return int(default)
            return min(high, max(low, value))

        raw_enabled = d.get("enabled", DEFAULT_SHAPES)
        if not isinstance(raw_enabled, (list, tuple, set)):
            raw_enabled = DEFAULT_SHAPES
        self.enabled = {name for name in raw_enabled if isinstance(name, str) and name in ALL_SHAPES}
        if not self.enabled:
            self.enabled = set(DEFAULT_SHAPES)

        self.closure_tol = finite_float("closure_tol", 0.18, 0.01, 0.50)
        self.score_threshold = finite_float("score_threshold", 0.52, 0.0, 1.0)
        self.min_points = bounded_int("min_points", 4, 2, 8192)
        self.snap_angle = finite_float("snap_angle", 7.0, 0.0, 90.0)
        self.line_dev_ratio = finite_float("line_dev_ratio", 0.022, 0.001, 0.20)
        self.corner_turn_min = finite_float("corner_turn_min", 52.0, 0.0, 180.0)
        self.resample_step = finite_float("resample_step", 4.0, 0.5, 40.0)
        self.smooth_sigma = finite_float("smooth_sigma", 1.2, 0.0, 20.0)

        self.tolerances = dict(SHAPE_TOL)
        raw_tolerances = d.get("tolerances", {})
        if isinstance(raw_tolerances, dict):
            for name, value in raw_tolerances.items():
                if name not in ALL_SHAPES:
                    continue
                try:
                    value = float(value)
                except (TypeError, ValueError, OverflowError):
                    continue
                if math.isfinite(value):
                    self.tolerances[name] = min(0.50, max(0.001, value))

        # 线型（虚线/点线）判据。边界限制既保护算法，也限制异常配置的 CPU 成本。
        self.dash_min_gaps = bounded_int("dash_min_gaps", 3, 1, 32)
        self.dash_max_cv = finite_float("dash_max_cv", 0.45, 0.0, 2.0)
        self.dash_skeleton_dev = finite_float("dash_skeleton_dev", 0.070, 0.001, 0.50)
        self.dotted_max_points_per_dash = bounded_int("dotted_max_points_per_dash", 2, 1, 16)

    def with_overrides(self, d):
        if not d:
            return self
        m = self.to_dict()
        m.update(d)
        return RecognizerConfig(m)

    def to_dict(self):
        return {"enabled": sorted(self.enabled), "closure_tol": self.closure_tol,
                "score_threshold": self.score_threshold, "min_points": self.min_points,
                "snap_angle": self.snap_angle, "line_dev_ratio": self.line_dev_ratio,
                "corner_turn_min": self.corner_turn_min,
                "resample_step": self.resample_step, "smooth_sigma": self.smooth_sigma,
                "tolerances": self.tolerances, "dash_min_gaps": self.dash_min_gaps,
                "dash_max_cv": self.dash_max_cv, "dash_skeleton_dev": self.dash_skeleton_dev,
                "dotted_max_points_per_dash": self.dotted_max_points_per_dash}

    def tol(self, shape):
        return float(self.tolerances.get(shape, SHAPE_TOL.get(shape, 0.08)))


def load_config_file(path=None):
    """从配置文件加载（缺失/损坏时返回默认配置 —— 识别链路绝不因配置崩）。"""
    import json
    import os
    path = path or os.path.join(os.path.dirname(os.path.abspath(__file__)), "recognizer_config.json")
    try:
        with io_open(path, "r", encoding="utf-8") as fh:
            return RecognizerConfig(json.load(fh))
    except Exception:
        return RecognizerConfig()


def io_open(path, mode, encoding="utf-8"):
    import builtins
    return builtins.open(path, mode, encoding=encoding)


# ============================================================ 常量与调参

MIN_POINTS = 4
RESAMPLE_STEP = 4.0
SMOOTH_SIGMA = 1.2
CLOSE_RATIO = 0.18
CLOSE_ABS = 64.0
SCORE_THRESHOLD = 0.52
LINE_DEV_RATIO = 0.030
SNAP_ANGLE = 7.0
STAR_MIN_RAYS = 3
CURVE_MIN_SAMPLES = 6

SHAPE_TOL = {
    "line": 0.022, "arrow": 0.060, "triangle": 0.055, "quad": 0.070,
    "polygon": 0.060, "circle": 0.045, "ellipse": 0.055, "star": 0.075,
    "heart": 0.080, "check": 0.055, "cross": 0.055, "wave": 0.075,
    "cloud": 0.085, "curve": 0.120,
}


# ============================================================ 基础几何

def span_of(pts):
    if len(pts) == 0:
        return 0.0
    d = pts.max(axis=0) - pts.min(axis=0)
    return float(max(d[0], d[1]))


def centroid(pts):
    return pts.mean(axis=0)


def dedupe(pts, eps=0.6):
    if len(pts) < 2:
        return pts
    keep = np.ones(len(pts), bool)
    keep[1:] = np.hypot(np.diff(pts[:, 0]), np.diff(pts[:, 1])) > eps
    return pts[keep]


def smooth_gaussian(pts, sigma=SMOOTH_SIGMA):
    """高斯平滑（端点保位 + 镜像延拓）：抑制手抖、保留拐角锐度。"""
    n = len(pts)
    if n < 5 or sigma <= 0:
        return pts
    half = max(int(round(3 * sigma)), 1)
    if n <= half + 1:
        return pts
    k = np.exp(-0.5 * (np.arange(-half, half + 1) / sigma) ** 2)
    k /= k.sum()
    head = pts[0] + (pts[0] - pts[1:half + 1][::-1])
    tail = pts[-1] + (pts[-1] - pts[-half - 1:-1][::-1])
    ext = np.vstack([head, pts, tail])
    out = np.empty_like(pts, dtype=np.float64)
    for j in range(2):
        out[:, j] = np.convolve(ext[:, j], k, mode="valid")
    out[0], out[-1] = pts[0], pts[-1]
    return out


def resample_uniform(pts, step=RESAMPLE_STEP):
    """等弧长重采样：让识别对报点密度/笔速完全无关。"""
    n = len(pts)
    if n < 2:
        return pts
    seg = np.hypot(np.diff(pts[:, 0]), np.diff(pts[:, 1]))
    total = float(seg.sum())
    if total < step * 2:
        return pts
    cum = np.concatenate([[0.0], np.cumsum(seg)])
    m = max(int(total / step) + 1, 3)
    t = np.linspace(0, total, m)
    return np.c_[np.interp(t, cum, pts[:, 0]), np.interp(t, cum, pts[:, 1])]


def rdp(pts, epsilon):
    """RDP 折线简化（迭代实现）。"""
    n = len(pts)
    if n < 3:
        return pts.copy()
    keep = np.zeros(n, bool)
    keep[0] = keep[-1] = True
    stack = [(0, n - 1)]
    while stack:
        i, j = stack.pop()
        if j <= i + 1:
            continue
        a, b = pts[i], pts[j]
        vec = b - a
        seg_len = float(np.hypot(vec[0], vec[1]))
        sub = pts[i + 1:j]
        if seg_len < 1e-9:
            dists = np.hypot(sub[:, 0] - a[0], sub[:, 1] - a[1])
        else:
            cross = np.abs(vec[0] * (sub[:, 1] - a[1]) - vec[1] * (sub[:, 0] - a[0]))
            dists = cross / seg_len
        k = int(np.argmax(dists))
        if dists[k] > epsilon:
            keep[i + 1 + k] = True
            stack.append((i, i + 1 + k))
            stack.append((i + 1 + k, j))
    return pts[keep]


def turn_angles(verts, closed):
    """顶点转向角（0~180°）。"""
    n = len(verts)
    if n < 3:
        return np.zeros(0)
    if closed:
        prev = verts[(np.arange(n) - 1) % n]
        nxt = verts[(np.arange(n) + 1) % n]
    else:
        prev, nxt = verts[:-2], verts[2:]
        verts = verts[1:-1]
    a, b = verts - prev, nxt - verts
    la = np.hypot(a[:, 0], a[:, 1]); lb = np.hypot(b[:, 0], b[:, 1])
    ok = (la > 1e-6) & (lb > 1e-6)
    cos = np.zeros(len(verts))
    cos[ok] = np.clip((a[ok, 0] * b[ok, 0] + a[ok, 1] * b[ok, 1]) / (la[ok] * lb[ok]), -1, 1)
    ang = np.degrees(np.arccos(cos)); ang[~ok] = 0.0
    return ang


def corner_vertices(pts, closed, turn_min=None):
    turn_min = _ACTIVE_CFG.corner_turn_min if turn_min is None else turn_min
    """角点顶点提取（v2 核心修正 1）：重采样会摊薄拐角、单点转向角不足，
    故先 RDP 凝练几何顶点，再按转向角筛选。直线→2 点；多边形→边数级顶点；
    圆弧→较多顶点但单个转向小（自然滤除）；波浪→波峰转向平缓（滤除）。"""
    span = span_of(pts)
    verts = rdp(pts, max(span * 0.020, 1.6))
    if closed and len(verts) > 3 and float(np.hypot(*(verts[0] - verts[-1]))) < span * 0.08:
        verts = verts[:-1]
    if len(verts) < 3:
        return verts, False
    ang = turn_angles(verts, closed)
    if len(ang) == 0:
        return verts, closed
    # 掩码对齐（致命修复）：非闭合时 turn_angles 长度 = len(verts)-2（对应 1..n-2），
    # 直接布尔索引会长度不匹配 → 异常 → 上游静默返回 None（arrow/check/cross/wave 全灭根因）
    hit = ang >= turn_min
    mask = np.zeros(len(verts), bool)
    if closed:
        mask[:] = hit
        sel_ang = ang[hit]
    else:
        mask[1:-1] = hit           # ang 对应 1..n-2，写回全长掩码
        sel_ang = ang[hit]         # 长度与 sel 一致（关键：勿用长度不等的 mask 索引 ang）
    if hit.sum() < 3:
        return verts, closed
    sel = verts[mask]
    # 光滑曲线过滤（v2 修正）：圆弧/椭圆经 RDP 会产生许多"顶点"，但其转向角彼此接近
    # （曲率均匀）—— 真实多边形的角点转向角离散度高。据此把圆/椭圆判为"无角点"。
    if len(sel) >= 6 and float(np.std(sel_ang)) < 14.0:
        return np.zeros((0, 2)), closed
    return sel, closed


def concave_slots(pts, hull_idx=None):
    """凹槽聚类（v2 修正 2b）：凸包内部 + 到最近凸包边的距离。
    关键认知（两次踩坑后定论）：凸包顶点在原始序列中并不连续，"按凸包边切轮廓段"
    的两指针假设不成立；而"到各边距离的最大值"衡量的是离边界之远近（星形整臂都会
    很大，连成一片）。正确度量 = 该点严格位于凸包内部时，其到凸包边界的最近距离 ——
    外尖在凸包上（0）、臂上点中等、内凹点最深（峰），故每臂天然形成一个深度峰，
    峰间在外尖处归零（自动断开）。星形=射线数峰、心形=1 深峰、云朵=若干浅峰、
    凸多边形/圆/椭圆=0 峰。返回 [(归一化深度, 峰值点索引)]。"""
    n = len(pts)
    if n < 6:
        return []
    span = span_of(pts)
    if span < 1e-6:
        return []
    if hull_idx is None:
        hull_idx = convex_hull(pts)
    if len(hull_idx) < 3:
        return []
    hull = pts[hull_idx]
    sa = 0.5 * float(np.dot(hull[:, 0], np.roll(hull[:, 1], -1)) -
                     np.dot(hull[:, 1], np.roll(hull[:, 0], -1)))
    orient = 1.0 if sa > 0 else -1.0
    m = len(hull)
    inside = np.ones(n, bool)
    dist = np.full(n, np.inf)
    for k in range(m):
        a, b = hull[k], hull[(k + 1) % m]
        v = b - a
        L = float(np.hypot(v[0], v[1]))
        if L < 1e-6:
            continue
        cross = orient * (v[0] * (pts[:, 1] - a[1]) - v[1] * (pts[:, 0] - a[0])) / L
        inside &= cross > 1e-6            # 严格内部（抗数值噪声）
        dist = np.minimum(dist, np.abs(cross))
    depth = np.where(inside, dist, 0.0)
    thr = span * 0.030
    slots, i = [], 0
    while i < n:
        if depth[i] > thr:
            j = i
            while j + 1 < n and depth[j + 1] > thr:
                j += 1
            pk = int(i + np.argmax(depth[i:j + 1]))
            slots.append((float(depth[pk] / span), pk))
            i = j + 1
        else:
            i += 1
    if len(slots) >= 2 and depth[0] > thr and depth[-1] > thr:
        slots = slots[1:] if slots[0][0] >= slots[-1][0] else slots[:-1]
    return slots


def mirror_symmetry(pts):
    """镜像对称度（v2 修正 3）：关于主轴中线的分箱配对。
    先前按投影参数插值配对，对闭合环无效（投影参数在环上非单调 → w(t) 多值）。
    分箱法：以主轴投影中位数把点分左右两半，按 |t-t_med| 归一化分箱，比较两半
    同箱内的法向坐标均值 —— O(n)，对开环/闭环、旋转、伸缩均稳健。
    返回 0~1（1=完全对称）。"""
    n = len(pts)
    if n < 8:
        return 0.0
    c = centroid(pts)
    q = pts - c
    span = span_of(pts)
    if span < 1e-6:
        return 0.0
    try:
        _, _, vt = np.linalg.svd(q, full_matrices=False)
    except np.linalg.LinAlgError:
        return 0.0
    d = vt[0]
    nn = np.array([-d[1], d[0]])
    t = q @ d
    w = q @ nn
    t_med = float(np.median(t))
    u = np.abs(t - t_med)
    umax = float(u.max())
    if umax < 1e-6:
        return 0.0
    un = u / umax
    K = 10
    bins = np.clip((un * K).astype(int), 0, K - 1)
    left = t < t_med
    errs = []
    for b in range(K):
        selL = (bins == b) & left
        selR = (bins == b) & ~left
        if selL.sum() < 2 or selR.sum() < 2:
            continue
        errs.append(abs(float(w[selL].mean()) - float(w[selR].mean())))
    if not errs:
        return 0.0
    err = float(np.median(errs)) / span
    return float(max(0.0, 1.0 - err * 6.0))


def polygon_area(pts):
    x, y = pts[:, 0], pts[:, 1]
    return 0.5 * abs(float(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1))))


def perimeter(pts, closed):
    d = np.diff(np.vstack([pts, pts[:1]]) if closed else pts, axis=0)
    return float(np.hypot(d[:, 0], d[:, 1]).sum()) if len(d) else 0.0


def convex_hull(pts):
    n = len(pts)
    if n < 3:
        return np.arange(n)
    order = np.lexsort((pts[:, 1], pts[:, 0]))

    def half(idx):
        st = []
        for i in idx:
            while len(st) >= 2:
                o, a = st[-2], st[-1]
                if (pts[a, 0] - pts[o, 0]) * (pts[i, 1] - pts[o, 1]) - \
                   (pts[a, 1] - pts[o, 1]) * (pts[i, 0] - pts[o, 0]) <= 0:
                    st.pop()
                else:
                    break
            st.append(i)
        return st

    return np.array(half(order)[:-1] + half(order[::-1])[:-1], int)


def polyline_residual(pts, poly, closed, q=92.0):
    """点到折线距离的 q 分位（残差，抗手抖尖峰）。"""
    if len(poly) < 2:
        return float("inf")
    segs = np.vstack([poly, poly[:1]]) if closed else poly
    best = np.full(len(pts), np.inf)
    for k in range(len(segs) - 1):
        a, b = segs[k], segs[k + 1]
        v = b - a
        L2 = float(v[0] ** 2 + v[1] ** 2)
        if L2 < 1e-12:
            d = np.hypot(pts[:, 0] - a[0], pts[:, 1] - a[1])
        else:
            t = np.clip(((pts - a) @ v) / L2, 0, 1)
            proj = a + t[:, None] * v
            d = np.hypot(pts[:, 0] - proj[:, 0], pts[:, 1] - proj[:, 1])
        best = np.minimum(best, d)
    return float(np.percentile(best, q))


# ============================================================ 几何拟合

def fit_line_tls(pts):
    c = centroid(pts)
    centered = pts - c
    try:
        _, _, vt = np.linalg.svd(centered, full_matrices=False)
    except np.linalg.LinAlgError:
        return c, np.array([1.0, 0.0]), float("inf")
    d = vt[0]
    n = np.array([-d[1], d[0]])
    return c, d / max(float(np.hypot(d[0], d[1])), 1e-9), float(np.abs(centered @ n).max())


def fit_circle_taubin(pts):
    x = pts[:, 0].astype(float); y = pts[:, 1].astype(float)
    mx, my = x.mean(), y.mean()
    u, v = x - mx, y - my
    Suu = (u * u).mean(); Svv = (v * v).mean(); Suv = (u * v).mean()
    Suuu = (u ** 3).mean(); Svvv = (v ** 3).mean()
    Suvv = (u * v * v).mean(); Svuu = (v * u * u).mean()
    A = np.array([[Suu, Suv], [Suv, Svv]])
    b = 0.5 * np.array([Suuu + Suvv, Svvv + Svuu])
    try:
        uc, vc = np.linalg.solve(A, b)
    except np.linalg.LinAlgError:
        return None
    cx, cy = uc + mx, vc + my
    r = math.sqrt(max(uc * uc + vc * vc + Suu + Svv, 1e-9))
    d = np.hypot(x - cx, y - cy) - r
    return float(cx), float(cy), float(r), float(np.abs(d).max())


def fit_ellipse_dls(pts):
    x = pts[:, 0].astype(float); y = pts[:, 1].astype(float)
    if len(pts) < 6:
        return None
    D = np.c_[x * x, x * y, y * y, x, y, np.ones_like(x)]
    S = D.T @ D
    C = np.zeros((6, 6)); C[0, 2] = C[2, 0] = 2.0; C[1, 1] = -1.0
    try:
        w, vec = np.linalg.eig(np.linalg.solve(S, C))
    except np.linalg.LinAlgError:
        return None
    cond = 4 * vec[0] * vec[2] - vec[1] ** 2
    valid = np.where(cond > 0)[0]
    if len(valid) == 0:
        return None
    coef = np.real(vec[:, valid[int(np.argmax(np.real(w[valid])))]]).tolist()
    A, B, Cc, Dd, E, F = coef
    if abs(B) < 1e-12:
        B = 1e-12
    den = 4 * A * Cc - B * B
    if abs(den) < 1e-12:
        return None
    cx = (2 * Cc * Dd - B * E) / den
    cy = (2 * A * E - B * Dd) / den
    num = 2 * (A * E * E + Cc * Dd * Dd + F * B * B - B * Dd * E - 4 * A * Cc * F)
    t = math.sqrt(max((A - Cc) ** 2 + B * B, 1e-12))
    try:
        a_ax = math.sqrt(abs(num / (den * ((A + Cc) + t))))
        b_ax = math.sqrt(abs(num / (den * ((A + Cc) - t))))
    except (ValueError, ZeroDivisionError):
        return None
    angle = 0.5 * math.atan2(B, A - Cc)
    a_ax, b_ax = max(a_ax, b_ax), min(a_ax, b_ax)
    if a_ax < 1e-6:
        return None
    tp = np.linspace(0, 2 * math.pi, 72, endpoint=False)
    ex = a_ax * np.cos(tp); ey = b_ax * np.sin(tp)
    ca, sa = math.cos(angle), math.sin(angle)
    poly = np.c_[ca * ex - sa * ey + cx, sa * ex + ca * ey + cy]
    return float(cx), float(cy), float(a_ax), float(b_ax), math.degrees(angle), \
        polyline_residual(pts, poly, True, 88.0)


def sector_vertices(pts, k, center, hull_idx=None):
    """k 个角扇区中取最远凸包点 → 稳健 k 边形顶点（抗噪声优于角点序列）。"""
    hull = pts[hull_idx if hull_idx is not None else convex_hull(pts)]
    if len(hull) < k:
        return None
    ang = np.arctan2(hull[:, 1] - center[1], hull[:, 0] - center[0])
    rad = np.hypot(hull[:, 0] - center[0], hull[:, 1] - center[1])
    sector = ((ang + math.pi) / (2 * math.pi) * k).astype(int) % k
    verts = []
    for s in range(k):
        idx = np.where(sector == s)[0]
        if len(idx) == 0:
            return None
        verts.append(hull[idx[int(np.argmax(rad[idx]))]])
    verts = np.array(verts)
    order = np.argsort(np.arctan2(verts[:, 1] - center[1], verts[:, 0] - center[0]))
    return verts[order]


def best_vertices(pts, k, center, hull_idx=None, corner_verts=None, closed_hint=True):
    """k 边形顶点择优（v2 修正）：扇区法对锐角/剪切四边形会选错顶点。
    候选 = [RDP 几何顶点（eps=span*0.035）, 角点集合, 扇区顶点]，取点数或就近者中
    折线残差最小的一组（个数不符时用扇区法兜底）。"""
    span = span_of(pts)
    cands = []
    rv = rdp(pts, max(span * 0.035, 2.0))
    if closed_hint and len(rv) > 3 and float(np.hypot(*(rv[0] - rv[-1]))) < span * 0.10:
        rv = rv[:-1]
    if len(rv) == k:
        cands.append(rv)
    if corner_verts is not None and len(corner_verts) == k:
        cands.append(corner_verts)
    sv = sector_vertices(pts, k, center, hull_idx)
    if sv is not None:
        cands.append(sv)
    best, best_res = None, float("inf")
    for c in cands:
        r = polyline_residual(pts, c, True)
        if r < best_res:
            best, best_res = c, r
    return best


def regularize_polygon(verts, reg=0.6):
    """顶点规范化（正多边形）：把顶点向等角等半径收敛（保旋转与尺度）。"""
    n = len(verts)
    if n < 3:
        return verts
    c = verts.mean(axis=0)
    r = np.hypot(verts[:, 0] - c[0], verts[:, 1] - c[1])
    ang = np.arctan2(verts[:, 1] - c[1], verts[:, 0] - c[0])
    order = np.argsort(ang)
    ang, rad = np.unwrap(ang[order]), r[order]
    ang_fit = (1 - reg) * ang + reg * np.linspace(ang[0], ang[-1], n)
    rad_fit = (1 - reg) * rad + reg * rad.mean()
    return np.c_[np.cos(ang_fit) * rad_fit + c[0], np.sin(ang_fit) * rad_fit + c[1]]


def orthogonalize_quad(verts):
    """矩形正交化：主轴对齐 + 对称半宽半高重排（保留旋转角）。"""
    c = verts.mean(axis=0)
    q = verts - c
    _, _, vt = np.linalg.svd(q, full_matrices=False)
    d1 = vt[0]; d2 = np.array([-d1[1], d1[0]])
    u = q @ d1; v = q @ d2
    w = (u.max() - u.min()) / 2; h = (v.max() - v.min()) / 2
    corners = np.array([[-w, -h], [w, -h], [w, h], [-w, h]])
    return np.array([c + x * d1 + y * d2 for x, y in corners])


def chaikin(pts, iters=2):
    out = pts
    for _ in range(iters):
        if len(out) < 3:
            break
        q = out[:-1] * 0.75 + out[1:] * 0.25
        r = out[:-1] * 0.25 + out[1:] * 0.75
        merged = np.empty((len(q) + len(r), 2))
        merged[0::2] = q; merged[1::2] = r
        out = np.vstack([out[0], merged, out[-1]])
    return out


# ============================================================ 特征

@dataclass
class Features:
    pts: np.ndarray
    span: float
    perim: float
    closed: bool
    gap: float
    circularity: float
    verts: np.ndarray
    n_corners: int
    hull_idx: np.ndarray = field(default_factory=lambda: np.zeros(0, int))
    concavities: list = field(default_factory=list)
    line_dev: float = 1.0
    ellipse_ratio: float = 1.0
    symmetry: float = 0.0
    tail_fork: bool = False
    fork_side: int = 0          # 箭头翼所在端（-1 前端 / +1 末端 / 0 无）
    mid_two_sided: bool = False  # 中部区间是否也双侧张开（波浪全程交替 vs 箭头仅端部展开）
    # —— 线型签名（虚线 / 点线）：基于【原始采样间距】——
    # 必须在平滑/重采样之前计算：等弧长重采样会抹平"划-隙"节奏，而节奏正是线型特征。
    raw: np.ndarray = field(default_factory=lambda: np.zeros((0, 2)))
    dash_gaps: int = 0          # 间隙段数量（连续稀疏段）
    dash_cv: float = 1.0        # 间隙间隔的变异系数（越小越规整 → 越像虚线）
    dash_len_ratio: float = 1.0  # 划段长度 / 间隙长度
    dash_pts_per_seg: float = 99.0  # 每划平均采样点数
    dash_len_mean: float = 0.0   # 每划平均弧长（px）—— 点线=极短，虚线=短划
    dash_dir: float = 0.0       # 线型主轴角度（度）
    speed_mean: float = 0.0     # 平均笔速（px/ms，raw 有时间戳时可用）


def dash_signature(raw):
    """线型签名（v2 修正新增）：沿原始点的相邻间距序列检测"划—隙"周期性。
    手绘虚线/点线常一笔连续快速画成 —— 快速笔画点间距被拉大（间隙），
    短划处点间距变小（密集）。据此统计间隙段数、间隔规整度、划段密度：
      · 虚线 = 间隙段 ≥3、间隔规整（cv 小）、每划采样点 ≥3
      · 点线 = 同上但每划采样点 ≤2（划极短 → 视觉为点）
    注意：必须以原始点计算（重采样会抹平节奏）。"""
    n = len(raw)
    if n < 10:
        return dict(gaps=0, cv=1.0, len_ratio=1.0, pts_per_seg=99.0, len_mean=0.0,
                    dir=0.0, speed=0.0)
    seg = np.hypot(np.diff(raw[:, 0]), np.diff(raw[:, 1]))
    lo, hi = float(np.percentile(seg, 25)), float(np.percentile(seg, 90))
    if hi < max(lo * 3.0, 1.5):
        return dict(gaps=0, cv=1.0, len_ratio=1.0, pts_per_seg=99.0, len_mean=0.0, dir=0.0, speed=0.0)
    thr = (lo + hi) / 2.0
    above = seg > thr
    # 间隙段（连续 above）的起点索引
    starts = []
    i = 0
    while i < len(above):
        if above[i]:
            j = i
            while j + 1 < len(above) and above[j + 1]:
                j += 1
            starts.append((i, j))
            i = j + 1
        else:
            i += 1
    gaps = len(starts)
    if gaps < 2:
        return dict(gaps=gaps, cv=1.0, len_ratio=1.0, pts_per_seg=99.0, len_mean=0.0,
                    dir=0.0, speed=0.0)
    centers = [(a + b) / 2.0 for a, b in starts]
    spacing = np.diff(centers)
    cv = float(np.std(spacing) / max(np.mean(spacing), 1e-6)) if len(spacing) else 1.0
    gap_lens = [seg[a:b + 1].sum() for a, b in starts]
    dense_lens = []
    prev = 0
    for a, _b in starts:
        dense = seg[prev:a]
        if len(dense):
            dense_lens.append(float(dense.sum()))
        prev = a
    len_ratio = float(np.mean(dense_lens) / max(np.mean(gap_lens), 1e-6)) if dense_lens else 1.0
    # 划段采样点数 = 前一间隙结束到下一间隙起点之间的点数
    seg_pts = []
    seg_lens = []
    prev_end = -1
    for a, b in starts:
        if prev_end >= 0:
            seg_pts.append(a - prev_end)
            seg_lens.append(float(seg[prev_end:a].sum()) if a > prev_end else 0.0)
        prev_end = b
    pts_per_seg = float(np.mean(seg_pts)) if seg_pts else 99.0
    len_mean = float(np.mean(seg_lens)) if seg_lens else 0.0
    d = raw[-1] - raw[0]
    dir_deg = math.degrees(math.atan2(d[1], d[0])) if float(np.hypot(d[0], d[1])) > 1e-6 else 0.0
    speed = float(seg.mean())  # 采样间距均值（无时间戳时的笔速代理量）
    return dict(gaps=gaps, cv=cv, len_ratio=len_ratio, pts_per_seg=pts_per_seg,
                len_mean=len_mean, dir=dir_deg, speed=speed)


def extract_features(points, closure_tol=CLOSE_RATIO, cfg=None):
    cfg = cfg or RecognizerConfig()
    raw = dedupe(np.asarray(points, float), eps=0.2)     # 原始点（线型签名用，轻去重）
    sig = dash_signature(raw)
    pts = dedupe(np.asarray(points, float))
    if len(pts) < cfg.min_points:
        raise ValueError("too few points")
    pts = smooth_gaussian(pts, cfg.smooth_sigma)
    pts = resample_uniform(pts, cfg.resample_step)
    if len(pts) < MIN_POINTS:
        raise ValueError("too few points")

    span = span_of(pts)
    if span < 1e-6:
        raise ValueError("degenerate")
    gap = float(np.hypot(*(pts[-1] - pts[0])))
    perim_open = perimeter(pts, False)
    closed = gap < perim_open * closure_tol or gap < CLOSE_ABS
    perim = perim_open + (gap if closed else 0.0)     # 修正：缺口计入闭合周长（圆度≈1）

    area = polygon_area(pts if closed else np.vstack([pts, pts[:1]]))
    circularity = 4 * math.pi * area / max(perim ** 2, 1e-9)
    hull_idx = convex_hull(pts)
    verts, _ = corner_vertices(pts, closed)
    conc = concave_slots(pts, hull_idx) if closed else []
    _, _, line_dev = fit_line_tls(pts)
    line_dev /= span
    ell = fit_ellipse_dls(pts) if closed else None
    e_ratio = (ell[2] / max(ell[3], 1e-9)) if ell else 1.0
    sym = 0.0   # v2.1：对称性判据已由各形状自身逻辑取代（此处不再计算，省一次 SVD）

    tail_fork = False
    fork_side = 0
    mid_two_sided = False                      # -1 = 翼在前端, +1 = 翼在末端（供箭头重建用）
    if not closed and len(pts) >= 12:
        # v2 修正：用【整体主轴】取端部窗口（按点数比例截尾只覆盖到一条翼，两侧张开的
        # 结构会被漏判）；且真实绘制序多样（干→翼 / 先画头再拉杆），故【两端都检测】。
        c, d, _ = fit_line_tls(pts)
        nn = np.array([-d[1], d[0]])
        u = (pts - c) @ d
        v = (pts - c) @ nn
        u0, u1 = float(u.min()), float(u.max())
        L = u1 - u0
        if L > span * 0.5:
            band = max(L * 0.32, span * 0.10)
            for side, sel in ((-1, u < u0 + band), (1, u > u1 - band)):
                vv = v[sel]
                if vv.size < 4:
                    continue
                pos = int((vv > span * 0.02).sum())
                neg = int((vv < -span * 0.02).sum())
                if pos >= 2 and neg >= 2:
                    tail_fork = True
                    fork_side = side
                    break
            # 中部双侧（排除首尾各 25%）：箭头主干居中（中部单侧），波浪全程交替（中部双侧）
            umid = (u > u0 + L * 0.25) & (u < u1 - L * 0.25)
            vm = v[umid]
            if vm.size >= 4:
                mid_two_sided = bool((vm > span * 0.02).sum() >= 2 and (vm < -span * 0.02).sum() >= 2)

    return Features(pts=pts, span=span, perim=perim, closed=closed, gap=gap,
                    circularity=circularity, verts=verts, n_corners=len(verts),
                    hull_idx=hull_idx, concavities=conc, line_dev=line_dev,
                    ellipse_ratio=e_ratio, symmetry=sym, tail_fork=tail_fork,
                    raw=raw, fork_side=fork_side, mid_two_sided=mid_two_sided,
                    dash_gaps=sig.get("gaps", 0), dash_cv=sig.get("cv", 1.0),
                    dash_len_ratio=sig.get("len_ratio", 1.0), dash_pts_per_seg=sig.get("pts_per_seg", 99.0),
                    dash_len_mean=sig.get("len_mean", 0.0),
                    dash_dir=sig.get("dir", 0.0), speed_mean=sig.get("speed", 0.0))


# ============================================================ 评分与重建工具

_ACTIVE_CFG = RecognizerConfig()      # recognize() 进入时按调用参数设置（单线程服务循环）
_BASE_CFG = load_config_file()        # 会话默认配置（recognizer_config.json，缺失则内置默认）


def score(res_norm, shape, bonus=1.0):
    tol = _ACTIVE_CFG.tol(shape)
    s = math.exp(-max(res_norm - tol * 0.30, 0.0) / tol)
    return float(np.clip(s * bonus, 0.0, 0.999))


def snap_line(a, b):
    v = b - a
    ang = math.degrees(math.atan2(v[1], v[0])) % 180.0
    for target in (0.0, 45.0, 90.0, 135.0):
        if abs(ang - target) <= _ACTIVE_CFG.snap_angle:
            rad = math.radians(target)
            d = np.array([math.cos(rad), math.sin(rad)])
            L = float(np.hypot(v[0], v[1]))
            a2, b2 = a.copy(), a + d * L
            if target in (0.0, 180.0):
                a2[1] = b2[1] = (a[1] + b[1]) / 2
            elif target == 90.0:
                a2[0] = b2[0] = (a[0] + b[0]) / 2
            return np.array([a2, b2])
    return np.array([a, b])


# ============================================================ 形状假设

def hyp_line(f):
    if f.closed or "line" not in _ACTIVE_CFG.enabled:
        return None
    # 线型让位：出现明显的"划—隙"节奏时交虚线/点线判据处理（间隙签名是更强证据）
    if f.dash_gaps >= _ACTIVE_CFG.dash_min_gaps:
        return None
    n = len(f.pts)
    k = max(int(n * 0.10), 2)
    core = f.pts[k:n - k] if n - 2 * k >= 4 else f.pts
    c, d, dev = fit_line_tls(core)
    if dev / f.span > _ACTIVE_CFG.line_dev_ratio:
        return None
    a, b = f.pts[0], f.pts[-1]
    L = float(np.hypot(*(b - a)))
    if L < f.span * 0.5:
        return None
    poly = snap_line(a, b)
    res = polyline_residual(f.pts, poly, False) / f.span
    # 曲直/速度/长短特征入 params（供上层策略：如"很短且慢 = 点/字"、"长且快 = 划线"）
    ang = math.degrees(math.atan2(b[1] - a[1], b[0] - a[0])) % 180.0
    return score(res, "line", 1.0 if f.n_corners <= 2 else 0.9), poly, {
        "snapped": bool(not np.allclose(poly[0], a)),
        "length": round(L, 1), "angle": round(ang, 1),
        "speed_hint": round(f.speed_mean, 2),
        "curvature": round(float(dev / max(L, 1e-6)), 4),
    }


def hyp_arrow(f):
    """箭头（v2 修正）：整体主轴 → 端部双侧张开判据 → 按翼所在端重建。
    兼容三种真实绘制序：干→翼→回尖 / 干→翼（收笔在翼尖）/ 先画翼后拉杆。
    主干直线性、翼双侧对称度、头部长度都参与评分。"""
    if "arrow" not in _ACTIVE_CFG.enabled:
        return None
    if f.closed or len(f.pts) < 12 or not f.tail_fork or f.fork_side == 0:
        return None
    pts = f.pts
    c, dd, _ = fit_line_tls(pts)
    nn = np.array([-dd[1], dd[0]])
    u = (pts - c) @ dd
    v = (pts - c) @ nn
    u0, u1 = float(u.min()), float(u.max())
    L = u1 - u0
    if L < f.span * 0.5:
        return None
    side = f.fork_side
    tip_u = u1 if side > 0 else u0
    # 主干 = 远离翼的一端（去掉靠近翼的 26%）
    if side > 0:
        shaft = pts[u < u1 - L * 0.26]
        start_pt, tip = pts[int(np.argmin(u))], pts[int(np.argmax(u))]
    else:
        shaft = pts[u > u0 + L * 0.26]
        start_pt, tip = pts[int(np.argmax(u))], pts[int(np.argmin(u))]
    if len(shaft) < 6:
        return None
    cs, ds, devs = fit_line_tls(shaft)
    if devs / f.span > 0.055:
        return None
    back = -ds if side > 0 else ds
    head_len = min(f.span * 0.13, 96.0)
    base = tip + back * head_len
    w = np.array([-back[1], back[0]])
    spread = head_len * 0.6
    poly = np.vstack([
        np.linspace(start_pt, tip, 22),
        np.linspace(base + w * spread, tip, 6)[1:],
        np.linspace(base - w * spread, tip, 6)[1:],
    ])
    res = polyline_residual(pts, poly, False) / f.span
    return score(res, "arrow"), poly, {"tip": [round(float(x), 1) for x in tip],
                                       "fork_side": side}


def hyp_dashed(f):
    """虚线 / 点线（v2 新增）：基于【原始采样节奏】的"划—隙"周期性检测。
    这类"线型"不是几何形状 —— 重建 = 把端点沿骨架拉直（去手抖），并输出 pattern
    参数（dashed/dotted + 划数 + 间隔规整度），由上层据此以虚线/点线样式重绘。
    判据：间隙段 ≥ N、间隔规整（cv 小）、骨架近似直线。"""
    cfg = _ACTIVE_CFG
    want = [k for k in ("dashed", "dotted") if k in cfg.enabled]
    if not want or f.closed:
        return None
    if f.dash_gaps < cfg.dash_min_gaps or f.dash_cv > cfg.dash_max_cv:
        return None
    n = len(f.pts)
    k = max(int(n * 0.10), 2)
    core = f.pts[k:n - k] if n - 2 * k >= 4 else f.pts
    _, _, dev = fit_line_tls(core)
    if dev / f.span > cfg.dash_skeleton_dev:
        return None
    # 分类（v2.1）：优先用"划段平均弧长"（比点数稳 —— 采样密度随设备/笔速变化）；
    # 划段远短于跨度阈值 → 点线；否则虚线。
    # 分类（v2.1）：划段长度相对【笔画总弧长】的占比 —— 点线的"点"极短（<13%），
    # 虚线的短划较长。用占比而非绝对像素，天然适配不同尺度与设备采样密度。
    ratio = f.dash_len_mean / max(f.perim, 1e-6)
    if f.dash_len_mean > 0 and ratio < 0.135:
        kind = "dotted"
    else:
        kind = "dotted" if f.dash_pts_per_seg <= cfg.dotted_max_points_per_dash else "dashed"
    if kind not in cfg.enabled:
        kind = want[0]
    a, b = f.pts[0], f.pts[-1]
    poly = snap_line(a, b)
    res = polyline_residual(f.pts, poly, False) / f.span
    conf = score(res * 1.15, "line", 1.0) * (1.0 - min(f.dash_cv, 0.9) * 0.22)
    return conf, poly, {"pattern": kind, "dashes": int(f.dash_gaps),
                        "gap_cv": round(float(f.dash_cv), 3),
                        "dash_len_ratio": round(float(f.dash_len_ratio), 3),
                        "dash_len_mean": round(float(f.dash_len_mean), 1)}


def _two_segment_vertices(f):
    """对勾/叉的本质是【两段折线】：用强简化取 3 个几何顶点（交点 + 两端）。
    手绘 + 噪声下 RDP 常见 5~6 个伪顶点 → 多档递增强简化直到 ≤4 点，4 点时合并最短段。"""
    if f.closed:
        return None
    rv = None
    for ratio in (0.050, 0.070, 0.095, 0.130):
        rv = rdp(f.pts, max(f.span * ratio, 2.5))
        if len(rv) <= 4:
            break
    if rv is None or len(rv) < 3:
        return None
    if len(rv) == 3:
        return rv
    if len(rv) == 4:
        d = [float(np.hypot(*(rv[i + 1] - rv[i]))) for i in range(3)]
        drop = int(np.argmin(d))
        keep = [rv[i] for i in range(4) if i != (drop + 1)]
        return np.array(keep[:3])
    return None


def hyp_check(f):
    if "check" not in _ACTIVE_CFG.enabled:
        return None
    v = _two_segment_vertices(f)
    if v is None:
        return None
    v1, v2 = v[1] - v[0], v[2] - v[1]
    L1, L2 = float(np.hypot(*v1)), float(np.hypot(*v2))
    if min(L1, L2) < f.span * 0.12 or L2 > L1 * 1.9:
        return None
    ang = math.degrees(math.acos(np.clip(np.dot(v1, -v2) / (L1 * L2), -1, 1)))
    if not 20 <= ang <= 110:
        return None
    poly = np.array([v[0], v[1], v[2]])
    res = polyline_residual(f.pts, poly, False) / f.span
    return score(res, "check", 1.0 if ang <= 100 else 0.88), poly, {"angle": round(ang, 1)}


def hyp_cross(f):
    """叉（v2.1 修正）：真实画法是「第一笔到端 → 折回交点 → 第二笔到另一端点」，
    故顶点结构为 4 点（起 / 端1 / 交点 / 端2）而非 3 点。判据：
      · RDP 简化后 4~5 点，且存在一个"回折点"（两侧邻段方向相反且长度接近）；
      · 两笔长度接近、夹角在 45°~135°。"""
    if "cross" not in _ACTIVE_CFG.enabled or f.closed:
        return None
    rv = None
    for ratio in (0.045, 0.060, 0.080, 0.110):
        rv = rdp(f.pts, max(f.span * ratio, 2.5))
        if len(rv) <= 5:
            break
    if rv is None or len(rv) < 3:
        return None
    # 找折返点：转向角最大且 ≥ 100°（两笔共线反向）
    if len(rv) >= 4:
        ang = turn_angles(rv, False)
        if len(ang) == 0:
            return None
        i = int(np.argmax(ang)) + 1
        if ang[i - 1] < 95:
            return None
        apex = rv[i]
        ends = [rv[j] for j in (0, i - 1, i + 1, len(rv) - 1) if 0 <= j < len(rv) and j != i]
        L1 = float(np.hypot(*(rv[0] - apex)))
        L2 = float(np.hypot(*(rv[-1] - apex)))
        if min(L1, L2) < f.span * 0.18:
            return None
        if max(L1, L2) / max(min(L1, L2), 1e-6) > 2.6:
            return None
        v1, v2 = rv[0] - apex, rv[-1] - apex
        ang2 = math.degrees(math.acos(np.clip(np.dot(v1, v2) / max(L1 * L2, 1e-9), -1, 1)))
        if not 40 <= ang2 <= 140:
            return None
        # 输出与笔画同构的折线（起 → 折返点 → 回中点 → 终点）：直接用简化顶点，
        # 残差最小（先前只取 3 点会把"回中点"当成偏离，置信度被压到 0.03 而无法识别）
        poly = rv.copy()
        res = polyline_residual(f.pts, poly, False) / f.span
        return score(res, "cross", 1.0 if abs(ang2 - 90) < 30 else 0.9), poly, \
            {"angle": round(ang2, 1), "apex": [round(float(x), 1) for x in apex]}
    # 退化情形：3 点折线且夹角大（无回折的简笔叉）
    v = rv
    v1, v2 = v[1] - v[0], v[2] - v[1]
    L1, L2 = float(np.hypot(*v1)), float(np.hypot(*v2))
    if min(L1, L2) < f.span * 0.18:
        return None
    if max(L1, L2) / max(min(L1, L2), 1e-6) > 2.6:
        return None
    ang = math.degrees(math.acos(np.clip(np.dot(v1, -v2) / (L1 * L2), -1, 1)))
    if not 40 <= ang <= 140:
        return None
    poly = np.array([v[0], v[1], v[2]])
    res = polyline_residual(f.pts, poly, False) / f.span
    return score(res, "cross", 0.92), poly, {"angle": round(ang, 1)}


def hyp_circle_ellipse(f):
    out = []
    if not f.closed or f.span < 10:
        return out
    if not ({"circle"} & _ACTIVE_CFG.enabled or {"ellipse"} & _ACTIVE_CFG.enabled):
        return out
    if "circle" not in _ACTIVE_CFG.enabled:
        pass  # 圆关闭时下面的圆分支会被 enabled 检查拦下
    circ = fit_circle_taubin(f.pts)
    if circ:
        cx, cy, r, maxres = circ
        # 圆判据（v2 修正）：圆形经 RDP 会产生许多"伪顶点"，故不用角点数；
        # 改用圆度（缺口已计入周长，真圆≈1）+ 椭圆主轴比（圆的后处理畸变很小）。
        if "circle" in _ACTIVE_CFG.enabled and f.ellipse_ratio < 1.25 and \
                (f.n_corners <= 2 or f.circularity > 0.88):
            tp = np.linspace(0, 2 * math.pi, 64, endpoint=False)
            poly = np.c_[cx + r * np.cos(tp), cy + r * np.sin(tp)]
            out.append((score(maxres / f.span * 0.8, "circle"), "circle", poly, {}))
    ell = fit_ellipse_dls(f.pts)
    if ell:
        cx, cy, a_ax, b_ax, ang, res = ell
        if "ellipse" in _ACTIVE_CFG.enabled and b_ax > 1e-6 and a_ax / b_ax > 1.10:
            tp = np.linspace(0, 2 * math.pi, 64, endpoint=False)
            ca, sa = math.cos(math.radians(ang)), math.sin(math.radians(ang))
            x = a_ax * np.cos(tp); y = b_ax * np.sin(tp)
            out.append((score(res / f.span, "ellipse"), "ellipse",
                        np.c_[ca * x - sa * y + cx, sa * x + ca * y + cy], {}))
    return out


def hyp_polygon_family(f):
    """三角形 / 四边形族 / 正多边形 统一竞争（规范化顶点）。"""
    out = []
    if not f.closed or f.span < 14:
        return out
    if not ({"triangle", "quad", "polygon"} & _ACTIVE_CFG.enabled):
        return out
    c = centroid(f.pts)
    if len(f.concavities) >= 3:
        return out                       # 有凹槽 → 让位星形/心形/云朵判据
    ks = {3, 4, 5, 6, 8}
    if 3 <= f.n_corners <= 10:
        ks.add(f.n_corners)
    for k in sorted(ks):
        verts = best_vertices(f.pts, k, c, f.hull_idx, f.verts)
        if verts is None:
            continue
        res = polyline_residual(f.pts, verts, True) / f.span
        if k == 3:
            if "triangle" not in _ACTIVE_CFG.enabled:
                continue
            conf = score(res, "triangle")
            if conf > 0.02:
                L3 = [float(np.hypot(*(verts[(i + 1) % 3] - verts[i]))) for i in range(3)]
                isos = (max(L3) - min(L3)) / max(max(L3), 1e-9) < 0.22
                out.append((conf, "triangle", np.vstack([verts, verts[:1]]),
                            {"kind": "isosceles" if isos else "generic"}))
        elif k == 4:
            if "quad" not in _ACTIVE_CFG.enabled:
                continue
            e = np.diff(np.vstack([verts, verts[:1]]), axis=0)
            L = np.hypot(e[:, 0], e[:, 1])
            if L.min() < 1e-6:
                continue
            angs = [math.degrees(math.atan2(e[i][1], e[i][0])) % 180.0 for i in range(4)]
            par = [min(abs((angs[i] - angs[i + 2]) % 180.0),
                       180.0 - abs((angs[i] - angs[i + 2]) % 180.0)) for i in range(2)]
            right = all(abs(((angs[i] - angs[(i + 1) % 4]) % 180.0) - 90) < 16 for i in range(4))
            ratio = L.max() / max(L.min(), 1e-6)
            if right and max(par) < 18:
                rect = orthogonalize_quad(verts)
                kind = "square" if ratio < 1.16 else "rectangle"
                out.append((score(res, "quad"), "quad", np.vstack([rect, rect[:1]]), {"kind": kind}))
                continue
            if max(par) < 20:
                kind = "rhombus" if ratio < 1.16 else "parallelogram"
                out.append((score(res, "quad", 0.93), "quad", np.vstack([verts, verts[:1]]),
                            {"kind": kind}))
            else:
                v2 = verts.copy()
                if min(abs(angs[0] - angs[2]), 180 - abs(angs[0] - angs[2])) < 30:
                    v2[0, 1] = v2[1, 1] = (v2[0, 1] + v2[1, 1]) / 2
                    v2[2, 1] = v2[3, 1] = (v2[2, 1] + v2[3, 1]) / 2
                out.append((score(res, "quad", 0.9), "quad", np.vstack([v2, v2[:1]]),
                            {"kind": "trapezoid"}))
        else:
            if "polygon" not in _ACTIVE_CFG.enabled:
                continue
            reg = regularize_polygon(verts, 0.55)
            poly = np.vstack([reg, reg[:1]])
            res2 = polyline_residual(f.pts, poly, True) / f.span
            out.append((score(res2, "polygon"), "polygon", poly,
                        {"sides": k, "kind": {5: "pentagon", 6: "hexagon", 8: "octagon"}.get(k, "polygon")}))
    return out


def hyp_star(f):
    if "star" not in _ACTIVE_CFG.enabled or not f.closed:
        return None
    deep = [s for s in f.concavities if s[0] > 0.05]
    if len(deep) < STAR_MIN_RAYS or len(deep) > 14:
        return None
    rays = len(deep)
    c = centroid(f.pts)
    outer = best_vertices(f.pts, rays, c, f.hull_idx, f.verts)
    if outer is None:
        outer = sector_vertices(f.pts, rays, c, f.hull_idx)
    if outer is None:
        return None
    r_in = float(np.percentile(np.hypot(*(f.pts - c).T), 10))
    inner = []
    for i in range(rays):
        a1 = outer[i] - c
        a2 = outer[(i + 1) % rays] - c
        mid = a1 / max(float(np.hypot(*a1)), 1e-9) + a2 / max(float(np.hypot(*a2)), 1e-9)
        nm = float(np.hypot(*mid))
        if nm < 1e-9:
            return None
        inner.append(c + mid / nm * max(r_in, f.span * 0.12))
    star = []
    for i in range(rays):
        star.append(outer[i]); star.append(inner[i])
    star = np.array(star)
    poly = np.vstack([star, star[:1]])
    res = polyline_residual(f.pts, poly, True) / f.span
    return score(res, "star", 1.0 if rays == 5 else 0.92), poly, {"rays": rays}


def hyp_heart(f):
    """心形（低频装饰图形，默认关闭，可在配置中开启）：参数化模板拟合 —— 去掉了"凹槽数/相背"等前置结构判据
    （手绘心形的凸包凹槽分布不稳定，前置判据常把真心形挡在门外）。残差本身足以
    把它与圆/矩形/星形区分：那类图形的标准心形模板拟合残差远大于阈值。"""
    if "heart" not in _ACTIVE_CFG.enabled:
        return None
    if not f.closed or f.span < 20:
        return None
    c = centroid(f.pts)
    t = np.linspace(0, 2 * math.pi, 96)
    hx0 = 16 * np.sin(t) ** 3
    hy0 = 13 * np.cos(t) - 5 * np.cos(2 * t) - 2 * np.cos(3 * t) - np.cos(4 * t)
    hx0, hy0 = hx0 / 16.0, -hy0 / 16.0                 # 图像坐标 y 向下
    target = f.pts - c
    best = None
    # 四种朝向（手绘心形朝向不定）：原样 / 水平镜像 / 垂直镜像 / 180°
    for mir in ((1, 1), (-1, 1), (1, -1), (-1, -1)):
        hx, hy = hx0 * mir[0], hy0 * mir[1]
        try:
            sol = np.linalg.lstsq(np.c_[hx, hy], target, rcond=None)[0]
        except np.linalg.LinAlgError:
            continue
        sc = float((sol[0, 0] + sol[1, 1]) / 2.0)
        if abs(sc) < 1e-6:
            continue
        sc = abs(sc)
        if not (0.30 * f.span < sc < 3.0 * f.span):
            continue
        poly = np.c_[hx * sc + c[0], hy * sc + c[1]]
        res = polyline_residual(f.pts, poly, True, 88.0) / f.span
        if best is None or res < best[0]:
            best = (res, poly)
    if best is None:
        return None
    return score(best[0], "heart"), best[1], {}


def hyp_wave(f):
    """波浪（v2 修正）：结构判据（横向、多穿越、有振幅）→ 正弦最小二乘重建。
    先前用"穿越次数估算周期 + 相位 arcsin"近似，残差偏大导致置信度被曲线兜底压制；
    现改为周期扫描（ω 网格）+ 线性最小二乘求 (a,b,c)，拟合精度显著提升。"""
    if "wave" not in _ACTIVE_CFG.enabled or f.closed or f.n_corners < 2:
        return None
    if f.tail_fork and not f.mid_two_sided:
        return None                       # 端部展开 + 中部单侧 = 箭头结构，不是波浪；
                                          # 波浪的端部自然也在中线两侧交替 → 需中部同样双侧

    pts = f.pts
    v = pts[-1] - pts[0]
    L = float(np.hypot(*v))
    if L < f.span * 0.6:
        return None
    ang = abs(math.degrees(math.atan2(v[1], v[0])))
    if min(ang, 180 - ang) > 32:
        return None
    d = v / max(L, 1e-9)
    nn = np.array([-d[1], d[0]])
    q = pts - pts[0]
    u = q @ d
    w = q @ nn
    amp = (w.max() - w.min()) / 2
    if amp < f.span * 0.035:
        return None
    wc = float(w.mean())
    n = len(w)
    lo, hi = max(int(n * 0.12), 1), min(n - int(n * 0.12), n)
    sign = np.sign(w[lo:hi] - wc)
    cross_idx = np.where(sign[1:] * sign[:-1] < 0)[0]
    crossings = int(len(cross_idx))
    if crossings < 2:
        return None
    # 单频规整度前置（v2.1 防误报）：自由曲线（多频叠加）穿越间隔极不均 ——
    # 真实波浪的穿越间隔近似等距（cv 小），不满足者直接拒（也省下后面的周期扫描）。
    if crossings >= 2 and len(cross_idx) >= 2:
        gaps_c = np.diff(cross_idx)
        cv_c = float(np.std(gaps_c) / max(np.mean(gaps_c), 1e-6))
        if cv_c > 0.45:
            return None
    # 周期扫描（在合理频段内取残差最小者）
    u0, u1 = float(u.min()), float(u.max())
    base = max(crossings / 2.0, 0.8)                  # 粗估周期数
    best = (float("inf"), None)
    for mult in np.linspace(0.6, 1.6, 9):
        om = 2 * math.pi * base * mult / max(u1 - u0, 1e-6)
        A = np.c_[np.sin(om * u), np.cos(om * u), np.ones_like(u)]
        try:
            coef, *_ = np.linalg.lstsq(A, w, rcond=None)
        except np.linalg.LinAlgError:
            continue
        r = float(np.sqrt((( A @ coef - w) ** 2).mean()))
        if r < best[0]:
            best = (r, coef, om)
    if best[1] is None:
        return None
    rms, coef, om = best
    res_norm = rms / f.span
    uu = np.linspace(u0, u1, 90)
    ww = coef[0] * np.sin(om * uu) + coef[1] * np.cos(om * uu) + coef[2]
    poly = pts[0] + uu[:, None] * d + ww[:, None] * nn
    res = polyline_residual(f.pts, poly, False) / f.span
    return score(max(res * 1.05, res_norm), "wave", 1.0 if crossings >= 3 else 0.92), poly, \
        {"amplitude": round(float(amp), 1), "crossings": crossings, "rms": round(float(rms), 2)}


def hyp_cloud(f):
    """云朵（v2 修正）：径向调制 + 多极值判据。
    云朵的凹槽常常很浅（低于凹槽阈值 → 此前 0 槽），但"以质心为极心的半径随角度
    出现多个极值"是其稳定的结构签名：椭圆=2 极值、圆≈0、星形=多极值但有深凹、
    心形有深凹。故用 调制幅度 + 极值数（≥3） + 无深凹 三者联合判定。"""
    if "cloud" not in _ACTIVE_CFG.enabled or not f.closed or f.span < 20:
        return None
    c = centroid(f.pts)
    q = f.pts - c
    r = np.hypot(q[:, 0], q[:, 1])
    rm = float(r.mean())
    if rm < f.span * 0.10:
        return None
    modulation = float(r.std() / max(rm, 1e-6))
    if modulation < 0.09 or modulation > 0.42:
        return None
    if any(s0[0] > 0.22 for s0 in f.concavities):     # 深凹 → 星形/心形，不是云
        return None
    if f.circularity < 0.42:
        return None
    # 半径-角度签名：等角重采样后数局部极大
    ang = np.arctan2(q[:, 1], q[:, 0])
    order = np.argsort(ang)
    grid = np.linspace(-math.pi, math.pi, 64, endpoint=False)
    rs = np.interp(grid, ang[order], r[order])
    rs_s = np.convolve(np.r_[rs[-2:], rs, rs[:2]], np.ones(3) / 3.0, mode="valid")[1:-1]
    peaks = 0
    for i in range(len(rs_s)):
        if rs_s[i] > rs_s[i - 1] and rs_s[i] >= rs_s[(i + 1) % len(rs_s)]:
            if rs_s[i] > rm * (1.0 + modulation * 0.35):
                peaks += 1
    if peaks < 3:
        return None
    hull = f.pts[convex_hull(f.pts)]
    ord2 = np.argsort(np.arctan2(hull[:, 1] - c[1], hull[:, 0] - c[0]))
    ring = hull[ord2]
    poly = chaikin(np.vstack([ring, ring[:1]]), 2)
    res = polyline_residual(f.pts, poly, True) / f.span
    return score(res, "cloud"), poly, {"bumps": peaks, "modulation": round(modulation, 2)}


def hyp_curve(f):
    if len(f.pts) < CURVE_MIN_SAMPLES:
        return None
    smooth = chaikin(f.pts, 2)
    smooth = rdp(smooth, max(f.span * 0.004, 0.8))
    smooth = chaikin(smooth, 1)
    res = polyline_residual(f.pts, smooth, False) / f.span
    return score(res * 0.6, "curve", 0.84), smooth, {}


# 具体形状假设（参与竞争；实际参与集合由配置 enabled 决定 —— 见 builders_for）
BUILDERS = (hyp_line, hyp_dashed, hyp_arrow, hyp_check, hyp_cross, hyp_circle_ellipse,
            hyp_polygon_family, hyp_star, hyp_heart, hyp_wave, hyp_cloud)
NAME_OF = {hyp_line: "line", hyp_dashed: "dashed", hyp_arrow: "arrow", hyp_check: "check",
           hyp_cross: "cross", hyp_star: "star", hyp_heart: "heart", hyp_wave: "wave",
           hyp_cloud: "cloud"}


def map_legacy_options(d):
    """旧协议字段 → v2 参数（向后兼容：老客户端仍在发 line_tolerance / circle_residual /
    corner_angle / closure_tolerance，映射后灵敏度系数才能真正生效）。"""
    if not d:
        return d
    out = dict(d)
    if "line_tolerance" in out and "line_dev_ratio" not in out:
        out["line_dev_ratio"] = float(out.pop("line_tolerance"))
    if "circle_residual" in out:
        tol = dict(out.get("tolerances") or {})
        tol.setdefault("circle", float(out.pop("circle_residual")))
        out["tolerances"] = tol
    if "corner_angle" in out and "corner_turn_min" not in out:
        out["corner_turn_min"] = float(out.pop("corner_angle"))
    if "closure_tolerance" in out and "closure_tol" not in out:
        out["closure_tol"] = float(out.pop("closure_tolerance"))
    out.pop("line_tolerance", None)
    return out


def active_shapes(cfg):
    """当前启用的形状名集合（polygon_family / circle_ellipse 承载多个子形状）。"""
    sh = set(cfg.enabled)
    if "dotted" in sh:
        sh.add("dashed")
    return sh


def _params_of(name, poly, f):
    p = {}
    if name == "circle" and len(poly) >= 8:
        c = centroid(poly)
        p = {"cx": round(float(c[0]), 1), "cy": round(float(c[1]), 1),
             "r": round(float(np.hypot(*(poly[0] - c))), 1)}
    elif name == "ellipse":
        ell = fit_ellipse_dls(f.pts)
        if ell:
            p = {"cx": round(ell[0], 1), "cy": round(ell[1], 1), "a": round(ell[2], 1),
                 "b": round(ell[3], 1), "angle": round(ell[4], 1)}
    elif name == "line":
        p = {"x1": round(float(poly[0][0]), 1), "y1": round(float(poly[0][1]), 1),
             "x2": round(float(poly[-1][0]), 1), "y2": round(float(poly[-1][1]), 1)}
    return p


# ============================================================ 主识别

def recognize(points, closure_tol=None, want_alternatives=True, cfg=None, options=None):
    """识别入口。
    cfg：RecognizerConfig（形状开关 + 全部阈值）；options：本次调用的覆盖项（dict）。
    兼容旧签名：closure_tol 若给出则覆盖 cfg.closure_tol。"""
    global _ACTIVE_CFG
    cfg = (cfg or _BASE_CFG).with_overrides(map_legacy_options(options))
    if closure_tol is not None:
        cfg = cfg.with_overrides({"closure_tol": closure_tol})
    _ACTIVE_CFG = cfg
    try:
        arr = np.asarray(points, dtype=np.float64)
        if arr.ndim != 2 or arr.shape[1] < 2 or len(arr) < cfg.min_points:
            return None
        if not np.isfinite(arr[:, :2]).all():
            return None
        f = extract_features(arr[:, :2], cfg.closure_tol, cfg)
    except Exception:
        return None

    cands = []
    allow = active_shapes(cfg)
    for builder in BUILDERS:
        if NAME_OF.get(builder) not in allow:
            # 承载多子形状的组合构建器：仅当任一子形状开启时参与
            if builder is hyp_circle_ellipse and not ({"circle", "ellipse"} & allow):
                continue
            if builder is hyp_polygon_family and not ({"triangle", "quad", "polygon"} & allow):
                continue
            if builder is not hyp_circle_ellipse and builder is not hyp_polygon_family:
                continue
        try:
            r = builder(f)
        except Exception:
            r = None
        if r is None:
            continue
        if isinstance(r, list):
            cands.extend(r)
        else:
            conf, poly, params = r
            nm = NAME_OF[builder]
            if nm == "dashed":
                nm = params.get("pattern", "dashed")
            cands.append((conf, nm, poly, params))

    cands.sort(key=lambda c: c[0], reverse=True)

    # 曲线兜底策略（v2 关键修正）：平滑曲线拟合残差天然很小、置信度必然很高 ——
    # 与具体形状同台竞争时会把真形状（心形/箭头/叉…）全部压掉（此前多轮"识别为零"
    # 的根因）。故曲线不进竞争：仅当【没有任何具体形状达到阈值】时才作为美化兜底输出。
    if not cands or cands[0][0] < cfg.score_threshold:
        cu = hyp_curve(f)
        if cu is None:
            return None
        cconf, cpoly, cparams = cu
        if cconf < cfg.score_threshold:
            return None
        result = {"shape": "curve", "points": np.round(cpoly, 1).tolist(),
                  "confidence": round(float(cconf), 3), "params": {}}
        if want_alternatives and cands:
            alts, seen = [], set()
            for c2, n2, _p, _ in cands:
                if n2 in seen:
                    continue
                seen.add(n2)
                alts.append({"shape": n2, "confidence": round(float(c2), 3)})
                if len(alts) >= 3:
                    break
            if alts:
                result["alternatives"] = alts
        return result

    conf, name, poly, params = cands[0]
    if poly is None or len(poly) < 2:
        return None

    result = {"shape": name, "points": np.round(poly, 1).tolist(),
              "confidence": round(float(conf), 3), "params": _params_of(name, poly, f)}
    if name in ("dashed", "dotted"):
        result["params"].setdefault("pattern", name)
    if params:
        result["params"].update(params)
    if want_alternatives:
        alts, seen = [], {name}
        for c2, n2, _p, _ in cands[1:]:
            if n2 in seen:
                continue
            seen.add(n2)
            alts.append({"shape": n2, "confidence": round(float(c2), 3)})
            if len(alts) >= 3:
                break
        if alts:
            result["alternatives"] = alts
    return result