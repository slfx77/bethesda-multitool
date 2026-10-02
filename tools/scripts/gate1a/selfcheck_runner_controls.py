# SPDX-License-Identifier: 0BSD
"""Self-check of the driver's gate-level control policy: a control no corpus sample reaches is exercised synthetically,
and only a DETECTED synthetic exercise lets the gate pass.

Runs ``run_gate1a.summarize`` and ``run_gate1a.markdown`` over hand-built sample receipts (no artifacts, no validator)
with the synthetic exercise injected, so the change is proven able to fail. Exit status 1 on any failed expectation.

Cases: the placeholder control never reached (two not-applicable samples) with the exercise forced to detected=false
reads NEVER EXERCISED and allPassed false; forced to detected=true it reads EXERCISED SYNTHETICALLY (never reached by
the corpus: 2 not applicable) and allPassed true, with the exercise recorded under the control entry beside
``neverReachedByCorpus``; forced to detected=true on a set whose own pristine check failed (``pristineChecks`` with a
fail, or ``pristinePassed`` false) it reads NEVER EXERCISED and allPassed false; no exercise available (None) or an
exercise that raises keeps NEVER EXERCISED; a control with a not-detected sample stays NOT DETECTED and is never
exercised synthetically; a control the corpus detected is never exercised synthetically and reads ok; an incomplete
sample keeps allPassed false even when every control is fine; the existing summary fields keep their names; the real
dispatch (``default_synthetic_exercise``) runs hop B's normal placeholder, tangent w-flip, tangent placeholder (pinned
Shared 591d083) and MASK controls on the ``gate1a_synthetic`` set to a detection, returns None for a hop-B control
without a synthetic exercise and for hops C and E, and drives ``summarize`` end to end to allPassed true; the real
dispatch on a DRIFTED ``gate1a_synthetic`` set (prim's stored TANGENT lane 0 wrong) reads NOT detected although the
placeholder verdict itself is a detection, so ``summarize`` reads NEVER EXERCISED and allPassed false; and with the
tangent placeholder and MASK controls never reached by the hand-built receipts, the real dispatch carries both to
EXERCISED SYNTHETICALLY and allPassed true, while a drifted tangent placeholder (stored as the source's zero lane) or a
drifted MASK cutoff leaves that control NEVER EXERCISED and allPassed false; and on a partial sample
``record_refusal_evidence`` names the previous pin's (0fe3212) tangent refusal of unreferenced zero lanes beside the
pinned writer's fill, and which writer's tangent-basis refusal text the manifest quotes, leaving the detail alone when
neither rule predicts a tangent refusal.
"""

import copy
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import gate1a_synthetic  # noqa: E402
import hop_b  # noqa: E402
import run_gate1a as runner  # noqa: E402
from gate1a_common import sha256_bytes  # noqa: E402

FAILURES = []


def expect(condition, message):
    if not condition:
        FAILURES.append(message)
        print('FAIL: ' + message)
    else:
        print('ok:   ' + message)


PLACEHOLDER = hop_b.CONTROL_PLACEHOLDER
TANGENT = hop_b.CONTROL_TANGENT_W
TANGENT_PLACEHOLDER = hop_b.CONTROL_TANGENT_PLACEHOLDER
MASK = hop_b.CONTROL_MASK
FACE = 'one face index flipped in a copy of the package'
UV = 'one UV swapped in a copy of the package only'
KEY_PLACEHOLDER = 'B: ' + PLACEHOLDER
KEY_TANGENT = 'B: ' + TANGENT
KEY_TANGENT_PLACEHOLDER = 'B: ' + TANGENT_PLACEHOLDER
KEY_MASK = 'B: ' + MASK
KEY_FACE = 'C: ' + FACE
KEY_UV = 'E: ' + UV


def hop(name, controls, passed=True, error=None):
    entry = {'hop': name, 'sample': 's', 'passed': passed, 'oracleValid': all(c.get('detected') is not False for c in controls),
             'error': error, 'traceback': None, 'checks': [], 'controls': controls}
    return entry


def control(name, detected, detail='x'):
    return {'name': name, 'detected': detected, 'detail': detail}


def receipts(placeholder_states=(None, None), tangent_states=(True, True), face_states=(True, True), uv_states=(True, True), incomplete=False,
             extra_b=()):
    """Sample receipts in the driver's shape: one sample per position of the state tuples. ``extra_b`` appends hop-B
    controls as (name, states) pairs."""
    out = []
    for i in range(len(placeholder_states)):
        b_controls = [control(TANGENT, tangent_states[i]), control(PLACEHOLDER, placeholder_states[i], 'not applicable: no lane')]
        b_controls += [control(name, states[i], 'not applicable: x') for name, states in extra_b]
        out.append({
            'sample': {'id': 'sample-%d' % i}, 'inputs': {}, 'missingArtifacts': [], 'partial': False, 'complete': not incomplete,
            'hops': {
                'B': hop('B', b_controls),
                'C': hop('C', [control(FACE, face_states[i])]),
                'E': hop('E', [control(UV, uv_states[i])]),
            }})
    return out


def exercise_returning(detected):
    calls = []

    def exercise(hop_name, control_name):
        calls.append((hop_name, control_name))
        return {'detected': detected, 'detail': 'forced %r' % detected, 'expectedMismatchWhere': 'meshes/0/primitives/0 (prim)/X',
                'artifactSha256': {'glb': 'aa', 'dump': 'bb', 'fidelity': 'cc', 'mutatedGlb': 'dd'}}
    exercise.calls = calls
    return exercise


def receipt_for(summary):
    return {'manifest': {'path': 'm', 'sha256': '0', 'samples': summary['samples'], 'subset': False}, 'started': 't0', 'finished': 't1',
            'tools': {}, 'summary': summary, 'samples': []}


def main():
    # 1. Forced NOT detected: the change can fail.
    forced_false = exercise_returning(False)
    summary = runner.summarize(receipts(), forced_false)
    entry = summary['controls'][KEY_PLACEHOLDER]
    expect(summary['allPassed'] is False, 'synthetic exercise forced to detected=false: allPassed false')
    expect(entry['neverReachedByCorpus'] is True and entry['neverExercised'] is True, 'placeholder control: neverReachedByCorpus true, neverExercised true')
    expect(entry['syntheticExercise'] is not None and entry['syntheticExercise']['detected'] is False, 'the exercise is recorded under the control entry')
    expect(runner.control_status(entry) == 'NEVER EXERCISED', 'status NEVER EXERCISED: %s' % runner.control_status(entry))
    md = runner.markdown(receipt_for(summary))
    expect(('| %s | 0 | 0 | 2 | NEVER EXERCISED |' % KEY_PLACEHOLDER) in md and 'Overall: **FAIL**' in md, 'markdown row NEVER EXERCISED and overall FAIL')
    expect('synthetic exercise NOT DETECTED: forced False' in md, 'markdown names the not-detected exercise')
    expect(forced_false.calls == [('B', PLACEHOLDER)], 'the exercise was called exactly once, for the placeholder control: %r' % forced_false.calls)

    # 2. Forced detected: the new status and allPassed true.
    forced_true = exercise_returning(True)
    summary = runner.summarize(receipts(), forced_true)
    entry = summary['controls'][KEY_PLACEHOLDER]
    expect(summary['allPassed'] is True, 'synthetic exercise forced to detected=true: allPassed true')
    expect(entry['neverReachedByCorpus'] is True and entry['neverExercised'] is False, 'placeholder control: neverReachedByCorpus true, neverExercised false')
    expect(entry['syntheticExercise']['detected'] is True and entry['syntheticExercise']['expectedMismatchWhere'] == 'meshes/0/primitives/0 (prim)/X',
           'the detected exercise is recorded under the control entry')
    status = runner.control_status(entry)
    expect(status == 'EXERCISED SYNTHETICALLY (never reached by the corpus: 2 not applicable)', 'status: %s' % status)
    md = runner.markdown(receipt_for(summary))
    expect(('| %s | 0 | 0 | 2 | EXERCISED SYNTHETICALLY (never reached by the corpus: 2 not applicable) |' % KEY_PLACEHOLDER) in md
           and 'Overall: **PASS**' in md, 'markdown row EXERCISED SYNTHETICALLY and overall PASS')
    expect('synthetic exercise detected: forced True; expected mismatch at `meshes/0/primitives/0 (prim)/X`; artifact SHA-256 glb aa, dump bb, fidelity cc, mutatedGlb dd' in md,
           'markdown line carries the detail, the expected stream and the artifact hashes')
    for key in (KEY_TANGENT, KEY_FACE, KEY_UV):
        expect(summary['controls'][key]['neverReachedByCorpus'] is False and summary['controls'][key]['syntheticExercise'] is None
               and runner.control_status(summary['controls'][key]) == 'ok', '%s: reached by the corpus, no exercise, ok' % key)
    expect(forced_true.calls == [('B', PLACEHOLDER)], 'only the never-reached control was exercised: %r' % forced_true.calls)
    expect(all(k in entry for k in ('detected', 'notDetected', 'notApplicable', 'neverExercised')) and entry['detected'] == 0 and entry['notApplicable'] == 2,
           'existing fields keep their names: %r' % sorted(entry))

    # 2b. Forced detected on a set whose own pristine checks FAILED: a detection on a drifted synthetic set does not
    # stand in for a corpus detection (a pristine failure on a corpus sample fails its hop), so NEVER EXERCISED and
    # allPassed false, with the markdown naming the failed pristine check.
    def detected_on_drifted_set(hop_name, control_name):
        ex = exercise_returning(True)(hop_name, control_name)
        ex['pristinePassed'] = False
        ex['pristineChecks'] = [{'name': 'accessors: x', 'status': 'fail', 'detail': None,
                                 'firstMismatch': {'where': 'meshes/0/primitives/0 (prim)/TANGENT', 'kind': 'value', 'detail': 'x'}},
                                {'name': 'fidelity rows: x', 'status': 'pass', 'detail': None, 'firstMismatch': None}]
        return ex
    summary = runner.summarize(receipts(), detected_on_drifted_set)
    entry = summary['controls'][KEY_PLACEHOLDER]
    expect(summary['allPassed'] is False and entry['neverExercised'] is True and entry['syntheticExercise']['detected'] is True
           and runner.control_status(entry) == 'NEVER EXERCISED',
           'forced detected on a set whose pristine check failed: NEVER EXERCISED, allPassed false (status %s)' % runner.control_status(entry))
    md = runner.markdown(receipt_for(summary))
    expect('synthetic exercise NOT DETECTED (reported detected on a set whose own pristine check failed' in md and 'accessors fail, fidelity rows pass' in md
           and ('| %s | 0 | 0 | 2 | NEVER EXERCISED |' % KEY_PLACEHOLDER) in md and 'Overall: **FAIL**' in md,
           'markdown names the failed pristine check, the row reads NEVER EXERCISED and overall FAIL')

    def detected_pristine_flag_only(hop_name, control_name):
        ex = exercise_returning(True)(hop_name, control_name)
        ex['pristinePassed'] = False
        return ex
    summary = runner.summarize(receipts(), detected_pristine_flag_only)
    expect(summary['allPassed'] is False and runner.control_status(summary['controls'][KEY_PLACEHOLDER]) == 'NEVER EXERCISED',
           'pristinePassed false alone (no pristineChecks list) is refused too')

    def detected_pristine_all_pass(hop_name, control_name):
        ex = exercise_returning(True)(hop_name, control_name)
        ex['pristinePassed'] = True
        ex['pristineChecks'] = [{'name': 'accessors: x', 'status': 'pass', 'detail': None, 'firstMismatch': None},
                                {'name': 'fidelity rows: x', 'status': 'pass', 'detail': None, 'firstMismatch': None}]
        return ex
    summary = runner.summarize(receipts(), detected_pristine_all_pass)
    entry = summary['controls'][KEY_PLACEHOLDER]
    md = runner.markdown(receipt_for(summary))
    expect(summary['allPassed'] is True and runner.control_status(entry).startswith('EXERCISED SYNTHETICALLY')
           and 'synthetic exercise detected: forced True' in md and 'pristine checks on the synthetic set: accessors pass, fidelity rows pass' in md,
           'detected with every recorded pristine check passing stands in, and the markdown lists the pristine checks')

    # 3. No exercise available (None) and an exercise that raises: NEVER EXERCISED both times.
    summary = runner.summarize(receipts(), lambda h, n: None)
    entry = summary['controls'][KEY_PLACEHOLDER]
    expect(summary['allPassed'] is False and entry['syntheticExercise'] is None and runner.control_status(entry) == 'NEVER EXERCISED',
           'no synthetic exercise available: NEVER EXERCISED, allPassed false')
    md = runner.markdown(receipt_for(summary))
    expect('never reached by the corpus and no synthetic exercise exists for it: NEVER EXERCISED' in md, 'markdown says no exercise exists')
    summary = runner.summarize(receipts(), None)
    expect(summary['allPassed'] is False and runner.control_status(summary['controls'][KEY_PLACEHOLDER]) == 'NEVER EXERCISED', 'synthetic_exercise=None: NEVER EXERCISED')

    def raising(h, n):
        raise RuntimeError('boom')
    summary = runner.summarize(receipts(), raising)
    entry = summary['controls'][KEY_PLACEHOLDER]
    expect(summary['allPassed'] is False and entry['syntheticExercise']['detected'] is False and 'boom' in entry['syntheticExercise']['detail']
           and runner.control_status(entry) == 'NEVER EXERCISED', 'an exercise that raises reads not detected: %s' % entry['syntheticExercise']['detail'])

    # 4. A not-detected sample stays NOT DETECTED and is never exercised synthetically; a detected control is never exercised.
    recorder = exercise_returning(True)
    summary = runner.summarize(receipts(placeholder_states=(None, False)), recorder)
    entry = summary['controls'][KEY_PLACEHOLDER]
    expect(summary['allPassed'] is False and entry['notDetected'] == 1 and entry['neverReachedByCorpus'] is False and entry['syntheticExercise'] is None
           and runner.control_status(entry) == 'NOT DETECTED' and recorder.calls == [], 'a not-detected sample: NOT DETECTED, no synthetic exercise')
    expect(summary['hops']['B']['samplesInvalid'] == 1, 'the not-detected sample marks its hop invalid')
    recorder = exercise_returning(True)
    summary = runner.summarize(receipts(placeholder_states=(None, True)), recorder)
    entry = summary['controls'][KEY_PLACEHOLDER]
    expect(summary['allPassed'] is True and entry['detected'] == 1 and entry['neverReachedByCorpus'] is False and entry['syntheticExercise'] is None
           and runner.control_status(entry) == 'ok' and recorder.calls == [], 'a detected sample: ok, no synthetic exercise')

    # 5. An incomplete sample keeps the gate failed even with a detected synthetic exercise.
    summary = runner.summarize(receipts(incomplete=True), exercise_returning(True))
    expect(summary['allPassed'] is False and summary['samplesIncomplete'] == 2 and runner.control_status(summary['controls'][KEY_PLACEHOLDER]).startswith('EXERCISED SYNTHETICALLY'),
           'an incomplete sample fails the gate regardless of the synthetic exercise')

    # 6. The real dispatch: hop B's three accessor controls and its MASK control on the gate1a_synthetic set, None elsewhere.
    work = tempfile.mkdtemp(prefix='gate1a-runner-controls-')
    ex = runner.default_synthetic_exercise('B', PLACEHOLDER, work)
    expect(ex is not None and ex['detected'] is True and ex['expectedMismatchWhere'] == 'meshes/0/primitives/0 (prim)/' + hop_b.PLACEHOLDER_STREAM,
           'default dispatch: placeholder control detected on the synthetic set: %s' % (ex and ex['detail']))
    expect(ex is not None and os.path.isfile(ex['files']['mutatedGlb']) and ex['files']['mutatedGlb'].startswith(runner.synthetic_work_dir(work, 'B', PLACEHOLDER)),
           'its artifacts are saved under <out>/synthetic/hop-b/<control>: %s' % (ex and ex['files']['mutatedGlb']))
    ex = runner.default_synthetic_exercise('B', TANGENT, work)
    expect(ex is not None and ex['detected'] is True and ex['expectedMismatchWhere'] == 'meshes/0/primitives/0 (prim)/TANGENT',
           'default dispatch: tangent w-flip control detected on the synthetic set: %s' % (ex and ex['detail']))
    ex = runner.default_synthetic_exercise('B', TANGENT_PLACEHOLDER, work)
    expect(ex is not None and ex['detected'] is True and ex['expectedMismatchWhere'] == 'meshes/0/primitives/0 (prim)/' + hop_b.TANGENT_PLACEHOLDER_STREAM
           and ex['firstMismatch']['where'] == ex['expectedMismatchWhere'] and os.path.isfile(ex['files']['mutatedGlb'])
           and ex['files']['mutatedGlb'].startswith(runner.synthetic_work_dir(work, 'B', TANGENT_PLACEHOLDER)),
           'default dispatch: tangent placeholder control detected on the synthetic set, artifacts under its own directory: %s' % (ex and ex['detail']))
    ex = runner.default_synthetic_exercise('B', MASK, work)
    expect(ex is not None and ex['detected'] is True and ex['expectedMismatchWhere'] == 'materials/0 (mask)/alphaCutoff'
           and ex['firstMismatch']['where'] == ex['expectedMismatchWhere'] and ex['artifactSha256'].get('mutatedGlb'),
           'default dispatch: MASK control detected on the synthetic set, mutated copy hashed: %s' % (ex and ex['detail']))
    expect(runner.default_synthetic_exercise('B', 'swapped BC1 endpoints re-encoded into a copy of the GLB PNG', work) is None, 'default dispatch: a hop-B control without an exercise returns None')
    expect(runner.default_synthetic_exercise('C', FACE, work) is None and runner.default_synthetic_exercise('E', UV, work) is None, 'default dispatch: hops C and E have no synthetic exercise')
    expect(runner.default_synthetic_exercise('Z', 'x', work) is None, 'default dispatch: an unknown hop returns None')

    # 6b. The real dispatch on a DRIFTED synthetic set: gate1a_synthetic's builder is replaced for the duration by one
    # that stores prim's TANGENT lane 0 wrong. The placeholder control's own verdict is still a detection (its stream
    # is compared ahead of TANGENT), yet the exercise reads NOT detected because the set fails its own pristine accessor
    # check, and through summarize the gate reads NEVER EXERCISED and allPassed false. This is the case that, until
    # 2026-09-24, read EXERCISED SYNTHETICALLY and allPassed true.
    real_builder = gate1a_synthetic.write_pristine_set

    def drifted_builder(work_dir):
        a = real_builder(work_dir)
        ta = [list(map(float, lane)) for lane in a['expectedTangents'][0]]
        ta[0] = [0.0, 1.0, 0.0, 1.0]
        glb_bytes = gate1a_synthetic.build_glb([ta, a['expectedTangents'][1]], a['writtenNormals'], a['rows'])
        with open(a['glb'], 'wb') as f:
            f.write(glb_bytes)
        a['glbBytes'] = glb_bytes
        a['sha256']['glb'] = sha256_bytes(glb_bytes)
        return a
    gate1a_synthetic.write_pristine_set = drifted_builder
    try:
        drift_work = tempfile.mkdtemp(prefix='gate1a-runner-controls-drift-')
        ex = runner.default_synthetic_exercise('B', PLACEHOLDER, drift_work)
        expect(ex is not None and ex['verdict'] is True and ex['detected'] is False and ex['pristinePassed'] is False
               and [c['status'] for c in ex['pristineChecks']] == ['fail', 'pass', 'pass'],
               'real dispatch on a drifted set: the placeholder verdict is a detection, the exercise reads NOT detected: %s' % (ex and ex['detail']))
        summary = runner.summarize(receipts(), lambda h, n: runner.default_synthetic_exercise(h, n, drift_work))
        entry = summary['controls'][KEY_PLACEHOLDER]
        expect(summary['allPassed'] is False and entry['neverExercised'] is True and runner.control_status(entry) == 'NEVER EXERCISED',
               'real dispatch on a drifted set through summarize: NEVER EXERCISED, allPassed false (status %s)' % runner.control_status(entry))
        md = runner.markdown(receipt_for(summary))
        expect('synthetic exercise NOT DETECTED' in md and 'accessors fail, fidelity rows pass' in md and 'Overall: **FAIL**' in md,
               'its markdown reads NOT DETECTED with the failed pristine check and overall FAIL')
    finally:
        gate1a_synthetic.write_pristine_set = real_builder
    ex = runner.default_synthetic_exercise('B', PLACEHOLDER, tempfile.mkdtemp(prefix='gate1a-runner-controls-restored-'))
    expect(ex['detected'] is True and ex['pristinePassed'] is True, 'the real builder is restored: detected again on the pristine set')

    # 7. End to end through the default dispatch: the never-reached placeholder control passes the gate synthetically.
    summary = runner.summarize(receipts(), lambda h, n: runner.default_synthetic_exercise(h, n, work))
    entry = summary['controls'][KEY_PLACEHOLDER]
    expect(summary['allPassed'] is True and entry['syntheticExercise']['detected'] is True
           and runner.control_status(entry) == 'EXERCISED SYNTHETICALLY (never reached by the corpus: 2 not applicable)',
           'end to end: allPassed true, status %s' % runner.control_status(entry))
    md = runner.markdown(receipt_for(summary))
    expect('artifact SHA-256 glb %s' % entry['syntheticExercise']['artifactSha256']['glb'] in md and 'Overall: **PASS**' in md, 'end to end markdown carries the artifact hash and PASS')
    # The default summarize (no injected exercise) takes the real dispatch too.
    summary = runner.summarize(copy.deepcopy(receipts()))
    expect(summary['allPassed'] is True and summary['controls'][KEY_PLACEHOLDER]['syntheticExercise']['detected'] is True, 'summarize() with no argument uses the real dispatch')

    # 8. The tangent placeholder control (pinned Shared 591d083) and the MASK control, never reached by the hand-built
    # receipts (two not-applicable samples each): the real dispatch carries both to EXERCISED SYNTHETICALLY, and each
    # is exercised exactly once; forced not detected, NEVER EXERCISED; on a drifted set, NEVER EXERCISED.
    extra = ((TANGENT_PLACEHOLDER, (None, None)), (MASK, (None, None)))
    calls = []

    def recording_dispatch(hop_name, control_name):
        calls.append((hop_name, control_name))
        return runner.default_synthetic_exercise(hop_name, control_name, work)
    summary = runner.summarize(receipts(extra_b=extra), recording_dispatch)
    md = runner.markdown(receipt_for(summary))
    for key in (KEY_TANGENT_PLACEHOLDER, KEY_MASK, KEY_PLACEHOLDER):
        entry = summary['controls'][key]
        expect(entry['neverReachedByCorpus'] is True and entry['syntheticExercise']['detected'] is True
               and runner.control_status(entry) == 'EXERCISED SYNTHETICALLY (never reached by the corpus: 2 not applicable)'
               and ('| %s | 0 | 0 | 2 | EXERCISED SYNTHETICALLY (never reached by the corpus: 2 not applicable) |' % key) in md,
               '%s: EXERCISED SYNTHETICALLY through the real dispatch (%s)' % (key, runner.control_status(entry)))
    expect(summary['allPassed'] is True and 'Overall: **PASS**' in md and sorted(calls) == sorted([('B', PLACEHOLDER), ('B', TANGENT_PLACEHOLDER), ('B', MASK)]),
           'all three never-reached controls exercised once each and the gate passes: %r' % calls)
    expect('pristine checks on the synthetic set: accessors pass, fidelity rows pass, materials pass' in md,
           'the markdown lists the three pristine checks of the synthetic set')

    def forced_false_for(target):
        def dispatch(hop_name, control_name):
            if control_name == target:
                return exercise_returning(False)(hop_name, control_name)
            return runner.default_synthetic_exercise(hop_name, control_name, work)
        return dispatch
    for target, key in ((TANGENT_PLACEHOLDER, KEY_TANGENT_PLACEHOLDER), (MASK, KEY_MASK)):
        summary = runner.summarize(receipts(extra_b=extra), forced_false_for(target))
        expect(summary['allPassed'] is False and runner.control_status(summary['controls'][key]) == 'NEVER EXERCISED',
               '%s forced not detected: NEVER EXERCISED, allPassed false' % key)

    def drifted_placeholder_builder(work_dir):
        a = real_builder(work_dir)
        ta = [list(map(float, lane)) for lane in a['expectedTangents'][0]]
        ta[3] = [0.0, 0.0, 0.0, -1.0]  # the source's zero lane instead of the (1, 0, 0, -1) placeholder
        glb_bytes = gate1a_synthetic.build_glb([ta, a['expectedTangents'][1]], a['writtenNormals'], a['rows'])
        with open(a['glb'], 'wb') as f:
            f.write(glb_bytes)
        a['glbBytes'] = glb_bytes
        a['sha256']['glb'] = sha256_bytes(glb_bytes)
        return a

    def drifted_mask_builder(work_dir):
        a = real_builder(work_dir)
        materials = [dict(gate1a_synthetic.GLB_MATERIALS[0], alphaCutoff=gate1a_synthetic.MASK_RAW / 255.0)]
        glb_bytes = gate1a_synthetic.build_glb(a['expectedTangents'], a['writtenNormals'], a['rows'], materials=materials)
        with open(a['glb'], 'wb') as f:
            f.write(glb_bytes)
        a['glbBytes'] = glb_bytes
        a['sha256']['glb'] = sha256_bytes(glb_bytes)
        return a
    for builder, key, name in ((drifted_placeholder_builder, KEY_TANGENT_PLACEHOLDER, TANGENT_PLACEHOLDER), (drifted_mask_builder, KEY_MASK, MASK)):
        gate1a_synthetic.write_pristine_set = builder
        try:
            drift_work = tempfile.mkdtemp(prefix='gate1a-runner-controls-drift2-')
            ex = runner.default_synthetic_exercise('B', name, drift_work)
            summary = runner.summarize(receipts(extra_b=extra), lambda h, n: runner.default_synthetic_exercise(h, n, drift_work))
            expect(ex['detected'] is False and ex['pristinePassed'] is False and summary['allPassed'] is False
                   and runner.control_status(summary['controls'][key]) == 'NEVER EXERCISED',
                   'real dispatch on a drifted set (%s): the exercise reads NOT detected, NEVER EXERCISED, allPassed false: %s' % (builder.__name__, ex['detail']))
        finally:
            gate1a_synthetic.write_pristine_set = real_builder

    # 9. Refusal evidence on a partial sample (``record_refusal_evidence``). The manifest names no writer pin, so a dump
    # whose only tangent trouble is unreferenced zero lanes must not read as "no tangent refusal" for an older writer's
    # artifacts: the detail names the previous pin's (0fe3212) refusal beside the pinned writer's fill and which writer's
    # refusal text the manifest quotes. Three gate1a_synthetic dumps: prim as built (2 unreferenced zero lanes), prim
    # with lane 0 a REFERENCED zero lane, and plain alone (no zero lane).
    evidence_work = tempfile.mkdtemp(prefix='gate1a-runner-refusal-')
    reasons_0fe3212 = ['convert: Unsupported: x.nif [occurrence: none] | Zero or nonfinite tangent directions or absent handedness cannot produce a strict tangent basis without inventing source data']
    reasons_591d083 = ['convert: Unsupported: x.nif [occurrence: none] | Referenced zero tangent directions, nonfinite directions, or absent handedness cannot produce a strict tangent basis without inventing source data.']
    reasons_blend = ['convert: Unsupported: x.nif [occurrence: none] | The recognized RGB candidate is Blend.']
    prim_referenced = dict(gate1a_synthetic.A, tangents=[[0, 0, 0, 1]] + gate1a_synthetic.A['tangents'][1:])
    dumps = {}
    for name, prims in (('placeholders', None), ('referenced', [('prim', prim_referenced), ('plain', gate1a_synthetic.B)]), ('plain', [('plain', gate1a_synthetic.B)])):
        dumps[name] = gate1a_synthetic.write_json(os.path.join(evidence_work, name + '.json'), gate1a_synthetic.dump_document(prims))
    base = 'no GLB artifact (sample declared partial by the manifest)'

    def evidence_detail(dump_name, reasons):
        entry = runner.hop_not_applicable_entry('B', 's', 'GLB versus dump', base)
        runner.record_refusal_evidence(entry, dumps[dump_name], reasons)
        return entry['checks'][0]['detail'], entry['checks'][0]['data'].get('refusalEvidence')

    detail, evidence = evidence_detail('placeholders', reasons_0fe3212)
    expect(detail == base + '; the dump carries zero-direction tangent lanes no index references on 1 primitive(s), which the previous writer '
           '(Shared 0fe3212) refuses and the pinned writer (Shared 591d083) fills with (1, 0, 0, w); the manifest quotes the Shared 0fe3212 tangent-basis refusal'
           and evidence['refusalPredictedByTangents'] is False and evidence['previousPinRule']['refusalPredictedByTangents'] is True,
           'unreferenced zero lanes, 0fe3212 reason: the detail names the previous pin\'s refusal and the quoted 0fe3212 text: %s' % detail)
    detail, _ = evidence_detail('placeholders', reasons_blend)
    expect(detail.endswith('fills with (1, 0, 0, w); the manifest quotes no tangent-basis refusal'),
           'the same dump refused for its material: the detail says the manifest quotes no tangent-basis refusal: %s' % detail)
    detail, evidence = evidence_detail('referenced', reasons_591d083)
    expect(detail == base + '; the dump carries invalid tangent lanes on 1 primitive(s), which the GLB writer refuses'
           and evidence['invalidTangentPrimitives'][0]['invalidLanes'] == 1 and evidence['manifestQuotesTangentRefusal'] == 'Shared 591d083',
           'a referenced zero lane, 591d083 reason: both rules refuse it, so the detail keeps its pin-neutral text and the evidence records the quoted 591d083 text: %s' % detail)
    detail, evidence = evidence_detail('plain', reasons_blend)
    expect(detail == base and evidence['refusalPredictedByTangents'] is False and evidence['previousPinRule']['refusalPredictedByTangents'] is False,
           'no zero lane: neither rule predicts a tangent refusal and the detail is left alone: %s' % detail)

    if FAILURES:
        print('%d expectation(s) failed' % len(FAILURES))
        return 1
    print('all expectations hold')
    return 0


if __name__ == '__main__':
    sys.exit(main())
