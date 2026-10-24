#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
识别引擎质量门禁（pytest）
==========================
运行：cd python && python3 -m pytest tests/test_recognizer.py -q
定位：这是识别算法的"回归防线"——任何调参/重构后必须全绿才可交付。
覆盖四类：
  1. 各形状识别率（合成手绘样本，含抖动/缺口/仿射畸变/采样不均）
  2. 形状开关与参数可调（关闭即不识别；阈值提高即收紧）
  3. 线型（虚线/点线）与鲁棒性（少点/NaN/重复点）
  4. 服务协议（hello / recognize / config / 未知命令 / shutdown）
"""
import os
import sys

import numpy as np
import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
sys.path.insert(0, ROOT)

import synth                     # noqa: E402
import pptink_recog as R         # noqa: E402

# 合成样本数（CI 上保持快速；本地排查可临时调大）
N = int(os.environ.get("PPTINK_TEST_SAMPLES", "20"))

# (名称, 生成器, 期望形状集合, 最低识别率)  —— 门禁阈值按实测留出余量
SHAPE_CASES = [
    ("line", lambda: synth.line(), {"line"}, 0.75),
    ("dashed", lambda: synth.dashed(), {"dashed"}, 0.65),
    ("dotted", lambda: synth.dotted(), {"dotted"}, 0.55),
    ("circle", lambda: synth.circle(), {"circle"}, 0.85),
    ("ellipse", lambda: synth.ellipse(), {"ellipse"}, 0.85),
    ("square", lambda: synth.square(), {"quad"}, 0.78),
    ("rectangle", lambda: synth.rectangle(), {"quad"}, 0.75),
    ("parallelogram", lambda: synth.parallelogram(), {"quad"}, 0.72),
    ("trapezoid", lambda: synth.trapezoid(), {"quad"}, 0.78),
    ("triangle", lambda: synth.triangle(), {"triangle"}, 0.80),
    ("polygon5", lambda: synth.polygon(5), {"polygon"}, 0.78),
    ("polygon6", lambda: synth.polygon(6), {"polygon"}, 0.82),
    ("arrow", lambda: synth.arrow(), {"arrow"}, 0.85),
    ("check", lambda: synth.check(), {"check"}, 0.85),
    ("cross", lambda: synth.cross(), {"cross"}, 0.85),
    ("wave", lambda: synth.wave(), {"wave"}, 0.85),
]


def _hit_rate(gen, expect, n=N):
    ok = 0
    for _ in range(n):
        r = R.recognize(gen())
        if r and r["shape"] in expect:
            ok += 1
    return ok / n


@pytest.mark.parametrize("name,gen,expect,floor", SHAPE_CASES, ids=[c[0] for c in SHAPE_CASES])
def test_shape_recognition_rate(name, gen, expect, floor):
    rate = _hit_rate(gen, expect)
    assert rate >= floor, f"{name} 识别率 {rate:.2f} 低于门禁 {floor}"


def test_result_schema():
    """返回结构必须满足 C# 协议（shape/points/confidence 三件套）。"""
    r = R.recognize(synth.circle())
    assert r is not None
    assert r["shape"] == "circle"
    assert isinstance(r["points"], list) and len(r["points"]) >= 8
    assert 0.0 < r["confidence"] <= 1.0
    assert all(len(p) == 2 for p in r["points"][:5])


def test_freeform_not_misrecognized_as_geometry():
    """自由曲线/乱涂：不应被当作具体几何（可为 curve 兜底或 None）。"""
    fp = 0
    total = N * 2
    for i in range(total):
        pts = synth.freeform(seed=i) if i % 2 == 0 else synth.scribble(seed=i)
        r = R.recognize(pts)
        if r and r["shape"] not in ("curve", "line", "wave"):   # 线与波浪是允许的弱结构兜底
            fp += 1
    assert fp / total <= 0.20, f"自由曲线误报率过高 {fp}/{total}"


# ———————— 形状开关与参数可调 ————————

def test_shape_switch_disables_recognition():
    """关闭某形状后，绝不应再输出该形状（这是用户可感知的开关语义）。"""
    cfg = R.RecognizerConfig({"enabled": [s for s in R.DEFAULT_SHAPES if s != "circle"]})
    hits = 0
    for _ in range(N):
        r = R.recognize(synth.circle(), cfg=cfg)
        if r and r["shape"] == "circle":
            hits += 1
    assert hits == 0, "关闭 circle 后仍输出 circle"


def test_optional_shapes_off_by_default():
    """低频装饰图形（心形/星形/云朵）默认关闭，可在配置中开启。"""
    assert "heart" not in R.DEFAULT_SHAPES
    assert "star" not in R.DEFAULT_SHAPES
    assert "cloud" not in R.DEFAULT_SHAPES
    assert "heart" in R.ALL_SHAPES and "star" in R.ALL_SHAPES and "cloud" in R.ALL_SHAPES
    cfg = R.RecognizerConfig({"enabled": list(R.ALL_SHAPES)})
    r = R.recognize(synth.star(5), cfg=cfg)
    assert r is not None and r["shape"] == "star", "开启 star 后应能识别"


def test_threshold_is_tunable():
    """置信度阈值可调：拉高到 0.99 → 几乎不再输出具体形状（全部走兜底或 None）。"""
    # 阈值须高于置信度上限（score() 末尾 clamp 到 0.999）才必然全部兜底
    strict = R.RecognizerConfig({"score_threshold": 1.0})
    n_shape = 0
    for _ in range(N):
        r = R.recognize(synth.triangle(), cfg=strict)
        if r and r["shape"] not in ("curve",):
            n_shape += 1
    assert n_shape == 0


def test_options_override_per_call():
    """单次调用的 options 覆盖（不改变全局配置）。"""
    r = R.recognize(synth.circle(), options={"score_threshold": 1.0})
    assert r is None or r["shape"] == "curve"


def test_config_roundtrip():
    cfg = R.RecognizerConfig({"snap_angle": 3.0, "enabled": ["line", "circle"]})
    back = R.RecognizerConfig(cfg.to_dict())
    assert back.snap_angle == 3.0
    assert back.enabled == {"line", "circle"}
    assert back.tol("circle") == pytest.approx(cfg.tol("circle"))


# ———————— 线型与鲁棒性 ————————

def test_dash_pattern_params_exposed():
    """虚线/点线须给出 pattern 参数（上层据此以虚线/点线样式重绘）。"""
    got = None
    for _ in range(N):
        r = R.recognize(synth.dashed())
        if r and r["shape"] in ("dashed", "dotted"):
            got = r
            break
    assert got is not None
    assert got["params"].get("pattern") in ("dashed", "dotted")
    assert int(got["params"]["dashes"]) >= 3


def test_robustness_small_and_degenerate():
    """少点 / 单点 / 重复点 / NaN：不抛异常（返回 None 即可）。"""
    assert R.recognize([]) is None
    assert R.recognize([[0, 0]]) is None
    assert R.recognize([[0, 0], [0, 0], [0, 0], [0, 0]]) is None
    bad = [[0, 0], [1, 1], [np.nan, 2], [3, 3], [4, 5], [6, 7]]
    assert R.recognize(bad) is None


def test_performance_budget():
    """200 点单次识别耗时应 < 40ms（课堂停顿 1s 触发，远超预算即视为回归）。"""
    import time
    pts = synth.circle(n=200)
    R.recognize(pts)     # 预热
    t0 = time.perf_counter()
    for _ in range(10):
        R.recognize(pts)
    ms = (time.perf_counter() - t0) / 10 * 1000
    assert ms < 40.0, f"单次识别 {ms:.1f}ms 超预算"