# SPDX-License-Identifier: 0BSD
"""The hand-built hop-B artifact set: a dump, the GLB the pinned writer would emit for it, and its fidelity report.

One implementation, shared by ``selfcheck_hop_b_paths.py`` (which exercises hop B's zero-normal, tangent, fidelity-row
and control paths on variants of this set) and by ``hop_b.synthetic_control_exercise`` (which the gate driver calls
for a control no corpus sample reaches, so the control is still exercised on the same mutation and verdict the
corpus run uses). Nothing here imports or executes writer code; the GLB is assembled from the writer's declared
conversions as ``writer_rules`` re-derives them.

The dump has one mesh with two primitives and one material:
  * ``prim``: 5 vertices, one triangle (0, 1, 2); vertex 1 has a non-unit normal (normalized by the writer), vertex 2
    a zero normal that the triangle references (reconstructed: unit length is checked), vertex 3 a zero normal nothing
    references (the (0, 1, 0) placeholder); tangent lane 1 is (3, 4, 0, 1) (normalized); tangent lanes 3 and 4 are
    zero directions nothing references (pinned Shared 591d083 fills them with (1, 0, 0, w): lane 3 (0, 0, 0, -1) keeps
    -1, lane 4 (-0, 0, -0, +1) keeps +1 and loses its negative zeros), so the writer emits the filled row, the
    placeholder row and the normalization row under geometry/tangent-normalization; vertex 4 sits on vertex 1's
    position with its own UV (an unused coincident slot, never welded or given vertex 1's tangent); three morph
    targets: t0 with tangent deltas, t1 with normal deltas only, t2 with tangent deltas.
  * ``plain``: 3 vertices, unit normals and unit tangents (nothing changes), one target with tangent deltas.
  * material ``mask``: alpha test Greater with raw threshold 128, blend off, back-face culling, bound to no primitive;
    the GLB carries MASK with the cutoff (128 + 0.5) / 255 in float32 and the report the material/alpha row, so the
    MASK control has a set to run on when no corpus sample reaches it.

``write_pristine_set`` writes ``dump.json``, ``model.glb`` and ``fidelity.json`` into a directory and returns their
paths, SHA-256s and the derived values (the expected tangents, the tangent direction error, the fidelity rows).
``build_package`` writes the Blender package (v3) of the same primitives, the source lanes the package writer keeps, so
``selfcheck_tangents.py`` can run hop E's tangent rule (the package's own index stream deciding which zero lane is
referenced) against a GLB of this set.
"""

import json
import os
import zipfile

import numpy as np

import writer_rules as rules
from dump_reader import Dump
from gate1a_common import sha256_bytes, sha256_file
from hop_b import rebuild_glb

A = {
    'positions': [[0, 0, 0], [1, 0, 0], [0, 1, 0], [5, 5, 5], [1, 0, 0]],
    'normals': [[0, 0, 1], [0, 0, 2], [0, 0, 0], [-0.0, 0, 0], [0, 0, 1]],
    'tangents': [[1, 0, 0, 1], [3, 4, 0, 1], [0.6, 0.8, 0, -1], [0, 0, 0, -1], [-0.0, 0, -0.0, 1]],
    'uvs': [[0, 0], [1, 0], [0, 1], [0.5, 0.5], [8, 9]],
    'indices': [0, 1, 2],
    'targets': [('t0', True, False), ('t1', False, True), ('t2', True, False)],  # (name, tangent deltas, normal deltas)
}
B = {
    'positions': [[0, 0, 1], [1, 0, 1], [0, 1, 1]],
    'normals': [[0, 0, 1], [0, 0, 1], [0, 0, 1]],
    'tangents': [[1, 0, 0, 1], [1, 0, 0, 1], [1, 0, 0, 1]],
    'uvs': [[0, 0], [1, 0], [0, 1]],
    'indices': [0, 1, 2],
    'targets': [('u0', True, False)],
}
PRIMS = [('prim', A), ('plain', B)]

# The NORMAL lanes the writer stores for ``prim``: lane 1 normalized, lane 2 "reconstructed" (any unit direction; the
# oracle checks unit length only), lane 3 the UnitY placeholder for the unreferenced zero lane, lane 4 unchanged.
# ``plain`` is unchanged.
A_WRITTEN_NORMALS = [[0, 0, 1], [0, 0, 1], [0.6, 0.8, 0], [0, 1, 0], [0, 0, 1]]
B_WRITTEN_NORMALS = B['normals']

# The MASK material (ModelGltfMaterials.Alpha lines 240-245: blend off, alpha test Greater, raw <= 254, reference ==
# raw / 255, cutoff (float)((raw + .5d) / 255d)), written here from the numbers, not through writer_rules.
MASK_RAW = 128
DUMP_MATERIALS = [{'name': 'mask', 'baseColor': [1, 1, 1, 1], 'doubleSided': False,
                   'renderState': {'blend': {'enabled': False}, 'cull': 'Back',
                                   'alphaTest': {'enabled': True, 'compare': 'Greater', 'rawReference': MASK_RAW, 'reference': MASK_RAW / 255.0}}}]
GLB_MATERIALS = [{'name': 'mask', 'alphaMode': 'MASK', 'alphaCutoff': float(np.float32((MASK_RAW + 0.5) / 255.0)), 'doubleSided': False,
                  'pbrMetallicRoughness': {'baseColorFactor': [1, 1, 1, 1]}}]

PRIM_LABEL = 'meshes/0/primitives/0 (prim)'
PLAIN_LABEL = 'meshes/0/primitives/1 (plain)'


def dump_document(prims=None, materials=None):
    """The exact-dump document (schema 1) of the two primitives (``prims``, default ``PRIMS``) and the materials
    (default ``DUMP_MATERIALS``)."""
    primitives = []
    for name, spec in prims or PRIMS:
        n = len(spec['positions'])
        targets = []
        for tname, tangent_deltas, normal_deltas in spec['targets']:
            targets.append({'name': tname, 'positionDeltas': [[0, 0, 0.5]] * n,
                            'normalDeltas': [[0, 0, 0]] * n if normal_deltas else None,
                            'tangentDeltas': [[0, 0, 0]] * n if tangent_deltas else None})
        primitives.append({
            'name': name,
            'vertices': [{'position': p, 'normal': nn, 'color': [1, 1, 1, 1], 'texCoord': u}
                         for p, nn, u in zip(spec['positions'], spec['normals'], spec['uvs'])],
            'indices': spec['indices'], 'materialIndex': None, 'colorEncoding': 'FloatingPoint', 'normalMode': 'Smooth',
            'purpose': 'Render', 'tangents': {'values': spec['tangents']}, 'morphTargets': targets})
    return {'schemaVersion': 1, 'document': {
        'name': 'synthetic', 'units': None, 'sourceBasis': None, 'nodes': [], 'scenes': [],
        'materials': DUMP_MATERIALS if materials is None else materials, 'skins': [],
        'meshes': [{'name': 'mesh', 'primitives': primitives}], 'images': []}}


CTYPE = {np.float32: 5126, np.uint16: 5123}


def build_glb(tangent_values, normal_values, rows, prims=None, materials=None):
    """A GLB with the two primitives (``prims``, default ``PRIMS``) and the materials (default ``GLB_MATERIALS``);
    ``tangent_values`` and ``normal_values`` are per-primitive lists; ``rows`` fill the ``multitoolFidelity`` carrier."""
    accessors, views = [], []
    bin_chunk = b''

    def acc(arr, dtype, typ):
        nonlocal bin_chunk
        a = np.ascontiguousarray(np.asarray(arr, dtype=dtype))
        data = a.tobytes()
        bin_chunk += b'\0' * ((4 - len(bin_chunk) % 4) % 4)
        views.append({'buffer': 0, 'byteOffset': len(bin_chunk), 'byteLength': len(data)})
        bin_chunk += data
        accessors.append({'bufferView': len(views) - 1, 'componentType': CTYPE[dtype], 'count': int(a.shape[0]), 'type': typ})
        return len(accessors) - 1

    primitives = []
    for index, (name, spec) in enumerate(prims or PRIMS):
        n = len(spec['positions'])
        attributes = {'POSITION': acc(spec['positions'], np.float32, 'VEC3'), 'NORMAL': acc(normal_values[index], np.float32, 'VEC3'),
                      'COLOR_0': acc([[1, 1, 1, 1]] * n, np.float32, 'VEC4'), 'TEXCOORD_0': acc(spec['uvs'], np.float32, 'VEC2'),
                      'TANGENT': acc(tangent_values[index], np.float32, 'VEC4')}
        targets = []
        for _, tangent_deltas, normal_deltas in spec['targets']:
            target = {'POSITION': acc([[0, 0, 0.5]] * n, np.float32, 'VEC3')}
            if normal_deltas:
                target['NORMAL'] = acc([[0, 0, 0]] * n, np.float32, 'VEC3')
            if tangent_deltas:
                target['TANGENT'] = acc([[0, 0, 0]] * n, np.float32, 'VEC3')
            targets.append(target)
        primitives.append({'attributes': attributes, 'indices': acc(spec['indices'], np.uint16, 'SCALAR'), 'targets': targets,
                           'extras': {'multitoolPrimitiveName': name}})
    doc = {'asset': {'version': '2.0'}, 'buffers': [{'byteLength': len(bin_chunk) + (4 - len(bin_chunk) % 4) % 4}],
           'bufferViews': views, 'accessors': accessors, 'meshes': [{'name': 'mesh', 'primitives': primitives}],
           'materials': GLB_MATERIALS if materials is None else materials,
           'nodes': [], 'scenes': [{'nodes': []}], 'scene': 0, 'extras': {'multitoolFidelity': {'rows': rows}}}
    return rebuild_glb(doc, bin_chunk)


def package_stream(entries, path, values, dtype, component_type, components):
    """Append one tightly packed little-endian stream entry to ``entries`` and return its manifest row."""
    array = np.ascontiguousarray(np.asarray(values, dtype=dtype).reshape(-1, components))
    data = array.tobytes()
    entries.append((path, data))
    return {'path': path, 'componentType': component_type, 'components': components, 'count': int(array.shape[0]),
            'byteStride': array.dtype.itemsize * components, 'byteLength': len(data)}


def build_package(target_path, prims=None, material_names=('mask',)):
    """The Blender package (v3) of the primitives (``prims``, default ``PRIMS``): ``manifest.json`` first, then per
    primitive the position, normal, uv0, tangent and indices streams (Float32 tuples and Int32 indices, little-endian,
    the layout ``package_reader`` documents) holding the SOURCE values, which is what the package writer keeps, every
    entry stored uncompressed. The mesh and primitive names equal ``build_glb``'s (``mesh``, ``prim``, ``plain``) and
    the materials carry the GLB's names with a counter-clockwise front face, so ``hop_e.GlbPackagePairing`` pairs the
    two and no index reversal applies. Returns ``target_path``."""
    entries, primitives = [], []
    for index, (name, spec) in enumerate(prims or PRIMS):
        prefix = 'streams/mesh_0000_prim_%04d.' % index
        primitives.append({
            'name': name, 'material': None, 'purpose': 'Render', 'normalMode': 'Smooth',
            'position': package_stream(entries, prefix + 'position.bin', spec['positions'], '<f4', 'Float32', 3),
            'normal': package_stream(entries, prefix + 'normal.bin', spec['normals'], '<f4', 'Float32', 3),
            'uv': [package_stream(entries, prefix + 'uv0.bin', spec['uvs'], '<f4', 'Float32', 2)],
            'tangent': package_stream(entries, prefix + 'tangent.bin', spec['tangents'], '<f4', 'Float32', 4),
            'indices': package_stream(entries, prefix + 'indices.bin', spec['indices'], '<i4', 'Int32', 1)})
    manifest = {'packageVersion': 3, 'name': 'synthetic', 'meshes': [{'name': 'mesh', 'primitives': primitives}],
                'materials': [{'name': material, 'renderState': {'frontFace': 'CounterClockwise'}} for material in material_names],
                'images': []}
    with zipfile.ZipFile(target_path, 'w', compression=zipfile.ZIP_STORED) as package:
        package.writestr('manifest.json', json.dumps(manifest))
        for path, data in entries:
            package.writestr(path, data)
    return target_path


def row(kind, mi, pi, ti, feature, outcome, code, description, bounds=None):
    """One fidelity row in the shape ``mesh fidelity --format glb --json`` emits."""
    return {'target': {'kind': kind, 'index': mi, 'primitiveIndex': pi, 'morphTargetIndex': ti}, 'featureId': feature,
            'outcome': outcome, 'reasonCode': code, 'description': description, 'bounds': bounds, 'isPending': False}


# The texts of ModelGltfGeometry lines 308 and 315 (pinned Shared 591d083) with prim's count of 2 filled in by hand.
FILLED_TANGENTS_2 = ('Filled 2 unused zero-tangent slots with UnitX solely for strict GLB validation, retaining authored handedness. '
                     'Finite nonzero directions outside unit-length tolerance are normalized separately. Zero lanes have no source '
                     'direction, so no overall angular error bound is claimed.')
PLACEHOLDER_TANGENTS_2 = ('Exactly 2 zero-tangent vertex slot(s) have no index references and receive (1, 0, 0) with their authored '
                          '+/-1 handedness only for strict unit-tangent storage. They do not participate in drawn triangles. Vertex count, '
                          'order, positions, indices and side channels are retained; no coincident vertex supplies an inferred tangent.')


def fidelity_rows(tangent_degrees):
    """The rows the writer would emit for the set: ``tangent_degrees`` is the direction error of prim's normalized lane.
    Row order (indexed by ``selfcheck_hop_b_paths``): 0 prim's normalization row (under geometry/tangent-normalization,
    since prim also has placeholders), 1-2 prim's normals rows, 3-4 the morph-tangent rows, 5-6 plain's rows, 7 prim's
    filled row, 8 prim's tangent placeholder row, 9 the material's alpha row."""
    return [
        row('Primitive', 0, 0, None, 'geometry/tangent-normalization', 'Approximated', 'geometry.tangents-normalized', 'x',
            {'domain': 'tangent direction', 'unit': 'degrees', 'maximumError': 0.1, 'observedError': tangent_degrees, 'isWithinLimit': True}),
        row('Primitive', 0, 0, None, 'geometry/normals', 'Degraded', 'geometry.zero-normals-reconstructed',
            'Reconstructed 1 zero normal(s) from area-weighted incident triangles in output winding, without welding seams. '
            'Filled 1 unused zero-normal slots with UnitY solely for strict GLB validation.'),
        row('Primitive', 0, 0, None, 'geometry/unreferenced-normal-slots', 'Degraded', 'geometry.unreferenced-normal-placeholders',
            'Exactly 1 zero-normal vertex slot(s) have no index references and receive (0, 1, 0) only for strict unit-normal storage.'),
        row('MorphTarget', 0, 0, 0, 'geometry/morph-tangent-deformation', 'Degraded', 'geometry.normalized-base-tangent-morph-unbounded', 'x'),
        row('MorphTarget', 0, 0, 2, 'geometry/morph-tangent-deformation', 'Degraded', 'geometry.normalized-base-tangent-morph-unbounded', 'x'),
        row('Primitive', 0, 1, None, 'geometry/tangents', 'Exact', 'geometry.tangents-preserved', 'x'),
        row('Primitive', 0, 1, None, 'geometry/normals', 'Exact', 'geometry.normals-preserved', 'x'),
        row('Primitive', 0, 0, None, 'geometry/tangents', 'Degraded', 'geometry.unreferenced-tangents-filled', FILLED_TANGENTS_2),
        row('Primitive', 0, 0, None, 'geometry/unreferenced-tangent-slots', 'Degraded', 'geometry.unreferenced-tangent-placeholders', PLACEHOLDER_TANGENTS_2),
        row('Material', 0, None, None, 'material/alpha', 'Converted', 'material.alpha-eight-bit-greater',
            'MASK uses (T+0.5)/255 for the declared raw eight-bit threshold T.'),
    ]


def fidelity_report(rows):
    """The ``mesh fidelity --format glb --json`` document carrying ``rows``."""
    return {'writers': [{'format': 'glb', 'fidelity': {'rows': rows}}]}


def write_json(path, document):
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(document, f)
    return path


def write_pristine_set(work_dir):
    """Write ``dump.json``, ``model.glb`` (the GLB the writer would emit) and ``fidelity.json`` into ``work_dir``.

    Returns a dict: the three paths, ``glbBytes``, ``dump`` (the parsed Dump), ``expectedTangents`` (per primitive,
    from ``writer_rules.normalize_tangents``), ``tangentDegrees``, ``writtenNormals``, ``rows`` and ``sha256`` of the
    three files. The expected tangents are ``writer_rules.normalize_tangents`` of the dump lanes and indices;
    ``selfcheck_hop_b_paths`` pins the lanes that change against hand-worked bits."""
    os.makedirs(work_dir, exist_ok=True)
    dump_path = write_json(os.path.join(work_dir, 'dump.json'), dump_document())
    dump = Dump(dump_path)
    prim_a, prim_b = dump.meshes[0]['primitives']
    expected_ta, tangent_degrees, _, _ = rules.normalize_tangents(prim_a.tangents, prim_a.indices)
    expected_tb, _, _, _ = rules.normalize_tangents(prim_b.tangents, prim_b.indices)
    rows = fidelity_rows(tangent_degrees)
    written_normals = [A_WRITTEN_NORMALS, B_WRITTEN_NORMALS]
    glb_bytes = build_glb([expected_ta, expected_tb], written_normals, rows)
    glb_path = os.path.join(work_dir, 'model.glb')
    with open(glb_path, 'wb') as f:
        f.write(glb_bytes)
    fidelity_path = write_json(os.path.join(work_dir, 'fidelity.json'), fidelity_report(rows))
    return {
        'workDir': work_dir, 'dump': dump_path, 'glb': glb_path, 'fidelity': fidelity_path,
        'glbBytes': glb_bytes, 'dumpObject': dump, 'expectedTangents': [expected_ta, expected_tb],
        'tangentDegrees': tangent_degrees, 'writtenNormals': written_normals, 'rows': rows,
        'sha256': {'glb': sha256_bytes(glb_bytes), 'dump': sha256_file(dump_path), 'fidelity': sha256_file(fidelity_path)},
    }
