# SPDX-License-Identifier: 0BSD
"""Gate 1a driver: runs hops B, C and E and their controls over a produced-artifacts manifest.

Usage:
    python run_gate1a.py <artifacts.json> --out <receipt directory> [--validator <gltf_validator.exe>]
                         [--shared <Multitool.Shared root>] [--limit N] [--filter substring]

The manifest (schema ``gate1a-artifacts/1``) lists one sample per cover file: id, key, role, source, entry,
sha256, platform, game, and the paths glb, package, dump, fidelity (any may be null when the producing
command failed; its exit codes are recorded under ``exitCodes``). The receipt directory receives
``receipt.json`` (every check, every control, tool versions, SHA-256 of every input artifact and of every
oracle script) and ``receipt.md`` (per-hop pass/fail counts, first mismatches, control results). Each sample's
entry is also persisted as ``samples/NNNN_<id>/result.json`` as soon as it is complete, so an interrupted run
leaves the finished samples on disk. The validator is located with the Shared ``gltf_validator_tool.py locate
--json``; not finding it is a hard failure of the run.

Two verdicts per hop and sample: ``passed`` speaks for the artifact (checks only) and ``oracleValid`` for the
oracle (its controls). A control that was not detected marks the hop INVALID, distinct from an artifact
failure; a control that is not applicable on a sample is neutral. A control that no sample reached (detected 0,
not detected 0) is exercised synthetically when its hop module offers ``synthetic_control_exercise`` (hop B's three
accessor controls and its MASK control, on the hand-built set of ``gate1a_synthetic``, through the same mutation,
pristine checks and verdict the corpus run uses); the receipt records that exercise under the control entry
(``syntheticExercise``, ``neverReachedByCorpus``), its artifacts are saved under
``<out>/synthetic/hop-<hop>/<control>/``, and the markdown status reads EXERCISED SYNTHETICALLY. A synthetic exercise stands in for a corpus detection only when it reports
``detected`` AND every pristine check it records on its untouched set passed (``pristineChecks``,
``pristinePassed``): a detection on a set that drifted from ``writer_rules`` proves nothing, exactly as a pristine
failure on a corpus sample fails its hop. A control neither reached by the corpus nor detected synthetically (no
exercise exists, it was not detected, or its set failed its own pristine check) fails the gate ("NEVER EXERCISED"),
so a control that silently never runs cannot masquerade as a pass.
"""

import argparse
import datetime
import glob
import json
import os
import subprocess
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hop_b  # noqa: E402
import hop_c  # noqa: E402
import hop_e  # noqa: E402
from dump_reader import Dump  # noqa: E402
from gate1a_common import HopResult, load_json, sha256_file, tool_versions  # noqa: E402

HOPS = ('B', 'C', 'E')
HOP_MODULES = {'B': hop_b, 'C': hop_c, 'E': hop_e}


def synthetic_work_dir(work_root, hop, control_name):
    safe = ''.join(c if c.isalnum() or c in '-_.' else '_' for c in control_name)[:80]
    return os.path.join(work_root, 'hop-%s' % hop.lower(), safe)


def default_synthetic_exercise(hop, control_name, work_root=None):
    """The hop module's ``synthetic_control_exercise`` for one control: its dict, or None when the hop module has no
    such function or the control has no synthetic exercise. The artifacts go under ``work_root`` when one is given
    (``<out>/synthetic/hop-<hop>/<control>/``), else into a temporary directory."""
    module = HOP_MODULES.get(hop)
    exercise = getattr(module, 'synthetic_control_exercise', None) if module is not None else None
    if exercise is None:
        return None
    work_dir = synthetic_work_dir(work_root, hop, control_name) if work_root else None
    return exercise(control_name, work_dir=work_dir)


def find_shared_root(explicit):
    if explicit:
        return explicit
    candidate = HERE
    for _ in range(8):
        probe = os.path.join(candidate, 'shared', 'Multitool.Shared')
        if os.path.isdir(probe):
            return probe
        candidate = os.path.dirname(candidate)
    return None


def locate_validator(explicit, shared_root):
    """The validator path through the Shared locate tool, or an explicit path. Raises when none is found."""
    if explicit:
        if not os.path.isfile(explicit):
            raise SystemExit('validator not found at %s' % explicit)
        return explicit, {'source': 'explicit'}
    if not shared_root:
        raise SystemExit('the Multitool.Shared root was not found; pass --shared or --validator')
    tool = os.path.join(shared_root, 'tools', 'vendor', 'gltf-validator', 'gltf_validator_tool.py')
    completed = subprocess.run([sys.executable, tool, 'locate', '--json'], capture_output=True, text=True)
    try:
        report = json.loads(completed.stdout)
    except json.JSONDecodeError:
        raise SystemExit('gltf_validator_tool.py locate produced no JSON: %s %s' % (completed.stdout[:300], completed.stderr[:300]))
    if report.get('status') != 'Found' or completed.returncode != 0:
        raise SystemExit('the Khronos validator is not restored on this host (status %r); a missing validator is a hard failure. '
                         'Restore with: python %s restore' % (report.get('status'), tool))
    return report['location']['executablePath'], report['location']


def validator_banner(path):
    completed = subprocess.run([path, '--version'], capture_output=True, text=True)
    banner = (completed.stderr or completed.stdout).splitlines()
    return banner[0] if banner else None


def oracle_identity():
    """SHA-256 of every oracle script in this directory, plus the git HEAD when one is reachable."""
    scripts = {os.path.basename(p): sha256_file(p) for p in sorted(glob.glob(os.path.join(HERE, '*.py')))}
    head = None
    try:
        completed = subprocess.run(['git', 'rev-parse', 'HEAD'], capture_output=True, text=True, cwd=HERE, timeout=30)
        head = completed.stdout.strip() if completed.returncode == 0 and completed.stdout.strip() else None
    except (OSError, subprocess.TimeoutExpired):
        head = None
    return {'scripts': scripts, 'gitHead': head}


def hop_error_entry(hop, sample_id, error, tb=None):
    """The dict shape of HopResult.to_dict for a hop that could not produce a result."""
    return {'hop': hop, 'sample': sample_id, 'passed': False, 'oracleValid': True, 'error': error, 'traceback': tb,
            'checks': [], 'controls': []}


def hop_not_applicable_entry(hop, sample_id, check_name, detail):
    """The dict shape of HopResult.to_dict for a hop whose inputs the manifest declares absent: one not-applicable check."""
    result = HopResult(hop, sample_id)
    result.check(check_name).skip(detail)
    return result.to_dict()


def record_refusal_evidence(entry, dump_path, partial_reasons=None):
    """On a partial sample with a dump, record what the dump says about the missing GLB: the primitives whose tangents
    carry an invalid lane (the writer's ``geometry.tangent-basis-unsupported`` refusal, no GLB written) and those it
    would normalize or fill. Evidence only; a dump that cannot be read leaves the entry not applicable with the reason.

    The manifest names no writer pin, so when the pinned rule (591d083) predicts no tangent refusal but the previous
    pin's rule (0fe3212, every zero direction refused) does, the detail says so rather than reading as "no tangent
    refusal" for an older writer's artifacts, and it names which writer's tangent-basis refusal text the manifest's
    ``partialReasons`` quote (``hop_b.manifest_tangent_refusal``). A refusal both rules predict keeps its detail text;
    the quoted text is in the evidence either way."""
    check = entry['checks'][0]
    try:
        evidence = hop_b.dump_refusal_evidence(Dump(dump_path), partial_reasons)
    except Exception as failure:  # noqa: BLE001 - evidence on a partial sample must never fail the driver
        check['data']['refusalEvidence'] = {'error': '%s: %s' % (type(failure).__name__, failure)}
        return
    check['data']['refusalEvidence'] = evidence
    previous = evidence['previousPinRule']
    if evidence['refusalPredictedByTangents']:
        # Both pins refuse these lanes (a referenced zero, nonfinite or unhanded lane), so the text is pin-neutral and
        # kept as it was; the quoted refusal text is still recorded in the evidence.
        check['detail'] += '; the dump carries invalid tangent lanes on %d primitive(s), which the GLB writer refuses' % len(evidence['invalidTangentPrimitives'])
    elif previous['refusalPredictedByTangents']:
        quoted = evidence['manifestQuotesTangentRefusal']
        check['detail'] += ('; the dump carries zero-direction tangent lanes no index references on %d primitive(s), which the previous writer '
                            '(Shared 0fe3212) refuses and the pinned writer (Shared 591d083) fills with (1, 0, 0, w); the manifest quotes %s'
                            % (len(previous['invalidTangentPrimitives']), 'the %s tangent-basis refusal' % quoted if quoted else 'no tangent-basis refusal'))


def run_hop(hop, sample_id, runner):
    """One hop's to_dict, or the error entry when the hop escaped its own guard (the driver's second guard)."""
    try:
        return runner().to_dict()
    except Exception as failure:  # noqa: BLE001 - never lose the samples already run
        return hop_error_entry(hop, sample_id, 'driver caught %s: %s' % (type(failure).__name__, failure), traceback.format_exc())


def hop_status(hop):
    if hop.get('oracleValid') is False:
        return 'INVALID (control not detected)'
    return 'PASS' if hop['passed'] else 'FAIL'


def control_state(control):
    if control.get('detected') is True:
        return 'detected'
    if control.get('detected') is None:
        return 'not applicable'
    return 'NOT DETECTED'


def control_status(counts):
    """The gate-level status of one control's summary entry, as the markdown prints it."""
    if counts['notDetected']:
        return 'NOT DETECTED'
    if counts.get('neverExercised'):
        return 'NEVER EXERCISED'
    if counts.get('neverReachedByCorpus'):
        return 'EXERCISED SYNTHETICALLY (never reached by the corpus: %d not applicable)' % counts['notApplicable']
    return 'ok'


def synthetic_exercise_detected(exercise):
    """Whether one synthetic exercise dict stands in for a corpus detection: it reports ``detected`` AND every pristine
    check it records on its untouched set passed (``pristinePassed`` is not False and no ``pristineChecks`` status is
    other than ``pass``). A detection on a synthetic set whose own pristine check fails proves nothing about the control
    (the set has drifted from ``writer_rules``), exactly as a pristine failure on a corpus sample fails its hop, so the
    driver refuses it here as well as in the hop module (``hop_b.synthetic_control_exercise`` already reads such an
    exercise as not detected; this guard holds even for a hop module that does not)."""
    if not exercise or exercise.get('detected') is not True:
        return False
    if exercise.get('pristinePassed') is False:
        return False
    return all(c.get('status') == 'pass' for c in (exercise.get('pristineChecks') or []))


def synthetic_exercise_line(counts):
    """One markdown line describing a control's synthetic exercise (or its absence); None when the corpus reached it."""
    if not counts.get('neverReachedByCorpus'):
        return None
    exercise = counts.get('syntheticExercise')
    if not exercise:
        return 'never reached by the corpus and no synthetic exercise exists for it: NEVER EXERCISED'
    hashes = exercise.get('artifactSha256') or {}
    if synthetic_exercise_detected(exercise):
        state = 'detected'
    elif exercise.get('detected') is True:
        state = 'NOT DETECTED (reported detected on a set whose own pristine check failed, which does not stand in)'
    else:
        state = 'NOT DETECTED'
    pristine = exercise.get('pristineChecks') or []
    if pristine:
        pristine_note = '; pristine checks on the synthetic set: %s' % ', '.join(
            '%s %s' % (str(c.get('name', '?')).split(':')[0], c.get('status')) for c in pristine)
    elif exercise.get('pristinePassed') is not None:
        pristine_note = '; pristine checks on the synthetic set: %s' % ('passed' if exercise.get('pristinePassed') else 'FAILED')
    else:
        pristine_note = ''
    return 'synthetic exercise %s: %s; expected mismatch at `%s`; artifact SHA-256 %s%s' % (
        state, exercise.get('detail'), exercise.get('expectedMismatchWhere'),
        ', '.join('%s %s' % (k, v) for k, v in hashes.items()) or 'none', pristine_note)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('manifest')
    parser.add_argument('--out', required=True)
    parser.add_argument('--validator')
    parser.add_argument('--shared')
    parser.add_argument('--limit', type=int)
    parser.add_argument('--filter')
    args = parser.parse_args(argv)

    manifest = load_json(args.manifest)
    all_samples = manifest.get('samples') or []
    samples = all_samples
    if args.filter:
        samples = [s for s in samples if args.filter.lower() in json.dumps(s).lower()]
    if args.limit:
        samples = samples[:args.limit]
    shared_root = find_shared_root(args.shared)
    validator, located = locate_validator(args.validator, shared_root)
    os.makedirs(args.out, exist_ok=True)
    started = datetime.datetime.now(datetime.timezone.utc).isoformat()
    receipts = []
    for index, sample in enumerate(samples):
        sample_id = sample.get('id') or ('%s :: %s' % (sample.get('key'), sample.get('entry')))
        sample['id'] = sample_id
        safe = ''.join(c if c.isalnum() or c in '-_.' else '_' for c in sample_id)[:120]
        work = os.path.join(args.out, 'samples', '%04d_%s' % (index, safe))
        inputs = {}
        for key in ('glb', 'package', 'dump', 'fidelity'):
            path = sample.get(key)
            if path and os.path.isfile(path):
                inputs[key] = {'path': path, 'sha256': sha256_file(path), 'bytes': os.path.getsize(path)}
            else:
                inputs[key] = {'path': path, 'sha256': None, 'missing': True}
                sample[key] = None
        print('[%d/%d] %s' % (index + 1, len(samples), sample_id), flush=True)
        hops = {}
        if sample.get('glb'):
            hops['B'] = run_hop('B', sample_id, lambda: hop_b.run(sample, validator, os.path.join(work, 'hop-b')))
        elif sample.get('partial'):
            # The manifest declares this sample partial (a GLB-writer refusal or a declined input): hop B has no
            # artifact to judge and reads not applicable, as hops C and E do for a missing package or dump. A missing
            # GLB on a sample NOT declared partial is a producing-command failure and stays an error (and fails the
            # gate through samplesIncomplete).
            hops['B'] = hop_not_applicable_entry('B', sample_id, 'GLB versus dump', 'no GLB artifact (sample declared partial by the manifest)')
            if sample.get('dump'):
                record_refusal_evidence(hops['B'], sample['dump'], sample.get('partialReasons'))
        else:
            hops['B'] = hop_error_entry('B', sample_id, 'no GLB artifact')
        hops['C'] = run_hop('C', sample_id, lambda: hop_c.run(sample, os.path.join(work, 'hop-c')))
        hops['E'] = run_hop('E', sample_id, lambda: hop_e.run(sample, os.path.join(work, 'hop-e')))
        for hop in hops.values():
            print('    hop %s: %s%s' % (hop['hop'], hop_status(hop), (' (' + hop['error'] + ')') if hop.get('error') else ''), flush=True)
            for check in hop['checks']:
                if check['status'] == 'fail':
                    print('      x %s: %s' % (check['name'], check['firstMismatch']['detail'] if check['firstMismatch'] else ''), flush=True)
            for control in hop['controls']:
                print('      control %s: %s' % (control['name'], control_state(control)), flush=True)
        missing = [k for k, v in inputs.items() if v.get('missing')]
        entry = {'sample': {k: sample.get(k) for k in ('id', 'key', 'role', 'source', 'entry', 'sha256', 'platform', 'game', 'exitCodes')},
                 'inputs': inputs, 'hops': hops, 'missingArtifacts': missing,
                 'partial': bool(sample.get('partial')),
                 'complete': not missing or bool(sample.get('partial'))}
        receipts.append(entry)
        try:
            os.makedirs(work, exist_ok=True)
            with open(os.path.join(work, 'result.json'), 'w', encoding='utf-8') as f:
                json.dump(entry, f, indent=1, default=str)
        except OSError as failure:
            print('    (result.json not written: %s)' % failure, flush=True)
        if missing:
            print('    missing artifacts: %s%s' % (', '.join(missing), ' (sample marked partial)' if sample.get('partial') else ' (INCOMPLETE: the producing command failed)'), flush=True)
    synthetic_root = os.path.join(args.out, 'synthetic')
    summary = summarize(receipts, lambda hop, name: default_synthetic_exercise(hop, name, synthetic_root))
    for key, counts in summary['controls'].items():
        if counts.get('neverReachedByCorpus'):
            print('control %s: %s' % (key, control_status(counts)), flush=True)
            print('    ' + synthetic_exercise_line(counts), flush=True)
    tools = tool_versions()
    tools['validator'] = {'path': validator, 'banner': None, 'located': located, 'sha256': None}
    try:
        tools['validator']['banner'] = validator_banner(validator)
    except (OSError, subprocess.SubprocessError) as failure:
        tools['validator']['bannerError'] = str(failure)
    try:
        tools['validator']['sha256'] = sha256_file(validator)
    except OSError as failure:
        tools['validator']['sha256Error'] = str(failure)
    try:
        tools['oracle'] = oracle_identity()
    except OSError as failure:
        tools['oracle'] = {'error': str(failure)}
    receipt = {
        'schema': 'gate1a-receipt/1',
        'started': started,
        'finished': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'manifest': {'path': os.path.abspath(args.manifest), 'sha256': sha256_file(args.manifest), 'samples': len(samples),
                     'samplesInManifest': len(all_samples), 'limit': args.limit, 'filter': args.filter,
                     'subset': len(samples) != len(all_samples)},
        'tools': tools,
        'summary': summary,
        'samples': receipts,
    }
    with open(os.path.join(args.out, 'receipt.json'), 'w', encoding='utf-8') as f:
        json.dump(receipt, f, indent=1, default=str)
    with open(os.path.join(args.out, 'receipt.md'), 'w', encoding='utf-8') as f:
        f.write(markdown(receipt))
    print('receipt: %s' % os.path.join(args.out, 'receipt.json'))
    return 0 if summary['allPassed'] else 1


def summarize(receipts, synthetic_exercise=default_synthetic_exercise):
    """The gate summary over the sample receipts. ``synthetic_exercise(hop, control name)`` is called for every control
    the corpus never reached (detected 0 and not detected 0) and returns that hop's synthetic exercise dict or None;
    an exception inside it reads as a not-detected exercise, never as a crash of the run."""
    control_keys = {}
    summary = {'samples': len(receipts), 'hops': {}, 'controls': {}, 'allPassed': True,
               'samplesIncomplete': sum(1 for r in receipts if not r['complete']),
               'samplesPartial': sum(1 for r in receipts if r['partial'])}
    if summary['samplesIncomplete']:
        summary['allPassed'] = False
    for hop in HOPS:
        results = [r['hops'][hop] for r in receipts]
        passed = sum(1 for h in results if h['passed'])
        failed = sum(1 for h in results if not h['passed'])
        invalid = sum(1 for h in results if h.get('oracleValid') is False)
        checks = [c for h in results for c in h['checks']]
        summary['hops'][hop] = {
            'samplesPassed': passed, 'samplesFailed': failed, 'samplesInvalid': invalid,
            'checksPassed': sum(1 for c in checks if c['status'] == 'pass'),
            'checksFailed': sum(1 for c in checks if c['status'] == 'fail'),
            'checksNotApplicable': sum(1 for c in checks if c['status'] == 'not-applicable'),
            'errors': sum(1 for h in results if h.get('error')),
            'exceptions': sum(1 for h in results if h.get('traceback')),
            'bindingsCompared': sum(int((c.get('data') or {}).get('bindingsCompared') or 0) for c in checks),
            'bindingsNotCompared': sum(int((c.get('data') or {}).get('bindingsNotCompared') or 0) for c in checks),
            'colorStreamsNotCompared': sum(len((c.get('data') or {}).get('colorsNotCompared') or []) for c in checks),
        }
        if failed or invalid:
            summary['allPassed'] = False
        for h in results:
            for control in h['controls']:
                entry = summary['controls'].setdefault(hop + ': ' + control['name'], {'detected': 0, 'notDetected': 0, 'notApplicable': 0})
                control_keys[hop + ': ' + control['name']] = (hop, control['name'])
                if control.get('detected') is True:
                    entry['detected'] += 1
                elif control.get('detected') is False:
                    entry['notDetected'] += 1
                    summary['allPassed'] = False
                else:
                    entry['notApplicable'] += 1
    # Gate-level guard: a control that no sample exercised leaves its oracle unproven. A control the corpus never
    # reached (detected 0, not detected 0) is exercised synthetically when its hop offers it, on the same mutation,
    # check and verdict; only a detected synthetic exercise whose set passed its own pristine checks stands in for a
    # corpus detection (synthetic_exercise_detected). Otherwise the control is NEVER EXERCISED and the gate cannot
    # pass on it.
    for key, entry in summary['controls'].items():
        hop, name = control_keys[key]
        entry['neverReachedByCorpus'] = entry['detected'] == 0 and entry['notDetected'] == 0
        entry['syntheticExercise'] = None
        if entry['neverReachedByCorpus'] and synthetic_exercise is not None:
            try:
                entry['syntheticExercise'] = synthetic_exercise(hop, name)
            except Exception as failure:  # noqa: BLE001 - a broken exercise is a not-detected exercise, never a lost run
                entry['syntheticExercise'] = {'detected': False, 'detail': 'synthetic exercise raised %s: %s' % (type(failure).__name__, failure),
                                              'expectedMismatchWhere': None, 'artifactSha256': {}, 'traceback': traceback.format_exc()}
        synthetic_detected = synthetic_exercise_detected(entry['syntheticExercise'])
        entry['neverExercised'] = entry['detected'] == 0 and not synthetic_detected
        if entry['neverExercised']:
            summary['allPassed'] = False
    return summary


def markdown(receipt):
    manifest = receipt['manifest']
    scope = ''
    if manifest.get('subset'):
        scope = ' **SUBSET**: %d of %d manifest samples (limit %r, filter %r).' % (
            manifest['samples'], manifest.get('samplesInManifest'), manifest.get('limit'), manifest.get('filter'))
    lines = ['# Gate 1a receipt (hops B, C, E)', '',
             'Started %s, finished %s. %d samples.%s Manifest `%s` (SHA-256 %s).' % (
                 receipt['started'], receipt['finished'], manifest['samples'], scope, manifest['path'], manifest['sha256']), '',
             '## Tools', '']
    for key, value in receipt['tools'].items():
        lines.append('- %s: %s' % (key, json.dumps(value) if isinstance(value, dict) else value))
    lines += ['', '## Per-hop counts', '',
              '| Hop | Samples passed | Samples failed | Oracle invalid | Checks passed | Checks failed | Checks n/a | Errors | Bindings compared / not compared |',
              '|---|---|---|---|---|---|---|---|---|']
    for hop, counts in receipt['summary']['hops'].items():
        lines.append('| %s | %d | %d | %d | %d | %d | %d | %d | %d / %d |' % (
            hop, counts['samplesPassed'], counts['samplesFailed'], counts['samplesInvalid'], counts['checksPassed'], counts['checksFailed'],
            counts['checksNotApplicable'], counts['errors'], counts['bindingsCompared'], counts['bindingsNotCompared']))
    lines += ['', '## Controls (each must be detected on at least one sample, or exercised synthetically when no sample reaches it; '
              'a not-detected control marks its hop INVALID)', '',
              '| Control | Detected | Not detected | Not applicable | Status |', '|---|---|---|---|---|']
    for name, counts in receipt['summary']['controls'].items():
        lines.append('| %s | %d | %d | %d | %s |' % (name, counts['detected'], counts['notDetected'], counts['notApplicable'], control_status(counts)))
    for name, counts in receipt['summary']['controls'].items():
        line = synthetic_exercise_line(counts)
        if line:
            lines += ['', '- %s: %s' % (name, line)]
    lines += ['', 'Samples with a missing artifact (a producing command failed): %d; samples declared partial by the manifest: %d.'
              % (receipt['summary']['samplesIncomplete'], receipt['summary']['samplesPartial'])]
    lines += ['', 'Overall: **%s**' % ('PASS' if receipt['summary']['allPassed'] else 'FAIL'), '', '## Samples', '']
    for sample in receipt['samples']:
        s = sample['sample']
        lines.append('### %s' % s['id'])
        lines.append('')
        lines.append('- role %s, platform %s, game %s, source %s' % (s.get('role'), s.get('platform'), s.get('game'), s.get('source')))
        if sample['missingArtifacts']:
            lines.append('- missing artifacts: %s%s' % (', '.join(sample['missingArtifacts']), ' (partial sample)' if sample['partial'] else ' (INCOMPLETE)'))
        if s.get('exitCodes'):
            lines.append('- exit codes: %s' % json.dumps(s['exitCodes']))
        for key, value in sample['inputs'].items():
            lines.append('- %s: %s%s' % (key, value.get('path'), (' (SHA-256 %s)' % value['sha256']) if value.get('sha256') else ' (missing)'))
        for hop, data in sample['hops'].items():
            lines.append('- hop %s: %s%s' % (hop, hop_status(data), (' error: ' + data['error']) if data.get('error') else ''))
            for check in data['checks']:
                mark = {'pass': 'ok', 'fail': 'FAIL', 'not-applicable': 'n/a'}[check['status']]
                detail = check.get('detail') or ''
                if check['firstMismatch']:
                    detail = '%s: %s' % (check['firstMismatch']['where'], check['firstMismatch']['detail'])
                data_notes = check.get('data') or {}
                if data_notes.get('bindingsNotCompared'):
                    detail += ' [%d bindings not compared]' % data_notes['bindingsNotCompared']
                if data_notes.get('colorsNotCompared'):
                    detail += ' [%d COLOR_0 streams not compared]' % len(data_notes['colorsNotCompared'])
                lines.append('    - [%s] %s%s' % (mark, check['name'], (': ' + detail) if detail else ''))
            for control in data['controls']:
                lines.append('    - control: %s: %s (%s)' % (control['name'], control_state(control), control.get('detail')))
        lines.append('')
    return '\n'.join(lines) + '\n'


if __name__ == '__main__':
    sys.exit(main())
