# SPDX-License-Identifier: 0BSD
"""Gate 1b: the exact dump's clips evaluated at playback (output) times by the independent curve evaluator.

Every curve is evaluated by ``tools/scripts/nif_curve_eval.py`` (committed in cut-1b slice 9 as the A8 oracle and
extended for gate 1b, see its ``dump_curve_node`` / ``clock_map`` section); this module only walks the dump
(``shared/Multitool.Shared/docs/model-dump-json.md``: clips with optional ``clock``, transform, Euler, morph,
morph-target and property tracks with optional controller ``clock``) and applies ScenePoseEvaluator.ApplyClip's order,
read from Shared Core/Models/ScenePoseEvaluator.cs (lines 382-445) and ScenePropertyPoseState.ApplyClip (lines 97-110):

* the clip clock maps the output time first and the track clock maps that Float32 result (``nif_curve_eval.map_clocks``;
  Shared docs/scene-animation-clock-lowering.md: "A track is sampled at T(C(tau))");
* a NotDriven transform track supplies nothing; a Constant track holds its static vector; transform tracks apply in
  order, then every Euler track replaces its node's rotation; morph tracks replace a node's complete weight vector, then
  morph-target tracks replace single targets; property tracks write their targets in order;
* the rest pose is the node's authored TRS, SceneMorphDefaults (the node's ``morphWeights``, else its mesh's, else
  zeros), visibility 1 (ScenePropertyPoseState line 88) and the material's authored source factors (the same
  source-first fallback the Blender importer's ``_animation_material_default`` and ScenePropertyPoseState use).

A value is returned in SOURCE units and basis: the GLB keeps animation values in source units under its coordinate
parents, and the package keeps source values too. Nothing here reads a writer output.
"""

import collections
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPTS = os.path.dirname(HERE)
if SCRIPTS not in sys.path:
    sys.path.insert(0, SCRIPTS)

import nif_curve_eval as nce  # noqa: E402  (tools/scripts/nif_curve_eval.py, the slice-9 evaluator)

F32 = np.float32

TRANSFORM_PATHS = {'Translation': 'translation', 'Rotation': 'rotation', 'Scale': 'scale'}
MATERIAL_KINDS = ('MaterialBaseColor', 'MaterialAlpha', 'MaterialEmissiveColor', 'MaterialEmissiveStrength',
                  'MaterialSpecularColor', 'MaterialAmbientColor')
LAYER_KINDS = ('LayerOffsetU', 'LayerOffsetV', 'LayerScaleU', 'LayerScaleV', 'LayerRotation')
COLOR_KINDS = ('MaterialBaseColor', 'MaterialEmissiveColor', 'MaterialSpecularColor', 'MaterialAmbientColor')


class DumpUnsupported(Exception):
    """A dump track the independent evaluator cannot reproduce (it is listed as not compared, never guessed)."""


def property_key(target):
    """The pose key of one ScenePropertyTarget ({kind, index, layerIndex})."""
    kind = target['kind']
    if kind == 'NodeVisibility':
        return ('visibility', int(target['index']))
    if kind in LAYER_KINDS:
        return ('layer', int(target['index']), int(target['layerIndex']), kind)
    return ('material', int(target['index']), kind)


def key_label(key):
    if key[0] == 'node':
        return 'nodes/%d/%s' % (key[1], key[2])
    if key[0] == 'visibility':
        return 'nodes/%d/visibility' % key[1]
    if key[0] == 'material':
        return 'materials/%d/%s' % (key[1], key[2])
    return 'materials/%d/layers/%d/%s' % (key[1], key[2], key[3])


class Driver:
    """One source track feeding one pose key: its domain and index (the fidelity rows' ``animation/<domain>/<index>``),
    the Curve(s) nif_curve_eval evaluates, the controller clock and how it writes the key."""

    def __init__(self, domain, index, kind, clock, curve=None, axes=None, target_index=None, width=None,
                 squad=False, source=None):
        self.domain = domain
        self.index = index
        self.kind = kind            # 'components', 'rotation', 'euler', 'weights', 'target-weight', 'property'
        self.clock = clock
        self.curve = curve
        self.axes = axes
        self.target_index = target_index
        self.width = width
        self.squad = squad
        self.source = source or {}

    @property
    def row_prefix(self):
        return 'animation/%s/%d' % (self.domain, self.index)


class DumpClip:
    """One dump animation: its clock and every pose key it drives, each with its drivers in application order."""

    def __init__(self, dump, index):
        raw = dump.animations[index]
        self.dump = dump
        self.index = index
        self.raw = raw
        self.name = raw.get('name')
        self.clock = raw.get('clock')
        self.duration = raw.get('durationSeconds')
        self.drivers = collections.OrderedDict()
        self.not_driven = []
        self.unsupported = []
        transform_order = []
        euler_order = []
        for ti, track in enumerate(raw.get('transformTracks') or []):
            path = TRANSFORM_PATHS[track['property']]
            key = ('node', int(track['nodeIndex']), path)
            if track.get('state') == 'NotDriven':
                self.not_driven.append(('transform-tracks', ti, key))
                continue
            if track.get('interpolation') == 'GamebryoSquad' and track.get('gamebryoSquadPolicy') not in (None, 'PcFloat32', 'Xbox360Estimate'):
                # nif_curve_eval samples PcFloat32 (Blow FastNormalize) and Xbox360Estimate (the unit-normalization centre
                # Shared publishes since cc5cda3); any other policy is listed as driven but not compared.
                self.unsupported.append(('transform-tracks', ti, key, 'Gamebryo Squad policy %r has no implemented sampler'
                                         % track.get('gamebryoSquadPolicy')))
                continue
            width = 4 if path == 'rotation' else 3
            curve = nce.Curve(nce.dump_curve_node(track, width))
            transform_order.append((key, Driver('transform-tracks', ti, 'rotation' if path == 'rotation' else 'components',
                                                track.get('clock'), curve=curve, width=width,
                                                squad=track.get('interpolation') == 'GamebryoSquad', source=track)))
        for ei, track in enumerate(raw.get('eulerRotationTracks') or []):
            key = ('node', int(track['nodeIndex']), 'rotation')
            if track.get('order') != 'Xyz':
                self.unsupported.append(('euler-rotation-tracks', ei, key, 'Euler order %r is not implemented (Shared refuses it too)'
                                         % track.get('order')))
                continue
            axes = [nce.Curve(nce.dump_curve_node(track[axis], 1)) for axis in ('x', 'y', 'z')]
            euler_order.append((key, Driver('euler-rotation-tracks', ei, 'euler', track.get('clock'), axes=axes, source=track)))
        for key, driver in transform_order + euler_order:
            self.drivers.setdefault(key, []).append(driver)
        for mi, track in enumerate(raw.get('morphTracks') or []):
            key = ('node', int(track['nodeIndex']), 'weights')
            width = int(track['targetCount'])
            curve = nce.Curve(nce.dump_curve_node(track, width, value_key='weights'))
            self.drivers.setdefault(key, []).append(Driver('morph-tracks', mi, 'weights', track.get('clock'), curve=curve,
                                                           width=width, source=track))
        for ti, track in enumerate(raw.get('morphTargetTracks') or []):
            key = ('node', int(track['nodeIndex']), 'weights')
            curve = nce.Curve(nce.dump_curve_node(track['weight'], 1))
            self.drivers.setdefault(key, []).append(Driver('morph-target-tracks', ti, 'target-weight', track.get('clock'),
                                                           curve=curve, target_index=int(track['targetIndex']), source=track))
        for pi, track in enumerate(raw.get('propertyTracks') or []):
            key = property_key(track['target'])
            width = int(track['curve']['componentCount'])
            curve = nce.Curve(nce.dump_curve_node(track['curve'], width))
            self.drivers.setdefault(key, []).append(Driver('property-tracks', pi, 'property', track.get('clock'), curve=curve,
                                                           width=width, source=track))
        for xi, _ in enumerate(raw.get('matrixTracks') or []):
            self.unsupported.append(('matrix-tracks', xi, None, 'matrix tracks are not evaluated by gate 1b (the GLB writer '
                                     'refuses matrix clips; SceneMatrixCurve is not reproduced)'))
        for bi, basis in enumerate(raw.get('transformBases') or []):
            self.unsupported.append(('transform-bases', bi, ('node', int(basis.get('nodeIndex', -1)), 'basis'),
                                     'post-transform bases are not evaluated by gate 1b'))

    def driven_keys(self):
        return list(self.drivers.keys())

    def drivers_of(self, key):
        return self.drivers.get(key, [])


class DumpAnimation:
    """The dump's clips plus the rest values they start from; ``value(clip, key, tau)`` is the pose component."""

    def __init__(self, dump):
        self.dump = dump
        self.document = dump.document
        self.clips = [DumpClip(dump, i) for i in range(len(dump.animations))]
        units = dump.units or {}
        self.meters_per_unit = units.get('metersPerUnit')

    # ------------------------------------------------------------------------------------------------ rest values
    def rest(self, key):
        """The rest value of a pose key as Float32 (source units), or None when the source has no authored value."""
        if key[0] == 'node':
            node = self.dump.nodes[key[1]]
            if key[2] == 'weights':
                return self.morph_defaults(key[1])
            trs = node.get('localTrs')
            if trs is None:
                return None
            return np.asarray(trs[key[2]], dtype=np.float64).astype(F32)
        if key[0] == 'visibility':
            return np.array([1.0], dtype=F32)
        if key[0] == 'material':
            return self.material_rest(key[1], key[2])
        return self.layer_rest(key[1], key[2], key[3])

    def morph_defaults(self, node_index):
        """SceneMorphDefaults.Get: the node's morphWeights, else its mesh's, else zeros over the first primitive's targets."""
        node = self.dump.nodes[node_index]
        if node.get('morphWeights') is not None:
            return np.asarray(node['morphWeights'], dtype=np.float64).astype(F32)
        mesh_index = node.get('meshIndex')
        if mesh_index is None:
            return np.zeros(0, dtype=F32)
        mesh = self.dump.meshes[mesh_index]
        if mesh.get('morphWeights') is not None:
            return np.asarray(mesh['morphWeights'], dtype=np.float64).astype(F32)
        primitives = mesh['primitives']
        count = len(primitives[0].morph_targets) if primitives else 0
        return np.zeros(count, dtype=F32)

    def material_rest(self, material_index, kind):
        material = self.dump.materials[material_index]
        source = material.get('source') or {}
        base = source.get('baseColor') if source.get('baseColor') is not None else material.get('baseColor') or [1, 1, 1, 1]
        if kind == 'MaterialBaseColor':
            values = base[:3]
        elif kind == 'MaterialAlpha':
            values = [base[3]]
        elif kind == 'MaterialEmissiveStrength':
            value = source.get('emissiveMultiplier')
            values = [value if value is not None else (material.get('emissiveStrength') if material.get('emissiveStrength') is not None else 1.0)]
        elif kind == 'MaterialEmissiveColor':
            value = source.get('emissiveColor')
            values = value if value is not None else (material.get('emissiveFactor') or [0, 0, 0])
        elif kind == 'MaterialSpecularColor':
            value = source.get('specularColor')
            values = value if value is not None else (material.get('specularColorFactor') or [1, 1, 1])
        else:
            value = source.get('ambientColor')
            values = value if value is not None else [0, 0, 0]
        return np.asarray(values, dtype=np.float64).astype(F32)

    def layer_rest(self, material_index, layer_index, kind):
        material = self.dump.materials[material_index]
        layers = material.get('layers') or (material.get('source') or {}).get('layers') or []
        transform = {}
        if 0 <= layer_index < len(layers):
            transform = ((layers[layer_index].get('binding') or {}).get('transform') or {})
        if kind == 'LayerRotation':
            return np.array([transform.get('rotation', 0.0) or 0.0], dtype=F32)
        member, fallback = ('offset', (0.0, 0.0)) if kind.startswith('LayerOffset') else ('scale', (1.0, 1.0))
        vector = transform.get(member) or fallback
        return np.array([vector[0 if kind.endswith('U') else 1]], dtype=F32)

    # ----------------------------------------------------------------------------------------------- evaluation
    def value(self, clip_index, key, tau):
        """(value, magnitude): the pose component ``key`` of clip ``clip_index`` at output time ``tau`` (a Float32),
        and the largest key/tangent/control magnitude of the source segments that produced it (for the evaluation slack).
        Raises DumpUnsupported when a driver cannot be evaluated."""
        clip = self.clips[clip_index]
        value = self.rest(key)
        if value is None:
            raise DumpUnsupported('%s has no authored TRS (a matrix-only node); gate 1b does not decompose matrices' % key_label(key))
        value = np.array(value, dtype=F32, copy=True)
        magnitude = float(np.max(np.abs(value))) if value.size else 0.0
        for driver in clip.drivers_of(key):
            seconds = nce.map_clocks(clip.clock, driver.clock, tau)
            if driver.kind == 'components':
                sample, segment = nce.sample_components(driver.curve, seconds)
                value = np.asarray([float(v) for v in sample], dtype=F32)
                magnitude = max(magnitude, float(segment))
            elif driver.kind == 'rotation':
                value = np.asarray([float(v) for v in nce.sample_rotation(driver.curve, seconds)], dtype=F32)
                magnitude = 1.0
            elif driver.kind == 'euler':
                value = np.asarray([float(v) for v in nce.euler_quaternion(driver.axes, seconds)], dtype=F32)
                magnitude = 1.0
            elif driver.kind == 'weights':
                sample, segment = nce.sample_components(driver.curve, seconds)
                if len(sample) != len(value):
                    raise DumpUnsupported('morph track %d drives %d targets, the node has %d' % (driver.index, len(sample), len(value)))
                value = np.asarray([float(v) for v in sample], dtype=F32)
                magnitude = max(magnitude, float(segment))
            elif driver.kind == 'target-weight':
                sample, segment = nce.sample_components(driver.curve, seconds)
                if not 0 <= driver.target_index < len(value):
                    raise DumpUnsupported('morph-target track %d names target %d of %d' % (driver.index, driver.target_index, len(value)))
                value[driver.target_index] = F32(sample[0])
                magnitude = max(magnitude, float(segment))
            else:
                sample, segment = nce.sample_components(driver.curve, seconds)
                value = np.asarray([float(v) for v in sample], dtype=F32)
                magnitude = max(magnitude, float(segment))
        return value, magnitude

    def track_value(self, clip_index, domain, index, component, tau):
        """One source track's own curve at output time ``tau`` (no pose composition), for the package hop, which pairs
        channels with their ``sourceDomain``/``sourceTrack``. ``component`` selects an Euler axis (0, 1, 2); every other
        domain returns its full vector. Returns (value, magnitude, driver)."""
        clip = self.clips[clip_index]
        driver = None
        for drivers in clip.drivers.values():
            for candidate in drivers:
                if candidate.domain == domain and candidate.index == index:
                    driver = candidate
                    break
            if driver is not None:
                break
        if driver is None:
            raise DumpUnsupported('%s/%d is not an evaluable driven track of clip %d' % (domain, index, clip_index))
        seconds = nce.map_clocks(clip.clock, driver.clock, tau)
        if driver.kind == 'euler':
            sample, segment = nce.sample_components(driver.axes[component], seconds)
            return np.asarray([float(sample[0])], dtype=F32), float(segment), driver
        if driver.kind == 'rotation':
            return np.asarray([float(v) for v in nce.sample_rotation(driver.curve, seconds)], dtype=F32), 1.0, driver
        sample, segment = nce.sample_components(driver.curve, seconds)
        return np.asarray([float(v) for v in sample], dtype=F32), float(segment), driver

    def source_extent(self, clip_index):
        """The largest unclocked key time or spline stop of a clip's driven tracks (the no-clock playback extent)."""
        clip = self.clips[clip_index]
        extent = 0.0
        for drivers in clip.drivers.values():
            for driver in drivers:
                curves = driver.axes if driver.axes is not None else [driver.curve]
                for curve in curves:
                    if curve.spline is not None:
                        extent = max(extent, float(curve.stop))
                    elif curve.times:
                        extent = max(extent, float(curve.times[-1]))
        return extent


def clip_clock_window(clock):
    """One clip-clock period in output seconds (Shared docs/scene-animation-clock-lowering.md, "Window and cycle"): the
    time a Clamp clock takes to reach its far endpoint (zero when it starts there), one interval for Loop, out and back
    for Reverse, zero for a hold (zero frequency or a zero-length interval). Returns a double."""
    frequency = float(F32(clock['frequency']))
    phase = float(F32(clock['phaseSeconds']))
    start = float(F32(clock['startSeconds']))
    stop = float(F32(clock['stopSeconds']))
    if frequency == 0 or start == stop:
        return 0.0
    length = stop - start
    if clock['cycle'] == 'Loop':
        return length / abs(frequency)
    if clock['cycle'] == 'Reverse':
        return 2 * length / abs(frequency)
    far = stop if frequency > 0 else start
    return max(0.0, (far - phase) / frequency)
