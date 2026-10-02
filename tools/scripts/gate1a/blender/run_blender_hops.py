# SPDX-License-Identifier: 0BSD
"""Gate 1a, the Blender hops: B' (GLB imports in 12 fresh processes), D (package -> .blend -> readback) and F (render
probes and the transmission route), run serially through the owner's memory gate.

Usage:
    python run_blender_hops.py {bprime,d,f,all} [--out DIR] [--blender PATH] [--artifacts MANIFEST ...]
        [--limit N] [--filter TEXT] [--repeats 12] [--timeout-seconds 900] [--admission-timeout-seconds 3600]
        [--min-free-gib 6] [--kill-free-gib 2] [--max-private-gib 4]
        [--render-samples 16] [--retry-failed] [--dry-run]

Hop F's blend pairs have ONE reference, no switch: each cell within 1/255 of the display fit its package declares and
within the declared bound + 1/255 of Gamebryo's result (``compare_f.check_blend``); the retired ``--blend-model``
option chose between two references and is gone. Hop F renders every probe and every probe control in BOTH engines,
Cycles and EEVEE, on every run (design 6.4); the retired ``--engine`` option chose one and is gone too. The probes: the
blend pairs (with the factor-swap control and control C2, planted display nodes), the importer's lit route against lit
twins (with control C4, a planted lit exponent), the asymmetric ramp and DDS orientation (each with its control); then
the engine-independent transmission route with its two controls.

Every Blender process goes through ``blender_launcher.launch``: one at a time (a named run mutex, serial launches, and
admission waits while any other blender.exe runs), admitted only at >= 6 GiB available physical memory (polled; the run
aborts with exit code 3 after the admission timeout, never bypassing), BelowNormal priority, a watchdog killing it below
2 GiB available or above 4 GiB private bytes (recorded as a failure), a per-launch timeout, and a receipt per launch
(command, exit code, seconds, peak private bytes, minimum available memory) under ``<out>/launches/``. Rerunning the same
command with the same ``--out`` resumes: a launch whose receipt matches (command, input, script and Blender SHA-256s,
outputs unchanged) is not relaunched; ``--retry-failed`` relaunches the ones that failed. Comparisons are always
recomputed from the saved outputs.

The Blender path defaults to ``C:/Program Files/Blender Foundation/Blender 5.1/blender.exe``; its SHA-256 and the
version it prints (``blender --version``, itself a gated launch) are recorded, and anything but 5.1.x is refused unless
``--allow-blender-version`` names it. A ``.py`` path is run through this Python instead: that is how
``selfcheck_blender_harness.py`` proves every non-Blender path with ``fake_blender.py``.

Outputs: ``<out>/receipt.json`` and ``<out>/receipt.md`` (per hop: samples, checks, controls, launch counts, time and
memory), ``<out>/results/<hop>/<unit>.json`` per sample, ``<out>/work/`` (probe inputs, controls, .blend files, readback
dumps, renders). Exit codes: 0 every selected hop passed with every control detected; 1 a check failed or a control was
not detected (or was masked); 2 usage, a refused limit, or the wrong Blender; 3 aborted (admission timeout, or another
harness run holds the run mutex).
"""

import argparse
import datetime
import glob
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
GATE1A = os.path.dirname(HERE)
sys.path.insert(0, HERE)
sys.path.insert(0, GATE1A)

import blender_launcher as bl  # noqa: E402
import compare_bprime  # noqa: E402
import compare_d  # noqa: E402
import compare_d_anim  # noqa: E402
import compare_f  # noqa: E402
import controls  # noqa: E402
import probe_builders as builders  # noqa: E402
from gate1a_common import HopResult, load_json  # noqa: E402

REPO = os.path.normpath(os.path.join(GATE1A, '..', '..', '..'))
DEFAULT_ARTIFACTS = [os.path.join(REPO, 'TestOutput', 'pin-66bf702-20260925', 'gate', 'artifacts.json'),
                     os.path.join(REPO, 'TestOutput', 'pin-591d083-20260925', 'extra-go', 'artifacts.json')]
REQUIRED_BLENDER = (5, 1)
HOPS = ('bprime', 'd', 'f')
HOP_TITLES = {'bprime': "B' (GLB -> Blender glTF importer, fresh processes)", 'd': 'D (package -> .blend -> readback)',
              'f': 'F (render probes and the transmission route)'}


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def find_shared_root(explicit=None):
    if explicit:
        return os.path.abspath(explicit)
    candidate = HERE
    for _ in range(8):
        probe = os.path.join(candidate, 'shared', 'Multitool.Shared')
        if os.path.isdir(probe):
            return probe
        candidate = os.path.dirname(candidate)
    raise SystemExit('the Multitool.Shared root was not found; pass --shared')


def git_head(path):
    try:
        completed = subprocess.run(['git', 'rev-parse', 'HEAD'], capture_output=True, text=True, cwd=path, timeout=30)
        return completed.stdout.strip() if completed.returncode == 0 else None
    except (OSError, subprocess.TimeoutExpired):
        return None


class Aborted(Exception):
    """The run stops here (admission timeout or the run mutex); the receipt records why."""


class VersionRefused(Exception):
    """The Blender at --blender is not 5.1.x; nothing but the version launch ran."""


class Runner:
    def __init__(self, args, limits, launcher, store, blender, scripts, log):
        self.args = args
        self.limits = limits
        self.launcher = launcher
        self.store = store
        self.blender = blender
        self.scripts = scripts
        self.log = log
        self.launches = []
        self.out = os.path.abspath(args.out)

    def work(self, *parts):
        path = os.path.join(self.out, 'work', *[bl.safe_name(p) for p in parts])
        os.makedirs(path, exist_ok=True)
        return path

    def stage_short(self, path):
        """A content-addressed copy of ``path`` under ``<out>/in``, for a path Blender cannot open (over MAX_PATH).

        Blender 5.1 is not long-path aware (see ``bl.MAX_PATH_ARGUMENT``); 22 of the 57 dacc231 sample GLBs sit at
        264 to 297 characters. The copy is verified by SHA-256 before use and reused when its bytes already match.
        """
        digest = bl.sha256_file(path)
        staged = os.path.join(self.out, 'in', digest[:16] + os.path.splitext(path)[1].lower())
        if not os.path.isfile(staged) or bl.sha256_file(staged) != digest:
            os.makedirs(os.path.dirname(staged), exist_ok=True)
            temporary = staged + '.tmp'
            shutil.copyfile(path, temporary)
            if bl.sha256_file(temporary) != digest:
                os.remove(temporary)
                raise SystemExit('staging %s did not reproduce its bytes' % path)
            os.replace(temporary, staged)
        return staged

    def launch(self, spec):
        if self.args.dry_run:
            self.log('    [dry run] %s/%s/%s: %s' % (spec.hop, spec.unit, spec.name, subprocess.list2cmdline(spec.command)))
            receipt = {'name': spec.name, 'hop': spec.hop, 'unit': spec.unit, 'command': spec.command,
                       'run': {'outcome': 'dry-run', 'exitCode': None, 'seconds': 0.0}, 'dryRun': True}
        else:
            try:
                receipt = bl.launch(spec, self.store, self.launcher, retry_failed=self.args.retry_failed, log=self.log)
            except bl.AdmissionTimeout as failure:
                raise Aborted(str(failure)) from None
        run = receipt.get('run') or {}
        self.launches.append({'hop': spec.hop, 'unit': spec.unit, 'name': spec.name, 'outcome': run.get('outcome'),
                              'exitCode': run.get('exitCode'), 'seconds': run.get('seconds'),
                              'peakPrivateBytes': run.get('peakPrivateBytes'), 'minAvailableBytes': run.get('minAvailableBytes'),
                              'resumed': bool(receipt.get('resumed')), 'receipt': self.store.receipt_path(spec)})
        return receipt

    def script_command(self, script_key, script_args, blend_file=None):
        return bl.blender_script_command(self.blender, self.scripts[script_key], script_args, blend_file)

    def write_result(self, hop, unit, document):
        path = os.path.join(self.out, 'results', hop, bl.safe_name(unit) + '.json')
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(document, f, indent=1, default=str)
        return path


# --------------------------------------------------------------------------------------------------------------------
# Samples
# --------------------------------------------------------------------------------------------------------------------


def load_samples(paths):
    manifests, samples = [], []
    for tag_index, path in enumerate(paths):
        manifest = load_json(path)
        if manifest.get('schema') != 'gate1a-artifacts/1':
            raise SystemExit('%s is not a gate1a-artifacts/1 manifest' % path)
        tag = 'm%d' % tag_index
        entries = manifest.get('samples') or []
        manifests.append({'path': os.path.abspath(path), 'sha256': bl.sha256_file(path), 'tag': tag, 'samples': len(entries)})
        for index, sample in enumerate(entries):
            sample = dict(sample)
            sample['_unit'] = '%s-%04d_%s' % (tag, index, bl.safe_name(sample.get('id') or sample.get('entry') or index, 90))
            samples.append(sample)
    return manifests, samples


def select(samples, key, text_filter, limit):
    """(present, missing) among the samples that DECLARE ``key`` (after --filter, then --limit). A sample whose declared
    file is missing on disk is returned in ``missing`` and becomes a FAILED sample, so moving or cleaning TestOutput can
    never shrink the gate silently; samples that declare no such file (a conversion that produced none) are not
    selected, as the manifest says."""
    declared = [s for s in samples if s.get(key)]
    if text_filter:
        declared = [s for s in declared if text_filter.lower() in json.dumps({k: v for k, v in s.items() if not k.startswith('_')}).lower()]
    if limit:
        declared = declared[:limit]
    return [s for s in declared if os.path.isfile(s[key])], [s for s in declared if not os.path.isfile(s[key])]


def missing_samples(runner, hop, key, missing):
    """One FAILED sample document per declared-but-missing file."""
    documents = []
    for sample in missing:
        document = {'unit': sample['_unit'], 'sample': sample.get('id'), key: sample[key], 'passed': False,
                    'detail': 'the manifest declares %s %s, but the file is missing on disk' % (key, sample[key])}
        runner.write_result(hop, sample['_unit'], document)
        runner.log('  %s %s: FAIL: %s' % (hop, sample['_unit'], document['detail']))
        documents.append(document)
    return documents


# --------------------------------------------------------------------------------------------------------------------
# Hop B'
# --------------------------------------------------------------------------------------------------------------------


def run_bprime(runner, samples, missing=()):
    repeats = runner.args.repeats
    runner.log("hop B': %d GLB sample(s) x %d fresh processes, plus the VEC4 + VEC3 control" % (len(samples), repeats))
    units = [(s['_unit'], s.get('id'), s['glb'], False) for s in samples]
    control_glb = os.path.join(runner.work('bprime', 'control-vec4-vec3'), 'custom_attributes.glb')
    builders.build_custom_attribute_glb(control_glb)
    units.append(('control-vec4-vec3', 'control: VEC4 + VEC3 underscore attributes', control_glb, True))
    results, control = ([] if runner.args.dry_run else missing_samples(runner, 'bprime', 'glb', missing)), None
    for unit, sample_id, glb, is_control in units:
        runner.log("  B' %s" % unit)
        work = runner.work('bprime', unit)
        mesh_nodes = compare_bprime.glb_mesh_nodes(glb)
        # Only a GLB whose path Blender cannot open is staged, so a short path keeps its command and its receipts.
        target, inputs = os.path.abspath(glb), {'glb': glb}
        if len(target) > bl.MAX_PATH_ARGUMENT:
            target = inputs['staged'] = runner.stage_short(glb)
        judged = []
        for k in range(1, repeats + 1):
            dump = os.path.join(work, 'readback-%02d.json' % k)
            spec = bl.LaunchSpec('bprime', unit, 'import-%02d' % k,
                                 runner.script_command('readback', ['--import-glb', target, '--out', dump]),
                                 inputs=inputs, scripts={'readback': runner.scripts['readback']}, outputs={'dump': dump},
                                 note='fresh process %d of %d' % (k, repeats))
            receipt = runner.launch(spec)
            if runner.args.dry_run:
                continue
            judged.append(compare_bprime.judge_launch(receipt, dump, mesh_nodes))
        if runner.args.dry_run:
            continue
        if is_control:
            detected, detail, data = compare_bprime.judge_control(judged, repeats)
            control = {'name': "B': VEC4 + VEC3 custom attributes fail in at least 1 of %d fresh processes" % repeats,
                       'detected': detected, 'detail': detail, 'data': data, 'glb': control_glb,
                       'glbSha256': bl.sha256_file(control_glb), 'launches': [entry for entry, _ in judged]}
            runner.write_result('bprime', unit, control)
            runner.log("    control: %s" % detail)
        else:
            passed, detail, data = compare_bprime.judge_sample(judged, repeats)
            document = {'unit': unit, 'sample': sample_id, 'glb': glb, 'glbSha256': bl.sha256_file(glb), 'meshNodes': mesh_nodes,
                        'passed': passed, 'detail': detail, 'data': data, 'launches': [entry for entry, _ in judged]}
            runner.write_result('bprime', unit, document)
            results.append(document)
            runner.log('    %s: %s' % ('PASS' if passed else 'FAIL', detail))
    return {'hop': 'bprime', 'samples': results, 'controls': [control] if control else [], 'repeats': repeats}


# --------------------------------------------------------------------------------------------------------------------
# Hop D
# --------------------------------------------------------------------------------------------------------------------


def d_import_and_readback(runner, unit, package, blend_input=None, label=''):
    """Import (or reuse ``blend_input``) and read back; returns (import receipt, readback receipt, blend, dump)."""
    work = runner.work('d', unit)
    blend = os.path.join(work, 'model.blend')
    dump = os.path.join(work, 'readback.json')
    import_receipt = None
    if blend_input is None:
        spec = bl.LaunchSpec('d', unit, 'import', runner.script_command('importModel', ['--package', os.path.abspath(package),
                                                                                         '--output', blend]),
                             inputs={'package': package}, scripts={'importModel': runner.scripts['importModel']},
                             outputs={'blend': blend}, note=label)
        import_receipt = runner.launch(spec)
        source_blend = blend
    else:
        source_blend = blend_input
    readback_receipt = None
    if runner.args.dry_run or (os.path.isfile(source_blend) and (import_receipt is None or (import_receipt.get('run') or {}).get('outcome') == 'ok')):
        spec = bl.LaunchSpec('d', unit, 'readback', runner.script_command('readback', ['--blend', os.path.abspath(source_blend),
                                                                                       '--out', dump, '--include-values']),
                             inputs={'blend': source_blend}, scripts={'readback': runner.scripts['readback']},
                             outputs={'dump': dump}, note=label)
        readback_receipt = runner.launch(spec)
    return import_receipt, readback_receipt, source_blend, dump


def run_d(runner, samples, missing=()):
    runner.log('hop D: %d package sample(s) plus the synthetic set; controls: shape-key vertex moved, DDS repacked, '
               'animation key moved' % len(samples))
    units = [{'unit': s['_unit'], 'sample': s.get('id'), 'package': s['package'], 'dump': s.get('dump'), 'synthetic': False}
             for s in samples]
    synthetic_package = os.path.join(runner.work('d', 'synthetic-d-set'), 'package.zip')
    builders.build_d_synthetic(synthetic_package)
    synthetic_dump = builders.write_synthetic_dump(synthetic_package, os.path.join(runner.work('d', 'synthetic-d-set'), 'dump.json'))
    units.append({'unit': 'synthetic-d-set', 'sample': 'synthetic: hop-D coverage package', 'package': synthetic_package,
                  'dump': synthetic_dump, 'synthetic': True})
    pristine = {}
    results = [] if runner.args.dry_run else missing_samples(runner, 'd', 'package', missing)
    for unit in units:
        runner.log('  D %s' % unit['unit'])
        import_receipt, readback_receipt, blend, dump = d_import_and_readback(runner, unit['unit'], unit['package'])
        if runner.args.dry_run:
            continue
        result = compare_d.compare(unit['sample'], unit['package'], unit['dump'], dump if readback_receipt else None,
                                   import_receipt, readback_receipt)
        passed = result['passed']
        document = {'unit': unit['unit'], 'sample': unit['sample'], 'synthetic': unit['synthetic'], 'package': unit['package'],
                    'packageSha256': bl.sha256_file(unit['package']), 'dump': unit['dump'], 'blend': blend,
                    'readback': dump, 'passed': passed, 'result': result}
        runner.write_result('d', unit['unit'], document)
        results.append(document)
        pristine[unit['unit']] = (unit, result, import_receipt, blend)
        failed = [c for c in result['checks'] if c['status'] == 'fail']
        runner.log('    %s%s' % ('PASS' if passed else 'FAIL', (': %s: %s' % (failed[0]['name'], (failed[0]['firstMismatch'] or {}).get('detail'))) if failed else ''))
    control_results = []
    if not runner.args.dry_run:
        control_results.append(d_shape_key_control(runner, units, pristine))
        control_results.append(d_repack_control(runner, units, pristine))
        control_results.append(d_animation_key_control(runner, units, pristine))
    return {'hop': 'd', 'samples': results, 'controls': control_results}


def _import_ok(entry):
    return entry is not None and ((entry[2] or {}).get('run') or {}).get('outcome') == 'ok' and os.path.isfile(entry[3])


def _control_candidates(units, pristine):
    corpus = [u for u in units if not u['synthetic']]
    synthetic = [u for u in units if u['synthetic']]
    return [(u, False) for u in corpus] + [(u, True) for u in synthetic]


def _masked_note(masked):
    return ('; %d candidate(s) skipped because the pristine comparison already fails at the control element: %s'
            % (len(masked), '; '.join(masked[:5]))) if masked else ''


def d_shape_key_control(runner, units, pristine):
    """The control runs on the first candidate that imported, carries a morph target, and whose PRISTINE comparison
    is clean at the element the control mutates (checked before anything is launched), so a sample masked there
    never spends the control; every skipped candidate is listed."""
    name = 'D: one shape-key vertex moved in a package copy'
    masked = []
    for unit, synthetic in _control_candidates(units, pristine):
        entry = pristine.get(unit['unit'])
        found = controls.first_morph_primitive(unit['package']) if _import_ok(entry) else None
        if found is None:
            continue
        expected_where = controls.shape_key_where(found[0], found[1])
        hits = compare_d.mismatches_at(entry[1], expected_where)
        if hits:
            masked.append('%s at %s (%s)' % (unit['unit'], hits[0]['where'], hits[0].get('detail')))
            continue
        control_unit = unit['unit'] + '__control-shape-key'
        mutated = os.path.join(runner.work('d', control_unit), 'control-shape-key.zip')
        change = controls.move_shape_key_vertex(unit['package'], mutated)
        import_receipt, readback_receipt, _blend, dump = d_import_and_readback(runner, control_unit, mutated, label=name)
        result = compare_d.compare(unit['sample'] + ' (shape-key control)', unit['package'], unit['dump'],
                                   dump if readback_receipt else None, import_receipt, readback_receipt)
        detected, detail = compare_d.control_verdict(entry[1], result, change['expectedWhere'])
        document = {'name': name, 'detected': detected, 'detail': detail + _masked_note(masked), 'onUnit': unit['unit'],
                    'synthetic': synthetic, 'change': change, 'mutatedPackage': mutated, 'mutatedSha256': bl.sha256_file(mutated),
                    'maskedCandidatesSkipped': masked, 'result': result}
        runner.write_result('d', control_unit, document)
        runner.log('  control %s on %s: %s' % (name, unit['unit'], document['detail']))
        return document
    return {'name': name, 'detected': None, 'maskedCandidatesSkipped': masked,
            'detail': 'NEVER EXERCISED: no unit with a morph target imported cleanly at the control element' + _masked_note(masked)}


def d_animation_key_control(runner, units, pristine):
    """One package animation key moved (``controls.move_animation_key``): the saved F-curve's keys must then differ from
    the ORIGINAL package at that channel. Candidates masked there are skipped before launching, as for the shape key."""
    name = 'D: one animation key moved in a package copy'
    if not any(controls.package_has_animations(unit['package']) for unit in units):
        # The control guards the animation comparison; with no animated package that comparison judged nothing.
        return {'name': name, 'detected': None, 'notApplicable': True,
                'detail': 'not applicable: no package in this run carries animations, so the animation comparison '
                          'rendered no verdict to guard'}
    masked = []
    for unit, synthetic in _control_candidates(units, pristine):
        entry = pristine.get(unit['unit'])
        found = controls.first_animation_key(unit['package']) if _import_ok(entry) else None
        if found is None:
            continue
        expected_where = controls.animation_key_where(found[0], found[1])
        hits = compare_d.mismatches_at(entry[1], expected_where)
        if hits:
            masked.append('%s at %s (%s)' % (unit['unit'], hits[0]['where'], hits[0].get('detail')))
            continue
        control_unit = unit['unit'] + '__control-animation-key'
        mutated = os.path.join(runner.work('d', control_unit), 'control-animation-key.zip')
        change = controls.move_animation_key(unit['package'], mutated)
        import_receipt, readback_receipt, _blend, dump = d_import_and_readback(runner, control_unit, mutated, label=name)
        result = compare_d.compare(unit['sample'] + ' (animation-key control)', unit['package'], unit['dump'],
                                   dump if readback_receipt else None, import_receipt, readback_receipt)
        detected, detail = compare_d.control_verdict(entry[1], result, change['expectedWhere'])
        # Credited only when the EVALUATION check also reports the moved key: the key comparison alone would detect it
        # even if Blender's evaluation were never compared.
        evaluation_hits = [m for c in result.get('checks') or [] if c.get('name') == compare_d_anim.EVALUATION_CHECK
                           for m in compare_d.mismatches_at({'checks': [c]}, change['expectedWhere'])]
        if detected is True and not evaluation_hits:
            detected, detail = False, detail + '; the evaluation check did not report it, so it is NOT credited'
        elif detected is True:
            detail += '; the evaluation check reports it too: %s' % (evaluation_hits[0].get('detail') or '')[:200]
        document = {'name': name, 'detected': detected, 'detail': detail + _masked_note(masked), 'onUnit': unit['unit'],
                    'synthetic': synthetic, 'change': change, 'mutatedPackage': mutated, 'mutatedSha256': bl.sha256_file(mutated),
                    'maskedCandidatesSkipped': masked, 'result': result}
        runner.write_result('d', control_unit, document)
        runner.log('  control %s on %s: %s' % (name, unit['unit'], document['detail']))
        return document
    return {'name': name, 'detected': None, 'maskedCandidatesSkipped': masked,
            'detail': 'NEVER EXERCISED: no unit with a Translation, Scale or MorphWeights channel imported cleanly at the '
                      'control element' + _masked_note(masked)}


def d_repack_control(runner, units, pristine):
    """As ``d_shape_key_control``: candidates masked at ``images/<index>/packed_sha256`` are skipped before launching."""
    name = 'D: a DDS repacked through save()'
    masked = []
    for unit, synthetic in _control_candidates(units, pristine):
        entry = pristine.get(unit['unit'])
        if not _import_ok(entry):
            continue
        target = controls.repack_target_image(unit['package'])
        if target is None:
            continue
        hits = compare_d.mismatches_at(entry[1], target['expectedWhere'])
        if hits:
            masked.append('%s at %s (%s)' % (unit['unit'], hits[0]['where'], hits[0].get('detail')))
            continue
        control_unit = unit['unit'] + '__control-dds-repack'
        work = runner.work('d', control_unit)
        control_blend = os.path.join(work, 'control-repacked.blend')
        repack_result = os.path.join(work, 'repack.json')
        spec = bl.LaunchSpec('d', control_unit, 'repack',
                             runner.script_command('repack', ['--blend', os.path.abspath(entry[3]), '--image', target['image'],
                                                              '--out', control_blend, '--result', repack_result,
                                                              '--work', work]),
                             inputs={'blend': entry[3]}, scripts={'repack': runner.scripts['repack']},
                             outputs={'blend': control_blend, 'result': repack_result}, note=name)
        repack_receipt = runner.launch(spec)
        repacked = load_json(repack_result) if os.path.isfile(repack_result) else None
        if (repack_receipt.get('run') or {}).get('outcome') != 'ok' or not os.path.isfile(control_blend):
            document = {'name': name, 'detected': None, 'onUnit': unit['unit'], 'target': target, 'repack': repacked,
                        'detail': 'the repack launch failed (%s, exit %s); the control could not be built'
                                  % ((repack_receipt.get('run') or {}).get('outcome'), (repack_receipt.get('run') or {}).get('exitCode'))}
            runner.write_result('d', control_unit, document)
            return document  # one failed repack is reported; never retried on every remaining sample
        _i, readback_receipt, _b, dump = d_import_and_readback(runner, control_unit, None, blend_input=control_blend, label=name)
        result = compare_d.compare(unit['sample'] + ' (DDS repack control)', unit['package'], unit['dump'],
                                   dump if readback_receipt else None, entry[2], readback_receipt)
        detected, detail = compare_d.control_verdict(entry[1], result, target['expectedWhere'])
        document = {'name': name, 'detected': detected, 'detail': detail + _masked_note(masked), 'onUnit': unit['unit'],
                    'synthetic': synthetic, 'target': target, 'repack': repacked, 'maskedCandidatesSkipped': masked, 'result': result}
        runner.write_result('d', control_unit, document)
        runner.log('  control %s on %s: %s' % (name, unit['unit'], document['detail']))
        return document
    return {'name': name, 'detected': None, 'maskedCandidatesSkipped': masked,
            'detail': 'NEVER EXERCISED: no unit with a decodable packed DDS imported cleanly at the control element' + _masked_note(masked)}


# --------------------------------------------------------------------------------------------------------------------
# Hop F
# --------------------------------------------------------------------------------------------------------------------

F_ENGINES = ('CYCLES', 'EEVEE')  # design 6.4: every run renders both engines; no switch chooses one
# (probe, builder, controls): each control is (label, control-package builder)
F_PROBES = (('blend-pairs', builders.build_blend_probe, (('factor-swap', controls.swap_blend_factors),
                                                         ('display-nodes', controls.plant_display_nodes))),
            ('lit-blend', builders.build_lit_blend_probe, (('lit-exponent', controls.plant_lit_exponent),)),
            ('color-ramp', builders.build_ramp_probe, (('linear-vertex-colors', controls.declare_linear_vertex_colors),)),
            ('dds-orientation', builders.build_orientation_probe, (('flipped-dds', controls.flip_dds_images),)))


def f_pristine_checks(result_obj, name, spec, probe):
    """The pristine checks of one probe; returns what its controls are judged against."""
    if name == 'blend-pairs':
        values = compare_f.check_blend(result_obj, spec, probe)
        compare_f.check_blend_exercise(result_obj, spec)
        compare_f.check_texels(result_obj, spec, probe)
        return values
    if name == 'lit-blend':
        compare_f.check_lit_twins(result_obj, spec, probe)
        return compare_f.check_lit_blend(result_obj, spec, probe)
    if name == 'color-ramp':
        return compare_f.check_ramp(result_obj, spec, probe)
    return compare_f.check_orientation(result_obj, spec, probe)


def f_judge_control(name, label, spec, pristine_values, control_probe):
    """(detected, detail, extra) of one probe control against the pristine values."""
    if label == 'factor-swap':
        detected, detail, verdicts = compare_f.blend_control(spec, pristine_values, control_probe)
        return detected, detail, {'pairs': verdicts}
    if label == 'display-nodes':
        detected, detail, verdicts = compare_f.display_node_control(spec, pristine_values, control_probe)
        return detected, detail, {'pairs': verdicts}
    if label == 'lit-exponent':
        detected, detail, verdicts = compare_f.lit_exponent_control(spec, pristine_values, control_probe)
        return detected, detail, {'pairs': verdicts}
    if name == 'color-ramp':
        detected, detail = compare_f.ramp_control(pristine_values, control_probe, spec)
        return detected, detail, {}
    detected, detail, verdicts = compare_f.orientation_control(pristine_values, control_probe, spec)
    return detected, detail, {'kinds': verdicts}


def f_import_and_probe(runner, unit, package, spec_path):
    work = runner.work('f', unit)
    blend = os.path.join(work, 'probe.blend')
    result_path = os.path.join(work, 'probe-result.json')
    spec = bl.LaunchSpec('f', unit, 'import', runner.script_command('importModel', ['--package', os.path.abspath(package),
                                                                                     '--output', blend]),
                         inputs={'package': package}, scripts={'importModel': runner.scripts['importModel']}, outputs={'blend': blend})
    import_receipt = runner.launch(spec)
    probe_receipt = None
    if runner.args.dry_run or (import_receipt.get('run') or {}).get('outcome') == 'ok':
        spec = bl.LaunchSpec('f', unit, 'probe', runner.script_command('probeRender', ['--blend', blend, '--spec', os.path.abspath(spec_path),
                                                                                       '--out', result_path, '--work', work]),
                             inputs={'blend': blend, 'spec': spec_path}, scripts={'probeRender': runner.scripts['probeRender']},
                             outputs={'result': result_path}, timeout_seconds=runner.args.render_timeout_seconds)
        probe_receipt = runner.launch(spec)
    # Only a probe launched (or resumed with its recorded output unchanged) in THIS run may be read: when the import
    # failed the probe was not launched, and a probe-result.json left by an earlier run must not be judged.
    return import_receipt, probe_receipt, (result_path if probe_receipt is not None else None)


def _probe_or_fail(result_obj, name, import_receipt, probe_receipt, result_path):
    """(probe result or None, the launch check). The check fails on a launch that was not made or did not end ok, an
    unreadable result, or probe ``errors`` (a setting the probe could not apply)."""
    check = result_obj.check('%s: import and probe launches' % name)
    for role, receipt in (('import', import_receipt), ('probe', probe_receipt)):
        run = (receipt or {}).get('run') or {}
        if receipt is None:
            check.fail(compare_f.Mismatch(role, 'presence', 'not launched (the import failed)'))
        elif run.get('outcome') != 'ok':
            check.fail(compare_f.Mismatch(role, 'launch', '%s (exit %s): %s' % (run.get('outcome'), run.get('exitCode'),
                                                                               (receipt.get('stderrTail') or '')[-500:])))
    probe = None
    if result_path and os.path.isfile(result_path):
        try:
            probe = compare_f.load_probe(result_path)
        except ValueError as failure:
            check.fail(compare_f.Mismatch('probe result', 'schema', str(failure)))
    if probe is not None and probe.get('errors'):
        check.fail(compare_f.Mismatch('probe result', 'errors', '; '.join(probe['errors'])[:800]))
    check.data['settings'] = (probe or {}).get('settings')
    check.ok()
    return probe, check


def run_f(runner):
    runner.log('hop F, in Cycles and in EEVEE: blend pairs (the declared display fit, and Gamebryo within its bound) and the '
               'texel rule, the lit route against lit twins, asymmetric ramp, DDS orientation, each with its controls; then the '
               'transmission route')
    controls_out = []
    documents = {}
    samples_out = []
    for engine in F_ENGINES:
        result_obj = HopResult('F', 'probes-%s' % engine.lower())
        for name, build, probe_controls in F_PROBES:
            unit = '%s-%s' % (name, engine.lower())
            work = runner.work('f', unit)
            package = os.path.join(work, 'package.zip')
            spec_path = os.path.join(work, 'spec.json')
            spec = build(package, spec_path, engine=engine, samples=runner.args.render_samples)
            runner.log('  F %s %s' % (engine, name))
            pristine = f_import_and_probe(runner, unit, package, spec_path)
            launched = []
            for label, make_control in probe_controls:
                control_unit = '%s-%s-control' % (unit, label)
                control_package = os.path.join(runner.work('f', control_unit), 'package.zip')
                change = make_control(package, control_package)
                launched.append((label, change, control_package, f_import_and_probe(runner, control_unit, control_package, spec_path)))
            if runner.args.dry_run:
                continue
            probe, pristine_launch = _probe_or_fail(result_obj, '%s %s' % (engine, name), *pristine)
            pristine_values = None
            if probe is not None:  # the pristine value check runs on any result of this run, even beside a failed launch check
                pristine_values = f_pristine_checks(result_obj, name, spec, probe)
                documents['%s %s' % (engine, name)] = {'pngVersusExrMaxDifference': compare_f.png_cross_check(probe, spec)}
            for label, change, control_package, control in launched:
                control_probe, control_launch = _probe_or_fail(HopResult('F', 'control'), '%s %s %s control' % (engine, name, label), *control)
                entry = {'name': 'F %s: %s %s control (%s)' % (engine, name, label, change.get('change')), 'engine': engine,
                         'change': change, 'controlPackageSha256': bl.sha256_file(control_package),
                         'controlLaunches': control_launch.to_dict()}
                # A control is judged only on launches of this run that ended cleanly, on both sides.
                if control_launch.passed is False:
                    first = control_launch.mismatches[0]
                    entry.update(detected=None, detail='NEVER EXERCISED: the control launches failed (%s: %s)' % (first.where, str(first.detail)[:300]))
                elif pristine_launch.passed is False:
                    first = pristine_launch.mismatches[0]
                    entry.update(detected=None, detail='masked: the pristine launches failed (%s: %s)' % (first.where, str(first.detail)[:300]))
                elif probe is None or control_probe is None:
                    entry.update(detected=None, detail='NEVER EXERCISED: the %s probe produced no result' % ('pristine' if probe is None else 'control'))
                else:
                    detected, detail, extra = f_judge_control(name, label, spec, pristine_values, control_probe)
                    entry.update(detected=detected, detail=detail, **extra)
                controls_out.append(entry)
                runner.log('    control %s: %s' % (label, entry.get('detail')))
        if not runner.args.dry_run:
            samples_out.append(f_document(runner, result_obj, 'probes-%s' % engine.lower(),
                                          {key: value for key, value in documents.items() if key.startswith(engine + ' ')}))
    result_obj = HopResult('F', 'transmission')
    # transmission route
    work = runner.work('f', 'transmission')
    glb = os.path.join(work, 'transmission.glb')
    builders.build_transmission_glb(glb)
    bindings = builders.transmission_bindings(glb)
    stripped = os.path.join(runner.work('f', 'transmission-control'), 'transmission-stripped.glb')
    change = controls.strip_transmission(glb, stripped)
    swapped = os.path.join(runner.work('f', 'transmission-swap-control'), 'transmission-et-swapped.glb')
    swap_change = controls.swap_transmission_terms(glb, swapped, builders.TRANSMISSION_MATERIAL)
    with open(glb, 'rb') as f:
        from glb_reader import Glb
        expectation_args = builders.transmission_expectations(Glb(f.read()).json)
    runner.log('  F transmission route')
    receipts = {}
    for unit, path in (('transmission', glb), ('transmission-control', stripped), ('transmission-swap-control', swapped)):
        dump = os.path.join(runner.work('f', unit), 'readback.json')
        spec = bl.LaunchSpec('f', unit, 'readback-import-glb',
                             runner.script_command('readback', ['--import-glb', os.path.abspath(path), '--out', dump] + expectation_args),
                             inputs={'glb': path}, scripts={'readback': runner.scripts['readback']}, outputs={'dump': dump})
        receipts[unit] = (runner.launch(spec), dump)
    if not runner.args.dry_run:
        readback = compare_f.readback_module(runner.args.shared_root)
        check_count = len(result_obj.checks)
        compare_f.check_transmission(result_obj, receipts['transmission'][0], receipts['transmission'][1], readback, expectation_args,
                                     bindings=bindings)
        pristine_passed = result_obj.checks[check_count].passed is True
        detected, detail = compare_f.transmission_control(pristine_passed, receipts['transmission-control'][0],
                                                          receipts['transmission-control'][1], readback, expectation_args)
        controls_out.append({'name': 'F: transmission control (KHR_materials_transmission stripped reads back opaque and fails the route)',
                             'detected': detected, 'detail': detail, 'change': change, 'glbSha256': bl.sha256_file(glb),
                             'strippedSha256': bl.sha256_file(stripped), 'expectations': expectation_args})
        runner.log('    control: %s' % detail)
        detected, detail = compare_f.transmission_swap_control(pristine_passed, receipts['transmission-swap-control'][0],
                                                               receipts['transmission-swap-control'][1], bindings)
        controls_out.append({'name': 'F: transmission E/T swap control (baseColorTexture and emissiveTexture exchanged must fail the binding identity at both sockets)',
                             'detected': detected, 'detail': detail, 'change': swap_change, 'glbSha256': bl.sha256_file(glb),
                             'swappedSha256': bl.sha256_file(swapped), 'bindings': bindings})
        runner.log('    control: %s' % detail)
    if runner.args.dry_run:
        return {'hop': 'f', 'samples': [], 'controls': []}
    samples_out.append(f_document(runner, result_obj, 'transmission', {}))
    return {'hop': 'f', 'samples': samples_out, 'controls': controls_out}


def f_document(runner, result_obj, unit, diagnostics):
    """Write one hop-F result document (the probes of one engine, or the transmission route) and log its checks."""
    result = result_obj.to_dict()
    result['diagnostics'] = diagnostics
    document = {'unit': unit, 'passed': result['passed'], 'result': result,
                'referenceModel': 'drawn (the declared display fit or lit curve) and gamebryo within the declared bound'}
    runner.write_result('f', unit, document)
    for check in result['checks']:
        runner.log('    %s %s: %s%s' % (unit, 'PASS' if check['status'] != 'fail' else 'FAIL', check['name'],
                                        (' (%s)' % (check['firstMismatch'] or {}).get('detail')) if check['status'] == 'fail' else ''))
    return document


# --------------------------------------------------------------------------------------------------------------------
# Summary, receipt, main
# --------------------------------------------------------------------------------------------------------------------


def summarize(hop_outputs, runner, subset):
    summary = {'hops': {}, 'allPassed': True, 'subset': subset}
    for hop, output in hop_outputs.items():
        samples = output.get('samples') or []
        controls_list = output.get('controls') or []
        launches = [row for row in runner.launches if row['hop'] == hop]
        entry = {'samplesPassed': sum(1 for s in samples if s.get('passed')),
                 'samplesFailed': sum(1 for s in samples if not s.get('passed')),
                 'controls': [{'name': c.get('name'), 'detected': c.get('detected'), 'notApplicable': bool(c.get('notApplicable')),
                               'detail': c.get('detail')} for c in controls_list],
                 'controlsDetected': sum(1 for c in controls_list if c.get('detected') is True),
                 'controlsNotDetected': sum(1 for c in controls_list if c.get('detected') is False),
                 'controlsNeverExercised': sum(1 for c in controls_list if c.get('detected') is None and not c.get('notApplicable')),
                 'controlsNotApplicable': sum(1 for c in controls_list if c.get('notApplicable')),
                 'launches': len(launches), 'launchesResumed': sum(1 for r in launches if r['resumed']),
                 'launchOutcomes': _count(r['outcome'] for r in launches),
                 'blenderSeconds': round(sum(r['seconds'] or 0.0 for r in launches), 1),
                 'peakPrivateGiB': bl.gib(max([r['peakPrivateBytes'] or 0 for r in launches] or [0])),
                 'minAvailableGiB': bl.gib(min([r['minAvailableBytes'] for r in launches if r['minAvailableBytes'] is not None] or [None]) if any(r['minAvailableBytes'] is not None for r in launches) else None)}
        entry['passed'] = (entry['samplesFailed'] == 0 and bool(samples) and entry['controlsNotDetected'] == 0
                           and entry['controlsNeverExercised'] == 0 and bool(controls_list)
                           and entry['controlsDetected'] == len(controls_list) - entry['controlsNotApplicable']
                           and entry['controlsDetected'] > 0)
        summary['hops'][hop] = entry
        if not entry['passed']:
            summary['allPassed'] = False
    return summary


def _count(values):
    out = {}
    for value in values:
        out[value] = out.get(value, 0) + 1
    return out


def identity_of_scripts(scripts):
    return {key: {'path': path, 'sha256': bl.sha256_file(path)} for key, path in scripts.items()}


def harness_identity():
    files = sorted(glob.glob(os.path.join(HERE, '*.py')) + glob.glob(os.path.join(HERE, 'inside_blender', '*.py')))
    return {'scripts': {os.path.relpath(p, HERE).replace('\\', '/'): bl.sha256_file(p) for p in files}, 'gitHead': git_head(HERE)}


def markdown(receipt):
    lines = ['# Gate 1a Blender hops (B\', D, F)', '',
             'Started %s, finished %s.%s' % (receipt['started'], receipt.get('finished'),
                                             ' **SUBSET** (%s).' % receipt['summary']['subset'] if receipt['summary'].get('subset') else ''),
             '']
    if receipt.get('aborted'):
        lines += ['**ABORTED:** %s' % receipt['aborted'], '']
    blender = receipt.get('blender') or {}
    lines += ['- Blender: `%s` %s (SHA-256 %s)%s' % (blender.get('path'), blender.get('version'), blender.get('sha256'),
                                                     ' **FAKE**' if blender.get('fake') else ''),
              '- Limits: admission at >= %s GiB available, watchdog below %s GiB available or above %s GiB private, timeout %s s, admission timeout %s s'
              % (receipt['limits']['minFreeGiB'], receipt['limits']['killFreeGiB'], receipt['limits']['maxPrivateGiB'],
                 receipt['limits']['timeoutSeconds'], receipt['limits']['admissionTimeoutSeconds']),
              '- Shared scripts: %s' % ', '.join('%s %s' % (k, v['sha256'][:12]) for k, v in receipt['scripts'].items()),
              '- Shared HEAD %s; harness HEAD %s' % (receipt.get('sharedGitHead'), (receipt.get('harness') or {}).get('gitHead')),
              '', '| Hop | Samples passed | Samples failed | Controls detected / total | Launches (resumed) | Blender seconds | Peak private GiB | Min available GiB | Status |',
              '|---|---|---|---|---|---|---|---|---|']
    for hop, entry in receipt['summary']['hops'].items():
        lines.append('| %s | %d | %d | %d / %d | %d (%d) | %s | %s | %s | %s |' % (
            HOP_TITLES[hop], entry['samplesPassed'], entry['samplesFailed'], entry['controlsDetected'], len(entry['controls']),
            entry['launches'], entry['launchesResumed'], entry['blenderSeconds'], entry['peakPrivateGiB'], entry['minAvailableGiB'],
            'PASS' if entry['passed'] else 'FAIL'))
    lines += ['', '## Controls', '']
    for hop, entry in receipt['summary']['hops'].items():
        for control in entry['controls']:
            state = ('not applicable' if control.get('notApplicable') else
                     {True: 'detected', False: 'NOT DETECTED', None: 'NEVER EXERCISED / masked'}[control['detected']])
            lines.append('- **%s**: %s. %s' % (control['name'], state, control['detail']))
    lines += ['', '## Samples', '']
    for hop, output in receipt['hops'].items():
        for sample in output.get('samples') or []:
            status = 'PASS' if sample.get('passed') else 'FAIL'
            detail = sample.get('detail')
            if detail is None and sample.get('result'):
                failed = [c for c in sample['result']['checks'] if c['status'] == 'fail']
                detail = ('%s: %s: %s' % (failed[0]['name'], (failed[0]['firstMismatch'] or {}).get('where'),
                                          (failed[0]['firstMismatch'] or {}).get('detail'))) if failed else 'all checks passed'
            lines.append('- %s `%s` %s: %s' % (hop, sample.get('unit'), status, detail))
    lines += ['', 'Overall: **%s**' % ('PASS' if receipt['summary']['allPassed'] and not receipt.get('aborted') else 'FAIL'), '']
    return '\n'.join(lines) + '\n'


def parse_arguments(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=('bprime', 'd', 'f', 'all'))
    parser.add_argument('--out', default=os.path.join(REPO, 'TestOutput', 'gate1a-blender-%s' % datetime.datetime.now().strftime('%Y%m%d-%H%M%S')))
    parser.add_argument('--blender', default=bl.DEFAULT_BLENDER)
    parser.add_argument('--allow-blender-version', help='accept this exact version string instead of 5.1.x')
    parser.add_argument('--artifacts', action='append', help='a gate1a-artifacts/1 manifest (repeatable; default: the 66bf702 gate manifest and the go.nif extra)')
    parser.add_argument('--shared', help='the Multitool.Shared root (default: found above this script)')
    parser.add_argument('--limit', type=int, help="per hop, the first N corpus samples after --filter (B' and D)")
    parser.add_argument('--filter', help='substring a corpus sample must contain (id, entry, key, paths)')
    parser.add_argument('--repeats', type=int, default=compare_bprime.REPEATS, help="fresh processes per GLB in hop B' (the gate needs 12)")
    parser.add_argument('--timeout-seconds', type=float, default=900.0)
    parser.add_argument('--render-timeout-seconds', type=float, default=1800.0)
    parser.add_argument('--admission-timeout-seconds', type=float, default=3600.0)
    parser.add_argument('--admission-poll-seconds', type=float, default=5.0)
    parser.add_argument('--poll-seconds', type=float, default=0.25)
    parser.add_argument('--min-free-gib', type=float, default=bl.OWNER_MIN_FREE_BYTES / bl.GIB)
    parser.add_argument('--kill-free-gib', type=float, default=bl.OWNER_KILL_FREE_BYTES / bl.GIB)
    parser.add_argument('--max-private-gib', type=float, default=bl.OWNER_MAX_PRIVATE_BYTES / bl.GIB)
    parser.add_argument('--render-samples', type=int, default=16)
    parser.add_argument('--retry-failed', action='store_true')
    parser.add_argument('--dry-run', action='store_true', help='print every launch the run would make; launch nothing')
    return parser.parse_args(argv)


def main(argv=None, probe=None, foreign_processes=None, sleep=None, clock=None, run_mutex=bl.RUN_MUTEX):
    """Entry point. ``probe``, ``foreign_processes``, ``sleep``, ``clock`` and ``run_mutex`` are injected by the
    self-check only (its own mutex name, so a self-check never waits on, or blocks, a real run)."""
    args = parse_arguments(argv)
    try:
        limits = bl.Limits(args.min_free_gib * bl.GIB, args.kill_free_gib * bl.GIB, args.max_private_gib * bl.GIB,
                           args.timeout_seconds, args.admission_timeout_seconds, args.poll_seconds, args.admission_poll_seconds)
    except bl.LimitError as failure:
        print('refused: %s' % failure, file=sys.stderr)
        return 2
    args.shared_root = find_shared_root(args.shared)
    scripts = {'readback': os.path.join(args.shared_root, 'tools', 'blender', 'readback.py'),
               'importModel': os.path.join(args.shared_root, 'src', 'Slfx77.Multitool.Media.Blender', 'Python', 'import_model.py'),
               'probeRender': os.path.join(HERE, 'inside_blender', 'probe_render.py'),
               'repack': os.path.join(HERE, 'inside_blender', 'repack_dds_control.py')}
    for key, path in scripts.items():
        if not os.path.isfile(path):
            print('missing script %s: %s' % (key, path), file=sys.stderr)
            return 2
    if not os.path.isfile(args.blender):
        print('Blender not found at %s' % args.blender, file=sys.stderr)
        return 2
    os.makedirs(args.out, exist_ok=True)

    def log(message):
        print(message, flush=True)

    manifests, samples = load_samples(args.artifacts or DEFAULT_ARTIFACTS)
    hops = HOPS if args.command == 'all' else (args.command,)
    subset = []
    if args.limit:
        subset.append('limit %d' % args.limit)
    if args.filter:
        subset.append('filter %r' % args.filter)
    if args.repeats != compare_bprime.REPEATS and 'bprime' in hops:
        subset.append('repeats %d (the gate needs %d)' % (args.repeats, compare_bprime.REPEATS))
    if args.dry_run:
        subset.append('dry run')
    blender = {'path': os.path.abspath(args.blender), 'sha256': bl.sha256_file(args.blender), 'fake': bl.is_python_fake(args.blender),
               'version': None}
    receipt = {'schema': 'gate1a-blender-receipt/1', 'started': utc_now(), 'command': args.command, 'argv': sys.argv[1:] if argv is None else argv,
               'out': os.path.abspath(args.out), 'limits': limits.to_dict(), 'blender': blender, 'manifests': manifests,
               'scripts': identity_of_scripts(scripts), 'sharedGitHead': git_head(args.shared_root), 'harness': harness_identity(),
               'options': {k: v for k, v in vars(args).items() if k not in ('artifacts',)}, 'hops': {}, 'aborted': None}
    launcher = bl.Launcher(limits, probe=probe, foreign_processes=foreign_processes, log=log,
                           **({'sleep': sleep} if sleep else {}), **({'clock': clock} if clock else {}))
    store = bl.LaunchStore(os.path.abspath(args.out), blender)
    runner = Runner(args, limits, launcher, store, os.path.abspath(args.blender), scripts, log)
    lock = bl.RunLock(run_mutex)
    hop_outputs = {}
    exit_code = 0
    try:
        if not args.dry_run and not lock.acquire(args.admission_timeout_seconds):
            raise Aborted('another harness run holds %s' % run_mutex)
        version_spec = bl.LaunchSpec('setup', 'blender', 'version', bl.executable_prefix(runner.blender) + ['--version'],
                                     inputs={'blender': runner.blender})
        version_receipt = runner.launch(version_spec)
        if not args.dry_run:
            # The version line comes FIRST and real Blender follows it with kilobytes of build flags, so a tail read
            # misses it; the whole output is small, so read it from the start.
            text = bl.head_text(version_receipt.get('stdout')) or ''
            match = re.search(r'Blender (\d+)\.(\d+)\.(\d+)', text)
            blender['version'] = match.group(0) if match else None
            blender['versionLine'] = text.strip().splitlines()[0] if text.strip() else None
            ok = match is not None and (int(match.group(1)), int(match.group(2))) == REQUIRED_BLENDER
            if not ok and not (args.allow_blender_version and blender['version'] == args.allow_blender_version):
                raise VersionRefused('%s reports %r; Blender 5.1.x is required (--allow-blender-version to override)'
                                     % (runner.blender, blender.get('versionLine')))
        for hop in hops:
            if hop == 'bprime':
                hop_outputs[hop] = run_bprime(runner, *select(samples, 'glb', args.filter, args.limit))
            elif hop == 'd':
                hop_outputs[hop] = run_d(runner, *select(samples, 'package', args.filter, args.limit))
            else:
                hop_outputs[hop] = run_f(runner)
    except VersionRefused as failure:
        receipt['aborted'] = 'refused: %s' % failure
        print('refused: %s' % failure, file=sys.stderr)
        exit_code = 2
    except Aborted as failure:
        receipt['aborted'] = str(failure)
        log('ABORTED: %s' % failure)
        exit_code = 3
    except Exception as failure:  # noqa: BLE001 - the receipt records the harness defect
        receipt['aborted'] = 'harness error: %s: %s' % (type(failure).__name__, failure)
        receipt['traceback'] = traceback.format_exc()
        log(receipt['traceback'])
        exit_code = 1
    finally:
        lock.release()
    receipt['hops'] = hop_outputs
    receipt['summary'] = summarize(hop_outputs, runner, '; '.join(subset))
    receipt['launches'] = runner.launches
    receipt['finished'] = utc_now()
    with open(os.path.join(args.out, 'receipt.json'), 'w', encoding='utf-8') as f:
        json.dump(receipt, f, indent=1, default=str)
    with open(os.path.join(args.out, 'receipt.md'), 'w', encoding='utf-8') as f:
        f.write(markdown(receipt))
    log('receipt: %s' % os.path.join(args.out, 'receipt.md'))
    if args.dry_run:
        log('dry run: %d launch(es) planned' % len(runner.launches))
        return 0
    if exit_code:
        return exit_code
    return 0 if receipt['summary']['allPassed'] else 1


if __name__ == '__main__':
    sys.exit(main())
