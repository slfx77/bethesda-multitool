# SPDX-License-Identifier: 0BSD
"""Gate 1b comparison rules shared by hops B-anim, C-anim and E-anim: bounds, sample times, discontinuity skips,
deviation measures and the per-channel coverage records the receipt prints.

**Bounds.** A channel is judged against the bound its writer states. The GLB writer's rows (``mesh fidelity --format glb
--json``, the same rows as the GLB's ``multitoolFidelity`` carrier) state per source track
``animation/<domain>/<track>/clock/value`` (Shared docs/scene-animation-clock-lowering.md, "Fidelity rows": the channel
value certificate, INCLUDING the observed prior conversion error of Hermite/TBC tangents, B-splines, Gamebryo
counter-warp, Squad and Euler), falling back to the track's conversion row (``/spline``, ``/interpolation``,
``/rotation``) when it has no clock row, and ``animation/morph-target-nodes/<node>/packing`` for packed weights. The
package states per component ``animation/<domain>/<track>/<component>/clock-retiming`` (``boundsUnit`` ratio: a
component error over ``max(1, lowered source key magnitude)``, or degrees for a normalized quaternion) and
``animation/transform-tracks/<track>/rotation`` / ``.../spline-conversion`` in degrees. Units: ``meters`` (a translation
difference, Euclidean, after the source-unit factor), ``degrees`` (the angle between the two normalized rotations),
``ratio``/``weight``/``component`` (the largest absolute component difference; the GLB rows state their denominator 1).

**Certificate and design limit.** Each bound carries the row's ``observedError`` (the writer's certificate) and its
``maximumError`` (the design limit). A sample passes when its deviation is within the certificate plus the evaluation
slack below; the ratio to the design limit is recorded beside it. Neither certificate covers Float32 key-value storage
or the two-ulp output-key placement ("Values are rounded once to Float32; the clock certificate is not a separate
certificate for key-value storage"), so the slack adds 4 Float32 ulps of the value magnitude (scalars; the same unit
nif_curve_eval uses for its A8 tolerance), 2e-4 degrees (rotations, nif_curve_eval's rotation tolerance) and the
channel's local speed times 2 ulps of the sample time.

**Sample times.** Every key time of the channel under test, the quarter points of every key interval and 33 evenly
spaced times over the clip's playback window, all inside ``[0, window]``; the list is capped (every key interval keeps
its midpoint) so one dense channel cannot dominate the run.

**Discontinuities.** A sample is skipped, and counted with its reason, when either side jumps within +-4 Float32 steps
of it: a Loop wrap is lowered as two keys one Float32 step apart and a STEP key moves the value at an instant whose
output placement may differ by two ulps, so a comparison exactly there would measure placement, not value. The test is
the value itself, evaluated at ``t - 4 ulp`` and ``t + 4 ulp`` on each side; a jump larger than the channel bound plus
slack (and than that side's OWN local speed over the step) skips the sample. Everywhere else a discontinuity the writer failed to emit is a mismatch.

**DON'T-SAMPLE list.** ``dont_sample.json`` beside this module lists (sample substring, clip, channel substring, time
range, reason) entries a reviewer accepted; every applied entry is recorded with the samples it removed. It ships
empty: no produced artifact needed one.
"""

import json
import math
import os

import numpy as np

F32 = np.float32
EVALUATION_ULPS = 4
ROTATION_SLACK_DEGREES = 2e-4
PLACEMENT_ULPS = 2
JUMP_ULPS = 4
UNIFORM_SAMPLES = 33
MAX_SAMPLES_PER_CHANNEL = 1200
DONT_SAMPLE_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'dont_sample.json')


def ulp32(value):
    return float(np.spacing(F32(abs(float(value)))))


def rotation_degrees(a, b):
    """The rotation angle between two quaternions (either sign), robust at small angles: 4 atan2(|a - b|, |a + b|) on
    the unit quaternions aligned to one hemisphere."""
    a = np.asarray(a, dtype=np.float64)
    b = np.asarray(b, dtype=np.float64)
    na = math.sqrt(float(np.dot(a, a)))
    nb = math.sqrt(float(np.dot(b, b)))
    if na == 0 or nb == 0 or not math.isfinite(na) or not math.isfinite(nb):
        return float('inf')
    a = a / na
    b = b / nb
    if float(np.dot(a, b)) < 0:
        b = -b
    return math.degrees(4 * math.atan2(math.sqrt(float(np.dot(a - b, a - b))), math.sqrt(float(np.dot(a + b, a + b)))))


class Bound:
    """One channel's stated bound: unit, certificate (observed), design limit (maximum) and the rows it came from."""

    def __init__(self, unit, observed=0.0, maximum=0.0, rows=None, note=None):
        self.unit = unit
        self.observed = float(observed or 0.0)
        self.maximum = float(maximum or 0.0)
        self.rows = list(rows or [])
        self.note = note

    def plus(self, other):
        if other is None:
            return self
        return Bound(self.unit, self.observed + other.observed, self.maximum + other.maximum, self.rows + other.rows,
                     '; '.join(n for n in (self.note, other.note) if n) or None)

    def to_dict(self):
        return {'unit': self.unit, 'observed': self.observed, 'maximum': self.maximum, 'rows': self.rows[:8],
                'note': self.note}


def value_kind(key):
    """How a pose key is measured: 'translation', 'rotation', 'boolean' or 'components'."""
    if key[0] == 'node' and key[2] in ('translation', 'rotation'):
        return key[2]
    if key[0] == 'visibility':
        return 'boolean'
    return 'components'


def deviation(kind, actual, expected, factor=1.0):
    """The measured difference in the bound's unit: meters (translation, Euclidean, times the source-unit factor),
    degrees (rotation), 0/1 (a visibility disagreement) or the largest absolute component difference."""
    actual = np.asarray(actual, dtype=np.float64)
    expected = np.asarray(expected, dtype=np.float64)
    if actual.shape != expected.shape:
        return float('inf')
    if kind == 'rotation':
        return rotation_degrees(actual, expected)
    if kind == 'boolean':
        return 0.0 if (float(actual[0]) != 0) == (float(expected[0]) >= 0.5) else 1.0
    if actual.size == 0:
        return 0.0
    difference = np.abs(actual - expected)
    if kind == 'translation':
        return float(math.sqrt(float(np.dot(actual - expected, actual - expected)))) * factor
    return float(np.max(difference))


def slack(kind, magnitude, speed, t, factor=1.0):
    """The evaluation and storage allowance the certificate does not cover (see the module text)."""
    placement = abs(speed) * PLACEMENT_ULPS * ulp32(t)
    if kind == 'rotation':
        return ROTATION_SLACK_DEGREES + placement
    if kind == 'boolean':
        return 0.0
    scalar = EVALUATION_ULPS * ulp32(max(abs(magnitude), 1e-30))
    if kind == 'translation':
        return (math.sqrt(3) * scalar + placement) * factor
    return scalar + placement


def sample_times(key_times, window, cap=MAX_SAMPLES_PER_CHANNEL):
    """Key times, quarter points of every interval and a uniform grid over [0, window], inside [0, window]."""
    times = set()
    keys = [float(t) for t in key_times]
    for t in keys:
        times.add(t)
    for k in range(len(keys) - 1):
        t0, t1 = keys[k], keys[k + 1]
        if t1 - t0 <= 8 * ulp32(t0 if t0 else t1):
            continue
        for fraction in (0.25, 0.5, 0.75):
            times.add(t0 + (t1 - t0) * fraction)
    if window is not None and window > 0:
        for i in range(UNIFORM_SAMPLES):
            times.add(window * i / (UNIFORM_SAMPLES - 1))
    elif window == 0:
        times.add(0.0)
    upper = window if window is not None else (max(keys) if keys else 0.0)
    chosen = sorted(float(F32(t)) for t in times if 0.0 <= t <= upper)
    chosen = sorted(set(chosen))
    if len(chosen) > cap:
        stride = len(chosen) / cap
        chosen = sorted(set(chosen[int(i * stride)] for i in range(cap)) | {chosen[-1]})
    return chosen


def jump_skip(evaluate, kind, t, tolerance, factor=1.0):
    """True when ``evaluate`` (time -> value) changes by more than ``tolerance`` between t - 4 ulp and t + 4 ulp."""
    step = JUMP_ULPS * ulp32(t if t else 1e-30)
    before = evaluate(max(0.0, t - step))
    after = evaluate(t + step)
    return deviation(kind, before, after, factor) > tolerance


def local_speed(evaluate, kind, t, factor=1.0):
    """|dv/dt| near t: the SMALLER of the two one-sided differences over h = max(256 ulp(t), 1e-6 s). A jump at or
    beside t inflates only the side that contains it, so it can never raise its own skip threshold (a central
    difference across a Loop wrap would read a speed of 1/(2h) and hide the wrap)."""
    h = max(256 * ulp32(t if t else 1e-30), 1e-6)
    here = evaluate(t)
    right = deviation(kind, here, evaluate(t + h), factor) / h
    if t - h < 0:
        return right
    left = deviation(kind, evaluate(t - h), here, factor) / h
    return min(left, right)


def load_dont_sample(path=DONT_SAMPLE_PATH):
    if not os.path.isfile(path):
        return []
    with open(path, encoding='utf-8') as f:
        document = json.load(f)
    return list(document.get('entries') or [])


def dont_sample_hit(entries, sample_id, clip_name, channel_label, t):
    """The first DON'T-SAMPLE entry covering this sample time, or None."""
    for entry in entries:
        if entry.get('sample') and entry['sample'] not in sample_id:
            continue
        if entry.get('clip') is not None and entry['clip'] != clip_name:
            continue
        if entry.get('channel') and entry['channel'] not in channel_label:
            continue
        low, high = entry.get('from', -math.inf), entry.get('to', math.inf)
        if low <= t <= high:
            return entry
    return None


class ChannelRecord:
    """The receipt's view of one compared (or not compared) channel."""

    def __init__(self, label, target, interpolation=None, keys=None):
        self.label = label
        self.target = target
        self.interpolation = interpolation
        self.keys = keys
        self.samples = 0
        self.skipped = {}
        self.worst = 0.0
        self.worst_at = None
        self.worst_ratio = 0.0
        self.worst_design_ratio = 0.0
        self.bound = None
        self.compared = False
        self.reason = None
        self.failed_at = None
        self.expected = None
        self.actual = None

    def skip(self, reason):
        self.skipped[reason] = self.skipped.get(reason, 0) + 1

    def observe(self, t, measured, allowance, design_allowance, expected=None, actual=None):
        self.samples += 1
        ratio = measured / allowance if allowance > 0 else (0.0 if measured == 0 else float('inf'))
        design = measured / design_allowance if design_allowance > 0 else (0.0 if measured == 0 else float('inf'))
        if ratio > self.worst_ratio or self.worst_at is None:
            self.worst_ratio = ratio
            self.worst = measured
            self.worst_at = t
            self.expected = expected
            self.actual = actual
        self.worst_design_ratio = max(self.worst_design_ratio, design)
        if ratio > 1 and self.failed_at is None:
            self.failed_at = t
        return ratio <= 1

    def to_dict(self):
        def finite(x):
            return x if x is None or math.isfinite(x) else 1e300
        record = {'label': self.label, 'target': list(self.target) if isinstance(self.target, tuple) else self.target,
                  'interpolation': self.interpolation, 'keys': self.keys, 'compared': self.compared,
                  'samples': self.samples, 'skipped': self.skipped, 'worstDeviation': finite(self.worst),
                  'worstAt': self.worst_at, 'worstRatioToCertificate': finite(self.worst_ratio),
                  'worstRatioToDesignLimit': finite(self.worst_design_ratio),
                  'bound': self.bound.to_dict() if self.bound else None}
        if self.reason:
            record['reason'] = self.reason
        if self.failed_at is not None:
            record['firstFailureAt'] = self.failed_at
            record['expectedAtWorst'] = None if self.expected is None else [float(v) for v in np.asarray(self.expected).reshape(-1)[:16]]
            record['actualAtWorst'] = None if self.actual is None else [float(v) for v in np.asarray(self.actual).reshape(-1)[:16]]
        return record


def rows_by_clip(rows):
    """{source clip index: {featureId: row}} over the animation rows of one writer's fidelity report."""
    table = {}
    for row in rows or []:
        target = row.get('target') or {}
        if target.get('kind') != 'Animation':
            continue
        table.setdefault(target.get('index'), {})[row.get('featureId')] = row
    return table


def row_bounds(row):
    """(unit, observed, maximum) of a GLB row (``bounds``) or a package row (flattened ``boundsUnit`` fields)."""
    if row is None:
        return None
    bounds = row.get('bounds')
    if bounds is not None:
        return bounds.get('unit'), bounds.get('observedError') or 0.0, bounds.get('maximumError') or 0.0
    if row.get('maximumError') is not None:
        return row.get('boundsUnit'), row.get('observedError') or 0.0, row.get('maximumError') or 0.0
    return None


def compare_series(record, times, actual_at, expected_at, kind, bound, factor=1.0, context=None):
    """Compare two time functions at ``times`` and fill ``record``. ``actual_at(t)`` returns the writer's value;
    ``expected_at(t)`` returns (value, magnitude). Returns True when every compared sample is within the certificate.

    ``context`` = {'dontSample': entries, 'sample': id, 'clip': name} applies the DON'T-SAMPLE list. A sample is skipped
    (and counted) when either side jumps within +-4 Float32 steps (see the module text). The speed used for the
    placement slack and the jump threshold is the writer side's smaller one-sided difference (``local_speed``)."""
    context = context or {}
    entries = context.get('dontSample') or []
    ok = True
    record.bound = bound
    for t in times:
        t32 = float(F32(t))
        hit = dont_sample_hit(entries, context.get('sample') or '', context.get('clip'), record.label, t32)
        if hit is not None:
            record.skip('dont-sample: %s' % hit.get('reason'))
            continue
        actual = actual_at(t32)
        expected, magnitude = expected_at(t32)
        speed = local_speed(actual_at, kind, t32, factor)
        allowance_slack = slack(kind, max(magnitude, float(np.max(np.abs(np.asarray(expected, dtype=np.float64)))) if np.size(expected) else magnitude),
                                speed, t32, factor)
        allowance = bound.observed + allowance_slack
        design = max(bound.maximum, bound.observed) + allowance_slack
        jump_threshold = max(design, 100 * speed * 2 * JUMP_ULPS * ulp32(t32 if t32 else 1e-30))
        if kind == 'boolean':
            jump_threshold = 0.5
        if jump_skip(actual_at, kind, t32, jump_threshold, factor):
            record.skip('writer discontinuity within 4 Float32 steps')
            continue
        # The source side's jump test uses the SOURCE's own speed: with the writer's speed, a wrong flat writer (a cubic span
        # written LINEAR, a missing key) would make the source's ordinary slope over +-4 Float32 steps read as a jump, and
        # the very samples that must fail would be skipped (found by the C-anim segment-flip control on plasmaplate).
        source_speed = local_speed(lambda s: expected_at(s)[0], kind, t32, factor)
        source_threshold = max(design, 100 * source_speed * 2 * JUMP_ULPS * ulp32(t32 if t32 else 1e-30))
        if kind == 'boolean':
            source_threshold = 0.5
        if jump_skip(lambda s: expected_at(s)[0], kind, t32, source_threshold, factor):
            record.skip('source discontinuity within 4 Float32 steps')
            continue
        measured = deviation(kind, actual, expected, factor)
        if not record.observe(t32, measured, allowance, design, expected, actual):
            ok = False
    record.compared = record.samples > 0
    if not record.compared and record.reason is None:
        record.reason = 'every sample time was skipped (%s)' % ', '.join('%s x%d' % kv for kv in record.skipped.items())
    return ok


def mismatch_detail(record):
    """The text of a channel's first failure for the Mismatch record."""
    bound = record.bound
    return ('deviation %.6g %s at t=%r (%.3g x the certificate %.6g + slack; design limit %.6g, ratio %.3g); expected %s, got %s'
            % (record.worst, bound.unit if bound else '', record.worst_at, record.worst_ratio,
               bound.observed if bound else 0.0, bound.maximum if bound else 0.0, record.worst_design_ratio,
               None if record.expected is None else [round(float(v), 7) for v in np.asarray(record.expected).reshape(-1)[:6]],
               None if record.actual is None else [round(float(v), 7) for v in np.asarray(record.actual).reshape(-1)[:6]]))


DECLARING_FEATURES = ('clip', 'animation/channels', 'animation/clock')
DECLARING_OUTCOMES = ('Dropped', 'Degraded', 'Metadata')


def declared_drop(clip_rows):
    """The writer's own statement for a source clip it did not emit as animation: the first row on ``clip`` (package) or
    ``animation/channels`` / ``animation/clock`` (GLB) whose outcome is Dropped, Degraded or Metadata, as
    'featureId outcome reasonCode: description', or None when the writer states nothing (a silent drop)."""
    for feature in DECLARING_FEATURES:
        row = (clip_rows or {}).get(feature)
        if row is not None and row.get('outcome') in DECLARING_OUTCOMES:
            return '%s %s %s: %s' % (feature, row.get('outcome'), row.get('reasonCode'), (row.get('description') or '')[:240])
    return None


def driven_track_count(clip):
    """The driven tracks of one dump clip: every transform track that is not NotDriven, and every Euler, morph,
    morph-target, property and matrix track."""
    driven = sum(1 for t in clip.get('transformTracks') or [] if t.get('state') != 'NotDriven')
    for key in ('eulerRotationTracks', 'morphTracks', 'morphTargetTracks', 'propertyTracks', 'matrixTracks'):
        driven += len(clip.get(key) or [])
    return driven


def dump_clip_census(document):
    """(clips, driven tracks) of a dump document."""
    clips = document.get('animations') or []
    return len(clips), sum(driven_track_count(clip) for clip in clips)


CLOCK_FIELDS = ('frequency', 'phaseSeconds', 'startSeconds', 'stopSeconds', 'cycle')


def same_clock(a, b):
    """Two dump/metadata clocks are the same declaration: both absent, or every field equal (numbers as Float32)."""
    if a is None or b is None:
        return a is None and b is None
    for field in CLOCK_FIELDS:
        x, y = a.get(field), b.get(field)
        if field == 'cycle':
            if x != y:
                return False
        elif x is None or y is None or float(F32(x)) != float(F32(y)):
            return False
    return True


def same_events(metadata_events, dump_events):
    """Ordered (Float32 time, exact text) equality of two event lists."""
    left = [(float(F32(e.get('timeSeconds'))), e.get('text')) for e in metadata_events or []]
    right = [(float(F32(e.get('timeSeconds'))), e.get('text')) for e in dump_events or []]
    return left == right
