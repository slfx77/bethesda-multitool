# SPDX-License-Identifier: 0BSD
"""Hop B-anim: document (exact JSON dump) -> GLB animation (gate 1b; design section 7.2 row B over the animation).

Every GLB animation is paired with its source clip through the writer's ``multitoolAnimationMetadata``
(``sourceAnimation`` -> ``outputAnimation``), read by the independent sampler of ``anim_glb`` (STEP, LINEAR with the
specification's shortest-path slerp for rotations, CUBICSPLINE with the tangents multiplied by the key interval, morph
weights and KHR_animation_pointer targets) and compared, at the sample times of ``anim_compare``, with the dump's pose
evaluated by ``anim_dump`` through ``nif_curve_eval`` (clip clock, then track clock, Float32 between them). A channel is
compared at the POSE level: the GLB channel on (node, path) against the dump's effective value of that node property
after every track that drives it, in ScenePoseEvaluator's order, so the writer's track-to-channel mapping (an Euler
track becoming the node's rotation, morph and morph-target tracks packed into one weights channel) is not assumed.

Checks:
  1. Clips: every GLB animation names its source clip; a clip clock's playback window equals the window this oracle
     derives from the clock (one period: the Clamp reach, one Loop interval, out and back for Reverse), within one
     Float32 step; every channel's key times lie in [0, window].
  2. Channels: each channel within its certificate (the GLB fidelity rows' ``clock/value`` bound of every source track
     that drives it, plus ``morph-target-nodes/<n>/packing`` for weights) plus the evaluation slack, inside the playback
     window and away from discontinuities (``anim_compare``); the receipt records every channel's worst deviation, its
     ratio to the certificate and to the design limit, the samples compared and those skipped with their reasons.
  3. Coverage: every dump property a clip drives has a channel, or holds its GLB rest value over the window; every
     channel maps to a driven property; tracks the oracle cannot evaluate (matrix tracks, transform bases, the Xbox 360
     Squad estimate, a material factor whose layer projection is not the identity) are listed as not compared with
     the reason, never as passes.

The GLB is judged by the glTF specification alone: a LINEAR rotation is the shortest-path slerp, so a key's sign is
free (a negative-dot interval plays the short way). How another consumer (Blender's glTF importer) plays it is not a
rule of this hop; the Blender writer is a separate output path with its own hops (C-anim, D).

Controls (each on a mutated copy of the GLB, judged at the mutated channel's own label; a mismatch the pristine check
already reports there is masking, not detection): a sampler's output shifted by one key; CUBICSPLINE tangents divided
by their key interval (a specification reader of the copy then computes exactly what SharpGLTF's interval-blind sampler
computes on the original); a quaternion sign flip on one interior key of a CUBICSPLINE rotation (a long-way turn of
the normalized cubic; on a LINEAR rotation the flip is the same animation under the specification, so it is not a
mutation this hop may report, and the self-check requires that it passes); one visibility byte inverted. ``synthetic_control_exercise`` runs any of them on ``gate1b_synthetic``'s set when no corpus sample reaches it,
and the 170-degree control (``gate1b_synthetic.run_170_degree_control``) runs through the same channel comparison.
"""

import copy
import json
import os
import tempfile
import traceback

import numpy as np

import anim_compare as ac
import anim_dump
import anim_glb
from dump_reader import Dump
from gate1a_common import HopResult, Mismatch, OracleError, sha256_bytes
from glb_reader import COMPONENT_DTYPES, TYPE_COMPONENTS, Glb
from hop_b import GlbDumpPairing, load_fidelity_rows, rebuild_glb

F32 = np.float32

CHECK_CLIPS = 'animation clips: GLB animations paired with dump clips, playback windows'
CHECK_CHANNELS = 'animation channels: GLB samplers against the dump clips (independent evaluator)'
CHECK_COVERAGE = 'animation coverage: driven dump properties and GLB channels'

MATERIAL_FACTOR_KINDS = {
    'baseColorFactor': [('MaterialBaseColor', 0), ('MaterialBaseColor', 1), ('MaterialBaseColor', 2), ('MaterialAlpha', 0)],
    'emissiveFactor': [('MaterialEmissiveColor', 0), ('MaterialEmissiveColor', 1), ('MaterialEmissiveColor', 2)],
    'emissiveStrength': [('MaterialEmissiveStrength', 0)],
    'specularColorFactor': [('MaterialSpecularColor', 0), ('MaterialSpecularColor', 1), ('MaterialSpecularColor', 2)],
}
TEXTURE_MEMBER_KINDS = {'offset': ['LayerOffsetU', 'LayerOffsetV'], 'scale': ['LayerScaleU', 'LayerScaleV'],
                        'rotation': ['LayerRotation']}
UNIT_BY_PATH = {'translation': 'meters', 'rotation': 'degrees', 'scale': 'ratio', 'weights': 'ratio'}


# ---------------------------------------------------------------------------------------------------------- rows
def glb_rows(sample, glb):
    """The GLB writer's rows: ``mesh fidelity --format glb --json`` when supplied, else the GLB's own carrier (hop B
    already proves the two are the same multiset)."""
    if sample.get('fidelity'):
        rows, _, _ = load_fidelity_rows(sample['fidelity'])
        return rows, 'fidelity.json'
    rows = ((glb.json.get('extras') or {}).get('multitoolFidelity') or {}).get('rows') or []
    return rows, 'GLB multitoolFidelity carrier'


def driver_bound(table, unit, driver):
    """One source track's bound: its ``clock/value`` row, else its conversion rows, else Exact (0)."""
    prefix = driver.row_prefix
    row = table.get(prefix + '/clock/value')
    stated = ac.row_bounds(row)
    if stated is not None:
        return ac.Bound(unit, stated[1], stated[2], [prefix + '/clock/value'])
    candidates = []
    for feature, candidate in table.items():
        if feature.startswith(prefix + '/'):
            bounds = ac.row_bounds(candidate)
            if bounds is not None and bounds[0] not in ('ulp', 'Float32 ULP'):
                candidates.append((feature, bounds))
    if candidates:
        observed = max(b[1] for _, b in candidates)
        maximum = max(b[2] for _, b in candidates)
        return ac.Bound(unit, observed, maximum, [f for f, _ in candidates])
    return ac.Bound(unit, 0.0, 0.0, [], 'no bounded row for %s: Exact' % prefix)


def key_bound(table, clip, key, unit):
    """The bound of a pose key: the largest certificate among the tracks that drive it, plus the packing row of a
    morph node."""
    drivers = clip.drivers_of(key)
    bound = ac.Bound(unit, 0.0, 0.0, [], None)
    for driver in drivers:
        single = driver_bound(table, unit, driver)
        bound = ac.Bound(unit, max(bound.observed, single.observed), max(bound.maximum, single.maximum),
                         bound.rows + single.rows, '; '.join(n for n in (bound.note, single.note) if n) or None)
    if key[0] == 'node' and key[2] == 'weights':
        packing = table.get('animation/morph-target-nodes/%d/packing' % key[1])
        stated = ac.row_bounds(packing)
        if stated is not None:
            bound = bound.plus(ac.Bound(unit, stated[1], stated[2], ['animation/morph-target-nodes/%d/packing' % key[1]]))
    return bound


# --------------------------------------------------------------------------------------------------- targets
class Target:
    """What a GLB channel animates, resolved against the dump: a pose-level expectation function and its measure."""

    def __init__(self, kind, keys, measure, unit, expected_at=None, reason=None):
        self.kind = kind
        self.keys = keys
        self.measure = measure
        self.unit = unit
        self.expected_at = expected_at
        self.reason = reason


def glb_material_static(glb, material_index, name):
    material = glb.json['materials'][material_index]
    if name == 'baseColorFactor':
        return list((material.get('pbrMetallicRoughness') or {}).get('baseColorFactor', [1, 1, 1, 1]))
    if name == 'emissiveFactor':
        return list(material.get('emissiveFactor', [0, 0, 0]))
    extensions = material.get('extensions') or {}
    if name == 'emissiveStrength':
        return [(extensions.get('KHR_materials_emissive_strength') or {}).get('emissiveStrength', 1.0)]
    return list((extensions.get('KHR_materials_specular') or {}).get('specularColorFactor', [1, 1, 1]))


def glb_texture_transform_static(glb, material_index, role, member):
    material = glb.json['materials'][material_index]
    info = None
    for prefix, candidate in anim_glb.TEXTURE_INFO_ROLES.items():
        if candidate != role:
            continue
        node = material
        for part in prefix.strip('/').split('/'):
            node = (node or {}).get(part)
        info = node
    transform = ((info or {}).get('extensions') or {}).get('KHR_texture_transform') or {}
    if member == 'offset':
        return list(transform.get('offset', [0, 0]))
    if member == 'scale':
        return list(transform.get('scale', [1, 1]))
    return [transform.get('rotation', 0.0)]


def portable_factor(dmat, kind, component):
    """The dump material's portable (writer-facing) value of one factor component."""
    if kind in ('MaterialBaseColor', 'MaterialAlpha'):
        base = dmat.get('baseColor') or [1, 1, 1, 1]
        return base[component if kind == 'MaterialBaseColor' else 3]
    if kind == 'MaterialEmissiveColor':
        return (dmat.get('emissiveFactor') or [0, 0, 0])[component]
    if kind == 'MaterialEmissiveStrength':
        return dmat.get('emissiveStrength') if dmat.get('emissiveStrength') is not None else 1.0
    return (dmat.get('specularColorFactor') or [1, 1, 1])[component]


def resolve_target(channel, glb, animations, dump_anim, clip_index, pairing):
    """The Target of one GLB channel, or a Target with ``reason`` set when the oracle does not compare it."""
    clip = dump_anim.clips[clip_index]
    target = channel.target
    if target[0] == 'node':
        _, node, path = target
        if path not in ('translation', 'rotation', 'scale', 'weights'):
            return Target('node', [], 'components', 'ratio', reason='unknown node path %r' % path)
        if node >= len(dump_anim.dump.nodes):
            return Target('node', [], 'components', 'ratio', reason='node %d is a writer-added helper, not a dump node' % node)
        key = ('node', node, path)
        measure = ac.value_kind(key)
        return Target('node', [key], measure, UNIT_BY_PATH[path],
                      expected_at=lambda t: dump_anim.value(clip_index, key, t))
    if target[0] == 'visibility':
        key = ('visibility', target[1])
        return Target('visibility', [key], 'boolean', 'boolean', expected_at=lambda t: dump_anim.value(clip_index, key, t))
    if target[0] == 'material-factor':
        _, glb_material, name, width = target
        if pairing is None or glb_material not in pairing.material_map:
            return Target('material', [], 'components', 'component', reason='GLB material %d has no paired dump material' % glb_material)
        dump_material = pairing.material_map[glb_material]
        dmat = dump_anim.dump.materials[dump_material]
        layout = MATERIAL_FACTOR_KINDS[name]
        rest = [float(v) for v in glb_material_static(glb, glb_material, name)]
        keys = []
        for kind, component in layout:
            key = ('material', dump_material, kind)
            if clip.drivers_of(key):
                source_rest = float(dump_anim.rest(key)[component])
                if float(F32(portable_factor(dmat, kind, component))) != float(F32(source_rest)):
                    return Target('material', [], 'components', 'component',
                                  reason='%s component %d: the portable rest %r differs from the source rest %r, so the writer '
                                         'projects the source through a layer equation (SceneTextureLayerProjection) the oracle '
                                         'does not reproduce' % (kind, component, portable_factor(dmat, kind, component), source_rest))
                if key not in keys:
                    keys.append(key)

        def expected(t, layout=layout, rest=rest, dump_material=dump_material):
            values = list(rest)
            magnitude = max(abs(v) for v in rest) if rest else 0.0
            for position, (kind, component) in enumerate(layout):
                key = ('material', dump_material, kind)
                if clip.drivers_of(key):
                    value, mag = dump_anim.value(clip_index, key, t)
                    values[position] = float(value[component])
                    magnitude = max(magnitude, mag)
            return np.asarray(values, dtype=np.float64), magnitude
        return Target('material', keys, 'components', 'component', expected_at=expected)
    if target[0] == 'texture-transform':
        _, glb_material, role, member, width = target
        if pairing is None or glb_material not in pairing.material_map:
            return Target('layer', [], 'components', 'component', reason='GLB material %d has no paired dump material' % glb_material)
        dump_material = pairing.material_map[glb_material]
        layers = dump_anim.dump.materials[dump_material].get('layers') or []
        matches = [i for i, layer in enumerate(layers) if layer.get('role') == role]
        if len(matches) != 1:
            return Target('layer', [], 'components', 'component',
                          reason='%d source layers carry role %s (the writer maps exactly one)' % (len(matches), role))
        layer = matches[0]
        kinds = TEXTURE_MEMBER_KINDS[member]
        rest = [float(v) for v in glb_texture_transform_static(glb, glb_material, role, member)]
        keys = [('layer', dump_material, layer, kind) for kind in kinds if clip.drivers_of(('layer', dump_material, layer, kind))]

        def expected(t, kinds=kinds, rest=rest, dump_material=dump_material, layer=layer):
            values = list(rest)
            magnitude = max(abs(v) for v in rest) if rest else 0.0
            for position, kind in enumerate(kinds):
                key = ('layer', dump_material, layer, kind)
                if clip.drivers_of(key):
                    value, mag = dump_anim.value(clip_index, key, t)
                    values[position] = float(value[0])
                    magnitude = max(magnitude, mag)
            return np.asarray(values, dtype=np.float64), magnitude
        return Target('layer', keys, 'components', 'component', expected_at=expected)
    return Target('pointer', [], 'components', 'component', reason='pointer %r is not a target the writer emits' % channel.pointer)


# ------------------------------------------------------------------------------------------------ comparison
def clip_window(meta, clip, channels):
    """The playback window: the metadata's ``outputWindowSeconds`` (checked against the clip clock by check 1), else
    the largest channel key time."""
    if meta is not None and meta.get('outputWindowSeconds') is not None:
        return float(meta['outputWindowSeconds'])
    extent = 0.0
    for channel in channels:
        if len(channel.times):
            extent = max(extent, float(channel.times[-1]))
    return extent


def check_clips(result, animations, dump_anim, rows=None):
    """Check 1. ``rows`` are the GLB writer's rows: a dump clip that drives tracks but has no output animation must be
    declared by one of them (``anim_compare.declared_drop``); a silent drop fails."""
    check = result.check(CHECK_CLIPS)
    pairs = []
    rows = []
    for animation in animations.animations:
        meta = animations.clip_metadata(animation['index'])
        label = 'animations/%d (%s)' % (animation['index'], animation['name'])
        if meta is None or meta.get('sourceAnimation') is None:
            check.fail(Mismatch(label, 'pairing', 'no multitoolAnimationMetadata clip names this output animation'))
            continue
        source = int(meta['sourceAnimation'])
        if not 0 <= source < len(dump_anim.clips):
            check.fail(Mismatch(label, 'pairing', 'sourceAnimation %d is outside the dump\'s %d clips' % (source, len(dump_anim.clips))))
            continue
        clip = dump_anim.clips[source]
        if meta.get('name') != clip.name or animation['name'] != clip.name:
            check.fail(Mismatch(label, 'pairing', 'GLB %r / metadata %r, dump clip %d is %r' % (animation['name'], meta.get('name'), source, clip.name)))
            continue
        window = clip_window(meta, clip, animation['channels'])
        entry = {'output': animation['index'], 'source': source, 'name': clip.name, 'window': window,
                 'clipClock': clip.clock, 'channels': len(animation['channels'])}
        # The clip metadata keeps the source clock and ordered events verbatim (docs/scene-animation-clock-lowering.md,
        # "Metadata": clip rows gain clock; events stay in source clip time).
        if not ac.same_clock(meta.get('clock'), clip.clock):
            check.fail(Mismatch(label + '/metadata/clock', 'declaration', 'metadata %r, dump %r' % (meta.get('clock'), clip.clock)))
            continue
        if 'events' in meta and not ac.same_events(meta.get('events'), clip.raw.get('events')):
            check.fail(Mismatch(label + '/metadata/events', 'declaration', 'metadata %d events, dump %d (or a time or text differs)'
                                % (len(meta.get('events') or []), len(clip.raw.get('events') or []))))
            continue
        if clip.clock is not None and meta.get('outputWindowSeconds') is not None:
            derived = anim_dump.clip_clock_window(clip.clock)
            entry['derivedWindow'] = derived
            if abs(float(F32(derived)) - window) > 2 * ac.ulp32(window if window else 1.0):
                check.fail(Mismatch(label + '/window', 'value', 'outputWindowSeconds %r, the clip clock %r gives %r' % (window, clip.clock, derived)))
                continue
        for channel in animation['channels']:
            if len(channel.times) and (float(channel.times[0]) < 0 or float(channel.times[-1]) > window + 2 * ac.ulp32(window if window else 1.0)):
                check.fail(Mismatch(channel.label + '/times', 'value', 'keys span [%r, %r] outside the window [0, %r]'
                                    % (float(channel.times[0]), float(channel.times[-1]), window)))
        rows.append(entry)
        pairs.append((animation, source, window, meta))
    check.data['clips'] = rows
    check.data['dumpClips'] = len(dump_anim.clips)
    check.data['glbAnimations'] = len(animations.animations)
    emitted = {p[1] for p in pairs}
    table = ac.rows_by_clip(rows or [])
    not_emitted = []
    for i, clip in enumerate(dump_anim.clips):
        if i in emitted:
            continue
        driven = ac.driven_track_count(clip.raw)
        statement = ac.declared_drop(table.get(i))
        not_emitted.append({'source': i, 'name': clip.name, 'drivenTracks': driven, 'writerStatement': statement})
        if driven and statement is None:
            check.fail(Mismatch('clips/%d (%s)' % (i, clip.name), 'dropped', 'the dump clip drives %d tracks but the GLB has no '
                                'animation for it and no row declares why (a silent drop)' % driven))
    check.data['clipsNotEmitted'] = not_emitted
    if check.passed is None:
        check.ok('%d GLB animations paired with %d dump clips; %d clips not emitted' % (len(pairs), len(dump_anim.clips), len(check.data['clipsNotEmitted'])))
    return pairs


def factor_of(dump_anim):
    mpu = dump_anim.meters_per_unit
    return float(F32(mpu)) if mpu else 1.0


def compare_channels(result, glb, animations, dump_anim, pairs, rows, pairing, context, unscaled_label=None):
    """Check 2. Returns the per-channel records (dicts) the receipt keeps."""
    check = result.check(CHECK_CHANNELS)
    table = ac.rows_by_clip(rows)
    factor = factor_of(dump_anim)
    records = []
    compared = 0
    for animation, source, window, meta in pairs:
        clip = dump_anim.clips[source]
        clip_rows = table.get(source, {})
        for channel in animation['channels']:
            record = ac.ChannelRecord(channel.label, channel.target, channel.interpolation, int(len(channel.times)))
            target = resolve_target(channel, glb, animations, dump_anim, source, pairing)
            record.keys = list(target.keys)
            if target.reason is not None:
                record.reason = target.reason
                records.append(record)
                continue
            bound = ac.Bound('boolean', 0, 0, [], 'binary') if target.measure == 'boolean' else ac.Bound(target.unit, 0, 0, [])
            for key in target.keys:
                if target.measure != 'boolean':
                    single = key_bound(clip_rows, clip, key, target.unit)
                    bound = ac.Bound(target.unit, max(bound.observed, single.observed), max(bound.maximum, single.maximum),
                                     bound.rows + single.rows, '; '.join(n for n in (bound.note, single.note) if n) or None)
            unscaled = unscaled_label is not None and channel.label == unscaled_label
            times = ac.sample_times(channel.times, window)

            def actual_at(t, channel=channel, unscaled=unscaled):
                return channel.sample(t, unscaled=unscaled)

            try:
                ok = ac.compare_series(record, times, actual_at, target.expected_at, target.measure, bound,
                                       factor if target.measure == 'translation' else 1.0,
                                       dict(context, clip=clip.name))
            except anim_dump.DumpUnsupported as failure:
                record.reason = 'the dump side is not evaluable: %s' % failure
                records.append(record)
                continue
            compared += 1 if record.compared else 0
            if not ok:
                check.fail(Mismatch(channel.label, 'value', ac.mismatch_detail(record)))
            records.append(record)
    check.data['channelsCompared'] = compared
    check.data['channelsNotCompared'] = sum(1 for r in records if not r.compared)
    check.data['samplesCompared'] = sum(r.samples for r in records)
    check.data['samplesSkipped'] = sum(sum(r.skipped.values()) for r in records)
    check.data['worstRatioToCertificate'] = max([r.worst_ratio for r in records if r.compared] or [0.0])
    check.data['worstRatioToDesignLimit'] = max([r.worst_design_ratio for r in records if r.compared] or [0.0])
    if check.passed is None:
        if compared == 0:
            check.skip('no GLB channel was compared (%d not compared)' % len(records))
        else:
            check.ok('%d channels within their certificates (%d samples, %d skipped at discontinuities; worst %.3g x certificate, %.3g x design limit)'
                     % (compared, check.data['samplesCompared'], check.data['samplesSkipped'],
                        check.data['worstRatioToCertificate'], check.data['worstRatioToDesignLimit']))
    return records


def check_coverage(result, glb, animations, dump_anim, pairs, records, context):
    """Check 4: every property a paired clip drives either has a compared channel, or (node TRS, weights, visibility
    without a channel) holds the GLB rest value over the window, or (a material factor or layer transform without a
    compared pointer channel) holds its own source rest value; every channel resolved to a driven property. Tracks the
    oracle cannot evaluate are listed with their reason."""
    check = result.check(CHECK_COVERAGE)
    rows = []
    factor = factor_of(dump_anim)
    for animation, source, window, meta in pairs:
        clip = dump_anim.clips[source]
        prefix = 'animations/%d ' % animation['index']
        compared_keys = set()
        for record in records:
            if record.label.startswith(prefix) and record.compared:
                compared_keys.update(getattr(record, 'keys', []) or [])
        emitted = set()
        for channel in animation['channels']:
            if channel.target[0] == 'node':
                emitted.add(('node', channel.node, channel.path))
            elif channel.target[0] == 'visibility':
                emitted.add(('visibility', channel.target[1]))
        for key in clip.driven_keys():
            if key in compared_keys:
                rows.append({'clip': clip.name, 'key': list(key), 'state': 'compared'})
                continue
            if key[0] in ('node', 'visibility') and key in emitted:
                rows.append({'clip': clip.name, 'key': list(key), 'state': 'channel present but not compared (see the channel record)'})
                continue
            if key[0] in ('node', 'visibility'):
                rest_value = glb_rest(animations, key)
            else:
                rest_value = dump_anim.rest(key)
            if rest_value is None:
                rows.append({'clip': clip.name, 'key': list(key), 'state': 'not compared: no rest value for this key'})
                continue
            label = 'animations/%d (%s)/%s (no channel: rest)' % (animation['index'], animation['name'], anim_dump.key_label(key))
            record = ac.ChannelRecord(label, key)
            measure = ac.value_kind(key)
            unit = 'boolean' if measure == 'boolean' else (UNIT_BY_PATH.get(key[2], 'ratio') if key[0] == 'node' else 'component')
            times = ac.sample_times([], window)
            ok = ac.compare_series(record, times, lambda t, v=np.asarray(rest_value, dtype=np.float64): v,
                                   lambda t, k=key: dump_anim.value(source, k, t), measure, ac.Bound(unit, 0, 0, []),
                                   factor if measure == 'translation' else 1.0, dict(context, clip=clip.name))
            state = ('no channel; the dump holds the GLB rest value' if key[0] in ('node', 'visibility')
                     else 'no pointer channel; the dump holds the source rest value') if ok else 'MISSING channel'
            rows.append({'clip': clip.name, 'key': list(key), 'state': state, 'record': record.to_dict()})
            if not ok:
                check.fail(Mismatch(label, 'presence', 'the dump drives %s away from its rest value but the GLB has no compared channel: %s'
                                    % (anim_dump.key_label(key), ac.mismatch_detail(record))))
        for domain, index, key, reason in clip.unsupported:
            rows.append({'clip': clip.name, 'track': '%s/%d' % (domain, index), 'state': 'not compared: %s' % reason})
        rows.append({'clip': clip.name, 'notDriven': len(clip.not_driven), 'state': 'NotDriven tracks supply no value (no channel expected)'})
    check.data['keys'] = rows[:400]
    check.data['channelsNotCompared'] = [r.to_dict() for r in records if not r.compared]
    if check.passed is None:
        check.ok('%d driven properties accounted for (%d not compared, reasons recorded)'
                 % (sum(1 for r in rows if 'key' in r or 'track' in r),
                    sum(1 for r in rows if str(r.get('state', '')).startswith('not compared'))))


def glb_rest(animations, key):
    if key[0] == 'node':
        try:
            value = animations.node_rest(key[1], key[2])
        except (KeyError, IndexError, OracleError):
            return None
        return np.asarray(value, dtype=np.float64)
    if key[0] == 'visibility':
        node = animations.glb.json['nodes'][key[1]]
        extension = (node.get('extensions') or {}).get('KHR_node_visibility') or {}
        return np.array([1.0 if extension.get('visible', True) else 0.0])
    return None


def check_animation(result, glb, dump, sample, context, unscaled_label=None):
    """Checks 1-4 on one GLB and dump. Returns (records, pairs) for the controls."""
    animations = anim_glb.GlbAnimations(glb)
    dump_anim = anim_dump.DumpAnimation(dump)
    rows, rows_source = glb_rows(sample, glb)
    pairing = None
    try:
        pairing = GlbDumpPairing(glb, dump)
    except OracleError:
        pairing = None
    pairs = check_clips(result, animations, dump_anim, rows)
    records = compare_channels(result, glb, animations, dump_anim, pairs, rows, pairing, context, unscaled_label)
    check_coverage(result, glb, animations, dump_anim, pairs, records, context)
    result.checks[1].data['rowsSource'] = rows_source
    return records, pairs, animations


# ---------------------------------------------------------------------------------------------------- controls
def output_view(glb, accessor_index):
    """(absolute BIN offset of element 0, element stride, element bytes, dtype, components) of an accessor."""
    accessor = glb.json['accessors'][accessor_index]
    view = glb.json['bufferViews'][accessor['bufferView']]
    dtype = COMPONENT_DTYPES[accessor['componentType']]
    components = TYPE_COMPONENTS[accessor['type']]
    element = dtype.itemsize * components
    stride = view.get('byteStride') or element
    return view.get('byteOffset', 0) + accessor.get('byteOffset', 0), stride, element, dtype, components


def rewrite_output(glb_bytes, channel, new_values):
    """A copy of the GLB whose ``channel`` output accessor holds ``new_values`` (shape keys x parts x width, in the
    accessor's own component type; normalized integers are re-encoded by rounding)."""
    glb = Glb(glb_bytes)
    offset, stride, element, dtype, components = output_view(glb, channel.output_accessor)
    flat = np.asarray(new_values, dtype=np.float64).reshape(-1)
    if dtype.kind in 'iu' and channel.output_normalized:
        info = np.iinfo(dtype)
        flat = np.round(flat * info.max)
    typed = flat.astype(dtype)
    count = glb.json['accessors'][channel.output_accessor]['count']
    typed = typed.reshape(count, components)
    bin_chunk = bytearray(glb.bin)
    for index in range(count):
        start = offset + index * stride
        bin_chunk[start:start + element] = typed[index].tobytes()
    return rebuild_glb(json.loads(glb.json_bytes.decode('utf-8')), bytes(bin_chunk))


def records_by_label(records):
    return {r.label: r for r in records}


def detectable(channel, mutated_values, sample_window, allowance=1e-9):
    """Whether a mutated channel's values change its own sampled output (so a comparison could see the mutation)."""
    probe = copy.copy(channel)
    probe.values = np.asarray(mutated_values, dtype=np.float64)
    worst = 0.0
    kind = 'rotation' if channel.is_rotation else ('boolean' if channel.is_boolean else 'components')
    for t in ac.sample_times(channel.times, sample_window):
        worst = max(worst, ac.deviation(kind, probe.sample(t), channel.sample(t)))
    return worst > allowance, worst


def mutate_key_shift(glb_bytes, animations, pairs, records):
    """Shift one sampler's output by one key (key k takes key k+1's value; the last key keeps its own), on the first
    compared channel with at least two keys whose shift is visible beyond ten times its compared allowance."""
    by_label = records_by_label(records)
    for animation, source, window, meta in pairs:
        for channel in animation['channels']:
            record = by_label.get(channel.label)
            if record is None or not record.compared or len(channel.times) < 2:
                continue
            shifted = np.concatenate([channel.values[1:], channel.values[-1:]], axis=0)
            allowance = 10 * max(record.bound.maximum if record.bound else 0.0, 1e-6)
            visible, worst = detectable(channel, shifted, window, allowance)
            if not visible:
                continue
            return rewrite_output(glb_bytes, channel, shifted), {
                'channel': channel.label, 'keys': int(len(channel.times)), 'worstChange': worst,
                'where': channel.label, 'kind': 'value',
                'description': '%s output shifted by one key (%d keys; the pristine sampler changes by up to %.4g)' % (channel.label, len(channel.times), worst)}
    return None, None


def mutate_cubic_unscaled(glb_bytes, animations, pairs, records):
    """Divide one CUBICSPLINE sampler's tangents by their key interval: the out-tangent of key k by t_{k+1} - t_k and
    the in-tangent of key k by t_k - t_{k-1}. A specification reader of the copy multiplies them back by the interval
    once, so it computes what an interval-blind sampler (SharpGLTF's) computes on the original. First compared cubic
    channel where that is visible beyond ten times its allowance."""
    by_label = records_by_label(records)
    for animation, source, window, meta in pairs:
        for channel in animation['channels']:
            record = by_label.get(channel.label)
            if record is None or not record.compared or channel.interpolation != 'CUBICSPLINE' or len(channel.times) < 2:
                continue
            values = channel.values.copy()
            times = channel.times.astype(np.float64)
            for k in range(len(times)):
                if k + 1 < len(times):
                    values[k, 2] = values[k, 2] / (times[k + 1] - times[k])
                if k > 0:
                    values[k, 0] = values[k, 0] / (times[k] - times[k - 1])
            allowance = 10 * max(record.bound.maximum if record.bound else 0.0, 1e-6)
            visible, worst = detectable(channel, values, window, allowance)
            if not visible:
                continue
            return rewrite_output(glb_bytes, channel, values), {
                'channel': channel.label, 'keys': int(len(channel.times)), 'worstChange': worst, 'where': channel.label,
                'kind': 'value', 'description': '%s tangents divided by their key intervals (the pristine sampler changes by up to %.4g)'
                                                % (channel.label, worst)}
    return None, None


def mutate_sign_flip(glb_bytes, animations, pairs, records):
    """Negate one interior key (all three parts) of a CUBICSPLINE rotation sampler: the same orientation with the
    opposite sign, so the normalized component cubic turns the long way on both of its intervals. A LINEAR rotation is
    never mutated: the specification's shortest-path slerp plays the negated key identically. The key is the first
    interior key not beside a one-step (Loop-wrap) interval."""
    by_label = records_by_label(records)
    for animation, source, window, meta in pairs:
        for channel in animation['channels']:
            record = by_label.get(channel.label)
            if record is None or not record.compared or not channel.is_rotation or channel.interpolation != 'CUBICSPLINE' or len(channel.times) < 3:
                continue
            times = channel.times
            for k in range(1, len(times) - 1):
                narrow = any(float(times[j + 1]) - float(times[j]) <= 2 * ac.ulp32(times[j] if times[j] else 1e-30) for j in (k - 1, k))
                if narrow:
                    continue
                values = channel.values.copy()
                values[k] = -values[k]
                return rewrite_output(glb_bytes, channel, values), {
                    'channel': channel.label, 'key': k, 'interpolation': channel.interpolation, 'where': channel.label, 'kind': 'value',
                    'description': '%s key %d negated (%s)' % (channel.label, k, channel.interpolation)}
    return None, None


def mutate_visibility(glb_bytes, animations, pairs, records):
    """Invert one KHR_node_visibility key (0 <-> 1) on the first compared visibility channel."""
    by_label = records_by_label(records)
    for animation, source, window, meta in pairs:
        for channel in animation['channels']:
            record = by_label.get(channel.label)
            if record is None or not record.compared or not channel.is_boolean:
                continue
            values = channel.values.copy()
            values[0] = 0.0 if float(values[0, 0, 0]) != 0 else 1.0
            return rewrite_output(glb_bytes, channel, values), {
                'channel': channel.label, 'key': 0, 'where': channel.label, 'kind': 'value',
                'description': '%s key 0 inverted (%r -> %r)' % (channel.label, float(channel.values[0, 0, 0]), float(values[0, 0, 0]))}
    return None, None


CONTROL_KEY_SHIFT = 'GLB sampler output shifted by one key in a copy of the GLB'
CONTROL_CUBIC_UNSCALED = 'CUBICSPLINE tangents unscaled by the key interval (SharpGLTF sampler) in a copy of the GLB'
CONTROL_SIGN_FLIP = 'quaternion sign flip on one rotation key in a copy of the GLB'
CONTROL_VISIBILITY = 'one KHR_node_visibility key inverted in a copy of the GLB'
CONTROLS = {
    CONTROL_KEY_SHIFT: {'mutate': mutate_key_shift, 'file': 'control-key-shift.glb',
                        'notApplicable': 'not applicable: no compared channel has two keys whose shift is visible'},
    CONTROL_CUBIC_UNSCALED: {'mutate': mutate_cubic_unscaled, 'file': 'control-cubic-unscaled.glb',
                             'notApplicable': 'not applicable: no compared CUBICSPLINE channel whose interval scaling is visible'},
    CONTROL_SIGN_FLIP: {'mutate': mutate_sign_flip, 'file': 'control-sign-flip.glb',
                        'notApplicable': ("not applicable: no compared CUBICSPLINE rotation with an interior key (a LINEAR key's sign is "
                                          "free under the specification's shortest-path slerp)"),
                        'syntheticRotation': 'CubicSpline'},
    CONTROL_VISIBILITY: {'mutate': mutate_visibility, 'file': 'control-visibility.glb',
                         'notApplicable': 'not applicable: no compared KHR_node_visibility channel'},
}
CONTROL_ORDER = (CONTROL_KEY_SHIFT, CONTROL_CUBIC_UNSCALED, CONTROL_SIGN_FLIP, CONTROL_VISIBILITY)


def control_verdict(probe, pristine, where, kind):
    """Detected when the probe reports a ``kind`` mismatch exactly at ``where`` and the pristine run reports none there
    (a pristine mismatch at that label is masking: the control cannot be told apart and reads not applicable)."""
    pristine_hits = [m for c in pristine.checks for m in c.mismatches if m.where == where]
    if pristine_hits:
        return None, 'not applicable: the pristine check already reports %s at %s' % (pristine_hits[0].kind, where)
    hits = [m for c in probe.checks for m in c.mismatches if m.where == where and m.kind == kind]
    if hits:
        return True, 'mismatch reported at %s (%s): %s' % (where, kind, hits[0].detail[:300])
    others = [m for c in probe.checks for m in c.mismatches]
    return False, 'NOT detected (expected a %s mismatch at %s; the probe reported %s)' % (
        kind, where, ('%s at %s' % (others[0].kind, others[0].where)) if others else 'nothing')


def run_control(name, sample, glb_bytes, dump, pristine, records, pairs, animations, context, work_dir):
    spec = CONTROLS[name]
    control = {'name': name, 'detected': None, 'detail': None}
    mutated, description = spec['mutate'](glb_bytes, animations, pairs, records)
    if mutated is None:
        control['detail'] = spec['notApplicable']
        return control
    probe = HopResult('B-anim-control', sample.get('id'))
    check_animation(probe, Glb(mutated), dump, sample, context)
    detected, verdict = control_verdict(probe, pristine, description['where'], description['kind'])
    control['detected'] = detected
    control['detail'] = '%s: %s' % (description['description'], verdict)
    control['expectedMismatchWhere'] = description['where']
    control['mutation'] = description
    os.makedirs(work_dir, exist_ok=True)
    path = os.path.join(work_dir, spec['file'])
    with open(path, 'wb') as f:
        f.write(mutated)
    control['files'] = {'glb': path, 'glbSha256': sha256_bytes(mutated)}
    return control


def run(sample, work_dir):
    """Hop B-anim over one sample dict (glb, dump, fidelity). A GLB without animations reads not applicable."""
    result = HopResult('B-anim', sample['id'])
    try:
        if not sample.get('glb') or not sample.get('dump'):
            result.check('GLB animation versus dump').skip('GLB or dump not supplied')
            return result
        with open(sample['glb'], 'rb') as f:
            glb_bytes = f.read()
        glb = Glb(glb_bytes)
        dump = Dump(sample['dump'])
        if not glb.json.get('animations'):
            if not dump.animations:
                result.check('GLB animation versus dump').skip('neither the GLB nor the dump carries animation')
                return result
            # The dump has clips the GLB does not play: each must be declared by a writer row (check 1 alone).
            rows, _ = glb_rows(sample, glb)
            check_clips(result, anim_glb.GlbAnimations(glb), anim_dump.DumpAnimation(dump), rows)
            clips = result.checks[-1]
            if clips.passed is not False:
                clips.skip('the GLB has no animations; the dump has %d clips (%d driven tracks), each declared by a writer row: %s' % (
                    len(dump.animations), sum(c['drivenTracks'] for c in clips.data['clipsNotEmitted']),
                    '; '.join('%s: %s' % (c['name'], c['writerStatement'] or 'no driven track') for c in clips.data['clipsNotEmitted'])[:900]))
            return result
        context = {'dontSample': ac.load_dont_sample(), 'sample': sample['id']}
        records, pairs, animations = check_animation(result, glb, dump, sample, context)
        result.checks[1].data['channels'] = [r.to_dict() for r in records]
        for name in CONTROL_ORDER:
            result.controls.append(run_control(name, sample, glb_bytes, dump, result, records, pairs, animations, context, work_dir))
    except OracleError as failure:
        result.error = str(failure)
    except Exception as failure:  # noqa: BLE001 - an oracle defect must reach the receipt, not abort the run
        result.error = 'oracle exception %s: %s' % (type(failure).__name__, failure)
        result.traceback = traceback.format_exc()
    return result


def synthetic_control_exercise(control_name, work_dir=None, builder=None):
    """Run one control on ``gate1b_synthetic``'s hand-built set (the same mutation, checks and verdict), for a control no
    corpus sample reaches. Detected only when the verdict detects AND the untouched set passes every check."""
    if control_name not in CONTROLS:
        return None
    import gate1b_synthetic
    work_dir = work_dir or tempfile.mkdtemp(prefix='gate1b-synthetic-b-')
    if builder is None:
        # The sign-flip control needs a CUBICSPLINE rotation; every other control runs on the LINEAR-rotation set.
        rotation = CONTROLS[control_name].get('syntheticRotation', 'Linear')
        builder = lambda directory: gate1b_synthetic.write_pristine_set(directory, rotation=rotation)
    artifacts = builder(work_dir)
    sample = {'id': 'synthetic', 'glb': artifacts['glb'], 'dump': artifacts['dump'], 'fidelity': artifacts['fidelity']}
    with open(artifacts['glb'], 'rb') as f:
        glb_bytes = f.read()
    dump = Dump(artifacts['dump'])
    pristine = HopResult('B-anim-synthetic', 'synthetic')
    context = {'dontSample': [], 'sample': 'synthetic'}
    records, pairs, animations = check_animation(pristine, Glb(glb_bytes), dump, sample, context)
    control = run_control(control_name, sample, glb_bytes, dump, pristine, records, pairs, animations, context, work_dir)
    failed = [c for c in pristine.checks if c.passed is False]
    pristine_passed = not failed
    verdict = control.get('detected')
    return {
        'control': control_name, 'detected': verdict is True and pristine_passed, 'verdict': verdict,
        'pristinePassed': pristine_passed,
        'detail': '%s (synthetic set; %s)' % (control.get('detail'), 'pristine checks passed' if pristine_passed else
                                              'pristine %s FAILED, so the exercise reads NOT detected' % ', '.join(c.name for c in failed)),
        'expectedMismatchWhere': control.get('expectedMismatchWhere'),
        'artifactSha256': dict(artifacts['sha256'], mutatedGlb=(control.get('files') or {}).get('glbSha256')),
        'pristineChecks': [{'name': c.name, 'status': c.to_dict()['status'], 'detail': c.detail,
                            'firstMismatch': c.mismatches[0].to_dict() if c.mismatches else None} for c in pristine.checks],
        'files': dict(artifacts.get('files') or {}, mutatedGlb=(control.get('files') or {}).get('glb')),
        'workDir': work_dir,
    }
