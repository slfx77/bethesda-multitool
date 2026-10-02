# SPDX-License-Identifier: 0BSD
"""Hop D over the animation (gate 1b): the saved .blend's actions, F-curves, NLA tracks and drivers against the package.

The readback (Shared ``tools/blender/readback.py --include-values``) dumps every action with its slots, layers, strips
and F-curves (keys, handles, per-key interpolation and handle types, and ``evaluated``: Blender's own
``FCurve.evaluate`` at every key, at one, two and three quarters of every key interval and one frame beyond each end),
plus ``animation_data`` for every animatable ID (active action and slot, every scalar AnimData setting, NLA tracks,
drivers with their validity). The expectation is the package read by ``anim_package`` (independent of the importer; its
F-curve points, FREE handles and modes restate ``_animation_coordinates`` / ``_animation_fcurve``) and the owner layout
``_make_animations`` / ``_animation_material_actions`` / ``_animation_morph_owners`` / ``_animation_stash`` /
``_animation_visibility_owners`` declare (read, never run):

* owners: every clip has one slotted action per owner the importer creates for ANY clip (``global_owners``): a node
  object for TRS, ``LocalMatrix``/``PostTransform`` and visibility channels (for a camera-facing node, its controller,
  the object carrying ``mt_node_index`` and ``mt_local_matrix``; its presentation child ``<name>.mt_facing``
  (``mt_facing_node_index``), any pivot empty ``<name>.mt_pivot`` (``mt_pivot_node_index``) and any anchor empty
  ``<name>.mt_anchor`` (``mt_anchor_node_index``) are new objects that must carry no action, NLA track or driver); a
  shape-key block per morph primitive
  (a primitive with morph targets) of a morph-animated node; a material node tree per vertex-color variant
  (``import_rules.material_variant_keys``) of a material that a material property targets. Owners are keyed by
  (kind, node, primitive) and (kind, material, variant), so a missing primitive or variant action is reported;
* each action: ``use_frame_range``, frame range [0, max(1, duration x 32)], one slot of the owner's ID type, one layer
  holding one KEYFRAME strip with one channel bag for that slot, ``mt_animation_index`` / ``mt_name`` /
  ``mt_duration_seconds`` equal to the clip;
* an animated F-curve per channel component at the importer's data path (``location``, ``scale``,
  ``rotation_quaternion`` in Blender's W, X, Y, Z order, ``rotation_euler``, ``matrix_parent_inverse``,
  ``["mt_visibility"]``, the key block's own ``value_path`` at offset + component + 1; a material component is matched
  by content among the action's non-rest F-curves with equal keys, modes and handles), keys equal to the package's
  (frame = time x 32, value) in Float32 bit for bit, CONSTANT extrapolation, per-key interpolation from the channel's
  policy, and on a cubic channel FREE handles equal bit for bit to the one-third handles;
* rest curves: the set is DERIVED from the package, not read from the importer's list: an OBJECT owner of a node with
  TRS-family channels rests location, scale, rotation (Euler for an Euler-animated node, else W, X, Y, Z) and the 16
  ``matrix_parent_inverse`` components. The importer keys the object's stored static parent inverse
  (``_animation_parent_inverse_rest``); in a version-4 package that is identity for a node with TRS (a camera-facing
  parent's pivot or residual lives on its pivot empty, never on the child) and the exact local matrix for a matrix-only
  node (an identity parent inverse is not multiplied in), so the expectation below is unchanged by the controller split; a
  LocalMatrix clip replaces the TRS rests by 0, 1 and identity, a visibility node rests ``["mt_visibility"]`` at 1, a
  KEY owner rests every block at the importer's morph default (node weights, else mesh weights, else 0); minus what the
  clip animates. Each rest curve is one key at frame 0, CONSTANT, equal to its expected value (an Euler rest and a
  material rest are compared with the importer's ``mt_animation_rest_components`` only), and the importer's list must
  name exactly the rest curves the action holds;
* Blender's own evaluation equals ``blender_fcurve.evaluate`` (Blender's arithmetic restated: its key search with the
  1e-4-frame threshold, the flat-span shortcut, Float32 ``findzero`` coefficients with a double root and a Float32
  ``berekeny``) within ``blender_fcurve.residual_bound``; every animated and rest F-curve carries exactly the evaluated
  frames the readback's rule produces. How far Blender's playback strays from the ideal curve
  (``anim_package.evaluate_fcurve``) is measured and reported as ``blenderVersusIdeal`` per property, never hidden;
* each action on exactly one muted NLA track of its own owner (track and strip named by the action, the action's slot,
  frame ranges [0, frame end], extrapolation NOTHING, blend REPLACE); each owner's active action and slot are the first
  packaged clip's, on that owner, with Blender's default AnimData settings (influence 1, REPLACE, HOLD, NLA on, no
  tweak mode); no other ID carries an action, NLA track or driver;
* with animated visibility: every node object carries ``mt_visibility`` and ``mt_effective_visibility`` and drives the
  latter as ``own`` or ``own*parent``; every primitive object drives ``hide_render`` and ``hide_viewport`` as
  ``visible<0.5`` (``1`` exactly when the package says it is never drawn); every driver is a valid SCRIPTED driver whose
  variables are valid SINGLE_PROP variables on OBJECT targets;
* the scene: 32 frames per second, frame range [0, max(1, ceil(longest duration x 32))], ``mt_animation_fps``,
  ``mt_active_animation_index``, ``mt_animation_sources``;
* the construction drivers, animated or not (``construction_drivers``; Shared 0152179, package versions 6 and 7): every
  gate and handedness driver of a typed-facing construction (``import_rules.typed_facing_drivers``) and the winding
  driver on every drawn-winding primitive object (``import_rules.reflected_face_driver``), each a valid SCRIPTED driver
  with exactly the importer's expression and variables; they are the only drivers a presentation child or a helper may
  carry, and no other driver is allowed where the package declares no animation.

``PackageModel`` also hands hop D's static checks (``compare_d``) what the animation changes there: the Euler-animated
nodes (rotation mode XYZ), whether visibility splits geometry from its controller, the first clip's frame-0 pose that
the saved objects and shape keys hold, and the shape-key slider range widened to every animated key and handle.

NOT compared, stated rather than implied: which material socket a material component drives (the binding table is
the importer's material graph; the component is matched by content inside its material's own action), the rest value
of a material component and of an Euler rotation, and handles of non-cubic keys (Blender's auto handles, unused by
CONSTANT and LINEAR spans).
"""

import json
import math
import os
import sys
from collections import Counter

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import anim_package  # noqa: E402
import blender_fcurve  # noqa: E402
import import_rules as rules  # noqa: E402
from gate1a_common import OracleError  # noqa: E402

F32 = np.float32
FPS = 32
EVALUATION_CHECK = "animation: Blender F-curve evaluation equals Blender's restated arithmetic on the package curve"
NODE_PATHS = {'Translation': 'location', 'Scale': 'scale', 'EulerRotation': 'rotation_euler',
              'Rotation': 'rotation_quaternion', 'LocalMatrix': 'matrix_parent_inverse',
              'PostTransform': 'matrix_parent_inverse', 'NodeVisibility': '["mt_visibility"]'}
DEFAULT_ANIMDATA = {'action_influence': 1.0, 'action_blend_type': 'REPLACE', 'action_extrapolation': 'HOLD',
                    'use_nla': True, 'use_tweak_mode': False}
IDENTITY16 = [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0]


def channel_where(clip_index, channel_index, component):
    return 'animations/%d/channels/%d/%d' % (clip_index, channel_index, component)


def _props(entry):
    return (entry or {}).get('properties') or {}


def _json_prop(value):
    if isinstance(value, str):
        try:
            return json.loads(value)
        except ValueError:
            return value
    return value


def _deep_equal(actual, expected):
    """JSON-like equality with numbers compared by value (the importer stores 2.0 where the package says 2) and
    booleans only against booleans."""
    if isinstance(expected, bool) or isinstance(actual, bool):
        return isinstance(expected, bool) and isinstance(actual, bool) and actual == expected
    if isinstance(expected, (int, float)) and isinstance(actual, (int, float)):
        return float(actual) == float(expected)
    if isinstance(expected, dict) and isinstance(actual, dict):
        return set(actual) == set(expected) and all(_deep_equal(actual[k], expected[k]) for k in expected)
    if isinstance(expected, list) and isinstance(actual, list):
        return len(actual) == len(expected) and all(_deep_equal(x, y) for x, y in zip(actual, expected))
    return actual == expected


def _f32(value):
    return float(F32(value))


def _pairs(flat):
    return np.asarray(flat or [], dtype=np.float64).reshape(-1, 2)


def _bits_equal(actual, expected):
    a = np.asarray(actual, dtype=F32).reshape(-1)
    e = np.asarray(expected, dtype=F32).reshape(-1)
    return a.shape == e.shape and np.array_equal(a.view(np.uint32), e.view(np.uint32))


def _variant_key(value):
    value = _json_prop(value)
    if isinstance(value, dict):
        return (value.get('domain'), value.get('storage'))
    if isinstance(value, (list, tuple)) and len(value) == 2:
        return (value[0], value[1])
    return None


def _is_object_channel(channel):
    return channel.target is None or channel.property == 'NodeVisibility'


def _is_material_channel(channel):
    return channel.target is not None and channel.property != 'NodeVisibility'


def expected_evaluation_frames(points):
    """The frames readback._evaluation_frames produces for these keys (Float32)."""
    frames = [F32(v) for v in np.asarray(points)[:, 0]] if len(points) else []
    if not frames:
        return []
    wanted = [F32(frames[0] - F32(1.0))]
    for index, frame in enumerate(frames):
        wanted.append(frame)
        if index + 1 < len(frames) and frames[index + 1] > frame:
            span = float(frames[index + 1]) - float(frame)
            wanted.extend(F32(float(frame) + span * quarter) for quarter in (0.25, 0.5, 0.75))
    wanted.append(F32(frames[-1] + F32(1.0)))
    return [float(v) for v in wanted]


class PackageModel:
    """What the importer builds from the package's animations, restated from the package alone."""

    def __init__(self, package, animations):
        self.manifest = package.manifest
        self.animations = animations
        self.nodes = self.manifest.get('nodes') or []
        self.meshes = self.manifest.get('meshes') or []
        self.clips = animations.clips
        channels = [c for clip in self.clips for c in clip['channels']]
        self.euler_nodes = {int(c.node) for c in channels if c.property == 'EulerRotation'}
        self.visibility_nodes = {int(c.node) for c in channels if c.property == 'NodeVisibility'}
        self.visibility_enabled = bool(self.visibility_nodes)
        self.object_groups = {(int(c.node), c.property == 'MorphWeights') for c in channels if _is_object_channel(c)}
        self.trs_rest_nodes = {int(c.node) for c in channels if c.target is None and c.property != 'MorphWeights'}
        self.material_targets = {}
        for c in channels:
            if _is_material_channel(c):
                layer = c.target.get('layerIndex')
                self.material_targets.setdefault(int(c.target.get('index')), set()).add(
                    (c.property, -1 if layer is None else int(layer)))
        self.variants = rules.material_variant_keys(self.manifest) if self.material_targets else []

    def morph_primitives(self, node_index):
        node = self.nodes[node_index]
        if node.get('mesh') is None:
            return []
        primitives = self.meshes[int(node['mesh'])].get('primitives') or []
        return [p for p, primitive in enumerate(primitives) if primitive.get('morphs')]

    def morph_defaults(self, node_index):
        """``_animation_morph_defaults``: node weights, else mesh weights, else zeros."""
        node = self.nodes[node_index]
        mesh = self.meshes[int(node['mesh'])]
        counts = [len(p.get('morphs') or []) for p in mesh.get('primitives') or []]
        weights = node.get('morphWeights')
        if weights is None:
            weights = mesh.get('morphWeights')
        if weights is None:
            weights = [0.0] * (counts[0] if counts else 0)
        return [float(w) for w in weights]

    def owner_keys(self):
        keys = set()
        for node, morph in self.object_groups:
            if morph:
                keys.update(('KEY', node, p) for p in self.morph_primitives(node))
            else:
                keys.add(('OBJECT', node, None))
        for m in self.material_targets:
            for variant in (self.variants[m] if m < len(self.variants) else []):
                keys.add(('NODETREE', m, tuple(variant)))
        return keys

    def owner_channels(self, clip, owner_key):
        kind, index, _extra = owner_key
        if kind == 'NODETREE':
            return [c for c in clip['channels'] if _is_material_channel(c) and int(c.target.get('index')) == index]
        morph = kind == 'KEY'
        return [c for c in clip['channels'] if _is_object_channel(c) and int(c.node) == index
                and (c.property == 'MorphWeights') == morph]

    def object_rests(self, node_index, channels):
        """{(data path, index): expected Float32 rest or None (not compared)} of an OBJECT owner before removing the
        components the clip animates."""
        node = self.nodes[node_index]
        rests = {}
        if node_index in self.trs_rest_nodes:
            trs = node.get('trs')
            if trs is not None:
                location = rules.vector(trs.get('translation'), 3, (0.0, 0.0, 0.0))
                scale = rules.vector(trs.get('scale'), 3, (1.0, 1.0, 1.0))
                x, y, z, w = rules.vector(trs.get('rotation'), 4, (0.0, 0.0, 0.0, 1.0))
                quaternion = (w, x, y, z)
                matrix = IDENTITY16
            else:
                location, scale, quaternion = (0.0, 0.0, 0.0), (1.0, 1.0, 1.0), (1.0, 0.0, 0.0, 0.0)
                matrix = [float(v) for v in node.get('localMatrix') or IDENTITY16]
            euler_rest = (None, None, None)
            if any(c.property == 'LocalMatrix' for c in channels):
                location, scale, quaternion = (0.0, 0.0, 0.0), (1.0, 1.0, 1.0), (1.0, 0.0, 0.0, 0.0)
                euler_rest = (0.0, 0.0, 0.0)
            for i, v in enumerate(location):
                rests[('location', i)] = _f32(v)
            for i, v in enumerate(scale):
                rests[('scale', i)] = _f32(v)
            if node_index in self.euler_nodes:
                for i, v in enumerate(euler_rest):
                    rests[('rotation_euler', i)] = None if v is None else _f32(v)
            else:
                for i, v in enumerate(quaternion):
                    rests[('rotation_quaternion', i)] = _f32(v)
            for i, v in enumerate(matrix):
                rests[('matrix_parent_inverse', i)] = _f32(v)
        if node_index in self.visibility_nodes:
            rests[('["mt_visibility"]', 0)] = 1.0
        return rests

    @staticmethod
    def animated_object_paths(channels):
        out = set()
        for c in channels:
            for component in range(c.width):
                index = (1, 2, 3, 0)[component] if c.property == 'Rotation' else int(c.offset) + component
                out.add((NODE_PATHS.get(c.property), index))
        return out

    def first_clip_pose(self):
        """({(node, 'location'|'scale', i): Float32}, {(node, block index): Float32}): what the saved objects and shape
        keys hold, the first packaged clip being active at frame 0 on every OBJECT and KEY owner."""
        clip = self.clips[0]
        pose, blocks = {}, {}
        for node, morph in self.object_groups:
            channels = self.owner_channels(clip, ('KEY' if morph else 'OBJECT', node, None))
            if morph:
                values = {i + 1: _f32(v) for i, v in enumerate(self.morph_defaults(node))}
                for c in channels:
                    for component in range(c.width):
                        curve = anim_package.fcurve(c, component)
                        values[int(c.offset) + component + 1] = _f32(blender_fcurve.evaluate(curve, 0.0)[0])
                for index, value in values.items():
                    blocks[(node, index)] = value
                continue
            rests = self.object_rests(node, channels)
            values = {key: value for key, value in rests.items() if key[0] in ('location', 'scale')}
            for c in channels:
                if c.property not in ('Translation', 'Scale'):
                    continue
                for component in range(c.width):
                    curve = anim_package.fcurve(c, component)
                    values[(NODE_PATHS[c.property], int(c.offset) + component)] = _f32(blender_fcurve.evaluate(curve, 0.0)[0])
            for (path, index), value in values.items():
                pose[(node, path, index)] = value
        return pose, blocks

    def slider_ranges(self):
        """{(node, block index): (low, high)}: [min(0, d), max(1, d)] from the morph default d, widened by every clip's
        key and handle values of that block, clamped to Blender's [-10, 10]."""
        ranges = {}
        for node in self.morph_value_nodes():
            for i, d in enumerate(self.morph_defaults(node)):
                ranges[(node, i + 1)] = [min(0.0, d), max(1.0, d)]
        for clip in self.clips:
            for c in clip['channels']:
                if c.property != 'MorphWeights' or not _is_object_channel(c):
                    continue
                for component in range(c.width):
                    curve = anim_package.fcurve(c, component)
                    arrays = [a for a in (curve['points'], curve['left'], curve['right']) if a is not None]
                    low = min(float(np.asarray(a, dtype=F32)[:, 1].min()) for a in arrays)
                    high = max(float(np.asarray(a, dtype=F32)[:, 1].max()) for a in arrays)
                    current = ranges.setdefault((int(c.node), int(c.offset) + component + 1), [0.0, 1.0])
                    current[0] = min(current[0], low)
                    current[1] = max(current[1], high)
        return {k: (max(-10.0, v[0]), min(10.0, v[1])) for k, v in ranges.items()}

    def morph_value_nodes(self):
        """Nodes whose primitives' shape-key values and sliders the importer overrides (``_animation_morph_owners``):
        morph-animated nodes and nodes carrying morphWeights."""
        out = {node for node, morph in self.object_groups if morph}
        out.update(i for i, n in enumerate(self.nodes) if n.get('morphWeights') is not None and n.get('mesh') is not None
                   and any(p.get('morphs') for p in self.meshes[int(n['mesh'])].get('primitives') or []))
        return out


def package_model(package):
    """A PackageModel for an animated package, else None."""
    animations = anim_package.PackageAnimations(package)
    if not animations.clips:
        return None
    return PackageModel(package, animations)


class ReadbackAnimation:
    """The readback's actions, owners, objects, meshes and shape keys, indexed by the importer's properties."""

    def __init__(self, readback):
        self.readback = readback
        self.actions = readback.get('actions') or []
        self.owners = readback.get('animation_data') or []
        self.objects = {o['name']: o for o in readback.get('objects') or []}
        self.materials = {m['name']: m for m in readback.get('materials') or []}
        self.node_objects = {}
        self.primitive_objects = []
        # A camera-facing node's presentation child and pivot empty: new objects, never an action owner.
        self.presentation_objects = {}
        for obj in readback.get('objects') or []:
            props = _props(obj)
            if 'mt_local_matrix' in props and 'mt_node_index' in props:
                self.node_objects.setdefault(int(props['mt_node_index']), []).append(obj)
            if 'mt_primitive_index' in props:
                self.primitive_objects.append(obj)
            for key, label in (('mt_facing_node_index', 'presentation child'), ('mt_pivot_node_index', 'pivot empty'),
                               ('mt_anchor_node_index', 'anchor empty')):
                if key in props:
                    self.presentation_objects[obj['name']] = '%s of node %s' % (label, props[key])
        self.keys = []
        for mesh in readback.get('meshes') or []:
            key = mesh.get('shape_keys')
            if key is not None:
                self.keys.append((key, mesh))

    def key_of(self, node, primitive):
        found = [(k, m) for k, m in self.keys
                 if (k.get('properties') or {}).get('mt_node_index') is not None
                 and int((k.get('properties') or {})['mt_node_index']) == node
                 and _props(m).get('mt_primitive_index') is not None and int(_props(m)['mt_primitive_index']) == primitive]
        return found[0] if len(found) == 1 else None

    @staticmethod
    def action_fcurves(action):
        """(slots, the F-curves of the one KEYFRAME strip's one channel bag, structure problems)."""
        problems = []
        slots = action.get('slots') or []
        layers = action.get('layers') or []
        fcurves = []
        if not action.get('slotted_api'):
            problems.append('not a slotted action')
        if len(slots) != 1:
            problems.append('%d slots, expected 1' % len(slots))
        if len(layers) != 1:
            problems.append('%d layers, expected 1' % len(layers))
        for layer in layers:
            strips = layer.get('strips') or []
            if len(strips) != 1 or strips[0].get('type') != 'KEYFRAME':
                problems.append('layer %r holds %s, expected one KEYFRAME strip' % (layer.get('name'), [s.get('type') for s in strips]))
            for strip in strips:
                bags = strip.get('channelbags') or []
                if len(bags) != 1:
                    problems.append('%d channel bags, expected 1' % len(bags))
                for bag in bags:
                    if slots and bag.get('slot') != slots[0].get('identifier'):
                        problems.append('channel bag slot %r, action slot %r' % (bag.get('slot'), slots[0].get('identifier')))
                    fcurves.extend(bag.get('fcurves') or [])
        return slots, fcurves, problems


def _action_owner_key(props):
    if 'mt_material_index' in props:
        return ('NODETREE', int(props['mt_material_index']), _variant_key(props.get('mt_vertex_color_variant')))
    if 'mt_node_index' in props:
        if 'mt_primitive_index' in props:
            return ('KEY', int(props['mt_node_index']), int(props['mt_primitive_index']))
        return ('OBJECT', int(props['mt_node_index']), None)
    return None


def _owner_label(owner_key):
    kind, index, extra = owner_key
    return '%s %s%s' % (kind, index, '' if extra is None else ' %s' % (extra,))


def construction_drivers(manifest, readback):
    """The drivers import_model adds outside the clips (Shared 0152179, package versions 6 and 7), as
    {(owner object name, data path, array index): (expression, {variable: (type, [target object names])}, prefix)}:
    the gates and handedness of every typed-facing construction (``import_rules.typed_facing_drivers``) and the winding
    driver of every drawn-winding primitive object (``import_rules.reflected_face_driver``). ``prefix`` marks the winding
    driver, whose data path ends in the Flip input's Blender socket identifier and is matched by its
    'modifiers["mt_reflected_face_winding"]' prefix. Owners are named from the readback: a typed node's helpers as
    ``clip_name(<controller name> + suffix)``, the way _facing_helper names them."""
    nodes = manifest.get('nodes') or []
    named = {}
    for obj in readback.get('objects') or []:
        props = _props(obj)
        if 'mt_local_matrix' in props and 'mt_node_index' in props:
            named.setdefault('controller', {}).setdefault(int(props['mt_node_index']), obj.get('name'))
        for key, role in (('mt_facing_node_index', 'presentation'), ('mt_pivot_node_index', 'pivot'),
                          ('mt_anchor_node_index', 'anchor')):
            if key in props:
                named.setdefault(role, {}).setdefault(int(props[key]), obj.get('name'))
    roots = [r for r in readback.get('roots') or [] if r.get('name') == 'mt_root' or _props(r).get('mt_name') == 'mt_root']
    root_name = roots[0].get('name') if len(roots) == 1 else 'mt_root'
    scenes = readback.get('scenes') or []
    target_name = (_props(scenes[0]).get('mt_billboard_target') if scenes else None) or 'mt_billboard_target'
    expected = {}
    for index, node in enumerate(nodes):
        kind = rules.typed_facing(manifest, node)
        controller = named.get('controller', {}).get(index)
        if kind is None or controller is None:
            continue  # an absent controller is compare_d's presence failure
        names = {role: named.get(role, {}).get(index) for role in ('controller', 'presentation', 'pivot', 'anchor')}
        names.update(root=root_name, billboard=target_name)
        for role, suffix in rules.FACING_HELPER_SUFFIXES.items():
            names[role] = rules.clip_name(controller + suffix)
        for (owner, path, array_index), (expression, variables) in rules.typed_facing_drivers(
                node['billboard'], kind, rules.pivot_empty(node)).items():
            expected[(names[owner], path, array_index)] = (
                expression, {name: (kind_name, [names[r] for r in roles]) for name, (kind_name, roles) in variables.items()},
                False)
    prefix, expression, variables = rules.reflected_face_driver()
    for obj in readback.get('objects') or []:
        props = _props(obj)
        if 'mt_primitive_index' not in props:
            continue
        index = int(props.get('mt_node_index', -1))
        if not 0 <= index < len(nodes) or nodes[index].get('mesh') is None:
            continue
        primitives = (manifest.get('meshes') or [])[int(nodes[index]['mesh'])].get('primitives') or []
        position = int(props['mt_primitive_index'])
        if 0 <= position < len(primitives) and \
                rules.reflected_face_policy(manifest, nodes[index], primitives[position]) == 'drawnWinding':
            expected[(obj.get('name'), prefix, 0)] = (
                expression, {name: (kind_name, [obj.get('name')]) for name, (kind_name, _roles) in variables.items()}, True)
    return expected


def _compare_construction_drivers(rec, manifest, readback, stats):
    """Each construction driver present once, SCRIPTED, valid, with exactly its expression and variables (a matrix
    variable reading ``matrix_world[r][c]`` of its own name); returns ((owner, data path, index) of every matched
    driver, the number expected)."""
    expected = construction_drivers(manifest, readback)
    actual = {}
    for owner in readback.get('animation_data') or []:
        if owner.get('kind') == 'OBJECT':
            for driver in owner.get('drivers') or []:
                actual[(owner.get('owner'), driver.get('data_path'), driver.get('array_index'))] = driver
    accounted = set()
    for (name, path, index), (expression, variables, prefix) in sorted(expected.items(), key=str):
        where = 'objects/%s/driver %s[%d]' % (name, path, index)
        matches = [key for key in actual if key[0] == name and key[2] == index and
                   (str(key[1]).startswith(path) if prefix else key[1] == path)]
        if len(matches) != 1:
            rec.fail(where, 'presence', '%d matching drivers, expected 1' % len(matches))
            continue
        accounted.add(matches[0])
        driver = actual[matches[0]]
        problems = []
        if driver.get('type') != 'SCRIPTED':
            problems.append('type %r' % driver.get('type'))
        if driver.get('expression') != expression:
            problems.append('expression %r, expected %r' % (driver.get('expression'), expression))
        if driver.get('is_valid') is False:
            problems.append('invalid driver')
        got = {}
        for variable in driver.get('variables') or []:
            targets = variable.get('targets') or []
            got[variable.get('name')] = (variable.get('type'), [t.get('id') for t in targets if t.get('id') is not None])
            if variable.get('is_name_valid') is False:
                problems.append('variable %r: invalid name' % variable.get('name'))
            if variable.get('type') == 'SINGLE_PROP':
                wanted = 'matrix_world[%s][%s]' % (str(variable.get('name'))[1:2], str(variable.get('name'))[2:3])
                if (targets[0] if targets else {}).get('data_path') != wanted:
                    problems.append('variable %r reads %r, expected %r' % (variable.get('name'),
                                                                        (targets[0] if targets else {}).get('data_path'), wanted))
        if got != variables:
            problems.append('variables %r, expected %r' % (got, variables))
        if problems:
            rec.fail(where, 'driver', '; '.join(problems))
        stats['constructionDrivers'] += 1
    return accounted, len(expected)


def _unaccounted_drivers(owner, accounted):
    """An owner's drivers that are not construction drivers (every driver of a non-OBJECT owner)."""
    return [driver for driver in owner.get('drivers') or [] if owner.get('kind') != 'OBJECT' or
            (owner.get('owner'), driver.get('data_path'), driver.get('array_index')) not in accounted]


def compare_animation(result, recorder_class, package, readback, stats):
    """Adds hop D's animation checks to ``result`` (a gate1a HopResult). A package without animations must read back
    with no action, NLA track or driver but the construction drivers the package declares."""
    construction = result.check('construction drivers: typed-facing gates and handedness, reflected-face winding '
                                '(package versions 6 and 7)')
    crec = recorder_class(construction)
    accounted, expected_construction = _compare_construction_drivers(crec, package.manifest, readback, stats)
    if expected_construction:
        crec.finish('%d construction drivers' % expected_construction)
    else:
        construction.skip('the package declares no typed facing and no drawn-winding primitive')
    try:
        model = package_model(package)
    except OracleError as failure:
        check = result.check('animation: package animations readable')
        rec = recorder_class(check)
        rec.fail('animations', 'package', str(failure))
        rec.finish()
        return
    if model is None:
        check = result.check('animation: no actions, NLA tracks or drivers (the package has no animations)')
        rec = recorder_class(check)
        actions = readback.get('actions') or []
        if actions:
            rec.fail('actions', 'presence', '%d action(s): %s' % (len(actions), [a.get('name') for a in actions][:5]))
        stray = [o for o in readback.get('animation_data') or []
                 if o.get('action') or o.get('nla_tracks') or _unaccounted_drivers(o, accounted)]
        if stray:
            rec.fail('animation_data', 'presence', '%d owner(s) carry an action, NLA track or driver: %s'
                     % (len(stray), [o.get('owner') for o in stray][:5]))
        rec.finish()
        return
    view = ReadbackAnimation(readback)
    first_clip = model.clips[0]['sourceIndex']

    structure = result.check('animation: actions, slots, F-curve keys, handles, modes and rests equal the package (exact)')
    srec = recorder_class(structure)
    evaluation = result.check(EVALUATION_CHECK)
    erec = recorder_class(evaluation)
    owners_check = result.check('animation: NLA tracks, active clips, AnimData settings and visibility drivers')
    orec = recorder_class(owners_check)
    scene_check = result.check('animation: scene timing and clip metadata')
    screc = recorder_class(scene_check)

    by_clip = {}
    for action in view.actions:
        props = _props(action)
        if 'mt_animation_index' not in props:
            srec.fail('actions/%s' % action.get('name'), 'presence', 'an action without mt_animation_index')
            continue
        by_clip.setdefault(int(props['mt_animation_index']), []).append(action)
    expected_owners = model.owner_keys()
    stats['animationClips'] += len(model.clips)
    worst = {'ratio': 0.0}
    ideal = {}
    active_by_owner = {}
    importer_owners = set()
    for clip in model.clips:
        ci = clip['index']
        source = clip['sourceIndex']
        duration = float(clip['duration'])
        frame_end = _f32(max(1.0, duration * FPS))
        seen = set()
        for action in by_clip.pop(source, []):
            props = _props(action)
            label = 'animations/%d/actions/%s' % (ci, action.get('name'))
            owner_key = _action_owner_key(props)
            slots, fcurves, problems = view.action_fcurves(action)
            for problem in problems:
                srec.fail(label, 'structure', problem)
            if owner_key is None:
                srec.fail(label, 'presence', 'an action with neither mt_node_index nor mt_material_index')
                continue
            if owner_key not in expected_owners:
                srec.fail(label, 'presence', 'owner %s is not one the importer creates for this package' % _owner_label(owner_key))
                continue
            if owner_key in seen:
                srec.fail(label, 'presence', 'a second action for owner %s in this clip' % _owner_label(owner_key))
            seen.add(owner_key)
            if not action.get('use_frame_range'):
                srec.fail(label, 'frame_range', 'use_frame_range is off')
            if not _bits_equal(action.get('frame_range') or [], [0.0, frame_end]):
                srec.fail(label, 'frame_range', 'frame range %r, expected [0, %r]' % (action.get('frame_range'), frame_end))
            if props.get('mt_name') != (clip['name'] or ''):
                srec.fail(label, 'metadata', 'mt_name %r, clip %r' % (props.get('mt_name'), clip['name']))
            if props.get('mt_duration_seconds') is None or float(props['mt_duration_seconds']) != duration:
                srec.fail(label, 'metadata', 'mt_duration_seconds %r, clip %r' % (props.get('mt_duration_seconds'), duration))
            if slots and slots[0].get('target_id_type') != owner_key[0]:
                srec.fail(label, 'slot', 'slot target %r, expected %r' % (slots[0].get('target_id_type'), owner_key[0]))
            stats['animationActions'] += 1
            channels = model.owner_channels(clip, owner_key)
            _compare_action_curves(srec, erec, label, ci, channels, fcurves, props, owner_key, model, view, stats, worst, ideal)
            if source == first_clip:
                active_by_owner[owner_key] = (action, slots[0].get('identifier') if slots else None)
            owner = _compare_stash(orec, label, action, slots, frame_end, view, owner_key, stats)
            if owner is not None:
                importer_owners.add((owner.get('kind'), owner.get('owner')))
        for missing in sorted(expected_owners - seen, key=str):
            srec.fail('animations/%d/owners/%s' % (ci, _owner_label(missing)), 'presence',
                      'clip %d has no action for owner %s' % (source, _owner_label(missing)))
    for source, actions in sorted(by_clip.items()):
        srec.fail('actions/mt_animation_index %d' % source, 'presence',
                  '%d action(s) name clip %d, which the package does not carry' % (len(actions), source))
    _compare_active(orec, view, active_by_owner, stats)
    driver_owners = set()
    if model.visibility_enabled:
        driver_owners = _compare_visibility_drivers(orec, view, model, stats)
    _compare_strays(orec, view, importer_owners, driver_owners, accounted)
    _compare_scene(screc, readback, model, first_clip)
    evaluation.data['worstRatioToResidualBound'] = worst['ratio']
    evaluation.data['blenderVersusIdeal'] = dict(sorted(ideal.items()))
    srec.finish('%d actions, %d animated and %d rest F-curves (%d material components matched by content)'
                % (stats['animationActions'], stats['animationCurves'], stats['animationRestCurves'],
                   stats['animationCurvesMatchedByContent']))
    erec.finish('%d frames compared (%d on the flat-span shortcut, %d by the key search), worst %.3g x the residual '
                'bound; Blender versus the ideal curve: %s'
                % (stats['animationFramesCompared'], stats['animationFramesFlat'], stats['animationFramesKeySearch'],
                   worst['ratio'], ', '.join('%s %.3g' % (k, v) for k, v in sorted(ideal.items())) or 'none'))
    orec.finish('%d NLA tracks, %d active owners, %d drivers' % (stats['animationTracks'], stats['animationActiveOwners'],
                                                               stats['animationDrivers']))
    screc.finish()


def _key_block_index(view, owner_key, path):
    found = view.key_of(owner_key[1], owner_key[2])
    if found is None:
        return None
    for block in found[0].get('key_blocks') or []:
        if block.get('value_path') == path:
            return int(block['index'])
    return None


def _match_curve(fcurves, remaining, expected, path, index, owner_key, view, rest_paths):
    """Indices of the F-curves that can be the animated curve for (path, index); a material component (path None) is
    matched by content: equal keys, modes and cubic handles, never a listed rest component."""
    out = []
    for i in remaining:
        fc = fcurves[i]
        if owner_key[0] == 'KEY':
            if _key_block_index(view, owner_key, fc.get('data_path')) != index:
                continue
        elif path is not None:
            if fc.get('data_path') != path or int(fc.get('array_index', -1)) != index:
                continue
        else:
            if (fc.get('data_path'), int(fc.get('array_index', -1))) in rest_paths:
                continue
            if not _bits_equal(_pairs(fc.get('keyframes')), expected['points']):
                continue
            if list(fc.get('interpolations') or []) != list(expected['modes']):
                continue
            if expected['left'] is not None and not (_bits_equal(_pairs(fc.get('handle_left')), expected['left'])
                                                     and _bits_equal(_pairs(fc.get('handle_right')), expected['right'])):
                continue
        out.append(i)
    return out


def _compare_action_curves(srec, erec, label, ci, channels, fcurves, props, owner_key, model, view, stats, worst, ideal):
    remaining = list(range(len(fcurves)))
    listed = _json_prop(props.get('mt_animation_rest_components')) or []
    listed = listed if isinstance(listed, list) else []
    listed_index = {(r.get('dataPath'), int(r.get('component', -1))): r for r in listed if isinstance(r, dict)}
    for channel in channels:
        if (owner_key[0] == 'OBJECT' and channel.property == 'LocalMatrix'
                and rules.matrix_keys_location(model.nodes[owner_key[1]])):
            # A camera-facing node without TRS keeps its decomposed controller, and the importer keys this channel's
            # translation lanes as the controller's location (import_rules.matrix_keys_location). BMT emits no such node,
            # so it is reported as unmodeled rather than compared against the parent-inverse layout it does not use.
            srec.fail(channel_where(ci, channel.index, 12), 'unmodeled', 'a complete-matrix channel on camera-facing node %d '
                      'without TRS keys the controller location; compare_d_anim does not model it' % owner_key[1])
            continue
        for component in range(channel.width):
            where = channel_where(ci, channel.index, component)
            expected = anim_package.fcurve(channel, component)
            if owner_key[0] == 'NODETREE':
                path, index = None, None
            elif owner_key[0] == 'KEY':
                path, index = 'key_blocks', int(channel.offset) + component + 1
            else:
                path = NODE_PATHS.get(channel.property)
                if path is None:
                    srec.fail(where, 'path', 'no data path for property %r' % channel.property)
                    continue
                index = (1, 2, 3, 0)[component] if channel.property == 'Rotation' else int(channel.offset) + component
            candidates = _match_curve(fcurves, remaining, expected, path, index, owner_key, view, set(listed_index))
            if path is None and candidates:
                candidates = candidates[:1]
                stats['animationCurvesMatchedByContent'] += 1
            if len(candidates) != 1:
                srec.fail(where, 'presence', '%d F-curves at %s[%s] in %s, expected 1' % (len(candidates), path, index, label))
                continue
            remaining.remove(candidates[0])
            fc = fcurves[candidates[0]]
            stats['animationCurves'] += 1
            _compare_curve(srec, where, fc, expected)
            _compare_evaluation(erec, where, fc, expected, stats, worst, ideal, channel.property)
    expected_rests = _expected_rests(owner_key, channels, model, view)
    derived = expected_rests is not None
    if not derived:
        # A material owner: the socket paths are the importer's binding table, so the rest set is the importer's list;
        # its size must still be every targeted component of this material the clip does not animate.
        expected_rests = {key: None for key in listed_index}
        targeted = sum(_kind_width(kind) for kind, _layer in model.material_targets.get(owner_key[1], ()))
        animated = sum(c.width for c in channels)
        if len(expected_rests) != targeted - animated:
            srec.fail('%s/rest' % label, 'presence', '%d rest components listed, the package implies %d (%d targeted, %d animated)'
                      % (len(expected_rests), targeted - animated, targeted, animated))
    for i in remaining:
        fc = fcurves[i]
        key = (fc.get('data_path'), int(fc.get('array_index', -1)))
        where = '%s/rest/%s[%s]' % (label, key[0], key[1])
        if key not in expected_rests:
            srec.fail(where, 'presence', 'an F-curve that is neither a packaged channel nor a rest component the package implies')
            continue
        stats['animationRestCurves'] += 1
        want = expected_rests.pop(key)
        listed_rest = listed_index.pop(key, None)
        if listed_rest is None:
            srec.fail(where, 'presence', 'the rest curve is missing from mt_animation_rest_components')
        value = want if want is not None else (listed_rest or {}).get('restValue')
        points = _pairs(fc.get('keyframes'))
        if value is None or len(points) != 1 or points[0, 0] != 0.0 or not _bits_equal(points[0, 1], value):
            srec.fail(where, 'rest', 'keys %s, expected one key (0, %r)' % (points.tolist()[:3], value))
        elif listed_rest is not None and not _bits_equal(listed_rest.get('restValue'), value):
            srec.fail(where, 'rest', 'mt_animation_rest_components says %r, expected %r' % (listed_rest.get('restValue'), value))
        if (fc.get('interpolations') or [None])[0] != 'CONSTANT' or fc.get('extrapolation') != 'CONSTANT':
            srec.fail(where, 'rest', 'interpolation %r extrapolation %r, expected CONSTANT' % (fc.get('interpolations'), fc.get('extrapolation')))
        curve = {'points': np.asarray([[0.0, value if value is not None else 0.0]], dtype=F32), 'left': None,
                 'right': None, 'modes': ['CONSTANT']}
        _compare_evaluation(erec, where, fc, curve, stats, worst, None, None)
    for key in sorted(expected_rests, key=str):
        srec.fail('%s/rest/%s[%s]' % (label, key[0], key[1]), 'presence', 'the package implies this rest component; no F-curve holds it')
    if derived:
        for key in sorted(listed_index, key=str):
            srec.fail('%s/rest/%s[%s]' % (label, key[0], key[1]), 'presence', 'mt_animation_rest_components lists a component that is not a rest curve')


def _kind_width(kind):
    return 3 if kind in ('MaterialBaseColor', 'MaterialSpecularColor', 'MaterialAmbientColor', 'MaterialEmissiveColor') else 1


def _expected_rests(owner_key, channels, model, view):
    """{(data path, index): expected value or None} of the rest curves this action must hold; None for a material owner
    (the caller takes the importer's list and checks its size)."""
    kind, index, extra = owner_key
    if kind == 'OBJECT':
        rests = model.object_rests(index, channels)
        for key in model.animated_object_paths(channels):
            rests.pop(key, None)
        return rests
    if kind == 'KEY':
        found = view.key_of(index, extra)
        blocks = {int(b['index']): b for b in (found[0].get('key_blocks') or [])} if found else {}
        animated = {int(c.offset) + component + 1 for c in channels for component in range(c.width)}
        rests = {}
        for i, value in enumerate(model.morph_defaults(index)):
            if i + 1 in animated:
                continue
            path = (blocks.get(i + 1) or {}).get('value_path') or ('<key block %d of node %d primitive %d>' % (i + 1, index, extra))
            rests[(path, 0)] = _f32(value)
        return rests
    return None


def _compare_curve(srec, where, fc, expected):
    points = _pairs(fc.get('keyframes'))
    if not _bits_equal(points, expected['points']):
        diff = next((k for k in range(min(len(points), len(expected['points'])))
                     if not _bits_equal(points[k], expected['points'][k])), None)
        srec.fail(where, 'keys', '%d keys, expected %d; first differing key %r: %r, expected %r'
                  % (len(points), len(expected['points']), diff,
                     points[diff].tolist() if diff is not None else None,
                     expected['points'][diff].tolist() if diff is not None else None))
        return
    modes = fc.get('interpolations') or []
    if list(modes) != list(expected['modes']):
        k = next((k for k in range(min(len(modes), len(expected['modes']))) if modes[k] != expected['modes'][k]), None)
        srec.fail(where, 'interpolation', 'key %r is %r, expected %r' % (k, modes[k] if k is not None else modes,
                                                                          expected['modes'][k] if k is not None else expected['modes']))
    if fc.get('extrapolation') != 'CONSTANT':
        srec.fail(where, 'extrapolation', '%r, expected CONSTANT' % fc.get('extrapolation'))
    if fc.get('mute'):
        srec.fail(where, 'mute', 'the F-curve is muted')
    if expected['left'] is not None:
        types = fc.get('handle_types') or []
        if any(t != ['FREE', 'FREE'] for t in types):
            srec.fail(where, 'handles', 'handle types %r, expected FREE on every key' % sorted({tuple(t) for t in types}))
        for side in ('left', 'right'):
            actual = _pairs(fc.get('handle_' + side))
            if not _bits_equal(actual, expected[side]):
                k = next((k for k in range(min(len(actual), len(expected[side])))
                          if not _bits_equal(actual[k], expected[side][k])), None)
                srec.fail(where, 'handles', '%s handle of key %r is %r, expected %r' % (
                    side, k, actual[k].tolist() if k is not None else None, expected[side][k].tolist() if k is not None else None))


def _compare_evaluation(erec, where, fc, expected, stats, worst, ideal, prop):
    evaluated = fc.get('evaluated')
    frames = expected_evaluation_frames(expected['points'])
    got = [float(f) for f, _v in evaluated or []]
    if evaluated is None or not _bits_equal(got, frames):
        erec.fail(where, 'presence', 'evaluated frames %s, expected the %d frames of the readback rule'
                  % ('missing' if evaluated is None else '(%d) differ' % len(got), len(frames)))
        return
    failed = False
    for frame, value in evaluated:
        want, branch = blender_fcurve.evaluate(expected, frame)
        stats['animationFramesCompared'] += 1
        if branch == 'flat':
            stats['animationFramesFlat'] += 1
        elif branch == 'key-search':
            stats['animationFramesKeySearch'] += 1
        bound = blender_fcurve.residual_bound(expected, frame)
        error = abs(float(value) - want)
        if not math.isfinite(float(value)) or error > bound:
            if not failed:
                erec.fail(where, 'value', "frame %r: Blender %r, Blender's arithmetic on the package curve %r (%s; error %.3g, "
                          'bound %.3g)' % (frame, float(value), want, branch, error, bound))
            failed = True
            continue
        if bound > 0:
            worst['ratio'] = max(worst['ratio'], error / bound)
        if ideal is not None and prop is not None:
            deviation = abs(float(value) - anim_package.evaluate_fcurve(expected, frame))
            ideal[prop] = max(ideal.get(prop, 0.0), deviation)


def _compare_stash(orec, label, action, slots, frame_end, view, owner_key, stats):
    name = action.get('name')
    slot = slots[0].get('identifier') if slots else None
    homes = [(owner, track) for owner in view.owners for track in owner.get('nla_tracks') or []
             if any(strip.get('action') == name for strip in track.get('strips') or [])]
    if len(homes) != 1:
        orec.fail(label + '/nla', 'presence', 'the action is on %d NLA tracks, expected 1' % len(homes))
        return None
    owner, track = homes[0]
    stats['animationTracks'] += 1
    if owner.get('kind') != owner_key[0] or not _owner_matches(view, owner, owner_key):
        orec.fail(label + '/nla', 'owner', "owner %s %r (%r) is not the importer's owner %s"
                  % (owner.get('kind'), owner.get('owner'), owner.get('user'), _owner_label(owner_key)))
    if track.get('name') != name or not track.get('mute'):
        orec.fail(label + '/nla', 'track', 'track %r mute %r, expected %r muted' % (track.get('name'), track.get('mute'), name))
    strips = track.get('strips') or []
    if len(strips) != 1:
        orec.fail(label + '/nla', 'track', '%d strips, expected 1' % len(strips))
        return owner
    strip = strips[0]
    settings = strip.get('settings') or {}
    if strip.get('action_slot') != slot:
        orec.fail(label + '/nla', 'strip', 'strip slot %r, action slot %r' % (strip.get('action_slot'), slot))
    for field, want in (('action_frame_start', 0.0), ('action_frame_end', frame_end), ('frame_start', 0.0), ('frame_end', frame_end)):
        if field not in settings or not _bits_equal(settings[field], want):
            orec.fail(label + '/nla', 'strip', '%s %r, expected %r' % (field, settings.get(field), want))
    for field, want in (('extrapolation', 'NOTHING'), ('blend_type', 'REPLACE')):
        if settings.get(field) != want:
            orec.fail(label + '/nla', 'strip', '%s %r, expected %r' % (field, settings.get(field), want))
    return owner


def _owner_matches(view, owner, owner_key):
    kind, index, extra = owner_key
    if kind == 'OBJECT':
        # The controller: a camera-facing node's presentation child and pivot empty carry neither property.
        obj = view.objects.get(owner.get('owner'))
        return obj is not None and 'mt_local_matrix' in _props(obj) and int(_props(obj).get('mt_node_index', -1)) == index
    if kind == 'KEY':
        found = view.key_of(index, extra)
        return found is not None and found[0].get('name') == owner.get('owner')
    material = view.materials.get(owner.get('owner'))
    return (material is not None and owner.get('user') in (None, 'MATERIAL')
            and int(_props(material).get('mt_material_index', -1)) == index
            and _variant_key(_props(material).get('mt_vertex_color_variant')) == extra)


def _compare_active(orec, view, active_by_owner, stats):
    active = {}
    for owner in view.owners:
        if owner.get('action') is not None:
            active[owner.get('action')] = owner
    for owner_key, (action, slot) in sorted(active_by_owner.items(), key=lambda item: str(item[0])):
        owner = active.pop(action.get('name'), None)
        where = 'owners/%s' % _owner_label(owner_key)
        if owner is None:
            orec.fail(where, 'active', "the first clip's action %r is not active on any owner" % action.get('name'))
            continue
        stats['animationActiveOwners'] += 1
        if owner.get('kind') != owner_key[0] or not _owner_matches(view, owner, owner_key):
            orec.fail(where, 'active', "the first clip's action is active on %s %r, not on its own owner" % (owner.get('kind'), owner.get('owner')))
        if owner.get('action_slot') != slot:
            orec.fail(where, 'active', 'active slot %r, expected %r' % (owner.get('action_slot'), slot))
        settings = owner.get('settings')
        if settings is not None:
            for field, want in DEFAULT_ANIMDATA.items():
                if field in settings and not _deep_equal(settings[field], want):
                    orec.fail(where, 'settings', 'AnimData %s %r, expected %r' % (field, settings[field], want))
    for name, owner in sorted(active.items()):
        orec.fail('owners/%s' % owner.get('owner'), 'active', 'action %r is active but is not a first-clip action' % name)


def _driver_problems(driver, expression_allowed):
    problems = []
    if driver.get('type') != 'SCRIPTED':
        problems.append('type %r' % driver.get('type'))
    if driver.get('expression') not in expression_allowed:
        problems.append('expression %r not in %r' % (driver.get('expression'), expression_allowed))
    if driver.get('is_valid') is False:
        problems.append('invalid driver')
    for variable in driver.get('variables') or []:
        targets = variable.get('targets') or [{}]
        if variable.get('type') != 'SINGLE_PROP' or variable.get('is_name_valid') is False or targets[0].get('id_type') != 'OBJECT':
            problems.append('variable %r: type %r, valid name %r, id type %r' % (variable.get('name'), variable.get('type'),
                                                                                variable.get('is_name_valid'), targets[0].get('id_type')))
    return problems


def _variables(driver):
    return {v['name']: ((v.get('targets') or [{}])[0].get('id'), (v.get('targets') or [{}])[0].get('data_path'))
            for v in driver.get('variables') or []}


def _compare_visibility_drivers(orec, view, model, stats):
    """The importer's visibility drivers; returns the (kind, owner) pairs that legitimately carry drivers."""
    nodes = model.nodes
    object_by_node = {index: objs[0]['name'] for index, objs in view.node_objects.items() if len(objs) == 1}
    drivers = {owner.get('owner'): {(d['data_path'], d['array_index']): d for d in owner.get('drivers') or []}
               for owner in view.owners if owner.get('kind') == 'OBJECT'}
    allowed = set()
    for index, node in enumerate(nodes):
        name = object_by_node.get(index)
        where = 'nodes/%d/visibility driver' % index
        obj = view.objects.get(name) or {}
        for prop in ('mt_visibility', 'mt_effective_visibility'):
            if prop not in _props(obj):
                orec.fail(where, 'presence', 'node %d (%r) lacks the %s property' % (index, name, prop))
        driver = (drivers.get(name) or {}).get(('["mt_effective_visibility"]', 0))
        if driver is None:
            orec.fail(where, 'presence', 'node %d (%r) has no mt_effective_visibility driver' % (index, name))
            continue
        allowed.add(('OBJECT', name))
        stats['animationDrivers'] += 1
        parent = node.get('parent')
        want = {'own': (name, '["mt_visibility"]')}
        if parent is not None:
            want['parent'] = (object_by_node.get(int(parent)), '["mt_effective_visibility"]')
        problems = _driver_problems(driver, ('own*parent' if parent is not None else 'own',))
        if problems or _variables(driver) != want:
            orec.fail(where, 'driver', '%s; variables %r, expected %r' % ('; '.join(problems) or 'variables differ', _variables(driver), want))
    materials = model.manifest.get('materials') or []
    for obj in view.primitive_objects:
        props = _props(obj)
        node_index = int(props.get('mt_node_index', -1))
        try:
            primitive = model.meshes[int(props.get('mt_mesh_index', -1))]['primitives'][int(props.get('mt_primitive_index', -1))]
            node = nodes[node_index]
        except (IndexError, KeyError, TypeError):
            orec.fail('objects/%s' % obj.get('name'), 'presence', 'a primitive object names no package primitive')
            continue
        material_index = primitive.get('material')
        material = {} if material_index is None else materials[int(material_index)]
        never_drawn = (primitive.get('purpose') == 'NoDraw' or node.get('role') == 'NoDraw'
                       or (material.get('renderState') or {}).get('cull') == 'FrontAndBack')
        allowed.add(('OBJECT', obj.get('name')))
        for path in ('hide_render', 'hide_viewport'):
            where = 'objects/%s/%s driver' % (obj.get('name'), path)
            driver = (drivers.get(obj.get('name')) or {}).get((path, 0))
            if driver is None:
                orec.fail(where, 'presence', 'no %s driver' % path)
                continue
            stats['animationDrivers'] += 1
            want = {'visible': (object_by_node.get(node_index), '["mt_effective_visibility"]')}
            problems = _driver_problems(driver, ('1',) if never_drawn else ('visible<0.5',))
            if problems or _variables(driver) != want:
                orec.fail(where, 'driver', '%s; variables %r, expected %r' % ('; '.join(problems) or 'variables differ', _variables(driver), want))
    return allowed


def _compare_strays(orec, view, importer_owners, driver_owners, accounted=frozenset()):
    """No action, NLA track or driver the importer does not create; the construction drivers (``accounted``, compared
    by _compare_construction_drivers) are the only drivers a presentation child or a typed-facing helper carries."""
    for owner in view.owners:
        key = (owner.get('kind'), owner.get('owner'))
        where = 'animation_data/%s %s' % key
        presented = view.presentation_objects.get(owner.get('owner')) if owner.get('kind') == 'OBJECT' else None
        drivers = _unaccounted_drivers(owner, accounted)
        if presented is not None and (drivers or owner.get('action') is not None or owner.get('nla_tracks')):
            # Actions stay on the controller, whose evaluated world is the camera-free source pose.
            orec.fail(where, 'presence', 'the %s carries an action, NLA track or driver; actions stay on the controller' % presented)
            continue
        if drivers and key not in driver_owners:
            orec.fail(where, 'presence', '%d driver(s) on an ID the importer does not drive' % len(drivers))
        if (owner.get('action') is not None or owner.get('nla_tracks')) and key not in importer_owners:
            orec.fail(where, 'presence', 'an action or NLA track on an ID the importer does not animate')


def _compare_scene(screc, readback, model, first_clip):
    scenes = readback.get('scenes') or []
    if len(scenes) != 1:
        screc.fail('scene', 'presence', '%d scenes, expected 1' % len(scenes))
        return
    scene = scenes[0]
    props = _props(scene)
    longest = max(float(clip['duration']) for clip in model.clips)
    frame_end = max(1, math.ceil(longest * FPS))
    for field, want in (('fps', FPS), ('fps_base', 1.0), ('frame_start', 0), ('frame_end', frame_end)):
        if scene.get(field) != want:
            screc.fail('scene/' + field, 'value', '%r, expected %r' % (scene.get(field), want))
    if props.get('mt_animation_fps') != FPS:
        screc.fail('scene/mt_animation_fps', 'value', '%r, expected %r' % (props.get('mt_animation_fps'), FPS))
    if props.get('mt_active_animation_index') != first_clip:
        screc.fail('scene/mt_active_animation_index', 'value', '%r, expected %r' % (props.get('mt_active_animation_index'), first_clip))
    sources = _json_prop(props.get('mt_animation_sources'))
    if not isinstance(sources, list) or len(sources) != len(model.clips):
        screc.fail('scene/mt_animation_sources', 'value', 'expected %d clip records, found %r' % (len(model.clips), type(sources).__name__))
        return
    for clip, record in zip(model.clips, sources):
        raw = clip['raw']
        want = {'sourceIndex': clip['sourceIndex'], 'name': raw.get('name'), 'durationSeconds': raw.get('durationSeconds'),
                'metadata': raw.get('metadata'), 'extras': raw.get('extras')}
        if not _deep_equal(record, want):
            screc.fail('scene/mt_animation_sources/%d' % clip['index'], 'value',
                       'record %s differs from the package clip %s' % (json.dumps(record, sort_keys=True)[:200], json.dumps(want, sort_keys=True)[:200]))
