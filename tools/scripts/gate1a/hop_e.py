# SPDX-License-Identifier: 0BSD
"""Hop E: writer versus writer (GLB against the Blender package), a consistency check.

Positions, indices, UVs, normals and tangents must agree after each writer's declared conversions: the package
stores source bits, the GLB stores source bits for positions and UVs, normalized normals, tangents normalized
where they fall outside the float32 unit tolerance and (1, 0, 0, w) on the zero-direction lanes no package index
references (``writer_rules.normalize_tangents``, pinned Shared 591d083), and reversed triangle corners for clockwise
materials (the material's front face is read from the package manifest, not from the GLB). Packed
image bytes must equal the dump's Original or StandardPayload bytes, and every GLB PNG must equal the pinned
Pillow decode of the packed DDS (plus the declared normal preparation) within 1/255.
Control: one UV swapped (u and v exchanged on one vertex) in a copy of the package only.
"""

import io
import os
import traceback

import numpy as np

import dds_decode
import writer_rules as rules
from dump_reader import Dump
from gate1a_common import HopResult, Mismatch, OracleError, first_bit_mismatch, first_int_mismatch, sha256_bytes
from glb_reader import Glb
from hop_b import GlbDumpPairing, companion_packed, expected_image_pixels, match_in_order
from package_reader import Package, mutate_package


class GlbPackagePairing:
    """GLB mesh/primitive -> package (mesh index, primitive index) by mesh name and multitoolPrimitiveName."""

    def __init__(self, glb, package):
        self.primitive_map = {}
        self.material_map = {}
        names = [m.get('name') for m in package.manifest['meshes']]
        cursor = 0
        for gi, gmesh in enumerate(glb.json.get('meshes', [])):
            wanted = [(p.get('extras') or {}).get('multitoolPrimitiveName') for p in gmesh['primitives']]
            found = None
            for mi in range(cursor, len(names)):
                if names[mi] != gmesh.get('name'):
                    continue
                pnames = [p.get('name') for p in package.manifest['meshes'][mi]['primitives']]
                positions, pointer = [], 0
                for name in wanted:
                    hit = match_in_order(pnames, name, pointer)
                    if hit is None:
                        positions = None
                        break
                    positions.append(hit)
                    pointer = hit + 1
                if positions is not None:
                    found = (mi, positions)
                    break
            if found is None:
                raise OracleError('GLB mesh %d (%r) has no package mesh with primitives %r' % (gi, gmesh.get('name'), wanted))
            for pi, ppi in enumerate(found[1]):
                self.primitive_map[(gi, pi)] = (found[0], ppi)
            cursor = found[0] + 1
        material_names = [m.get('name') for m in package.manifest.get('materials', [])]
        cursor = 0
        for gi, gmat in enumerate(glb.json.get('materials', [])):
            hit = match_in_order(material_names, gmat.get('name'), cursor)
            if hit is None:
                raise OracleError('GLB material %d (%r) has no package material of that name' % (gi, gmat.get('name')))
            self.material_map[gi] = hit
            cursor = hit + 1


def compare_geometry(result, glb, package, pairing):
    check = result.check('positions, indices, UVs, normals: GLB versus package after the declared conversions')
    streams = 0
    for gi, gmesh in enumerate(glb.json.get('meshes', [])):
        for pi, gprim in enumerate(gmesh['primitives']):
            mi, ppi = pairing.primitive_map[(gi, pi)]
            pprim = package.manifest['meshes'][mi]['primitives'][ppi]
            label = 'meshes/%d/primitives/%d (%s)' % (gi, pi, pprim.get('name'))
            attributes = gprim['attributes']
            streams += 1
            mismatch = first_bit_mismatch(glb.accessor(attributes['POSITION']), package.stream(pprim['position']), label + '/POSITION')
            if mismatch:
                check.fail(mismatch)
                return
            streams += 1
            mismatch = first_bit_mismatch(glb.accessor(attributes['TEXCOORD_0']), package.stream(pprim['uv'][0]), label + '/TEXCOORD_0')
            if mismatch:
                check.fail(mismatch)
                return
            for set_index in range(1, len(pprim['uv'])):
                key = 'TEXCOORD_%d' % set_index
                if key not in attributes:
                    check.fail(Mismatch(label + '/' + key, 'presence', 'package has uv%d, GLB has no %s' % (set_index, key)))
                    return
                streams += 1
                mismatch = first_bit_mismatch(glb.accessor(attributes[key]), package.stream(pprim['uv'][set_index]), label + '/' + key)
                if mismatch:
                    check.fail(mismatch)
                    return
            material_index = pprim.get('material')
            front = None
            if material_index is not None:
                front = ((package.manifest['materials'][material_index].get('renderState') or {}).get('frontFace'))
            package_indices = package.stream(pprim['indices']).reshape(-1).astype(np.int64)
            expected = rules.reverse_indices(package_indices) if front == 'Clockwise' else package_indices
            streams += 1
            mismatch = first_int_mismatch(glb.accessor(gprim['indices']).reshape(-1), expected, label + '/indices' + (' (reversed)' if front == 'Clockwise' else ''))
            if mismatch:
                check.fail(mismatch)
                return
            expected_normals, _, _, zero = rules.normalize_normals(package.stream(pprim['normal']), pprim.get('normalMode'))
            if expected_normals is not None and 'NORMAL' in attributes:
                actual = glb.accessor(attributes['NORMAL'])
                keep = np.ones(len(expected_normals), dtype=bool)
                if zero:
                    keep[zero] = False
                streams += 1
                mismatch = first_bit_mismatch(actual[keep], expected_normals[keep], label + '/NORMAL')
                if mismatch:
                    check.fail(mismatch)
                    return
            if pprim.get('tangent') is not None and 'TANGENT' in attributes:
                # The package stores the source tangents; the GLB stores them after ModelGltfGeometry.NormalizeTangents
                # (``rules.normalize_tangents``, pinned Shared 591d083: a lane outside the float32 unit tolerance
                # normalized in double, w kept; a zero-direction lane that no index of the package's own index stream
                # references becomes (1, 0, 0, w)). An invalid lane (a referenced zero direction, a nonfinite direction,
                # w not exactly +1/-1) means the GLB writer refused, so a GLB that exists contradicts the package.
                expected_tangents, _, _, invalid_tangents = rules.normalize_tangents(package.stream(pprim['tangent']), package_indices)
                if invalid_tangents:
                    check.fail(Mismatch(label + '/TANGENT', 'unsupported', '%d package tangent lane(s) are invalid for the GLB writer '
                                        '(referenced zero or nonfinite direction, or w not exactly +1/-1), yet a GLB exists' % invalid_tangents))
                    return
                streams += 1
                mismatch = first_bit_mismatch(glb.accessor(attributes['TANGENT']), expected_tangents, label + '/TANGENT')
                if mismatch:
                    check.fail(mismatch)
                    return
    check.data['streamsCompared'] = streams
    check.ok('%d streams agree' % streams)


def compare_images(result, glb, package, dump, glb_dump_pairing):
    check = result.check('images: packed bytes equal the dump payload; GLB PNG equals the pinned decode of the packed DDS')
    from PIL import Image
    rows = package.manifest.get('images') or []
    packed = {}
    for index, row in enumerate(rows):
        data = package.image_payload(row)
        if data is None:
            continue
        image = dump.images[index] if index < len(dump.images) else None
        expected = None
        if image is not None:
            expected = {'Original': image.original, 'StandardPayload': image.standard, 'Derivation': image.derivation_payload}.get(row.get('packing'))
        if expected is None or expected['bytes'] != data:
            check.fail(Mismatch('images/%d' % index, 'bytes', 'packed bytes (%s) do not equal the dump %s payload' % (sha256_bytes(data)[:16], row.get('packing'))))
            return
        packed[index] = data
    compared, worst, details = 0, 0, []
    for gi, gmat in enumerate(glb.json.get('materials', [])):
        dmat = dump.materials[glb_dump_pairing.material_map[gi]]
        if rules.blend_plan_applies(dmat):
            # The writer derives material-specific images only on the blend route (ModelGltfBlendPreparation.Apply
            # line 37: a material without a BlendPlan is left untouched). ModelGltfBlendPlan.TryCreate line 20 gives no
            # plan unless RenderState is "{ Blend.Enabled: true, AlphaTest.Enabled: false }", so an absent render state
            # or a null blend declaration is NOT blend enabled (ModelGltfMaterials.Alpha reads it the same way: line 240
            # "state?.Blend?.Enabled != true" and line 262 "state?.Blend ?? DisabledBlend()"). The first cut indexed
            # renderState.blend.enabled directly and raised on a null blend; every other material binds the ordinary
            # prepared images and is compared.
            details.append({'material': gi, 'note': 'blend route: the derived images are not reproduced'})
            continue
        for role, key in (('base-color', 'texture'), ('normal', 'normalTexture'), ('specular', 'specularTexture'), ('emissive', 'emissiveTexture')):
            gbinding = None
            if role == 'base-color':
                gbinding = (gmat.get('pbrMetallicRoughness') or {}).get('baseColorTexture')
            elif role == 'normal':
                gbinding = gmat.get('normalTexture')
            elif role == 'specular':
                gbinding = ((gmat.get('extensions') or {}).get('KHR_materials_specular') or {}).get('specularTexture')
            else:
                gbinding = gmat.get('emissiveTexture')
            dbinding = dmat.get(key)
            if gbinding is None or dbinding is None:
                continue
            image_index = dbinding['imageIndex']
            if image_index not in packed:
                continue
            source_index, _ = glb.texture_image(gbinding['index'])
            png, _ = glb.image_bytes(source_index)
            with Image.open(io.BytesIO(png)) as im:
                im.load()
                actual = np.asarray(im.convert('RGBA'), dtype=np.int16)
            expected, notes = expected_image_pixels(dump, dmat, role, image_index, packed=companion_packed(gmat, dmat))
            if actual.shape != expected.shape:
                check.fail(Mismatch('materials/%d/%s' % (gi, role), 'shape', 'PNG %s, decode %s' % (actual.shape, expected.shape)))
                return
            diff = np.abs(actual - expected.astype(np.int16))
            per_channel = [int(diff[:, :, c].max()) for c in range(4)]
            compared += 1
            worst = max(worst, max(per_channel))
            details.append({'material': gi, 'role': role, 'packedImage': image_index, 'maxDifferencePerChannel': per_channel, 'notes': notes})
            if max(per_channel) > 1:
                check.fail(Mismatch('materials/%d/%s' % (gi, role), 'pixels', 'max per-channel difference %r (bound 1/255)' % per_channel))
                check.data['images'] = details
                return
    check.data['images'] = details
    check.data['packedImages'] = len(packed)
    check.data['materialsNotCompared'] = sum(1 for entry in details if 'note' in entry)
    check.ok('%d packed images equal the dump payloads; %d GLB PNGs within %d/255 of the pinned decode%s' % (
        len(packed), compared, worst, ', %d materials on the blend route not compared' % check.data['materialsNotCompared'] if check.data['materialsNotCompared'] else ''))


def swap_one_uv(source_path, target_path):
    """A copy of the package with u and v exchanged on the first vertex whose u and v bits differ, searching every UV
    stream of every primitive in document order. A vertex with u == v swaps to itself and no exact comparison could see
    it (two run-1 samples had u == v on every vertex of the only stream), so the search is over all streams and, when
    no vertex qualifies, the control is not applicable. Returns (stream path, vertex index) or None."""
    package = Package(source_path)
    target = None
    for _, _, _, primitive in package.primitives():
        for uv in primitive.get('uv') or []:
            bits = np.ascontiguousarray(package.stream(uv), dtype=np.float32).view(np.uint32)
            differing = np.flatnonzero(bits[:, 0] != bits[:, 1])
            if len(differing):
                target = (uv['path'], int(differing[0]))
                break
        if target is not None:
            break
    package.close()
    if target is None:
        return None
    path, vertex = target

    def mutate(name, data):
        if name != path:
            return data
        array = np.frombuffer(data, dtype='<f4').copy().reshape(-1, 2)
        array[vertex, 0], array[vertex, 1] = array[vertex, 1], array[vertex, 0]
        return array.tobytes()

    mutate_package(source_path, target_path, mutate)
    return path, vertex


def run(sample, work_dir):
    result = HopResult('E', sample['id'])
    try:
        if not sample.get('package') or not sample.get('glb') or not sample.get('dump'):
            result.check('GLB versus package').skip('GLB, package or dump not supplied')
            return result
        glb = Glb.from_path(sample['glb'])
        package = Package(sample['package'])
        dump = Dump(sample['dump'])
        pairing = GlbPackagePairing(glb, package)
        glb_dump = GlbDumpPairing(glb, dump)
        compare_geometry(result, glb, package, pairing)
        compare_images(result, glb, package, dump, glb_dump)
        package.close()
        os.makedirs(work_dir, exist_ok=True)
        mutated_path = os.path.join(work_dir, 'control-uv-swapped.zip')
        swapped = swap_one_uv(sample['package'], mutated_path)
        control = {'name': 'one UV swapped in a copy of the package only', 'detected': None, 'detail': None, 'firstMismatch': None}
        if swapped is None:
            control['detail'] = 'not applicable: every vertex of every UV stream has u == v, so an exchange changes nothing'
        else:
            path, vertex = swapped
            mutated = Package(mutated_path)
            probe = HopResult('E-control', sample['id'])
            compare_geometry(probe, glb, mutated, GlbPackagePairing(glb, mutated))
            mutated.close()
            failed = probe.checks[-1].passed is False
            control['detected'] = failed
            control['detail'] = 'u and v exchanged on vertex %d of %s: %s' % (vertex, path, 'mismatch reported' if failed else 'NOT detected')
            control['firstMismatch'] = probe.checks[-1].mismatches[0].to_dict() if probe.checks[-1].mismatches else None
        result.controls.append(control)
    except OracleError as failure:
        result.error = str(failure)
    except Exception as failure:  # noqa: BLE001 - an oracle defect must reach the receipt, not abort the run
        result.error = 'oracle exception %s: %s' % (type(failure).__name__, failure)
        result.traceback = traceback.format_exc()
    return result
