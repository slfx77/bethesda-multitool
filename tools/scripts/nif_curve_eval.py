# SPDX-License-Identifier: 0BSD
"""Independent curve evaluator for cut-1b hop A8-sample (docs/design/cut1b-nif-animation-reader-plan-20260925.md,
section 3).

Usage:
    python nif_curve_eval.py <input.json> <output.json>

The input (schema ``nif-curve-eval/1``) is written by the C# test NifAnimationA8SampleOracleTests: for one manifest
file, every keyed transform or Euler track of the reader's document, its curve payload exactly as the document holds it
(float bits), and the poses Shared's ScenePoseEvaluator produced at sampled times (the track-local time after the
document's clip and track clocks, and the observed translation, scale or local rotation matrix of the track's node in a
document that holds that track alone). This script evaluates each curve again, with plain Python and numpy and no BMT or
Shared code, and compares:

* Hermite: the cubic Hermite on the normalized segment, tangents as stored ([incoming, value, outgoing]), no time
  scaling.
* TBC: Kochanek-Bartels interior tangents (the engine's CalculateDVals, RE-19: ((1+C)h(1-B))fwd + ((1-C)h(1+B))bwd for
  the incoming and ((1-C)h(1-B))fwd + ((1+C)h(1+B))bwd for the outgoing, h = (1-T)/2, times 2*pre/(pre+next) and
  2*next/(pre+next)), each rounded to Float32; the document's boundary tangents at the ends.
* B-spline: open-uniform clamped Cox-de Boor (explicit knot vector, the standard triangular basis recursion), compact
  controls decoded in Float32 as bias + (s / 32767) * multiplier.
* Linear and CONST: interpolation and hold; quaternion LINEAR through RE-17's counter-warped nlerp in Float32, then a
  double normalization; Squad through RE-24 in Float32 with the PC FastNormalize at each nested interpolation.
* Euler: the three axis curves composed as qZ * qY * qX.

Tolerance: a scalar component must lie within 4 units in the last place (Float32) of the largest key, tangent or control
magnitude of its segment; a rotation within ROTATION_TOLERANCE_DEGREES (the angle between the two rotations, measured on
the observed local matrix). Controls, evaluated in the same run and reported beside the oracle: a B-spline evaluated with
an unclamped uniform knot vector, a TBC with continuity and bias exchanged (component curves, and Squad inner points
recomputed through RE-24 from the keys and the stored T, C, B the input carries), and a Hermite with the incoming and
outgoing tangents exchanged. Each is reported as the worst ratio of its deviation to the tolerance.

The output (schema ``nif-curve-eval-result/1``) lists every track's worst ratio, the worst deviation of the file and
each control's worst ratio. The exit status is 0 whenever the evaluation ran (the caller asserts on the ratios), 2 for
a usage or input error and 3 when numpy is missing.

Gate 1b (tools/scripts/gate1a/anim_dump.py) imports this module as its dump evaluator. For it this module adds, without
changing the result of any curve the A8 input can carry: ``dump_curve_node`` (the exact dump's curves in the node form
above), ``clock_map`` and ``map_clocks`` (SceneAnimationClock.Map and the clip-then-track composition, Float32 between
the two), CUBICSPLINE components (per-second tangents times the key interval), keyed LINEAR rotations through
``numerics_slerp`` (System.Numerics Quaternion.Slerp) and component-sampled Hermite/CubicSpline/TBC/Step rotations
normalized in double. The NIF reader emits none of those rotation or CUBICSPLINE forms, so the A8 tracks keep their
previous evaluation.
"""

import bisect
import json
import math
import struct
import sys

try:
    import numpy as np
except ImportError:  # pragma: no cover - reported to the caller
    sys.stderr.write('numpy is required\n')
    sys.exit(3)

SCHEMA_IN = 'nif-curve-eval/1'
SCHEMA_OUT = 'nif-curve-eval-result/1'
SCALAR_ULPS = 4
ROTATION_TOLERANCE_DEGREES = 2e-4
# The ratio reported for a track the evaluator could not evaluate (JSON has no infinity).
ERROR_RATIO = 1e300
F32 = np.float32

# RE-17 counter-warp constants (Float32).
ATTEN = F32(0.8227969)
SLOPE = F32(0.5854922)
# RE-17 / RE-24 PC FastNormalize constants, as Shared's Squad sampler states them.
FN_N = F32(0.9590659737586975)
FN_F = F32(-0.5325155854225159)
FN_A = F32(1.0214351415634155)
FN_T1 = F32(0.9152119755744934)
FN_T2 = F32(0.6521196961402893)
# RE-24 constants.
PI_F = struct.unpack('<f', struct.pack('<I', 0x40490FDB))[0]
EPS_F = struct.unpack('<f', struct.pack('<I', 0x3A83126F))[0]
# Interpolations whose keys are incoming/value/outgoing triples (Shared SceneCurve: "Hermite and CubicSpline keys contain
# incoming, value and outgoing triples"; GamebryoSquad stores its inner points the same way). CubicSpline joined for
# gate 1b (ScenePoseChannel.Sample line 26: cubic = CubicSpline or Hermite).
TRIPLE_INTERPOLATIONS = ('Hermite', 'GamebryoSquad', 'CubicSpline')


def f32(bits):
    """The Float32 value of a uint bit pattern, as a numpy float32."""
    return F32(struct.unpack('<f', struct.pack('<I', bits & 0xFFFFFFFF))[0])


def ulp32(magnitude):
    return float(np.spacing(F32(abs(magnitude)))) if magnitude != 0 else float(np.spacing(F32(0)))


# ----------------------------------------------------------------------------------------------------------- curves
class Curve:
    """One document curve: times, key-major values (triples for Hermite), TBC parameters and endpoints, or a spline."""

    def __init__(self, node):
        self.interpolation = node['interpolation']
        self.width = node['width']
        self.state = node.get('state', 'Keyed')
        self.static = [f32(b) for b in node.get('static') or []]
        self.times = [f32(b) for b in node.get('times') or []]
        self.time_list = [float(x) for x in self.times]
        self.values = [f32(b) for b in node.get('values') or []]
        tbc = node.get('tbc')
        self.tbc = None if tbc is None else [(f32(tbc[i]), f32(tbc[i + 1]), f32(tbc[i + 2])) for i in
                                             range(0, len(tbc), 3)]
        self.start_outgoing = [f32(b) for b in node.get('startOutgoing') or []]
        self.end_incoming = [f32(b) for b in node.get('endIncoming') or []]
        self.spline = node.get('spline')
        self.squad_tbc = node.get('squadTbc')
        # Shared SceneGamebryoSquadPolicy: PcFloat32 (Blow FastNormalize) or Xbox360Estimate (the unit-normalization
        # centre, each factor 1f / MathF.Sqrt(s)); None for every other curve.
        self.squad_policy = node.get('squadPolicy')
        if self.spline is not None:
            s = self.spline
            self.degree = s['degree']
            self.count = s['count']
            self.start = f32(s['start'])
            self.stop = f32(s['stop'])
            if s['quantized']:
                bias = f32(s['bias'])
                multiplier = f32(s['multiplier'])
                self.controls = [F32(bias + (F32(v) / F32(32767.0)) * multiplier) for v in s['shorts']]
            else:
                self.controls = [f32(b) for b in s['floats']]

    def key_value(self, key, component):
        if self.interpolation in TRIPLE_INTERPOLATIONS:
            stride = self.width * 3
            return self.values[key * stride + self.width + component]
        return self.values[key * self.width + component]

    def hermite_parts(self, key, component, swap=False):
        stride = self.width * 3
        incoming = self.values[key * stride + component]
        outgoing = self.values[key * stride + 2 * self.width + component]
        return (outgoing, incoming) if swap else (incoming, outgoing)


def upper_bound(curve, t):
    """The first key strictly later than t (Shared's segment rule for component and LINEAR curves)."""
    return bisect.bisect_right(curve.time_list, float(t))


def kb_tangents(curve, key, component, swap_cb=False):
    """RE-19 CalculateDVals at an interior key, in double, each tangent rounded to Float32: (incoming, outgoing)."""
    t0 = float(curve.times[key - 1])
    t1 = float(curve.times[key])
    t2 = float(curve.times[key + 1])
    tension, continuity, bias = (float(x) for x in curve.tbc[key])
    if swap_cb:
        continuity, bias = bias, continuity
    v_prev = float(curve.key_value(key - 1, component))
    v = float(curve.key_value(key, component))
    v_next = float(curve.key_value(key + 1, component))
    bwd = v - v_prev
    fwd = v_next - v
    h = (1.0 - tension) * 0.5
    incoming_raw = ((1.0 + continuity) * h * (1.0 - bias)) * fwd + ((1.0 - continuity) * h * (1.0 + bias)) * bwd
    outgoing_raw = ((1.0 - continuity) * h * (1.0 - bias)) * fwd + ((1.0 + continuity) * h * (1.0 + bias)) * bwd
    pre = t1 - t0
    nxt = t2 - t1
    return F32(incoming_raw * (2.0 * pre / (pre + nxt))), F32(outgoing_raw * (2.0 * nxt / (pre + nxt)))


def tbc_outgoing(curve, key, component, swap_cb):
    if key == 0:
        return curve.start_outgoing[component]
    return kb_tangents(curve, key, component, swap_cb)[1]


def tbc_incoming(curve, key, component, swap_cb):
    if key == len(curve.times) - 1:
        return curve.end_incoming[component]
    return kb_tangents(curve, key, component, swap_cb)[0]


def hermite(p0, m0, p1, m1, amount):
    squared = amount * amount
    cubed = squared * amount
    return ((2 * cubed - 3 * squared + 1) * p0 + (cubed - 2 * squared + amount) * m0
            + (-2 * cubed + 3 * squared) * p1 + (cubed - squared) * m1)


def sample_components(curve, t, variant=None):
    """A component curve at t: (Float32 values, the largest key/tangent/control magnitude of the segment)."""
    if curve.state == 'Constant':
        return [F32(x) for x in curve.static], max(abs(float(x)) for x in curve.static)
    if curve.spline is not None:
        return sample_spline(curve, t, variant == 'bsplineUnclamped')
    n = len(curve.times)
    upper = upper_bound(curve, t)
    lower = max(0, upper - 1)
    width = curve.width
    if upper == 0 or upper == n or curve.interpolation == 'Step':
        values = [curve.key_value(lower, c) for c in range(width)]
        return values, max(abs(float(v)) for v in values)
    amount = (float(t) - float(curve.times[lower])) / (float(curve.times[upper]) - float(curve.times[lower]))
    out = []
    magnitude = 0.0
    for c in range(width):
        p0 = curve.key_value(lower, c)
        p1 = curve.key_value(upper, c)
        magnitude = max(magnitude, abs(float(p0)), abs(float(p1)))
        if curve.interpolation == 'Linear':
            out.append(F32((1 - amount) * float(p0) + amount * float(p1)))
        elif curve.interpolation == 'Hermite':
            swap = variant == 'hermiteForwardBackwardExchanged'
            m0 = curve.hermite_parts(lower, c, swap)[1]
            m1 = curve.hermite_parts(upper, c, swap)[0]
            magnitude = max(magnitude, abs(float(m0)), abs(float(m1)))
            out.append(F32(hermite(float(p0), float(m0), float(p1), float(m1), amount)))
        elif curve.interpolation == 'CubicSpline':
            # Gate 1b: per-second derivatives, each multiplied by the key interval (ScenePoseChannel.Sample line 45,
            # tangentScale = duration for CubicSpline only); the normalized-segment Hermite above keeps scale 1.
            duration = float(curve.times[upper]) - float(curve.times[lower])
            m0 = curve.hermite_parts(lower, c)[1]
            m1 = curve.hermite_parts(upper, c)[0]
            magnitude = max(magnitude, abs(float(m0)) * duration, abs(float(m1)) * duration)
            out.append(F32(hermite(float(p0), duration * float(m0), float(p1), duration * float(m1), amount)))
        elif curve.interpolation == 'Tbc':
            swap = variant == 'tbcContinuityBiasExchanged'
            m0 = tbc_outgoing(curve, lower, c, swap)
            m1 = tbc_incoming(curve, upper, c, swap)
            magnitude = max(magnitude, abs(float(m0)), abs(float(m1)))
            out.append(F32(hermite(float(p0), float(m0), float(p1), float(m1), amount)))
        else:
            raise ValueError('unsupported component interpolation ' + curve.interpolation)
    return out, magnitude


def clamped_knots(count, degree):
    interior = count - degree
    return [0.0] * (degree + 1) + [float(i) for i in range(1, interior)] + [float(interior)] * (degree + 1)


def uniform_knots(count, degree):
    return [float(i - degree) for i in range(count + degree + 1)]


def basis(knots, degree, span, u):
    """The standard triangular recursion (NURBS Book A2.2): the degree + 1 nonzero basis functions at u."""
    values = [1.0] + [0.0] * degree
    left = [0.0] * (degree + 1)
    right = [0.0] * (degree + 1)
    for j in range(1, degree + 1):
        left[j] = u - knots[span + 1 - j]
        right[j] = knots[span + j] - u
        saved = 0.0
        for r in range(j):
            temp = values[r] / (right[r + 1] + left[j - r])
            values[r] = saved + right[r + 1] * temp
            saved = left[j - r] * temp
        values[j] = saved
    return values


def find_span(knots, count, degree, u):
    if u >= knots[count]:
        return count - 1
    low = degree
    high = count
    while high - low > 1:
        middle = (low + high) // 2
        if u < knots[middle]:
            high = middle
        else:
            low = middle
    return low


def sample_spline(curve, t, unclamped=False):
    width = curve.width
    count = curve.count
    degree = curve.degree
    if float(t) <= float(curve.start) or float(t) >= float(curve.stop):
        first = 0 if float(t) <= float(curve.start) else count - 1
        values = [curve.controls[first * width + c] for c in range(width)]
        return values, max(abs(float(v)) for v in values)
    u = (float(t) - float(curve.start)) / (float(curve.stop) - float(curve.start)) * (count - degree)
    knots = uniform_knots(count, degree) if unclamped else clamped_knots(count, degree)
    span = find_span(knots, count, degree, u)
    weights = basis(knots, degree, span, u)
    out = []
    magnitude = 0.0
    for c in range(width):
        value = 0.0
        for slot in range(degree + 1):
            control = curve.controls[(span - degree + slot) * width + c]
            magnitude = max(magnitude, abs(float(control)))
            value += float(control) * weights[slot]
        out.append(F32(value))
    return out, magnitude


# ------------------------------------------------------------------------------------------------------ rotations
def normalize_double(q):
    """Shared's final orientation publication: double length, each component divided and rounded to Float32."""
    x, y, z, w = (float(c) for c in q)
    length = math.sqrt(x * x + y * y + z * z + w * w)
    return [F32(x / length), F32(y / length), F32(z / length), F32(w / length)]


def counter_warp(amount, correction):
    return amount * (correction + (F32(1) + (correction * amount) * ((amount + amount) - F32(3))))


def counter_warped_components(first, second, amount):
    """RE-17 in Float32 on X, Y, Z, W quaternions: the W, X, Y, Z dot, the counter-warp, p + t'(q - p)."""
    fx, fy, fz, fw = first
    sx, sy, sz, sw = second
    dot = ((fw * sw + fx * sx) + fy * sy) + fz * sz
    attenuation = F32(1) - ATTEN * dot
    correction = SLOPE * (attenuation * attenuation)
    warped = counter_warp(amount, correction) if amount <= F32(0.5) else F32(1) - counter_warp(F32(1) - amount,
                                                                                               correction)
    return [fx + warped * (sx - fx), fy + warped * (sy - fy), fz + warped * (sz - fz), fw + warped * (sw - fw)]


def fast_step(value):
    return ((value - FN_N) * FN_F) + FN_A


def fast_normalize_interp(first, second, amount, center=False):
    """The counter-warped interpolation normalized by the PC Blow FastNormalize factor, or with ``center`` by the Xbox
    360 unit-normalization centre 1f / MathF.Sqrt(s) (two correctly rounded Float32 operations; Shared
    SceneGamebryoSquad, cc5cda3), in the same Float32 order."""
    x, y, z, w = counter_warped_components(first, second, amount)
    squared = ((x * x + y * y) + z * z) + w * w
    if center:
        factor = F32(F32(1.0) / F32(np.sqrt(F32(squared))))
        return [x * factor, factor * y, z * factor, factor * w]
    factor = fast_step(squared)
    if not squared > FN_T1:
        factor = factor * fast_step((factor * factor) * squared)
        if not squared > FN_T2:
            factor = factor * fast_step((factor * factor) * squared)
    return [x * factor, factor * y, z * factor, factor * w]


def quaternion_part(curve, key, part):
    """Part 0 incoming, 1 key, 2 outgoing of a Squad triple, or the key of a LINEAR/CONST curve (X, Y, Z, W)."""
    if curve.interpolation == 'GamebryoSquad':
        base = key * 12 + part * 4
        return curve.values[base:base + 4]
    return curve.values[key * 4:key * 4 + 4]


def sample_rotation(curve, t, variant=None, squad_points=None):
    """A rotation curve at t as a unit X, Y, Z, W quaternion (Float32 components)."""
    if curve.spline is not None:
        values, _ = sample_spline(curve, t, variant == 'bsplineUnclamped')
        return normalize_double(values)
    if curve.state == 'Constant':
        return normalize_double(curve.static)
    n = len(curve.times)
    if curve.interpolation == 'GamebryoSquad':
        return normalize_double(sample_squad(curve, t, squad_points))
    upper = upper_bound(curve, t)
    if curve.interpolation == 'GamebryoCounterWarpedNlerp' and 0 < upper < n:
        lower = upper - 1
        amount = F32((float(t) - float(curve.times[lower])) / (float(curve.times[upper]) - float(curve.times[lower])))
        return normalize_double(counter_warped_components(quaternion_part(curve, lower, 1),
                                                          quaternion_part(curve, upper, 1), amount))
    if curve.interpolation == 'Linear' and 0 < upper < n:
        # Gate 1b: a keyed LINEAR quaternion is System.Numerics Quaternion.Slerp (shortest path) of the two keys at the
        # Float32 segment fraction (ScenePoseChannel.SampleRotation lines 147-153). The NIF reader emits no such track,
        # so the A8 inputs never reached this branch; before it the curve held its lower key here.
        lower = upper - 1
        amount = F32((float(t) - float(curve.times[lower])) / (float(curve.times[upper]) - float(curve.times[lower])))
        return normalize_double(numerics_slerp(quaternion_part(curve, lower, 1), quaternion_part(curve, upper, 1),
                                               amount))
    if curve.interpolation in ('Hermite', 'CubicSpline', 'Tbc', 'Step'):
        # Gate 1b: every other keyed rotation samples its four components like a vector, then normalizes in double
        # (ScenePoseChannel.SampleRotation's final branch, lines 159-163, then NormalizeRotation). A Step curve reads
        # the same lower key as the hold below; the A8 inputs never reached Hermite, CubicSpline or Tbc rotations.
        values, _ = sample_components(curve, t)
        return normalize_double(values)
    return normalize_double(quaternion_part(curve, max(0, upper - 1), 1))


def sample_squad(curve, t, points=None):
    """Shared's GamebryoSquad sampler (RE-24), optionally with replacement inner points: the PC Blow FastNormalize, or
    for an Xbox360Estimate curve the unit-normalization centre in every normalization (Shared cc5cda3)."""
    center = getattr(curve, 'squad_policy', None) == 'Xbox360Estimate'
    times = curve.times
    n = len(times)
    key = lambda k: quaternion_part(curve, k, 1)
    incoming = (lambda k: points[0][k]) if points else (lambda k: quaternion_part(curve, k, 0))
    outgoing = (lambda k: points[1][k]) if points else (lambda k: quaternion_part(curve, k, 2))
    if n == 1 or t < times[0]:
        return key(0)
    if t > times[-1]:
        return key(n - 1)
    lower = 1
    upper = n - 1
    while lower < upper:
        middle = lower + (upper - lower) // 2
        if times[middle] < t:
            lower = middle + 1
        else:
            upper = middle
    numerator = F32(t - times[lower - 1])
    duration = F32(times[lower] - times[lower - 1])
    amount = F32(numerator / duration)
    endpoints = fast_normalize_interp(key(lower - 1), key(lower), amount, center)
    controls = fast_normalize_interp(outgoing(lower - 1), incoming(lower), amount, center)
    weight = F32(F32(F32(2) * amount) * F32(F32(1) - amount))
    return fast_normalize_interp(endpoints, controls, weight, center)


# RE-24 inner points (Float32, W, X, Y, Z tuples), used by the TBC exchange control on Squad curves.
def re24_mul(a, b):
    w = ((a[0] * b[0] - a[1] * b[1]) - a[2] * b[2]) - a[3] * b[3]
    x = ((a[0] * b[1] + a[1] * b[0]) + a[2] * b[3]) - a[3] * b[2]
    y = ((a[0] * b[2] + a[2] * b[0]) + a[3] * b[1]) - a[1] * b[3]
    z = ((a[0] * b[3] + a[3] * b[0]) + a[1] * b[2]) - a[2] * b[1]
    return [w, x, y, z]


def re24_conj(q):
    return [q[0], -q[1], -q[2], -q[3]]


def re24_log(q):
    if q[0] <= F32(-1):
        angle = F32(PI_F)
    elif q[0] >= F32(1):
        angle = F32(0)
    else:
        angle = F32(math.acos(float(q[0])))
    s = F32(math.sin(float(angle)))
    factor = angle / s if abs(s) >= F32(EPS_F) else F32(1)
    return [F32(0), factor * q[1], factor * q[2], factor * q[3]]


def re24_exp(v):
    angle = F32(math.sqrt(float((v[1] * v[1] + v[2] * v[2]) + v[3] * v[3])))
    c = F32(math.cos(float(angle)))
    s = F32(math.sin(float(angle)))
    factor = s / angle if abs(s) >= F32(EPS_F) else F32(1)
    return [c, factor * v[1], factor * v[2], factor * v[3]]


def re24_tbc_points(times, keys, parameters, swap_cb):
    """Incoming and outgoing inner points (X, Y, Z, W) from aligned keys (X, Y, Z, W) and per-key (T, C, B)."""
    wxyz = [[k[3], k[0], k[1], k[2]] for k in keys]
    n = len(wxyz)
    incoming = []
    outgoing = []
    for i in range(n):
        p = 0 if i == 0 else i - 1
        x = n - 1 if i == n - 1 else i + 1
        tension, continuity, bias = parameters[i]
        if swap_cb:
            continuity, bias = bias, continuity
        l1 = re24_log(re24_mul(re24_conj(wxyz[p]), wxyz[i]))
        l2 = re24_log(re24_mul(re24_conj(wxyz[i]), wxyz[x]))
        inv = F32(1) / (times[x] - times[p])
        om_t = F32(1) - tension
        om_c = F32(1) - continuity
        op_c = F32(1) + continuity
        om_b = F32(1) - bias
        op_b = F32(1) + bias
        a = (times[i] - times[p]) * inv
        a_t = a * om_t
        c1 = (a_t * op_c) * op_b
        c2 = (a_t * om_c) * om_b
        dd_half = [F32(0.5) * ((c2 * l2[k] + c1 * l1[k]) - l2[k]) for k in range(4)]
        out = re24_mul(wxyz[i], re24_exp(dd_half))
        b = (times[x] - times[i]) * inv
        b_t = b * om_t
        c3 = (b_t * om_c) * op_b
        c4 = (b_t * op_c) * om_b
        ds_half = [F32(0.5) * (l1[k] - (c4 * l2[k] + c3 * l1[k])) for k in range(4)]
        inn = re24_mul(wxyz[i], re24_exp(ds_half))
        incoming.append([inn[1], inn[2], inn[3], inn[0]])
        outgoing.append([out[1], out[2], out[3], out[0]])
    return incoming, outgoing


def euler_quaternion(axes, t, variant=None):
    angles = []
    for axis in axes:
        values, _ = sample_components(axis, t, variant)
        angles.append(float(values[0]))
    half = [a * 0.5 for a in angles]
    qx = (math.sin(half[0]), 0.0, 0.0, math.cos(half[0]))
    qy = (0.0, math.sin(half[1]), 0.0, math.cos(half[1]))
    qz = (0.0, 0.0, math.sin(half[2]), math.cos(half[2]))
    return normalize_double(hamilton(hamilton(qz, qy), qx))


def hamilton(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def rotation_rows(q):
    """System.Numerics Matrix4x4.CreateFromQuaternion's upper 3x3 (row-vector convention), in double."""
    x, y, z, w = (float(c) for c in q)
    xx, yy, zz = x * x, y * y, z * z
    xy, wz, xz, wy, yz, wx = x * y, w * z, x * z, w * y, y * z, w * x
    return [[1 - 2 * (yy + zz), 2 * (xy + wz), 2 * (xz - wy)],
            [2 * (xy - wz), 1 - 2 * (zz + xx), 2 * (yz + wx)],
            [2 * (xz + wy), 2 * (yz - wx), 1 - 2 * (yy + xx)]]


def angle_degrees(expected_rows, observed_rows):
    """The rotation angle between two 3x3 rotations (robust at small angles), in degrees."""
    a = np.array(expected_rows, dtype=np.float64)
    b = np.array(observed_rows, dtype=np.float64)
    relative = a.T @ b
    skew = (relative - relative.T) * 0.5
    sine = math.sqrt(skew[2, 1] ** 2 + skew[0, 2] ** 2 + skew[1, 0] ** 2)
    cosine = (np.trace(relative) - 1.0) * 0.5
    return math.degrees(math.atan2(sine, cosine))


# ------------------------------------------------------------------------------------ gate 1b: the exact dump
# Gate 1b (tools/scripts/gate1a/anim_dump.py) evaluates the exact JSON dump's clips with THIS evaluator rather than a
# second one. The dump prints every Float32 as its shortest round-trip decimal (Shared docs/model-dump-json.md), so
# np.float32(decimal) recovers the stored bits; dump_curve_node rebuilds from those decimals the node form the C#
# harness writes (NifCurveEvalHarness.CurveJson: every float as its bits). The clock functions reproduce
# SceneAnimationClock.Map and ScenePoseEvaluator.ApplyClip's composition (clip clock, then track clock, each result a
# Float32), which the A8 inputs never needed because the harness sampled at already-mapped track-local times.
def bits32(value):
    """The uint bit pattern of a value rounded to Float32 (a dump decimal parses to the exact stored bits)."""
    return struct.unpack('<I', struct.pack('<f', float(F32(value))))[0]


def _bits_list(values):
    return [bits32(v) for v in (values or [])]


def dump_curve_node(curve, width, interpolation=None, value_key='values'):
    """A dump curve as the node ``Curve`` reads: a transform track ({property, times, values, interpolation, state,
    staticValue, spline, tbcParameters, tbcEndpoints}), a SceneCurve ({componentCount, ...} on Euler axes, morph-target
    weights and property tracks) or a morph track (``value_key='weights'``, width = targetCount, always Keyed)."""
    node = {
        'interpolation': interpolation or curve.get('interpolation') or 'Linear',
        'width': int(width),
        'state': curve.get('state') or 'Keyed',
        'times': _bits_list(curve.get('times')),
        'values': _bits_list(curve.get(value_key)),
        'static': _bits_list(curve.get('staticValue')) if curve.get('staticValue') is not None else None,
        'tbc': None, 'startOutgoing': None, 'endIncoming': None,
        'squadPolicy': curve.get('gamebryoSquadPolicy'),
    }
    parameters = curve.get('tbcParameters')
    if parameters is not None:
        node['tbc'] = [bits32(p[k]) for p in parameters for k in ('tension', 'continuity', 'bias')]
    endpoints = curve.get('tbcEndpoints')
    if endpoints is not None:
        node['startOutgoing'] = _bits_list(endpoints.get('startOutgoing'))
        node['endIncoming'] = _bits_list(endpoints.get('endIncoming'))
    spline = curve.get('spline')
    if spline is not None:
        node['spline'] = {
            'degree': spline['degree'], 'count': spline['controlPointCount'],
            'start': bits32(spline['startSeconds']), 'stop': bits32(spline['stopSeconds']),
            'quantized': bool(spline.get('isQuantized')),
            'shorts': [int(s) for s in spline.get('quantizedControlPoints') or []],
            'bias': bits32(spline['bias']) if spline.get('bias') is not None else None,
            'multiplier': bits32(spline['multiplier']) if spline.get('multiplier') is not None else None,
            'floats': _bits_list(spline.get('controlPoints')),
        }
    return node


def clock_map(clock, seconds):
    """SceneAnimationClock.Map (Shared Core/Models/SceneAnimationClock.cs lines 57-77) as a numpy float32.

    ``clock`` is the dump's {frequency, phaseSeconds, startSeconds, stopSeconds, cycle}. Double intermediates, C#'s
    ``%`` (math.fmod: truncated, the dividend's sign) for the positive modulo, Math.Clamp, the final (float) cast and
    MathF.BitDecrement of the excluded Loop stop are reproduced as written."""
    seconds = F32(seconds)
    if not math.isfinite(float(seconds)):
        raise ValueError('a clock input must be finite')
    frequency = float(F32(clock['frequency']))
    phase = float(F32(clock['phaseSeconds']))
    start = F32(clock['startSeconds'])
    stop = F32(clock['stopSeconds'])
    if float(start) == float(stop):
        return start
    local = frequency * float(seconds) + phase
    cycle = clock['cycle']
    if cycle == 'Clamp':
        return F32(min(max(local, float(start)), float(stop)))
    length = float(stop) - float(start)
    if cycle == 'Loop':
        mapped = F32(float(start) + _positive_modulo(local - float(start), length))
        if mapped >= stop:
            return np.nextafter(stop, F32(-np.inf))
        return max(start, mapped)
    if cycle != 'Reverse':
        raise ValueError('unknown clock cycle %r' % cycle)
    offset = _positive_modulo(local - float(start), length * 2)
    reverse = float(start) + offset if offset <= length else float(stop) - (offset - length)
    return F32(min(max(reverse, float(start)), float(stop)))


def _positive_modulo(value, modulus):
    remainder = math.fmod(value, modulus)
    return remainder + modulus if remainder < 0 else remainder


def map_clocks(clip_clock, track_clock, seconds):
    """ScenePoseEvaluator.ApplyClip's composition: the clip clock maps the input first, then the track clock maps the
    clip clock's Float32 result (``track.Clock?.Map(clipSeconds) ?? clipSeconds``); an absent clock is the identity."""
    value = F32(seconds)
    if clip_clock is not None:
        value = clock_map(clip_clock, value)
    if track_clock is not None:
        value = clock_map(track_clock, value)
    return value


def numerics_slerp(first, second, amount):
    """System.Numerics Quaternion.Slerp in Float32 (X, Y, Z, W): the dot, the shortest-path flip, the 1e-6 linear
    fallback and the MathF sine weights, as the .NET reference implementation writes them."""
    epsilon = F32(1e-6)
    t = F32(amount)
    cos_omega = F32(F32(F32(first[0] * second[0]) + F32(first[1] * second[1]) + F32(first[2] * second[2])) +
                    F32(first[3] * second[3]))
    flip = False
    if cos_omega < F32(0):
        flip = True
        cos_omega = F32(-cos_omega)
    if cos_omega > F32(1) - epsilon:
        s1 = F32(1) - t
        s2 = F32(-t) if flip else t
    else:
        omega = F32(math.acos(float(cos_omega)))
        inverse_sine = F32(F32(1) / F32(math.sin(float(omega))))
        s1 = F32(F32(math.sin(float(F32((F32(1) - t) * omega)))) * inverse_sine)
        s2 = F32(F32(math.sin(float(F32(t * omega)))) * inverse_sine)
        if flip:
            s2 = F32(-s2)
    return [F32(first[c] * s1 + second[c] * s2) for c in range(4)]


# ------------------------------------------------------------------------------------------------------ evaluation
VARIANTS = ('bsplineUnclamped', 'tbcContinuityBiasExchanged', 'hermiteForwardBackwardExchanged')


def variant_applies(track, variant):
    curves = [track['curve']] if track['kind'] != 'euler' else track['axes']
    for curve in curves:
        if variant == 'bsplineUnclamped' and curve.spline is not None:
            return True
        if variant == 'hermiteForwardBackwardExchanged' and curve.interpolation == 'Hermite':
            return True
        if variant == 'tbcContinuityBiasExchanged':
            if curve.interpolation == 'Tbc' and curve.tbc and any(p[1] != p[2] for p in curve.tbc):
                return True
            if curve.interpolation == 'GamebryoSquad' and curve.squad_tbc and \
                    any(curve.squad_tbc[i + 1] != curve.squad_tbc[i + 2] for i in range(0, len(curve.squad_tbc), 3)):
                return True
    return False


def squad_control_points(curve):
    """RE-24 inner points with continuity and bias exchanged, from the curve's aligned keys and stored T, C, B."""
    n = len(curve.times)
    if n < 2 or not curve.squad_tbc:
        return None
    parameters = [(f32(curve.squad_tbc[i]), f32(curve.squad_tbc[i + 1]), f32(curve.squad_tbc[i + 2]))
                  for i in range(0, len(curve.squad_tbc), 3)]
    keys = [quaternion_part(curve, k, 1) for k in range(n)]
    return re24_tbc_points(curve.times, keys, parameters, True)


def evaluate(track, sample, variant=None):
    """(ratio, absolute deviation or angle) of one sample; the ratio is deviation / tolerance."""
    kind = track['kind']
    t = f32(sample['t'])
    if kind in ('translation', 'scale'):
        values, magnitude = sample_components(track['curve'], t, variant)
        tolerance = SCALAR_ULPS * ulp32(magnitude)
        observed = sample['observed']
        deviation = max(abs(float(values[c]) - float(observed[c])) for c in range(3))
        return deviation / tolerance, deviation
    if kind == 'rotation':
        curve = track['curve']
        points = squad_control_points(curve) if (variant == 'tbcContinuityBiasExchanged' and
                                                 curve.interpolation == 'GamebryoSquad') else None
        q = sample_rotation(curve, t, variant, points)
    else:
        q = euler_quaternion(track['axes'], t, variant)
    angle = angle_degrees(rotation_rows(q), sample['observedRows'])
    return angle / ROTATION_TOLERANCE_DEGREES, angle


def load_track(node):
    track = {'id': node['id'], 'kind': node['kind']}
    if node['kind'] == 'euler':
        track['axes'] = [Curve(axis) for axis in node['axes']]
    else:
        track['curve'] = Curve(node['curve'])
    track['samples'] = [s for s in node['samples'] if s.get('observed') is not None or
                        s.get('observedRows') is not None]
    return track


def finite(node):
    """The output with every non-finite number replaced by ERROR_RATIO (JSON has no NaN or infinity)."""
    if isinstance(node, dict):
        return {key: finite(value) for key, value in node.items()}
    if isinstance(node, list):
        return [finite(value) for value in node]
    if isinstance(node, float) and not math.isfinite(node):
        return ERROR_RATIO
    if isinstance(node, np.floating):
        value = float(node)
        return value if math.isfinite(value) else ERROR_RATIO
    return node


def main(argv):
    if len(argv) != 3:
        sys.stderr.write(__doc__)
        return 2
    try:
        with open(argv[1], encoding='utf-8') as handle:
            document = json.load(handle)
    except (OSError, ValueError) as failure:
        sys.stderr.write('cannot read the input: %s\n' % failure)
        return 2
    if document.get('schema') != SCHEMA_IN:
        sys.stderr.write('unexpected input schema %r\n' % document.get('schema'))
        return 2

    results = []
    worst = {'ratio': 0.0, 'track': None, 'deviation': 0.0}
    controls = {name: {'applicableTracks': 0, 'worstRatio': 0.0, 'track': None} for name in VARIANTS}
    for node in document['tracks']:
        track = load_track(node)
        entry = {'id': track['id'], 'kind': track['kind'], 'samples': len(track['samples']), 'worstRatio': 0.0,
                 'worstDeviation': 0.0, 'worstAt': None, 'controls': {}}
        try:
            for sample in track['samples']:
                ratio, deviation = evaluate(track, sample)
                if ratio > entry['worstRatio'] or entry['worstAt'] is None:
                    entry['worstRatio'] = ratio
                    entry['worstDeviation'] = deviation
                    entry['worstAt'] = sample['t']
            for variant in VARIANTS:
                if not variant_applies(track, variant):
                    continue
                variant_worst = 0.0
                for sample in track['samples']:
                    ratio, _ = evaluate(track, sample, variant)
                    variant_worst = max(variant_worst, ratio)
                entry['controls'][variant] = variant_worst
                control = controls[variant]
                control['applicableTracks'] += 1
                if variant_worst > control['worstRatio']:
                    control['worstRatio'] = variant_worst
                    control['track'] = track['id']
        except (ValueError, ZeroDivisionError, IndexError, KeyError) as failure:
            entry['error'] = '%s: %s' % (type(failure).__name__, failure)
            entry['worstRatio'] = ERROR_RATIO
        entry['passed'] = 'error' not in entry and entry['worstRatio'] <= 1.0
        if entry['worstRatio'] > worst['ratio']:
            worst = {'ratio': entry['worstRatio'], 'track': entry['id'], 'deviation': entry['worstDeviation']}
        results.append(entry)

    output = {
        'schema': SCHEMA_OUT,
        'python': sys.version.split()[0],
        'numpy': np.__version__,
        'tolerance': {'scalarUlps': SCALAR_ULPS, 'rotationDegrees': ROTATION_TOLERANCE_DEGREES},
        'tracks': results,
        'worst': worst,
        'controls': controls,
        'failures': [entry['id'] for entry in results if not entry['passed']],
    }
    with open(argv[2], 'w', encoding='utf-8') as handle:
        json.dump(finite(output), handle, indent=1, allow_nan=False)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
