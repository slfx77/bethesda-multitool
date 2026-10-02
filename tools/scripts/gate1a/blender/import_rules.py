# SPDX-License-Identifier: 0BSD
"""The Blender importer's DECLARED conversions, restated for the hop-D and hop-F oracles.

Every function names the ``shared/Multitool.Shared/src/Slfx77.Multitool.Media.Blender/Python/import_model.py`` lines
(Shared 66bf702; the file is unchanged at a5f9e1b) it restates. Nothing here imports or executes the importer: the
oracle reads the package with ``package_reader`` and the .blend through ``tools/blender/readback.py``'s dump.

Independence, said plainly: these rules are restated FROM the importer, so an error the importer and this module
share is invisible to hop D, which then proves that Blender stores exactly what the rules produce (storage, round
trip, packing), not that the rules are right. What pins the rules from OUTSIDE the importer:
* the self-check's design vectors, typed from the design text and checked against this module (section 6.2: stencil
  Both / Clockwise culling and winding, NIF triangles exact, UV maps exact up to 8, relative morphs added in float32 as
  absolute shape keys; section 6.3: BYTE_COLOR keeps the source bytes, FLOAT_COLOR the raw floats; section 6.4:
  ``mt_source_normal``), and the E/T terms of the 11 pairs worked out by hand from out = sf * S + df * Cd;
* hop F's renders (a wrong V flip fails the orientation probe, a wrong color space the ramp, a wrong E/T graph the
  blend pairs) and hop E (writer versus writer, consistency).
Welding order, degenerate-face omission and loop order have no outside anchor beyond the exact dump's face arrays.
``selfcheck_blender_harness.py`` also loads the importer's pure functions under a stub ``bpy`` (no Blender) and compares
them with this module on hand tables and on every real package; a disagreement there is a finding to adjudicate
against the design (either side may be wrong), never presumed an oracle bug.
"""

import math

import numpy as np

F32 = np.dtype('<f4')
COLOR_ROLES = ("BaseColor", "Glow", "Detail", "Dark", "Decal", "LightMap", "SpecularColor", "Environment")
UV_LAYER_LIMIT = 8  # import_model.py line 138
VERTEX_COLOR_LAYER = 'Color'  # line 165
SOURCE_NORMAL_ATTRIBUTE = 'mt_source_normal'  # line 166
SOURCE_TANGENT_ATTRIBUTE = 'mt_source_tangent'  # line 167
BLEND_SIGNS = {'Add': (1.0, 1.0), 'Subtract': (1.0, -1.0), 'ReverseSubtract': (-1.0, 1.0)}  # line 1526
COLOR_ENCODINGS = ('UnsignedByteNormalized', 'UnsignedShortNormalized', 'FloatingPoint')  # line 1525


def enum(value, allowed, default=None):
    """import_model._enum (lines 486-496): case-insensitive name match ignoring '_' and '-'; None when unknown."""
    if value is None:
        return default
    text = str(value).replace('_', '').replace('-', '').lower()
    for candidate in allowed:
        if candidate.lower() == text:
            return candidate
    return None


def clip_name(text, limit=63):
    """import_model._clip_name (lines 2691-2696)."""
    encoded = text.encode('utf-8')
    if len(encoded) <= limit:
        return text
    return encoded[:limit].decode('utf-8', errors='ignore')


# --------------------------------------------------------------------------------------------------------------------
# Materials: variant keys, culling, color space
# --------------------------------------------------------------------------------------------------------------------


def vertex_color_key(primitive):
    """import_model._vertex_color_key (lines 1529-1544): (domain, storage); linear only for a declared Linear space."""
    encoding = enum(primitive.get('colorEncoding'), COLOR_ENCODINGS)
    space = enum(primitive.get('colorSpace'), ('Unknown', 'Srgb', 'Linear'), 'Unknown')
    return ('linear' if space == 'Linear' else 'gamma', 'byte' if encoding == 'UnsignedByteNormalized' else 'float')


def material_variant_keys(manifest):
    """import_model._material_variant_keys (lines 1547-1563): per material, the keys in first-use order."""
    materials = manifest.get('materials') or []
    keys = [[] for _ in materials]
    for mesh in manifest.get('meshes') or []:
        for primitive in mesh.get('primitives') or []:
            index = primitive.get('material')
            if index is None:
                continue
            key = vertex_color_key(primitive)
            if key not in keys[int(index)]:
                keys[int(index)].append(key)
    return [entry if entry else [('gamma', 'float')] for entry in keys]


def culling(entry):
    """import_model._culling (lines 1566-1592): (use_backface_culling, reverse_winding, label)."""
    state = entry.get('renderState') or {}
    front_clockwise = enum(state.get('frontFace'), ('CounterClockwise', 'Clockwise'), 'CounterClockwise') == 'Clockwise'
    if state.get('cull') is not None:
        mode = enum(state.get('cull'), ('None', 'Front', 'Back', 'FrontAndBack'))
        if mode == 'None':
            return False, False, 'none'
        if mode == 'FrontAndBack':
            return True, False, 'frontAndBack'
        reverse = (mode == 'Front') != front_clockwise
        return True, reverse, 'cull%s%s' % (mode, '-reversed' if reverse else '')
    stencil = state.get('stencil')
    if stencil is not None:
        draw = enum(stencil.get('drawMode'), ('CounterClockwise', 'Clockwise', 'Both'))
        if draw == 'Both':
            return False, False, 'stencilBoth'
        return True, draw == 'Clockwise', 'stencil%s' % draw
    if bool(entry.get('doubleSided', False)):
        return False, False, 'doubleSided'
    return True, False, 'singleSided'


def lighting_model(entry):
    """import_model._apply_lighting_model (lines 1894-1906), the design's one document-level lighting row (section
    6.1): the source declaration's lightingModel when the source names one, else the portable one; Lambert by default."""
    source = entry.get('source')
    declared = (source or {}).get('lightingModel', entry.get('lightingModel'))
    return declared if declared is not None else 'Lambert'


def has_depth_bias(entry):
    """import_model._has_depth_bias (lines 1595-1600): a nonzero constant or slope bias (the design's depth-bias row)."""
    depth = (entry.get('renderState') or {}).get('depth')
    if depth is None:
        return False
    return float(depth.get('constantBias') or 0.0) != 0.0 or float(depth.get('slopeBias') or 0.0) != 0.0


def node_has_shear(local_row_major):
    """import_model._has_shear (lines 3339-3346) on the manifest's row-major localMatrix: two axes of the linear part not
    orthogonal (relative 1e-6). Design section 6.2: shear goes through the children's matrix_parent_inverse."""
    m = np.asarray([float(v) for v in local_row_major], dtype=np.float64).reshape(4, 4)
    axes = m[:3, :3]  # row i of the row-vector matrix is Blender's column i
    for first, second in ((0, 1), (0, 2), (1, 2)):
        scale = math.sqrt(float(axes[first] @ axes[first]) * float(axes[second] @ axes[second]))
        if scale > 0.0 and abs(float(axes[first] @ axes[second])) > 1e-6 * scale:
            return True
    return False


def billboard_offset(node):
    """import_model._billboard_offset (lines 3394-3403): (pivot, anchor) when a typed billboard declares a nonzero one."""
    billboard = node.get('billboard')
    if billboard is None:
        return None
    pivot = vector(billboard.get('pivot'), 3, (0.0, 0.0, 0.0))
    anchor = vector(billboard.get('anchor'), 3, (0.0, 0.0, 0.0))
    if not any(pivot) and not any(anchor):
        return None
    return pivot, anchor


# --------------------------------------------------------------------------------------------------------------------
# Camera-facing nodes: controller, presentation child, pivot empty (Shared billboards gap, phase 1)
# --------------------------------------------------------------------------------------------------------------------

BILLBOARD_MODES = ('None', 'AxialY', 'CameraPlane')  # import_model.BILLBOARD_MODES
BILLBOARD_AIMS = ('CameraPlane', 'CameraPosition')  # import_model.BILLBOARD_AIMS
BILLBOARD_ROLLS = ('Camera', 'FixedUp')  # import_model.BILLBOARD_ROLLS


def camera_facing(node):
    """import_model._camera_facing: a typed billboard, or a legacy presentation billboard other than None. Such a node is
    a controller object (``mt_node_index``, the exact source transform, decomposed in every package version when the node
    has no TRS, every action, no constraint, never the single mesh object) plus a presentation child ``<name>.mt_facing``
    (``mt_facing_node_index``) at the identity carrying the anchor constraint (``anchor_constraint``) and the facing
    constraint. An unknown legacy mode reads as not camera-facing here; the importer refuses it."""
    if node.get('billboard') is not None:
        return True
    legacy = enum((node.get('presentation') or {}).get('legacyBillboard'), BILLBOARD_MODES, 'None')
    return legacy is not None and legacy != 'None'


def pivot_empty(node):
    """Whether import_model._create_presentation_objects adds the constant pivot empty ``<name>.mt_pivot``
    (``mt_pivot_node_index``): a camera-facing node whose children compose with a nonidentity
    ``residual @ Translation(-pivot)``, i.e. a nonzero typed pivot, or the shear residual of a matrix node. The package
    version does not matter: a camera-facing node without TRS keeps its decomposed controller in version 4 too (its facing
    constraint must see an unsheared basis), so its residual lives here in every version."""
    if not camera_facing(node):
        return False
    if any(vector((node.get('billboard') or {}).get('pivot'), 3, (0.0, 0.0, 0.0))):
        return True
    return node.get('trs') is None and node_has_shear(node.get('localMatrix') or [0.0] * 16)


def matrix_keys_location(node):
    """import_model._matrix_keys_location: a camera-facing node without TRS, whose admitted complete-matrix channels
    (every key on the rest basis) key the decomposed controller's location from lanes M41..M43, never its
    ``matrix_parent_inverse``."""
    return node.get('trs') is None and camera_facing(node)


def document_worlds(nodes):
    """Rest document worlds W_i = W_parent @ L_i (column-vector, float64, no root) from the package's row-major local
    matrices, as import_model._node_worlds composes them (there in float32)."""
    worlds = [None] * len(nodes)
    for start in range(len(nodes)):
        chain, index = [], start
        while index is not None and worlds[index] is None:
            if index in chain:
                raise ValueError('node %d is its own ancestor' % index)
            chain.append(index)
            parent = nodes[index].get('parent')
            index = None if parent is None else int(parent)
        for index in reversed(chain):
            local = np.asarray([float(v) for v in nodes[index].get('localMatrix') or [0.0] * 16],
                               dtype=np.float64).reshape(4, 4).T
            parent = nodes[index].get('parent')
            worlds[index] = local if parent is None else worlds[int(parent)] @ local
    return worlds


def anchor_empty(node):
    """import_model._place_billboard_anchor: the Float32 document-space anchor the empty ``<name>.mt_anchor``
    (``mt_anchor_node_index``, parented to mt_root with an identity parent inverse) stores as its location, or None when
    the typed billboard declares no nonzero anchor (every NIF billboard). The presentation child itself stays at the
    identity; no basis is inverted."""
    anchor = vector((node.get('billboard') or {}).get('anchor'), 3, (0.0, 0.0, 0.0))
    if not any(anchor):
        return None
    return [float(np.float32(v)) for v in anchor]


def anchor_constraint(node):
    """The presentation child's first constraint for a nonzero anchor, as (name, type, target, settings) with target
    'anchor' (the ``<name>.mt_anchor`` empty); its ``space_object`` is mt_root. None without an anchor."""
    if anchor_empty(node) is None:
        return None
    return ('mt_billboard_anchor', 'COPY_LOCATION', 'anchor',
            {'use_offset': True, 'owner_space': 'CUSTOM', 'target_space': 'CUSTOM'})


def _axis(direction, tolerance=1e-6):
    """import_model._axis: ('X'|'Y'|'Z', sign) when a direction lies on one axis after normalization, else None."""
    d = np.asarray(direction, dtype=np.float64)
    length = float(np.linalg.norm(d))
    if not length > 0 or not math.isfinite(length):
        return None
    d = d / length
    for index, name in enumerate(('X', 'Y', 'Z')):
        if max(abs(float(d[other])) for other in range(3) if other != index) <= tolerance:
            return name, 1 if d[index] > 0 else -1
    return None


def _track_axis(axis):
    """import_model._track_axis."""
    return ('TRACK_' if axis[1] > 0 else 'TRACK_NEGATIVE_') + axis[0]


def billboard_constraints(node, world, manifest=None):
    """import_model._typed_billboard_constraints / _legacy_billboard_constraints: the constraints the presentation child
    carries, as [(name, type, target, settings)] with target 'billboard' (the mt_billboard_target empty) or 'root'
    (mt_root), or, for a typed construction in a version-6 or version-7 ``manifest`` (``typed_facing``), a helper role
    of ``FACING_HELPER_SUFFIXES`` ('plane', 'camera_up', 'document_up'; ``typed_facing_constraints``). None for a locked
    axis on a singular rest world, where the importer's float32 ``inverted_safe`` decides and this float64 restatement
    cannot (not compared, counted). An extended declaration without a typed construction gets no constraint (lines
    4653-4656). Shared's tests/Slfx77.Multitool.Media.Blender.Tests/Python/billboard_constraint_vectors.json pins the
    importer's phase-1 builders and the C# mirror to one table; re-run
    TestOutput/gap-impl-20260927/billboards/import_rules_vector_check.py against it after editing this function."""
    world_settings = {'mix_mode': 'REPLACE', 'target_space': 'WORLD', 'owner_space': 'WORLD'}
    billboard = node.get('billboard')
    if billboard is None:
        legacy = enum((node.get('presentation') or {}).get('legacyBillboard'), BILLBOARD_MODES, 'None')
        if legacy == 'CameraPlane':
            return [('mt_billboard', 'COPY_ROTATION', 'billboard', world_settings)]
        if legacy == 'AxialY':
            return [('mt_billboard_reset', 'COPY_ROTATION', 'root', world_settings),
                    ('mt_billboard', 'LOCKED_TRACK', 'billboard', {'lock_axis': 'LOCK_Y', 'track_axis': 'TRACK_Z'})]
        return []
    kind = typed_facing(manifest, node)
    if kind is not None:
        return [(name, kind_name, role, settings)
                for name, kind_name, role, settings, _gate in typed_facing_constraints(billboard, kind)]
    if extended_declaration(billboard):
        return []
    aim, rigid = billboard.get('aim'), billboard.get('rigid')
    if aim is None or rigid is None or billboard.get('front') is None or billboard.get('up') is None:
        return []
    aim = enum(aim, BILLBOARD_AIMS)
    front, up = _axis(vector(billboard['front'], 3, None)), _axis(vector(billboard['up'], 3, None))
    if front is None or up is None:
        return []
    if billboard.get('lockedAxis') is not None:
        linear = np.asarray(world, dtype=np.float64)[:3, :3]
        if np.linalg.det(linear) == 0.0:
            return None
        locked = _axis(np.linalg.inv(linear) @ np.asarray(vector(billboard['lockedAxis'], 3, None), dtype=np.float64))
        if locked is None or locked[0] == front[0]:
            return []
        return [('mt_billboard', 'LOCKED_TRACK', 'billboard', {'lock_axis': 'LOCK_' + locked[0], 'track_axis': _track_axis(front)})]
    if aim == 'CameraPosition' and bool(rigid):
        if billboard.get('roll') is None:
            return []
        if up[1] < 0 or up[0] == front[0]:
            return [('mt_billboard', 'DAMPED_TRACK', 'billboard', {'track_axis': _track_axis(front)})]
        return [('mt_billboard', 'TRACK_TO', 'billboard', {'track_axis': _track_axis(front), 'up_axis': 'UP_' + up[0],
                                                          'use_target_z': enum(billboard.get('roll'), BILLBOARD_ROLLS) == 'Camera'})]
    if aim == 'CameraPlane' and bool(rigid) and front == ('Z', 1) and up == ('Y', 1):
        return [('mt_billboard', 'COPY_ROTATION', 'billboard', world_settings)]
    return [('mt_billboard', 'DAMPED_TRACK', 'billboard', {'track_axis': _track_axis(front)})]


# --------------------------------------------------------------------------------------------------------------------
# Typed facing (package versions 6 and 7) and reflected faces (version 7): Shared 0152179's import_model.py
# --------------------------------------------------------------------------------------------------------------------
# Line numbers below are import_model.py at canonical Shared 0152179. These restate the importer (read, never run); the
# 0152179 importer's own pure functions are what TestOutput/billboards-reader-20260928's review-fix check compared them
# with (typed kinds, constraint rows, helpers and drivers, executed against recording stand-ins for bpy).

EXTENDED_ROLLS = ('nodeup', 'cameraswung')  # import_model._typed_billboard_constraints line 4655, lower-cased
REFLECTED_FACE_RULES = ('AuthoredFront', 'DrawnWinding')  # import_model._reflection_rule line 1779
REFLECTED_FACE_MODIFIER = 'mt_reflected_face_winding'  # import_model._reflected_face_group / _reflected_face_modifier
# import_model._matrix_determinant_variables (lines 1793-1801): the evaluated final linear transform's determinant.
DETERMINANT_EXPRESSION = 'm00*(m11*m22-m12*m21)-m01*(m10*m22-m12*m20)+m02*(m10*m21-m11*m20)'
# The helper empties import_model._typed_facing_constraints creates, by role (``_facing_helper`` names each
# ``clip_name(<controller name> + suffix)``).
FACING_HELPER_SUFFIXES = {
    'origin': '.mt_facing_origin', 'camera': '.mt_camera_frame', 'plane': '.mt_camera_front', 'camera_up': '.mt_camera_up',
    'arc': '.mt_camera_arc', 'document': '.mt_document_frame', 'document_up': '.mt_document_up',
    'lock': '.mt_lock_handedness', 'reflection': '.mt_reflection',
}


def extended_declaration(billboard):
    """import_model._typed_billboard_constraints lines 4653-4656 (and _check_manifest's ``extended``): a declaration the
    phase-1 constraints cannot reproduce (a lock frame, a reflection, a threshold, or a NodeUp or CameraSwung roll)."""
    return (billboard.get('lockedAxisFrame') is not None or billboard.get('reflection') is not None or
            billboard.get('planeFallbackCosine') is not None or billboard.get('unfacedDistanceSquared') is not None or
            str(billboard.get('roll') or '').lower() in EXTENDED_ROLLS)


def _declared_axis(value):
    """import_model._axis on a raw manifest value: None for an absent value, else this module's ``_axis``."""
    if value is None:
        return None
    return _axis(vector(value, 3, (0.0, 0.0, 0.0)))


def typed_facing_kind(billboard):
    """import_model._typed_facing_kind (lines 4429-4455), which matches Shared's BlendBillboardConstraints.ExtendedKind:
    the version-six construction of a typed billboard, or 'None'. Names are compared exactly, as the importer does."""
    aim, rigid = billboard.get('aim'), billboard.get('rigid')
    front = _declared_axis(billboard.get('front'))
    if aim not in BILLBOARD_AIMS or rigid is None or front is None:
        return 'None'
    if billboard.get('reflection') is not None and _declared_axis(billboard['reflection']) is None:
        return 'None'
    up = _declared_axis(billboard.get('up'))
    if billboard.get('lockedAxis') is not None:
        frame = billboard.get('lockedAxisFrame')
        locked = _declared_axis(billboard['lockedAxis'])
        if frame == 'Node':
            return 'NodeLocked' if locked is not None and locked[0] != front[0] else 'None'
        return 'DocumentLocked' if frame == 'Document' and up is not None and up[0] != front[0] else 'None'
    prefix = 'Plane' if aim == 'CameraPlane' else 'Position'
    if not rigid:
        return prefix + 'MinimumArc'
    if up is None or up[0] == front[0]:
        return 'None'
    roll = billboard.get('roll')
    if roll in ('NodeUp', 'FixedUp'):
        return prefix + roll
    if (prefix, roll) in (('Plane', 'Camera'), ('Position', 'CameraSwung')):
        return prefix + roll
    return 'None'


def typed_facing(manifest, node):
    """The typed construction import_model._typed_billboard_constraints builds for a node (lines 4648-4651): the kind of
    its typed billboard in a version-6 or version-7 package, or None (no typed billboard, an earlier package, or kind
    'None', which falls back to the phase-1 rules)."""
    billboard = node.get('billboard')
    if billboard is None or manifest is None or manifest.get('packageVersion') not in (6, 7):
        return None
    kind = typed_facing_kind(billboard)
    return None if kind == 'None' else kind


def _typed_thresholds(billboard):
    """(near, cosine, arc): import_model._typed_facing_constraints lines 4549-4557. ``near`` is the unfaced-ball test's
    driver text or 'False', ``cosine`` the plane-fallback cosine (CameraPosition only), ``arc`` whether the camera-arc
    helper exists."""
    radius = billboard.get('unfacedDistanceSquared')
    cosine = billboard.get('planeFallbackCosine') if billboard.get('aim') == 'CameraPosition' else None
    near = 'd*d < %.17g*k*k' % float(radius) if radius is not None and radius > 0 else 'False'
    return near, cosine, cosine is not None


def typed_facing_constraints(billboard, kind):
    """import_model._typed_facing_constraints (lines 4523-4605): the presentation child's facing constraints in order, as
    (name, type, target role, settings, gate); target roles 'billboard' (mt_billboard_target) or a helper role of
    ``FACING_HELPER_SUFFIXES`` ('plane', 'camera_up', 'document_up'); gate 'live', 'face' or 'plane'."""
    front = _declared_axis(billboard['front'])
    up = _declared_axis(billboard.get('up'))
    _near, cosine, _arc = _typed_thresholds(billboard)
    rows = []

    def gated(kind_name, role, gate='live', **settings):
        rows.append(('mt_billboard_%02d' % len(rows), kind_name, role, settings, gate))

    def aim_stack(role, gate):
        if kind == 'NodeLocked':
            locked = _declared_axis(billboard['lockedAxis'])
            gated('LOCKED_TRACK', role, gate, lock_axis='LOCK_' + locked[0], track_axis=_track_axis(front))
        elif kind == 'DocumentLocked':
            gated('LOCKED_TRACK', role, gate, lock_axis='LOCK_' + up[0], track_axis=_track_axis(front))
        elif kind.endswith('NodeUp') or kind.endswith('FixedUp'):
            gated('LOCKED_TRACK', role, gate, lock_axis='LOCK_' + up[0], track_axis=_track_axis(front))
            gated('DAMPED_TRACK', role, gate, track_axis=_track_axis(front))
        else:
            gated('DAMPED_TRACK', role, gate, track_axis=_track_axis(front))

    if kind in ('PlaneCamera', 'PositionCameraSwung'):
        gated('DAMPED_TRACK', 'plane', track_axis=_track_axis(front))
        gated('LOCKED_TRACK', 'camera_up', lock_axis='LOCK_' + front[0], track_axis=_track_axis(up))
        if kind == 'PositionCameraSwung':
            gated('DAMPED_TRACK', 'billboard', 'face', track_axis=_track_axis(front))
    else:
        if kind == 'DocumentLocked' or kind.endswith('FixedUp'):
            gated('DAMPED_TRACK', 'document_up', track_axis=_track_axis(up))
        if billboard['aim'] == 'CameraPlane':
            aim_stack('plane', 'live')
        else:
            aim_stack('billboard', 'face')
            if cosine is not None:
                aim_stack('plane', 'plane')
    return rows


def typed_facing_helpers(billboard, kind):
    """The helper empties of import_model._typed_facing_constraints (``_facing_helper``, lines 4458-4468: EMPTY, an
    identity parent inverse, rotation mode QUATERNION, ``mt_billboard_helper_node_index``), in creation order, as
    (role, parent role, constraints). A parent role is 'controller' (the node object), 'billboard' (mt_billboard_target),
    'root' (mt_root), another helper's role, or 'presentation' for the two helpers ``_facing_insert`` places between the
    presentation child and its children ('lock', then 'reflection', which then holds 'lock'). A constraint is (name, type,
    target role, settings); the anchor's COPY_LOCATION on the origin keeps Blender's default name (None: not compared)."""
    anchor = any(vector(billboard.get('anchor'), 3, (0.0, 0.0, 0.0)))
    world = {'owner_space': 'WORLD', 'target_space': 'WORLD'}
    helpers = [('origin', 'controller', [(None, 'COPY_LOCATION', 'anchor',
                                          {'use_offset': True, 'owner_space': 'CUSTOM', 'target_space': 'CUSTOM'})]
                if anchor else []),
               ('camera', 'billboard', [('mt_anchor_position', 'COPY_LOCATION', 'origin', dict(world))]),
               ('plane', 'camera', []),
               ('camera_up', 'camera', [])]
    if _typed_thresholds(billboard)[2]:
        helpers.append(('arc', 'billboard', [('mt_camera_angle', 'DAMPED_TRACK', 'origin', {'track_axis': 'TRACK_NEGATIVE_Z'})]))
    if kind == 'DocumentLocked' or kind.endswith('FixedUp'):
        helpers.append(('document', 'root', [('mt_anchor_position', 'COPY_LOCATION', 'origin', dict(world))]))
        helpers.append(('document_up', 'document', []))
    inserted = []
    if kind == 'NodeLocked':
        inserted.append('lock')
    if billboard.get('reflection') is not None:
        inserted.append('reflection')
    # Each _facing_insert moves the presentation child's children under the new helper, so the last inserted hangs from
    # the presentation child and every earlier one from the next.
    for position, role in enumerate(inserted):
        helpers.append((role, inserted[position + 1] if position + 1 < len(inserted) else 'presentation', []))
    return helpers


def typed_facing_attach(billboard, kind):
    """The helper role a typed node's children, primitives and pivot empty hang from after ``_facing_insert``: the first
    inserted helper ('lock' before 'reflection'), or None when nothing is inserted."""
    if kind == 'NodeLocked':
        return 'lock'
    if billboard.get('reflection') is not None:
        return 'reflection'
    return None


def typed_facing_drivers(billboard, kind, pivot_empty_present):
    """The simple-expression drivers import_model._typed_facing_constraints adds (lines 4562-4637), as
    {(owner role, data path, array index): (expression, {variable: (type, [target roles])})}; owner roles 'presentation',
    'pivot' or a helper role, target roles as in ``typed_facing_helpers`` plus 'controller' and 'root'. Only the
    declaration decides them: a gate needs the unfaced ball (``near``) or, off the live gate, the plane-fallback cone."""
    near, cosine, arc = _typed_thresholds(billboard)
    cone = 'qx*qx+qy*qy <= %.17g' % ((1.0 - float(cosine)) * .5) if cosine is not None else 'False'
    expressions = {'live': '0.0 if (%s) else 1.0' % near,
                   'face': '0.0 if ((%s) or (%s)) else 1.0' % (near, cone),
                   'plane': '0.0 if (%s) else (1.0 if (%s) else 0.0)' % (near, cone)}
    gate_variables = {'d': ('LOC_DIFF', ['billboard', 'origin']), 'k': ('TRANSFORMS', ['root'])}
    if arc:
        gate_variables.update({'qx': ('TRANSFORMS', ['arc']), 'qy': ('TRANSFORMS', ['arc'])})
    near_variables = {'d': ('LOC_DIFF', ['billboard', 'origin']), 'k': ('TRANSFORMS', ['root'])}
    drivers = {}
    for name, _type, _role, _settings, gate in typed_facing_constraints(billboard, kind):
        if near != 'False' or gate != 'live' and cosine is not None:
            drivers[('presentation', 'constraints["%s"].influence' % name, 0)] = (expressions[gate], dict(gate_variables))
    if kind == 'NodeLocked':
        determinant = {'m%d%d' % (r, c): ('SINGLE_PROP', ['controller']) for r in range(3) for c in range(3)}
        front = _declared_axis(billboard['front'])
        locked = _declared_axis(billboard['lockedAxis'])
        axis = next(i for i, name in enumerate('XYZ') if name not in (front[0], locked[0]))
        if near != 'False':
            drivers[('lock', 'scale', axis)] = ('1.0 if (%s) else (-1.0 if (%s) < 0.0 else 1.0)' % (near, DETERMINANT_EXPRESSION),
                                                dict(determinant, **near_variables))
        else:
            drivers[('lock', 'scale', axis)] = ('-1.0 if (%s) < 0.0 else 1.0' % DETERMINANT_EXPRESSION, determinant)
    if billboard.get('reflection') is not None and near != 'False':
        axis = 'XYZ'.index(_declared_axis(billboard['reflection'])[0])
        drivers[('reflection', 'scale', axis)] = ('1.0 if (%s) else -1.0' % near, dict(near_variables))
    if near != 'False':
        # The anchor and pivot gates read no camera arc (``_facing_gate_driver`` without ``arc``).
        if anchor_empty({'billboard': billboard}) is not None:
            drivers[('presentation', 'constraints["mt_billboard_anchor"].influence', 0)] = (expressions['live'],
                                                                                          dict(near_variables))
        if pivot_empty_present:
            for index, value in enumerate(vector(billboard.get('pivot'), 3, (0.0, 0.0, 0.0))):
                if value != 0:
                    drivers[('pivot', 'location', index)] = ('%.17g if (%s) else 0.0' % (value, near), dict(near_variables))
    return drivers


def typed_facing_reflection_scale(billboard):
    """The stored scale of the 'reflection' helper (import_model lines 4625-4627): -1 on the reflection axis, else 1;
    None when a driver sets it (a declared unfaced ball), whose saved value depends on Blender's last evaluation."""
    if billboard.get('reflection') is None or _typed_thresholds(billboard)[0] != 'False':
        return None
    scale = [1.0, 1.0, 1.0]
    scale['XYZ'.index(_declared_axis(billboard['reflection'])[0])] = -1.0
    return scale


# --------------------------------------------------------------------------------------------------------------------
# Reflected faces (package version 7)
# --------------------------------------------------------------------------------------------------------------------


def reflection_rule(manifest):
    """import_model._reflection_rule (lines 1774-1781): the declared face rule, only at package version 7 (the package
    reader refuses a version-7 package without one); None otherwise."""
    if manifest.get('packageVersion') != 7:
        return None
    declaration = manifest.get('reflectedFaces')
    if isinstance(declaration, dict) and declaration.get('rule') in REFLECTED_FACE_RULES:
        return declaration['rule']
    return None


def never_drawn(manifest, node, primitive):
    """import_model._never_drawn (lines 1784-1790): a NoDraw node role or primitive purpose, or a material culling
    FrontAndBack (names compared exactly, as the importer does)."""
    index = primitive.get('material')
    material = {} if index is None else (manifest.get('materials') or [])[int(index)]
    return (node.get('role') == 'NoDraw' or primitive.get('purpose') == 'NoDraw'
            or (material.get('renderState') or {}).get('cull') == 'FrontAndBack')


def reflected_face_policy(manifest, node, primitive):
    """import_model._apply_reflected_faces (lines 1850-1880): the ``mt_reflected_face_policy`` of a primitive object in a
    version-7 package, or None without a rule. 'notDrawn' also hides the object (render and viewport), and only
    'drawnWinding' adds the ``mt_reflected_face_winding`` NODES modifier with its determinant driver
    (``reflected_face_driver``)."""
    rule = reflection_rule(manifest)
    if rule is None:
        return None
    if never_drawn(manifest, node, primitive):
        return 'notDrawn'
    if primitive.get('material') is None:
        return 'unresolvedMissingMaterial'
    if not culling(manifest['materials'][int(primitive['material'])])[0]:
        return 'bothSides'
    if rule == 'DrawnWinding':
        return 'drawnWinding'
    if node.get('skin') is not None:
        return 'unresolvedSkinnedAuthoredFront'
    return 'nativeAuthoredFront'


def reflected_face_driver():
    """import_model._reflected_face_modifier (lines 1836-1847): the driver on the modifier's Flip input, as (data path
    prefix, expression, {variable: (type, [target roles])}); the one target role 'self' is the primitive object itself,
    and the input's socket identifier (Blender's) is not restated."""
    variables = {'m%d%d' % (r, c): ('SINGLE_PROP', ['self']) for r in range(3) for c in range(3)}
    return 'modifiers["%s"]' % REFLECTED_FACE_MODIFIER, '(%s) < 0.0' % DETERMINANT_EXPRESSION, variables


def vector(value, count, default):
    """import_model._vec (lines 498-515): a list, or an x/y/z/w object; ``default`` when absent."""
    if value is None:
        return tuple(float(v) for v in default)
    if isinstance(value, dict):
        lowered = {str(key).lower(): component for key, component in value.items()}
        return tuple(float(lowered[key]) for key in ('x', 'y', 'z', 'w')[:count])
    return tuple(float(v) for v in value)


def image_color_space(entry):
    """import_model._image_color_space (lines 683-702): 'sRGB' or 'Non-Color' (None for an unknown declaration)."""
    declared = entry.get('colorSpace')
    if declared is not None:
        text = str(declared).replace('-', '').replace('_', '').replace(' ', '').lower()
        if text in ('srgb', 'mixed'):
            return 'sRGB'
        if text == 'noncolor':
            return 'Non-Color'
        return None
    descriptor = entry.get('payloadDescriptor') or entry.get('descriptor') or {}
    if str(descriptor.get('colorSpace') or '').lower() == 'linear':
        return 'Non-Color'
    return 'sRGB'


def image_alpha_mode(entry, material=None, portable_base=False):
    """import_model._image_alpha_mode (Shared 314f125, on canonical main 0152179 at lines 806-831; applied per SAMPLE by
    _convert_binding line 2016): the interpretation of one sample, not of the image. The canonical datablock every image
    loads (_load_images, lines 846-862) is always CHANNEL_PACKED; PREMUL selects a separate cached " [PREMUL]" variant
    datablock (_image_variant, lines 864-912) and requires ALL of: a color base sample (``portable_base``, the
    base-color slot wanting color), a material without an explicit source-layer program, a payload descriptor (else the
    source descriptor) declaring premultiplied alpha, a color space other than Non-Color, no active alpha test, a
    portable Blend or Additive alpha mode, and no render-state blend that is enabled and Affine (an enabled affine
    equation, and any DISABLED blend, keep the stored channels). Never Blender's default STRAIGHT, which hands a Color
    output whose Alpha output is unlinked premultiplied (Cycles floor(c * a / 255) on the 8-bit texel, the 2026-09-25
    hop F render)."""
    descriptor = entry.get('payloadDescriptor') or entry.get('descriptor') or {}
    if (not portable_base or material is None or material.get('source') is not None or
            str(descriptor.get('alphaMeaning') or '').lower() != 'premultiplied' or
            image_color_space(entry) == 'Non-Color'):
        return 'CHANNEL_PACKED'
    state = material.get('renderState') or {}
    alpha_test = state.get('alphaTest')
    if alpha_test is not None and bool(alpha_test.get('enabled', True)):
        return 'CHANNEL_PACKED'
    if enum(material.get('alphaMode'), ('Opaque', 'Mask', 'Blend', 'Additive'), 'Opaque') not in ('Blend', 'Additive'):
        return 'CHANNEL_PACKED'
    blend = state.get('blend')
    if blend is not None and (not bool(blend.get('enabled', True)) or compile_blend_state(blend)[0] == 'Affine'):
        return 'CHANNEL_PACKED'
    return 'PREMUL'


def premultiplied_variant_expected(entry, index, materials):
    """Whether the importer creates the " [PREMUL]" variant datablock of image ``index``: some material samples it as
    its base-color texture (the only ``portable_base`` route) and ``image_alpha_mode`` selects PREMUL for that use."""
    for material in materials or []:
        binding = material.get('texture') or {}
        if binding.get('image') == index and image_alpha_mode(entry, material, True) == 'PREMUL':
            return True
    return False


# --------------------------------------------------------------------------------------------------------------------
# Blend compilation (the drawn E + clamp(T) * Cd graph)
# --------------------------------------------------------------------------------------------------------------------


def blend_term(term):
    """import_model._blend_term (lines 1366-1371): (constant4, scale4, input)."""
    constant = tuple(float(v) for v in (term.get('constant') or (0.0, 0.0, 0.0, 0.0)))
    scale = tuple(float(v) for v in (term.get('scale') or (0.0, 0.0, 0.0, 0.0)))
    selector = enum(term.get('input'), ('Zero', 'SourceColor', 'DestinationColor', 'SourceAlpha', 'DestinationAlpha',
                                        'SourceAlphaSaturate'), 'Zero')
    return constant, scale, selector


def blend_component(equation, component):
    """import_model._blend_component (lines 1603-1643): (status, E terms, T terms) with terms [c, S, Sa, S*S, S*Sa]."""
    operation = enum(equation.get('operation'), ('Add', 'Subtract', 'ReverseSubtract', 'Min', 'Max'), 'Add')
    if operation not in BLEND_SIGNS:
        return 'NonAffine', None, None
    source_sign, destination_sign = BLEND_SIGNS[operation]
    s_constant, s_scale, s_input = blend_term(equation.get('sourceFactor') or {})
    d_constant, d_scale, d_input = blend_term(equation.get('destinationFactor') or {})
    emission = [0.0, source_sign * s_constant[component], 0.0, 0.0, 0.0]
    transmission = [destination_sign * d_constant[component], 0.0, 0.0, 0.0, 0.0]
    if s_scale[component] != 0.0:
        value = source_sign * s_scale[component]
        if s_input == 'SourceColor':
            emission[3] += value
        elif s_input == 'SourceAlpha':
            emission[4] += value
        elif s_input == 'DestinationColor':
            transmission[1] += value
        elif s_input == 'SourceAlphaSaturate' and component == 3:
            emission[1] += value
        elif s_input != 'Zero':
            return 'DestinationAlpha', None, None
    if d_scale[component] != 0.0:
        value = destination_sign * d_scale[component]
        if d_input == 'SourceColor':
            transmission[1] += value
        elif d_input == 'SourceAlpha':
            transmission[2] += value
        elif d_input == 'DestinationColor':
            return 'NonAffine', None, None
        elif d_input == 'SourceAlphaSaturate' and component == 3:
            transmission[0] += value
        elif d_input != 'Zero':
            return 'DestinationAlpha', None, None
    return 'Affine', emission, transmission


def compile_blend_state(blend):
    """import_model._compile_blend_state (lines 1646-1660)."""
    worst = 'Affine'
    emission, transmission = [], []
    for component in range(4):
        equation = blend.get('alphaEquation' if component == 3 else 'colorEquation') or {}
        status, e, t = blend_component(equation, component)
        if status == 'NonAffine':
            worst = 'NonAffine'
        elif status == 'DestinationAlpha' and worst != 'NonAffine':
            worst = 'DestinationAlpha'
        emission.append(e)
        transmission.append(t)
    return worst, emission, transmission


def material_blend(entry):
    """import_model._material_blend: (E terms, T terms) or (None, None) for an undrawn blend. An enabled affine
    render-state blend is then drawn by its display record (``display_record``); a portable Blend or Additive alpha
    mode by the linear graph (``unlit_blend_linear``)."""
    state = entry.get('renderState') or {}
    alpha_mode = enum(entry.get('alphaMode'), ('Opaque', 'Mask', 'Blend', 'Additive'), 'Opaque')
    blend = state.get('blend')
    if blend is not None and not bool(blend.get('enabled', True)):
        return None, None
    if blend is not None:
        klass, emission, transmission = compile_blend_state(blend)
        if klass == 'Affine':
            return emission, transmission
    if alpha_mode == 'Blend':
        return [[0.0, 0.0, 0.0, 0.0, 1.0]] * 4, [[1.0, 0.0, -1.0, 0.0, 0.0]] * 4
    if alpha_mode == 'Additive':
        return [[0.0, 0.0, 0.0, 0.0, 1.0]] * 4, [[1.0, 0.0, 0.0, 0.0, 0.0]] * 4
    return None, None


def poly(terms, s_rgb, s_a):
    """import_model._poly_rgb (lines 2036-2048) on constants: c0 + c1*S + c2*Sa + c3*S*S + c4*S*Sa per channel."""
    out = []
    for channel in range(3):
        c0, c1, c2, c3, c4 = terms[channel]
        s = s_rgb[channel]
        out.append(c0 + c1 * s + c2 * s_a + c3 * s * s + c4 * s * s_a)
    return out


def unlit_blend_linear(emission_terms, transmission_terms, s_rgb_linear, s_a, d_rgb_linear):
    """The LINEAR graph: E = max(poly(E, S, Sa), 0), T = clamp(poly(T, S, Sa), 0, 1), out = E + T * Cd, all in Blender's
    linear space. The importer draws it only for a portable Blend or Additive alpha mode (import_model._blend_closure,
    unlit route); a render-state blend drew it before the display-blend change (the 2026-09-25 hop F render recorded in
    fixtures/hop_f_blend_pairs_20260925.json), and hop F keeps it as the ``linear`` diagnostic model."""
    e = [max(v, 0.0) for v in poly(emission_terms, s_rgb_linear, s_a)]
    t = [min(1.0, max(0.0, v)) for v in poly(transmission_terms, s_rgb_linear, s_a)]
    return [e[c] + t[c] * d_rgb_linear[c] for c in range(3)]


def display_record(entry):
    """The display record an enabled affine render-state blend carries (``renderState.blend.display``, verbatim), which
    import_model._material_blend requires and keeps as ``mt_blend_graph.display``; None for any other material."""
    blend = (entry.get('renderState') or {}).get('blend')
    if blend is None or not bool(blend.get('enabled', True)):
        return None
    klass, _emission, _transmission = compile_blend_state(blend)
    return blend.get('display') if klass == 'Affine' else None


def display_route(entry):
    """import_model._display_blend_route: ``display`` for an unlit surface, ``display-lit`` for a lit one whose record
    carries a lit curve, ``display-lit-fallback`` for a lit one without; None when no display record is drawn."""
    display = display_record(entry)
    if display is None:
        return None
    if bool(entry.get('unlit', False)):
        return 'display'
    return 'display-lit' if display.get('lit') is not None else 'display-lit-fallback'


def display_blend(display, emission_terms, transmission_terms, s_rgb_display, s_a):
    """(E, T) per channel of the importer's display graph for an unlit surface (import_model._display_fit_channel):
    Sd = clamp(S) on display values (the importer encodes the linear source and clamps it), a = clamp(Sa),
    k0 = poly(E terms, Sd, a), k1 = poly(T terms, Sd, a), then display_blend_math.fit_terms of the record's fit."""
    import display_blend_math as dbm
    a = min(1.0, max(0.0, float(s_a)))
    out = []
    for channel in range(3):
        sd = min(1.0, max(0.0, float(s_rgb_display[channel])))
        k0 = _poly1(emission_terms[channel], sd, a)
        k1 = _poly1(transmission_terms[channel], sd, a)
        out.append(dbm.fit_terms(display['fit'], k0, k1))
    return out


def _poly1(terms, s, s_a):
    """c0 + c1*S + c2*Sa + c3*S*S + c4*S*Sa for one channel."""
    c0, c1, c2, c3, c4 = (float(v) for v in terms)
    return c0 + c1 * s + c2 * s_a + c3 * s * s + c4 * s * s_a


def nif_pair(blend, factors):
    """'SF/DF' when the blend's color and alpha equations are both one NIF factor pair (``factors``: name -> manifest
    term, probe_builders.NIF_FACTORS) with Add, else None: the record a C#-written NIF blend must carry is then known."""
    if blend is None:
        return None
    names = {}
    for key in ('colorEquation', 'alphaEquation'):
        equation = blend.get(key) or {}
        if enum(equation.get('operation'), ('Add', 'Subtract', 'ReverseSubtract', 'Min', 'Max'), 'Add') != 'Add':
            return None
        pair = []
        for side in ('sourceFactor', 'destinationFactor'):
            constant, scale, selector = blend_term(equation.get(side) or {})
            match = [name for name, term in factors.items()
                     if (tuple(float(v) for v in term['constant']), tuple(float(v) for v in term['scale']),
                         enum(term['input'], ('Zero', 'SourceColor', 'DestinationColor', 'SourceAlpha', 'DestinationAlpha',
                                              'SourceAlphaSaturate'))) == (constant, scale, selector)]
            if len(match) != 1:
                return None
            pair.append(match[0])
        names[key] = '/'.join(pair)
    return names['colorEquation'] if names['colorEquation'] == names['alphaEquation'] else None


def srgb_encode(x):
    """import_model._srgb_encode_value (lines 1697-1700): linear to sRGB-encoded, clamped below at 0."""
    x = max(float(x), 0.0)
    return 12.92 * x if x < 0.0031308 else 1.055 * math.pow(x, 1.0 / 2.4) - 0.055


def srgb_decode(x):
    """import_model._srgb_decode_value (lines 1703-1706)."""
    x = max(float(x), 0.0)
    return x / 12.92 if x < 0.04045 else math.pow((x + 0.055) / 1.055, 2.4)


# --------------------------------------------------------------------------------------------------------------------
# Geometry: topology, welding, degenerate faces, per-element streams
# --------------------------------------------------------------------------------------------------------------------


def reversed_corner_order(sizes):
    """import_model._reversed_corner_order (lines 2374-2380): reverse each face, keeping its first corner."""
    sizes = np.asarray(sizes, dtype=np.int64)
    total = int(sizes.sum())
    start = np.repeat(np.cumsum(sizes) - sizes, sizes)
    size = np.repeat(sizes, sizes)
    offset = np.arange(total, dtype=np.int64) - start
    return start + (size - offset) % size


def faces_without_repeated_vertices(sizes, blender_corners):
    """import_model._faces_without_repeated_vertices (lines 2425-2445): faces whose corners are distinct vertices."""
    face_count = len(sizes)
    if face_count == 0:
        return np.ones(0, dtype=bool)
    if int(np.min(sizes)) == 3 and int(np.max(sizes)) == 3:
        tri = np.asarray(blender_corners).reshape(-1, 3)
        return (tri[:, 0] != tri[:, 1]) & (tri[:, 1] != tri[:, 2]) & (tri[:, 0] != tri[:, 2])
    keep = np.ones(face_count, dtype=bool)
    start = 0
    for face, size in enumerate(np.asarray(sizes).tolist()):
        block = np.asarray(blender_corners[start:start + size]).tolist()
        if len(set(block)) != size:
            keep[face] = False
        start += size
    return keep


def attribute_layout(component_type, components, normalized):
    """import_model._attribute_layout (lines 2324-2341): (Blender data type, RNA property) or None."""
    if component_type == 'Float32':
        return {1: ('FLOAT', 'value'), 2: ('FLOAT2', 'vector'), 3: ('FLOAT_VECTOR', 'vector'),
                4: ('FLOAT_COLOR', 'color')}.get(components)
    if component_type == 'Int32':
        return {1: ('INT', 'value'), 2: ('INT32_2D', 'value')}.get(components)
    if component_type == 'Int8' and components == 1:
        return 'INT8', 'value'
    if component_type == 'UInt8' and components == 4 and normalized:
        return 'BYTE_COLOR', 'color_srgb'
    if component_type in ('UInt8', 'Int16', 'UInt16') and components == 1:
        return 'INT', 'value'
    return None


class PrimitivePlan:
    """What the importer builds for one primitive, from a source that yields its streams (package or dump).

    ``source`` needs: vertex_count, positions(), normals(), colors(), uvs() (list), tangents() (or None), indices(),
    faces() ((sizes, corners) or None), points() ((point of vertex, point count) or None), attributes() (list of
    (declaration dict, tuples array)), morphs() (list of (name, form, values array)).
    """

    def __init__(self, source, reverse, label):
        self.label = label
        n = source.vertex_count
        self.vertex_count = n
        self.positions = np.ascontiguousarray(source.positions(), dtype=F32).reshape(n, 3)
        faces = source.faces()
        if faces is not None:  # import_model._read_topology lines 2385-2397
            sizes, source_corners = (np.asarray(faces[0], dtype=np.int64).reshape(-1),
                                     np.asarray(faces[1], dtype=np.int64).reshape(-1))
            self.from_faces = True
        else:
            source_corners = np.asarray(source.indices(), dtype=np.int64).reshape(-1)
            sizes = np.full(len(source_corners) // 3, 3, dtype=np.int64)
            self.from_faces = False
        corner_order = reversed_corner_order(sizes) if reverse else np.arange(len(source_corners), dtype=np.int64)
        corners = source_corners[corner_order]
        points = source.points()
        if points is None:  # import_model._weld lines 2410-2413
            vertex_to_blender = np.arange(n, dtype=np.int64)
            representatives = vertex_to_blender
            used = None
            welded = False
            point_count = None
        else:
            point_of_vertex, point_count = points
            used, first, inverse = np.unique(np.asarray(point_of_vertex, dtype=np.int64).reshape(-1), return_index=True,
                                             return_inverse=True)
            vertex_to_blender = inverse.astype(np.int64).reshape(-1)
            representatives = first.astype(np.int64)
            welded = True
        self.source_face_count = len(sizes)
        self.source_corner_count = len(corners)
        keep = faces_without_repeated_vertices(sizes, vertex_to_blender[corners])
        self.degenerate = int(len(keep) - int(keep.sum()))
        self.kept_faces = None
        if self.degenerate:  # lines 2437-2446
            corner_keep = np.repeat(keep, sizes)
            sizes = sizes[keep]
            corners = corners[corner_keep]
            corner_order = corner_order[corner_keep]
            self.kept_faces = np.nonzero(keep)[0]
        self.sizes = sizes.astype(np.int64)
        self.corners = corners
        self.corner_order = corner_order
        self.vertex_to_blender = vertex_to_blender
        self.representatives = representatives
        self.used_points = used
        self.point_count = point_count
        self.welded = welded
        self.reverse = bool(reverse)
        self.blender_vertex_count = len(representatives)
        self.source = source

    # -- the arrays the importer writes (lines 2592-2672) --

    def blender_positions(self):
        return np.ascontiguousarray(self.positions[self.representatives], dtype=F32)

    def loop_vertex_indices(self):
        return self.vertex_to_blender[self.corners].astype(np.int64)

    def loop_starts(self):
        return (np.cumsum(self.sizes) - self.sizes).astype(np.int64)

    def uv_maps(self):
        """[(name, per-corner (u, 1 - v) float32)] per _write_uv_sets (lines 2461-2475)."""
        out = []
        for set_index, values in enumerate(self.source.uvs()):
            corner = np.ascontiguousarray(np.asarray(values, dtype=F32).reshape(-1, 2)[self.corners], dtype=F32)
            corner[:, 1] = np.float32(1.0) - corner[:, 1]
            out.append(('uv%d' % set_index, corner))
        return out

    def color(self, encoding):
        """(data type, domain, values) of the Color attribute (_write_colors lines 2478-2490)."""
        colors = np.asarray(self.source.colors(), dtype=F32).reshape(-1, 4)
        byte = encoding == 'UnsignedByteNormalized'
        values = colors[self.corners] if self.welded else colors
        return ('BYTE_COLOR' if byte else 'FLOAT_COLOR', 'CORNER' if self.welded else 'POINT', np.ascontiguousarray(values, dtype=F32))

    def source_normals(self):
        """mt_source_normal per corner (_write_normals lines 2493-2505)."""
        normals = np.asarray(self.source.normals(), dtype=F32).reshape(-1, 3)
        return np.ascontiguousarray(normals[self.corners], dtype=F32)

    def tangent(self):
        """(domain, values) of mt_source_tangent, or None (_write_tangents lines 2508-2516)."""
        tangents = self.source.tangents()
        if tangents is None:
            return None
        tangents = np.asarray(tangents, dtype=F32).reshape(-1, 4)
        values = tangents[self.corners] if self.welded else tangents
        return ('CORNER' if self.welded else 'POINT', np.ascontiguousarray(values, dtype=F32))

    def attribute_elements(self, domain, values):
        """(Blender domain, per-element values) per _attribute_elements (lines 2519-2540), or raise ValueError."""
        count = len(values)
        if domain == 'Vertex':
            expected = self.vertex_count
            result = ('CORNER', values[self.corners]) if self.welded else ('POINT', values)
        elif domain == 'Point':
            if self.used_points is None:
                raise ValueError('a Point attribute without point indices')
            expected = self.point_count
            result = ('POINT', values[self.used_points])
        elif domain == 'Face':
            expected = self.source_face_count
            result = ('FACE', values if self.kept_faces is None else values[self.kept_faces])
        else:
            expected = self.source_corner_count
            result = ('CORNER', values[self.corner_order])
        if count != expected:
            raise ValueError('a %s attribute has %d tuples; %d are required' % (domain, count, expected))
        return result

    def attributes(self):
        """Per declared attribute: dict with name, data type, domain and the expected element values (None when the
        admission leaves the stream unrepresented), per _write_attributes (lines 2543-2579)."""
        out = []
        for index, (declared, tuples) in enumerate(self.source.attributes()):
            component_type = declared.get('componentType')
            components = int(declared.get('components') or 0)
            normalized = bool(declared.get('normalized', False))
            domain = enum(declared.get('domain'), ('Vertex', 'Point', 'Face', 'FaceCorner'))
            layout = attribute_layout(component_type, components, normalized)
            row = {'index': index, 'name': declared.get('name'), 'domain': domain, 'layout': layout}
            if layout is None:
                row['elements'] = None
                out.append(row)
                continue
            blender_domain, elements = self.attribute_elements(domain, np.asarray(tuples))
            data_type = layout[0]
            if data_type in ('FLOAT', 'FLOAT2', 'FLOAT_VECTOR', 'FLOAT_COLOR'):
                values = np.ascontiguousarray(elements, dtype=F32)
            elif data_type == 'BYTE_COLOR':
                values = np.ascontiguousarray(elements).astype(np.int64)  # the stored bytes
            else:
                values = np.ascontiguousarray(elements).astype(np.int64)
            row.update(dataType=data_type, blenderDomain=blender_domain, elements=values,
                       names=('mt_attr_%s' % declared.get('name'), 'mt_attr_%04d' % index))
            out.append(row)
        return out

    def shape_keys(self):
        """[(clipped name, form, float32 Blender-vertex coordinates)] per _apply_shape_keys (lines 3252-3305)."""
        out = []
        for target_index, (name, form, values) in enumerate(self.source.morphs()):
            values = np.asarray(values, dtype=F32).reshape(-1, 3)
            if form == 'relative':
                coordinates = np.add(self.positions, values, dtype=F32)
            else:
                coordinates = values
            reduced = coordinates[self.representatives]
            if self.welded and not np.array_equal(reduced[self.vertex_to_blender], coordinates):
                raise ValueError('morph %d disagrees across welded vertices' % target_index)
            out.append((clip_name(str(name or ('morph_%04d' % target_index))), form, np.ascontiguousarray(reduced, dtype=F32)))
        return out


class PackagePrimitiveSource:
    """PrimitivePlan source over a package primitive (``package_reader.Package``)."""

    def __init__(self, package, primitive):
        self.package = package
        self.primitive = primitive
        self.vertex_count = int(primitive.get('vertexCount'))

    def _stream(self, manifest):
        return self.package.stream(manifest)

    def positions(self):
        return self._stream(self.primitive['position'])

    def normals(self):
        return self._stream(self.primitive['normal'])

    def colors(self):
        return self._stream(self.primitive['color'])

    def uvs(self):
        return [self._stream(stream) for stream in self.primitive.get('uv') or []]

    def tangents(self):
        stream = self.primitive.get('tangent')
        return self._stream(stream) if stream is not None else None

    def indices(self):
        return self._stream(self.primitive['indices']).reshape(-1)

    def faces(self):
        faces = self.primitive.get('faces')
        if faces is None:
            return None
        return self._stream(faces['sizes']).reshape(-1), self._stream(faces['corners']).reshape(-1)

    def points(self):
        points = self.primitive.get('points')
        if points is None:
            return None
        return self._stream(points['indices']).reshape(-1), int(points.get('pointCount'))

    def attributes(self):
        return [(declared, self._stream(declared['stream'])) for declared in self.primitive.get('attributes') or []]

    def morphs(self):
        out = []
        for target in self.primitive.get('morphs') or []:
            form = str(target.get('form'))
            stream = target.get('absolutePositions') if form == 'absolute' else target.get('positionDeltas')
            out.append((target.get('name'), form, self._stream(stream)))
        return out


class DumpPrimitiveSource:
    """PrimitivePlan source over the exact dump's primitive (``dump_reader.DumpPrimitive``); ``color_stream`` is hop C's
    expected package color stream for it (legacy colors, or the primary attribute's normalized samples)."""

    def __init__(self, primitive, color_stream):
        self.primitive = primitive
        self.vertex_count = primitive.vertex_count
        self._colors = color_stream

    def positions(self):
        return self.primitive.positions

    def normals(self):
        return self.primitive.normals

    def colors(self):
        return self._colors

    def uvs(self):
        return [self.primitive.uv0] + list(self.primitive.additional_uvs)

    def tangents(self):
        return self.primitive.tangents

    def indices(self):
        return self.primitive.indices

    def faces(self):
        faces = self.primitive.faces
        if faces is None:
            return None
        return faces['faceSizes'], faces['cornerIndices']

    def points(self):
        points = self.primitive.point_indices
        if points is None:
            return None
        return points['values'], int(points['pointCount'])

    def attributes(self):
        out = []
        for index, stream in enumerate(self.primitive.attributes):
            declared = {'name': stream['name'], 'componentType': stream['componentType'], 'components': stream['components'],
                        'normalized': stream['normalized'], 'domain': stream['domain']}
            out.append((declared, self.primitive.attribute_tuples(index)))
        return out

    def morphs(self):
        out = []
        for target in self.primitive.morph_targets:
            if target.get('absolutePositions') is not None:
                out.append((target['name'], 'absolute', target['absolutePositions']))
            else:
                out.append((target['name'], 'relative', target['positionDeltas']))
        return out
