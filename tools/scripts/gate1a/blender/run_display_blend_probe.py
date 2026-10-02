# SPDX-License-Identifier: 0BSD
"""The display-blend probe on Blender 5.1: the T <= 1 and T > 1 constant sets, both engines, one run.

Usage:
    python run_display_blend_probe.py [--out DIR] [--blender PATH] [--render-samples 16] [--timeout-seconds 1800]
        [--admission-timeout-seconds 3600] [--retry-failed] [--allow-blender-version X] [--dry-run]

What it answers (display-blend DESIGN section 7 and CHALLENGE items 1, 2, 4, 5 and 7), per engine (Cycles and EEVEE,
always both):
* Does a Transparent BSDF color above 1 composite unclamped (E + T lin(Cd))? The two-cell sentinels, through Value
  nodes and through socket defaults.
* Does Blender draw each constant set's display-space graphs as declared (within 1/255), over the PNG, BGRA8 DDS and BC3
  DDS copies of the texture? Set B's cells that need T > 1 are judged separately.
* Do the reported bounds hold on the render (within bound + 1/255 of Gamebryo), and does each worst-case cell realize
  its bound (within 2/255, a probe-design check)?
* The lit route with m in [0, 2] against lit twins; the additive dark-background profile beside the worst case (D1).
* Control: the same scene from a spec copy whose set-A nodes are (0, 1) / xa 0, set-B tmax 1 and lit curves today's.

It never builds a package and never runs import_model.py: ``inside_blender/display_blend_variant.py`` draws the graphs
straight from the spec. Every Blender process goes through the harness's own ``blender_launcher`` (one at a time, the
run mutex, admission at >= 6 GiB available, BelowNormal priority, the watchdog at 2 GiB available / 4 GiB private
bytes, a receipt per launch); rerunning the same command with the same ``--out`` resumes. Five launches: the version,
then pristine and control per engine.

Outputs under ``--out``: ``receipt.json`` / ``receipt.md``; ``work/`` (images, ``spec.json``, ``spec-control.json``, and
per engine and variant the EXR, PNG and ``probe-result.json``); ``launches/`` (per-launch receipts and logs).

How to read a run (review 2026-09-27 item 10). The checks come in two kinds. VALIDITY checks say the probe works: the
read-back graphs compute their declared models, the texel rule, set A, set K and every set-B cell or material that
needs no T above 1 drawn as declared and within its bound, the worst cells exercising their bounds, clean sentinel
verdicts with their non-discriminating rows on the composite model, the lit route on T <= 1, the controls, and the
consistency of the set-B answer with the linked sentinels' verdict. ANSWER checks are set B's cells and materials that
need T above 1; whether they pass IS the answer, reported per engine as ``setBAvailable`` in the D1 inputs. A clean
"clamped at 1" run therefore fails its answer checks, reads "set B: UNAVAILABLE" and still exits 0.
Exit codes: 0 the probe is valid in both engines (every validity check passed, both controls detected), whatever the
set-B answer; 1 a validity check failed, a control was not detected (or was masked), or the answer contradicts the
sentinels; 2 usage, a refused limit or a Blender that is not 5.1.x; 3 aborted (admission timeout, or another harness run
holds the run mutex).
"""

import argparse
import datetime
import json
import os
import re
import sys
import traceback

sys.dont_write_bytecode = True  # never leave bytecode beside the harness modules this imports

HERE = os.path.dirname(os.path.abspath(__file__))


def find_harness(start):
    """The directory holding the unchanged harness modules (blender_launcher.py and friends): this directory once the
    staged files are promoted, else the first ancestor's tools/scripts/gate1a/blender that has them."""
    if os.path.isfile(os.path.join(start, 'blender_launcher.py')):
        return start
    candidate = start
    for _ in range(12):
        probe = os.path.join(candidate, 'tools', 'scripts', 'gate1a', 'blender')
        if os.path.isfile(os.path.join(probe, 'blender_launcher.py')) and os.path.abspath(probe) != os.path.abspath(start):
            return probe
        candidate = os.path.dirname(candidate)
    raise SystemExit('the gate-1a Blender harness (blender_launcher.py) was not found above %s' % start)


HARNESS = find_harness(HERE)
REPO = os.path.normpath(os.path.join(HARNESS, '..', '..', '..', '..'))
sys.path[:0] = [HERE, os.path.dirname(HERE)]  # the staged copies (probe_builders, compare_f, display_blend_math) first
for extra in (HARNESS, os.path.dirname(HARNESS)):  # then the unchanged modules they import
    if extra not in sys.path:
        sys.path.append(extra)

import blender_launcher as bl  # noqa: E402
import compare_f  # noqa: E402
import display_blend_math as dbm  # noqa: E402
import probe_builders as builders  # noqa: E402
from gate1a_common import HopResult  # noqa: E402

ENGINES = ('CYCLES', 'EEVEE')
VARIANTS = ('pristine', 'control')
REQUIRED_BLENDER = (5, 1)
HOP = 'display-blend'
SCRIPTS = {'variant': os.path.join(HERE, 'inside_blender', 'display_blend_variant.py'),
           'probeRender': os.path.join(HERE, 'inside_blender', 'probe_render.py')}


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def build(work, samples):
    """(constants, pristine spec, control spec, spec paths): the probe inputs, written under ``work``."""
    constants = dbm.load_constants()
    spec = builders.build_display_blend_probe(work, constants, control=False, samples=samples)
    control = builders.build_display_blend_probe(work, constants, control=True, samples=samples)
    return constants, spec, control, {'pristine': os.path.join(work, 'spec.json'), 'control': os.path.join(work, 'spec-control.json')}


def launch_spec(blender, work, engine, variant, spec_path, timeout_seconds):
    unit = os.path.join(work, '%s-%s' % (engine.lower(), variant))
    os.makedirs(unit, exist_ok=True)
    result = os.path.join(unit, 'probe-result.json')
    command = bl.blender_script_command(blender, SCRIPTS['variant'], ['--spec', os.path.abspath(spec_path), '--out', result,
                                                                      '--work', unit, '--engine', engine])
    inputs = {'spec': spec_path}
    for name in os.listdir(os.path.join(work, 'images')):
        inputs['image ' + name] = os.path.join(work, 'images', name)
    return bl.LaunchSpec(HOP, engine, variant, command, inputs=inputs, scripts=dict(SCRIPTS), outputs={'result': result},
                         timeout_seconds=timeout_seconds), result


def load_result(path, receipt):
    """(probe result or None, why not)."""
    run = (receipt or {}).get('run') or {}
    if receipt is None:
        return None, 'not launched'
    if run.get('outcome') not in (bl.OUTCOME_OK, bl.OUTCOME_NONZERO) or not path or not os.path.isfile(path):
        return None, '%s (exit %s): %s' % (run.get('outcome'), run.get('exitCode'), ((receipt.get('stderrTail') or '')[-400:]))
    try:
        return compare_f.load_probe(path), None
    except (ValueError, OSError) as failure:
        return None, 'unreadable result: %s' % failure


def judge(spec, control_spec, results):
    """Judge every engine: ``results[(engine, variant)] = (probe or None, why not)``. Returns the per-engine document."""
    engines = {}
    for engine in ENGINES:
        document = {'engine': engine}
        pristine, why = results.get((engine, 'pristine'), (None, 'not run'))
        result_obj = HopResult('F-display-blend', engine)
        answer_obj = HopResult('F-display-blend set B (T > 1)', engine)
        launch = result_obj.check('%s: the pristine launch wrote a result without errors' % engine)
        if pristine is None:
            launch.fail(compare_f.Mismatch('pristine', 'launch', why))
        elif pristine.get('errors'):
            launch.fail(compare_f.Mismatch('pristine', 'errors', '; '.join(pristine['errors'])[:800]))
        launch.data['settings'] = (pristine or {}).get('settings')
        launch.data['engine'] = (pristine or {}).get('engine')
        launch.ok()
        evaluation = None
        if pristine is not None:
            evaluation, document['summary'] = compare_f.check_display_blend(result_obj, spec, pristine, engine, answer_obj=answer_obj)
            document['pngVersusExrMaxDifference'] = compare_f.png_cross_check(pristine, spec)
        control, why = results.get((engine, 'control'), (None, 'not run'))
        control_obj = HopResult('F-display-blend', engine + ' control')
        if control is None or evaluation is None:
            document['control'] = {'detected': None, 'detail': 'NEVER EXERCISED: %s' % (why if control is None else 'the pristine render is missing')}
        else:
            _control_evaluation, control_summary = compare_f.check_display_blend(control_obj, control_spec, control, engine + ' control',
                                                                                pristine=False)
            detected, detail, verdicts = compare_f.display_blend_control(spec, evaluation, control_spec, control)
            document['control'] = {'detected': detected, 'detail': detail, 'groups': verdicts,
                                   'controlDrawsItsOwnConstants': control_summary['drawn'],
                                   'controlErrors': (control.get('errors') or [])[:20]}
        document['result'] = result_obj.to_dict()
        document['passed'] = result_obj.passed  # validity: the probe works
        document['answer'] = answer_obj.to_dict()
        document['setBAnswerPassed'] = answer_obj.passed if pristine is not None else None
        engines[engine] = document
    return engines


def _check_status(document, name_part, where='result'):
    for check in ((document.get(where) or {}).get('checks') or []):
        if name_part in check['name']:
            return check['status']
    return None


def decisions(engines, constants):
    """The owner's D1 inputs per engine, stated from the checks (never assumed). Set B is available only when the
    probe is valid in that engine, the linked sentinels composite T > 1 unclamped (the path every drawn T > 1 takes,
    by the rule that a constant above 1 is never a socket default), every ANSWER check passed (set B's blend cells,
    bounds, lit cells, lit bounds and read-back graphs that need T > 1), and the set-B cells that need no T > 1 passed
    too. The socket-default verdict is reported for the importer's rule, never relied on."""
    out = {}
    for engine, document in engines.items():
        summary = document.get('summary') or {}
        verdicts = summary.get('transparentAboveOne') or {}
        sentinel = verdicts.get('linked')
        drawn = summary.get('drawn') or {}
        set_b_low = (drawn.get('B, T <= 1') or {}).get('failures') == 0 and _check_status(document, 'bound: set B, T <= 1') == 'pass'
        lit_b_low = _check_status(document, 'lit: set B, T <= 1') in ('pass', 'not-applicable') \
            and _check_status(document, 'lit bound: set B, T <= 1') in ('pass', 'not-applicable')
        answer = document.get('setBAnswerPassed') is True
        set_b_ok = bool(document.get('passed')) and sentinel == 'unclamped' and answer and set_b_low and lit_b_low
        set_a_ok = (drawn.get('A') or {}).get('failures') == 0 and _check_status(document, 'lit: set A, T <= 1') == 'pass'
        additive = {name: constants['sets'][name]['pairs']['ONE/ONE']['bound255'] for name in ('K', 'A', 'B')}
        over_black = {name: rows[0]['worst255'] for name, rows in constants['darkProfile'].items()}
        failed_answer = [c['name'] for c in ((document.get('answer') or {}).get('checks') or []) if c['status'] == 'fail']
        out[engine] = {'probeValid': bool(document.get('passed')), 'transparentAboveOneComposites': sentinel,
                       'socketDefaultAboveOneComposites': verdicts.get('socket'),
                       'socketDefaultsStoredOtherwise': (summary.get('graphs') or {}).get('socketSentinelDefaultsStoredOtherwise'),
                       'setADrawnAsDeclared': set_a_ok, 'setBAnswerChecksPassed': answer, 'setBAnswerChecksFailed': failed_answer,
                       'setBDrawnAsDeclared': set_b_ok, 'additiveBound255': additive, 'additiveOverBlack255': over_black,
                       'setBAvailable': set_b_ok}
    return out


def profile_markdown(profile):
    """The additive dark-background profile beside each set's worst case: per grey Cd, the analytic worst over every
    k0 and the worst measured over the rendered profile cells (all three additive pairs and every k0 level)."""
    if not profile:
        return []
    measured = {}
    for key, greys in profile['cells'].items():
        set_name = key.split(' ')[1]
        for grey, row in greys.items():
            if row['measured255'] is not None:
                slot = measured.setdefault(set_name, {})
                slot[int(grey)] = max(slot.get(int(grey), 0.0), row['measured255'])
    greys = sorted({int(g) for rows in profile['analyticWorstOverK0'].values() for g in rows})
    lines = ['Additive dark-background profile (error /255 by background grey; analytic worst over k0 / measured worst '
             'over the rendered cells; the last column is the worst case over every background):', '',
             '| Set | ' + ' | '.join('Cd %d' % g for g in greys) + ' | Worst case |',
             '|---|' + '---|' * (len(greys) + 1)]
    for set_name in ('K', 'A', 'B'):
        analytic = profile['analyticWorstOverK0'].get(set_name) or {}
        cells = ['%.1f / %s' % (analytic.get(str(g), float('nan')),
                                '%.1f' % measured[set_name][g] if g in measured.get(set_name, {}) else '-') for g in greys]
        lines.append('| %s | %s | %.2f |' % (set_name, ' | '.join(cells), profile['worstCaseBound255'][set_name]))
    return lines + ['']


def markdown(receipt):
    lines = ['# Display-blend probe receipt', '', '- Started %s, finished %s' % (receipt['started'], receipt.get('finished')),
             '- Blender: `%s` %s' % (receipt['blender'].get('path'), receipt['blender'].get('version')),
             '- Constants SHA-256 (canonical JSON): `%s`' % receipt.get('constantsSha256'), '']
    if receipt.get('aborted'):
        lines += ['**ABORTED:** %s' % receipt['aborted'], '']
    lines += ['How to read this receipt: VALIDITY decides the exit code (0 = the probe works in both engines). The set-B '
              'ANSWER checks are the cells and graphs that need T above 1; their failing on a clean "clamped at 1" run is '
              'the answer "set B unavailable", not a broken probe. A contradiction between the answer and the linked '
              'sentinels is a validity failure.', '']
    for engine, document in (receipt.get('engines') or {}).items():
        decision = (receipt.get('decisions') or {}).get(engine) or {}
        lines += ['## %s: probe %s; set B %s' % (engine, 'VALID' if document.get('passed') else 'INVALID',
                                                  'AVAILABLE' if decision.get('setBAvailable') else 'UNAVAILABLE'), '',
                  'Validity checks:', '']
        for check in (document.get('result') or {}).get('checks') or []:
            first = (check.get('firstMismatch') or {})
            lines.append('- %s: %s%s' % (check['status'].upper(), check['name'],
                                         (' (%s: %s)' % (first.get('where'), first.get('detail'))) if check['status'] == 'fail' else ''))
        lines += ['', 'Answer checks (set B, T > 1; a failure here is the answer, not a probe failure):', '']
        for check in (document.get('answer') or {}).get('checks') or []:
            first = (check.get('firstMismatch') or {})
            lines.append('- %s: %s%s' % (check['status'].upper(), check['name'],
                                         (' (%s: %s)' % (first.get('where'), first.get('detail'))) if check['status'] == 'fail' else ''))
        control = document.get('control') or {}
        lines += ['- CONTROL: %s' % control.get('detail'), '']
        summary = document.get('summary') or {}
        if summary.get('bounds'):
            lines += ['| Set | Pair | Bound /255 | Measured worst /255 |', '|---|---|---|---|']
            for set_name, pairs in summary['bounds'].items():
                for pair, row in sorted(pairs.items()):
                    lines.append('| %s | %s | %.4f | %.3f |' % (set_name, pair, row['bound255'], row['measuredWorst255']))
            lines.append('')
        lines += profile_markdown(summary.get('profile'))
    for engine, decision in (receipt.get('decisions') or {}).items():
        lines += ['## D1 inputs, %s' % engine, '', '```', json.dumps(decision, indent=1), '```', '']
    lines += ['Probe: **%s**; set B: %s' % ('VALID' if receipt.get('probeValid') else 'INVALID',
                                             ', '.join('%s %s' % (engine, 'available' if d.get('setBAvailable') else 'unavailable')
                                                       for engine, d in (receipt.get('decisions') or {}).items()) or 'not judged'), '']
    return '\n'.join(lines)


def parse_arguments(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--out', default=os.path.join(REPO, 'TestOutput', 'display-blend-probe-%s' % datetime.datetime.now().strftime('%Y%m%d-%H%M%S')))
    parser.add_argument('--blender', default=bl.DEFAULT_BLENDER)
    parser.add_argument('--allow-blender-version', help='accept this exact version string instead of 5.1.x')
    parser.add_argument('--render-samples', type=int, default=16)
    parser.add_argument('--timeout-seconds', type=float, default=1800.0)
    parser.add_argument('--admission-timeout-seconds', type=float, default=3600.0)
    parser.add_argument('--retry-failed', action='store_true')
    parser.add_argument('--dry-run', action='store_true', help='build the probe and print every launch; launch nothing')
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_arguments(argv)
    try:
        limits = bl.Limits(timeout_seconds=args.timeout_seconds, admission_timeout_seconds=args.admission_timeout_seconds)
    except bl.LimitError as failure:
        print('refused: %s' % failure, file=sys.stderr)
        return 2
    for key, path in SCRIPTS.items():
        if not os.path.isfile(path):
            print('missing script %s: %s' % (key, path), file=sys.stderr)
            return 2
    if not args.dry_run and not os.path.isfile(args.blender):
        print('Blender not found at %s' % args.blender, file=sys.stderr)
        return 2
    out = os.path.abspath(args.out)
    work = os.path.join(out, 'work')
    os.makedirs(work, exist_ok=True)
    constants, spec, control_spec, spec_paths = build(work, args.render_samples)
    blender = {'path': os.path.abspath(args.blender), 'version': None,
               'sha256': bl.sha256_file(args.blender) if os.path.isfile(args.blender) else None, 'fake': bl.is_python_fake(args.blender)}
    receipt = {'schema': 'gate1a-display-blend-receipt/1', 'started': utc_now(), 'argv': sys.argv[1:] if argv is None else argv,
               'out': out, 'limits': limits.to_dict(), 'blender': blender, 'constantsSha256': spec['constantsSha256'],
               'scripts': {k: bl.sha256_file(v) for k, v in SCRIPTS.items()}, 'harness': HARNESS, 'aborted': None,
               'cells': len(spec['cells']), 'materials': len(spec['materials']), 'resolution': spec['render']['resolution']}

    def log(message):
        print(message, flush=True)

    launches, results = [], {}
    exit_code = 0
    lock = bl.RunLock()
    try:
        if not args.dry_run and not lock.acquire(args.admission_timeout_seconds):
            raise RuntimeError('another harness run holds %s' % bl.RUN_MUTEX)
        launcher = bl.Launcher(limits, log=log)
        store = bl.LaunchStore(out, blender)

        def launch(launch_spec_):
            if args.dry_run:
                log('[dry run] %s/%s: %s' % (launch_spec_.unit, launch_spec_.name, ' '.join(launch_spec_.command)))
                return None
            receipt_ = bl.launch(launch_spec_, store, launcher, retry_failed=args.retry_failed, log=log)
            launches.append({'unit': launch_spec_.unit, 'name': launch_spec_.name, 'run': receipt_.get('run'),
                             'resumed': bool(receipt_.get('resumed')), 'receipt': store.receipt_path(launch_spec_)})
            return receipt_

        version = launch(bl.LaunchSpec('setup', 'blender', 'version', bl.executable_prefix(args.blender) + ['--version'],
                                       inputs={'blender': args.blender}))
        if version is not None:
            text = bl.head_text(version.get('stdout')) or ''
            match = re.search(r'Blender (\d+)\.(\d+)\.(\d+)', text)
            blender['version'] = match.group(0) if match else None
            ok = match is not None and (int(match.group(1)), int(match.group(2))) == REQUIRED_BLENDER
            if not ok and not (args.allow_blender_version and blender['version'] == args.allow_blender_version):
                receipt['aborted'] = 'refused: %r is not Blender 5.1.x' % (text.strip().splitlines()[:1],)
                exit_code = 2
        if not exit_code:
            for engine in ENGINES:
                for variant in VARIANTS:
                    launch_spec_, result_path = launch_spec(args.blender, work, engine, variant, spec_paths[variant], args.timeout_seconds)
                    receipt_ = launch(launch_spec_)
                    if receipt_ is not None:
                        results[(engine, variant)] = load_result(result_path, receipt_)
    except bl.AdmissionTimeout as failure:
        receipt['aborted'] = str(failure)
        exit_code = 3
    except RuntimeError as failure:
        receipt['aborted'] = str(failure)
        exit_code = 3
    except Exception as failure:  # noqa: BLE001 - the receipt records the harness defect
        receipt['aborted'] = 'harness error: %s: %s' % (type(failure).__name__, failure)
        receipt['traceback'] = traceback.format_exc()
        exit_code = 1
    finally:
        lock.release()
    receipt['launches'] = launches
    if args.dry_run:
        log('dry run: probe built under %s (%d cells, %d materials, %s px); no launch made' % (work, len(spec['cells']),
                                                                                             len(spec['materials']), spec['render']['resolution']))
        return 0
    if not receipt['aborted']:
        receipt['engines'] = judge(spec, control_spec, results)
        receipt['decisions'] = decisions(receipt['engines'], constants)
        receipt['probeValid'] = all(document.get('passed') and (document.get('control') or {}).get('detected') is True
                                    for document in receipt['engines'].values())
    receipt['finished'] = utc_now()
    with open(os.path.join(out, 'receipt.json'), 'w', encoding='utf-8') as f:
        json.dump(receipt, f, indent=1, default=str)
    with open(os.path.join(out, 'receipt.md'), 'w', encoding='utf-8') as f:
        f.write(markdown(receipt))
    log('receipt: %s' % os.path.join(out, 'receipt.md'))
    if exit_code:
        return exit_code
    return 0 if receipt.get('probeValid') else 1


if __name__ == '__main__':
    sys.exit(main())
