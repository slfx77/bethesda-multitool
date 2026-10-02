# SPDX-License-Identifier: 0BSD
"""Hop E-anim: writer versus writer over the animation (GLB against the Blender package; gate 1b, design section 7.2
row E), a consistency check.

Clips pair through the GLB's ``multitoolAnimationMetadata`` ``sourceAnimation`` and the package's ``sourceIndex``. Each
GLB channel is paired with the package channels that drive the same node property: Translation, Scale, a quaternion
Rotation or the three EulerRotation axes (composed as qZ * qY * qX, an axis without a channel at the package node's
rest Euler angle), MorphWeights (the morph-track channel, then the morph-target channels, over the rest weights),
NodeVisibility, and material properties through their pointers (only where hop B-anim's identity-projection condition
holds). Both are evaluated by this harness's own samplers (``anim_glb``, ``anim_package``) at the union of the two
writers' key times, their quarter points and a uniform grid inside the shorter of the two playback windows, and must
agree within the SUM of the two stated certificates (the GLB rows of hop B-anim, the package rows of hop C-anim, each
converted to the GLB channel's unit) plus both evaluation slacks. A property one writer animates and the other does
not is compared against the other writer's rest value.

Controls: one GLB sampler's output shifted by one key in a copy of the GLB only (``hop_b_anim.mutate_key_shift``), and
one package cubic span flipped to LINEAR in a copy of the package only (``hop_c_anim.mutate_segment_flip``); each must be
reported at the GLB channel it perturbs (for the package control, the GLB channel paired with the flipped channel).
"""

import math
import os
import tempfile
import traceback

import numpy as np

import anim_compare as ac
import anim_dump
import anim_glb
import anim_package
import hop_b_anim
import hop_c_anim
from dump_reader import Dump
from gate1a_common import HopResult, Mismatch, OracleError, sha256_bytes, sha256_file
from glb_reader import Glb
from hop_b import GlbDumpPairing
from hop_e import GlbPackagePairing
from package_reader import Package

F32 = np.float32

CHECK_CLIPS = 'writer clips: GLB animations paired with packaged clips'
CHECK_CHANNELS = 'writer channels: GLB against package within the sum of both certificates'
PATH_PROPERTY = {'translation': 'Translation', 'scale': 'Scale'}


def quaternion_from_euler(angles):
    """qZ * qY * qX (Hamilton, X, Y, Z, W) of three XYZ axis angles, in double."""
    half = [0.5 * float(a) for a in angles]
    qx = (math.sin(half[0]), 0.0, 0.0, math.cos(half[0]))
    qy = (0.0, math.sin(half[1]), 0.0, math.cos(half[1]))
    qz = (0.0, 0.0, math.sin(half[2]), math.cos(half[2]))
    return np.asarray(hamilton(hamilton(qz, qy), qx), dtype=np.float64)


def hamilton(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def euler_from_quaternion(q):
    """The XYZ angles whose qZ * qY * qX is q (Blender's ``to_euler('XYZ')`` convention: R = Rz Ry Rx)."""
    x, y, z, w = (float(c) for c in q)
    norm = math.sqrt(x * x + y * y + z * z + w * w)
    x, y, z, w = x / norm, y / norm, z / norm, w / norm
    r00 = 1 - 2 * (y * y + z * z)
    r10 = 2 * (x * y + z * w)
    r20 = 2 * (x * z - y * w)
    r21 = 2 * (y * z + x * w)
    r22 = 1 - 2 * (x * x + y * y)
    ay = math.asin(max(-1.0, min(1.0, -r20)))
    ax = math.atan2(r21, r22)
    az = math.atan2(r10, r00)
    return [ax, ay, az]


class PackageSide:
    """The package channels of one clip, resolved to the GLB's pose keys."""

    def __init__(self, clip, animations, table):
        self.clip = clip
        self.animations = animations
        self.table = table

    def channels(self, predicate):
        return [c for c in self.clip['channels'] if predicate(c)]

    def absolute_bound(self, channel, unit, factor=1.0):
        """A package channel's certificate in the GLB channel's unit (ratio x denominator, radians -> degrees for Euler
        axes, Euclidean meters for translation)."""
        bound = hop_c_anim.channel_bound(self.table, channel)
        if bound.unit in ('degrees', 'boolean'):
            return ac.Bound(unit, bound.observed, bound.maximum, bound.rows, bound.note)
        denominators = hop_c_anim.component_denominators(channel)
        if unit == 'meters':
            scale = math.sqrt(float(np.sum(denominators * denominators))) * factor
        else:
            scale = float(np.max(denominators))
        if unit == 'degrees':
            scale = math.degrees(scale)
        return ac.Bound(unit, bound.observed * scale, bound.maximum * scale, bound.rows, bound.note)

    def resolve(self, key, glb_rest, factor=1.0):
        """(evaluate(t) -> value, bound, package labels) for a GLB pose key; the rest value when no channel drives it."""
        kind = key[0]
        if kind == 'node' and key[2] in PATH_PROPERTY:
            prop = PATH_PROPERTY[key[2]]
            found = self.channels(lambda c: c.node == key[1] and c.property == prop and c.target is None)
            if not found:
                return self._rest(key), ac.Bound('meters' if prop == 'Translation' else 'ratio'), []
            channel = found[-1]
            unit = 'meters' if prop == 'Translation' else 'ratio'
            return channel.sample, self.absolute_bound(channel, unit, factor), [channel.label]
        if kind == 'node' and key[2] == 'rotation':
            quaternions = self.channels(lambda c: c.node == key[1] and c.property == 'Rotation')
            if quaternions:
                channel = quaternions[-1]
                return channel.sample, self.absolute_bound(channel, 'degrees'), [channel.label]
            axes = self.channels(lambda c: c.node == key[1] and c.property == 'EulerRotation')
            if not axes:
                return self._rest(key), ac.Bound('degrees'), []
            rest = euler_from_quaternion(self.animations.node_rest(key[1], 'Rotation'))
            by_axis = {c.offset: c for c in axes}
            bound = ac.Bound('degrees')
            for channel in by_axis.values():
                bound = bound.plus(self.absolute_bound(channel, 'degrees'))

            def euler(t, by_axis=by_axis, rest=rest):
                angles = [float(by_axis[a].sample(t)[0]) if a in by_axis else rest[a] for a in range(3)]
                return quaternion_from_euler(angles)
            return euler, bound, [c.label for c in by_axis.values()]
        if kind == 'node' and key[2] == 'weights':
            morph = self.channels(lambda c: c.node == key[1] and c.property == 'MorphWeights' and c.domain == 'morph-tracks')
            targets = self.channels(lambda c: c.node == key[1] and c.property == 'MorphWeights' and c.domain != 'morph-tracks')
            if not morph and not targets:
                return self._rest(key), ac.Bound('ratio'), []
            bound = ac.Bound('ratio')
            for channel in morph + targets:
                single = self.absolute_bound(channel, 'ratio')
                bound = ac.Bound('ratio', max(bound.observed, single.observed), max(bound.maximum, single.maximum),
                                 bound.rows + single.rows)
            rest = np.asarray(glb_rest, dtype=np.float64)

            def weights(t, morph=morph, targets=targets, rest=rest):
                value = rest.copy()
                for channel in morph + targets:
                    sample = channel.sample(t)
                    value[channel.offset:channel.offset + channel.width] = sample
                return value
            return weights, bound, [c.label for c in morph + targets]
        if kind == 'visibility':
            found = self.channels(lambda c: c.property == 'NodeVisibility' and c.node == key[1])
            if not found:
                return (lambda t: np.array([1.0])), ac.Bound('boolean'), []
            return found[-1].sample, ac.Bound('boolean'), [found[-1].label]
        raise OracleError('no package resolution for %r' % (key,))

    def _rest(self, key):
        if key[0] == 'node' and key[2] in ('translation', 'scale', 'rotation'):
            value = self.animations.node_rest(key[1], {'translation': 'Translation', 'scale': 'Scale', 'rotation': 'Rotation'}[key[2]])
            return lambda t, v=value: v
        raise OracleError('no package rest for %r' % (key,))


def material_pointer_side(channel, side, glb, dump_anim, pkg_pairing, glb_dump_pairing):
    """(evaluate, bound, labels) for a material-factor or texture-transform pointer, from the package property channels,
    or a reason string when E does not compare it."""
    target = channel.target
    glb_material = target[1]
    if glb_material not in pkg_pairing.material_map or glb_dump_pairing is None or glb_material not in glb_dump_pairing.material_map:
        return None, None, None, 'GLB material %d has no paired package or dump material' % glb_material
    pkg_material = pkg_pairing.material_map[glb_material]
    dump_material = glb_dump_pairing.material_map[glb_material]
    dmat = dump_anim.dump.materials[dump_material]
    if target[0] == 'material-factor':
        name = target[2]
        layout = hop_b_anim.MATERIAL_FACTOR_KINDS[name]
        rest = [float(v) for v in hop_b_anim.glb_material_static(glb, glb_material, name)]
        slots = []
        for position, (kind, component) in enumerate(layout):
            found = side.channels(lambda c, kind=kind: c.target is not None and c.property == kind and int(c.target.get('index')) == pkg_material)
            if found:
                # Hop B-anim's identity-projection condition: the writer's portable rest equals the source rest.
                portable = float(F32(hop_b_anim.portable_factor(dmat, kind, component)))
                source_rest = float(dump_anim.material_rest(dump_material, kind)[component])
                if portable != source_rest:
                    return None, None, None, ('%s component %d: portable rest %r, source rest %r; the layer projection is not reproduced'
                                              % (kind, component, portable, source_rest))
                slots.append((position, found[-1], component))
    else:
        _, _, role, member, width = target
        layers = dmat.get('layers') or []
        matches = [i for i, layer in enumerate(layers) if layer.get('role') == role]
        if len(matches) != 1:
            return None, None, None, '%d source layers carry role %s' % (len(matches), role)
        rest = [float(v) for v in hop_b_anim.glb_texture_transform_static(glb, glb_material, role, member)]
        slots = []
        for position, kind in enumerate(hop_b_anim.TEXTURE_MEMBER_KINDS[member]):
            found = side.channels(lambda c, kind=kind: c.target is not None and c.property == kind and int(c.target.get('index')) == pkg_material
                                  and int(c.target.get('layerIndex')) == matches[0])
            if found:
                slots.append((position, found[-1], 0))
    bound = ac.Bound('component')
    for _, pchannel, _ in slots:
        single = side.absolute_bound(pchannel, 'component')
        bound = ac.Bound('component', max(bound.observed, single.observed), max(bound.maximum, single.maximum), bound.rows + single.rows)

    def evaluate(t, slots=slots, rest=rest):
        values = list(rest)
        for position, pchannel, component in slots:
            values[position] = float(pchannel.sample(t)[component])
        return np.asarray(values, dtype=np.float64)
    return evaluate, bound, [s[1].label for s in slots], None


def compare_writers(result, glb, package, dump, sample_id, context, glb_rows_list):
    """Checks 1-2. Returns (records, pairs, package records, package pairs, package label -> GLB label)."""
    glb_anims = anim_glb.GlbAnimations(glb)
    pkg_anims = anim_package.PackageAnimations(package)
    dump_anim = anim_dump.DumpAnimation(dump)
    pkg_table = hop_c_anim.package_rows_table(pkg_anims)
    glb_table = ac.rows_by_clip(glb_rows_list)
    factor = hop_b_anim.factor_of(dump_anim)
    pkg_pairing = GlbPackagePairing(glb, package)
    try:
        glb_dump_pairing = GlbDumpPairing(glb, dump)
    except OracleError:
        glb_dump_pairing = None
    clips_check = result.check(CHECK_CLIPS)
    by_source = {int(c['sourceIndex']): c for c in pkg_anims.clips if c['sourceIndex'] is not None}
    pairs = []
    pkg_pairs = []
    for animation in glb_anims.animations:
        meta = glb_anims.clip_metadata(animation['index'])
        if meta is None or meta.get('sourceAnimation') is None:
            clips_check.fail(Mismatch('animations/%d' % animation['index'], 'pairing', 'no metadata names the source clip'))
            continue
        source = int(meta['sourceAnimation'])
        pclip = by_source.get(source)
        if pclip is None:
            clips_check.fail(Mismatch('animations/%d (%s)' % (animation['index'], animation['name']), 'pairing',
                                      'source clip %d is in the GLB but not in the package' % source))
            continue
        glb_window = hop_b_anim.clip_window(meta, None, animation['channels'])
        window = min(glb_window, float(pclip['duration']))
        pairs.append((animation, source, window, meta, pclip))
        pkg_pairs.append((pclip, source, float(pclip['duration'])))
    missing = sorted(set(by_source) - {p[1] for p in pairs})
    clips_check.data['packagedOnly'] = missing
    if missing:
        clips_check.fail(Mismatch('animations', 'pairing', 'source clips %r are packaged but not in the GLB' % missing))
    if clips_check.passed is None:
        clips_check.ok('%d clips in both writers' % len(pairs))
    check = result.check(CHECK_CHANNELS)
    records = []
    pkg_records = {}
    pkg_to_glb = {}
    used_pkg = set()
    for animation, source, window, meta, pclip in pairs:
        side = PackageSide(pclip, pkg_anims, pkg_table.get(source, {}))
        dclip = dump_anim.clips[source]
        for channel in animation['channels']:
            record = ac.ChannelRecord(channel.label, channel.target, channel.interpolation, int(len(channel.times)))
            target = channel.target
            try:
                if target[0] == 'node':
                    if channel.node >= len(dump_anim.dump.nodes):
                        record.reason = 'writer-added node'
                        records.append(record)
                        continue
                    key = ('node', channel.node, channel.path)
                    glb_rest = glb_anims.node_rest(channel.node, channel.path)
                    evaluate, pbound, labels = side.resolve(key, glb_rest, factor)
                    gbound = hop_b_anim.key_bound(glb_table.get(source, {}), dclip, key, hop_b_anim.UNIT_BY_PATH[channel.path])
                    measure = ac.value_kind(key)
                elif target[0] == 'visibility':
                    key = ('visibility', target[1])
                    evaluate, pbound, labels = side.resolve(key, None)
                    gbound = ac.Bound('boolean')
                    measure = 'boolean'
                elif target[0] in ('material-factor', 'texture-transform'):
                    evaluate, pbound, labels, reason = material_pointer_side(channel, side, glb, dump_anim, pkg_pairing, glb_dump_pairing)
                    if reason:
                        record.reason = reason
                        records.append(record)
                        continue
                    resolved = hop_b_anim.resolve_target(channel, glb, glb_anims, dump_anim, source, glb_dump_pairing)
                    gbound = ac.Bound('component')
                    for rkey in resolved.keys:
                        single = hop_b_anim.key_bound(glb_table.get(source, {}), dclip, rkey, 'component')
                        gbound = ac.Bound('component', max(gbound.observed, single.observed), max(gbound.maximum, single.maximum), gbound.rows + single.rows)
                    measure = 'components'
                else:
                    record.reason = 'pointer %r is not compared' % channel.pointer
                    records.append(record)
                    continue
            except OracleError as failure:
                record.reason = str(failure)
                records.append(record)
                continue
            bound = ac.Bound(gbound.unit, gbound.observed + pbound.observed, gbound.maximum + pbound.maximum,
                             ['GLB ' + r for r in gbound.rows] + ['package ' + r for r in pbound.rows])
            package_times = []
            for label in labels:
                used_pkg.add(label)
                pkg_to_glb[label] = channel.label
                pchannel = next(c for c in pclip['channels'] if c.label == label)
                package_times.extend(float(t) for t in pchannel.times)
                prec = ac.ChannelRecord(label, (pchannel.domain, pchannel.track), pchannel.interpolation, int(len(pchannel.times)))
                prec.compared = True
                prec.bound = hop_c_anim.channel_bound(side.table, pchannel)
                pkg_records[label] = prec
            times = ac.sample_times(sorted(set(float(t) for t in channel.times) | set(package_times)), window)
            ok = ac.compare_series(record, times, lambda t, channel=channel: channel.sample(t),
                                   lambda t, evaluate=evaluate: (np.asarray(evaluate(t), dtype=np.float64), 1.0),
                                   measure, bound, factor if measure == 'translation' else 1.0, dict(context, clip=animation['name']))
            record.keys = labels
            if not ok:
                check.fail(Mismatch(channel.label, 'value', 'GLB against package %s: %s' % (labels or ['(rest)'], ac.mismatch_detail(record))))
            records.append(record)
    unmatched = []
    for animation, source, window, meta, pclip in pairs:
        for pchannel in pclip['channels']:
            if pchannel.label not in used_pkg:
                unmatched.append(pchannel.label)
    check.data['packageChannelsWithoutGlbChannel'] = unmatched
    check.data['channelsCompared'] = sum(1 for r in records if r.compared)
    check.data['channelsNotCompared'] = [r.to_dict() for r in records if not r.compared]
    check.data['samplesCompared'] = sum(r.samples for r in records)
    check.data['samplesSkipped'] = sum(sum(r.skipped.values()) for r in records)
    check.data['worstRatioToCertificate'] = max([r.worst_ratio for r in records if r.compared] or [0.0])
    check.data['channels'] = [r.to_dict() for r in records]
    if unmatched:
        check.data['packageOnlyNote'] = ('package channels whose property the GLB does not animate (the GLB rest holds): compared '
                                         'against the GLB rest by hop C-anim coverage only when the dump drives them')
    if check.passed is None:
        if not records or not any(r.compared for r in records):
            check.skip('no channel compared')
        else:
            check.ok('%d GLB channels agree with the package within the summed certificates (%d samples, %d skipped; worst %.3g x the sum)'
                     % (check.data['channelsCompared'], check.data['samplesCompared'], check.data['samplesSkipped'],
                        check.data['worstRatioToCertificate']))
    return records, [(p[0], p[1], p[2], p[3]) for p in pairs], list(pkg_records.values()), pkg_pairs, pkg_to_glb, glb_anims, pkg_anims


CONTROL_GLB_KEY_SHIFT = 'GLB sampler output shifted by one key in a copy of the GLB only'
CONTROL_PACKAGE_SEGMENT = 'package cubic span flipped to LINEAR in a copy of the package only'
CONTROL_ORDER = (CONTROL_GLB_KEY_SHIFT, CONTROL_PACKAGE_SEGMENT)


def run_controls(sample, glb_bytes, package_path, dump, pristine, state, rows_list, context, work_dir):
    records, pairs, pkg_records, pkg_pairs, pkg_to_glb, glb_anims, pkg_anims = state
    controls = []
    os.makedirs(work_dir, exist_ok=True)
    # 1: GLB only.
    control = {'name': CONTROL_GLB_KEY_SHIFT, 'detected': None, 'detail': None}
    mutated, description = hop_b_anim.mutate_key_shift(glb_bytes, glb_anims, pairs, records)
    if mutated is None:
        control['detail'] = 'not applicable: no compared channel has two keys whose shift is visible'
    else:
        probe = HopResult('E-anim-control', sample['id'])
        package = Package(package_path)
        try:
            compare_writers(probe, Glb(mutated), package, dump, sample['id'], context, rows_list)
        finally:
            package.close()
        detected, verdict = hop_b_anim.control_verdict(probe, pristine, description['where'], 'value')
        control.update({'detected': detected, 'detail': '%s: %s' % (description['description'], verdict),
                        'expectedMismatchWhere': description['where']})
        path = os.path.join(work_dir, 'control-key-shift.glb')
        with open(path, 'wb') as f:
            f.write(mutated)
        control['files'] = {'glb': path, 'glbSha256': sha256_bytes(mutated)}
    controls.append(control)
    # 2: package only.
    control = {'name': CONTROL_PACKAGE_SEGMENT, 'detected': None, 'detail': None}
    path = os.path.join(work_dir, 'control-segment-flip.zip')
    description = hop_c_anim.mutate_segment_flip(package_path, path, pkg_anims, pkg_records, pkg_pairs)
    if description is None:
        control['detail'] = 'not applicable: no paired package CubicSpline span whose LINEAR replacement is visible'
    else:
        where = pkg_to_glb.get(description['where'])
        probe = HopResult('E-anim-control', sample['id'])
        package = Package(path)
        try:
            compare_writers(probe, Glb(glb_bytes), package, dump, sample['id'], context, rows_list)
        finally:
            package.close()
        detected, verdict = hop_b_anim.control_verdict(probe, pristine, where, 'value')
        control.update({'detected': detected, 'detail': '%s (paired with %s): %s' % (description['description'], where, verdict),
                        'expectedMismatchWhere': where, 'files': {'package': path, 'packageSha256': sha256_file(path)}})
    controls.append(control)
    return controls


def run(sample, work_dir):
    result = HopResult('E-anim', sample['id'])
    try:
        if not sample.get('package') or not sample.get('glb') or not sample.get('dump'):
            result.check('GLB animation versus package').skip('GLB, package or dump not supplied')
            return result
        with open(sample['glb'], 'rb') as f:
            glb_bytes = f.read()
        glb = Glb(glb_bytes)
        package = Package(sample['package'])
        dump = Dump(sample['dump'])
        if not glb.json.get('animations') or not package.manifest.get('animations'):
            result.check('GLB animation versus package').skip('%s carries no animations' % (
                'the GLB' if not glb.json.get('animations') else 'the package'))
            package.close()
            return result
        rows_list, _ = hop_b_anim.glb_rows(sample, glb)
        context = {'dontSample': ac.load_dont_sample(), 'sample': sample['id']}
        state = compare_writers(result, glb, package, dump, sample['id'], context, rows_list)
        package.close()
        result.controls.extend(run_controls(sample, glb_bytes, sample['package'], dump, result, state, rows_list, context, work_dir))
    except OracleError as failure:
        result.error = str(failure)
    except Exception as failure:  # noqa: BLE001 - an oracle defect must reach the receipt, not abort the run
        result.error = 'oracle exception %s: %s' % (type(failure).__name__, failure)
        result.traceback = traceback.format_exc()
    return result


def synthetic_control_exercise(control_name, work_dir=None, builder=None):
    if control_name not in CONTROL_ORDER:
        return None
    import gate1b_synthetic
    work_dir = work_dir or tempfile.mkdtemp(prefix='gate1b-synthetic-e-')
    artifacts = (builder or gate1b_synthetic.write_pristine_set)(work_dir)
    sample = {'id': 'synthetic', 'glb': artifacts['glb'], 'dump': artifacts['dump'], 'fidelity': artifacts['fidelity'],
              'package': artifacts['package']}
    with open(artifacts['glb'], 'rb') as f:
        glb_bytes = f.read()
    dump = Dump(artifacts['dump'])
    glb = Glb(glb_bytes)
    rows_list, _ = hop_b_anim.glb_rows(sample, glb)
    context = {'dontSample': [], 'sample': 'synthetic'}
    pristine = HopResult('E-anim-synthetic', 'synthetic')
    package = Package(artifacts['package'])
    try:
        state = compare_writers(pristine, glb, package, dump, 'synthetic', context, rows_list)
    finally:
        package.close()
    controls = run_controls(sample, glb_bytes, artifacts['package'], dump, pristine, state, rows_list, context, work_dir)
    control = next(c for c in controls if c['name'] == control_name)
    failed = [c for c in pristine.checks if c.passed is False]
    pristine_passed = not failed
    verdict = control.get('detected')
    return {
        'control': control_name, 'detected': verdict is True and pristine_passed, 'verdict': verdict,
        'pristinePassed': pristine_passed,
        'detail': '%s (synthetic set; %s)' % (control.get('detail'), 'pristine checks passed' if pristine_passed else
                                              'pristine %s FAILED, so the exercise reads NOT detected' % ', '.join(c.name for c in failed)),
        'expectedMismatchWhere': control.get('expectedMismatchWhere'),
        'artifactSha256': dict(artifacts['sha256'], mutated=(control.get('files') or {}).get('glbSha256') or (control.get('files') or {}).get('packageSha256')),
        'pristineChecks': [{'name': c.name, 'status': c.to_dict()['status'], 'detail': c.detail,
                            'firstMismatch': c.mismatches[0].to_dict() if c.mismatches else None} for c in pristine.checks],
        'files': dict(artifacts.get('files') or {}, mutated=(control.get('files') or {}).get('glb') or (control.get('files') or {}).get('package')),
        'workDir': work_dir,
    }
