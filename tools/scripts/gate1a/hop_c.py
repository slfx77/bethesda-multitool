# SPDX-License-Identifier: 0BSD
"""Hop C: document (exact JSON dump) -> Blender package.

Every vertex stream, index stream, morph and skin stream, attribute stream and packed image is compared
exactly with the dump (design section 7.2, row C). The package stores stored bits, so the comparison is
bit for bit with no conversion; the color stream is the dump's vertex colors, or the normalized samples
of the primary color attribute the primitive names (BlenderPackageGeometryMapper remarks), reproduced here
through the same integer normalization the GLB writer declares (writer_rules.read_component).
Control: one face index flipped (the second and third corner of the first triangle exchanged) in a copy
of the package.
"""

import skin_lanes
import os
import traceback

import numpy as np

import writer_rules as rules
from dump_reader import Dump
from gate1a_common import HopResult, Mismatch, OracleError, first_bit_mismatch, first_int_mismatch, sha256_bytes
from package_reader import Package, mutate_package


def expected_color_stream(primitive):
    """The package color stream: legacy colors, or the primary attribute's normalized samples (no EOTF)."""
    index = primitive.primary_color_attribute_index
    if index is None:
        return primitive.colors.astype(np.float32)
    stream = primitive.attributes[index]
    tuples = primitive.attribute_tuples(index)
    mapping = primitive.point_indices['values'] if stream['domain'] == 'Point' else np.arange(primitive.vertex_count)
    out = np.zeros((primitive.vertex_count, 4), dtype=np.float32)
    for vertex in range(primitive.vertex_count):
        values = tuples[mapping[vertex]]
        for c in range(min(3, stream['components'])):
            out[vertex, c] = rules.read_component(values[c], stream['componentType'])
        out[vertex, 3] = rules.read_component(values[3], stream['componentType']) if stream['components'] == 4 else 1.0
    return out


def compare_streams(result, package, dump):
    check = result.check('streams: position, normal, color, uv, tangent, indices, faces, points, skin, morphs, attributes')
    streams = 0
    if len(package.manifest['meshes']) != len(dump.meshes):
        check.fail(Mismatch('meshes', 'count', 'package %d, dump %d' % (len(package.manifest['meshes']), len(dump.meshes))))
        return
    for mesh_index, primitive_index, mesh, primitive in package.primitives():
        if primitive_index >= len(dump.meshes[mesh_index]['primitives']):
            check.fail(Mismatch('meshes/%d' % mesh_index, 'count', 'package has more primitives than the dump'))
            return
        prim = dump.meshes[mesh_index]['primitives'][primitive_index]
        label = 'mesh %d primitive %d (%s)' % (mesh_index, primitive_index, prim.name)
        if mesh.get('name') != dump.meshes[mesh_index]['name'] or primitive.get('name') != prim.name:
            check.fail(Mismatch(label, 'name', 'package %r/%r, dump %r/%r' % (mesh.get('name'), primitive.get('name'), dump.meshes[mesh_index]['name'], prim.name)))
            return
        pairs = [
            ('position', package.stream(primitive['position']), prim.positions),
            ('normal', package.stream(primitive['normal']), prim.normals),
            ('color', package.stream(primitive['color']), expected_color_stream(prim)),
            ('uv0', package.stream(primitive['uv'][0]), prim.uv0),
        ]
        for set_index, values in enumerate(prim.additional_uvs):
            if set_index + 1 >= len(primitive['uv']):
                check.fail(Mismatch(label + '/uv%d' % (set_index + 1), 'presence', 'absent from the package'))
                return
            pairs.append(('uv%d' % (set_index + 1), package.stream(primitive['uv'][set_index + 1]), values))
        if len(primitive['uv']) != 1 + len(prim.additional_uvs):
            check.fail(Mismatch(label + '/uv', 'count', 'package %d sets, dump %d' % (len(primitive['uv']), 1 + len(prim.additional_uvs))))
            return
        if (primitive.get('tangent') is None) != (prim.tangents is None):
            check.fail(Mismatch(label + '/tangent', 'presence', 'package %s, dump %s' % ('present' if primitive.get('tangent') else 'absent', 'present' if prim.tangents is not None else 'absent')))
            return
        if prim.tangents is not None:
            pairs.append(('tangent', package.stream(primitive['tangent']), prim.tangents))
        for name, actual, expected in pairs:
            streams += 1
            mismatch = first_bit_mismatch(actual, expected, label + '/' + name)
            if mismatch:
                check.fail(mismatch)
                return
        streams += 1
        mismatch = first_int_mismatch(package.stream(primitive['indices']).reshape(-1), prim.indices, label + '/indices')
        if mismatch:
            check.fail(mismatch)
            return
        if primitive.get('indexCount') != len(prim.indices) or primitive.get('vertexCount') != prim.vertex_count:
            check.fail(Mismatch(label, 'counts', 'manifest vertexCount/indexCount %r/%r, dump %d/%d' % (primitive.get('vertexCount'), primitive.get('indexCount'), prim.vertex_count, len(prim.indices))))
            return
        faces = primitive.get('faces')
        if (faces is None) != (prim.faces is None):
            check.fail(Mismatch(label + '/faces', 'presence', 'package %s, dump %s' % (faces is not None, prim.faces is not None)))
            return
        if faces is not None:
            for key, stream_key in (('faceSizes', 'sizes'), ('cornerIndices', 'corners')):
                streams += 1
                mismatch = first_int_mismatch(package.stream(faces[stream_key]).reshape(-1), prim.faces[key], label + '/faces.' + stream_key)
                if mismatch:
                    check.fail(mismatch)
                    return
        points = primitive.get('points')
        if (points is None) != (prim.point_indices is None):
            check.fail(Mismatch(label + '/points', 'presence', 'package %s, dump %s' % (points is not None, prim.point_indices is not None)))
            return
        if points is not None:
            streams += 1
            mismatch = first_int_mismatch(package.stream(points['indices'] if 'indices' in points else points['stream']).reshape(-1), prim.point_indices['values'], label + '/points')
            if mismatch:
                check.fail(mismatch)
                return
        influences = primitive.get('influences') or primitive.get('skin')
        if (influences is None) != (prim.influences_per_vertex == 0):
            check.fail(Mismatch(label + '/skin', 'presence', 'package %s, dump influences per vertex %d' % (influences is not None, prim.influences_per_vertex)))
            return
        if influences is not None:
            if influences.get('influencesPerVertex') != prim.influences_per_vertex:
                check.fail(Mismatch(label + '/skin', 'stride', 'package %r, dump %d' % (influences.get('influencesPerVertex'), prim.influences_per_vertex)))
                return
            streams += 2
            # The Blender writer lowers console signed engine lanes and welds seam copies (skin_lanes restates the rule).
            points_for_lanes = prim.point_indices['values'] if prim.point_indices is not None else None
            expected_joints, expected_weights, signed, welded = skin_lanes.written_lanes(
                prim.joint_indices, prim.weights, prim.influences_per_vertex, points_for_lanes)
            check.data['skinLanesSigned'] = check.data.get('skinLanesSigned', 0) + signed
            check.data['skinLanesWelded'] = check.data.get('skinLanesWelded', 0) + welded
            mismatch = first_int_mismatch(package.stream(influences['joints']).reshape(-1), expected_joints, label + '/skin.joints')
            if mismatch:
                check.fail(mismatch)
                return
            mismatch = first_bit_mismatch(package.stream(influences['weights']).reshape(-1), expected_weights, label + '/skin.weights')
            if mismatch:
                check.fail(mismatch)
                return
        morphs = primitive.get('morphs') or []
        if len(morphs) != len(prim.morph_targets):
            check.fail(Mismatch(label + '/morphs', 'count', 'package %d, dump %d' % (len(morphs), len(prim.morph_targets))))
            return
        for ti, (morph, target) in enumerate(zip(morphs, prim.morph_targets)):
            tlabel = '%s/morph %d' % (label, ti)
            if morph.get('name') != target['name']:
                check.fail(Mismatch(tlabel, 'name', 'package %r, dump %r' % (morph.get('name'), target['name'])))
                return
            # The morph row's stream keys are the manifest record's camelCased property names
            # (BlenderPackageMorphManifest.cs lines 6-10, BlenderPackageJsonContext.cs line 11): form "relative" carries
            # positionDeltas and no absolutePositions, form "absolute" the reverse, and normalDeltas / tangentDeltas are
            # optional on either form (BlenderPackageGeometryMapper.cs lines 142-173). The first cut of this check read
            # "deltas" / "positions" / "normals" / "tangents", keys the writer never emits, so every relative morph
            # failed as a form mismatch while the two forms agreed and the arrays were never compared.
            expected_form = 'absolute' if target['absolutePositions'] is not None else 'relative'
            stream_key = 'absolutePositions' if expected_form == 'absolute' else 'positionDeltas'
            other_key = 'positionDeltas' if expected_form == 'absolute' else 'absolutePositions'
            if morph.get('form') != expected_form or morph.get(stream_key) is None or morph.get(other_key) is not None:
                check.fail(Mismatch(tlabel, 'form', 'package form %r with %s, dump form %r' % (
                    morph.get('form'), ', '.join(k for k in ('positionDeltas', 'absolutePositions') if morph.get(k) is not None) or 'no position stream',
                    expected_form)))
                return
            streams += 1
            mismatch = first_bit_mismatch(package.stream(morph[stream_key]), target[stream_key], tlabel + '/' + stream_key)
            if mismatch:
                check.fail(mismatch)
                return
            for key in ('normalDeltas', 'tangentDeltas'):
                if (target[key] is None) != (morph.get(key) is None):
                    check.fail(Mismatch(tlabel + '/' + key, 'presence', 'package %s, dump %s' % (morph.get(key) is not None, target[key] is not None)))
                    return
                if target[key] is not None:
                    streams += 1
                    mismatch = first_bit_mismatch(package.stream(morph[key]), target[key], tlabel + '/' + key)
                    if mismatch:
                        check.fail(mismatch)
                        return
        attributes = primitive.get('attributes') or []
        if len(attributes) != len(prim.attributes):
            check.fail(Mismatch(label + '/attributes', 'count', 'package %d, dump %d' % (len(attributes), len(prim.attributes))))
            return
        for ai, (attribute, stream) in enumerate(zip(attributes, prim.attributes)):
            alabel = '%s/attr_%04d' % (label, ai)
            manifest = attribute.get('stream') or attribute
            data = package.entry(manifest['path'])
            streams += 1
            if data != stream['bytes']:
                first = next((i for i in range(min(len(data), len(stream['bytes']))) if data[i] != stream['bytes'][i]), min(len(data), len(stream['bytes'])))
                check.fail(Mismatch(alabel, 'bytes', 'first differing byte at %d (package %d bytes, dump %d bytes)' % (first, len(data), len(stream['bytes']))))
                return
            for key in ('name', 'semantic', 'domain', 'componentType', 'components', 'count', 'byteStride', 'normalized', 'colorSpace'):
                if attribute.get(key) != stream.get(key):
                    check.fail(Mismatch(alabel + '/' + key, 'declaration', 'package %r, dump %r' % (attribute.get(key), stream.get(key))))
                    return
        if primitive.get('primaryColorAttribute') != prim.primary_color_attribute_index and primitive.get('primaryColorAttributeIndex') != prim.primary_color_attribute_index:
            check.fail(Mismatch(label, 'primaryColorAttribute', 'package %r, dump %r' % (primitive.get('primaryColorAttribute', primitive.get('primaryColorAttributeIndex')), prim.primary_color_attribute_index)))
            return
    check.data['streamsCompared'] = streams
    check.ok('%d streams bit-exact' % streams)


def compare_images(result, package, dump):
    check = result.check('images: packed bytes equal the dump payload named by the packing decision')
    rows = package.manifest.get('images') or []
    if len(rows) != len(dump.images):
        check.fail(Mismatch('images', 'count', 'package %d, dump %d' % (len(rows), len(dump.images))))
        return
    compared = 0
    details = []
    for index, (row, image) in enumerate(zip(rows, dump.images)):
        packing = row.get('packing')
        if packing == 'Missing':
            if image.original is not None:
                check.fail(Mismatch('images/%d' % index, 'packing', 'packed as Missing but the dump holds original bytes'))
                return
            details.append({'image': index, 'packing': packing})
            continue
        expected = {'Original': image.original, 'StandardPayload': image.standard, 'Derivation': image.derivation_payload,
                    'LegacyPng': {'bytes': image.legacy_content, 'sha256': sha256_bytes(image.legacy_content), 'container': 'png'} if image.source is None else None}.get(packing)
        if expected is None:
            check.fail(Mismatch('images/%d' % index, 'packing', 'packing %r names a payload the dump image does not carry' % packing))
            return
        data = package.image_payload(row)
        compared += 1
        if data != expected['bytes']:
            check.fail(Mismatch('images/%d' % index, 'bytes', 'package %d bytes (sha %s), dump %s %d bytes (sha %s)' % (len(data), sha256_bytes(data)[:16], packing, len(expected['bytes']), expected['sha256'][:16])))
            return
        if row.get('sha256') != expected['sha256']:
            check.fail(Mismatch('images/%d' % index, 'sha256', 'manifest %r, dump %r' % (row.get('sha256'), expected['sha256'])))
            return
        details.append({'image': index, 'packing': packing, 'sha256': expected['sha256'], 'bytes': len(data), 'name': image.name})
    check.data['images'] = details
    check.ok('%d packed images byte-identical to the dump' % compared)


def compare_declarations(result, package, dump):
    check = result.check('manifest: units, basis, root, nodes, materials')
    manifest = package.manifest
    units = manifest.get('units') or {}
    dunits = dump.units or {}
    if units.get('declared') != (dump.units is not None) or (dump.units is not None and units.get('metersPerUnit') != dunits.get('metersPerUnit')):
        check.fail(Mismatch('units', 'value', 'package %r, dump %r' % (units, dunits)))
        return
    basis = manifest.get('basis') or {}
    dbasis = dump.basis or {}
    for key in ('up', 'forward', 'right', 'handedness', 'normalizedByReader'):
        if dump.basis is not None and basis.get(key) != dbasis.get(key):
            check.fail(Mismatch('basis/' + key, 'value', 'package %r, dump %r' % (basis.get(key), dbasis.get(key))))
            return
    root = manifest.get('root') or {}
    expected_scale = (dunits.get('metersPerUnit') or 1.0) * (root.get('optionScale') or 1.0)
    if root.get('scale') != expected_scale:
        check.fail(Mismatch('root/scale', 'value', 'package %r, expected %r' % (root.get('scale'), expected_scale)))
        return
    nodes = manifest.get('nodes') or []
    if len(nodes) != len(dump.nodes):
        check.fail(Mismatch('nodes', 'count', 'package %d, dump %d' % (len(nodes), len(dump.nodes))))
        return
    for index, (pnode, dnode) in enumerate(zip(nodes, dump.nodes)):
        if pnode.get('name') != dnode.get('name') or list(pnode.get('children', [])) != list(dnode.get('children', [])) \
                or pnode.get('mesh') != dnode.get('meshIndex') or pnode.get('skin') != dnode.get('skinIndex'):
            check.fail(Mismatch('nodes/%d' % index, 'value', 'package %r, dump %r' % ({k: pnode.get(k) for k in ('name', 'children', 'mesh', 'skin')}, {k: dnode.get(k) for k in ('name', 'children', 'meshIndex', 'skinIndex')})))
            return
        if not np.array_equal(np.asarray(pnode.get('localMatrix'), dtype=np.float32), np.asarray(dnode.get('localTransform'), dtype=np.float32)):
            check.fail(Mismatch('nodes/%d/localMatrix' % index, 'value', 'package %r, dump %r' % (pnode.get('localMatrix'), dnode.get('localTransform'))))
            return
    materials = manifest.get('materials') or []
    if len(materials) != len(dump.materials):
        check.fail(Mismatch('materials', 'count', 'package %d, dump %d' % (len(materials), len(dump.materials))))
        return
    for index, (pmat, dmat) in enumerate(zip(materials, dump.materials)):
        for key in ('name', 'alphaMode', 'doubleSided', 'unlit'):
            if pmat.get(key) != dmat.get(key):
                check.fail(Mismatch('materials/%d/%s' % (index, key), 'value', 'package %r, dump %r' % (pmat.get(key), dmat.get(key))))
                return
        pstate = pmat.get('renderState') or {}
        dstate = dmat.get('renderState') or {}
        if pstate.get('frontFace') != dstate.get('frontFace') or (pstate.get('alphaTest') or {}).get('rawReference') != (dstate.get('alphaTest') or {}).get('rawReference'):
            check.fail(Mismatch('materials/%d/renderState' % index, 'value', 'package frontFace %r / raw %r, dump %r / %r' % (pstate.get('frontFace'), (pstate.get('alphaTest') or {}).get('rawReference'), dstate.get('frontFace'), (dstate.get('alphaTest') or {}).get('rawReference'))))
            return
    check.ok('%d nodes, %d materials, units %r' % (len(nodes), len(materials), dunits.get('metersPerUnit')))


def flip_first_face(source_path, target_path):
    """A copy of the package whose first triangle (of the first primitive that has one) has its second and third
    corners exchanged. Returns the mutated stream path, or None when no primitive carries a triangle: a model with no
    drawable geometry packages an empty mesh list, and the control is then not applicable rather than an exception."""
    package = Package(source_path)
    path = None
    for _, _, _, primitive in package.primitives():
        indices = primitive.get('indices')
        if indices and indices.get('count', 0) * indices.get('components', 1) >= 3:
            path = indices['path']
            break
    package.close()
    if path is None:
        return None

    def mutate(name, data):
        if name != path:
            return data
        values = np.frombuffer(data, dtype='<i4').copy()
        values[1], values[2] = values[2], values[1]
        return values.tobytes()

    mutate_package(source_path, target_path, mutate)
    return path


def run(sample, work_dir):
    result = HopResult('C', sample['id'])
    try:
        if not sample.get('package') or not sample.get('dump'):
            result.check('package versus dump').skip('package or dump not supplied')
            return result
        package = Package(sample['package'])
        dump = Dump(sample['dump'])
        result.check('package version').ok('packageVersion %d, %d entries, all stored%s' % (
            package.version, len(package.entry_names),
            '; %d animation clips not compared by the static hops (gate 1b)' % package.animation_count
            if package.animation_count else ''))
        compare_declarations(result, package, dump)
        compare_streams(result, package, dump)
        compare_images(result, package, dump)
        package.close()
        os.makedirs(work_dir, exist_ok=True)
        mutated_path = os.path.join(work_dir, 'control-face-flipped.zip')
        path = flip_first_face(sample['package'], mutated_path)
        control = {'name': 'one face index flipped in a copy of the package', 'detected': None, 'detail': None, 'firstMismatch': None}
        if path is None:
            control['detail'] = 'not applicable: no primitive in the package carries a triangle'
        else:
            probe = HopResult('C-control', sample['id'])
            mutated = Package(mutated_path)
            compare_streams(probe, mutated, dump)
            mutated.close()
            failed = probe.checks[-1].passed is False
            control['detected'] = failed
            control['detail'] = 'corners 2 and 3 of triangle 0 exchanged in %s: %s' % (path, 'mismatch reported' if failed else 'NOT detected')
            control['firstMismatch'] = probe.checks[-1].mismatches[0].to_dict() if probe.checks[-1].mismatches else None
        result.controls.append(control)
    except OracleError as failure:
        result.error = str(failure)
    except Exception as failure:  # noqa: BLE001 - an oracle defect must reach the receipt, not abort the run
        result.error = 'oracle exception %s: %s' % (type(failure).__name__, failure)
        result.traceback = traceback.format_exc()
    return result
