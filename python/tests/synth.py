#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
合成手绘笔迹生成器（识别算法的测试基准）
========================================
设计要点：不是「理想几何 + 高斯噪声」——那样测不出真实可用性。这里模拟真实课堂手绘的
六类畸变，让被测算法面对与现场同等难度的输入：

  1. 高斯抖动        σ 随笔长缩放（快笔更抖）
  2. 采样不均        点间距随机（真实报点率随笔速变化）
  3. 闭合缺口        闭环图形首尾留缝（多为 2%~14% 周长）
  4. 起收笔钩尾      落笔/抬笔常有小拖尾
  5. 仿射畸变        轻度拉伸/剪切（手绘椭圆常歪、矩形常不齐）
  6. 端点漂移        直线两端偏向

所有生成器返回 (N, 2) float64 点序列（图像坐标系，y 向下）。
"""

from __future__ import annotations

import math

import numpy as np

RNG = np.random.default_rng(20261021)


def _jitter(pts: np.ndarray, sigma: float) -> np.ndarray:
    n = len(pts)
    if n == 0:
        return pts
    tang = np.gradient(pts, axis=0)
    norm = np.hypot(tang[:, 0], tang[:, 1])
    norm[norm < 1e-9] = 1.0
    perp = np.c_[-tang[:, 1] / norm, tang[:, 0] / norm]
    along = tang / norm[:, None]
    side = RNG.normal(0, sigma, n)[:, None]
    push = RNG.normal(0, sigma * 0.5, n)[:, None]
    return pts + perp * side + along * push


def _resample_random(pts: np.ndarray, base_step: float = 3.5) -> np.ndarray:
    """按随机步长重采样（模拟报点间距不均）。"""
    if len(pts) < 2:
        return pts
    seg = np.hypot(np.diff(pts[:, 0]), np.diff(pts[:, 1]))
    cum = np.concatenate([[0.0], np.cumsum(seg)])
    total = cum[-1]
    if total < base_step:
        return pts
    steps = []
    s = 0.0
    while s < total:
        steps.append(s)
        s += base_step * RNG.uniform(0.45, 1.8)
    steps.append(total)
    x = np.interp(steps, cum, pts[:, 0])
    y = np.interp(steps, cum, pts[:, 1])
    return np.c_[x, y]


def _affine(pts: np.ndarray, stretch=1.0, shear=0.0, rotate=0.0, scale=1.0) -> np.ndarray:
    m = np.array([[1.0, shear], [0.0, stretch]])
    c, s = math.cos(math.radians(rotate)), math.sin(math.radians(rotate))
    r = np.array([[c, -s], [s, c]])
    return (pts * scale) @ (m @ r).T


def _hooks(pts: np.ndarray, prob: float = 0.5, amp: float = 6.0) -> np.ndarray:
    """起收笔小钩尾（真实手绘常见的抬笔拖尾）。"""
    if len(pts) < 4 or RNG.random() > prob:
        return pts
    d0 = pts[1] - pts[0]
    d1 = pts[-1] - pts[-2]
    a = np.array([-d0[1], d0[0]]); a = a / max(np.hypot(*a), 1e-9)
    b = np.array([-d1[1], d1[0]]); b = b / max(np.hypot(*b), 1e-9)
    head = pts[0] + a * amp * RNG.uniform(0.3, 1.0)
    tail = pts[-1] + b * amp * RNG.uniform(0.3, 1.0)
    return np.vstack([head, pts, tail])


def _finish(pts: np.ndarray, noise=1.1, hooks=True) -> np.ndarray:
    pts = _affine(pts, stretch=RNG.uniform(0.94, 1.06), shear=RNG.uniform(-0.08, 0.08),
                  rotate=RNG.uniform(-6, 6), scale=RNG.uniform(0.85, 1.15))
    pts = _resample_random(pts)
    if hooks:
        pts = _hooks(pts)
    pts = _jitter(pts, noise)
    return np.asarray(pts, dtype=np.float64)


def _closed(pts: np.ndarray, gap_ratio=(0.02, 0.14)) -> np.ndarray:
    """闭环图形首尾留缝。"""
    gap = RNG.uniform(*gap_ratio) * float(np.hypot(np.diff(pts[:, 0]).sum(), 0)) 
    total = float(np.hypot(np.diff(pts[:, 0]), np.diff(pts[:, 1])).sum())
    cut = RNG.uniform(*gap_ratio) * total
    # 去掉末端一小段形成缺口
    seg = np.hypot(np.diff(pts[:, 0]), np.diff(pts[:, 1]))
    cum = np.concatenate([[0.0], np.cumsum(seg)])
    keep = cum <= (cum[-1] - cut)
    out = pts[keep]
    return out if len(out) >= 6 else pts


def line(n=40, length=420.0, angle=None):
    a = RNG.uniform(0, 2 * math.pi) if angle is None else math.radians(angle)
    t = np.linspace(0, 1, n)[:, None]
    pts = np.c_[t * length * math.cos(a), t * length * math.sin(a)]
    return _finish(pts, hooks=RNG.random() < 0.4)


def circle(r=140.0, n=72):
    t = np.linspace(0, 2 * math.pi, n, endpoint=False)
    return _finish(_closed(np.c_[r * np.cos(t), r * np.sin(t)]), hooks=False)


def ellipse(a=180.0, b=110.0, ang=25.0, n=72):
    t = np.linspace(0, 2 * math.pi, n, endpoint=False)
    ca, sa = math.cos(math.radians(ang)), math.sin(math.radians(ang))
    x = a * np.cos(t); y = b * np.sin(t)
    return _finish(_closed(np.c_[ca * x - sa * y, sa * x + ca * y]), hooks=False)


def _quad_from_verts(v: np.ndarray, n_step=14):
    pts = []
    for i in range(4):
        a, b = v[i], v[(i + 1) % 4]
        for t in np.linspace(0, 1, n_step, endpoint=False):
            pts.append(a * (1 - t) + b * t)
    pts = np.array(pts)
    return _finish(_closed(pts), hooks=False)


def square(side=200.0):
    h = side / 2
    return _quad_from_verts(np.array([[-h, -h], [h, -h], [h, h], [-h, h]]))


def rectangle(w=260.0, h=150.0):
    return _quad_from_verts(np.array([[-w / 2, -h / 2], [w / 2, -h / 2], [w / 2, h / 2], [-w / 2, h / 2]]))


def parallelogram(w=250.0, h=150.0, shear=0.35):
    return _quad_from_verts(np.array([[-w / 2, -h / 2], [w / 2 - w * shear, -h / 2],
                                      [-w / 2 + w * shear, h / 2], [-w / 2, h / 2]][::-1]))


def rhombus(w=230.0, h=140.0):
    return _quad_from_verts(np.array([[-w / 2, 0], [0, -h / 2], [w / 2, 0], [0, h / 2]]))


def trapezoid(top=120.0, bottom=250.0, h=150.0):
    return _quad_from_verts(np.array([[-top / 2, -h / 2], [top / 2, -h / 2],
                                      [bottom / 2, h / 2], [-bottom / 2, h / 2]]))


def triangle(kind="isosceles", w=260.0, h=200.0):
    if kind == "isosceles":
        v = np.array([[-w / 2, h / 2], [w / 2, h / 2], [0, -h / 2]])
    elif kind == "right":
        v = np.array([[-w / 2, h / 2], [w / 2, h / 2], [-w / 2, -h / 2]])
    else:  # equilateral
        s = 220.0
        v = np.array([[0, -s * math.sqrt(3) / 3], [s / 2, s * math.sqrt(3) / 6],
                      [-s / 2, s * math.sqrt(3) / 6]])
    pts = []
    for i in range(3):
        a, b = v[i], v[(i + 1) % 3]
        for t in np.linspace(0, 1, 18, endpoint=False):
            pts.append(a * (1 - t) + b * t)
    return _finish(_closed(np.array(pts)), hooks=False)


def polygon(k=5, r=150.0):
    angs = np.linspace(0, 2 * math.pi, k, endpoint=False) - math.pi / 2
    v = np.c_[r * np.cos(angs), r * np.sin(angs)]
    pts = []
    for i in range(k):
        a, b = v[i], v[(i + 1) % k]
        for t in np.linspace(0, 1, 14, endpoint=False):
            pts.append(a * (1 - t) + b * t)
    return _finish(_closed(np.array(pts)), hooks=False)


def star(rays=5, r_out=170.0, r_in=None):
    r_in = r_in if r_in is not None else r_out * 0.42
    k = rays * 2
    angs = np.linspace(0, 2 * math.pi, k, endpoint=False) - math.pi / 2
    rads = np.where(np.arange(k) % 2 == 0, r_out, r_in)
    v = np.c_[rads * np.cos(angs), rads * np.sin(angs)]
    pts = []
    for i in range(k):
        a, b = v[i], v[(i + 1) % k]
        for t in np.linspace(0, 1, 10, endpoint=False):
            pts.append(a * (1 - t) + b * t)
    return _finish(_closed(np.array(pts)), hooks=False)


def heart(scale=150.0):
    t = np.linspace(0, 2 * math.pi, 96)
    x = 16 * np.sin(t) ** 3
    y = 13 * np.cos(t) - 5 * np.cos(2 * t) - 2 * np.cos(3 * t) - np.cos(4 * t)
    pts = np.c_[x / 16.0 * scale, -y / 16.0 * scale]
    return _finish(_closed(pts), hooks=False)


def arrow(angle=20.0, length=380.0, head=70.0, two_way=False, style=None):
    """箭头：三种真实绘制序变体（算法不得依赖某一种点序）：
      A 干→翼1→翼2（回到尖端收笔，最常见）
      B 干→翼1→翼2（收笔在翼尖，不回尖端）
      C 翼1→翼2→干（先画头再拉杆）"""
    a = math.radians(angle)
    d = np.array([math.cos(a), math.sin(a)])
    start = -d * length / 2
    tip = d * length / 2
    shaft = np.linspace(start, tip, 34)
    w = np.array([-d[1], d[0]])
    left = tip - d * head + w * head * 0.55
    right = tip - d * head - w * head * 0.55
    style = style if style else int(RNG.integers(0, 3))
    if style == 0:
        pts = np.vstack([shaft, np.linspace(left, tip, 5)[1:], np.linspace(right, tip, 5)[1:]])
    elif style == 1:
        pts = np.vstack([shaft, np.linspace(left, tip, 5)[1:], np.linspace(right, tip, 5)[1:5]])
    else:
        pts = np.vstack([np.linspace(left, tip, 6), np.linspace(right, tip, 6)[::-1], shaft[::-1]])
    if two_way:
        l2 = start + d * head + w * head * 0.55
        r2 = start + d * head - w * head * 0.55
        pts = np.vstack([np.linspace(l2, start, 5)[1:], np.linspace(r2, start, 5)[1:], pts])
    return _finish(pts, hooks=False)


def check(size=200.0):
    v = np.array([[-size * 0.5, 0], [0, size * 0.45], [size * 0.5, -size * 0.45]])
    pts = []
    for i in range(2):
        a, b = v[i], v[i + 1]
        for t in np.linspace(0, 1, 22, endpoint=False):
            pts.append(a * (1 - t) + b * t)
    pts.append(v[-1])
    return _finish(np.array(pts), hooks=False)


def cross(size=200.0):
    """叉的真实画法：第一笔到端 → 折回交点 → 第二笔到另一端点（一笔连续）。"""
    a0 = np.array([-size / 2, -size / 2]); a1 = np.array([size / 2, size / 2])
    b1 = np.array([-size / 2, size / 2])
    mid = np.zeros(2)
    pts = np.vstack([np.linspace(a0, a1, 26), np.linspace(a1, mid, 10), np.linspace(mid, b1, 26)])
    return _finish(pts, hooks=False)


def wave(width=420.0, amp=70.0, periods=2.5):
    t = np.linspace(0, 2 * math.pi * periods, 90)
    return _finish(np.c_[np.linspace(0, width, 90), amp * np.sin(t)], hooks=False)


def cloud():
    """云朵：沿极坐标半径调制生成的闭合鼓包轮廓（顶部 3~4 个鼓包 + 底部平缓）。
    闭环由角度参数保证首尾相接，再交 _closed 留缺口。"""
    t = np.linspace(0, 2 * math.pi, 140, endpoint=False)
    # 上半（y 负方向）鼓包：3 个余弦波瓣；下半平缓
    lobes = 1.0 + 0.18 * np.cos(t * 1.0)
    up = np.clip(-np.sin(t), 0, 1)
    r = 130 * lobes * (1.0 + 0.16 * up * (1.0 + 0.6 * np.cos(3.0 * t)))
    pts = np.c_[r * np.cos(t), r * np.sin(t)]
    pts[:, 1] *= 0.78
    pts = pts - pts.mean(axis=0)
    return _finish(_closed(pts), hooks=False)


def freeform(n=80, seed=None):
    """自由曲线/随手涂鸦：多个正弦叠加 + 随机漂移（不应被识别为具体形状）。"""
    rng = np.random.default_rng(seed) if seed is not None else RNG
    t = np.linspace(0, 1, n)
    x = t * 380 + 22 * np.sin(7 * t + rng.uniform(0, 3)) + 14 * np.sin(19 * t)
    y = 130 * np.sin(2.3 * math.pi * t + rng.uniform(0, 3)) + 40 * t + 11 * np.cos(13 * t)
    return _finish(np.c_[x, y], hooks=False)


def scribble(n=60, seed=None):
    """乱涂：随机游走（绝不应识别为具体形状）。"""
    rng = np.random.default_rng(seed) if seed is not None else RNG
    ang = rng.uniform(0, 2 * math.pi)
    pts = [np.zeros(2)]
    for _ in range(n):
        ang += rng.normal(0, 0.8)
        pts.append(pts[-1] + np.array([math.cos(ang), math.sin(ang)]) * rng.uniform(4, 16))
    return _jitter(np.array(pts), 0.8)

def dashed(length=360.0, dashes=6, gap_ratio=1.1, dots=False, angle=None):
    """虚线 / 点线（点线 = 划极短的虚线）：一笔连续绘制的"划—隙"节奏。
    划段用密采样（step≈2.5px），间隙用稀疏采样（step≈gap）—— 与真实手绘一致：
    快划过去时采样点被拉稀。dots=True 时划段只留 1~2 个点（视觉即点）。"""
    a = RNG.uniform(0, 2 * math.pi) if angle is None else math.radians(angle)
    d = np.array([math.cos(a), math.sin(a)])
    n_dash = max(dashes, 3)
    dash_len = length / n_dash * 0.52
    gap_len = dash_len * gap_ratio
    pts = []
    t = 0.0
    while t < length:
        if dots:
            # 点线的"点"：极短密段（约 6px 内 3 点）—— 视觉即一个点
            seg = np.array([0.0, 2.5, 5.0])
            cur_dash = 6.0
            cur_gap = gap_len
        else:
            seg = np.arange(0, dash_len, 2.4)
            cur_dash = dash_len
            cur_gap = gap_len
        for dlt in seg:
            pts.append(d * (t + dlt))
        t += cur_dash
        g = 0.0
        while g < cur_gap:
            pts.append(d * (t + g))
            g += max(cur_gap / 2.5, 9.0)
        t += cur_gap
    pts = np.array(pts)
    if len(pts) < 14:
        return line(length=length, angle=angle)
    pts = _affine(pts, stretch=RNG.uniform(0.96, 1.04), shear=RNG.uniform(-0.05, 0.05),
                  rotate=RNG.uniform(-5, 5) if angle is None else 0.0, scale=RNG.uniform(0.9, 1.1))
    return _jitter(pts, 0.9)


def dotted(length=320.0, dots=9, angle=None):
    return dashed(length=length, dashes=dots, gap_ratio=2.6, dots=True, angle=angle)
