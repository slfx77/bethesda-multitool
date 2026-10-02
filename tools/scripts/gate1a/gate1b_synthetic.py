# SPDX-License-Identifier: 0BSD
"""Gate 1b's hand-built artifact sets (one implementation): the pristine animated set every hop's synthetic control
exercise runs on, and the 170-degree control.

**The pristine set** (``write_pristine_set``): a dump with one unclocked clip over [0, 1.25] s driving a Hermite
translation (normalized-segment tangents), a LINEAR rotation (Shared's slerp), two morph-target weights, a node
visibility STEP, a material alpha and a base-color layer's U offset; the GLB a writer emits for it (CUBICSPLINE
translation with the tangents divided by the key interval, LINEAR rotation, one LINEAR weights sampler over the union
of the targets' keys, a KHR_node_visibility STEP byte channel and two KHR_animation_pointer channels on
baseColorFactor and the base-color KHR_texture_transform offset); the GLB fidelity rows (``clock/value`` per track,
``morph-target-nodes/2/packing``) in both ``fidelity.json`` and the GLB carrier; and the Blender package v4 of the
same clip (a CubicSpline translation with explicit ``segmentInterpolations``, a LINEAR raw-quaternion rotation, per
target MorphWeights, NodeVisibility, MaterialAlpha and LayerOffsetU channels, with per-component ``clock-retiming``
rows). ``write_pristine_set(..., rotation='CubicSpline')`` swaps the rotation for a CubicSpline one (per-second
tangents, the dump's triples written unchanged as a glTF CUBICSPLINE sampler) and writes no package: hop B-anim's
sign-flip control needs a cubic rotation, because on a LINEAR one the specification's slerp makes a key's sign free.
The certificates stated in the rows are MEASURED by this module on a 4,001-point grid with the harness's own
samplers and rounded up (a writer states what it measured; a set whose rows understate it fails its own pristine
checks, which is what keeps a drifted set from standing in for a detection).

**The 170-degree control** (``run_170_degree_control``), reusing slice 9's synthetic pair
(``tests/.../NifAnimationRotationPathOracleTests.OneHundredSeventyDegreePair_Synthetic``: first = identity, second =
(axis sin 85 deg, cos 85 deg) with axis = Vector3.Normalize(1, 2, 3) in Float32, keys at 0 s and 1 s, interpolation
GamebryoCounterWarpedNlerp) and its measured figures (receipt ``TestOutput/cut1b-slice9-20260927/
NifAnimationRotationPathOracleTests``: plain slerp and plain nlerp of the pair differ by 6.757577503019481 degrees;
against the engine's counter-warped nlerp a plain slerp misses by 1.7896241134089974 and a plain nlerp by
8.473070952612225). A writer that inserts keys (257 here) passes hops B-anim and C-anim; the same two keys written
without insertion, as a GLB LINEAR (slerp) channel and as a package LINEAR (component nlerp) channel, must each be
reported, and the slerp-versus-nlerp figure must exceed 6 degrees. The control is detected when all of that holds and
the three measured figures reproduce slice 9's within 0.01 degrees.
"""

import json
import math
import os
import zipfile

import numpy as np

import anim_compare as ac
import anim_glb
import anim_package
from gate1a_common import HopResult, sha256_file
from glb_reader import Glb
from hop_b import rebuild_glb

F32 = np.float32
METERS_PER_UNIT = 0.014287673738112657
WINDOW = 1.25
SLICE9_SLERP_VERSUS_NLERP = 6.757577503019481
SLICE9_SLERP_MISS = 1.7896241134089974
SLICE9_NLERP_MISS = 8.473070952612225
DETECTS_NLERP_DEGREES = 6.0


def f32list(values):
    return [float(F32(v)) for v in values]


def quat_axis_angle(axis, degrees):
    half = math.radians(degrees) / 2
    norm = math.sqrt(sum(a * a for a in axis))
    return [axis[0] / norm * math.sin(half), axis[1] / norm * math.sin(half), axis[2] / norm * math.sin(half), math.cos(half)]


# ------------------------------------------------------------------------------------------------ the dump
TRANSLATION_TIMES = [0.0, 0.5, 1.25]
# (incoming, value, outgoing) per key, normalized-segment tangents (Shared SceneCurve: Hermite triples).
TRANSLATION_KEYS = [([0.0, 0.0, 0.0], [1.0, 2.0, 3.0], [0.8, -0.4, 0.2]),
                    ([0.6, 0.3, -0.2], [1.5, 1.6, 3.4], [0.9, 0.1, 0.4]),
                    ([0.3, -0.6, 0.5], [2.25, 1.0, 3.8], [0.0, 0.0, 0.0])]
ROTATION_TIMES = [0.0, 0.5, 1.0, 1.25]
ROTATION_KEYS = [quat_axis_angle((0, 0, 1), 0), quat_axis_angle((0, 0, 1), 10), quat_axis_angle((0, 1, 1), 20),
                 quat_axis_angle((0, 1, 1), 24)]


def rotation_cubic_keys():
    """(incoming, value, outgoing) per ROTATION_KEYS key: per-second central-difference tangents, zero at both ends."""
    keys = []
    n = len(ROTATION_TIMES)
    for k, q in enumerate(ROTATION_KEYS):
        if 0 < k < n - 1:
            span = ROTATION_TIMES[k + 1] - ROTATION_TIMES[k - 1]
            tangent = [(b - a) / span for a, b in zip(ROTATION_KEYS[k - 1], ROTATION_KEYS[k + 1])]
        else:
            tangent = [0.0, 0.0, 0.0, 0.0]
        keys.append((tangent, list(q), tangent))
    return keys


TARGET0_TIMES, TARGET0_VALUES = [0.0, 0.75, 1.25], [0.0, 1.0, 0.25]
TARGET1_TIMES, TARGET1_VALUES = [0.0, 1.25], [0.5, 0.0]
VISIBILITY_TIMES, VISIBILITY_VALUES = [0.0, 0.625], [1.0, 0.0]
ALPHA_TIMES, ALPHA_VALUES = [0.0, 1.25], [1.0, 0.5]
OFFSET_TIMES, OFFSET_VALUES = [0.0, 1.25], [0.0, 0.5]


def document(rotation='Linear'):
    """The synthetic dump document (schema 1): four nodes, one morphed mesh, one material with a base-color layer.
    ``rotation`` is the rotation track's interpolation: 'Linear' (ROTATION_KEYS) or 'CubicSpline' (rotation_cubic_keys)."""
    identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
    trs = lambda t: {'translation': t, 'rotation': [0, 0, 0, 1], 'scale': [1, 1, 1]}
    nodes = [
        {'name': 'SynthRoot', 'localTransform': identity, 'localTrs': trs([0, 0, 0]), 'children': [1, 2, 3], 'meshIndex': None,
         'skinIndex': None, 'role': 'Transform', 'morphWeights': None},
        {'name': 'Mover', 'localTransform': identity[:12] + [1, 2, 3, 1], 'localTrs': trs([1, 2, 3]), 'children': [], 'meshIndex': None,
         'skinIndex': None, 'role': 'Transform', 'morphWeights': None},
        {'name': 'Morphed', 'localTransform': identity, 'localTrs': trs([0, 0, 0]), 'children': [], 'meshIndex': 0,
         'skinIndex': None, 'role': 'Transform', 'morphWeights': None},
        {'name': 'Blinker', 'localTransform': identity, 'localTrs': trs([0, 0, 0]), 'children': [], 'meshIndex': None,
         'skinIndex': None, 'role': 'Transform', 'morphWeights': None},
    ]
    vertices = [{'position': p, 'normal': [0, 0, 1], 'color': [1, 1, 1, 1], 'texCoord': [p[0], p[1]]}
                for p in ([0, 0, 0], [1, 0, 0], [0, 1, 0])]
    primitive = {'name': 'prim0', 'vertices': vertices, 'indices': [0, 1, 2], 'materialIndex': 0, 'purpose': 'Render',
                 'normalMode': 'Smooth',
                 'morphTargets': [{'name': 'Open', 'positionDeltas': [[0, 0, 1], [0, 0, 0], [0, 0, 0]]},
                                  {'name': 'Close', 'positionDeltas': [[0, 0, 0], [0, 0, -1], [0, 0, 0]]}]}
    layer = {'role': 'BaseColor', 'binding': {'imageIndex': None, 'samplerIndex': None, 'textureCoordinateSet': 0, 'transform': None}}
    material = {'name': 'SynthMaterial', 'baseColor': [1, 1, 1, 1], 'emissiveFactor': [0, 0, 0], 'emissiveStrength': 1,
                'specularColorFactor': [1, 1, 1], 'layers': [layer],
                'source': {'baseColor': [1, 1, 1, 1], 'emissiveColor': [0, 0, 0], 'emissiveMultiplier': 1, 'layers': [layer]}}

    def curve(times, values, interpolation, width=1):
        return {'componentCount': width, 'state': 'Keyed', 'times': f32list(times), 'values': f32list(values),
                'interpolation': interpolation, 'staticValue': None, 'spline': None, 'tbcParameters': None, 'tbcEndpoints': None}

    def transform(node, prop, times, values, interpolation, state='Keyed'):
        return {'nodeIndex': node, 'property': prop, 'times': f32list(times), 'values': f32list(values),
                'interpolation': interpolation, 'clock': None, 'state': state, 'staticValue': None, 'spline': None,
                'tbcParameters': None, 'tbcEndpoints': None, 'gamebryoSquadPolicy': None}

    translation_values = [c for key in TRANSLATION_KEYS for part in key for c in part]
    if rotation == 'Linear':
        rotation_values = [c for q in ROTATION_KEYS for c in q]
    elif rotation == 'CubicSpline':
        rotation_values = [c for key in rotation_cubic_keys() for part in key for c in part]
    else:
        raise ValueError('synthetic rotation must be Linear or CubicSpline, not %r' % rotation)
    clip = {
        'name': 'SynthClip', 'durationSeconds': None, 'clock': None, 'events': [], 'timing': None, 'sourcePolicy': None,
        'transformTracks': [transform(1, 'Translation', TRANSLATION_TIMES, translation_values, 'Hermite'),
                            transform(1, 'Rotation', ROTATION_TIMES, rotation_values, rotation),
                            transform(1, 'Scale', [], [], 'Linear', 'NotDriven')],
        'eulerRotationTracks': [], 'matrixTracks': [], 'transformBases': [], 'morphTracks': [],
        'morphTargetTracks': [{'nodeIndex': 2, 'targetIndex': 0, 'weight': curve(TARGET0_TIMES, TARGET0_VALUES, 'Linear'), 'clock': None},
                              {'nodeIndex': 2, 'targetIndex': 1, 'weight': curve(TARGET1_TIMES, TARGET1_VALUES, 'Linear'), 'clock': None}],
        'propertyTracks': [
            {'target': {'kind': 'NodeVisibility', 'index': 3, 'layerIndex': -1}, 'curve': curve(VISIBILITY_TIMES, VISIBILITY_VALUES, 'Step'), 'clock': None},
            {'target': {'kind': 'MaterialAlpha', 'index': 0, 'layerIndex': -1}, 'curve': curve(ALPHA_TIMES, ALPHA_VALUES, 'Linear'), 'clock': None},
            {'target': {'kind': 'LayerOffsetU', 'index': 0, 'layerIndex': 0}, 'curve': curve(OFFSET_TIMES, OFFSET_VALUES, 'Linear'), 'clock': None},
        ],
    }
    return {'name': 'gate1b-synthetic', 'sourceFormat': 'synthetic', 'nodes': nodes,
            'scenes': [{'name': 'scene', 'rootNodeIndices': [0]}], 'defaultSceneIndex': 0,
            'meshes': [{'name': 'MorphMesh', 'primitives': [primitive], 'morphWeights': None}],
            'materials': [material], 'images': [], 'samplers': [], 'skins': [], 'animations': [clip],
            'units': {'metersPerUnit': METERS_PER_UNIT}, 'sourceBasis': None}


# --------------------------------------------------------------------------------------------- the GLB
class BinBuilder:
    def __init__(self, document):
        self.document = document
        self.bin = bytearray()
        document.setdefault('accessors', [])
        document.setdefault('bufferViews', [])

    def add(self, values, gltf_type, component_type=5126):
        dtype = {5126: '<f4', 5121: '<u1'}[component_type]
        data = np.asarray(values, dtype=dtype).tobytes()
        while len(self.bin) % 4:
            self.bin.append(0)
        view = {'buffer': 0, 'byteOffset': len(self.bin), 'byteLength': len(data)}
        self.bin.extend(data)
        self.document['bufferViews'].append(view)
        components = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}[gltf_type]
        accessor = {'bufferView': len(self.document['bufferViews']) - 1, 'componentType': component_type,
                    'count': int(np.asarray(values).size // components), 'type': gltf_type}
        if gltf_type == 'SCALAR' and component_type == 5126 and np.asarray(values).size:
            accessor['min'] = [float(np.min(values))]
            accessor['max'] = [float(np.max(values))]
        self.document['accessors'].append(accessor)
        return len(self.document['accessors']) - 1

    def finish(self):
        while len(self.bin) % 4:
            self.bin.append(0)
        self.document['buffers'] = [{'byteLength': len(self.bin)}]
        return rebuild_glb(self.document, bytes(self.bin))


def gltf_translation_keys():
    """Hermite normalized-segment tangents as glTF per-second tangents: out_k / (t_{k+1} - t_k), in_k / (t_k - t_{k-1})."""
    values = []
    n = len(TRANSLATION_TIMES)
    for k, (incoming, value, outgoing) in enumerate(TRANSLATION_KEYS):
        a = [c / (TRANSLATION_TIMES[k] - TRANSLATION_TIMES[k - 1]) for c in incoming] if k > 0 else [0.0, 0.0, 0.0]
        b = [c / (TRANSLATION_TIMES[k + 1] - TRANSLATION_TIMES[k]) for c in outgoing] if k + 1 < n else [0.0, 0.0, 0.0]
        values.extend(a + value + b)
    return values


def union_weights():
    knots = sorted(set(TARGET0_TIMES) | set(TARGET1_TIMES))
    values = []
    for t in knots:
        values.append(float(np.interp(t, TARGET0_TIMES, TARGET0_VALUES)))
        values.append(float(np.interp(t, TARGET1_TIMES, TARGET1_VALUES)))
    return knots, values


def glb_document(rows, rotation='Linear'):
    gltf = {'asset': {'version': '2.0', 'generator': 'gate1b_synthetic'},
            'extensionsUsed': ['KHR_animation_pointer', 'KHR_node_visibility', 'KHR_texture_transform'],
            'extensionsRequired': ['KHR_animation_pointer'],
            'scene': 0, 'scenes': [{'nodes': [4]}],
            'nodes': [{'name': 'SynthRoot', 'children': [1, 2, 3]},
                      {'name': 'Mover', 'translation': [1, 2, 3]},
                      {'name': 'Morphed', 'mesh': 0},
                      {'name': 'Blinker', 'extensions': {'KHR_node_visibility': {}}},
                      {'name': 'multitool coordinates/0', 'children': [0],
                       'matrix': [METERS_PER_UNIT, 0, 0, 0, 0, 0, -METERS_PER_UNIT, 0, 0, METERS_PER_UNIT, 0, 0, 0, 0, 0, 1]}],
            'materials': [{'name': 'SynthMaterial', 'pbrMetallicRoughness': {
                'baseColorFactor': [1, 1, 1, 1],
                'baseColorTexture': {'index': 0, 'extensions': {'KHR_texture_transform': {'offset': [0, 0]}}}}}],
            'textures': [{}],
            'meshes': [{'name': 'MorphMesh', 'weights': [0, 0], 'primitives': [
                {'attributes': {}, 'targets': [{}, {}], 'material': 0, 'extras': {'multitoolPrimitiveName': 'prim0'}}]}],
            'extras': {'multitoolFidelity': {'rows': rows},
                       'multitoolAnimationMetadata': {'version': 1, 'clips': [
                           {'sourceAnimation': 0, 'outputAnimation': 0, 'name': 'SynthClip', 'durationSeconds': None,
                            'clock': None, 'outputWindowSeconds': WINDOW}]}}}
    builder = BinBuilder(gltf)
    position = builder.add([[0, 0, 0], [1, 0, 0], [0, 1, 0]], 'VEC3')
    gltf['meshes'][0]['primitives'][0]['attributes']['POSITION'] = position
    gltf['meshes'][0]['primitives'][0]['targets'] = [{'POSITION': builder.add([[0, 0, 1], [0, 0, 0], [0, 0, 0]], 'VEC3')},
                                                     {'POSITION': builder.add([[0, 0, 0], [0, 0, -1], [0, 0, 0]], 'VEC3')}]
    samplers, channels = [], []

    def channel(times, values, gltf_type, interpolation, target, component_type=5126):
        samplers.append({'input': builder.add(times, 'SCALAR'), 'output': builder.add(values, gltf_type, component_type),
                         'interpolation': interpolation})
        channels.append({'sampler': len(samplers) - 1, 'target': target})

    channel(TRANSLATION_TIMES, gltf_translation_keys(), 'VEC3', 'CUBICSPLINE', {'node': 1, 'path': 'translation'})
    if rotation == 'CubicSpline':
        channel(ROTATION_TIMES, [c for key in rotation_cubic_keys() for part in key for c in part], 'VEC4', 'CUBICSPLINE',
                {'node': 1, 'path': 'rotation'})
    else:
        channel(ROTATION_TIMES, [c for q in ROTATION_KEYS for c in q], 'VEC4', 'LINEAR', {'node': 1, 'path': 'rotation'})
    knots, weights = union_weights()
    channel(knots, weights, 'SCALAR', 'LINEAR', {'node': 2, 'path': 'weights'})
    pointer = lambda p: {'path': 'pointer', 'extensions': {'KHR_animation_pointer': {'pointer': p}}}
    channel(VISIBILITY_TIMES, [int(v) for v in VISIBILITY_VALUES], 'SCALAR', 'STEP',
            pointer('/nodes/3/extensions/KHR_node_visibility/visible'), 5121)
    channel(ALPHA_TIMES, [c for a in ALPHA_VALUES for c in (1, 1, 1, a)], 'VEC4', 'LINEAR',
            pointer('/materials/0/pbrMetallicRoughness/baseColorFactor'))
    channel(OFFSET_TIMES, [c for u in OFFSET_VALUES for c in (u, 0)], 'VEC2', 'LINEAR',
            pointer('/materials/0/pbrMetallicRoughness/baseColorTexture/extensions/KHR_texture_transform/offset'))
    gltf['animations'] = [{'name': 'SynthClip', 'samplers': samplers, 'channels': channels}]
    return builder.finish()


# ------------------------------------------------------------------------------------------ the package
def package_manifest(rows, rotation_values=None, rotation_times=None):
    identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
    trs = lambda t: {'translation': t, 'rotation': [0, 0, 0, 1], 'scale': [1, 1, 1]}
    nodes = [{'name': 'SynthRoot', 'parent': None, 'children': [1, 2, 3], 'localMatrix': identity, 'trs': trs([0, 0, 0])},
             {'name': 'Mover', 'parent': 0, 'children': [], 'localMatrix': identity[:12] + [1, 2, 3, 1], 'trs': trs([1, 2, 3])},
             {'name': 'Morphed', 'parent': 0, 'children': [], 'localMatrix': identity, 'trs': trs([0, 0, 0]), 'mesh': 0},
             {'name': 'Blinker', 'parent': 0, 'children': [], 'localMatrix': identity, 'trs': trs([0, 0, 0])}]
    return {'packageVersion': 4, 'name': 'gate1b-synthetic', 'nodes': nodes,
            'meshes': [{'name': 'MorphMesh', 'primitives': [{'name': 'prim0', 'material': 0, 'morphs': [{'name': 'Open'}, {'name': 'Close'}]}]}],
            'materials': [{'name': 'SynthMaterial'}], 'images': [], 'animationCount': 1, 'fidelity': rows}


def package_channels():
    """(manifest channel dict without streams, times, values) of every synthetic package channel."""
    out = []

    def add(domain, track, node, prop, width, interpolation, times, values, offset=0, target=None, extra=None):
        raw = {'sourceDomain': domain, 'sourceTrack': track, 'node': node, 'property': prop, 'componentOffset': offset,
               'width': width, 'interpolation': interpolation}
        if target is not None:
            raw['target'] = target
        raw.update(extra or {})
        out.append((raw, f32list(times), f32list(values)))

    add('transform-tracks', 0, 1, 'Translation', 3, 'CubicSpline', TRANSLATION_TIMES, gltf_translation_keys(),
        extra={'segmentInterpolations': ['CubicSpline', 'CubicSpline']})
    add('transform-tracks', 1, 1, 'Rotation', 4, 'Linear', ROTATION_TIMES, [c for q in ROTATION_KEYS for c in q])
    add('morph-target-tracks', 0, 2, 'MorphWeights', 1, 'Linear', TARGET0_TIMES, TARGET0_VALUES, offset=0)
    add('morph-target-tracks', 1, 2, 'MorphWeights', 1, 'Linear', TARGET1_TIMES, TARGET1_VALUES, offset=1)
    add('property-tracks', 0, 3, 'NodeVisibility', 1, 'Step', VISIBILITY_TIMES, VISIBILITY_VALUES,
        target={'kind': 'NodeVisibility', 'index': 3, 'layerIndex': -1})
    add('property-tracks', 1, -1, 'MaterialAlpha', 1, 'Linear', ALPHA_TIMES, ALPHA_VALUES,
        target={'kind': 'MaterialAlpha', 'index': 0, 'layerIndex': -1})
    add('property-tracks', 2, -1, 'LayerOffsetU', 1, 'Linear', OFFSET_TIMES, OFFSET_VALUES,
        target={'kind': 'LayerOffsetU', 'index': 0, 'layerIndex': 0})
    return out


def write_package(path, manifest, channels, clip_name='SynthClip', duration=WINDOW):
    entries = []
    manifest_channels = []
    for index, (raw, times, values) in enumerate(channels):
        base = 'streams/animation_0000_channel_%04d' % index
        raw = dict(raw)
        for name, data in (('times', times), ('values', values)):
            stream = np.asarray(data, dtype='<f4').tobytes()
            raw[name] = {'path': '%s.%s.bin' % (base, name), 'componentType': 'Float32', 'components': 1,
                         'count': len(data), 'byteStride': 4, 'byteLength': len(stream)}
            entries.append((raw[name]['path'], stream))
        manifest_channels.append(raw)
    manifest = dict(manifest)
    manifest['animations'] = [{'sourceIndex': 0, 'name': clip_name, 'durationSeconds': duration, 'channels': manifest_channels,
                               'metadata': {'sourceName': clip_name}, 'extras': {}}]
    with zipfile.ZipFile(path, 'w', compression=zipfile.ZIP_STORED) as archive:
        archive.writestr('manifest.json', json.dumps(manifest))
        for name, data in entries:
            archive.writestr(name, data)


# ------------------------------------------------------------------------------------- measured certificates
def grid(window=WINDOW, points=4001):
    return [window * i / (points - 1) for i in range(points)]


def row_writer():
    """(rows, row): a list of GLB fidelity rows and the function appending one ``clock/value``-shaped row to it."""
    rows = []

    def row(feature, unit, maximum, observed, reason='animation.clock-value-bounded'):
        rows.append({'target': {'kind': 'Animation', 'index': 0, 'primitiveIndex': None, 'morphTargetIndex': None},
                     'featureId': feature, 'outcome': 'Approximated' if observed else 'Exact', 'reasonCode': reason,
                     'description': 'synthetic', 'isPending': False,
                     'bounds': {'domain': 'synthetic', 'unit': unit, 'maximumError': maximum, 'observedError': observed,
                                'isWithinLimit': True}})
    return rows, row


def measure(actual_at, expected_at, kind, factor=1.0, times=None):
    """The largest deviation of ``actual_at`` from ``expected_at`` over ``times`` (default: 4,001 points over the
    synthetic window), each time rounded to Float32."""
    worst = 0.0
    for t in (times if times is not None else grid()):
        t32 = float(F32(t))
        worst = max(worst, ac.deviation(kind, actual_at(t32), expected_at(t32), factor))
    return worst


def round_up(value):
    return 0.0 if value == 0 else float(np.nextafter(value * 1.01 + 1e-15, np.inf))


def write_pristine_set(work_dir, mutate_rows=None, rotation='Linear'):
    """dump.json, model.glb, fidelity.json and package.zip under ``work_dir``; returns their paths, SHA-256s and bytes.
    With ``rotation='CubicSpline'`` the rotation is cubic and no package is written (``package`` is None)."""
    import anim_dump
    from dump_reader import Dump
    os.makedirs(work_dir, exist_ok=True)
    dump_path = os.path.join(work_dir, 'dump.json')
    with open(dump_path, 'w', encoding='utf-8') as f:
        json.dump({'schemaVersion': 1, 'document': document(rotation)}, f)
    dump_anim = anim_dump.DumpAnimation(Dump(dump_path))
    factor = float(F32(METERS_PER_UNIT))
    # GLB certificates, measured on the GLB this module writes.
    probe = anim_glb.GlbAnimations(Glb(glb_document([], rotation)))
    channels = probe.animations[0]['channels']
    source = lambda key: (lambda t: dump_anim.value(0, key, t)[0])
    translation = round_up(measure(channels[0].sample, source(('node', 1, 'translation')), 'translation', factor))
    rotation_bound = round_up(measure(channels[1].sample, source(('node', 1, 'rotation')), 'rotation'))
    weights = round_up(measure(channels[2].sample, source(('node', 2, 'weights')), 'components'))
    rows, row = row_writer()
    row('animation/transform-tracks/0/clock/value', 'meters', 0.0005, translation)
    row('animation/transform-tracks/1/clock/value', 'degrees', 0.1, rotation_bound)
    row('animation/morph-target-tracks/0/clock/value', 'ratio', 5e-7, weights)
    row('animation/morph-target-tracks/1/clock/value', 'ratio', 5e-7, weights)
    row('animation/morph-target-nodes/2/packing', 'weight', 5e-7, 0.0, reason='animation.morph-targets-packed')
    for index in range(3):
        row('animation/property-tracks/%d/clock/value' % index, 'component', 5e-7, 0.0)
    if mutate_rows is not None:
        rows = mutate_rows(rows)
    glb_bytes = glb_document(rows, rotation)
    glb_path = os.path.join(work_dir, 'model.glb')
    with open(glb_path, 'wb') as f:
        f.write(glb_bytes)
    fidelity_path = os.path.join(work_dir, 'fidelity.json')
    with open(fidelity_path, 'w', encoding='utf-8') as f:
        json.dump({'schema': 'multitool.mesh-info/1', 'writers': [{'format': 'glb', 'failure': None, 'fidelity': {'rows': rows}}]}, f)
    if rotation != 'Linear':
        files = {'dump': dump_path, 'glb': glb_path, 'fidelity': fidelity_path}
        return {'dump': dump_path, 'glb': glb_path, 'fidelity': fidelity_path, 'package': None, 'glbBytes': glb_bytes,
                'files': files, 'sha256': {k: sha256_file(v) for k, v in files.items()}}
    # Package certificates, measured on the package this module writes (per component, over max(1, key magnitude)).
    package_path = os.path.join(work_dir, 'package.zip')
    raw_channels = package_channels()
    write_package(package_path, package_manifest([]), raw_channels)
    from package_reader import Package
    package = Package(package_path)
    animations = anim_package.PackageAnimations(package)
    package.close()
    prows = []
    for pchannel in animations.clips[0]['channels']:
        if pchannel.property == 'NodeVisibility':
            continue
        if pchannel.property == 'Rotation':
            observed = round_up(measure(pchannel.sample, lambda t: dump_anim.track_value(0, 'transform-tracks', 1, 0, t)[0], 'rotation'))
            prows.append({'target': {'kind': 'Animation', 'index': 0}, 'featureId': 'animation/transform-tracks/1/0/clock-retiming',
                          'outcome': 'Approximated', 'reasonCode': 'animation-clock-curves', 'description': 'synthetic',
                          'boundsDomain': 'normalized quaternion orientation', 'boundsUnit': 'degrees', 'maximumError': 0.1,
                          'observedError': observed})
            continue
        denominators = np.maximum(1.0, np.max(np.abs(pchannel.values[:, 1 if pchannel.parts == 3 else 0, :].astype(np.float64)), axis=0))
        for component in range(pchannel.width):
            if pchannel.domain == 'morph-target-tracks':
                expected = lambda t, c=pchannel: dump_anim.track_value(0, c.domain, c.track, 0, t)[0]
            else:
                expected = lambda t, c=pchannel, k=component: dump_anim.track_value(0, c.domain, c.track, 0, t)[0][k:k + 1]
            observed = round_up(measure(lambda t, c=pchannel, k=component: c.sample(t)[k:k + 1] / denominators[k],
                                        lambda t, e=expected, k=component: np.asarray(e(t), dtype=np.float64) / denominators[k], 'components'))
            prows.append({'target': {'kind': 'Animation', 'index': 0},
                          'featureId': 'animation/%s/%d/%d/clock-retiming' % (pchannel.domain, pchannel.track, pchannel.offset + component),
                          'outcome': 'Approximated' if observed else 'Converted', 'reasonCode': 'animation-clock-curves', 'description': 'synthetic',
                          'boundsDomain': 'local components / max(1, lowered source key magnitude)', 'boundsUnit': 'ratio',
                          'maximumError': 1e-6, 'observedError': observed})
    write_package(package_path, package_manifest(prows), raw_channels)
    files = {'dump': dump_path, 'glb': glb_path, 'fidelity': fidelity_path, 'package': package_path}
    return {'dump': dump_path, 'glb': glb_path, 'fidelity': fidelity_path, 'package': package_path, 'glbBytes': glb_bytes,
            'files': files, 'sha256': {k: sha256_file(v) for k, v in files.items()}}


# ------------------------------------------------------------------------------------------- 170 degrees
def slice9_pair():
    """Slice 9's synthetic 170-degree pair, reproduced with its Float32 rounding points."""
    x, y, z = F32(1), F32(2), F32(3)
    length = F32(math.sqrt(float(F32(F32(x * x) + F32(y * y)) + F32(z * z))))
    axis = [F32(x / length), F32(y / length), F32(z / length)]
    half = 85.0 * math.pi / 180.0
    second = [float(F32(float(a) * math.sin(half))) for a in axis] + [float(F32(math.cos(half)))]
    return [0.0, 0.0, 0.0, 1.0], second


def pair_document(first, second):
    identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
    track = {'nodeIndex': 0, 'property': 'Rotation', 'times': [0.0, 1.0], 'values': first + second,
             'interpolation': 'GamebryoCounterWarpedNlerp', 'clock': None, 'state': 'Keyed', 'staticValue': None,
             'spline': None, 'tbcParameters': None, 'tbcEndpoints': None, 'gamebryoSquadPolicy': None}
    clip = {'name': 'Pair170', 'durationSeconds': None, 'clock': None, 'events': [], 'transformTracks': [track],
            'eulerRotationTracks': [], 'matrixTracks': [], 'transformBases': [], 'morphTracks': [], 'morphTargetTracks': [],
            'propertyTracks': []}
    return {'name': 'gate1b-170', 'nodes': [{'name': 'Antenna', 'localTransform': identity, 'children': [], 'meshIndex': None,
                                              'localTrs': {'translation': [0, 0, 0], 'rotation': [0, 0, 0, 1], 'scale': [1, 1, 1]},
                                              'morphWeights': None}],
            'scenes': [{'rootNodeIndices': [0]}], 'meshes': [], 'materials': [], 'images': [], 'skins': [],
            'animations': [clip], 'units': {'metersPerUnit': 1.0}}


def pair_glb(times, keys, rows):
    gltf = {'asset': {'version': '2.0'}, 'scene': 0, 'scenes': [{'nodes': [0]}], 'nodes': [{'name': 'Antenna'}],
            'extras': {'multitoolFidelity': {'rows': rows}, 'multitoolAnimationMetadata': {'clips': [
                {'sourceAnimation': 0, 'outputAnimation': 0, 'name': 'Pair170', 'outputWindowSeconds': 1.0, 'clock': None}]}}}
    builder = BinBuilder(gltf)
    sampler = {'input': builder.add(times, 'SCALAR'), 'output': builder.add([c for q in keys for c in q], 'VEC4'), 'interpolation': 'LINEAR'}
    gltf['animations'] = [{'name': 'Pair170', 'samplers': [sampler], 'channels': [{'sampler': 0, 'target': {'node': 0, 'path': 'rotation'}}]}]
    return builder.finish()


def run_170_degree_control(work_dir, pair=None):
    """The gate-level 170-degree control (see the module text). Returns the control dict the receipt records.

    ``pair`` replaces slice 9's fixture (the self-check passes a 20-degree pair, where the control must read NOT
    detected: a writer may omit insertion there, and slerp and nlerp differ by far less than 6 degrees); the slice-9
    reproduction is then not part of the verdict."""
    import anim_dump
    import hop_b_anim
    import hop_c_anim
    from dump_reader import Dump
    from package_reader import Package
    os.makedirs(work_dir, exist_ok=True)
    first, second = pair if pair is not None else slice9_pair()
    pair_grid = grid(1.0)
    slice9_points = [i / 32 for i in range(33)]
    dump_path = os.path.join(work_dir, 'dump.json')
    with open(dump_path, 'w', encoding='utf-8') as f:
        json.dump({'schemaVersion': 1, 'document': pair_document(first, second)}, f)
    dump = Dump(dump_path)
    dump_anim = anim_dump.DumpAnimation(dump)
    source = lambda t: dump_anim.value(0, ('node', 0, 'rotation'), t)[0]
    unit_second = np.asarray(second, dtype=np.float64) / np.linalg.norm(second)
    slerp_vs_nlerp = max(ac.rotation_degrees(anim_glb.gltf_slerp(first, unit_second, u / 1024), anim_glb.plain_nlerp(first, unit_second, u / 1024))
                         for u in range(1025))
    dense_times = [i / 256 for i in range(257)]
    dense_keys = [[float(v) for v in source(float(F32(t)))] for t in dense_times]
    for k in range(1, len(dense_keys)):
        if np.dot(dense_keys[k - 1], dense_keys[k]) < 0:
            dense_keys[k] = [-v for v in dense_keys[k]]
    outcome = {'name': '170-degree pair (slice 9 synthetic fixture): two keys written without insertion', 'files': {}}
    results = {}
    for label, times, keys in (('inserted', dense_times, dense_keys), ('two-key', [0.0, 1.0], [first, list(unit_second)])):
        channel = anim_glb.GlbAnimations(Glb(pair_glb(times, keys, []))).animations[0]['channels'][0]
        observed = round_up(measure(channel.sample, source, 'rotation', times=pair_grid)) if label == 'inserted' else 0.0
        rows = [{'target': {'kind': 'Animation', 'index': 0}, 'featureId': 'animation/transform-tracks/0/clock/value',
                 'outcome': 'Approximated', 'reasonCode': 'animation.clock-value-bounded', 'isPending': False,
                 'bounds': {'unit': 'degrees', 'maximumError': 0.1, 'observedError': observed}}]
        glb_path = os.path.join(work_dir, '%s.glb' % label)
        with open(glb_path, 'wb') as f:
            f.write(pair_glb(times, keys, rows))
        glb_result = HopResult('B-anim-170', label)
        hop_b_anim.check_animation(glb_result, Glb.from_path(glb_path), dump, {'id': label, 'glb': glb_path}, {})
        package_path = os.path.join(work_dir, '%s.zip' % label)
        raw = {'sourceDomain': 'transform-tracks', 'sourceTrack': 0, 'node': 0, 'property': 'Rotation', 'componentOffset': 0,
               'width': 4, 'interpolation': 'Linear'}
        manifest = {'packageVersion': 4, 'nodes': [{'name': 'Antenna', 'trs': {'translation': [0, 0, 0], 'rotation': [0, 0, 0, 1], 'scale': [1, 1, 1]}}],
                    'animationCount': 1, 'fidelity': []}
        write_package(package_path, manifest, [(raw, times, [c for q in keys for c in q])], 'Pair170', 1.0)
        package = Package(package_path)
        pchannel = anim_package.PackageAnimations(package).clips[0]['channels'][0]
        package.close()
        pobserved = round_up(measure(pchannel.sample, source, 'rotation', times=pair_grid)) if label == 'inserted' else 0.0
        manifest['fidelity'] = [{'target': {'kind': 'Animation', 'index': 0}, 'featureId': 'animation/transform-tracks/0/0/clock-retiming',
                                 'boundsUnit': 'degrees', 'maximumError': 0.1, 'observedError': pobserved}]
        write_package(package_path, manifest, [(raw, times, [c for q in keys for c in q])], 'Pair170', 1.0)
        package = Package(package_path)
        package_result = HopResult('C-anim-170', label)
        try:
            hop_c_anim.check_package(package_result, package, dump, label, {})
        finally:
            package.close()
        # Slice 9 measured its misses at the 33 segment fractions i/32; the reproduction uses the same points, the
        # worst figures the 4,001-point grid.
        glb_misses = measure(channel.sample, source, 'rotation', times=slice9_points)
        package_misses = measure(pchannel.sample, source, 'rotation', times=slice9_points)
        results[label] = {'glbPassed': glb_result.passed, 'packagePassed': package_result.passed,
                          'glbSlice9PointsDegrees': glb_misses, 'packageSlice9PointsDegrees': package_misses,
                          'glbWorstDegrees': measure(channel.sample, source, 'rotation', times=pair_grid),
                          'packageWorstDegrees': measure(pchannel.sample, source, 'rotation', times=pair_grid),
                          'glbFirstMismatch': next((m.to_dict() for c in glb_result.checks for m in c.mismatches), None),
                          'packageFirstMismatch': next((m.to_dict() for c in package_result.checks for m in c.mismatches), None)}
        outcome['files'][label] = {'glb': glb_path, 'glbSha256': sha256_file(glb_path), 'package': package_path,
                                   'packageSha256': sha256_file(package_path)}
    two = results['two-key']
    reproduced = None if pair is not None else (
        abs(slerp_vs_nlerp - SLICE9_SLERP_VERSUS_NLERP) < 0.01 and abs(two['glbSlice9PointsDegrees'] - SLICE9_SLERP_MISS) < 0.01
        and abs(two['packageSlice9PointsDegrees'] - SLICE9_NLERP_MISS) < 0.01)
    detected = (results['inserted']['glbPassed'] and results['inserted']['packagePassed'] and not two['glbPassed']
                and not two['packagePassed'] and slerp_vs_nlerp > DETECTS_NLERP_DEGREES and reproduced is not False)
    outcome.update({
        'detected': bool(detected), 'dumpPath': dump_path, 'dumpSha256': sha256_file(dump_path),
        'measured': {'slerpVersusNlerpDegrees': slerp_vs_nlerp, 'twoKeyGlbSlerpMissDegrees': two['glbSlice9PointsDegrees'],
                     'twoKeyPackageNlerpMissDegrees': two['packageSlice9PointsDegrees'],
                     'twoKeyGlbSlerpMissOnGridDegrees': two['glbWorstDegrees'],
                     'twoKeyPackageNlerpMissOnGridDegrees': two['packageWorstDegrees']},
        'slice9': {'slerpVersusNlerpDegrees': SLICE9_SLERP_VERSUS_NLERP, 'plainSlerpWorstDegrees': SLICE9_SLERP_MISS,
                   'plainNlerpWorstDegrees': SLICE9_NLERP_MISS,
                   'receipt': 'TestOutput/cut1b-slice9-20260927/NifAnimationRotationPathOracleTests/receipt.json'},
        'reproducesSlice9': reproduced, 'results': results,
        'detail': ('with 257 inserted keys the GLB (slerp) and the package (component nlerp) pass (worst %.3g and %.3g degrees); '
                   'the two source keys alone miss the counter-warped nlerp by %.4f degrees as a GLB LINEAR slerp and by %.4f as '
                   'a package LINEAR nlerp (slice 9: %.4f and %.4f); plain slerp and nlerp of the pair differ by %.4f degrees '
                   '(> %g; slice 9: %.4f)' % (results['inserted']['glbWorstDegrees'], results['inserted']['packageWorstDegrees'],
                                              two['glbSlice9PointsDegrees'], two['packageSlice9PointsDegrees'], SLICE9_SLERP_MISS, SLICE9_NLERP_MISS,
                                              slerp_vs_nlerp, DETECTS_NLERP_DEGREES, SLICE9_SLERP_VERSUS_NLERP)),
    })
    return outcome
