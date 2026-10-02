# SPDX-License-Identifier: 0BSD
"""Gate 1b driver: runs hops B-anim, C-anim and E-anim and their controls over a produced-artifacts manifest.

Usage:
    python run_gate1b.py <artifacts.json> --out <receipt directory> [--limit N] [--filter substring]

The manifest is the one gate 1a reads (schema ``gate1a-artifacts/1``: per sample glb, package, dump, fidelity, partial
and partialReasons); gate 1b needs no new producing command, because ``mesh convert``, ``mesh package`` and ``mesh
dump`` already carry the animation. The receipt directory receives ``receipt.json``, ``receipt.md`` and one
``samples/NNNN_<id>/result.json`` per sample as soon as it completes.

Why a separate driver rather than new hops in run_gate1a.py: gate 1a's receipt schema, its HOPS tuple and its control
policy are pinned by ``selfcheck_runner_controls.py`` and by the receipts of the gate runs already taken; the animation
hops need the GLB fidelity rows as their bounds (hop B needs only the validator), pair clips rather than primitives,
and report per-channel coverage the static receipt has no place for. Running them beside gate 1a keeps every static
receipt byte-comparable, and this driver reuses gate 1a's gate-level control policy functions unchanged
(``run_gate1a.synthetic_exercise_detected``, ``control_status``, ``synthetic_exercise_line``, ``oracle_identity``).

Verdicts per hop and sample are gate 1a's: ``passed`` (the checks) and ``oracleValid`` (the controls). A sample whose
GLB or package carries no animation reads not applicable for that hop, with the reason (the dump's clip count and, on
a partial sample, the manifest's ``partialReasons``) in the per-sample coverage. A control no sample reached is
exercised on ``gate1b_synthetic``'s set through the hop's ``synthetic_control_exercise`` (the same mutation, checks
and verdict), and stands in only when the synthetic set passes its own pristine checks; otherwise it is NEVER
EXERCISED and fails the gate. The 170-degree control is gate-level and always runs on its fixture
(``gate1b_synthetic.run_170_degree_control``); it fails the gate unless detected.
"""

import argparse
import datetime
import json
import os
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import anim_compare  # noqa: E402
import gate1b_synthetic  # noqa: E402
import hop_b_anim  # noqa: E402
import hop_c_anim  # noqa: E402
import hop_e_anim  # noqa: E402
import run_gate1a  # noqa: E402
from gate1a_common import HopResult, load_json, sha256_file, tool_versions  # noqa: E402

HOPS = ('B-anim', 'C-anim', 'E-anim')
HOP_MODULES = {'B-anim': hop_b_anim, 'C-anim': hop_c_anim, 'E-anim': hop_e_anim}
CONTROL_170 = '170-degree pair: two keys without insertion (GLB slerp, package nlerp) against the counter-warped nlerp'


def default_synthetic_exercise(hop, control_name, work_root=None):
    module = HOP_MODULES.get(hop)
    exercise = getattr(module, 'synthetic_control_exercise', None) if module is not None else None
    if exercise is None:
        return None
    work_dir = run_gate1a.synthetic_work_dir(work_root, hop, control_name) if work_root else None
    return exercise(control_name, work_dir=work_dir)


def not_applicable(hop, sample_id, detail):
    result = HopResult(hop, sample_id)
    result.check('%s inputs' % hop).skip(detail)
    return result.to_dict()


def hop_state(hop_entry):
    """gate 1a's hop status, except that a hop whose checks are all not applicable (and which has no error) reads
    'not applicable' rather than PASS."""
    checks = hop_entry.get('checks') or []
    if not hop_entry.get('error') and checks and all(c['status'] == 'not-applicable' for c in checks):
        return 'not applicable'
    return run_gate1a.hop_status(hop_entry)


def coverage(hop_entry):
    """The per-sample channel coverage of one hop: clips, channels compared and not compared (with reasons), samples."""
    out = {'status': hop_state(hop_entry) if hop_entry.get('checks') or hop_entry.get('error') else 'not run', 'clips': None,
           'channelsCompared': 0, 'channelsNotCompared': [], 'samplesCompared': 0, 'samplesSkipped': {}}
    for check in hop_entry.get('checks') or []:
        data = check.get('data') or {}
        if 'clips' in data and isinstance(data['clips'], list):
            out['clips'] = len(data['clips'])
            if data.get('clipsNotEmitted'):
                out['clipsNotEmitted'] = data['clipsNotEmitted']
            if data.get('clipsNotPackaged'):
                out['clipsNotPackaged'] = data['clipsNotPackaged']
        for record in data.get('channels') or []:
            if record.get('compared'):
                out['channelsCompared'] += 1
                out['samplesCompared'] += record.get('samples') or 0
                for reason, count in (record.get('skipped') or {}).items():
                    out['samplesSkipped'][reason] = out['samplesSkipped'].get(reason, 0) + count
            else:
                out['channelsNotCompared'].append({'channel': record.get('label'), 'reason': record.get('reason')})
        if check.get('status') == 'not-applicable' and not out['channelsCompared']:
            out['reason'] = check.get('detail')
    return out


def dump_census(path):
    """{'clips', 'drivenTracks'} of a dump, or None without one (read here only for the per-sample coverage)."""
    if not path:
        return None
    try:
        document = load_json(path).get('document') or {}
    except (OSError, ValueError) as failure:
        return {'error': str(failure)}
    clips, driven = anim_compare.dump_clip_census(document)
    return {'clips': clips, 'drivenTracks': driven}


def run_sample(sample, work):
    """The three hops over one sample. A missing GLB or package on a sample the manifest declares partial reads not
    applicable with the manifest's ``partialReasons`` (the writer's refusal); on any other sample it is an error."""
    sample_id = sample['id']
    hops = {}
    partial = bool(sample.get('partial'))
    reasons = '; '.join(str(r) for r in (sample.get('partialReasons') or []))[:600]
    if sample.get('glb'):
        hops['B-anim'] = run_gate1a.run_hop('B-anim', sample_id, lambda: hop_b_anim.run(sample, os.path.join(work, 'hop-b-anim')))
    elif partial:
        hops['B-anim'] = not_applicable('B-anim', sample_id, 'no GLB artifact (sample declared partial: %s)' % reasons)
    else:
        hops['B-anim'] = run_gate1a.hop_error_entry('B-anim', sample_id, 'no GLB artifact')
    if sample.get('package') or not partial:
        hops['C-anim'] = run_gate1a.run_hop('C-anim', sample_id, lambda: hop_c_anim.run(sample, os.path.join(work, 'hop-c-anim')))
    else:
        hops['C-anim'] = not_applicable('C-anim', sample_id, 'no package artifact (sample declared partial: %s)' % reasons)
    if (sample.get('glb') and sample.get('package')) or not partial:
        hops['E-anim'] = run_gate1a.run_hop('E-anim', sample_id, lambda: hop_e_anim.run(sample, os.path.join(work, 'hop-e-anim')))
    else:
        hops['E-anim'] = not_applicable('E-anim', sample_id, 'the GLB or the package is absent (sample declared partial: %s)' % reasons)
    return hops


def summarize(receipts, synthetic_exercise=default_synthetic_exercise, gate_controls=None):
    """The gate summary, with gate 1a's control policy (``run_gate1a.synthetic_exercise_detected``) and the gate-level
    controls (the 170-degree control) that must each be detected."""
    summary = {'samples': len(receipts), 'hops': {}, 'controls': {}, 'gateControls': {}, 'allPassed': True,
               'samplesIncomplete': sum(1 for r in receipts if not r['complete']),
               'samplesPartial': sum(1 for r in receipts if r['partial']),
               'animatedDumps': sum(1 for r in receipts if ((r['coverage'].get('dump') or {}).get('clips') or 0) > 0),
               'dumpClips': sum(((r['coverage'].get('dump') or {}).get('clips') or 0) for r in receipts),
               'dumpDrivenTracks': sum(((r['coverage'].get('dump') or {}).get('drivenTracks') or 0) for r in receipts)}
    if summary['samplesIncomplete']:
        summary['allPassed'] = False
    keys = {}
    for hop in HOPS:
        results = [r['hops'][hop] for r in receipts]
        applicable = [h for h in results if not (len(h['checks']) == 1 and h['checks'][0]['status'] == 'not-applicable') or h.get('error')]
        covers = [r['coverage'][hop] for r in receipts]
        summary['hops'][hop] = {
            'samplesApplicable': len(applicable),
            'samplesPassed': sum(1 for h in applicable if h['passed']),
            'samplesFailed': sum(1 for h in applicable if not h['passed']),
            'samplesInvalid': sum(1 for h in applicable if h.get('oracleValid') is False),
            'samplesNotApplicable': len(results) - len(applicable),
            'errors': sum(1 for h in results if h.get('error')),
            'channelsCompared': sum(c['channelsCompared'] for c in covers),
            'channelsNotCompared': sum(len(c['channelsNotCompared']) for c in covers),
            'samplesCompared': sum(c['samplesCompared'] for c in covers),
            'samplesSkipped': sum(sum(c['samplesSkipped'].values()) for c in covers),
            'worstRatioToCertificate': max([((c.get('data') or {}).get('worstRatioToCertificate') or 0.0)
                                            for h in results for c in h['checks']] or [0.0]),
        }
        if summary['hops'][hop]['samplesFailed'] or summary['hops'][hop]['samplesInvalid']:
            summary['allPassed'] = False
        for h in results:
            for control in h['controls']:
                key = hop + ': ' + control['name']
                entry = summary['controls'].setdefault(key, {'detected': 0, 'notDetected': 0, 'notApplicable': 0})
                keys[key] = (hop, control['name'])
                if control.get('detected') is True:
                    entry['detected'] += 1
                elif control.get('detected') is False:
                    entry['notDetected'] += 1
                    summary['allPassed'] = False
                else:
                    entry['notApplicable'] += 1
    # A hop that no sample reached still owes its controls: list every control the hop module declares.
    for hop, module in HOP_MODULES.items():
        for name in getattr(module, 'CONTROL_ORDER', ()):
            key = hop + ': ' + name
            if key not in summary['controls']:
                summary['controls'][key] = {'detected': 0, 'notDetected': 0, 'notApplicable': 0}
                keys[key] = (hop, name)
    for key, entry in summary['controls'].items():
        hop, name = keys[key]
        entry['neverReachedByCorpus'] = entry['detected'] == 0 and entry['notDetected'] == 0
        entry['syntheticExercise'] = None
        if entry['neverReachedByCorpus'] and synthetic_exercise is not None:
            try:
                entry['syntheticExercise'] = synthetic_exercise(hop, name)
            except Exception as failure:  # noqa: BLE001 - a broken exercise is a not-detected exercise
                entry['syntheticExercise'] = {'detected': False, 'detail': 'synthetic exercise raised %s: %s' % (type(failure).__name__, failure),
                                              'expectedMismatchWhere': None, 'artifactSha256': {}, 'traceback': traceback.format_exc()}
        entry['neverExercised'] = entry['detected'] == 0 and not run_gate1a.synthetic_exercise_detected(entry['syntheticExercise'])
        if entry['neverExercised']:
            summary['allPassed'] = False
    for name, control in (gate_controls or {}).items():
        summary['gateControls'][name] = control
        if control.get('detected') is not True:
            summary['allPassed'] = False
    return summary


def markdown(receipt):
    manifest = receipt['manifest']
    scope = ''
    if manifest.get('subset'):
        scope = ' **SUBSET**: %d of %d manifest samples (limit %r, filter %r).' % (
            manifest['samples'], manifest.get('samplesInManifest'), manifest.get('limit'), manifest.get('filter'))
    lines = ['# Gate 1b receipt (hops B-anim, C-anim, E-anim)', '',
             'Started %s, finished %s. %d samples.%s Manifest `%s` (SHA-256 %s).' % (
                 receipt['started'], receipt['finished'], manifest['samples'], scope, manifest['path'], manifest['sha256']), '',
             '## Tools', '']
    for key, value in receipt['tools'].items():
        lines.append('- %s: %s' % (key, json.dumps(value) if isinstance(value, dict) else value))
    lines += ['', '## Per-hop animation counts', '',
              '| Hop | Samples applicable | Passed | Failed | Oracle invalid | Not applicable | Errors | Channels compared | Channels not compared | Samples compared | Samples skipped | Worst x certificate |',
              '|---|---|---|---|---|---|---|---|---|---|---|---|']
    for hop, c in receipt['summary']['hops'].items():
        lines.append('| %s | %d | %d | %d | %d | %d | %d | %d | %d | %d | %d | %.3g |' % (
            hop, c['samplesApplicable'], c['samplesPassed'], c['samplesFailed'], c['samplesInvalid'], c['samplesNotApplicable'],
            c['errors'], c['channelsCompared'], c['channelsNotCompared'], c['samplesCompared'], c['samplesSkipped'],
            c['worstRatioToCertificate']))
    lines += ['', '## Controls (each must be detected on at least one sample, or exercised synthetically when no sample reaches it)', '',
              '| Control | Detected | Not detected | Not applicable | Status |', '|---|---|---|---|---|']
    for name, counts in receipt['summary']['controls'].items():
        lines.append('| %s | %d | %d | %d | %s |' % (name, counts['detected'], counts['notDetected'], counts['notApplicable'],
                                                    run_gate1a.control_status(counts)))
    for name, counts in receipt['summary']['controls'].items():
        line = run_gate1a.synthetic_exercise_line(counts)
        if line:
            lines += ['', '- %s: %s' % (name, line)]
    for name, control in receipt['summary']['gateControls'].items():
        lines += ['', '- gate control %s: **%s**. %s' % (name, 'detected' if control.get('detected') else 'NOT DETECTED', control.get('detail'))]
    lines += ['', 'Dumps with clips: %d (%d clips, %d driven tracks). Samples declared partial by the manifest: %d; incomplete: %d.' % (
                  receipt['summary']['animatedDumps'], receipt['summary']['dumpClips'], receipt['summary']['dumpDrivenTracks'],
                  receipt['summary']['samplesPartial'], receipt['summary']['samplesIncomplete']),
              '', 'Overall: **%s**' % ('PASS' if receipt['summary']['allPassed'] else 'FAIL'), '', '## Samples (channel coverage)', '']
    for sample in receipt['samples']:
        s = sample['sample']
        census = sample['coverage'].get('dump') or {}
        compared = any(sample['coverage'][hop]['channelsCompared'] for hop in HOPS)
        failed = any(sample['hops'][hop].get('error') or not sample['hops'][hop]['passed'] for hop in HOPS)
        if not census.get('clips') and not compared and not failed:
            continue  # no animation anywhere: listed in receipt.json only
        lines.append('### %s' % s['id'])
        lines.append('')
        lines.append('- dump: %s clips, %s driven tracks' % (census.get('clips'), census.get('drivenTracks')))
        for hop in HOPS:
            data = sample['hops'][hop]
            cover = sample['coverage'][hop]
            lines.append('- %s: %s%s; %d channels compared (%d samples, skipped %s), %d not compared%s' % (
                hop, hop_state(data), (' error: ' + data['error']) if data.get('error') else '',
                cover['channelsCompared'], cover['samplesCompared'], cover['samplesSkipped'] or 0, len(cover['channelsNotCompared']),
                (' (%s)' % cover['reason']) if cover.get('reason') else ''))
            for item in cover['channelsNotCompared'][:12]:
                lines.append('    - not compared: %s: %s' % (item['channel'], item['reason']))
            for check in data['checks']:
                if check['status'] == 'fail':
                    lines.append('    - [FAIL] %s: %s: %s' % (check['name'], (check['firstMismatch'] or {}).get('where'),
                                                            (check['firstMismatch'] or {}).get('detail')))
            for control in data['controls']:
                lines.append('    - control: %s: %s (%s)' % (control['name'], run_gate1a.control_state(control), control.get('detail')))
        lines.append('')
    return '\n'.join(lines) + '\n'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('manifest')
    parser.add_argument('--out', required=True)
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
        hops = run_sample(sample, work)
        cover = {hop: coverage(hops[hop]) for hop in HOPS}
        cover['dump'] = dump_census(sample.get('dump'))
        for hop in HOPS:
            data = hops[hop]
            print('    %s: %s%s' % (hop, hop_state(data), (' (' + data['error'] + ')') if data.get('error') else ''), flush=True)
            for check in data['checks']:
                if check['status'] == 'fail':
                    print('      x %s: %s' % (check['name'], (check['firstMismatch'] or {}).get('detail', '')[:300]), flush=True)
            for control in data['controls']:
                print('      control %s: %s' % (control['name'], run_gate1a.control_state(control)), flush=True)
        missing = [k for k, v in inputs.items() if v.get('missing')]
        entry = {'sample': {k: sample.get(k) for k in ('id', 'key', 'role', 'source', 'entry', 'sha256', 'platform', 'game', 'partialReasons')},
                 'inputs': inputs, 'hops': hops, 'coverage': cover, 'missingArtifacts': missing,
                 'partial': bool(sample.get('partial')), 'complete': not missing or bool(sample.get('partial'))}
        receipts.append(entry)
        try:
            os.makedirs(work, exist_ok=True)
            with open(os.path.join(work, 'result.json'), 'w', encoding='utf-8') as f:
                json.dump(entry, f, indent=1, default=str)
        except OSError as failure:
            print('    (result.json not written: %s)' % failure, flush=True)
    synthetic_root = os.path.join(args.out, 'synthetic')
    try:
        control_170 = gate1b_synthetic.run_170_degree_control(os.path.join(synthetic_root, 'gate-170-degrees'))
    except Exception as failure:  # noqa: BLE001 - a broken fixture reads as not detected
        control_170 = {'detected': False, 'detail': 'the 170-degree fixture raised %s: %s' % (type(failure).__name__, failure),
                       'traceback': traceback.format_exc()}
    summary = summarize(receipts, lambda hop, name: default_synthetic_exercise(hop, name, synthetic_root), {CONTROL_170: control_170})
    for key, counts in summary['controls'].items():
        if counts.get('neverReachedByCorpus'):
            print('control %s: %s' % (key, run_gate1a.control_status(counts)), flush=True)
            print('    ' + run_gate1a.synthetic_exercise_line(counts), flush=True)
    print('gate control %s: %s' % (CONTROL_170, 'detected' if control_170.get('detected') else 'NOT DETECTED'), flush=True)
    tools = tool_versions()
    try:
        tools['oracle'] = run_gate1a.oracle_identity()
        tools['oracle']['nifCurveEval'] = sha256_file(os.path.join(os.path.dirname(HERE), 'nif_curve_eval.py'))
    except OSError as failure:
        tools['oracle'] = {'error': str(failure)}
    receipt = {
        'schema': 'gate1b-receipt/1', 'started': started, 'finished': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'manifest': {'path': os.path.abspath(args.manifest), 'sha256': sha256_file(args.manifest), 'samples': len(samples),
                     'samplesInManifest': len(all_samples), 'limit': args.limit, 'filter': args.filter,
                     'subset': len(samples) != len(all_samples)},
        'tools': tools, 'summary': summary, 'samples': receipts,
    }
    with open(os.path.join(args.out, 'receipt.json'), 'w', encoding='utf-8') as f:
        json.dump(receipt, f, indent=1, default=str)
    with open(os.path.join(args.out, 'receipt.md'), 'w', encoding='utf-8') as f:
        f.write(markdown(receipt))
    print('receipt: %s' % os.path.join(args.out, 'receipt.json'))
    return 0 if summary['allPassed'] else 1


if __name__ == '__main__':
    sys.exit(main())
