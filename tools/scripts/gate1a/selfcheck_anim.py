# SPDX-License-Identifier: 0BSD
"""Gate 1b self-check: hand vectors for every sampler and clock rule the animation hops rely on, the controls on the
hand-built set and on drifted sets, the gate-level control policy, and the 170-degree control both ways.
Exit status 0 when every expectation holds, 1 otherwise (each failure printed). No artifacts are needed.

    python tools/scripts/gate1a/selfcheck_anim.py [--work <directory>]
"""

import argparse
import json
import math
import os
import sys
import tempfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import anim_compare as ac  # noqa: E402
import anim_dump  # noqa: E402,F401  (imported first: it puts tools/scripts on the path for nif_curve_eval)
import anim_glb  # noqa: E402
import anim_package  # noqa: E402
import gate1b_synthetic  # noqa: E402
import hop_b_anim  # noqa: E402
import hop_c_anim  # noqa: E402
import hop_e_anim  # noqa: E402
import nif_curve_eval as nce  # noqa: E402  (anim_dump put tools/scripts on the path)
import run_gate1b  # noqa: E402
from gate1a_common import sha256_file  # noqa: E402
from glb_reader import Glb  # noqa: E402
from package_reader import mutate_package  # noqa: E402

F32 = np.float32
FAILURES = []


def expect(condition, message):
    print('%s %s' % ('ok  ' if condition else 'FAIL', message))
    if not condition:
        FAILURES.append(message)


def close(a, b, tolerance):
    return abs(float(a) - float(b)) <= tolerance


# ------------------------------------------------------------------------------------------------------ clocks
def check_clocks():
    loop = {'frequency': 1, 'phaseSeconds': 0, 'startSeconds': 0, 'stopSeconds': 2, 'cycle': 'Loop'}
    clamp = {'frequency': 1, 'phaseSeconds': 0, 'startSeconds': 0, 'stopSeconds': 2, 'cycle': 'Clamp'}
    reverse = {'frequency': 1, 'phaseSeconds': 0, 'startSeconds': 0, 'stopSeconds': 2, 'cycle': 'Reverse'}
    expect(nce.clock_map(clamp, 3.0) == F32(2) and nce.clock_map(clamp, 1.0) == F32(1), 'Clamp holds at its stop and passes inside')
    expect(nce.clock_map(loop, 2.5) == F32(0.5) and nce.clock_map(loop, 2.0) == F32(0), 'Loop wraps at the excluded stop')
    expect(nce.clock_map(reverse, 2.5) == F32(1.5) and nce.clock_map(reverse, 4.5) == F32(0.5) and nce.clock_map(reverse, 3.0) == F32(1),
           'Reverse turns around at the stop and again at the start')
    hold = dict(clamp, startSeconds=1, stopSeconds=1)
    expect(nce.clock_map(hold, 7.0) == F32(1), 'a zero-length interval returns its start')
    backwards = {'frequency': -1, 'phaseSeconds': 2, 'startSeconds': 0, 'stopSeconds': 2, 'cycle': 'Clamp'}
    expect(nce.clock_map(backwards, 0.5) == F32(1.5), 'a negative frequency runs backwards from its phase')
    negative = {'frequency': 1, 'phaseSeconds': -3, 'startSeconds': 0, 'stopSeconds': 2, 'cycle': 'Loop'}
    expect(nce.clock_map(negative, 0.5) == F32(1.5), 'the positive modulo of a negative offset (C# % is truncated, then +length)')
    # The excluded Loop stop: find an input whose double result rounds to the stop in Float32; Map returns BitDecrement.
    edge = {'frequency': 1, 'phaseSeconds': 0, 'startSeconds': 1, 'stopSeconds': 2, 'cycle': 'Loop'}
    found = None
    for bits in range(0x3FFFFF00, 0x40000000):
        seconds = np.frombuffer(np.uint32(bits).tobytes(), dtype=F32)[0]
        local = float(seconds) * 1.0 + 0.0
        mapped = F32(1.0 + math.fmod(local - 1.0, 1.0))
        if mapped >= F32(2):
            found = seconds
            break
    if found is not None:
        expect(nce.clock_map(edge, found) == np.nextafter(F32(2), F32(-np.inf)),
               'a Loop result that rounds to the excluded stop returns the preceding Float32 (MathF.BitDecrement)')
    else:
        expect(True, 'no Float32 input reaches the excluded stop on [1, 2) (the BitDecrement branch is covered by construction)')
    # Shared docs/scene-animation-clock-lowering.md: the Float32 rounding between nested clocks matters.
    clip = {'frequency': 1, 'phaseSeconds': 16777216, 'startSeconds': 16777216, 'stopSeconds': 16777220, 'cycle': 'Clamp'}
    track = {'frequency': 1, 'phaseSeconds': -16777216, 'startSeconds': 0, 'stopSeconds': 4, 'cycle': 'Clamp'}
    expect(nce.map_clocks(clip, track, 0.5) == F32(0) and nce.map_clocks(clip, track, 1.5) == F32(2),
           'nested clocks round the clip clock output to Float32 (Core gives x=0 at 0.5 and x=2 at 1.5)')
    exact = [min(max(t + 16777216 - 16777216, 0), 4) for t in (0.5, 1.5)]
    expect(exact == [0.5, 1.5], 'control: composing the two clocks in double (no rounding) would give 0.5 and 1.5')


# ----------------------------------------------------------------------------------------------------- samplers
def curve(interpolation, times, values, width=1, state='Keyed'):
    return nce.Curve({'interpolation': interpolation, 'width': width, 'state': state, 'times': [nce.bits32(t) for t in times],
                      'values': [nce.bits32(v) for v in values], 'static': None, 'tbc': None, 'startOutgoing': None, 'endIncoming': None})


def check_source_samplers():
    hermite = curve('Hermite', [0.0, 2.0], [0.0, 0.0, 1.0, 1.0, 1.0, 0.0])
    cubic = curve('CubicSpline', [0.0, 2.0], [0.0, 0.0, 0.5, 0.5, 1.0, 0.0])
    for t in (0.25, 0.5, 1.3):
        h = float(nce.sample_components(hermite, F32(t))[0][0])
        c = float(nce.sample_components(cubic, F32(t))[0][0])
        expect(close(h, c, 1e-7) and close(h, t / 2, 1e-6), 'CUBICSPLINE per-second tangents times the interval equal the normalized Hermite (t=%g: %r, %r)' % (t, h, c))
    q90 = [0.0, 0.0, math.sin(math.pi / 4), math.cos(math.pi / 4)]
    linear = curve('Linear', [0.0, 1.0], [0, 0, 0, 1] + q90, width=4)
    mid = nce.sample_rotation(linear, F32(0.5))
    expect(close(ac.rotation_degrees(mid, [0, 0, math.sin(math.pi / 8), math.cos(math.pi / 8)]), 0, 1e-4),
           'a keyed LINEAR quaternion is Quaternion.Slerp (45 degrees at the midpoint of a 90-degree pair)')
    flipped = curve('Linear', [0.0, 1.0], [0, 0, 0, 1] + [-c for c in q90], width=4)
    expect(close(ac.rotation_degrees(nce.sample_rotation(flipped, F32(0.5)), mid), 0, 1e-4),
           'Quaternion.Slerp takes the shortest path when the second key is negated')
    rng = np.random.default_rng(7)
    worst = 0.0
    for _ in range(200):
        a = rng.normal(size=4)
        b = a + 0.3 * rng.normal(size=4)
        a = a / np.linalg.norm(a)
        b = b / np.linalg.norm(b)
        u = float(rng.uniform())
        numerics = nce.numerics_slerp([F32(v) for v in a], [F32(v) for v in b], F32(u))
        worst = max(worst, ac.rotation_degrees([float(v) for v in numerics], anim_glb.gltf_slerp(a, b, u)))
    expect(worst < 2e-4, 'System.Numerics slerp (Float32) and the glTF slerp (double) agree within 2e-4 degrees (%.3g)' % worst)


class FakeChannel(anim_glb.GlbChannel):
    """A GlbChannel built from arrays (no accessor), for the sampler hand vectors."""

    def __init__(self, times, values, interpolation, path='translation'):
        self.animation_index = 0
        self.animation_name = 'hand'
        self.index = 0
        self.times = np.asarray(times, dtype=F32)
        self.interpolation = interpolation
        self.parts = 3 if interpolation == 'CUBICSPLINE' else 1
        values = np.asarray(values, dtype=np.float64)
        self.width = values.size // (len(times) * self.parts)
        self.values = values.reshape(len(times), self.parts, self.width)
        self.path = path
        self.node = 0
        self.pointer = None
        self.target = ('node', 0, path)
        self.is_rotation = path == 'rotation'
        self.is_boolean = False
        self.label = 'hand %s' % path


def check_glb_samplers():
    # Keys t = 0, 2; values 0, 1; out-tangent of key 0 and in-tangent of key 1 both 0.5 per second: the line v = t / 2.
    channel = FakeChannel([0.0, 2.0], [0.0, 0.0, 0.5, 0.5, 1.0, 0.0], 'CUBICSPLINE')
    expect(close(channel.sample(0.5)[0], 0.25, 1e-12), 'glTF CUBICSPLINE multiplies the tangents by the key interval (0.25 at t=0.5)')
    expect(close(channel.sample(0.5, unscaled=True)[0], 0.203125, 1e-12),
           'control: the interval-blind (SharpGLTF) evaluation gives 0.203125 there')
    step = FakeChannel([0.0, 1.0, 2.0], [0.0, 1.0, 2.0], 'STEP', path='scale')
    expect(step.sample(0.999)[0] == 0.0 and step.sample(1.0)[0] == 1.0 and step.sample(5.0)[0] == 2.0 and step.sample(-1)[0] == 0.0,
           'STEP holds [t_k, t_k+1) and both ends')
    q = [0.0, math.sin(math.radians(45)), 0.0, math.cos(math.radians(45))]
    negated = [-c for c in q]
    rotation = FakeChannel([0.0, 1.0, 2.0], [0, 0, 0, 1] + negated + [0, 0, 0, 1], 'LINEAR', path='rotation')
    expect(close(ac.rotation_degrees(rotation.sample(0.5), [0, math.sin(math.radians(22.5)), 0, math.cos(math.radians(22.5))]), 0, 1e-6),
           'glTF LINEAR rotation is the shortest-path slerp even across a negated key')
    zero = [0.0, 0.0, 0.0, 0.0]
    cubic = FakeChannel([0.0, 1.0], zero + [0, 0, 0, 1] + zero + zero + q + zero, 'CUBICSPLINE', path='rotation')
    flipped = FakeChannel([0.0, 1.0], zero + [0, 0, 0, 1] + zero + zero + negated + zero, 'CUBICSPLINE', path='rotation')
    long_way = ac.rotation_degrees(cubic.sample(0.5), flipped.sample(0.5))
    expect(long_way > 170, 'a negated CUBICSPLINE rotation key turns the normalized cubic the long way (%.1f degrees at the midpoint)' % long_way)


def check_linear_sign_is_free(work):
    """The GLB is judged by the specification alone: negating an interior key of the synthetic set's LINEAR rotation is
    the same animation under shortest-path slerp, so hop B-anim must pass the mutated GLB exactly as it passes the
    original. Control: the same negation on the CUBICSPLINE set is reported at that channel."""
    from dump_reader import Dump
    from gate1a_common import HopResult
    for rotation, reported in (('Linear', False), ('CubicSpline', True)):
        artifacts = gate1b_synthetic.write_pristine_set(os.path.join(work, 'sign-' + rotation), rotation=rotation)
        with open(artifacts['glb'], 'rb') as f:
            glb_bytes = f.read()
        channel = [c for c in anim_glb.GlbAnimations(Glb(glb_bytes)).animations[0]['channels'] if c.is_rotation][0]
        values = channel.values.copy()
        values[1] = -values[1]
        mutated = hop_b_anim.rewrite_output(glb_bytes, channel, values)
        sample = {'id': 'sign-' + rotation, 'glb': artifacts['glb'], 'dump': artifacts['dump'], 'fidelity': artifacts['fidelity']}
        pristine = HopResult('B-anim', sample['id'])
        hop_b_anim.check_animation(pristine, Glb(glb_bytes), Dump(artifacts['dump']), sample, {'dontSample': [], 'sample': sample['id']})
        broken = [c.name for c in pristine.checks if c.passed is False]
        expect(not broken, 'the unmutated %s set passes hop B-anim (failed: %s)' % (rotation, broken))
        expect(channel.interpolation == {'Linear': 'LINEAR', 'CubicSpline': 'CUBICSPLINE'}[rotation],
               'the %s set writes a %s rotation sampler (got %s)' % (rotation, rotation, channel.interpolation))
        result = HopResult('B-anim', sample['id'])
        records = hop_b_anim.check_animation(result, Glb(mutated), Dump(artifacts['dump']), sample,
                                             {'dontSample': [], 'sample': sample['id']})[0]
        hits = [m for c in result.checks for m in c.mismatches if m.where == channel.label]
        record = next((r for r in records if r.label == channel.label), None)
        expect(record is not None and record.compared and record.samples > 0,
               'the mutated %s rotation channel is COMPARED (%s samples), so a pass is a real comparison'
               % (rotation, record.samples if record is not None else 'no record'))
        failed = [c.name for c in result.checks if c.passed is False]
        if reported:
            expect(bool(hits), 'a negated CUBICSPLINE rotation key is reported at %s' % channel.label)
        else:
            expect(not failed and not hits, 'a negated LINEAR rotation key passes hop B-anim (failed: %s)' % failed)


def check_package_evaluation():
    class Raw:
        pass
    channel = Raw()
    channel.interpolation = 'CubicSpline'
    channel.times = np.asarray([0.0, 2.0], dtype=F32)
    channel.values = np.asarray([0.0, 0.0, 0.5, 0.5, 1.0, 0.0], dtype=F32).reshape(2, 3, 1)
    channel.width = 1
    channel.parts = 3
    channel.segments = None
    channel.mode = lambda k: 'CubicSpline'
    fc = anim_package.fcurve(channel, 0)
    expect(close(fc['right'][0, 0], 64 / 3, 1e-5) and close(fc['right'][0, 1], 64 / 3 * 0.5 / 32, 1e-6),
           'the importer puts the right handle one third of the interval away, value + d * tangent / 32')
    expect(close(anim_package.evaluate_fcurve(fc, 0.5 * 32), 0.25, 1e-6), 'a Bezier with handles at one third reproduces the cubic Hermite (0.25)')
    channel.segments = ['Linear']
    channel.mode = lambda k: 'Linear'
    fc = anim_package.fcurve(channel, 0)
    expect(fc['left'] is None and close(anim_package.evaluate_fcurve(fc, 0.5 * 32), 0.25, 1e-9),
           'a LINEAR segment policy removes the handles and lerps')
    channel.values = np.asarray([0.0, 0.0, 2.0, 0.5, 1.0, 0.0], dtype=F32).reshape(2, 3, 1)
    channel.segments = None
    channel.mode = lambda k: 'CubicSpline'
    cubic = anim_package.evaluate_fcurve(anim_package.fcurve(channel, 0), 0.5 * 32)
    channel.segments = ['Linear']
    channel.mode = lambda k: 'Linear'
    linear = anim_package.evaluate_fcurve(anim_package.fcurve(channel, 0), 0.5 * 32)
    expect(abs(cubic - linear) > 0.1, 'control: flipping a bending cubic span to LINEAR moves it (%.4f against %.4f)' % (cubic, linear))


def check_compare_rules():
    record = ac.ChannelRecord('hand step', ('node', 0, 'scale'))
    step_at = lambda t: np.array([0.0 if t < 1.0 else 1.0])
    source_at = lambda t: (np.array([0.0 if t < float(np.nextafter(F32(1.0), F32(2))) else 1.0]), 1.0)
    ok = ac.compare_series(record, [0.5, 1.0, 1.5], step_at, source_at, 'components', ac.Bound('ratio'))
    expect(ok and record.samples == 2 and sum(record.skipped.values()) == 1,
           'a jump placed one Float32 step apart on the two sides is skipped, the samples around it compared (%r)' % record.skipped)
    record = ac.ChannelRecord('hand late jump', ('node', 0, 'scale'))
    late = lambda t: (np.array([0.0 if t < 1.2 else 1.0]), 1.0)
    ok = ac.compare_series(record, [0.5, 1.0, 1.1, 1.5], step_at, late, 'components', ac.Bound('ratio'))
    expect(not ok and record.failed_at == float(F32(1.1)), 'control: a jump the writer misplaces by 0.2 s is reported at t=1.1 (as a Float32 time)')
    small = [0.0, 0.0, math.sin(math.radians(5e-7)), math.cos(math.radians(5e-7))]
    expect(close(ac.rotation_degrees([0, 0, 0, 1], small), 1e-6, 1e-12), 'the rotation angle is accurate at 1e-6 degrees')


# ------------------------------------------------------------------------------------------------ controls
def drift_dump(artifacts):
    with open(artifacts['dump'], encoding='utf-8') as f:
        envelope = json.load(f)
    envelope['document']['animations'][0]['propertyTracks'][1]['curve']['values'][1] = 0.6
    with open(artifacts['dump'], 'w', encoding='utf-8') as f:
        json.dump(envelope, f)
    artifacts['sha256']['dump'] = sha256_file(artifacts['dump'])
    return artifacts


def drift_package(artifacts):
    source = artifacts['package']
    target = source + '.drift.zip'

    def mutate(name, data):
        if name == 'streams/animation_0000_channel_0005.values.bin':
            return np.asarray([1.0, 0.6], dtype='<f4').tobytes()
        return data
    mutate_package(source, target, mutate)
    os.replace(target, source)
    artifacts['sha256']['package'] = sha256_file(source)
    return artifacts


def check_controls(work):
    for module in (hop_b_anim, hop_c_anim, hop_e_anim):
        for name in module.CONTROL_ORDER:
            exercise = module.synthetic_control_exercise(name, os.path.join(work, 'pristine', module.__name__, str(abs(hash(name)) % 10**8)))
            expect(exercise['detected'] is True and exercise['pristinePassed'],
                   '%s synthetic exercise detects "%s": %s' % (module.__name__, name, exercise['detail'][:160]))
    drifts = ((hop_b_anim, drift_dump, 'the dump alpha drifts, so the GLB pointer channel disagrees'),
              (hop_c_anim, drift_dump, 'the dump alpha drifts, so the package alpha channel disagrees'),
              (hop_e_anim, drift_package, 'the package alpha drifts, so the two writers disagree'))
    for module, drift, why in drifts:
        name = module.CONTROL_ORDER[0]
        builder = lambda directory, drift=drift: drift(gate1b_synthetic.write_pristine_set(directory))
        exercise = module.synthetic_control_exercise(name, os.path.join(work, 'drift', module.__name__), builder=builder)
        expect(exercise['detected'] is False and exercise['pristinePassed'] is False,
               '%s: a drifted set reads NOT detected even when its verdict is %r (%s)' % (module.__name__, exercise['verdict'], why))


def check_declarations(work):
    from dump_reader import Dump
    from gate1a_common import HopResult
    from package_reader import Package
    artifacts = gate1b_synthetic.write_pristine_set(os.path.join(work, 'declarations'))

    def edit(manifest):
        manifest['animations'][0]['channels'][5]['target']['index'] = 1
    mutated = artifacts['package'] + '.target.zip'
    hop_c_anim.rewrite_manifest(artifacts['package'], mutated, edit)
    result = HopResult('selfcheck', 'declarations')
    package = Package(mutated)
    try:
        hop_c_anim.check_package(result, package, Dump(artifacts['dump']), 'declarations', {})
    finally:
        package.close()
    hits = [m for c in result.checks for m in c.mismatches if m.kind == 'declaration']
    expect(len(hits) == 1 and 'MaterialAlpha' in hits[0].where,
           'a package channel whose target names another material is a declaration mismatch (%s)' % (hits[0].where if hits else None))


def check_gate_policy():
    def receipt(detected):
        hop = {'hop': 'B-anim', 'passed': True, 'oracleValid': detected is not False, 'error': None, 'checks': [],
               'controls': [{'name': hop_b_anim.CONTROL_KEY_SHIFT, 'detected': detected}]}
        empty = {'hop': 'x', 'passed': True, 'oracleValid': True, 'error': None,
                 'checks': [{'name': 'n/a', 'status': 'not-applicable', 'detail': 'none', 'data': {}}], 'controls': []}
        cover = {'channelsCompared': 0, 'channelsNotCompared': [], 'samplesCompared': 0, 'samplesSkipped': {}}
        return [{'hops': {'B-anim': hop, 'C-anim': dict(empty, hop='C-anim'), 'E-anim': dict(empty, hop='E-anim')},
                 'coverage': {'B-anim': cover, 'C-anim': cover, 'E-anim': cover, 'dump': None}, 'partial': False, 'complete': True}]
    good = {'detected': True, 'pristinePassed': True, 'pristineChecks': [{'status': 'pass'}]}
    bad = {'detected': False}
    gate = {'170': {'detected': True}}
    summary = run_gate1b.summarize(receipt(None), lambda hop, name: good, gate)
    expect(summary['allPassed'] and all(not e['neverExercised'] for e in summary['controls'].values()),
           'every control never reached but detected synthetically: the gate passes')
    summary = run_gate1b.summarize(receipt(None), lambda hop, name: bad if name == hop_b_anim.CONTROL_KEY_SHIFT else good, gate)
    expect(not summary['allPassed'] and summary['controls']['B-anim: ' + hop_b_anim.CONTROL_KEY_SHIFT]['neverExercised'],
           'a never-reached control whose synthetic exercise is not detected: NEVER EXERCISED, the gate fails')
    summary = run_gate1b.summarize(receipt(False), lambda hop, name: good, gate)
    expect(not summary['allPassed'], 'a control a corpus sample did not detect fails the gate')
    summary = run_gate1b.summarize(receipt(True), lambda hop, name: good, {'170': {'detected': False}})
    expect(not summary['allPassed'], 'a 170-degree control that is not detected fails the gate')


def check_170(work):
    control = gate1b_synthetic.run_170_degree_control(os.path.join(work, '170'))
    measured = control['measured']
    expect(control['detected'] and control['reproducesSlice9'],
           '170-degree control detected and reproducing slice 9 (slerp vs nlerp %.6f, GLB miss %.6f, package miss %.6f)'
           % (measured['slerpVersusNlerpDegrees'], measured['twoKeyGlbSlerpMissDegrees'], measured['twoKeyPackageNlerpMissDegrees']))
    half = math.radians(10.0)
    small = gate1b_synthetic.slice9_pair()[0], [float(F32(math.sin(half) / math.sqrt(14) * c)) for c in (1, 2, 3)] + [float(F32(math.cos(half)))]
    control = gate1b_synthetic.run_170_degree_control(os.path.join(work, '20'), pair=small)
    expect(control['detected'] is False, 'control: the same fixture at 20 degrees reads NOT detected (slerp vs nlerp %.4f degrees)'
           % control['measured']['slerpVersusNlerpDegrees'])


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--work')
    args = parser.parse_args(argv)
    work = args.work or tempfile.mkdtemp(prefix='gate1b-selfcheck-')
    check_clocks()
    check_source_samplers()
    check_glb_samplers()
    check_package_evaluation()
    check_compare_rules()
    check_linear_sign_is_free(work)
    check_controls(work)
    check_declarations(work)
    check_gate_policy()
    check_170(work)
    print('\n%d failure(s); work directory %s' % (len(FAILURES), work))
    return 1 if FAILURES else 0


if __name__ == '__main__':
    sys.exit(main())
