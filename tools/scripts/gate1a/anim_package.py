# SPDX-License-Identifier: 0BSD
"""Gate 1b: an independent reader of the Blender package v4 ``animations`` and an evaluator that plays them the way
the importer builds and Blender evaluates F-curves.

The channel contract is read from the header comment of Shared ``Slfx77.Multitool.Media.Blender/Python/import_model.py``
("Version 4 requires animations[]{sourceIndex,name,durationSeconds,channels,extras,metadata}. Each channel retains
sourceDomain,sourceTrack,node,property,componentOffset,width,interpolation,times,values and optional
segmentInterpolations (one STEP/LINEAR/CUBICSPLINE policy per key interval). Times and values are scalar f32 streams;
cubic values are key-major incoming/value/outgoing triples. Rotation channels may declare rotationCurve:
ComponentPolynomial"; "V4 numeric actions use 32 frames per source second") and from what ``_animation_coordinates`` and
``_animation_fcurve`` do with it (read, never imported or executed):

* frames are the key times times 32 (a power of two, exact in Float32); each key's F-curve point is
  (frame, value part) rounded to Float32;
* a CUBICSPLINE interval (the channel's interpolation, or its ``segmentInterpolations`` entry) gets FREE Bezier handles
  one third of the interval away in frames: right handle of key k at (f_k + d, v_k + d * out_k / 32), left handle of key
  k+1 at (f_{k+1} - d, v_{k+1} - d * in_{k+1} / 32), d = (f_{k+1} - f_k) / 3, both rounded to Float32; a key's
  interpolation mode is the policy of the interval it starts (STEP -> CONSTANT, LINEAR, CUBICSPLINE -> BEZIER); the
  F-curve extrapolates CONSTANT; the importer never calls ``FCurve.update()``, so the handles stay as supplied;
* Blender evaluates a BEZIER interval as the cubic Bezier through (key, right handle, next left handle, next key), the
  parameter solved from the frame (``findzero`` in Blender; bisection plus Newton here), a LINEAR interval by lerp and
  a CONSTANT interval by holding the left key;
* quaternion components (X, Y, Z, W in the package; Blender stores W, X, Y, Z) are evaluated independently and the
  rotation is normalized when Blender builds the matrix, so a LINEAR quaternion interval is a component-linear nlerp and
  a cubic one a normalized component cubic; EulerRotation channels are independent XYZ axes (Blender's XYZ Euler is
  Rz * Ry * Rx, the source's qZ * qY * qX); node visibility is the ``mt_visibility`` property driving
  ``hide = visible < 0.5``.

The importer's admission (``_animation_prepare``) is re-derived where a package could pass the value comparison and
still be refused: strictly increasing times inside [0, duration], finite values, the segment-policy list shape,
binary STEP visibility, quaternion amplitudes inside [1/8, 8], LINEAR quaternion intervals with a nonnegative dot
("certified nonnegative hemisphere"), cubic quaternion channels only as ComponentPolynomial, morph values inside
[-10, 10], and cubic handles whose times collapse or whose nonzero offset vanishes in Float32.
"""

import math

import numpy as np

from gate1a_common import OracleError

F32 = np.float32
FPS = 32.0
MODES = {'Step': 'CONSTANT', 'Linear': 'LINEAR', 'CubicSpline': 'BEZIER'}


class PackageChannel:
    def __init__(self, package, clip_index, clip, channel_index, raw):
        self.clip_index = clip_index
        self.clip_name = clip.get('name')
        self.index = channel_index
        self.raw = raw
        self.domain = raw.get('sourceDomain')
        self.track = raw.get('sourceTrack')
        self.node = raw.get('node')
        self.property = raw.get('property')
        self.offset = int(raw.get('componentOffset') or 0)
        self.width = int(raw.get('width') or 1)
        self.interpolation = raw.get('interpolation')
        self.segments = raw.get('segmentInterpolations')
        self.rotation_curve = raw.get('rotationCurve')
        self.target = raw.get('target')
        self.times = package.stream(raw['times']).reshape(-1).astype(F32)
        values = package.stream(raw['values']).reshape(-1).astype(F32)
        parts = 3 if self.interpolation == 'CubicSpline' else 1
        if values.size != len(self.times) * parts * self.width:
            raise OracleError('package clip %d channel %d: %d values for %d keys x %d parts x width %d'
                              % (clip_index, channel_index, values.size, len(self.times), parts, self.width))
        self.parts = parts
        self.values = values.reshape(len(self.times), parts, self.width)
        self.label = 'animations/%d (%s)/channels/%d (%s %s/%s %s)' % (
            clip_index, self.clip_name, channel_index, self.domain, self.track, self.node, self.property)
        self._curves = None

    def mode(self, key):
        if self.segments is not None and key < len(self.segments):
            return self.segments[key]
        return self.interpolation

    def curves(self):
        if self._curves is None:
            self._curves = [fcurve(self, component) for component in range(self.width)]
        return self._curves

    def sample(self, t):
        """The channel's component vector at time t (seconds), as Blender evaluates its F-curves; a Rotation is
        normalized (X, Y, Z, W)."""
        frame = float(t) * FPS
        values = np.array([evaluate_fcurve(curve, frame) for curve in self.curves()], dtype=np.float64)
        if self.property == 'Rotation':
            norm = math.sqrt(float(np.dot(values, values)))
            if norm == 0 or not math.isfinite(norm):
                raise OracleError('%s: a sampled quaternion has no direction' % self.label)
            values = values / norm
        return values


def fcurve(channel, component):
    """(frames, values, left handles, right handles, modes) of one component as the importer writes it."""
    frames = channel.times.astype(np.float64) * FPS
    cubic = channel.interpolation == 'CubicSpline'
    scalar = channel.values[:, 1 if cubic else 0, component].astype(np.float64)
    points = np.column_stack((frames, scalar)).astype(F32)
    left = right = None
    count = len(frames)
    modes = [MODES[channel.mode(k)] for k in range(count)]
    if cubic:
        active = [k for k in range(count - 1) if channel.mode(k) == 'CubicSpline']
        if active:
            left = points.astype(np.float64)
            right = points.astype(np.float64)
            for k in active:
                delta = (frames[k + 1] - frames[k]) / 3.0
                left[k + 1, 0] -= delta
                right[k, 0] += delta
                left[k + 1, 1] -= delta * (float(channel.values[k + 1, 0, component]) / FPS)
                right[k, 1] += delta * (float(channel.values[k, 2, component]) / FPS)
            with np.errstate(over='ignore', invalid='ignore'):
                left = left.astype(F32)
                right = right.astype(F32)
    return {'points': points, 'left': left, 'right': right, 'modes': modes}


def evaluate_fcurve(curve, frame):
    points = curve['points']
    count = len(points)
    if frame <= float(points[0, 0]):
        return float(points[0, 1])
    if frame >= float(points[-1, 0]):
        return float(points[-1, 1])
    frames = points[:, 0].astype(np.float64)
    k = int(np.searchsorted(frames, frame, side='right')) - 1
    k = min(max(k, 0), count - 2)
    mode = curve['modes'][k]
    x0, y0 = float(points[k, 0]), float(points[k, 1])
    x3, y3 = float(points[k + 1, 0]), float(points[k + 1, 1])
    if mode == 'CONSTANT':
        return y0
    if mode == 'LINEAR':
        return y0 + (y3 - y0) * (frame - x0) / (x3 - x0)
    x1, y1 = float(curve['right'][k, 0]), float(curve['right'][k, 1])
    x2, y2 = float(curve['left'][k + 1, 0]), float(curve['left'][k + 1, 1])
    u = solve_bezier_parameter(x0, x1, x2, x3, frame)
    v = 1.0 - u
    return v * v * v * y0 + 3 * v * v * u * y1 + 3 * v * u * u * y2 + u * u * u * y3


def solve_bezier_parameter(x0, x1, x2, x3, x):
    """The u in [0, 1] with Bx(u) = x (Bx monotonic for handles inside the interval): bisection, then Newton."""
    def bx(u):
        v = 1.0 - u
        return v * v * v * x0 + 3 * v * v * u * x1 + 3 * v * u * u * x2 + u * u * u * x3

    low, high = 0.0, 1.0
    for _ in range(60):
        middle = 0.5 * (low + high)
        if bx(middle) < x:
            low = middle
        else:
            high = middle
    return 0.5 * (low + high)


def admission_problems(channel, duration):
    """The importer refusals (``_animation_prepare`` / ``_animation_coordinates``) this channel would raise."""
    problems = []
    times = channel.times.astype(np.float64)
    if len(times) == 0:
        return ['no keys']
    # The importer tests ``times[0] < 0 or times[-1] > duration`` with ``times`` a float32 array and ``duration`` a Python
    # float: numpy 2 treats the Python float as weak and compares in Float32, so the JSON decimal of a Float32 duration
    # equals its own Float32 time. A double comparison would refuse every clip whose last key is its window.
    stored = channel.times
    if stored[0] < F32(0) or stored[-1] > F32(duration) or np.any(stored[1:] <= stored[:-1]):
        problems.append('times must increase within [0, %r] (got [%r, %r])' % (duration, float(times[0]), float(times[-1])))
    if not np.all(np.isfinite(channel.values)):
        problems.append('nonfinite values')
    if channel.interpolation not in ('Step', 'Linear', 'CubicSpline'):
        problems.append('interpolation %r is not STEP, LINEAR or CUBICSPLINE' % channel.interpolation)
    segments = channel.segments
    if segments is not None:
        if (len(segments) != len(times) - 1 or any(m not in ('Step', 'Linear', 'CubicSpline') for m in segments)
                or (channel.interpolation != 'CubicSpline' and 'CubicSpline' in segments)):
            problems.append('segmentInterpolations do not match the key intervals and tangent layout')
    if channel.property == 'NodeVisibility':
        if channel.interpolation != 'Step' or (segments is not None and any(m != 'Step' for m in segments)):
            problems.append('node visibility is not binary STEP')
        if np.any((channel.values != 0) & (channel.values != 1)):
            problems.append('node visibility values are not exactly 0 or 1')
    if channel.property == 'Rotation':
        if channel.interpolation == 'CubicSpline' and channel.rotation_curve is None:
            problems.append('a cubic quaternion channel without rotationCurve ComponentPolynomial')
        knots = channel.values[:, 1 if channel.parts == 3 else 0, :].astype(np.float64)
        norms = np.sum(knots * knots, axis=1)
        if np.any(norms < 1 / 64) or np.any(norms > 64):
            problems.append('quaternion knots outside the amplitude range [1/8, 8]')
        if channel.rotation_curve is None:
            linear = [channel.mode(k) == 'Linear' for k in range(len(times) - 1)]
            dots = np.sum(knots[:-1] * knots[1:], axis=1)
            bad = [k for k in range(len(times) - 1) if linear[k] and dots[k] < 0]
            if bad:
                problems.append('LINEAR quaternion interval %d has a negative dot (the importer requires the certified '
                                'nonnegative hemisphere)' % bad[0])
    if channel.property == 'MorphWeights' and np.any(np.abs(channel.values[:, 1 if channel.parts == 3 else 0, :]) > 10):
        problems.append('morph values outside [-10, 10]')
    if channel.interpolation == 'CubicSpline':
        for component in range(channel.width):
            curve = fcurve(channel, component)
            if curve['left'] is None:
                continue
            points = curve['points']
            for k in range(len(times) - 1):
                if channel.mode(k) != 'CubicSpline':
                    continue
                if not (points[k, 0] < curve['right'][k, 0] < points[k + 1, 0] and points[k, 0] < curve['left'][k + 1, 0] < points[k + 1, 0]):
                    problems.append('cubic handle times collapse at Float32 precision (component %d, interval %d)' % (component, k))
                    break
            exact_left = channel.values[:, 0, component]
            exact_right = channel.values[:, 2, component]
            if (np.any((exact_left != 0) & (curve['left'][:, 1] == points[:, 1]) & (np.arange(len(times)) > 0)
                       & np.array([k > 0 and channel.mode(k - 1) == 'CubicSpline' for k in range(len(times))]))
                    or np.any((exact_right != 0) & (curve['right'][:, 1] == points[:, 1])
                              & np.array([k < len(times) - 1 and channel.mode(k) == 'CubicSpline' for k in range(len(times))]))):
                problems.append('a nonzero cubic handle disappears at Float32 precision (component %d)' % component)
    return problems


class PackageAnimations:
    def __init__(self, package):
        self.package = package
        manifest = package.manifest
        self.manifest = manifest
        self.clips = []
        for ci, clip in enumerate(manifest.get('animations') or []):
            channels = [PackageChannel(package, ci, clip, i, raw) for i, raw in enumerate(clip.get('channels') or [])]
            self.clips.append({'index': ci, 'name': clip.get('name'), 'sourceIndex': clip.get('sourceIndex'),
                               'duration': clip.get('durationSeconds'), 'channels': channels, 'raw': clip})
        self.rows = manifest.get('fidelity') or []

    def node_rest(self, node_index, prop):
        """The package node's rest value for a TRS property (its ``trs``) or morph defaults."""
        node = self.manifest['nodes'][node_index]
        trs = node.get('trs') or {}
        if prop == 'Translation':
            return np.asarray(trs.get('translation', [0, 0, 0]), dtype=np.float64)
        if prop == 'Rotation':
            return np.asarray(trs.get('rotation', [0, 0, 0, 1]), dtype=np.float64)
        if prop == 'Scale':
            return np.asarray(trs.get('scale', [1, 1, 1]), dtype=np.float64)
        raise OracleError('no rest for %r' % prop)
