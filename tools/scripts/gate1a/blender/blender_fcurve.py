# SPDX-License-Identifier: 0BSD
"""Blender's keyframe F-curve evaluation, restated in Python for hop D (never run inside Blender).

Restated from Blender's ``blenkernel/intern/fcurve.cc`` as the 5.x series ships it (written from the published source
text; no Blender source is on this machine, so the real Blender readback is the arbiter: hop D compares Blender's own
``FCurve.evaluate`` with this function and fails on any disagreement beyond ``residual_bound``):

* ``fcurve_eval_keyframes``: at or before the first key and at or after the last key the CONSTANT extrapolation returns
  that key's value;
* ``fcurve_eval_keyframes_interpolate``: ``BKE_fcurve_bezt_binarysearch_index_ex`` with threshold 0.0001 frames tests
  the first key, then the last key, then bisection midpoints, and a frame within the threshold (``IS_EQT``, ``<=``) of
  the key it tests returns that key's value; a frame within 1e-8 of the upper key returns it; a CONSTANT interval holds
  the lower key; LINEAR is ``BLI_easing_linear_ease`` (change * time / duration + begin, Float32); BEZIER returns the
  lower key when the four control values differ by less than FLT_EPSILON (|v1-v4|, |v2-v3|, |v3-v4|; the #40372
  shortcut), else solves the frame cubic (``findzero``: coefficients in Float32, ``solve_cubic`` in double, the root
  cast to Float32 and accepted in [-1e-10, 1.000001]) and evaluates the value cubic in Float32 (``berekeny``);
  a root that cannot be found returns 0.
* ``BKE_fcurve_correct_bezpart`` is restated too; it changes nothing when both handles lie inside the interval, which
  is what the importer writes.

``evaluate`` takes the curve as ``anim_package.fcurve`` returns it (Float32 points, left and right handles or None, and
per-key modes CONSTANT / LINEAR / BEZIER).
"""

import math

import numpy as np

F32 = np.float32
KEY_SEARCH_THRESHOLD = F32(0.0001)
EXACT_EPSILON = F32(1e-8)
FLT_EPSILON = F32(2.0 ** -23)
SMALL = -1.0e-10


def _f(value):
    return F32(value)


def _is_eqt(a, b, c):
    return (a - b) <= c if a > b else (b - a) <= c


def binary_search(frames, frame):
    """(index, exact) as ``BKE_fcurve_bezt_binarysearch_index_ex`` with the 0.0001 threshold."""
    count = len(frames)
    if count == 0:
        return 0, False
    first = frames[0]
    if _is_eqt(frame, first, KEY_SEARCH_THRESHOLD):
        return 0, True
    if frame < first:
        return 0, False
    last = frames[count - 1]
    if _is_eqt(frame, last, KEY_SEARCH_THRESHOLD):
        return count - 1, True
    if frame > last:
        return count, False
    start, end = 0, count
    loop, maximum = 0, count * 2
    while start <= end and loop < maximum:
        middle = start + (end - start) // 2
        value = frames[middle]
        if _is_eqt(frame, value, KEY_SEARCH_THRESHOLD):
            return middle, True
        if frame > value:
            start = middle + 1
        elif frame < value:
            end = middle - 1
        loop += 1
    return start, False


def _cbrt(d):
    if d == 0.0:
        return 0.0
    if d < 0.0:
        return -math.exp(math.log(-d) / 3)
    return math.exp(math.log(d) / 3)


def _accept(root):
    return float(F32(SMALL)) <= root <= float(F32(1.000001))


def solve_cubic(c0, c1, c2, c3):
    """``solve_cubic``: the accepted Float32 roots in Blender's order."""
    roots = []

    def push(value):
        root = float(F32(value))
        roots.append(root)
        return _accept(root)

    if c3 != 0.0:
        a = c2 / c3
        b = c1 / c3
        c = c0 / c3
        a = a / 3
        p = b / 3 - a * a
        q = (2 * a * a * a - a * b + c) / 2
        d = q * q + p * p * p
        if d > 0.0:
            t = math.sqrt(d)
            return [roots[-1]] if push(_cbrt(-q + t) + _cbrt(-q - t) - a) else []
        if d == 0.0:
            t = _cbrt(-q)
            out = []
            if push(2 * t - a):
                out.append(roots[-1])
            if push(-t - a):
                out.append(roots[-1])
            return out
        phi = math.acos(max(-1.0, min(1.0, -q / math.sqrt(-(p * p * p)))))
        t = math.sqrt(-p)
        p = math.cos(phi / 3)
        q = math.sqrt(3 - 3 * p * p)
        out = []
        for value in (2 * t * p - a, -t * (p + q) - a, -t * (p - q) - a):
            if push(value):
                out.append(roots[-1])
        return out
    a, b, c = c2, c1, c0
    if a != 0.0:
        p = b * b - 4 * a * c
        if p > 0:
            p = math.sqrt(p)
            out = []
            for value in ((-b - p) / (2 * a), (-b + p) / (2 * a)):
                if push(value):
                    out.append(roots[-1])
            return out
        if p == 0:
            return [roots[-1]] if push(-b / (2 * a)) else []
        return []
    if b != 0.0:
        return [roots[-1]] if push(-c / b) else []
    if c == 0.0:
        return [0.0]
    return []


def findzero(x, q0, q1, q2, q3):
    """``findzero``: Float32 coefficients widened to double, then ``solve_cubic``."""
    x, q0, q1, q2, q3 = (_f(v) for v in (x, q0, q1, q2, q3))
    c0 = float(F32(q0 - x))
    c1 = float(F32(F32(3.0) * F32(q1 - q0)))
    c2 = float(F32(F32(3.0) * F32(F32(q0 - F32(F32(2.0) * q1)) + q2)))
    c3 = float(F32(F32(F32(q3 - q0)) + F32(F32(3.0) * F32(q1 - q2))))
    return solve_cubic(c0, c1, c2, c3)


def berekeny(f1, f2, f3, f4, t):
    """``berekeny`` for one parameter, every operation in Float32 left to right."""
    f1, f2, f3, f4, t = (_f(v) for v in (f1, f2, f3, f4, t))
    c0 = f1
    c1 = F32(F32(3.0) * F32(f2 - f1))
    c2 = F32(F32(3.0) * F32(F32(f1 - F32(F32(2.0) * f2)) + f3))
    c3 = F32(F32(F32(f4 - f1)) + F32(F32(3.0) * F32(f2 - f3)))
    value = F32(c0 + F32(t * c1))
    value = F32(value + F32(F32(t * t) * c2))
    value = F32(value + F32(F32(F32(t * t) * t) * c3))
    return float(value)


def correct_bezpart(v1, v2, v3, v4):
    """``BKE_fcurve_correct_bezpart`` in Float32; returns the possibly moved (v2, v3)."""
    v1 = [_f(v) for v in v1]
    v2 = [_f(v) for v in v2]
    v3 = [_f(v) for v in v3]
    v4 = [_f(v) for v in v4]
    h1 = [F32(v1[0] - v2[0]), F32(v1[1] - v2[1])]
    h2 = [F32(v4[0] - v3[0]), F32(v4[1] - v3[1])]
    span = F32(v4[0] - v1[0])
    len1, len2 = F32(abs(h1[0])), F32(abs(h2[0]))
    if F32(len1 + len2) == 0:
        return v2, v3
    if len1 > span:
        fac = F32(span / len1)
        v2 = [F32(v1[0] - F32(fac * h1[0])), F32(v1[1] - F32(fac * h1[1]))]
    if len2 > span:
        fac = F32(span / len2)
        v3 = [F32(v4[0] - F32(fac * h2[0])), F32(v4[1] - F32(fac * h2[1]))]
    return v2, v3


def evaluate(curve, frame):
    """Blender's value of the F-curve at ``frame`` (a Float32). Returns (value, path) where path names the branch."""
    points = curve['points']
    frame = F32(frame)
    frames = [F32(v) for v in points[:, 0]]
    count = len(frames)
    if frame <= frames[0]:
        return float(F32(points[0, 1])), 'extrapolate-first'
    if frames[count - 1] <= frame:
        return float(F32(points[count - 1, 1])), 'extrapolate-last'
    index, exact = binary_search(frames, frame)
    if exact:
        return float(F32(points[index, 1])), 'key-search'
    if abs(F32(frames[index] - frame)) < EXACT_EPSILON or index == 0:
        return float(F32(points[index, 1])), 'exact-key'
    lower = index - 1
    mode = curve['modes'][lower]
    begin = F32(points[lower, 1])
    duration = F32(frames[index] - frames[lower])
    if mode == 'CONSTANT' or duration == 0:
        return float(begin), 'constant'
    if mode == 'LINEAR':
        change = F32(F32(points[index, 1]) - begin)
        time = F32(frame - frames[lower])
        return float(F32(F32(F32(change * time) / duration) + begin)), 'linear'
    v1 = [frames[lower], begin]
    v2 = [F32(curve['right'][lower, 0]), F32(curve['right'][lower, 1])]
    v3 = [F32(curve['left'][index, 0]), F32(curve['left'][index, 1])]
    v4 = [frames[index], F32(points[index, 1])]
    if (abs(F32(v1[1] - v4[1])) < FLT_EPSILON and abs(F32(v2[1] - v3[1])) < FLT_EPSILON
            and abs(F32(v3[1] - v4[1])) < FLT_EPSILON):
        return float(v1[1]), 'flat'
    v2, v3 = correct_bezpart(v1, v2, v3, v4)
    roots = findzero(frame, v1[0], v2[0], v3[0], v4[0])
    if not roots:
        return 0.0, 'findzero-failed'
    return berekeny(v1[1], v2[1], v3[1], v4[1], roots[0]), 'bezier'


def residual_bound(curve, frame):
    """The allowance between Blender and this restatement: 8 Float32 steps of the span's largest control value (the
    value cubic's own rounding if an operation contracts or reorders), plus the value change one Float32 step of the
    root can cause (the double math library of Blender and of Python may differ in the last bit)."""
    points = curve['points']
    frames = points[:, 0].astype(np.float64)
    k = int(np.searchsorted(frames, float(frame), side='right')) - 1
    k = min(max(k, 0), len(points) - 1)
    values = [abs(float(points[k, 1]))]
    slope = 0.0
    if k + 1 < len(points):
        values.append(abs(float(points[k + 1, 1])))
        if curve['left'] is not None:
            y = [float(points[k, 1]), float(curve['right'][k, 1]), float(curve['left'][k + 1, 1]), float(points[k + 1, 1])]
            values.extend(abs(v) for v in y[1:3])
            slope = 3 * max(abs(y[1] - y[0]), abs(y[2] - y[1]), abs(y[3] - y[2]))
        else:
            slope = abs(float(points[k + 1, 1]) - float(points[k, 1]))
    magnitude = max(values)
    return 8 * 2.0 ** -24 * max(magnitude, 1e-38) + slope * 2.0 ** -23
