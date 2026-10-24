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
import sys

import numpy as np

# 识别引擎（v2.1）：全部算法在 pptink_recog 模块内（形状开关 + 参数可调 + 回归门禁）。
# 本文件只负责协议服务循环，保持与 C# PythonBridge 的行式 JSON 契约不变。
from pptink_recog import (recognize as _recognize, load_config_file,
                          DEFAULT_SHAPES, ALL_SHAPES)

try:
    import cv2  # noqa: F401  可选增强
    HAS_CV2 = True
except ImportError:
    HAS_CV2 = False


# ---------------------------------------------------------------- 服务循环

# 会话级识别配置（可由 settings 面板下发 config 命令覆盖）。审查修复：原先纯默认构造
# 从不读文件，而 C# 侧也从不下发 config 命令 —— 随便携包发布、头注释写明可调的
# recognizer_config.json 被用户编辑后完全静默失效。现以文件配置作为会话初始默认
# （优先级：请求 options > config 命令 > 配置文件 > 内置默认；文件缺失/损坏时
# load_config_file 返回内置默认，识别链路不因配置崩）。
_session_cfg = load_config_file()


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
