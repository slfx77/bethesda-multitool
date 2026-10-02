# SPDX-License-Identifier: 0BSD
"""Hop B': each sampled GLB imports through Blender 5.1's glTF importer in N (12) FRESH Blender processes.

One launch is ``blender --background --factory-startup --python-exit-code 1 --python readback.py -- --import-glb <glb>
--out <dump>``. A launch passes when Blender exits 0, the readback dump exists with schema
``multitool.blender-readback/1`` and mode ``glb``, and the import produced at least one mesh object whenever the GLB draws
a mesh (readback.py does not check the operator's return value, so an importer that reports an error and returns
CANCELLED would otherwise leave an empty scene and exit 0). A sample passes when all N launches pass AND their dumps are
identical (canonical JSON SHA-256): the defect this hop guards against depends on Python's per-process hash
randomization, and its silent form (two custom attributes of equal width exchanging their data) changes the dump without
failing the import, so a disagreement between fresh processes is a failure.

Launches the HARNESS ended (a timeout or a watchdog kill) fail a pristine sample like any other failed launch, but never
count toward a control's detection: only the importer failing, or dumps disagreeing, detects the control.

Control: a GLB carrying a VEC4 and a VEC3 underscore custom attribute (``probe_builders.build_custom_attribute_glb``)
must fail in at least one of 12 processes. NeversoftMultitool measured 5 of 12 on Blender 5.1; if each process fails
independently with probability 5/12, all 12 pass with probability (7/12)^12, about 0.15%.

The control counts only failures that ARE the set-order defect: the importer's traceback must carry numpy's
concatenation error (``DEFECT_SIGNATURE``; io_scene_gltf2 ``blender/imp/mesh.py`` line 273 concatenates the VEC3 array
onto the VEC4 one when the hash-ordered set iterates the names in the other order). It is judged INCONCLUSIVE (None,
which fails the hop) when any judged launch failed WITHOUT that signature (a malformed control, a readback regression,
an add-on that refuses the attributes outright), when the passing launches' dumps disagree (not this defect's
behavior for two attributes of different widths), or when the defect fired in EVERY judged launch: the defect is
intermittent by construction (5 of 12 measured; 12 of 12 by chance is about (5/12)^12, 3e-5), so a deterministic
failure means the processes are not independent (hash randomization is off) or the importer changed, and either way
the twelve-process guard for the real samples has lost its power.
"""

import hashlib
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from glb_reader import Glb  # noqa: E402
import blender_launcher as launcher  # noqa: E402

READBACK_SCHEMA = 'multitool.blender-readback/1'
REPEATS = 12
# numpy's error for np.concatenate((attribute_data[idx], attr_data)) with a VEC3 array onto a VEC4 one. The text is
# compiled into the numpy Blender 5.1 bundles (2.3.4, numpy/_core/_multiarray_umath, and pinned by its own
# test_shape_base.py); the second alternative is its dimension clause, kept in case the first is truncated.
DEFECT_SIGNATURE = re.compile(r'input array dimensions except for the concatenation axis must match exactly'
                              r'|along dimension 1, the array at index \d+ has size \d+')


def launch_stderr(receipt):
    """The launch's whole stderr (the receipt's log file), else its recorded tail."""
    path = (receipt or {}).get('stderr')
    if path and os.path.isfile(path):
        with open(path, 'rb') as f:
            return f.read().decode('utf-8', 'replace')
    return (receipt or {}).get('stderrTail') or ''


def glb_mesh_nodes(glb_path):
    """How many nodes of the default scene (every node when the GLB names no scene) instance a mesh with primitives."""
    with open(glb_path, 'rb') as f:
        glb = Glb(f.read())
    document = glb.json
    nodes = document.get('nodes') or []
    meshes = document.get('meshes') or []
    scenes = document.get('scenes') or []
    if scenes:
        stack = list(scenes[document.get('scene', 0)].get('nodes') or [])
        reachable = set()
        while stack:
            index = stack.pop()
            if index in reachable:
                continue
            reachable.add(index)
            stack.extend(nodes[index].get('children') or [])
    else:
        reachable = set(range(len(nodes)))
    return sum(1 for index in reachable if nodes[index].get('mesh') is not None and meshes[nodes[index]['mesh']].get('primitives'))


def canonical_sha256(dump):
    return hashlib.sha256(json.dumps(dump, sort_keys=True, separators=(',', ':')).encode('utf-8')).hexdigest()


def judge_launch(receipt, dump_path, mesh_nodes):
    """One launch: {name, outcome, exitCode, passed, harnessEnded, importerFailed, reason, dumpSha256, meshObjects}."""
    run = (receipt or {}).get('run') or {}
    entry = {'name': (receipt or {}).get('name'), 'outcome': run.get('outcome'), 'exitCode': run.get('exitCode'),
             'seconds': run.get('seconds'), 'peakPrivateGiB': run.get('peakPrivateGiB'), 'passed': False,
             'harnessEnded': launcher.launch_failed_by_harness(receipt), 'importerFailed': False, 'reason': None,
             'dumpSha256': None, 'meshObjects': None, 'resumed': (receipt or {}).get('resumed'), 'defectSignature': False}
    if entry['harnessEnded']:
        entry['reason'] = 'the harness ended the launch: %s' % run.get('outcome')
        return entry, None
    if run.get('outcome') != launcher.OUTCOME_OK:
        entry['importerFailed'] = True
        stderr = launch_stderr(receipt)
        match = DEFECT_SIGNATURE.search(stderr)
        entry['defectSignature'] = match is not None
        tail = stderr.strip().splitlines()
        entry['reason'] = 'Blender exited %s%s' % (run.get('exitCode'), (': ' + (match.group(0) if match else tail[-1])) if (match or tail) else '')
        return entry, None
    if not dump_path or not os.path.isfile(dump_path):
        entry['importerFailed'] = True
        entry['reason'] = 'exit 0 but no readback dump'
        return entry, None
    try:
        with open(dump_path, 'r', encoding='utf-8') as f:
            dump = json.load(f)
    except ValueError as failure:
        entry['importerFailed'] = True
        entry['reason'] = 'the readback dump is not JSON: %s' % failure
        return entry, None
    entry['dumpSha256'] = canonical_sha256(dump)
    entry['meshObjects'] = sum(1 for obj in dump.get('objects') or [] if obj.get('type') == 'MESH')
    if dump.get('schema') != READBACK_SCHEMA or dump.get('mode') != 'glb':
        entry['importerFailed'] = True
        entry['reason'] = 'dump schema %r mode %r' % (dump.get('schema'), dump.get('mode'))
        return entry, dump
    if mesh_nodes and not entry['meshObjects']:
        entry['importerFailed'] = True
        entry['reason'] = 'the GLB draws %d mesh node(s) but the import produced no mesh object' % mesh_nodes
        return entry, dump
    entry['passed'] = True
    return entry, dump


def differing_keys(first, other):
    keys = sorted(set(first) | set(other))
    return [key for key in keys if first.get(key) != other.get(key)]


def judge_sample(judged, repeats):
    """(passed, detail, data) over the launches of one GLB. ``judged`` is a list of (entry, dump)."""
    entries = [entry for entry, _ in judged]
    dumps = [dump for entry, dump in judged if entry['passed']]
    hashes = sorted({entry['dumpSha256'] for entry in entries if entry['passed']})
    differences = []
    if len(hashes) > 1:
        first = dumps[0]
        for dump, entry in zip(dumps[1:], [e for e in entries if e['passed']][1:]):
            if canonical_sha256(dump) != entries[0]['dumpSha256']:
                differences.append({'launch': entry['name'], 'topLevelKeysDiffering': differing_keys(first, dump)})
    passed_count = sum(1 for entry in entries if entry['passed'])
    data = {'launches': len(entries), 'required': repeats, 'passedLaunches': passed_count,
            'importerFailures': sum(1 for e in entries if e['importerFailed']),
            'harnessEnded': sum(1 for e in entries if e['harnessEnded']), 'distinctDumps': len(hashes),
            'dumpDifferences': differences[:5], 'meshObjects': sorted({e['meshObjects'] for e in entries if e['meshObjects'] is not None}),
            'secondsTotal': round(sum(e['seconds'] or 0.0 for e in entries), 3),
            'peakPrivateGiB': max([e['peakPrivateGiB'] or 0.0 for e in entries] or [0.0])}
    if len(entries) < repeats:
        return False, '%d of %d launches ran' % (len(entries), repeats), data
    failed = [e for e in entries if not e['passed']]
    if failed:
        return False, '%d of %d launches failed; first %s: %s' % (len(failed), len(entries), failed[0]['name'], failed[0]['reason']), data
    if len(hashes) > 1:
        return False, 'the %d fresh processes produced %d different dumps (%s)' % (len(entries), len(hashes), differences[:1]), data
    return True, '%d of %d fresh processes imported it with identical dumps' % (len(entries), len(entries)), data


def judge_control(judged, repeats):
    """(detected True / False / None, detail, data) for the VEC4 + VEC3 control.

    Detected: at least one judged launch failed WITH the set-order defect's signature, not every judged launch did, no
    judged launch failed without it, and the passing launches agree. None (inconclusive, the hop fails): a failure
    without the signature, disagreeing dumps, the defect in every judged launch, or (with no failure at all) fewer
    than ``repeats`` judged launches. False: every one of ``repeats`` judged launches imported it with identical dumps.
    Harness-ended launches (timeout, watchdog) are never judged."""
    entries = [entry for entry, _ in judged]
    judged_entries = [e for e in entries if not e['harnessEnded']]
    importer_failures = [e for e in judged_entries if e['importerFailed']]
    signature = [e for e in importer_failures if e.get('defectSignature')]
    unrelated = [e for e in importer_failures if not e.get('defectSignature')]
    hashes = {e['dumpSha256'] for e in judged_entries if e['passed']}
    judged_count = len(judged_entries)
    data = {'launches': len(entries), 'judgedLaunches': judged_count, 'importerFailures': len(importer_failures),
            'defectFailures': len(signature), 'unrelatedFailures': len(unrelated), 'distinctDumps': len(hashes),
            'failedLaunches': [e['name'] for e in importer_failures],
            'firstFailure': importer_failures[0]['reason'] if importer_failures else None}
    if unrelated:
        return None, ('INCONCLUSIVE: %d of %d judged launch(es) failed WITHOUT the set-order signature (first %s: %s); the '
                      'control must fail by the defect it guards against' % (len(unrelated), judged_count, unrelated[0]['name'],
                                                                             unrelated[0]['reason'])), data
    if len(hashes) > 1:
        return None, ('INCONCLUSIVE: the %d passing launches produced %d different dumps, which is not how the defect '
                      'behaves for attributes of different widths' % (judged_count - len(importer_failures), len(hashes))), data
    if signature:
        if len(signature) == judged_count:
            return None, ('INCONCLUSIVE: the set-order defect fired in every one of the %d judged processes; it is intermittent '
                          'by construction (NMT 5 of 12), so the processes are not independent (hash randomization off?) '
                          'or the importer changed' % judged_count), data
        return True, 'detected: the set-order defect (%s) fired in %d of %d judged processes' % (
            signature[0]['reason'], len(signature), judged_count), data
    if judged_count < repeats:
        return None, 'underpowered: only %d of %d launches were judged (the rest were ended by the harness)' % (judged_count, repeats), data
    return False, 'NOT detected: all %d fresh processes imported the VEC4 + VEC3 control with identical dumps' % judged_count, data
