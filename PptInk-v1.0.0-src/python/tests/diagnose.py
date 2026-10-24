#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""识别质量诊断：跑各形状合成样本，输出识别率、误判分布、误报率与耗时。
开发用工具（CI 里作为质量门禁的可观测输出）。用法：
    python3 python/tests/diagnose.py [每组样本数]
"""
import collections
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import numpy as np
import synth
import pptink_recog as R

N = int(sys.argv[1]) if len(sys.argv) > 1 else 30

CASES = [
    ("line", lambda: synth.line(), {"line"}),
    ("dashed", lambda: synth.dashed(), {"dashed"}),
    ("dotted", lambda: synth.dotted(), {"dotted"}),
    ("circle", lambda: synth.circle(), {"circle"}),
    ("ellipse", lambda: synth.ellipse(), {"ellipse"}),
    ("square", lambda: synth.square(), {"quad"}),
    ("rectangle", lambda: synth.rectangle(), {"quad"}),
    ("parallelogram", lambda: synth.parallelogram(), {"quad"}),
    ("rhombus", lambda: synth.rhombus(), {"quad"}),
    ("trapezoid", lambda: synth.trapezoid(), {"quad"}),
    ("triangle", lambda: synth.triangle(), {"triangle"}),
    ("polygon5", lambda: synth.polygon(5), {"polygon"}),
    ("polygon6", lambda: synth.polygon(6), {"polygon"}),
    ("arrow", lambda: synth.arrow(), {"arrow"}),
    ("check", lambda: synth.check(), {"check"}),
    ("cross", lambda: synth.cross(), {"cross"}),
    ("wave", lambda: synth.wave(), {"wave"}),
]

print(f"{'case':<14}{'n':>4}{'ok':>4}{'rate':>7}  commons")
total_ok = total = 0
for name, gen, expect in CASES:
    ok = 0
    misc = collections.Counter()
    for _ in range(N):
        pts = gen()
        try:
            r = R.recognize(pts)
        except Exception as e:
            misc[f"EXC:{type(e).__name__}"] += 1
            continue
        got = r["shape"] if r else "none"
        if got in expect:
            ok += 1
        else:
            misc[got] += 1
    total_ok += ok
    total += N
    top = ", ".join(f"{k}:{v}" for k, v in misc.most_common(3))
    print(f"{name:<14}{N:>4}{ok:>4}{ok/N:>7.2f}  {top}")

# —— 误报：自由曲线/乱涂不应被识别为具体几何 ——
fp = 0
shapes_seen = collections.Counter()
for i in range(N * 2):
    pts = synth.freeform(seed=i) if i % 2 == 0 else synth.scribble(seed=i)
    r = R.recognize(pts)
    if r and r["shape"] != "curve":
        fp += 1
        shapes_seen[r["shape"]] += 1
print(f"\n误报 freeform/scribble → 具体形状: {fp}/{N*2}  {dict(shapes_seen)}")
print(f"总体识别率: {total_ok}/{total} = {total_ok/total:.3f}")

# —— 性能 ——
pts = synth.circle(n=200)
t0 = time.perf_counter()
for _ in range(50):
    R.recognize(pts)
dt = (time.perf_counter() - t0) / 50 * 1000
print(f"性能：200 点平均 {dt:.2f} ms/次（目标 < 10ms）")