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
    * ping / warmup：健康探活与预热（v1.1.2）
- 依赖：numpy 必需；opencv-python 可选（角点与 minAreaRect 增强），缺失自动退化
- 运行形态：由 C# 主进程按需拉起的独立子进程（AI 未启用不驻留内存）

依赖缺失不会让进程崩溃退出：桥保持可服务，并在 hello/ping/recognize 里返回
`numpy` / `recognizer` 可用性与明确的 reason，C# 侧据此给出"缺 NumPy"而不是
"桥返回空"这种无从下手的提示。
"""

import json
import os
import sys
import time
import traceback

# ---------------------------------------------------------------- 依赖（降级而非崩溃）

try:
    import numpy as np
    HAS_NUMPY = True
    NUMPY_ERROR = ""
except Exception as _exc:  # noqa: BLE001 - 缺依赖也必须能回一条可读的错误
    np = None
    HAS_NUMPY = False
    NUMPY_ERROR = f"{type(_exc).__name__}: {_exc}"

# 识别引擎（v2.1）：全部算法在 pptink_recog 模块内（形状开关 + 参数可调 + 回归门禁）。
# 本文件只负责协议服务循环，保持与 C# PythonBridge 的行式 JSON 契约不变。
HAS_RECOG = False
RECOG_ERROR = ""
_recognize = None
load_config_file = None
DEFAULT_SHAPES = ()
ALL_SHAPES = ()

if HAS_NUMPY:
    try:
        from pptink_recog import (recognize as _recognize, load_config_file,
                                  DEFAULT_SHAPES, ALL_SHAPES)
        HAS_RECOG = True
    except Exception as _exc:  # noqa: BLE001
        RECOG_ERROR = f"{type(_exc).__name__}: {_exc}"
        print("[pptink-bridge] recognizer import failed: " + traceback.format_exc(), file=sys.stderr)
else:
    RECOG_ERROR = "numpy_missing"

try:
    import cv2  # noqa: F401  可选增强
    HAS_CV2 = True
except Exception:  # noqa: BLE001 - 某些精简发行版的 cv2 会抛非 ImportError
    HAS_CV2 = False

SERVER_VERSION = 3
MAX_POINTS = 8192
MAX_LINE_CHARS = 4 * 1024 * 1024   # 单行协议上限：异常输入不拖垮常驻进程
STARTED_AT = time.monotonic()


# ---------------------------------------------------------------- 服务循环

# 会话级识别配置（可由 settings 面板下发 config 命令覆盖）。审查修复：原先纯默认构造
# 从不读文件，而 C# 侧也从不下发 config 命令 —— 随便携包发布、头注释写明可调的
# recognizer_config.json 被用户编辑后完全静默失效。现以文件配置作为会话初始默认
# （优先级：请求 options > config 命令 > 配置文件 > 内置默认；文件缺失/损坏时
# load_config_file 返回内置默认，识别链路不因配置崩）。
_session_cfg = load_config_file() if HAS_RECOG else None

# 轻量运行统计（ping 返回，排查"桥还活着吗/卡在哪"时无需再猜）
_stats = {"requests": 0, "recognize": 0, "failed": 0, "last_error": ""}


def _dependency_state() -> dict:
    """依赖可用性：C# 侧用它把"未检测到"翻译成具体原因。"""
    return {"numpy": HAS_NUMPY, "recognizer": HAS_RECOG, "cv2": HAS_CV2,
            "numpy_error": NUMPY_ERROR, "recognizer_error": RECOG_ERROR}


def _unavailable() -> dict:
    """识别不可用时的统一响应（按缺哪个依赖给不同的 reason）。"""
    _stats["failed"] += 1
    if not HAS_NUMPY:
        return {"ok": False, "reason": "numpy_missing", "detail": NUMPY_ERROR,
                **_dependency_state()}
    return {"ok": False, "reason": "recognizer_unavailable", "detail": RECOG_ERROR,
            **_dependency_state()}


def handle(msg: dict) -> dict:
    global _session_cfg
    _stats["requests"] += 1
    cmd = msg.get("cmd")
    if cmd == "hello":
        return {"ok": True, "server": "pptink-bridge", "version": SERVER_VERSION,
                "cv2": HAS_CV2, "shapes": list(DEFAULT_SHAPES),
                "optional_shapes": list(ALL_SHAPES), **_dependency_state()}
    if cmd == "ping":
        return {"ok": True, "server": "pptink-bridge", "version": SERVER_VERSION,
                "pid": os.getpid(), "uptime_s": round(time.monotonic() - STARTED_AT, 3),
                "stats": dict(_stats), **_dependency_state()}
    if cmd == "warmup":
        # 预热：把 NumPy 的第一次调用开销（内存池 / BLAS 初始化）提前付掉，
        # 让课堂上的第一笔识别不承担冷启动抖动。失败不影响后续请求。
        if not HAS_RECOG:
            return _unavailable()
        try:
            probe = np.asarray([[0.0, 0.0], [10.0, 0.0], [10.0, 10.0], [0.0, 10.0],
                                [1.0, 1.0], [9.0, 1.0], [9.0, 9.0], [1.0, 9.0]])
            _recognize(probe, cfg=_session_cfg, options=None)
            return {"ok": True, "warmed": True, **_dependency_state()}
        except Exception as exc:  # noqa: BLE001
            print(f"[pptink-bridge] warmup failed: {type(exc).__name__}: {exc}", file=sys.stderr)
            return {"ok": True, "warmed": False, "detail": type(exc).__name__,
                    **_dependency_state()}
    if cmd == "recognize":
        if not HAS_RECOG:
            return _unavailable()
        # Local IPC is still an input boundary: reject malformed/oversized requests before
        # allocating algorithm work arrays. One bad request must not terminate the bridge.
        raw_points = msg.get("points", [])
        if not isinstance(raw_points, list) or len(raw_points) > MAX_POINTS:
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
        try:
            result = _recognize(pts, cfg=_session_cfg, options=opts or None)
        except Exception as exc:  # noqa: BLE001 - 单次算法异常不终止常驻进程
            _stats["failed"] += 1
            _stats["last_error"] = f"recognize:{type(exc).__name__}: {exc}"
            print(f"[pptink-bridge] recognize failed: {traceback.format_exc()}", file=sys.stderr)
            return {"ok": False, "reason": "recognize_failed", "detail": type(exc).__name__}
        _stats["recognize"] += 1
        if result is None:
            return {"ok": False, "reason": "unrecognized"}
        return {"ok": True, **result}
    if cmd == "config":
        # 更新会话配置（形状开关 + 全部阈值）；enabled 传空表示使用默认集合
        if not HAS_RECOG:
            return _unavailable()
        raw_options = msg.get("options") or {}
        if not isinstance(raw_options, dict):
            return {"ok": False, "reason": "invalid_options"}
        opts = dict(raw_options)
        if not opts.get("enabled"):
            opts.pop("enabled", None)
        _session_cfg = _session_cfg.with_overrides(opts)
        return {"ok": True, "config": _session_cfg.to_dict()}
    if cmd == "shapes":
        if not HAS_RECOG:
            return _unavailable()
        return {"ok": True, "enabled": sorted(_session_cfg.enabled),
                "implemented": list(ALL_SHAPES)}
    if cmd == "shutdown":
        return {"ok": True, "bye": True}
    _stats["failed"] += 1
    return {"ok": False, "reason": f"unknown cmd {cmd}"}


def _emit(resp: dict) -> None:
    try:
        encoded = json.dumps(resp, ensure_ascii=False, allow_nan=False)
    except (TypeError, ValueError, OverflowError) as exc:
        # A malformed/unexpected response must never terminate the long-lived stdio worker.
        print(f"[pptink-bridge] response serialization failed: {type(exc).__name__}: {exc}", file=sys.stderr)
        encoded = '{"ok":false,"reason":"response_serialization_failed"}'
    sys.stdout.write(encoded + "\n")
    sys.stdout.flush()


def main():
    # stdio 行式 JSON 服务循环（与 C# PythonBridge 严格对齐）。
    # 严禁向 stdout 打印任何非协议内容（日志一律 stderr），否则污染协议流。
    try:
        sys.stdin.reconfigure(encoding="utf-8", errors="replace")
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:  # noqa: BLE001 - 旧版运行时没有 reconfigure
        pass
    print(f"[pptink-bridge] stdio ready (cv2={HAS_CV2}, numpy={HAS_NUMPY}, recog={HAS_RECOG})",
          file=sys.stderr)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        if len(line) > MAX_LINE_CHARS:
            _stats["failed"] += 1
            _emit({"ok": False, "reason": "request_too_large"})
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
                    _stats["failed"] += 1
                    _stats["last_error"] = f"{type(exc).__name__}: {exc}"
                    print(f"[pptink-bridge] request failed: {type(exc).__name__}: {exc}", file=sys.stderr)
                    resp = {"ok": False, "reason": "request_failed"}
        _emit(resp)
        if resp.get("bye"):
            return


if __name__ == "__main__":
    main()