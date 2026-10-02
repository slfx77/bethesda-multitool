# SPDX-License-Identifier: 0BSD
"""Hop D: package -> .blend (Shared import_model.py), read back with Shared tools/blender/readback.py.

The readback dump (schema ``multitool.blender-readback/1``, written with ``--include-values``) must equal the package
AND the exact mesh dump, byte for byte, except the custom normals, which carry the declared 0.1 degree tolerance
(readback.py's docstring; design section 6.4 keeps the exact source normals in the ``mt_source_normal`` corner
attribute, which is compared bit for bit). The expectation applies ``import_rules`` (the importer's declared
conversions, restated from import_model.py; see that module for what pins them from outside the importer) to the
package and, with the SAME rules, to the exact dump; the two must agree first, which re-checks hop C's inputs, not the
rules themselves.

Compared: vertices, loops and n-gon sizes (after the declared winding reversal, welding and degenerate-face omission),
UV maps ``uv{k}`` as (u, 1 - v), the ``Color`` attribute (eight-bit storage compared as its stored bytes, float storage
bit for bit, POINT or CORNER domain), ``mt_source_normal``, ``mt_source_tangent``, every ``mt_attr_*`` attribute in its
declared Blender type and domain, ``mt_point_index``, absolute shape keys (Basis plus one block per target, relative
targets added in float32), smooth flags, the mesh ``mt_*`` properties, material variants and their culling, the node and
primitive objects, the root and scene ``mt_*`` properties, every packed image's bytes (SHA-256 and length), color space
and alpha mode, and the animation (``compare_d_anim``: actions, F-curves, Blender's own F-curve evaluation, NLA tracks,
visibility drivers and scene timing against the package; a package without animations must read back no action).

Also compared (``compare_nodes``, ``compare_properties``, ``compare_native_states``, ``compare_absences``): the node
hierarchy (each node object's parent is its package parent's attach object, or ``mt_root``; skinned primitives sit under
``mt_root``), ``mt_local_matrix`` bit for bit, the stored location and scale (the authored TRS as float32; a node without
TRS decomposes its matrix, scale within a declared 1e-5 relative bound), the rotation mode, the object type, every
``matrix_parent_inverse`` the importer sets (identity, or the parent's billboard pivot on its pivot empty), and every
``mt_*`` property the importer copies from the package on nodes, materials, images, meshes, shape keys, the scene and the
root (compared as the stored value, or as the JSON text ``import_model._set_prop`` writes), the lighting model, the blend
record and the surface render method it implies, the per-owner ``mt_native`` rows, polygon material indices, and that no
vertex group, modifier, armature or constraint appears where the package declares no skin, depth bias, billboard or
(version 7) drawn-winding reflected-face rule.

Camera-facing nodes (``import_rules.camera_facing``: a typed billboard, or a legacy presentation billboard other than
None; the Shared billboards gap, phase 1) are a controller plus a presentation child, and hop D expects exactly that:
* the controller is the node object (``mt_node_index``, ``mt_local_matrix``): EMPTY, never the single mesh object, its
  stored location and scale the source TRS in float32 or, without TRS, the decomposition in every package version (never
  moved to the anchor), no constraint, and neither ``mt_billboard_constraint`` nor ``mt_billboard_raw_mode``;
* one presentation child per camera-facing node (``mt_facing_node_index``, no ``mt_node_index``): EMPTY, parented to the
  controller, identity ``matrix_parent_inverse``, location zero, scale 1, ``mt_billboard_constraint`` (whose listed
  constraint types are the facing constraints below) and ``mt_billboard_raw_mode`` exactly when the typed billboard has a
  raw mode, and exactly these constraints, which only it may carry: the anchor's ``import_rules.anchor_constraint`` first
  (a COPY_LOCATION with offset in mt_root's custom space toward the anchor empty; none without an anchor), then the facing
  constraints ``import_rules.billboard_constraints`` restates (name, type, target, settings);
* one anchor empty (``mt_anchor_node_index``) exactly where ``import_rules.anchor_empty`` gives an anchor: EMPTY, parented
  to mt_root, identity ``matrix_parent_inverse``, location the Float32 document anchor, no constraint;
* one pivot empty (``mt_pivot_node_index``) exactly where ``import_rules.pivot_empty`` says, parented to the presentation
  child, carrying ``Translation(-pivot)`` as its ``matrix_parent_inverse``;
* the node's children and primitive objects hang under the pivot empty or else the presentation child, with an identity
  ``matrix_parent_inverse``. The import summary counts the three kinds (``presentationObjects``, ``pivotObjects``,
  ``anchorObjects``).

Package versions 6 and 7 (canonical Shared 0152179): a typed billboard whose ``import_rules.typed_facing`` kind is not
'None' is built by import_model._typed_facing_constraints instead, and hop D expects exactly that construction:
* the presentation child's facing constraints are ``import_rules.typed_facing_constraints`` (``mt_billboard_00``,
  ``mt_billboard_01``... toward mt_billboard_target or a helper), after any anchor constraint;
* the helper empties ``import_rules.typed_facing_helpers`` lists (``mt_billboard_helper_node_index``, each named
  ``<controller>`` + its suffix): EMPTY, the stated parent, identity ``matrix_parent_inverse``, rotation mode QUATERNION,
  exactly the stated constraints, stored location zero except the camera-front and camera-up helpers (the lever, a power
  of two in meters, on +Z and +Y; ``_facing_lever`` restated) and the document-up helper (the lock axis times the lever
  over the root scale), and unit scale except the reflection helper (-1 on its axis unless a driver sets it) and the
  lock-handedness helper (driven, not compared);
* the helpers ``_facing_insert`` places between the presentation child and the node's content (a node lock's, then a
  reflection's, which then holds the first) hold the node's children, primitives and pivot empty;
* their drivers are ``compare_d_anim``'s construction drivers.
A typed billboard whose kind is 'None', and every extended declaration in a version-5 package, gets no facing
constraint (``import_rules.extended_declaration``). Package version 7 (reflected faces,
``compare_reflected_faces``): every primitive object carries ``mt_reflected_face_policy``
(``import_rules.reflected_face_policy``), exactly the 'drawnWinding' ones carry the ``mt_reflected_face_winding`` NODES
modifier (its determinant driver is a construction driver), 'notDrawn' objects are hidden in render and viewport, and a
lone never-drawn primitive is no longer its node's mesh object.

NOT compared, stated rather than implied: node ROTATION (readback.py dumps neither ``rotation_quaternion`` nor
``matrix_basis``, and the importer sets location, rotation and scale without updating the view layer before it saves,
so the saved ``matrix_world`` / ``matrix_local`` are not guaranteed to reflect them; rotation is covered only through
``mt_local_matrix``), the parent inverse under a sheared parent (it depends on Blender's own decomposition; the corpus has
0, and every skip is counted in ``stats``), a locked-track constraint on a singular rest world (the importer's float32
``inverted_safe`` decides; counted as ``billboardConstraintsNotCompared``), the material node graphs and their texture
bindings (hop F's renders judge the drawn result), the armature bones and vertex-group weights, and the derived
``mt_billboard_constraint`` / ``mt_morph_targets`` JSON beyond their listed members (the constraints themselves are
compared).

Controls (each must be detected, ``control_verdict``): one shape-key vertex moved in a package copy; a DDS image
re-encoded through ``save()`` and packed again. A control counts only when its mismatch appears at the mutated element
and the pristine comparison did not already fail there (a masked control reads not applicable, never detected).
"""

import hashlib
import json
import math
import os
import sys
from collections import Counter

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from dump_reader import Dump  # noqa: E402
from gate1a_common import HopResult, Mismatch, OracleError, first_bit_mismatch, first_int_mismatch, json_loads  # noqa: E402
from hop_c import expected_color_stream  # noqa: E402
from package_reader import Package  # noqa: E402
import import_rules as rules  # noqa: E402
import compare_d_anim  # noqa: E402

READBACK_SCHEMA = 'multitool.blender-readback/1'
IMPORT_SUMMARY_SCHEMA = 'multitool.blender-import/1'
CUSTOM_NORMAL_DEGREES = 0.1
MISMATCH_LIMIT = 200


def load_readback(path):
    with open(path, 'r', encoding='utf-8') as f:
        return json.load(f)


def import_summary(stdout_text):
    """The importer's one-line JSON summary (the last stdout line that parses with its schema), or None."""
    for line in reversed((stdout_text or '').splitlines()):
        line = line.strip()
        if line.startswith('{'):
            try:
                value = json.loads(line)
            except ValueError:
                continue
            if value.get('schema') == IMPORT_SUMMARY_SCHEMA:
                return value
    return None


def json_property(value):
    """A readback custom property stored as JSON text (import_model._set_prop) parsed back, else as is."""
    if isinstance(value, str):
        try:
            return json.loads(value)
        except ValueError:
            return value
    return value


def packed(values, typecode):
    dtype = {'f': '<f4', 'i': '<i4', 'b': '<i1'}[typecode]
    return np.ascontiguousarray(np.asarray(values).reshape(-1), dtype=dtype).tobytes()


class Recorder:
    """Collects the mismatches of one check so a control can be judged at its mutated element: the first
    ``MISMATCH_LIMIT`` with their details, and EVERY mismatch's ``where`` (``mismatchWheres``), so a masking mismatch
    past the cap is still seen."""

    def __init__(self, check):
        self.check = check
        self.all = []
        self.wheres = []

    def fail(self, where, kind, detail):
        mismatch = Mismatch(where, kind, detail)
        if len(self.all) < MISMATCH_LIMIT:
            self.all.append(mismatch.to_dict())
        self.wheres.append(where)
        self.check.fail(mismatch)

    def finish(self, detail=None):
        self.check.data['mismatches'] = self.all
        self.check.data['mismatchWheres'] = self.wheres
        self.check.data['mismatchesTruncated'] = max(0, len(self.wheres) - len(self.all))
        if not self.check.mismatches:
            self.check.ok(detail)


def _brief(value, limit=240):
    text = repr(value)
    return text if len(text) <= limit else text[:limit] + '...'


def _deep_equal(actual, expected):
    """Structural equality of JSON-like values; numbers compare by value (1 == 1.0), booleans only with booleans."""
    if isinstance(expected, bool) or isinstance(actual, bool):
        return isinstance(expected, bool) and isinstance(actual, bool) and actual == expected
    if isinstance(expected, (int, float)) and isinstance(actual, (int, float)):
        return float(actual) == float(expected)
    if isinstance(expected, dict) and isinstance(actual, dict):
        return set(actual) == set(expected) and all(_deep_equal(actual[k], expected[k]) for k in expected)
    if isinstance(expected, (list, tuple)) and isinstance(actual, (list, tuple)):
        return len(actual) == len(expected) and all(_deep_equal(a, e) for a, e in zip(actual, expected))
    return actual == expected


def same_property(actual, expected):
    """A readback custom property against the manifest value the importer copied into it: equal as stored, or as the
    JSON text ``import_model._set_prop`` writes for dictionaries, non-numeric lists and verbatim (extras, payload)
    members."""
    if _deep_equal(actual, expected):
        return True
    if isinstance(actual, str) and not isinstance(expected, str):
        try:
            return _deep_equal(json_loads(actual), expected)
        except ValueError:
            return False
    if isinstance(actual, str) and isinstance(expected, str):  # a verbatim JSON string member
        try:
            return json_loads(actual) == expected
        except ValueError:
            return False
    return False


def expect_property(rec, where, props, key, expected):
    """``expected`` None means the importer stores nothing (``_set_prop`` skips None)."""
    if expected is None:
        if key in props:
            rec.fail(where, 'presence', 'present (%s), but the package has no value' % _brief(props.get(key)))
        return
    if key not in props:
        rec.fail(where, 'presence', 'absent, expected %s' % _brief(expected))
    elif not same_property(props[key], expected):
        rec.fail(where, 'value', 'readback %s, expected %s' % (_brief(props[key]), _brief(expected)))


def _norm(value):
    return None if value is None else str(value).replace('_', '').replace('-', '').lower()


def expect_enum(rec, where, props, key, expected):
    """An enum the importer stores canonicalized (``_enum``): compared case-insensitively, ignoring '_' and '-'."""
    if expected is None:
        if key in props:
            rec.fail(where, 'presence', 'present (%r), but the package has no value' % props.get(key))
    elif key not in props or _norm(props.get(key)) != _norm(expected):
        rec.fail(where, 'value', 'readback %r, expected %r' % (props.get(key), expected))


def compare_bulk(rec, where, bulk, expected, typecode):
    """Exact comparison of one readback bulk entry with an expected array (count, packed-bytes SHA-256, values)."""
    if bulk is None:
        rec.fail(where, 'presence', 'absent from the readback')
        return False
    expected = np.asarray(expected)
    components = expected.shape[1] if expected.ndim == 2 else 1
    count = expected.shape[0] if expected.ndim >= 1 else 0
    if bulk.get('count') != count or (bulk.get('components') not in (None, components)):
        rec.fail(where, 'count', 'readback %r x %r, expected %d x %d' % (bulk.get('count'), bulk.get('components'), count, components))
        return False
    digest = hashlib.sha256(packed(expected, typecode)).hexdigest()
    if bulk.get('sha256') == digest:
        return True
    values = bulk.get('values')
    if values is None:
        rec.fail(where, 'sha256', 'readback %s, expected %s (no values to locate the difference)' % (bulk.get('sha256'), digest))
        return False
    if typecode == 'f':
        mismatch = first_bit_mismatch(np.asarray(values, dtype=np.float64).astype(np.float32).reshape(expected.shape),
                                      np.asarray(expected, dtype=np.float32), where)
    else:
        mismatch = first_int_mismatch(np.asarray(values, dtype=np.int64).reshape(expected.shape), expected, where)
    if mismatch is None:
        rec.fail(where, 'sha256', 'values agree but the packed SHA-256 differs (readback %s, expected %s)' % (bulk.get('sha256'), digest))
    else:
        rec.fail(mismatch.where, mismatch.kind, mismatch.detail)
    return False


def compare_bytes(rec, where, bulk, expected_bytes):
    """Eight-bit storage (BYTE_COLOR): readback floats are the stored bytes / 255; compare the bytes themselves."""
    if bulk is None or bulk.get('values') is None:
        rec.fail(where, 'presence', 'absent, or written without --include-values (eight-bit storage is compared by value)')
        return False
    expected_bytes = np.asarray(expected_bytes, dtype=np.int64)
    values = np.asarray(bulk['values'], dtype=np.float64)
    if values.size != expected_bytes.size:
        rec.fail(where, 'count', 'readback %d values, expected %d' % (values.size, expected_bytes.size))
        return False
    stored = np.rint(values * 255.0).astype(np.int64).reshape(expected_bytes.shape)
    mismatch = first_int_mismatch(stored, expected_bytes, where + ' (stored bytes)')
    if mismatch:
        rec.fail(mismatch.where, mismatch.kind, mismatch.detail)
        return False
    return True


def angle_degrees(a, b):
    """atan2(|a x b|, a . b) in degrees, row by row, in double."""
    a = np.asarray(a, dtype=np.float64)
    b = np.asarray(b, dtype=np.float64)
    cross = np.linalg.norm(np.cross(a, b), axis=1)
    dot = np.einsum('ij,ij->i', a, b)
    return np.degrees(np.arctan2(cross, dot))


def _primitive_sources(package, dump):
    """[(mesh index, primitive index, mesh manifest, primitive manifest, dump primitive or None)] in document order."""
    out = []
    for mesh_index, primitive_index, mesh, primitive in package.primitives():
        dump_primitive = None
        if dump is not None and mesh_index < len(dump.meshes) and primitive_index < len(dump.meshes[mesh_index]['primitives']):
            dump_primitive = dump.meshes[mesh_index]['primitives'][primitive_index]
        out.append((mesh_index, primitive_index, mesh, primitive, dump_primitive))
    return out


def _plans(package, dump, rec):
    """Per primitive (m, p): (package plan, dump plan or None). Plan construction failures are mismatches."""
    manifest = package.manifest
    materials = manifest.get('materials') or []
    plans = {}
    for m, p, mesh, primitive, dump_primitive in _primitive_sources(package, dump):
        label = 'mesh %d prim %d' % (m, p)
        material = primitive.get('material')
        reverse = material is not None and rules.culling(materials[int(material)])[1]
        try:
            plan = rules.PrimitivePlan(rules.PackagePrimitiveSource(package, primitive), reverse, label)
        except (ValueError, KeyError, OracleError) as failure:
            rec.fail(label, 'plan', 'the package primitive cannot be planned: %s' % failure)
            continue
        dump_plan = None
        if dump_primitive is not None:
            try:
                dump_plan = rules.PrimitivePlan(rules.DumpPrimitiveSource(dump_primitive, expected_color_stream(dump_primitive)),
                                                reverse, label)
            except (ValueError, KeyError, OracleError) as failure:
                rec.fail(label + ' (dump)', 'plan', 'the dump primitive cannot be planned: %s' % failure)
        plans[(m, p)] = (plan, dump_plan, mesh, primitive)
    return plans


def compare_plans(rec, plans, dump):
    """The package and the exact dump must yield the same importer output for every primitive."""
    if dump is None:
        rec.fail('dump', 'presence', 'no exact dump for this sample')
        return
    for (m, p), (plan, dump_plan, _mesh, primitive) in plans.items():
        label = 'mesh %d prim %d' % (m, p)
        if dump_plan is None:
            rec.fail(label, 'presence', 'the dump has no primitive %d of mesh %d' % (p, m))
            continue
        pairs = [('blender positions', plan.blender_positions(), dump_plan.blender_positions(), 'f'),
                 ('loops', plan.loop_vertex_indices(), dump_plan.loop_vertex_indices(), 'i'),
                 ('n-gon sizes', plan.sizes, dump_plan.sizes, 'i'),
                 ('mt_source_normal', plan.source_normals(), dump_plan.source_normals(), 'f')]
        encoding = primitive.get('colorEncoding')
        pairs.append(('Color', plan.color(encoding)[2], dump_plan.color(encoding)[2], 'f'))
        uv_a, uv_b = plan.uv_maps(), dump_plan.uv_maps()
        if len(uv_a) != len(uv_b):
            rec.fail(label + '/uv', 'count', 'package %d sets, dump %d' % (len(uv_a), len(uv_b)))
        for (name, a), (_, b) in zip(uv_a, uv_b):
            pairs.append((name, a, b, 'f'))
        ta, tb = plan.tangent(), dump_plan.tangent()
        if (ta is None) != (tb is None):
            rec.fail(label + '/mt_source_tangent', 'presence', 'package %s, dump %s' % (ta is not None, tb is not None))
        elif ta is not None:
            pairs.append(('mt_source_tangent', ta[1], tb[1], 'f'))
        try:
            keys_a, keys_b = plan.shape_keys(), dump_plan.shape_keys()
            if len(keys_a) != len(keys_b):
                rec.fail(label + '/shape_keys', 'count', 'package %d, dump %d' % (len(keys_a), len(keys_b)))
            for index, ((name, _, a), (_, _, b)) in enumerate(zip(keys_a, keys_b)):
                pairs.append(('shape key %d (%s)' % (index + 1, name), a, b, 'f'))
        except ValueError as failure:
            rec.fail(label + '/shape_keys', 'plan', str(failure))
        for row_a, row_b in zip(plan.attributes(), dump_plan.attributes()):
            if row_a.get('elements') is not None and row_b.get('elements') is not None:
                pairs.append(('attribute %s' % row_a['name'], row_a['elements'], row_b['elements'],
                              'f' if row_a['dataType'] in ('FLOAT', 'FLOAT2', 'FLOAT_VECTOR', 'FLOAT_COLOR') else 'i'))
        for name, a, b, kind in pairs:
            where = '%s/package-vs-dump/%s' % (label, name)
            a, b = np.asarray(a), np.asarray(b)
            mismatch = first_bit_mismatch(a, b, where) if kind == 'f' else first_int_mismatch(a, b, where)
            if mismatch:
                rec.fail(mismatch.where, mismatch.kind, mismatch.detail)


def _find_attribute(mesh_entry, names):
    by_name = {a['name']: a for a in mesh_entry.get('attributes') or []}
    for name in names:
        if name in by_name:
            return by_name[name]
    return None


def compare_mesh(rec, normals_rec, label, plan, mesh_manifest, primitive, readback_mesh, materials_by_name,
                 package_materials, stats, morph=None):
    """``morph``, for a primitive of an animated package's morph-animated or morph-weighted node: {'values': {block
    index: Float32}, 'sliders': {block index: (low, high)}} (``compare_d_anim.PackageModel``), which replace the mesh
    defaults: the importer writes that node's defaults and the first clip's frame-0 weights, and widens the sliders to
    every animated key and handle."""
    props = readback_mesh.get('properties') or {}
    # counts, vertices, loops, n-gon sizes
    if readback_mesh.get('vertex_count') != plan.blender_vertex_count:
        rec.fail(label + '/vertex_count', 'count', 'readback %r, expected %d' % (readback_mesh.get('vertex_count'), plan.blender_vertex_count))
    compare_bulk(rec, label + '/positions', readback_mesh.get('positions'), plan.blender_positions(), 'f')
    compare_bulk(rec, label + '/loops', readback_mesh.get('loop_vertex_indices'), plan.loop_vertex_indices(), 'i')
    compare_bulk(rec, label + '/n-gon sizes', readback_mesh.get('polygon_loop_totals'), plan.sizes, 'i')
    compare_bulk(rec, label + '/polygon loop starts', readback_mesh.get('polygon_loop_starts'), plan.loop_starts(), 'i')
    histogram = sorted([int(size), int(count)] for size, count in Counter(plan.sizes.tolist()).items())
    if readback_mesh.get('polygon_size_histogram') != histogram:
        rec.fail(label + '/n-gon histogram', 'value', 'readback %r, expected %r' % (readback_mesh.get('polygon_size_histogram'), histogram))
    stats['loops'] += len(plan.corners)
    stats['faces'] += len(plan.sizes)
    stats['vertices'] += plan.blender_vertex_count
    stats['ngons'] += int((plan.sizes > 3).sum())
    # UV maps
    expected_uvs = plan.uv_maps()
    layers = {layer['name']: layer for layer in readback_mesh.get('uv_maps') or []}
    expected_layer_names = sorted(name for index, (name, _) in enumerate(expected_uvs) if index < rules.UV_LAYER_LIMIT)
    if sorted(layers) != expected_layer_names:
        rec.fail(label + '/uv maps', 'names', 'readback %s, expected %s' % (sorted(layers), expected_layer_names))
    for index, (name, values) in enumerate(expected_uvs):
        if index < rules.UV_LAYER_LIMIT:
            compare_bulk(rec, '%s/uv map %s' % (label, name), (layers.get(name) or {}).get('values'), values, 'f')
        else:
            attribute = _find_attribute(readback_mesh, [name])
            if attribute is None or attribute.get('data_type') != 'FLOAT2' or attribute.get('domain') != 'CORNER':
                rec.fail('%s/uv attribute %s' % (label, name), 'presence', 'expected a FLOAT2 CORNER attribute')
            else:
                compare_bulk(rec, '%s/uv attribute %s' % (label, name), attribute.get('values'), values, 'f')
    stats['uvMaps'] += len(expected_uvs)
    # Color
    encoding = rules.enum(primitive.get('colorEncoding'), rules.COLOR_ENCODINGS)
    data_type, domain, values = plan.color(encoding)
    colors = {c['name']: c for c in readback_mesh.get('color_attributes') or []}
    color = colors.get(rules.VERTEX_COLOR_LAYER)
    if color is None:
        rec.fail(label + '/Color', 'presence', 'no Color attribute')
    else:
        if color.get('data_type') != data_type or color.get('domain') != domain:
            rec.fail(label + '/Color', 'type', 'readback %s %s, expected %s %s' % (color.get('data_type'), color.get('domain'), data_type, domain))
        elif data_type == 'BYTE_COLOR':
            compare_bytes(rec, label + '/Color', color.get('values'), np.rint(values.astype(np.float64) * 255.0))
        else:
            compare_bulk(rec, label + '/Color', color.get('values'), values, 'f')
    if readback_mesh.get('active_color_attribute') != rules.VERTEX_COLOR_LAYER:
        rec.fail(label + '/active color', 'value', 'readback %r, expected Color' % readback_mesh.get('active_color_attribute'))
    # mt_source_normal, smooth flags, custom normals
    source_normals = plan.source_normals()
    attribute = _find_attribute(readback_mesh, [rules.SOURCE_NORMAL_ATTRIBUTE])
    if attribute is None or attribute.get('data_type') != 'FLOAT_VECTOR' or attribute.get('domain') != 'CORNER':
        rec.fail(label + '/mt_source_normal', 'presence', 'expected a FLOAT_VECTOR CORNER attribute, found %r'
                 % (((attribute or {}).get('data_type'), (attribute or {}).get('domain')),))
    else:
        compare_bulk(rec, label + '/mt_source_normal', attribute.get('values'), source_normals, 'f')
    mode = rules.enum(primitive.get('normalMode'), ('Vertex', 'Flat'), 'Vertex')
    smooth = readback_mesh.get('polygon_use_smooth')
    if smooth is not None and smooth.get('values') is not None:
        expected_smooth = [1 if mode == 'Vertex' else 0] * len(plan.sizes)
        if list(smooth['values']) != expected_smooth:
            rec.fail(label + '/use_smooth', 'value', 'expected every face %s' % ('smooth' if mode == 'Vertex' else 'flat'))
    custom = readback_mesh.get('custom_normals') or {}
    if mode == 'Vertex' and len(plan.corners):
        if not custom.get('has_custom_normals'):
            normals_rec.fail(label + '/custom normals', 'presence', 'Vertex mode sets custom normals; the readback has none')
        else:
            corner = (custom.get('corner_normals') or {}).get('values')
            if corner is None:
                normals_rec.fail(label + '/custom normals', 'presence', 'no corner-normal values (run readback with --include-values)')
            else:
                corner = np.asarray(corner, dtype=np.float64).reshape(-1, 3)
                source = source_normals.astype(np.float64)
                if corner.shape != source.shape:
                    normals_rec.fail(label + '/custom normals', 'count', 'readback %d corners, expected %d' % (corner.shape[0], source.shape[0]))
                else:
                    nonzero = np.linalg.norm(source, axis=1) > 0.0
                    angles = angle_degrees(source[nonzero], corner[nonzero]) if nonzero.any() else np.zeros(0)
                    worst = float(angles.max()) if angles.size else 0.0
                    stats['customNormalsMaxDegrees'] = max(stats['customNormalsMaxDegrees'], worst)
                    stats['customNormalsCompared'] += int(nonzero.sum())
                    stats['customNormalsZeroSourceCorners'] += int((~nonzero).sum())
                    if worst > CUSTOM_NORMAL_DEGREES or not np.isfinite(worst):
                        where = int(np.nonzero(nonzero)[0][int(np.argmax(angles))])
                        normals_rec.fail(label + '/custom normals', 'tolerance', 'corner %d differs by %.6f degrees (bound %.1f)'
                                         % (where, worst, CUSTOM_NORMAL_DEGREES))
    elif custom.get('has_custom_normals'):
        normals_rec.fail(label + '/custom normals', 'presence', 'Flat mode sets no custom normals; the readback has them')
    # mt_source_tangent
    tangent = plan.tangent()
    attribute = _find_attribute(readback_mesh, [rules.SOURCE_TANGENT_ATTRIBUTE])
    if tangent is None:
        if attribute is not None:
            rec.fail(label + '/mt_source_tangent', 'presence', 'present, but the package has no tangent stream')
    elif attribute is None or attribute.get('data_type') != 'FLOAT_COLOR' or attribute.get('domain') != tangent[0]:
        rec.fail(label + '/mt_source_tangent', 'presence', 'expected a FLOAT_COLOR %s attribute' % tangent[0])
    else:
        compare_bulk(rec, label + '/mt_source_tangent', attribute.get('values'), tangent[1], 'f')
    # typed attribute streams
    for row in plan.attributes():
        names = ('mt_attr_%s' % row['name'], 'mt_attr_%04d' % row['index'])
        attribute = _find_attribute(readback_mesh, names)
        where = '%s/attribute %s' % (label, row['name'])
        if row['elements'] is None:
            if attribute is not None:
                rec.fail(where, 'presence', 'the admission leaves this stream unrepresented, yet %s exists' % attribute['name'])
            continue
        stats['attributes'] += 1
        if attribute is None:
            rec.fail(where, 'presence', 'no attribute %s or %s' % names)
            continue
        if attribute.get('data_type') != row['dataType'] or attribute.get('domain') != row['blenderDomain']:
            rec.fail(where, 'type', 'readback %s %s, expected %s %s' % (attribute.get('data_type'), attribute.get('domain'),
                                                                       row['dataType'], row['blenderDomain']))
            continue
        if row['dataType'] == 'BYTE_COLOR':
            compare_bytes(rec, where, attribute.get('values'), row['elements'])
        elif row['dataType'] in ('FLOAT', 'FLOAT2', 'FLOAT_VECTOR', 'FLOAT_COLOR'):
            compare_bulk(rec, where, attribute.get('values'), row['elements'], 'f')
        elif row['dataType'] == 'INT8':
            compare_bulk(rec, where, attribute.get('values'), row['elements'], 'b')
        else:
            compare_bulk(rec, where, attribute.get('values'), row['elements'], 'i')
    if plan.welded:
        attribute = _find_attribute(readback_mesh, ['mt_point_index'])
        if attribute is None or attribute.get('data_type') != 'INT' or attribute.get('domain') != 'POINT':
            rec.fail(label + '/mt_point_index', 'presence', 'expected an INT POINT attribute on a welded primitive')
        else:
            compare_bulk(rec, label + '/mt_point_index', attribute.get('values'), plan.used_points, 'i')
    # shape keys
    try:
        expected_keys = plan.shape_keys()
    except ValueError as failure:
        rec.fail(label + '/shape_keys', 'plan', str(failure))
        expected_keys = []
    keys = readback_mesh.get('shape_keys')
    if not expected_keys:
        if keys is not None:
            rec.fail(label + '/shape_keys', 'presence', 'present, but the package has no morph target')
    elif keys is None:
        rec.fail(label + '/shape_keys', 'presence', 'absent, expected Basis plus %d' % len(expected_keys))
    else:
        blocks = sorted(keys.get('key_blocks') or [], key=lambda b: b['index'])
        if len(blocks) != len(expected_keys) + 1:
            rec.fail(label + '/shape_keys', 'count', 'readback %d blocks, expected %d' % (len(blocks), len(expected_keys) + 1))
        else:
            weights = mesh_manifest.get('morphWeights') or [0.0] * len(expected_keys)
            if blocks[0].get('name') != 'Basis':
                rec.fail(label + '/shape_keys/0', 'name', 'readback %r, expected Basis' % blocks[0].get('name'))
            compare_bulk(rec, label + '/shape_keys/0 (Basis)/coordinates', blocks[0].get('coordinates'), plan.blender_positions(), 'f')
            seen = Counter()
            for index, ((name, _form, coordinates), block) in enumerate(zip(expected_keys, blocks[1:]), start=1):
                where = '%s/shape_keys/%d (%s)' % (label, index, name)
                seen[name] += 1
                actual_name = block.get('name')
                if actual_name != name and not (seen[name] > 1 and str(actual_name).startswith(name + '.')) \
                        and not (name == 'Basis' and str(actual_name).startswith('Basis.')):
                    rec.fail(where, 'name', 'readback %r, expected %r' % (actual_name, name))
                compare_bulk(rec, where + '/coordinates', block.get('coordinates'), coordinates, 'f')
                weight = float(weights[index - 1])
                if morph is not None and index in morph['values']:
                    weight = morph['values'][index]
                if block.get('value') != float(np.float32(weight)):
                    rec.fail(where + '/value', 'value', 'readback %r, expected %r' % (block.get('value'), float(np.float32(weight))))
                if block.get('relative_key') != blocks[0].get('name'):
                    rec.fail(where + '/relative_key', 'value', 'readback %r, expected the basis' % block.get('relative_key'))
                low, high = max(-10.0, min(0.0, weight)), min(10.0, max(1.0, weight))
                if morph is not None and index in morph['sliders']:
                    low, high = morph['sliders'][index]
                if block.get('slider_min') != float(np.float32(low)) or block.get('slider_max') != float(np.float32(high)):
                    rec.fail(where + '/sliders', 'value', 'readback [%r, %r], expected [%r, %r]'
                             % (block.get('slider_min'), block.get('slider_max'), low, high))
            stats['shapeKeys'] += len(expected_keys)
    # mesh properties
    expected_props = {'mt_mesh_index': plan_index(label)[0], 'mt_primitive_index': plan_index(label)[1],
                      'mt_normal_mode': mode, 'mt_color_encoding': encoding, 'mt_winding_reversed': plan.reverse,
                      'mt_topology': 'faces' if plan.from_faces else 'triangles', 'mt_name': str(mesh_manifest.get('name') or ''),
                      'mt_primitive_name': str(primitive.get('name') or '')}
    if plan.degenerate:
        expected_props['mt_degenerate_faces_omitted'] = plan.degenerate
    if plan.welded:
        expected_props['mt_point_count'] = plan.point_count
    for key, expected in expected_props.items():
        actual = props.get(key)
        if isinstance(expected, bool):
            actual = bool(actual) if actual in (0, 1, True, False) else actual
        if actual != expected:
            rec.fail('%s/properties/%s' % (label, key), 'value', 'readback %r, expected %r' % (props.get(key), expected))
    if not plan.degenerate and 'mt_degenerate_faces_omitted' in props:
        rec.fail(label + '/properties/mt_degenerate_faces_omitted', 'presence', 'present, but no face repeats a vertex')
    stats['degenerateFacesOmitted'] += plan.degenerate
    expect_enum(rec, '%s/properties/mt_purpose' % label, props, 'mt_purpose', primitive.get('purpose') or 'Render')
    expect_property(rec, '%s/properties/mt_normal_provenance' % label, props, 'mt_normal_provenance', primitive.get('normalProvenance'))
    expect_property(rec, '%s/properties/mt_color_space' % label, props, 'mt_color_space',
                    {'colorSpace': primitive.get('colorSpace'), 'provenance': primitive.get('colorSpaceProvenance'),
                     'attribute': primitive.get('primaryColorAttribute')})
    expect_property(rec, '%s/properties/mt_mesh_extras' % label, props, 'mt_mesh_extras', mesh_manifest.get('extras'))
    expect_property(rec, '%s/properties/mt_extras' % label, props, 'mt_extras', primitive.get('extras'))
    compare_attribute_rows(rec, label, plan, primitive, readback_mesh, props)
    compare_bulk(rec, label + '/polygon material indices', readback_mesh.get('polygon_material_indices'),
                 np.zeros(len(plan.sizes), dtype=np.int64), 'i')
    if expected_keys and keys is not None:
        weights = [float(w) for w in (mesh_manifest.get('morphWeights') or [0.0] * len(expected_keys))]
        expect_property(rec, '%s/properties/mt_morph_weights' % label, props, 'mt_morph_weights', weights)
        key_props = keys.get('properties') or {}
        expect_property(rec, '%s/shape_keys/properties/mt_name' % label, key_props, 'mt_name', str(mesh_manifest.get('name') or ''))
        expect_property(rec, '%s/shape_keys/properties/mt_morph_weights' % label, key_props, 'mt_morph_weights', weights)
        expect_property(rec, '%s/shape_keys/properties/mt_morph_targets' % label, key_props, 'mt_morph_targets',
                        [{'name': target.get('name'), 'form': str(target.get('form')), 'normalDeltas': target.get('normalDeltas') is not None,
                          'tangentDeltas': target.get('tangentDeltas') is not None} for target in primitive.get('morphs') or []])
    elif 'mt_morph_weights' in props:
        rec.fail('%s/properties/mt_morph_weights' % label, 'presence', 'present, but the primitive has no shape keys')
    # material slot and variant
    material_index = primitive.get('material')
    names = readback_mesh.get('materials') or []
    if material_index is None:
        if names:
            rec.fail(label + '/materials', 'presence', 'the primitive names no material, the mesh has %s' % names)
    elif len(names) != 1 or names[0] not in materials_by_name:
        rec.fail(label + '/materials', 'presence', 'expected one material slot, found %s' % names)
    else:
        material = materials_by_name[names[0]]
        mprops = material.get('properties') or {}
        key = rules.vertex_color_key(primitive)
        variant = json_property(mprops.get('mt_vertex_color_variant'))
        if mprops.get('mt_material_index') != int(material_index) or variant != {'domain': key[0], 'storage': key[1]}:
            rec.fail(label + '/materials', 'variant', 'slot %r is material %r variant %r, expected %d %r'
                     % (names[0], mprops.get('mt_material_index'), variant, int(material_index), key))
        use_culling, _reverse, culling_label = rules.culling(package_materials[int(material_index)])
        if material.get('use_backface_culling') != use_culling or mprops.get('mt_culling') != culling_label:
            rec.fail(label + '/materials/culling', 'value', 'readback %r %r, expected %r %r'
                     % (material.get('use_backface_culling'), mprops.get('mt_culling'), use_culling, culling_label))


def compare_attribute_rows(rec, label, plan, primitive, readback_mesh, props):
    """``mt_attributes``: one row per declared stream copying its declaration, naming the Blender attribute that holds
    it (which must exist with that type and domain), or ``blenderName`` None for an unrepresented stream."""
    declared = primitive.get('attributes') or []
    where = '%s/properties/mt_attributes' % label
    if not declared:
        if 'mt_attributes' in props:
            rec.fail(where, 'presence', 'present, but the primitive declares no attribute stream')
        return
    rows = props.get('mt_attributes')
    try:
        rows = json_loads(rows) if isinstance(rows, str) else rows
    except ValueError:
        rows = None
    if not isinstance(rows, list) or len(rows) != len(declared):
        rec.fail(where, 'count', 'readback %s, expected %d row(s)' % (_brief(rows), len(declared)))
        return
    by_name = {a['name']: a for a in readback_mesh.get('attributes') or []}
    for plan_row, row, entry in zip(plan.attributes(), rows, declared):
        at = '%s/%d' % (where, plan_row['index'])
        copied = {'index': plan_row['index'], 'name': entry.get('name'), 'semantic': entry.get('semantic'),
                  'components': int(entry.get('components') or 0), 'normalized': bool(entry.get('normalized', False)),
                  'count': entry.get('count'), 'colorSpace': entry.get('colorSpace'),
                  'colorSpaceProvenance': entry.get('colorSpaceProvenance')}
        for key, value in copied.items():
            if not isinstance(row, dict) or not _deep_equal(row.get(key), value):
                rec.fail('%s/%s' % (at, key), 'value', 'readback %s, expected %s' % (_brief((row or {}).get(key)), _brief(value)))
        for key in ('domain', 'componentType'):
            if _norm((row or {}).get(key)) != _norm(entry.get(key)):
                rec.fail('%s/%s' % (at, key), 'value', 'readback %r, expected %r' % ((row or {}).get(key), entry.get(key)))
        name = (row or {}).get('blenderName')
        if plan_row['elements'] is None:
            if name is not None:
                rec.fail(at + '/blenderName', 'value', 'readback %r, expected None (the stream is unrepresented)' % name)
            continue
        attribute = by_name.get(name)
        if name not in plan_row['names'] or attribute is None:
            rec.fail(at + '/blenderName', 'value', 'readback %r, expected one of %s naming an existing attribute' % (name, plan_row['names']))
        elif (row.get('blenderType'), row.get('blenderDomain')) != (plan_row['dataType'], plan_row['blenderDomain']) or \
                (attribute.get('data_type'), attribute.get('domain')) != (plan_row['dataType'], plan_row['blenderDomain']):
            rec.fail(at + '/blenderType', 'value', 'row %s %s, attribute %s %s, expected %s %s'
                     % (row.get('blenderType'), row.get('blenderDomain'), attribute.get('data_type'), attribute.get('domain'),
                        plan_row['dataType'], plan_row['blenderDomain']))


def plan_index(label):
    parts = label.split()
    return int(parts[1]), int(parts[3])


IMAGE_COPIED_MEMBERS = (('packing', 'mt_packing'), ('originalContainer', 'mt_original_container'), ('descriptor', 'mt_descriptor'),
                        ('payloadDescriptor', 'mt_payload_descriptor'), ('origin', 'mt_origin'), ('location', 'mt_source_location'),
                        ('standardPayloadNote', 'mt_standard_payload_note'),
                        ('standardPayloadEvidence', 'mt_standard_payload_evidence'), ('derivation', 'mt_derivation'),
                        ('displayTransforms', 'mt_display_transforms'), ('legacyMipLevelCount', 'mt_legacy_mip_level_count'),
                        ('extras', 'mt_extras'))


def compare_images(rec, package, readback, stats):
    images = readback.get('images') or []
    by_index = {}
    for image in images:
        index = (image.get('properties') or {}).get('mt_image_index')
        if index is not None:
            by_index.setdefault(int(index), []).append(image)
    for index, entry in enumerate(package.manifest.get('images') or []):
        where = 'images/%d' % index
        blender = by_index.get(index, [])
        if not entry.get('path'):
            if blender:
                rec.fail(where, 'presence', 'the package image is missing, yet Blender holds %d image(s) for it' % len(blender))
            continue
        # Per-use PREMUL (Shared 314f125): the canonical datablock is always CHANNEL_PACKED; a " [PREMUL]" variant
        # datablock exists exactly when some material's base-color sample selects PREMUL.
        canonical = [i for i in blender if i.get('alpha_mode') == 'CHANNEL_PACKED']
        variants = [i for i in blender if i.get('alpha_mode') != 'CHANNEL_PACKED']
        if len(canonical) != 1:
            if len(blender) == 1:
                rec.fail(where + '/alpha_mode', 'value', "readback %r, expected the canonical 'CHANNEL_PACKED' datablock"
                         % blender[0].get('alpha_mode'))
            else:
                rec.fail(where, 'presence', 'expected one canonical CHANNEL_PACKED Blender image, found %d (of %d)'
                         % (len(canonical), len(blender)))
            continue
        expected_variant = rules.premultiplied_variant_expected(entry, index, package.manifest.get('materials'))
        if expected_variant:
            if len(variants) != 1 or variants[0].get('alpha_mode') != 'PREMUL':
                rec.fail(where + '/premultiplied variant', 'presence',
                         'expected one PREMUL variant datablock, found %r' % [i.get('alpha_mode') for i in variants])
        elif variants:
            rec.fail(where + '/premultiplied variant', 'presence',
                     'no material base-color sample selects PREMUL, yet Blender holds variant(s) %r'
                     % [i.get('alpha_mode') for i in variants])
        image = canonical[0]
        try:
            data = package.image_payload(entry)
        except OracleError as failure:
            rec.fail(where, 'package', 'the package image cannot be read: %s' % failure)
            continue
        digest = hashlib.sha256(data).hexdigest()
        stats['images'] += 1
        if not image.get('is_packed'):
            rec.fail(where + '/is_packed', 'value', 'the image is not packed')
        if image.get('packed_sha256') != digest:
            rec.fail(where + '/packed_sha256', 'value', 'packed bytes hash to %s, the package holds %s' % (image.get('packed_sha256'), digest))
        if image.get('packed_length') != len(data):
            rec.fail(where + '/packed_length', 'value', 'packed %r bytes, the package holds %d' % (image.get('packed_length'), len(data)))
        color_space = rules.image_color_space(entry)
        if image.get('colorspace') != color_space:
            rec.fail(where + '/colorspace', 'value', 'readback %r, expected %r' % (image.get('colorspace'), color_space))
        if expected_variant and len(variants) == 1:
            variant = variants[0]
            if variant.get('packed_sha256') != digest:
                rec.fail(where + '/premultiplied variant/packed_sha256', 'value',
                         'variant bytes hash to %s, the package holds %s' % (variant.get('packed_sha256'), digest))
        mt_sha = (image.get('properties') or {}).get('mt_sha256')
        if mt_sha != str(entry.get('sha256') or '').lower():
            rec.fail(where + '/properties/mt_sha256', 'value', 'readback %r, expected %r' % (mt_sha, entry.get('sha256')))
        props = image.get('properties') or {}
        expect_property(rec, where + '/properties/mt_name', props, 'mt_name', str(entry.get('name') or ('image_%04d' % index)))
        expect_property(rec, where + '/properties/mt_source_container', props, 'mt_source_container', str(entry.get('container') or ''))
        expect_property(rec, where + '/properties/mt_color_space', props, 'mt_color_space', color_space)
        expect_property(rec, where + '/properties/mt_role_color_space', props, 'mt_role_color_space', entry.get('colorSpace'))
        for key, prop in IMAGE_COPIED_MEMBERS:
            value = entry.get(key)
            expect_property(rec, '%s/properties/%s' % (where, prop), props, prop, value if value is not None and value != [] else None)
    known = set(range(len(package.manifest.get('images') or [])))
    extra = sorted(set(by_index) - known)
    if extra:
        rec.fail('images', 'presence', 'Blender images name absent package images %s' % extra)


def compare_objects_and_properties(rec, package, readback, stats):
    manifest = package.manifest
    objects = readback.get('objects') or []
    meshes_by_name = {mesh['name']: mesh for mesh in readback.get('meshes') or []}
    node_objects = {}
    primitive_objects = Counter()
    for obj in objects:
        props = obj.get('properties') or {}
        if 'mt_local_matrix' in props:
            node_objects.setdefault(int(props.get('mt_node_index', -1)), []).append(obj)
        if 'mt_primitive_index' in props:
            mesh = meshes_by_name.get(obj.get('data')) or {}
            mesh_props = mesh.get('properties') or {}
            key = (int(props.get('mt_node_index', -1)), int(props.get('mt_mesh_index', -1)), int(props.get('mt_primitive_index', -1)))
            if (mesh_props.get('mt_mesh_index'), mesh_props.get('mt_primitive_index')) != key[1:]:
                rec.fail('objects/%s' % obj.get('name'), 'data', 'the object for %s instances mesh %r (%r, %r)'
                         % (key, obj.get('data'), mesh_props.get('mt_mesh_index'), mesh_props.get('mt_primitive_index')))
            primitive_objects[key] += 1
    nodes = manifest.get('nodes') or []
    for index, node in enumerate(nodes):
        found = node_objects.get(index, [])
        if len(found) != 1:
            rec.fail('nodes/%d' % index, 'presence', 'expected one node object, found %d' % len(found))
    extra_nodes = sorted(set(node_objects) - set(range(len(nodes))))
    if extra_nodes:
        rec.fail('nodes', 'presence', 'objects name absent nodes %s' % extra_nodes)
    # Camera-facing nodes: one presentation child each, a pivot empty exactly where the child inverse is not identity, and
    # an anchor empty exactly where a typed billboard declares a nonzero anchor.
    for key, label, wanted, stat in (('mt_facing_node_index', 'presentation child', rules.camera_facing, 'presentationObjects'),
                                     ('mt_pivot_node_index', 'pivot empty', rules.pivot_empty, 'pivotObjects'),
                                     ('mt_anchor_node_index', 'anchor empty', lambda n: rules.anchor_empty(n) is not None,
                                      'anchorObjects')):
        found = {}
        for obj in objects:
            props = obj.get('properties') or {}
            if key in props:
                found.setdefault(int(props[key]), []).append(obj)
        for index, node in enumerate(nodes):
            expected = 1 if wanted(node) else 0
            if len(found.get(index, [])) != expected:
                rec.fail('nodes/%d/%s' % (index, key), 'presence', 'expected %d %s, found %d' % (expected, label, len(found.get(index, []))))
        extra = sorted(set(found) - set(range(len(nodes))))
        if extra:
            rec.fail('objects', 'presence', '%s objects name absent nodes %s' % (label, extra))
        stats[stat] = sum(len(objs) for objs in found.values())
    # Typed facing (package versions 6 and 7): the helper empties import_model._typed_facing_constraints creates.
    helpers_found = {}
    for obj in objects:
        props = obj.get('properties') or {}
        if 'mt_billboard_helper_node_index' in props:
            helpers_found.setdefault(int(props['mt_billboard_helper_node_index']), []).append(obj)
    for index, node in enumerate(nodes):
        kind = rules.typed_facing(manifest, node)
        expected = len(rules.typed_facing_helpers(node['billboard'], kind)) if kind is not None else 0
        if len(helpers_found.get(index, [])) != expected:
            rec.fail('nodes/%d/mt_billboard_helper_node_index' % index, 'presence', 'expected %d typed-facing helper(s) '
                     '(kind %s), found %d' % (expected, kind, len(helpers_found.get(index, []))))
    extra = sorted(set(helpers_found) - set(range(len(nodes))))
    if extra:
        rec.fail('objects', 'presence', 'typed-facing helpers name absent nodes %s' % extra)
    stats['typedFacingHelperObjects'] = sum(len(objs) for objs in helpers_found.values())
    expected_primitive_objects = Counter()
    for index, node in enumerate(nodes):
        if node.get('mesh') is None:
            continue
        mesh_index = int(node['mesh'])
        for primitive_index in range(len(manifest['meshes'][mesh_index].get('primitives') or [])):
            expected_primitive_objects[(index, mesh_index, primitive_index)] += 1
    if primitive_objects != expected_primitive_objects:
        missing = sorted(set(expected_primitive_objects) - set(primitive_objects))
        extra = sorted(set(primitive_objects) - set(expected_primitive_objects))
        doubled = sorted(k for k, v in primitive_objects.items() if v > 1)
        rec.fail('objects', 'presence', 'primitive objects missing %s, unexpected %s, duplicated %s' % (missing[:8], extra[:8], doubled[:8]))
    stats['nodes'] = len(nodes)
    stats['primitiveObjects'] = sum(primitive_objects.values())
    # root and scene properties
    roots = [root for root in readback.get('roots') or [] if root.get('name') == 'mt_root' or
             (root.get('properties') or {}).get('mt_name') == 'mt_root']
    if len(roots) != 1:
        rec.fail('mt_root', 'presence', 'expected one mt_root, found %d' % len(roots))
    else:
        props = roots[0].get('properties') or {}
        root = manifest.get('root') or {}
        if root.get('matrix') is not None:
            expected = [float(np.float32(v)) for v in root['matrix']]
            actual = props.get('mt_root_matrix')
            if actual is None or [float(v) for v in actual] != expected:
                rec.fail('mt_root/properties/mt_root_matrix', 'value', 'readback %r, expected %r' % (actual, expected))
        expected_scale = float(root['scale']) if root.get('scale') is not None else float((manifest.get('units') or {}).get('metersPerUnit') or 1.0)
        if props.get('mt_root_scale') != expected_scale:
            rec.fail('mt_root/properties/mt_root_scale', 'value', 'readback %r, expected %r' % (props.get('mt_root_scale'), expected_scale))
        for key, member in (('mt_units', 'units'), ('mt_basis', 'basis')):
            if manifest.get(member) is not None and json_property(props.get(key)) != manifest[member]:
                rec.fail('mt_root/properties/%s' % key, 'value', 'readback %r, expected %r' % (props.get(key), manifest[member]))
        if props.get('mt_source_format') != str(manifest.get('sourceFormat') or ''):
            rec.fail('mt_root/properties/mt_source_format', 'value', 'readback %r' % props.get('mt_source_format'))
    scenes = readback.get('scenes') or []
    scene_props = (scenes[0].get('properties') or {}) if scenes else {}
    for key, expected in (('mt_package_version', int(manifest.get('packageVersion'))),
                          ('mt_animation_count', int(manifest.get('animationCount') or 0)),
                          ('mt_name', str(manifest.get('name') or ''))):
        if scene_props.get(key) != expected:
            rec.fail('scene/properties/%s' % key, 'value', 'readback %r, expected %r' % (scene_props.get(key), expected))
    for scene in scenes:
        view = scene.get('view_settings') or {}
        if view.get('view_transform') != 'Standard':
            rec.fail('scene/view_transform', 'value', 'readback %r, expected Standard' % view.get('view_transform'))
    # material variants
    keys = rules.material_variant_keys(manifest)
    seen = Counter()
    for material in readback.get('materials') or []:
        props = material.get('properties') or {}
        if 'mt_material_index' not in props:
            continue
        variant = json_property(props.get('mt_vertex_color_variant')) or {}
        seen[(int(props['mt_material_index']), variant.get('domain'), variant.get('storage'))] += 1
    expected = Counter((index, key[0], key[1]) for index, entry in enumerate(keys) for key in entry)
    if seen != expected:
        rec.fail('materials', 'variants', 'readback %s, expected %s' % (sorted(seen.items())[:12], sorted(expected.items())[:12]))
    stats['materials'] = sum(seen.values())


IDENTITY4 = [[1.0 if r == c else 0.0 for c in range(4)] for r in range(4)]
LOCATION_SCALE_RELATIVE = 1e-5  # declared bound for a scale Blender decomposes from a matrix (float32 arithmetic)


def _f32(values):
    return [float(np.float32(v)) for v in values]


def _root_object_name(readback):
    roots = [root for root in readback.get('roots') or [] if root.get('name') == 'mt_root' or
             (root.get('properties') or {}).get('mt_name') == 'mt_root']
    return roots[0]['name'] if len(roots) == 1 else None


def _node_objects(readback):
    """{node index: object} for node objects (they carry mt_local_matrix); duplicates are reported elsewhere."""
    found = {}
    for obj in readback.get('objects') or []:
        props = obj.get('properties') or {}
        if 'mt_local_matrix' in props and 'mt_node_index' in props:
            found.setdefault(int(props['mt_node_index']), []).append(obj)
    return {index: objects[0] for index, objects in found.items() if len(objects) == 1}


def _indexed_objects(readback, key):
    """{node index: object} for the objects carrying ``key`` (``mt_facing_node_index``, ``mt_pivot_node_index``);
    duplicates and absences are reported by compare_objects_and_properties."""
    found = {}
    for obj in readback.get('objects') or []:
        props = obj.get('properties') or {}
        if key in props:
            found.setdefault(int(props[key]), []).append(obj)
    return {index: objects[0] for index, objects in found.items() if len(objects) == 1}


def _children_inverse(node):
    """The matrix_parent_inverse of an object parented to a node's attach object: identity under a camera-facing node
    (its pivot empty carries the pivot and any residual), else ``_child_inverse``."""
    return IDENTITY4 if rules.camera_facing(node) else _child_inverse(node)


def _child_inverse(node):
    """The residual @ Translation(-pivot) a node's children compose with: identity, a translation by minus the billboard
    pivot, or None when a shear residual makes it depend on Blender's own decomposition (not compared). For a
    camera-facing node it is the pivot empty's own matrix_parent_inverse (``_children_inverse`` gives the children's)."""
    if node.get('trs') is None and rules.node_has_shear(node.get('localMatrix') or [0.0] * 16):
        return None
    offset = rules.billboard_offset(node)
    if offset is None:
        return IDENTITY4
    pivot = _f32(offset[0])
    return [[1.0, 0.0, 0.0, float(np.float32(-pivot[0]))], [0.0, 1.0, 0.0, float(np.float32(-pivot[1]))],
            [0.0, 0.0, 1.0, float(np.float32(-pivot[2]))], [0.0, 0.0, 0.0, 1.0]]


def _matrix_equal(actual, expected):
    return isinstance(actual, list) and len(actual) == 4 and all(
        isinstance(row, list) and len(row) == 4 and all(float(a) == float(e) for a, e in zip(row, erow))
        for row, erow in zip(actual, expected))


def compare_nodes(rec, package, readback, stats, model=None):
    """The node hierarchy, local transforms, parent inverses, object types and node mt_* properties. ``model`` (an
    animated package's ``compare_d_anim.PackageModel``) supplies what the animation changes: rotation mode XYZ on
    Euler-animated nodes, geometry split from every controller when visibility is animated, and the first clip's
    frame-0 location and scale on its OBJECT owners."""
    euler_nodes = model.euler_nodes if model is not None else set()
    visibility_split = model is not None and model.visibility_enabled
    pose = model.first_clip_pose()[0] if model is not None else {}
    manifest = package.manifest
    nodes = manifest.get('nodes') or []
    objects = _node_objects(readback)
    root_name = _root_object_name(readback)
    facing_objects = _indexed_objects(readback, 'mt_facing_node_index')
    pivot_objects = _indexed_objects(readback, 'mt_pivot_node_index')
    anchor_objects = _indexed_objects(readback, 'mt_anchor_node_index')
    helper_objects = _helper_objects(readback)
    scenes = readback.get('scenes') or []
    target_name = ((scenes[0].get('properties') or {}) if scenes else {}).get('mt_billboard_target') or 'mt_billboard_target'
    try:
        worlds = rules.document_worlds(nodes)
    except (ValueError, TypeError) as failure:
        rec.fail('nodes', 'package', 'the package node worlds cannot be composed: %s' % failure)
        return
    typed = {index: rules.typed_facing(manifest, node) for index, node in enumerate(nodes)}
    lever = _facing_lever(manifest, nodes, worlds) if any(kind is not None for kind in typed.values()) else None

    def inserted_name(index):
        """The helper _facing_insert placed innermost between a typed node's presentation child and its content, or None."""
        role = rules.typed_facing_attach(nodes[index]['billboard'], typed[index]) if typed[index] is not None else None
        controller = objects.get(index)
        if role is None or controller is None:
            return None
        return rules.clip_name(str(controller.get('name')) + rules.FACING_HELPER_SUFFIXES[role])

    def attach_name(index):
        """The object a node's children and primitives parent to: its pivot empty, else the helper a typed construction
        inserted, else its presentation child when it is camera-facing; else its node object."""
        node = nodes[index]
        if rules.camera_facing(node):
            if rules.pivot_empty(node):
                return (pivot_objects.get(index) or {}).get('name')
            return inserted_name(index) or (facing_objects.get(index) or {}).get('name')
        return (objects.get(index) or {}).get('name')

    for index, node in enumerate(nodes):
        obj = objects.get(index)
        if obj is None:
            continue  # presence is compare_objects_and_properties' check
        where = 'nodes/%d' % index
        props = obj.get('properties') or {}
        expect_property(rec, where + '/properties/mt_name', props, 'mt_name', str(node.get('name') or ('node_%04d' % index)))
        local = props.get('mt_local_matrix')
        expected_local = [float(v) for v in node.get('localMatrix') or []]
        parent = node.get('parent')
        if len(expected_local) != 16 or (parent is not None and not 0 <= int(parent) < len(nodes)):
            rec.fail(where, 'package', 'the package node has %d localMatrix values and parent %r' % (len(expected_local), parent))
            continue
        if not isinstance(local, list) or len(local) != len(expected_local) or any(float(a) != e for a, e in zip(local, expected_local)):
            rec.fail(where + '/properties/mt_local_matrix', 'value', 'readback %s, expected %s' % (_brief(local), _brief(expected_local)))
        expect_enum(rec, where + '/properties/mt_role', props, 'mt_role', node.get('role'))
        for key, member in (('mt_presentation', 'presentation'), ('mt_billboard', 'billboard'), ('mt_extras', 'extras')):
            expect_property(rec, '%s/properties/%s' % (where, key), props, key, node.get(member))
        expect_property(rec, where + '/properties/mt_morph_weights', props, 'mt_morph_weights',
                        [float(w) for w in node['morphWeights']] if node.get('morphWeights') is not None else None)
        expect_property(rec, where + '/properties/mt_mesh_index', props, 'mt_mesh_index',
                        int(node['mesh']) if node.get('mesh') is not None else None)
        expect_property(rec, where + '/properties/mt_skin_index', props, 'mt_skin_index',
                        int(node['skin']) if node.get('skin') is not None else None)
        sheared = node.get('trs') is None and rules.node_has_shear(node.get('localMatrix') or [0.0] * 16)
        if sheared != ('mt_shear_residual' in props):
            rec.fail(where + '/properties/mt_shear_residual', 'presence', 'present %s, expected %s (shear %s)'
                     % ('mt_shear_residual' in props, sheared, sheared))
        # hierarchy: the package parent's attach object, or mt_root
        expected_parent = root_name if parent is None else attach_name(int(parent))
        if expected_parent is None or obj.get('parent') != expected_parent:
            rec.fail(where + '/parent', 'value', 'readback %r, expected %r (package parent %r)' % (obj.get('parent'), expected_parent, parent))
        # object type: the primitive's mesh object only for a lone, unskinned, unsheared primitive of a node that is not
        # camera-facing (a camera-facing controller never holds geometry) and, in a version-7 package, is drawn
        # (import_model._create_node_object: a never-drawn primitive is hidden, so it never shares its controller)
        facing = rules.camera_facing(node)
        primitives = (manifest['meshes'][int(node['mesh'])].get('primitives') or []) if node.get('mesh') is not None else []
        single = (len(primitives) == 1 and node.get('skin') is None and not sheared and rules.billboard_offset(node) is None
                  and not visibility_split and not facing
                  and not (manifest.get('packageVersion') == 7 and rules.never_drawn(manifest, node, primitives[0])))
        if obj.get('type') != ('MESH' if single else 'EMPTY'):
            rec.fail(where + '/type', 'value', 'readback %r, expected %r' % (obj.get('type'), 'MESH' if single else 'EMPTY'))
        # stored transform (rotation: see the module docstring)
        rotation_mode = 'XYZ' if index in euler_nodes else 'QUATERNION'
        if obj.get('rotation_mode') != rotation_mode:
            rec.fail(where + '/rotation_mode', 'value', 'readback %r, expected %s' % (obj.get('rotation_mode'), rotation_mode))
        # The controller keeps the source transform even with an anchor: the anchor lives on the presentation child.
        trs = node.get('trs')
        if trs is not None:
            location = _f32(rules.vector(trs.get('translation'), 3, (0.0, 0.0, 0.0)))
            scale = _f32(rules.vector(trs.get('scale'), 3, (1.0, 1.0, 1.0)))
            exact_scale = True
        else:
            m = np.asarray(expected_local, dtype=np.float64).reshape(4, 4)
            location = _f32(m[3, :3])
            lengths = np.linalg.norm(m[:3, :3], axis=1)
            scale = (-lengths if np.linalg.det(m[:3, :3]) < 0 else lengths).tolist()
            exact_scale = False
            stats['nodesDecomposed'] += 1
        if any(key[0] == index for key in pose):
            # The first clip is active at frame 0 on this OBJECT owner, so the saved object holds that pose.
            location = [pose.get((index, 'location', i), location[i]) for i in range(3)]
            scale = [pose.get((index, 'scale', i), scale[i]) for i in range(3)]
            exact_scale = True
            stats['nodesHoldingFirstClipPose'] += 1
        actual_location = obj.get('location')
        if not isinstance(actual_location, list) or [float(v) for v in actual_location] != location:
            rec.fail(where + '/location', 'value', 'readback %r, expected %r' % (actual_location, location))
        actual_scale = obj.get('scale')
        if not isinstance(actual_scale, list) or len(actual_scale) != 3:
            rec.fail(where + '/scale', 'presence', 'readback %r' % (actual_scale,))
        elif exact_scale and [float(v) for v in actual_scale] != scale:
            rec.fail(where + '/scale', 'value', 'readback %r, expected %r' % (actual_scale, scale))
        elif not exact_scale and any(abs(float(a) - e) > LOCATION_SCALE_RELATIVE * max(1.0, abs(e)) for a, e in zip(actual_scale, scale)):
            rec.fail(where + '/scale', 'tolerance', 'readback %r, expected %r within %g relative' % (actual_scale, scale, LOCATION_SCALE_RELATIVE))
        expected_inverse = IDENTITY4 if parent is None else _children_inverse(nodes[int(parent)])
        if expected_inverse is None:
            stats['shearedParentInversesNotCompared'] += 1
        elif not _matrix_equal(obj.get('matrix_parent_inverse'), expected_inverse):
            rec.fail(where + '/matrix_parent_inverse', 'value', 'readback %s, expected %s' % (_brief(obj.get('matrix_parent_inverse')), expected_inverse))
        if facing:
            names = _facing_names(obj, facing_objects.get(index), anchor_objects.get(index), root_name, target_name)
            _compare_presentation(rec, index, node, obj, facing_objects.get(index), pivot_objects.get(index),
                                  anchor_objects.get(index), worlds[index], names, manifest, inserted_name(index), stats)
            if typed[index] is not None:
                _compare_typed_helpers(rec, index, node, typed[index], names, helper_objects, lever, manifest, stats)
        stats['nodesCompared'] += 1
    # primitive child objects of multi-primitive, skinned, sheared or anchored nodes
    for obj in readback.get('objects') or []:
        props = obj.get('properties') or {}
        if 'mt_primitive_index' not in props or 'mt_local_matrix' in props:
            continue
        index = int(props.get('mt_node_index', -1))
        if not 0 <= index < len(nodes):
            continue  # reported by compare_objects_and_properties
        node = nodes[index]
        where = 'objects/%s' % obj.get('name')
        skinned = node.get('skin') is not None
        expected_parent = root_name if skinned else attach_name(index)
        if expected_parent is None or obj.get('parent') != expected_parent:
            rec.fail(where + '/parent', 'value', 'readback %r, expected %r' % (obj.get('parent'), expected_parent))
        expected_inverse = IDENTITY4 if skinned else _children_inverse(node)
        if expected_inverse is None:
            stats['shearedParentInversesNotCompared'] += 1
        elif not _matrix_equal(obj.get('matrix_parent_inverse'), expected_inverse):
            rec.fail(where + '/matrix_parent_inverse', 'value', 'readback %s, expected %s' % (_brief(obj.get('matrix_parent_inverse')), expected_inverse))
        if [float(v) for v in obj.get('location') or []] != [0.0, 0.0, 0.0] or [float(v) for v in obj.get('scale') or []] != [1.0, 1.0, 1.0]:
            rec.fail(where + '/transform', 'value', 'a primitive child keeps the identity transform; readback location %r scale %r'
                     % (obj.get('location'), obj.get('scale')))
        expect_property(rec, where + '/properties/mt_name', props, 'mt_name', str(node.get('name') or ''))
        expect_property(rec, where + '/properties/mt_mesh_index', props, 'mt_mesh_index', int(node['mesh']) if node.get('mesh') is not None else None)
        stats['primitiveChildrenCompared'] += 1


def _facing_names(controller, facing, anchor, root_name, target_name):
    """Object names by the roles import_rules uses: the controller, the presentation child, the anchor empty, mt_root,
    the billboard target and every typed-facing helper (``clip_name(<controller name> + suffix)``, as _facing_helper
    names it)."""
    names = {'controller': controller.get('name'), 'presentation': (facing or {}).get('name'),
             'anchor': (anchor or {}).get('name'), 'root': root_name, 'billboard': target_name}
    for role, suffix in rules.FACING_HELPER_SUFFIXES.items():
        names[role] = rules.clip_name(str(controller.get('name')) + suffix)
    return names


def _helper_objects(readback):
    """{(node index, object name): object} for the typed-facing helpers (``mt_billboard_helper_node_index``)."""
    found = {}
    for obj in readback.get('objects') or []:
        props = obj.get('properties') or {}
        if 'mt_billboard_helper_node_index' in props:
            found[(int(props['mt_billboard_helper_node_index']), obj.get('name'))] = obj
    return found


def _facing_lever(manifest, nodes, worlds):
    """import_model._facing_lever (lines 4508-4520) in float64: 2 ** ceil(log2(extent)) meters, where extent is the
    larger of 1 and 4 x the root scale x the largest |rest document world translation + billboard anchor| over every
    node. None (the helper locations are then not compared) when a node's value lies within 1e-6 relative of a power of
    two, where the importer's Float32 worlds could round across it."""
    root = manifest.get('root') or {}
    scale = float(root['scale']) if root.get('scale') is not None else float((manifest.get('units') or {}).get('metersPerUnit') or 1.0)
    extent = 1.0
    for node, world in zip(nodes, worlds):
        anchor = np.asarray(rules.vector((node.get('billboard') or {}).get('anchor'), 3, (0.0, 0.0, 0.0)), dtype=np.float64)
        value = 4.0 * scale * float(np.linalg.norm(np.asarray(world, dtype=np.float64)[:3, 3] + anchor))
        if value >= 1.0 and abs(value - 2.0 ** round(math.log2(value))) <= 1e-6 * value:
            return None
        extent = max(extent, value)
    return 2.0 ** math.ceil(math.log2(extent))


def _compare_typed_helpers(rec, index, node, kind, names, helper_objects, lever, manifest, stats):
    """The helper empties of a typed construction (import_rules.typed_facing_helpers): presence by name, EMPTY, parent,
    identity parent inverse, rotation mode QUATERNION, stored location and scale, and exactly the stated constraints."""
    billboard = node['billboard']
    root = manifest.get('root') or {}
    root_scale = float(root['scale']) if root.get('scale') is not None else float((manifest.get('units') or {}).get('metersPerUnit') or 1.0)
    reflection_scale = rules.typed_facing_reflection_scale(billboard)
    for role, parent_role, constraints in rules.typed_facing_helpers(billboard, kind):
        where = 'nodes/%d/typedFacing/%s' % (index, role)
        helper = helper_objects.get((index, names[role]))
        if helper is None:
            rec.fail(where, 'presence', 'no helper %r carrying mt_billboard_helper_node_index %d (kind %s)' % (names[role], index, kind))
            continue
        if helper.get('type') != 'EMPTY':
            rec.fail(where + '/type', 'value', 'readback %r, expected EMPTY' % helper.get('type'))
        if helper.get('parent') != names[parent_role]:
            rec.fail(where + '/parent', 'value', 'readback %r, expected %r (%s)' % (helper.get('parent'), names[parent_role], parent_role))
        if not _matrix_equal(helper.get('matrix_parent_inverse'), IDENTITY4):
            rec.fail(where + '/matrix_parent_inverse', 'value', 'readback %s, expected identity' % _brief(helper.get('matrix_parent_inverse')))
        if helper.get('rotation_mode') != 'QUATERNION':
            rec.fail(where + '/rotation_mode', 'value', 'readback %r, expected QUATERNION' % helper.get('rotation_mode'))
        location = [0.0, 0.0, 0.0]
        if role in ('plane', 'camera_up', 'document_up'):
            if lever is None:
                location = None
                stats['typedFacingHelperLocationsNotCompared'] += 1
            elif role == 'plane':
                location = [0.0, 0.0, lever]
            elif role == 'camera_up':
                location = [0.0, lever, 0.0]
            else:
                axis = np.asarray(rules.vector(billboard['lockedAxis'], 3, (0.0, 0.0, 0.0)), dtype=np.float64)
                location = (axis / np.linalg.norm(axis) * (lever / root_scale)).tolist()
        actual = [float(v) for v in helper.get('location') or []]
        if location is not None and (len(actual) != 3 or any(abs(a - e) > 1e-6 * max(1.0, abs(e)) for a, e in zip(actual, location))):
            rec.fail(where + '/location', 'value', 'readback %r, expected %r' % (helper.get('location'), location))
        scale = [1.0, 1.0, 1.0]
        if role == 'reflection':
            scale = reflection_scale
        elif role == 'lock':
            scale = None  # driven by the determinant of the evaluated controller
        if scale is None:
            stats['typedFacingDrivenScalesNotCompared'] += 1
        elif [float(v) for v in helper.get('scale') or []] != scale:
            rec.fail(where + '/scale', 'value', 'readback %r, expected %r' % (helper.get('scale'), scale))
        actual_constraints = helper.get('constraints') or []
        if len(actual_constraints) != len(constraints):
            rec.fail(where + '/constraints', 'count', 'readback %s, expected %s' % (
                [(c.get('name'), c.get('type')) for c in actual_constraints], [(c[0], c[1]) for c in constraints]))
        else:
            for position, ((name, kind_name, target_role, settings), constraint) in enumerate(zip(constraints, actual_constraints)):
                cwhere = '%s/constraints/%d' % (where, position)
                got = (constraint.get('type'), constraint.get('target'))
                if got != (kind_name, names[target_role]) or (name is not None and constraint.get('name') != name):
                    rec.fail(cwhere, 'value', 'readback %r, expected %r' % ((constraint.get('name'),) + got,
                                                                           (name, kind_name, names[target_role])))
                for key, value in settings.items():
                    if constraint.get(key) != value:
                        rec.fail('%s/%s' % (cwhere, key), 'value', 'readback %r, expected %r' % (constraint.get(key), value))
                if target_role == 'anchor' and constraint.get('space_object') != names['root']:
                    rec.fail(cwhere + '/space_object', 'value', 'readback %r, expected mt_root %r' % (constraint.get('space_object'), names['root']))
        stats['typedFacingHelpersCompared'] += 1


def _compare_presentation(rec, index, node, controller, facing, pivot, anchor, world, names, manifest, inserted, stats):
    """A camera-facing node's presentation child (and pivot and anchor empties): parentage, identity parent inverse,
    identity location and unit scale, the mt_* identity properties, the billboard properties that moved from the
    controller, and exactly the anchor and facing constraints import_rules restates (typed in a version-6 or version-7
    package). ``inserted`` names the helper a typed construction placed above the pivot empty, or is None."""
    root_name, target_name = names['root'], names['billboard']
    controller_props = controller.get('properties') or {}
    for moved in ('mt_billboard_constraint', 'mt_billboard_raw_mode'):
        if moved in controller_props:
            rec.fail('nodes/%d/properties/%s' % (index, moved), 'presence',
                     'the controller carries %s, which belongs on the presentation child' % moved)
    if facing is None:
        return  # presence is compare_objects_and_properties' check
    where = 'nodes/%d/presentation' % index
    props = facing.get('properties') or {}
    expect_property(rec, where + '/properties/mt_name', props, 'mt_name', str(node.get('name') or ''))
    for forbidden in ('mt_node_index', 'mt_local_matrix', 'mt_primitive_index'):
        if forbidden in props:
            rec.fail(where + '/properties/' + forbidden, 'presence', 'the presentation child carries %s, which identifies the controller' % forbidden)
    if facing.get('type') != 'EMPTY':
        rec.fail(where + '/type', 'value', 'readback %r, expected EMPTY' % facing.get('type'))
    if facing.get('parent') != controller.get('name'):
        rec.fail(where + '/parent', 'value', 'readback %r, expected the controller %r' % (facing.get('parent'), controller.get('name')))
    if not _matrix_equal(facing.get('matrix_parent_inverse'), IDENTITY4):
        rec.fail(where + '/matrix_parent_inverse', 'value', 'readback %s, expected identity' % _brief(facing.get('matrix_parent_inverse')))
    if [float(v) for v in facing.get('location') or []] != [0.0, 0.0, 0.0]:
        rec.fail(where + '/location', 'value', 'readback %r, expected zero (the anchor is a constraint, never a stored offset)'
                 % (facing.get('location'),))
    if [float(v) for v in facing.get('scale') or []] != [1.0, 1.0, 1.0]:
        rec.fail(where + '/scale', 'value', 'readback %r, expected unit scale' % (facing.get('scale'),))
    raw_mode = (node.get('billboard') or {}).get('rawMode')
    expect_property(rec, where + '/properties/mt_billboard_raw_mode', props, 'mt_billboard_raw_mode',
                    int(raw_mode) if raw_mode is not None else None)
    facing_constraints = rules.billboard_constraints(node, world, manifest)
    record = props.get('mt_billboard_constraint')
    if record is None:
        rec.fail(where + '/properties/mt_billboard_constraint', 'presence', 'absent, expected the facing constraint record')
    elif facing_constraints is not None:
        try:
            listed = json.loads(record) if isinstance(record, str) else record
            listed_types = [row.get('type') for row in listed.get('constraints') or []]
        except (TypeError, ValueError, AttributeError):
            listed_types = None
        if listed_types != [kind for _name, kind, _role, _settings in facing_constraints]:
            rec.fail(where + '/properties/mt_billboard_constraint', 'value', 'lists %r, expected the facing constraints %r'
                     % (listed_types, [kind for _name, kind, _role, _settings in facing_constraints]))
    anchor_constraint = rules.anchor_constraint(node)
    expected = None if facing_constraints is None else ([anchor_constraint] if anchor_constraint else []) + facing_constraints
    actual = facing.get('constraints') or []
    if expected is None:
        stats['billboardConstraintsNotCompared'] += 1
    elif len(actual) != len(expected):
        rec.fail(where + '/constraints', 'count', 'readback %s, expected %s' % ([(a.get('name'), a.get('type')) for a in actual],
                                                                             [(e[0], e[1]) for e in expected]))
    else:
        for position, ((name, kind, role, settings), constraint) in enumerate(zip(expected, actual)):
            want_target = names[role]
            got = (constraint.get('name'), constraint.get('type'), constraint.get('target'))
            if got != (name, kind, want_target):
                rec.fail('%s/constraints/%d' % (where, position), 'value', 'readback %r, expected %r' % (got, (name, kind, want_target)))
            for key, value in settings.items():
                if constraint.get(key) != value:
                    rec.fail('%s/constraints/%d/%s' % (where, position, key), 'value', 'readback %r, expected %r' % (constraint.get(key), value))
            if role == 'anchor' and constraint.get('space_object') != root_name:
                rec.fail('%s/constraints/%d/space_object' % (where, position), 'value', 'readback %r, expected mt_root %r'
                         % (constraint.get('space_object'), root_name))
        stats['billboardConstraintsCompared'] += len(expected)
    location = rules.anchor_empty(node)
    if location is not None and anchor is not None:
        awhere = 'nodes/%d/anchor' % index
        expect_property(rec, awhere + '/properties/mt_name', anchor.get('properties') or {}, 'mt_name', str(node.get('name') or ''))
        if anchor.get('type') != 'EMPTY':
            rec.fail(awhere + '/type', 'value', 'readback %r, expected EMPTY' % anchor.get('type'))
        if anchor.get('parent') != root_name:
            rec.fail(awhere + '/parent', 'value', 'readback %r, expected mt_root %r' % (anchor.get('parent'), root_name))
        if not _matrix_equal(anchor.get('matrix_parent_inverse'), IDENTITY4):
            rec.fail(awhere + '/matrix_parent_inverse', 'value', 'readback %s, expected identity' % _brief(anchor.get('matrix_parent_inverse')))
        if [float(v) for v in anchor.get('location') or []] != location:
            rec.fail(awhere + '/location', 'value', 'readback %r, expected the document anchor %r' % (anchor.get('location'), location))
        stats['anchorObjectsCompared'] += 1
    stats['presentationObjectsCompared'] += 1
    if pivot is None:
        return  # presence is compare_objects_and_properties' check
    where = 'nodes/%d/pivot' % index
    props = pivot.get('properties') or {}
    expect_property(rec, where + '/properties/mt_name', props, 'mt_name', str(node.get('name') or ''))
    if pivot.get('type') != 'EMPTY':
        rec.fail(where + '/type', 'value', 'readback %r, expected EMPTY' % pivot.get('type'))
    expected_parent = inserted or facing.get('name')
    if pivot.get('parent') != expected_parent:
        rec.fail(where + '/parent', 'value', 'readback %r, expected %s %r' % (
            pivot.get('parent'), 'the inserted typed-facing helper' if inserted else 'the presentation child', expected_parent))
    expected_inverse = _child_inverse(node)
    if expected_inverse is None:
        stats['shearedParentInversesNotCompared'] += 1
    elif not _matrix_equal(pivot.get('matrix_parent_inverse'), expected_inverse):
        rec.fail(where + '/matrix_parent_inverse', 'value', 'readback %s, expected %s' % (_brief(pivot.get('matrix_parent_inverse')), expected_inverse))
    if [float(v) for v in pivot.get('location') or []] != [0.0, 0.0, 0.0] or [float(v) for v in pivot.get('scale') or []] != [1.0, 1.0, 1.0]:
        rec.fail(where + '/transform', 'value', 'the pivot empty keeps the identity transform; readback location %r scale %r'
                 % (pivot.get('location'), pivot.get('scale')))
    stats['pivotObjectsCompared'] += 1


def compare_display_record(rec, where, entry, record, stats):
    """An enabled affine render-state blend is drawn by its display record: ``mt_blend_graph`` keeps the package's record
    verbatim under ``display`` and names the route ``_display_blend_route`` takes (``display``, ``display-lit``,
    ``display-lit-fallback``); any other material carries no display record. A record of a NIF factor pair must also be
    the one the harness restates from set B of display_blend_constants.json (probe_builders.display_record_and_kinds),
    so a drift between the Shared writer's constants and the committed constants file fails here. A pinned or exact bound
    must agree exactly (1e-12: a pin divided by 255 on both sides); a bound the writer MEASURED at plan time (an
    untabulated equation's fit, the generic linear lit curve) within DISPLAY_GRID_TOLERANCE, since C# Math.Pow and numpy
    np.power may differ in the last ulp and a flipped near-tie among the 64 worst cells moves the refinement (review
    finding 6); every fit and curve parameter still agrees exactly, the generic lit curve's searched exponent included
    (review fixes 2: its candidates are a hundredth apart, so only a libm-level tie between two could separate the C#
    choice from the restated one). The self-check shows each of the three failures at its element."""
    import probe_builders as builders  # noqa: E402 - imported here: only this check needs the writer restatement
    display = rules.display_record(entry)
    if display is None:
        if record.get('display') is not None:
            rec.fail(where + '/properties/mt_blend_graph/display', 'value', 'readback %s on a material without an enabled '
                     'affine render-state blend' % _brief(record.get('display')))
        return
    if not _deep_equal(record.get('display'), display):
        rec.fail(where + '/properties/mt_blend_graph/display', 'value', 'readback %s, expected the package record %s'
                 % (_brief(record.get('display')), _brief(display)))
    route = rules.display_route(entry)
    if record.get('route') != route:
        rec.fail(where + '/properties/mt_blend_graph/route', 'value', 'readback %r, expected %r' % (record.get('route'), route))
    blend = (entry.get('renderState') or {}).get('blend')
    if rules.nif_pair(blend, builders.NIF_FACTORS) is not None:
        expected, kinds = builders.display_record_and_kinds(blend)
        if not _display_close(display, expected, kinds):
            rec.fail(where + '/renderState/blend/display', 'value', 'the package declares %s; set B of display_blend_constants.json gives %s'
                     % (_brief(display), _brief(expected)))
        stats['displayRecordsAgainstConstants'] += 1
    stats['displayRecordsCompared'] += 1


DISPLAY_GRID_TOLERANCE = 1e-6 / 255.0  # a writer-measured bound, C# against numpy (normalized units)


def _display_close(actual, expected, kinds=None):
    """Display records equal member for member, numbers within 1e-12, except a bound ``kinds`` names grid-measured
    (``probe_builders.display_record_and_kinds``: the record's ``bound``, or the lit curve's), held within
    DISPLAY_GRID_TOLERANCE."""
    kinds = kinds or {}
    if not (isinstance(expected, dict) and isinstance(actual, dict)) or set(actual) != set(expected):
        return _close(actual, expected, 1e-12)
    for key in expected:
        if key == 'bound':
            tolerance = DISPLAY_GRID_TOLERANCE if kinds.get('bound') == 'grid' else 1e-12
            if not _close(actual[key], expected[key], tolerance):
                return False
        elif key == 'lit' and isinstance(expected[key], dict):
            if not _display_close(actual[key], expected[key], {'bound': kinds.get('lit')}):
                return False
        elif not _display_close(actual[key], expected[key]):
            return False
    return True


def _close(actual, expected, tolerance):
    """Numbers within ``tolerance``; anything else equal."""
    if isinstance(expected, (int, float)) and isinstance(actual, (int, float)) and not isinstance(expected, bool):
        return abs(float(actual) - float(expected)) <= tolerance
    return actual == expected


def compare_properties(rec, package, readback, stats):
    """Every mt_* property the importer copies onto materials, the scene and the root."""
    manifest = package.manifest
    materials = manifest.get('materials') or []
    images = manifest.get('images') or []
    for material in readback.get('materials') or []:
        props = material.get('properties') or {}
        if 'mt_material_index' not in props:
            continue
        index = int(props['mt_material_index'])
        if not 0 <= index < len(materials):
            rec.fail('materials/%s' % material.get('name'), 'presence', 'names the absent material %d' % index)
            continue
        entry = materials[index]
        where = 'materials/%s' % material.get('name')
        state = entry.get('renderState') or {}
        expect_property(rec, where + '/properties/mt_name', props, 'mt_name', str(entry.get('name') or ('material_%04d' % index)))
        expect_enum(rec, where + '/properties/mt_lighting_model', props, 'mt_lighting_model', rules.lighting_model(entry))
        expect_property(rec, where + '/properties/mt_portable', props, 'mt_portable',
                        {member: value for member, value in entry.items() if member not in ('source', 'renderState', 'extras')})
        expect_property(rec, where + '/properties/mt_render_state', props, 'mt_render_state', entry.get('renderState'))
        expect_property(rec, where + '/properties/mt_material_source', props, 'mt_material_source', entry.get('source'))
        expect_property(rec, where + '/properties/mt_depth_bias', props, 'mt_depth_bias', state.get('depth') if rules.has_depth_bias(entry) else None)
        expect_property(rec, where + '/properties/mt_draw_order', props, 'mt_draw_order', state.get('drawOrder'))
        expect_property(rec, where + '/properties/mt_extras', props, 'mt_extras', entry.get('extras'))
        emission, transmission = rules.material_blend(entry)
        record = props.get('mt_blend_graph')
        try:
            record = json_loads(record) if isinstance(record, str) else record
        except ValueError:
            record = None
        if not isinstance(record, dict):
            rec.fail(where + '/properties/mt_blend_graph', 'presence', 'readback %s' % _brief(props.get('mt_blend_graph')))
        else:
            if _norm(record.get('alphaMode')) != _norm(entry.get('alphaMode') or 'Opaque'):
                rec.fail(where + '/properties/mt_blend_graph/alphaMode', 'value', 'readback %r, expected %r' % (record.get('alphaMode'), entry.get('alphaMode')))
            if not _deep_equal(record.get('emission'), emission) or not _deep_equal(record.get('transmission'), transmission):
                rec.fail(where + '/properties/mt_blend_graph/terms', 'value', 'readback E %s T %s, expected E %s T %s'
                         % (_brief(record.get('emission')), _brief(record.get('transmission')), _brief(emission), _brief(transmission)))
            compare_display_record(rec, where, entry, record, stats)
        method = 'BLENDED' if emission is not None else 'DITHERED'
        if material.get('surface_render_method') != method:
            rec.fail(where + '/surface_render_method', 'value', 'readback %r, expected %r' % (material.get('surface_render_method'), method))
        missing = props.get('mt_missing_images')
        if missing is not None:
            try:
                missing = json_loads(missing) if isinstance(missing, str) else missing
            except ValueError:
                missing = None
            bindings = [layer.get('binding') or {} for layer in (entry.get('source') or {}).get('layers') or []]
            bindings += [value for value in entry.values() if isinstance(value, dict) and 'image' in value]  # portable textures
            referenced = {int(binding.get('image', -1)) for binding in bindings if binding.get('image') is not None}
            allowed = {i for i in referenced if 0 <= i < len(images) and images[i].get('missing') is not None}
            if not isinstance(missing, list) or not missing or not set(int(i) for i in missing) <= allowed:
                rec.fail(where + '/properties/mt_missing_images', 'value', 'readback %s; only missing images this material binds (%s) may be listed'
                         % (_brief(props.get('mt_missing_images')), sorted(allowed)))
        stats['materialPropertiesCompared'] += 1
    scenes = readback.get('scenes') or []
    scene_props = (scenes[0].get('properties') or {}) if scenes else {}
    expect_property(rec, 'scene/properties/mt_source_format', scene_props, 'mt_source_format', str(manifest.get('sourceFormat') or ''))
    expect_property(rec, 'scene/properties/mt_source_identity', scene_props, 'mt_source_identity',
                    str(manifest['sourceIdentity']) if manifest.get('sourceIdentity') is not None else None)
    expect_property(rec, 'scene/properties/mt_default_scene', scene_props, 'mt_default_scene',
                    int(manifest['defaultScene']) if manifest.get('defaultScene') is not None else None)
    expect_property(rec, 'scene/properties/mt_scenes', scene_props, 'mt_scenes', manifest.get('scenes') or None)
    expect_property(rec, 'scene/properties/mt_samplers', scene_props, 'mt_samplers', manifest.get('samplers') or None)
    expect_property(rec, 'scene/properties/mt_missing_images', scene_props, 'mt_missing_images',
                    [{'index': i, 'image': image} for i, image in enumerate(images) if image.get('missing') is not None] or None)
    root_name = _root_object_name(readback)
    root = next((obj for obj in readback.get('objects') or [] if obj.get('name') == root_name), None)
    if root is not None:
        props = root.get('properties') or {}
        expect_property(rec, 'mt_root/properties/mt_name', props, 'mt_name', 'mt_root')
        expect_property(rec, 'mt_root/properties/mt_source_identity', props, 'mt_source_identity',
                        str(manifest['sourceIdentity']) if manifest.get('sourceIdentity') is not None else None)
        expect_property(rec, 'mt_root/properties/mt_layer_selection', props, 'mt_layer_selection', manifest.get('layerSelection'))
        expect_property(rec, 'mt_root/properties/mt_extras', props, 'mt_extras', manifest.get('extras'))
        expect_property(rec, 'mt_root/properties/mt_animation_count', props, 'mt_animation_count', int(manifest.get('animationCount') or 0))
        if any(skin.get('bindProjection') is not None for skin in manifest.get('skins') or []):
            stats['fidelityNotComparedBindProjection'] += 1  # the importer adds the measured projection error to its rows
        else:
            expect_property(rec, 'mt_root/properties/mt_fidelity', props, 'mt_fidelity', manifest.get('fidelity'))


ELEMENT_KINDS = ('Document', 'Scene', 'Node', 'Mesh', 'Primitive', 'Material', 'Image', 'Sampler', 'Skin', 'Animation', 'MorphTarget')


def compare_native_states(rec, package, readback, stats):
    """``mt_native`` on every owner the package's native-state rows target, holding exactly those rows in order."""
    manifest = package.manifest
    rows = manifest.get('nativeStates') or []
    objects = readback.get('objects') or []
    scenes = readback.get('scenes') or []
    scene_key = ('scene', scenes[0]['name']) if scenes else None
    root_key = ('object', _root_object_name(readback))
    node_objects = _node_objects(readback)
    materials, images, meshes, armatures = {}, {}, {}, {}
    for material in readback.get('materials') or []:
        index = (material.get('properties') or {}).get('mt_material_index')
        if index is not None:
            materials.setdefault(int(index), []).append(('material', material['name']))
    for image in readback.get('images') or []:
        index = (image.get('properties') or {}).get('mt_image_index')
        if index is not None:
            images[int(index)] = ('image', image['name'])
    for mesh in readback.get('meshes') or []:
        props = mesh.get('properties') or {}
        if 'mt_mesh_index' in props:
            meshes[(int(props['mt_mesh_index']), int(props.get('mt_primitive_index', 0)))] = mesh
    for obj in objects:
        props = obj.get('properties') or {}
        if obj.get('type') == 'ARMATURE' and 'mt_skin_index' in props:
            armatures[int(props['mt_skin_index'])] = ('object', obj['name'])
    expected = {}
    for number, row in enumerate(rows):
        target = row.get('target') or {}
        kind = next((k for k in ELEMENT_KINDS if _norm(k) == _norm(target.get('kind'))), None)
        index = target.get('index')
        primitive = target.get('primitiveIndex')
        owners = None
        if kind == 'Document':
            owners = [root_key]
        elif kind in ('Scene', 'Sampler', 'Animation'):
            owners = [scene_key]
        elif kind == 'Node' and index is not None and int(index) in node_objects:
            owners = [('object', node_objects[int(index)]['name'])]
        elif kind == 'Material' and index is not None:
            owners = materials.get(int(index))
        elif kind == 'Image' and index is not None:
            owners = [images.get(int(index), scene_key)]
        elif kind == 'Skin' and index is not None and int(index) in armatures:
            owners = [armatures[int(index)]]
        elif kind == 'Mesh' and index is not None:
            owners = [('mesh', mesh['name']) for (m, _p), mesh in sorted(meshes.items()) if m == int(index)] or None
        elif kind in ('Primitive', 'MorphTarget') and index is not None and primitive is not None:
            mesh = meshes.get((int(index), int(primitive)))
            if mesh is not None:
                owners = [('key', mesh['name']) if kind == 'MorphTarget' and mesh.get('shape_keys') is not None else ('mesh', mesh['name'])]
        if not owners or any(owner is None or owner[1] is None for owner in owners):
            rec.fail('nativeStates/%d' % number, 'owner', 'no Blender owner for target %s' % _brief(target))
            continue
        for owner in owners:
            expected.setdefault(owner, []).append(row)
    actual = {}
    for obj in objects:
        if 'mt_native' in (obj.get('properties') or {}):
            actual[('object', obj['name'])] = obj['properties']['mt_native']
    for scene in scenes:
        if 'mt_native' in (scene.get('properties') or {}):
            actual[('scene', scene['name'])] = scene['properties']['mt_native']
    for kind, collection in (('material', 'materials'), ('image', 'images'), ('mesh', 'meshes')):
        for item in readback.get(collection) or []:
            if 'mt_native' in (item.get('properties') or {}):
                actual[(kind, item['name'])] = item['properties']['mt_native']
    for mesh in readback.get('meshes') or []:
        keys = mesh.get('shape_keys') or {}
        if 'mt_native' in (keys.get('properties') or {}):
            actual[('key', mesh['name'])] = keys['properties']['mt_native']
    for owner in sorted(set(expected) | set(actual), key=lambda k: (k[0], str(k[1]))):
        where = 'mt_native/%s/%s' % owner
        if owner not in actual:
            rec.fail(where, 'presence', 'absent, expected %d row(s)' % len(expected[owner]))
        elif owner not in expected:
            rec.fail(where, 'presence', 'present, but no package row targets this owner')
        elif not same_property(actual[owner], expected[owner]):
            rec.fail(where, 'value', 'the stored rows differ from the %d package row(s)' % len(expected[owner]))
    stats['nativeStateRows'] = len(rows)
    stats['nativeStateOwners'] = len(expected)


def compare_absences(rec, package, readback, stats):
    """No skin, depth-bias or billboard data where the package declares none."""
    manifest = package.manifest
    nodes = manifest.get('nodes') or []
    materials = manifest.get('materials') or []
    skins = manifest.get('skins') or []
    objects = readback.get('objects') or []
    armature_objects = [obj for obj in objects if obj.get('type') == 'ARMATURE']
    indices = sorted(int((obj.get('properties') or {}).get('mt_skin_index', -1)) for obj in armature_objects)
    if indices != list(range(len(skins))):
        rec.fail('armatures', 'count', 'armature objects for skins %s, expected one per skin 0..%d' % (indices, len(skins) - 1))
    for obj in objects:
        props = obj.get('properties') or {}
        where = 'objects/%s' % obj.get('name')
        index = int(props.get('mt_node_index', -1))
        node = nodes[index] if 0 <= index < len(nodes) else {}
        is_primitive = 'mt_primitive_index' in props
        skinned_primitive = is_primitive and node.get('skin') is not None
        if obj.get('vertex_groups') and not skinned_primitive:
            rec.fail(where + '/vertex_groups', 'presence', '%d vertex group(s) on an object that draws no skinned primitive' % len(obj['vertex_groups']))
        biased = False
        winding = False
        if is_primitive and node.get('mesh') is not None:
            primitives = manifest['meshes'][int(node['mesh'])].get('primitives') or []
            position = int(props['mt_primitive_index'])
            material = primitives[position].get('material') if 0 <= position < len(primitives) else None
            biased = material is not None and rules.has_depth_bias(materials[int(material)])
            winding = 0 <= position < len(primitives) and \
                rules.reflected_face_policy(manifest, node, primitives[position]) == 'drawnWinding'
        for modifier in obj.get('modifiers') or []:
            allowed = (modifier.get('type') == 'ARMATURE' and skinned_primitive) or \
                      (modifier.get('type') == 'DISPLACE' and modifier.get('name') == 'mt_depth_bias' and biased) or \
                      (modifier.get('type') == 'NODES' and modifier.get('name') == rules.REFLECTED_FACE_MODIFIER and winding)
            if not allowed:
                declared = {'ARMATURE': 'skin', 'NODES': 'drawn-winding reflected-face rule'}.get(modifier.get('type'), 'depth bias')
                rec.fail(where + '/modifiers', 'presence', 'modifier %r (%s) where the package declares no %s'
                         % (modifier.get('name'), modifier.get('type'), declared))
        if winding and sum(1 for modifier in obj.get('modifiers') or [] if modifier.get('type') == 'NODES' and
                           modifier.get('name') == rules.REFLECTED_FACE_MODIFIER) != 1:
            rec.fail(where + '/modifiers', 'presence', 'expected one %r NODES modifier (the DrawnWinding rule)' % rules.REFLECTED_FACE_MODIFIER)
        # Only a camera-facing node's presentation child carries mt_billboard* constraints; never its controller. A
        # typed-facing helper's constraints are compared by _compare_typed_helpers.
        helper_index = int(props.get('mt_billboard_helper_node_index', -1))
        if 0 <= helper_index < len(nodes) and rules.typed_facing(manifest, nodes[helper_index]) is not None:
            continue
        facing_index = int(props.get('mt_facing_node_index', -1))
        billboard = 0 <= facing_index < len(nodes) and rules.camera_facing(nodes[facing_index])
        for constraint in obj.get('constraints') or []:
            if not billboard or not str(constraint.get('name') or '').startswith('mt_billboard'):
                rec.fail(where + '/constraints', 'presence', 'constraint %r on an object that is not the presentation child of a '
                         'camera-facing node' % constraint.get('name'))
    stats['armatures'] = len(armature_objects)


def compare_reflected_faces(rec, package, readback, stats):
    """Package version 7 (import_model._apply_reflected_faces): each primitive object's ``mt_reflected_face_policy``
    (``import_rules.reflected_face_policy``), and every 'notDrawn' object hidden in render and viewport. The modifier is
    compare_absences' check and its driver compare_d_anim's."""
    manifest = package.manifest
    nodes = manifest.get('nodes') or []
    for obj in readback.get('objects') or []:
        props = obj.get('properties') or {}
        if 'mt_primitive_index' not in props:
            continue
        index = int(props.get('mt_node_index', -1))
        if not 0 <= index < len(nodes) or nodes[index].get('mesh') is None:
            continue  # reported by compare_objects_and_properties
        primitives = manifest['meshes'][int(nodes[index]['mesh'])].get('primitives') or []
        position = int(props['mt_primitive_index'])
        if not 0 <= position < len(primitives):
            continue
        where = 'objects/%s' % obj.get('name')
        policy = rules.reflected_face_policy(manifest, nodes[index], primitives[position])
        expect_property(rec, where + '/properties/mt_reflected_face_policy', props, 'mt_reflected_face_policy', policy)
        if policy == 'notDrawn' and not (obj.get('hide_render') is True and obj.get('hide_viewport') is True):
            rec.fail(where + '/hide', 'value', 'a never-drawn primitive shows (hide_render %r, hide_viewport %r)'
                     % (obj.get('hide_render'), obj.get('hide_viewport')))
        if policy is not None:
            stats['reflectedFacePolicies'] += 1
            stats['reflectedFaceWindingModifiers'] += policy == 'drawnWinding'


def compare_import_summary(rec, package, summary):
    if summary is None:
        rec.fail('import', 'summary', 'the importer printed no multitool.blender-import/1 summary line')
        return
    manifest = package.manifest
    expected = {'primitiveMeshes': sum(len(m.get('primitives') or []) for m in manifest.get('meshes') or []),
                'nodes': len(manifest.get('nodes') or []),
                'images': sum(1 for image in manifest.get('images') or [] if image.get('path')),
                'missingImages': sum(1 for image in manifest.get('images') or [] if not image.get('path')),
                'materials': sum(len(k) for k in rules.material_variant_keys(manifest)) if manifest.get('materials') else 0,
                'animationCount': int(manifest.get('animationCount') or 0),
                'packageVersion': int(manifest.get('packageVersion')),
                'presentationObjects': sum(1 for node in manifest.get('nodes') or [] if rules.camera_facing(node)),
                'pivotObjects': sum(1 for node in manifest.get('nodes') or [] if rules.pivot_empty(node)),
                'anchorObjects': sum(1 for node in manifest.get('nodes') or [] if rules.anchor_empty(node) is not None)}
    for key, value in expected.items():
        if summary.get(key) != value:
            rec.fail('import/summary/%s' % key, 'value', 'importer reports %r, expected %r' % (summary.get(key), value))


def compare(sample_id, package_path, dump_path, readback_path, import_receipt=None, readback_receipt=None, dump_required=True):
    """Hop D for one sample: a gate1a HopResult dict whose checks carry every mismatch they saw (``data.mismatches``),
    plus ``stats`` (counts of what was compared) and ``blenderVersion``. ``dump_required=False`` (the synthetic set, which
    has no exact dump) reads the package-versus-dump check not applicable instead of failing it."""
    result = HopResult('D', sample_id)
    stats = Counter()
    stats['customNormalsMaxDegrees'] = 0.0
    run_check = result.check('launches: import_model.py and readback.py exit 0')
    rec = Recorder(run_check)
    for role, receipt in (('import', import_receipt), ('readback', readback_receipt)):
        run = (receipt or {}).get('run') or {}
        if receipt is None:
            rec.fail(role, 'presence', 'no launch receipt')
        elif run.get('outcome') != 'ok':
            rec.fail(role, 'launch', '%s (exit %s): %s' % (run.get('outcome'), run.get('exitCode'), (receipt.get('stderrTail') or '')[-600:]))
    rec.finish()
    try:
        package = Package(package_path)
    except (OracleError, OSError, ValueError) as failure:
        result.error = 'package unreadable: %s' % failure
        return result.to_dict()
    try:
        dump = Dump(dump_path) if dump_path and os.path.isfile(dump_path) else None
    except (OracleError, OSError, ValueError, KeyError) as failure:
        dump = None
        result.error = 'dump unreadable: %s' % failure
    summary_check = result.check('import summary counts')
    rec = Recorder(summary_check)
    compare_import_summary(rec, package, import_summary((import_receipt or {}).get('stdoutTail') or _read_text((import_receipt or {}).get('stdout'))))
    rec.finish()
    if not readback_path or not os.path.isfile(readback_path):
        result.check('readback dump').fail(Mismatch('readback', 'presence', 'no readback JSON at %s' % readback_path))
        package.close()
        return result.to_dict()
    readback = load_readback(readback_path)
    source_check = result.check('readback dump: schema, mode and values')
    rec = Recorder(source_check)
    if readback.get('schema') != READBACK_SCHEMA:
        rec.fail('readback/schema', 'value', 'readback %r, expected %r' % (readback.get('schema'), READBACK_SCHEMA))
    if readback.get('mode') != 'blend':
        rec.fail('readback/mode', 'value', 'readback %r, expected blend' % readback.get('mode'))
    if not readback.get('include_values'):
        rec.fail('readback/include_values', 'value', 'hop D needs --include-values (custom normals and eight-bit colors)')
    rec.finish('Blender %s' % readback.get('blender_version_string'))
    blender_version = readback.get('blender_version_string')

    agree = result.check('package and exact dump agree on what the importer writes')
    rec = Recorder(agree)
    plans = _plans(package, dump, rec)
    if dump is None and not dump_required:
        rec.finish()
        agree.skip('no exact dump: a synthetic package, compared with the package alone')
    else:
        compare_plans(rec, plans, dump)
        rec.finish()

    meshes = result.check('meshes: vertices, loops, n-gon sizes, UV maps, colors, attributes, shape keys, mt_* (exact)')
    normals = result.check('custom normals within %.1f degrees of mt_source_normal' % CUSTOM_NORMAL_DEGREES)
    rec = Recorder(meshes)
    normals_rec = Recorder(normals)
    try:
        model = compare_d_anim.package_model(package)
    except OracleError:
        model = None  # the animation check reports the unreadable animations
    morph_nodes = model.morph_value_nodes() if model is not None else set()
    morph_pose = model.first_clip_pose()[1] if model is not None else {}
    morph_sliders = model.slider_ranges() if model is not None else {}
    users = {}
    for obj in readback.get('objects') or []:
        if obj.get('data') is not None and 'mt_node_index' in (obj.get('properties') or {}):
            users.setdefault(obj['data'], set()).add(int(obj['properties']['mt_node_index']))
    readback_meshes = {}
    for mesh in readback.get('meshes') or []:
        props = mesh.get('properties') or {}
        if 'mt_mesh_index' in props and 'mt_primitive_index' in props:
            readback_meshes.setdefault((int(props['mt_mesh_index']), int(props['mt_primitive_index'])), []).append(mesh)
    materials_by_name = {material['name']: material for material in readback.get('materials') or []}
    package_materials = package.manifest.get('materials') or []
    for (m, p), (plan, _dump_plan, mesh_manifest, primitive) in plans.items():
        label = 'mesh %d prim %d' % (m, p)
        found = readback_meshes.get((m, p), [])
        owners = [sorted(users.get(mesh['name'], ())) for mesh in found]
        # An animated import copies a shared mesh once per morph-owning node (_animation_morph_owners); each copy is
        # then used by exactly one such node and is compared on its own.
        copies = (len(found) > 1 and all(len(o) == 1 and o[0] in morph_nodes for o in owners)
                  and len({o[0] for o in owners}) == len(found))
        if len(found) != 1 and not copies:
            rec.fail(label, 'presence', 'expected one mesh datablock, found %d (used by nodes %s)' % (len(found), owners))
            continue
        if copies:
            stats['animatedMeshCopies'] += len(found) - 1
        for mesh, owner in zip(found, owners):
            morph = None
            node = owner[0] if len(owner) == 1 else None
            if node in morph_nodes:
                morph = {'values': {b: v for (n, b), v in morph_pose.items() if n == node},
                         'sliders': {b: r for (n, b), r in morph_sliders.items() if n == node}}
                if not morph['values']:
                    morph['values'] = {i + 1: float(np.float32(v)) for i, v in enumerate(model.morph_defaults(node))}
            compare_mesh(rec, normals_rec, label if len(found) == 1 else '%s (node %s copy)' % (label, node), plan,
                         mesh_manifest, primitive, mesh, materials_by_name, package_materials, stats, morph)
    extra = sorted(set(readback_meshes) - set(plans))
    if extra:
        rec.fail('meshes', 'presence', 'mesh datablocks name absent primitives %s' % extra)
    rec.finish()
    normals_rec.finish()
    normals.data['maxDegrees'] = stats['customNormalsMaxDegrees']
    normals.data['cornersCompared'] = stats['customNormalsCompared']
    normals.data['zeroSourceCornersNotCompared'] = stats['customNormalsZeroSourceCorners']

    images = result.check('images: packed bytes, SHA-256, length, color space, alpha mode')
    rec = Recorder(images)
    compare_images(rec, package, readback, stats)
    rec.finish()

    objects = result.check('objects, materials and mt_* properties')
    rec = Recorder(objects)
    compare_objects_and_properties(rec, package, readback, stats)
    rec.finish()

    hierarchy = result.check('nodes: hierarchy, stored location and scale, parent inverses, object types, node mt_* properties')
    rec = Recorder(hierarchy)
    compare_nodes(rec, package, readback, stats, model)
    rec.finish()

    properties = result.check('materials, scene and root: every mt_* property copied from the package; blend record and render method')
    rec = Recorder(properties)
    compare_properties(rec, package, readback, stats)
    rec.finish()

    native = result.check('native states: mt_native rows on every owner the package targets')
    rec = Recorder(native)
    compare_native_states(rec, package, readback, stats)
    rec.finish()

    absences = result.check('no vertex group, modifier, armature or constraint the package does not declare')
    rec = Recorder(absences)
    compare_absences(rec, package, readback, stats)
    rec.finish()

    reflected = result.check('reflected faces: per-primitive policy and hidden never-drawn objects (package version 7)')
    rule = rules.reflection_rule(package.manifest)
    if rule is None:
        reflected.skip('the package declares no reflected-face rule (package version %s)' % package.manifest.get('packageVersion'))
    else:
        rec = Recorder(reflected)
        compare_reflected_faces(rec, package, readback, stats)
        rec.finish('%s: %d primitive policies, %d winding modifiers' % (rule, stats['reflectedFacePolicies'],
                                                                         stats['reflectedFaceWindingModifiers']))

    compare_d_anim.compare_animation(result, Recorder, package, readback, stats)
    package.close()
    out = result.to_dict()
    out['stats'] = dict(stats)
    out['blenderVersion'] = blender_version
    return out


def _read_text(path):
    if not path or not os.path.isfile(path):
        return None
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        return f.read()


def all_mismatches(result_dict):
    """Every recorded mismatch of a HopResult.to_dict() (each check's data.mismatches, else its first mismatch)."""
    out = []
    for check in result_dict.get('checks') or []:
        recorded = (check.get('data') or {}).get('mismatches')
        if recorded:
            out.extend(recorded)
        elif check.get('firstMismatch'):
            out.append(check['firstMismatch'])
    return out


def at_element(where, expected_where):
    """``where`` names ``expected_where`` itself or something inside it: the prefix must end at an element boundary
    ('/' or ' '), so 'shape_keys/1' never matches 'shape_keys/10'."""
    return where == expected_where or (where.startswith(expected_where) and where[len(expected_where)] in '/ ')


def mismatches_at(result_dict, expected_where):
    """Every mismatch of a HopResult.to_dict() at ``expected_where``, including those past the per-check detail cap
    (from ``mismatchWheres``; their detail then reads as not recorded)."""
    out = []
    for check in (result_dict or {}).get('checks') or []:
        data = check.get('data') or {}
        detailed = data.get('mismatches') or ([check['firstMismatch']] if check.get('firstMismatch') else [])
        wheres = data.get('mismatchWheres')
        if wheres is None:
            wheres = [m['where'] for m in detailed]
        by_where = {}
        for mismatch in detailed:
            by_where.setdefault(mismatch['where'], mismatch)
        for where in wheres:
            if at_element(where, expected_where):
                out.append(by_where.get(where) or {'where': where, 'kind': None, 'detail': '(past the recorded detail cap)'})
    return out


def control_verdict(pristine_dict, control_dict, expected_where):
    """detected True / False / None (not applicable: masked by a pristine failure at the same element).

    Detected: the control comparison reports a mismatch AT ``expected_where`` (``at_element``) and the pristine
    comparison reported none there, counting every mismatch, not only the detailed ones. A control whose comparison
    failed only elsewhere is NOT detected."""
    pristine_hits = mismatches_at(pristine_dict, expected_where)
    if pristine_hits:
        return None, 'masked: the pristine comparison already fails at %s (%s)' % (expected_where, pristine_hits[0]['detail'])
    hits = mismatches_at(control_dict, expected_where)
    if hits:
        return True, 'detected at %s: %s' % (hits[0]['where'], hits[0]['detail'])
    others = all_mismatches(control_dict)
    return False, ('NOT detected at %s; %s' % (expected_where, ('the control failed elsewhere first: %s: %s' % (others[0]['where'], others[0]['detail'])) if others else 'the control readback matched the pristine expectation'))
