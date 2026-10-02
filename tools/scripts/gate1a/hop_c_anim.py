# SPDX-License-Identifier: 0BSD
"""Hop C-anim: document (exact JSON dump) -> Blender package v4 animation (gate 1b; design section 7.2 row C over the
animation).

Every packaged clip is paired with its source clip by ``sourceIndex``, every channel with its source track by
``sourceDomain``/``sourceTrack`` (the package keeps both), read by ``anim_package`` and played the way the importer
builds and Blender evaluates the F-curves (32 frames per second, Float32 points and FREE handles one third of the
interval away, CONSTANT/LINEAR/BEZIER per interval, quaternion components interpolated independently and normalized,
XYZ Euler axes, binary visibility), then compared with the dump track evaluated by ``anim_dump`` through
``nif_curve_eval`` at the same output time (clip clock, then track clock).

Checks:
  1. Clips: every packaged clip names a dump clip; its duration is the playback window (a clocked clip without an
     authored duration plays one clip-clock period, the same window rule as the GLB writer), and every channel passes
     the importer's admission (``anim_package.admission_problems``): a package the importer would refuse is a failure.
  2. Channels: each channel within its certificate plus slack. The package rows state per component
     ``animation/<domain>/<track>/<component>/clock-retiming``: ratio bounds over ``max(1, lowered source key
     magnitude)`` (the denominator is taken from the channel's own lowered key values, per component), or degrees for a
     normalized quaternion; ``animation/transform-tracks/<track>/rotation`` and ``.../spline-conversion`` add their
     degree bounds for rotations. Translation, scale, Euler axes, weights and material properties are compared per
     component after that normalization; rotations by angle; visibility exactly.
  3. Coverage: every driven dump track has a channel (or holds its rest value), every channel names a driven track.

Controls (each on a mutated copy of the package, judged at the mutated channel's label): a cubic channel's segment
policy flipped to LINEAR on one span (``segmentInterpolations``); a quaternion sign flip on one interior key of a
rotation channel (a long-way turn under the component-linear or component-cubic evaluation); one visibility value
inverted. ``synthetic_control_exercise`` runs them on ``gate1b_synthetic``'s package when no corpus sample reaches them.
"""

import json
import os
import tempfile
import traceback

import numpy as np

import anim_compare as ac
import anim_dump
import anim_package
from dump_reader import Dump
from gate1a_common import HopResult, Mismatch, OracleError, json_loads, sha256_file
from package_reader import Package, mutate_package

F32 = np.float32

CHECK_CLIPS = 'package clips: paired with dump clips, playback windows, importer admission'
CHECK_CHANNELS = 'package channels: Blender F-curve evaluation against the dump tracks (independent evaluator)'
CHECK_COVERAGE = 'package coverage: driven dump tracks and package channels'


def package_rows_table(animations):
    return ac.rows_by_clip(animations.rows)


def component_denominators(channel):
    """max(1, the largest |lowered key value|) per component: the ratio rows' denominator."""
    keys = channel.values[:, 1 if channel.parts == 3 else 0, :].astype(np.float64)
    return np.maximum(1.0, np.max(np.abs(keys), axis=0))


def channel_bound(table, channel):
    """The package channel's bound: ratio (per component, after the denominator) or degrees (a quaternion)."""
    prefix = 'animation/%s/%s' % (channel.domain, channel.track)
    if channel.property == 'Rotation':
        observed = maximum = 0.0
        used = []
        for feature, row in table.items():
            if not feature.startswith(prefix + '/'):
                continue
            stated = ac.row_bounds(row)
            if stated is None or stated[0] != 'degrees':
                continue
            observed = max(observed, stated[1])
            maximum = max(maximum, stated[2])
            used.append(feature)
        return ac.Bound('degrees', observed, maximum, used, None if used else 'no degree row: exact orientation')
    if channel.property == 'NodeVisibility':
        return ac.Bound('boolean', 0, 0, [], 'binary')
    observed = maximum = 0.0
    used = []
    components = [channel.offset + c for c in range(channel.width)] if channel.domain != 'euler-rotation-tracks' else [channel.offset]
    for component in components:
        feature = '%s/%d/clock-retiming' % (prefix, component)
        stated = ac.row_bounds(table.get(feature))
        if stated is not None:
            observed = max(observed, stated[1])
            maximum = max(maximum, stated[2])
            used.append(feature)
    if channel.domain == 'morph-target-tracks' and not used:
        stated = ac.row_bounds(table.get('%s/0/clock-retiming' % prefix))
        if stated is not None:
            observed, maximum = stated[1], stated[2]
            used.append('%s/0/clock-retiming' % prefix)
    return ac.Bound('ratio', observed, maximum, used, None if used else 'no clock-retiming row: exact')


def expected_function(dump_anim, clip_index, channel):
    """(measure, expected_at(t) -> (value, magnitude)) for one package channel, or raises DumpUnsupported."""
    domain = channel.domain
    if domain == 'euler-rotation-tracks':
        axis = channel.offset
        return 'components', lambda t: dump_anim.track_value(clip_index, domain, channel.track, axis, t)[:2]
    if channel.property == 'Rotation':
        return 'rotation', lambda t: dump_anim.track_value(clip_index, domain, channel.track, 0, t)[:2]
    if channel.property == 'NodeVisibility':
        return 'boolean', lambda t: dump_anim.track_value(clip_index, domain, channel.track, 0, t)[:2]
    if domain == 'morph-tracks':
        def morph(t):
            value, magnitude, _ = dump_anim.track_value(clip_index, domain, channel.track, 0, t)
            return value[channel.offset:channel.offset + channel.width], magnitude
        return 'components', morph
    return 'components', lambda t: dump_anim.track_value(clip_index, domain, channel.track, 0, t)[:2]


def compare_channel(record, channel, dump_anim, clip_index, table, window, context):
    measure, expected = expected_function(dump_anim, clip_index, channel)
    bound = channel_bound(table, channel)
    record.bound = bound
    times = ac.sample_times(channel.times, window)
    if measure == 'components':
        denominators = component_denominators(channel)

        def actual_at(t, denominators=denominators):
            return channel.sample(t) / denominators

        def expected_at(t, denominators=denominators):
            value, magnitude = expected(t)
            return np.asarray(value, dtype=np.float64) / denominators, magnitude / float(np.min(denominators))
        return ac.compare_series(record, times, actual_at, expected_at, 'components', bound, 1.0, context)
    return ac.compare_series(record, times, channel.sample, expected, measure, bound, 1.0, context)


def check_package(result, package, dump, sample_id, context):
    """Checks 1-3. Returns (animations, records, pairs)."""
    animations = anim_package.PackageAnimations(package)
    dump_anim = anim_dump.DumpAnimation(dump)
    table = package_rows_table(animations)
    clips_check = result.check(CHECK_CLIPS)
    pairs = []
    clip_rows = []
    for clip in animations.clips:
        label = 'animations/%d (%s)' % (clip['index'], clip['name'])
        source = clip['sourceIndex']
        if source is None or not 0 <= int(source) < len(dump_anim.clips):
            clips_check.fail(Mismatch(label, 'pairing', 'sourceIndex %r is outside the dump\'s %d clips' % (source, len(dump_anim.clips))))
            continue
        source = int(source)
        dclip = dump_anim.clips[source]
        if clip['name'] != dclip.name:
            clips_check.fail(Mismatch(label, 'pairing', 'package %r, dump clip %d %r' % (clip['name'], source, dclip.name)))
            continue
        window = float(clip['duration']) if clip['duration'] is not None else None
        entry = {'package': clip['index'], 'source': source, 'name': dclip.name, 'window': window, 'clipClock': dclip.clock,
                 'authoredDuration': dclip.duration, 'channels': len(clip['channels'])}
        if window is None:
            clips_check.fail(Mismatch(label, 'window', 'no durationSeconds'))
            continue
        if dclip.duration is not None:
            expected_window = float(F32(dclip.duration))
        elif dclip.clock is not None:
            expected_window = float(F32(anim_dump.clip_clock_window(dclip.clock)))
        else:
            expected_window = None
        entry['expectedWindow'] = expected_window
        if expected_window is not None and abs(expected_window - window) > 2 * ac.ulp32(window if window else 1.0):
            clips_check.fail(Mismatch(label + '/window', 'value', 'durationSeconds %r, the source gives %r (duration %r, clock %r)'
                                      % (window, expected_window, dclip.duration, dclip.clock)))
        for channel in clip['channels']:
            for problem in anim_package.admission_problems(channel, window):
                clips_check.fail(Mismatch(channel.label, 'importer', 'the importer refuses this channel: %s' % problem))
        clip_rows.append(entry)
        pairs.append((clip, source, window))
    clips_check.data['clips'] = clip_rows
    emitted = {p[1] for p in pairs}
    not_packaged = []
    for i, dclip in enumerate(dump_anim.clips):
        if i in emitted:
            continue
        driven = ac.driven_track_count(dclip.raw)
        statement = ac.declared_drop(table.get(i))
        not_packaged.append({'source': i, 'name': dclip.name, 'drivenTracks': driven, 'writerStatement': statement})
        if driven and statement is None:
            clips_check.fail(Mismatch('clips/%d (%s)' % (i, dclip.name), 'dropped', 'the dump clip drives %d tracks but the package '
                                      'has no clip for it and no row declares why (a silent drop)' % driven))
    clips_check.data['clipsNotPackaged'] = not_packaged
    if clips_check.passed is None:
        clips_check.ok('%d packaged clips paired, windows agree, every channel admitted; %d dump clips not packaged'
                       % (len(pairs), len(clips_check.data['clipsNotPackaged'])))
    channels_check = result.check(CHECK_CHANNELS)
    records = []
    compared = 0
    for clip, source, window in pairs:
        # The package rows are indexed by the SOURCE clip (BlendAdmission.AddAnimationRows: "source-indexed animation outcomes").
        clip_table = table.get(source, {})
        for channel in clip['channels']:
            record = ac.ChannelRecord(channel.label, (channel.domain, channel.track, channel.property, channel.offset),
                                      channel.interpolation + ('/segments' if channel.segments is not None else ''), int(len(channel.times)))
            try:
                ok = compare_channel(record, channel, dump_anim, source, clip_table, window,
                                     dict(context, clip=clip['name']))
            except anim_dump.DumpUnsupported as failure:
                record.reason = 'not compared: %s' % failure
                records.append(record)
                continue
            compared += 1 if record.compared else 0
            if not ok:
                channels_check.fail(Mismatch(channel.label, 'value', ac.mismatch_detail(record)))
            records.append(record)
    channels_check.data['channelsCompared'] = compared
    channels_check.data['channelsNotCompared'] = sum(1 for r in records if not r.compared)
    channels_check.data['samplesCompared'] = sum(r.samples for r in records)
    channels_check.data['samplesSkipped'] = sum(sum(r.skipped.values()) for r in records)
    channels_check.data['worstRatioToCertificate'] = max([r.worst_ratio for r in records if r.compared] or [0.0])
    channels_check.data['worstRatioToDesignLimit'] = max([r.worst_design_ratio for r in records if r.compared] or [0.0])
    channels_check.data['channels'] = [r.to_dict() for r in records]
    if channels_check.passed is None:
        if compared == 0:
            channels_check.skip('no package channel was compared')
        else:
            channels_check.ok('%d channels within their certificates (%d samples, %d skipped at discontinuities; worst %.3g x certificate, %.3g x design limit)'
                              % (compared, channels_check.data['samplesCompared'], channels_check.data['samplesSkipped'],
                                 channels_check.data['worstRatioToCertificate'], channels_check.data['worstRatioToDesignLimit']))
    check_coverage(result, animations, dump_anim, pairs, context)
    check_declarations(result, animations, dump, pairs)
    return animations, records, pairs


def check_coverage(result, animations, dump_anim, pairs, context):
    check = result.check(CHECK_COVERAGE)
    rows = []
    for clip, source, window in pairs:
        dclip = dump_anim.clips[source]
        packaged = {(c.domain, c.track) for c in clip['channels']}
        driven = {}
        for key, drivers in dclip.drivers.items():
            for driver in drivers:
                driven[(driver.domain, driver.index)] = (key, driver)
        # A track the dump drives but this oracle cannot evaluate (an Xbox 360 Squad, an unimplemented Euler order) is
        # still a driven track: its channel is listed as not compared with the reason, never a pairing failure.
        unevaluable = {(entry[0], entry[1]) for entry in getattr(dclip, 'unsupported', [])}
        for channel in clip['channels']:
            if (channel.domain, channel.track) not in driven and (channel.domain, channel.track) not in unevaluable:
                check.fail(Mismatch(channel.label, 'pairing', 'names %s/%s, which is not a driven track of dump clip %d'
                                    % (channel.domain, channel.track, source)))
        for (domain, index), (key, driver) in driven.items():
            if (domain, index) in packaged:
                continue
            rest = dump_anim.rest(key)
            label = 'animations/%d (%s)/%s/%d (no channel: rest)' % (clip['index'], clip['name'], domain, index)
            record = ac.ChannelRecord(label, (domain, index))
            if rest is None:
                rows.append({'clip': clip['name'], 'track': '%s/%d' % (domain, index), 'state': 'not compared: no rest value'})
                continue
            measure = ac.value_kind(key)
            try:
                ok = ac.compare_series(record, ac.sample_times([], window), lambda t, v=np.asarray(rest, dtype=np.float64): v,
                                       lambda t, k=key: dump_anim.value(source, k, t), measure, ac.Bound('rest', 0, 0, []),
                                       1.0, dict(context, clip=clip['name']))
            except anim_dump.DumpUnsupported as failure:
                rows.append({'clip': clip['name'], 'track': '%s/%d' % (domain, index), 'state': 'not compared: %s' % failure})
                continue
            rows.append({'clip': clip['name'], 'track': '%s/%d' % (domain, index),
                         'state': 'no channel; the dump holds its rest value' if ok else 'MISSING channel'})
            if not ok:
                check.fail(Mismatch(label, 'presence', 'the dump drives %s/%d away from its rest value but the package has no channel: %s'
                                    % (domain, index, ac.mismatch_detail(record))))
        for domain, index, key, reason in dclip.unsupported:
            rows.append({'clip': clip['name'], 'track': '%s/%d' % (domain, index), 'state': 'not compared: %s' % reason})
    check.data['tracks'] = rows[:400]
    if check.passed is None:
        check.ok('every packaged channel names a driven track; %d driven tracks without a channel hold their rest value'
                 % sum(1 for r in rows if r['state'].startswith('no channel')))


CHECK_DECLARATIONS = 'package declarations: clip metadata, per-track metadata and channel targets equal the dump'
DOMAIN_KEYS = {'transform-tracks': 'transformTracks', 'euler-rotation-tracks': 'eulerRotationTracks', 'morph-tracks': 'morphTracks',
               'morph-target-tracks': 'morphTargetTracks', 'property-tracks': 'propertyTracks', 'matrix-tracks': 'matrixTracks'}


def dump_declaration(domain, track):
    """(node, property, target, state) a package channel or metadata entry must declare for one dump track."""
    if domain == 'transform-tracks':
        return int(track['nodeIndex']), track['property'], None, track.get('state')
    if domain == 'euler-rotation-tracks':
        return int(track['nodeIndex']), 'EulerRotation', None, None
    if domain in ('morph-tracks', 'morph-target-tracks'):
        return int(track['nodeIndex']), 'MorphWeights', None, None
    if domain == 'property-tracks':
        target = track['target']
        node = int(target['index']) if target['kind'] == 'NodeVisibility' else -1
        return node, target['kind'], {'kind': target['kind'], 'index': int(target['index']), 'layerIndex': int(target['layerIndex'])}, \
            (track.get('curve') or {}).get('state')
    return None, None, None, None


def check_declarations(result, animations, dump, pairs):
    """Every packaged clip's metadata keeps the dump clip's clock and ordered events; every metadata channel entry names a
    dump track with its node, property, target, state and controller clock; every numeric channel names the node,
    property, target and component offset of the track it plays (a morph-target channel at its target index, an Euler
    channel at its axis)."""
    check = result.check(CHECK_DECLARATIONS)
    entries = 0
    for clip, source, window in pairs:
        dclip = dump.animations[source]
        label = 'animations/%d (%s)' % (clip['index'], clip['name'])
        metadata = clip['raw'].get('metadata') or {}
        if not ac.same_clock(metadata.get('clock'), dclip.get('clock')):
            check.fail(Mismatch(label + '/metadata/clock', 'declaration', 'package %r, dump %r' % (metadata.get('clock'), dclip.get('clock'))))
        if not ac.same_events(metadata.get('events'), dclip.get('events')):
            check.fail(Mismatch(label + '/metadata/events', 'declaration', 'package %d events, dump %d (or a time or text differs)'
                                % (len(metadata.get('events') or []), len(dclip.get('events') or []))))
        declared = set()
        for entry in metadata.get('channels') or []:
            entries += 1
            domain, index = entry.get('sourceDomain'), entry.get('sourceTrack')
            tracks = dclip.get(DOMAIN_KEYS.get(domain, ''), None) or []
            where = '%s/metadata/%s/%s' % (label, domain, index)
            if not isinstance(index, int) or not 0 <= index < len(tracks):
                check.fail(Mismatch(where, 'declaration', 'names no track of the dump clip'))
                continue
            declared.add((domain, index))
            node, prop, target, state = dump_declaration(domain, tracks[index])
            got = (entry.get('node'), entry.get('property'), entry.get('target'))
            if got != (node, prop, target) or (state is not None and entry.get('state') != state) \
                    or not ac.same_clock(entry.get('clock'), tracks[index].get('clock')):
                check.fail(Mismatch(where, 'declaration', 'package node/property/target %r (state %r, clock %r), dump %r (state %r, clock %r)'
                                    % (got, entry.get('state'), entry.get('clock'), (node, prop, target), state, tracks[index].get('clock'))))
        for domain, key in DOMAIN_KEYS.items():
            for index, _ in enumerate(dclip.get(key) or []):
                if metadata.get('channels') is not None and (domain, index) not in declared:
                    check.fail(Mismatch('%s/metadata/%s/%d' % (label, domain, index), 'declaration', 'the dump track has no metadata entry'))
        for channel in clip['channels']:
            tracks = dclip.get(DOMAIN_KEYS.get(channel.domain, ''), None) or []
            if not isinstance(channel.track, int) or not 0 <= channel.track < len(tracks):
                continue  # reported by the coverage check
            track = tracks[channel.track]
            node, prop, target, _ = dump_declaration(channel.domain, track)
            offset_ok = True
            if channel.domain == 'morph-target-tracks':
                offset_ok = channel.offset == int(track['targetIndex']) and channel.width == 1
            elif channel.domain == 'euler-rotation-tracks':
                offset_ok = 0 <= channel.offset < 3 and channel.width == 1
            elif channel.domain == 'morph-tracks':
                offset_ok = channel.offset == 0 and channel.width == int(track['targetCount'])
            if (channel.node, channel.property, channel.target) != (node, prop, target) or not offset_ok:
                check.fail(Mismatch(channel.label, 'declaration', 'channel node/property/target/offset/width %r, the dump track declares %r'
                                    % ((channel.node, channel.property, channel.target, channel.offset, channel.width), (node, prop, target))))
    check.data['metadataEntries'] = entries
    if check.passed is None:
        check.ok('%d clips: metadata clock and events equal the dump; %d metadata entries and every channel name their track exactly'
                 % (len(pairs), entries))


# ---------------------------------------------------------------------------------------------------- controls
def rewrite_manifest(source_path, target_path, edit):
    """A copy of the package whose manifest.json is ``edit(manifest)`` (streams and images unchanged)."""
    def mutate(name, data):
        if name != 'manifest.json':
            return data
        manifest = json_loads(data.decode('utf-8'))
        edit(manifest)
        return json.dumps(manifest, separators=(',', ':')).encode('utf-8')
    mutate_package(source_path, target_path, mutate)


def rewrite_values(source_path, target_path, stream_path, values):
    def mutate(name, data):
        if name != stream_path:
            return data
        return np.asarray(values, dtype='<f4').reshape(-1).tobytes()
    mutate_package(source_path, target_path, mutate)


def by_label(records):
    return {r.label: r for r in records}


def pick_segment_flip(animations, records, pairs):
    """The first compared CubicSpline channel and span whose LINEAR replacement moves the evaluation by more than ten
    times the channel's allowance (a cubic span that is already linear could never be detected)."""
    lookup = by_label(records)
    for clip, source, window in pairs:
        for channel in clip['channels']:
            record = lookup.get(channel.label)
            if record is None or not record.compared or channel.interpolation != 'CubicSpline' or len(channel.times) < 2:
                continue
            for k in range(len(channel.times) - 1):
                if channel.mode(k) != 'CubicSpline':
                    continue
                t = float(channel.times[k]) + 0.5 * (float(channel.times[k + 1]) - float(channel.times[k]))
                cubic = channel.sample(t)
                segments = [channel.mode(j) for j in range(len(channel.times) - 1)]
                segments[k] = 'Linear'
                original = channel.segments
                channel.segments = segments
                channel._curves = None
                linear = channel.sample(t)
                channel.segments = original
                channel._curves = None
                kind = 'rotation' if channel.property == 'Rotation' else 'components'
                change = ac.deviation(kind, cubic / (1 if kind == 'rotation' else component_denominators(channel)),
                                      linear / (1 if kind == 'rotation' else component_denominators(channel)))
                if change > 10 * max(record.bound.maximum if record.bound else 0.0, 1e-6):
                    return channel, k, segments, change
    return None


def mutate_segment_flip(package_path, target_path, animations, records, pairs):
    picked = pick_segment_flip(animations, records, pairs)
    if picked is None:
        return None
    channel, k, segments, change = picked

    def edit(manifest):
        manifest['animations'][channel.clip_index]['channels'][channel.index]['segmentInterpolations'] = segments
    rewrite_manifest(package_path, target_path, edit)
    return {'where': channel.label, 'kind': 'value', 'channel': channel.label, 'span': k, 'change': change,
            'description': '%s span %d (t=%r..%r) flipped from CubicSpline to Linear in segmentInterpolations (moves the midpoint by %.4g)'
                           % (channel.label, k, float(channel.times[k]), float(channel.times[k + 1]), change)}


def mutate_sign_flip(package_path, target_path, animations, records, pairs):
    lookup = by_label(records)
    for clip, source, window in pairs:
        for channel in clip['channels']:
            record = lookup.get(channel.label)
            if record is None or not record.compared or channel.property != 'Rotation' or len(channel.times) < 3:
                continue
            modes = [channel.mode(j) for j in range(len(channel.times) - 1)]
            for k in range(1, len(channel.times) - 1):
                if modes[k - 1] == 'Step' and modes[k] == 'Step':
                    continue
                values = channel.values.copy()
                values[k] = -values[k]
                rewrite_values(package_path, target_path, channel.raw['values']['path'], values)
                return {'where': channel.label, 'kind': 'value', 'channel': channel.label, 'key': k,
                        'description': '%s key %d negated (%s%s)' % (channel.label, k, channel.interpolation,
                                                                     ', ' + channel.rotation_curve if channel.rotation_curve else '')}
    return None


def mutate_visibility(package_path, target_path, animations, records, pairs):
    lookup = by_label(records)
    for clip, source, window in pairs:
        for channel in clip['channels']:
            record = lookup.get(channel.label)
            if record is None or not record.compared or channel.property != 'NodeVisibility':
                continue
            values = channel.values.copy()
            values[0] = 0.0 if float(values[0, 0, 0]) != 0 else 1.0
            rewrite_values(package_path, target_path, channel.raw['values']['path'], values)
            return {'where': channel.label, 'kind': 'value', 'channel': channel.label, 'key': 0,
                    'description': '%s key 0 inverted (%r -> %r)' % (channel.label, float(channel.values[0, 0, 0]), float(values[0, 0, 0]))}
    return None


CONTROL_SEGMENT_FLIP = 'package cubic span flipped to LINEAR in segmentInterpolations in a copy of the package'
CONTROL_SIGN_FLIP = 'quaternion sign flip on one package rotation key in a copy of the package'
CONTROL_VISIBILITY = 'one package NodeVisibility value inverted in a copy of the package'
CONTROLS = {
    CONTROL_SEGMENT_FLIP: {'mutate': mutate_segment_flip, 'file': 'control-segment-flip.zip',
                           'notApplicable': 'not applicable: no compared CubicSpline span whose LINEAR replacement is visible'},
    CONTROL_SIGN_FLIP: {'mutate': mutate_sign_flip, 'file': 'control-sign-flip.zip',
                        'notApplicable': 'not applicable: no compared rotation channel with an interior non-STEP key'},
    CONTROL_VISIBILITY: {'mutate': mutate_visibility, 'file': 'control-visibility.zip',
                         'notApplicable': 'not applicable: no compared NodeVisibility channel'},
}
CONTROL_ORDER = (CONTROL_SEGMENT_FLIP, CONTROL_SIGN_FLIP, CONTROL_VISIBILITY)


def control_verdict(probe, pristine, where, kind):
    pristine_hits = [m for c in pristine.checks for m in c.mismatches if m.where == where]
    if pristine_hits:
        return None, 'not applicable: the pristine check already reports %s at %s' % (pristine_hits[0].kind, where)
    hits = [m for c in probe.checks for m in c.mismatches if m.where == where and m.kind == kind]
    if hits:
        others = sorted({m.kind for c in probe.checks for m in c.mismatches if m.where == where})
        return True, 'mismatch reported at %s (%s): %s' % (where, '/'.join(others), hits[0].detail[:300])
    others = [m for c in probe.checks for m in c.mismatches]
    return False, 'NOT detected (expected a %s mismatch at %s; the probe reported %s)' % (
        kind, where, ('%s at %s' % (others[0].kind, others[0].where)) if others else 'nothing')


def run_control(name, sample_id, package_path, dump, pristine, animations, records, pairs, context, work_dir):
    spec = CONTROLS[name]
    control = {'name': name, 'detected': None, 'detail': None}
    os.makedirs(work_dir, exist_ok=True)
    path = os.path.join(work_dir, spec['file'])
    description = spec['mutate'](package_path, path, animations, records, pairs)
    if description is None:
        control['detail'] = spec['notApplicable']
        return control
    probe = HopResult('C-anim-control', sample_id)
    mutated = Package(path)
    try:
        check_package(probe, mutated, dump, sample_id, context)
    finally:
        mutated.close()
    detected, verdict = control_verdict(probe, pristine, description['where'], description['kind'])
    control['detected'] = detected
    control['detail'] = '%s: %s' % (description['description'], verdict)
    control['expectedMismatchWhere'] = description['where']
    control['mutation'] = description
    control['files'] = {'package': path, 'packageSha256': sha256_file(path)}
    return control


def run(sample, work_dir):
    result = HopResult('C-anim', sample['id'])
    try:
        if not sample.get('package') or not sample.get('dump'):
            result.check('package animation versus dump').skip('package or dump not supplied')
            return result
        package = Package(sample['package'])
        dump = Dump(sample['dump'])
        if package.version < 4 or not package.manifest.get('animations'):
            if not dump.animations:
                result.check('package animation versus dump').skip('neither the package (version %d) nor the dump carries animation' % package.version)
                package.close()
                return result
            # The dump has clips the package does not carry: each must be declared by a 'clip' row.
            check = result.check(CHECK_CLIPS)
            table = ac.rows_by_clip(package.manifest.get('fidelity') or [])
            statements = []
            for i, dclip in enumerate(dump.animations):
                driven = ac.driven_track_count(dclip)
                statement = ac.declared_drop(table.get(i))
                statements.append({'source': i, 'name': dclip.get('name'), 'drivenTracks': driven, 'writerStatement': statement})
                if driven and statement is None:
                    check.fail(Mismatch('clips/%d (%s)' % (i, dclip.get('name')), 'dropped', 'the dump clip drives %d tracks but the '
                                        'package (version %d) carries no clip and no row declares why' % (driven, package.version)))
            check.data['clipsNotPackaged'] = statements
            if check.passed is not False:
                check.skip('the package (version %d) carries no animations; the dump has %d clips, each declared: %s' % (
                    package.version, len(dump.animations),
                    '; '.join('%s: %s' % (s['name'], s['writerStatement'] or 'no driven track') for s in statements)[:900]))
            package.close()
            return result
        context = {'dontSample': ac.load_dont_sample(), 'sample': sample['id']}
        animations, records, pairs = check_package(result, package, dump, sample['id'], context)
        package.close()
        for name in CONTROL_ORDER:
            result.controls.append(run_control(name, sample['id'], sample['package'], dump, result, animations, records, pairs,
                                               context, work_dir))
    except OracleError as failure:
        result.error = str(failure)
    except Exception as failure:  # noqa: BLE001 - an oracle defect must reach the receipt, not abort the run
        result.error = 'oracle exception %s: %s' % (type(failure).__name__, failure)
        result.traceback = traceback.format_exc()
    return result


def synthetic_control_exercise(control_name, work_dir=None, builder=None):
    if control_name not in CONTROLS:
        return None
    import gate1b_synthetic
    work_dir = work_dir or tempfile.mkdtemp(prefix='gate1b-synthetic-c-')
    artifacts = (builder or gate1b_synthetic.write_pristine_set)(work_dir)
    dump = Dump(artifacts['dump'])
    pristine = HopResult('C-anim-synthetic', 'synthetic')
    context = {'dontSample': [], 'sample': 'synthetic'}
    package = Package(artifacts['package'])
    try:
        animations, records, pairs = check_package(pristine, package, dump, 'synthetic', context)
    finally:
        package.close()
    control = run_control(control_name, 'synthetic', artifacts['package'], dump, pristine, animations, records, pairs, context, work_dir)
    failed = [c for c in pristine.checks if c.passed is False]
    pristine_passed = not failed
    verdict = control.get('detected')
    return {
        'control': control_name, 'detected': verdict is True and pristine_passed, 'verdict': verdict,
        'pristinePassed': pristine_passed,
        'detail': '%s (synthetic set; %s)' % (control.get('detail'), 'pristine checks passed' if pristine_passed else
                                              'pristine %s FAILED, so the exercise reads NOT detected' % ', '.join(c.name for c in failed)),
        'expectedMismatchWhere': control.get('expectedMismatchWhere'),
        'artifactSha256': dict(artifacts['sha256'], mutatedPackage=(control.get('files') or {}).get('packageSha256')),
        'pristineChecks': [{'name': c.name, 'status': c.to_dict()['status'], 'detail': c.detail,
                            'firstMismatch': c.mismatches[0].to_dict() if c.mismatches else None} for c in pristine.checks],
        'files': dict(artifacts.get('files') or {}, mutatedPackage=(control.get('files') or {}).get('package')),
        'workDir': work_dir,
    }
