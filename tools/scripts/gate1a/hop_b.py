# SPDX-License-Identifier: 0BSD
"""Hop B: document (exact JSON dump) -> GLB.

Four checks (design section 7.2, row B):
  1. The Khronos validator reports 0 errors (the pinned payload; a validator that cannot be located fails the run).
  2. Every accessor equals the dump after the writer's declared conversions, bit for bit on float32: TANGENT
     against ``writer_rules.tangent_plan`` (pinned Shared 591d083: a lane outside the float32 unit tolerance is
     normalized in double, its handedness kept; a zero-direction lane no index references must be exactly
     (1, 0, 0, w) with the authored w, compared as its own stream; an invalid lane, a referenced zero direction
     included, means the writer refused, so a GLB that exists is a failure); NORMAL against
     ``writer_rules.normalize_normals`` on non-zero lanes, an unreferenced zero lane must be exactly (0, 1, 0)
     (``SceneVertexNormals.ReconstructMissing`` with fillUnreferenced), a referenced zero lane must be unit length
     (its area-weighted reconstruction is not reproduced).
  3. Every GLB PNG equals Pillow's decode of the dump's image payload after the declared image conversions,
     within 1/255 per channel, with the maximum difference recorded.
  4. Every fidelity row of ``mesh fidelity --format glb --json`` is present in the GLB's multitoolFidelity
     carrier as often as in the report (both are compared as multisets and every row is judged, so a duplicated row
     cannot hide behind an agreeing copy), and the values rows declare are present in the file: the normalized-normal
     and normalized-tangent direction errors re-measured with the writer's atan2 formula and their outcome checked
     against the 0.1 degree
     threshold, the geometry/normals row of every evaluated primitive being the one the zero-lane and normalization
     rules predict (``expected_normals_row``) with its zero-normal counts (reconstructed M, filled N) parsed from the
     row text and compared with the oracle's count of referenced and unreferenced zero lanes, the placeholder row
     present exactly when N > 0, the tangent rows of every evaluated primitive being exactly the set
     ``writer_rules.expected_tangent_rows`` derives (featureId, reason code, outcome, bound) with the placeholder
     counts ("Filled {N}", "Exactly {N}") parsed from the row text and compared with the oracle's count of unreferenced
     zero-direction lanes, and the morph-tangent row present exactly when the base tangents changed (normalized or
     filled) and the target carries tangent deltas.
Seven controls, each on a mutated copy: swapped BC1 endpoints re-encoded into a copy of the GLB's base-color
PNG; the MASK cutoff rewritten as T/255 in a copy of the GLB; a one-texel UV shift in a copy of the GLB; the
base-color payload made undecodable (FourCC rewritten) in a copy of the dump, so an unavailable expectation
must read as a failed check; the handedness sign of one TANGENT lane flipped in a copy of the GLB (the first lane
that is not a tangent placeholder); one unreferenced zero-normal placeholder lane rewritten as (1, 0, 0) in a copy
of the GLB (unit length, so only the exact placeholder comparison can see it); one unreferenced zero-tangent
placeholder lane rewritten as (0, 1, 0, w) in a copy of the GLB (unit length with its handedness kept, so only the
exact (1, 0, 0, w) comparison can see it). The three accessor controls are table-driven (``GEOMETRY_CONTROLS``,
``run_geometry_control``) and ``synthetic_control_exercise`` runs one of them, or the MASK control
(``run_mask_control``), on the hand-built artifact set of ``gate1a_synthetic`` through the same mutation, pristine
checks and verdict, which the driver calls for a control no corpus sample reaches.
"""

import collections
import copy
import io
import json
import os
import re
import struct
import subprocess
import tempfile
import traceback

import numpy as np

import dds_decode
import writer_rules as rules
from dump_reader import Dump
from gate1a_common import HopResult, Mismatch, OracleError, bits_equal, f32, first_bit_mismatch, first_int_mismatch, json_loads, sha256_bytes
from glb_reader import Glb

GLB_BINDINGS = [
    ('base-color', lambda m: (m.get('pbrMetallicRoughness') or {}).get('baseColorTexture'), 'texture'),
    ('normal', lambda m: m.get('normalTexture'), 'normalTexture'),
    ('occlusion', lambda m: m.get('occlusionTexture'), 'occlusionTexture'),
    ('emissive', lambda m: m.get('emissiveTexture'), 'emissiveTexture'),
    ('metallic-roughness', lambda m: (m.get('pbrMetallicRoughness') or {}).get('metallicRoughnessTexture'), 'metallicRoughnessTexture'),
    ('specular', lambda m: ((m.get('extensions') or {}).get('KHR_materials_specular') or {}).get('specularTexture'), 'specularTexture'),
    ('specular-color', lambda m: ((m.get('extensions') or {}).get('KHR_materials_specular') or {}).get('specularColorTexture'), 'specularColorTexture'),
    ('transmission', lambda m: ((m.get('extensions') or {}).get('KHR_materials_transmission') or {}).get('transmissionTexture'), 'transmissionTexture'),
]


def run_validator(validator, glb_path):
    """The validator's JSON report. Raises OracleError when the validator cannot run."""
    if not validator or not os.path.isfile(validator):
        raise OracleError('the Khronos validator was not located; a missing validator is a hard failure of the run')
    try:
        completed = subprocess.run([validator, '-o', glb_path], capture_output=True, timeout=600)
    except subprocess.TimeoutExpired as failure:
        raise OracleError('the validator exceeded 600 s on %s: stdout %r stderr %r'
                          % (glb_path, (failure.stdout or b'')[:500], (failure.stderr or b'')[:500])) from failure
    if not completed.stdout.strip():
        raise OracleError('the validator produced no report: %s' % completed.stderr.decode('utf-8', 'replace')[:500])
    try:
        report = json.loads(completed.stdout.decode('utf-8'))
    except (json.JSONDecodeError, UnicodeDecodeError) as failure:
        raise OracleError('the validator report is not JSON (%s): stdout %r stderr %r'
                          % (failure, completed.stdout[:500], completed.stderr[:500])) from failure
    if not isinstance(report, dict):
        raise OracleError('the validator report is not a JSON object: %r' % completed.stdout[:500])
    report['exitCode'] = completed.returncode
    return report


def match_in_order(names, wanted, start):
    """The index of the first ``wanted`` name in ``names`` at or after ``start``, or None."""
    for index in range(start, len(names)):
        if names[index] == wanted:
            return index
    return None


class GlbDumpPairing:
    """Maps GLB meshes, primitives, materials, images and nodes back to dump indices by the names the writer keeps."""

    def __init__(self, glb, dump):
        self.glb = glb
        self.dump = dump
        self.mesh_map = {}
        self.primitive_map = {}
        self.material_map = {}
        self.node_offset_ok = None
        dump_mesh_names = [m['name'] for m in dump.meshes]
        cursor = 0
        for gi, gmesh in enumerate(glb.json.get('meshes', [])):
            wanted = [(p.get('extras') or {}).get('multitoolPrimitiveName') for p in gmesh['primitives']]
            found = None
            for di in range(cursor, len(dump_mesh_names)):
                if dump_mesh_names[di] != gmesh.get('name'):
                    continue
                dump_names = [p.name for p in dump.meshes[di]['primitives']]
                positions = []
                pointer = 0
                for name in wanted:
                    hit = match_in_order(dump_names, name, pointer)
                    if hit is None:
                        positions = None
                        break
                    positions.append(hit)
                    pointer = hit + 1
                if positions is not None:
                    found = (di, positions)
                    break
            if found is None:
                raise OracleError('GLB mesh %d (%r, primitives %r) has no dump mesh with those primitive names' % (gi, gmesh.get('name'), wanted))
            self.mesh_map[gi] = found[0]
            for pi, di_prim in enumerate(found[1]):
                self.primitive_map[(gi, pi)] = (found[0], di_prim)
            cursor = found[0] + 1
        dump_material_names = [m.get('name') for m in dump.materials]
        cursor = 0
        for gi, gmat in enumerate(glb.json.get('materials', [])):
            hit = match_in_order(dump_material_names, gmat.get('name'), cursor)
            if hit is None:
                raise OracleError('GLB material %d (%r) has no dump material of that name after index %d' % (gi, gmat.get('name'), cursor))
            self.material_map[gi] = hit
            cursor = hit + 1


def layer_selection_mode(glb):
    """The layer selection mode the GLB's own fidelity carrier declares (``layers.static-selection``: "Mode X: ..."),
    or None when the carrier has no such row."""
    rows = ((glb.json.get('extras') or {}).get('multitoolFidelity') or {}).get('rows') or []
    for row in rows:
        if row.get('reasonCode') == 'layers.static-selection':
            match = re.match(r'Mode (\w+):', row.get('description') or '')
            return match.group(1) if match else None
    return None


def check_nodes(result, glb, dump, pairing=None):
    """Names and transforms index for index; children, mesh and skin bindings as the writer's static layer selection
    and draw selection declare them (``rules.layer_plan``, from ModelGltfLayers.Plan and ModelGltfDrawSelection.Plan);
    one coordinate parent per kept scene root. A child the writer keeps that the GLB lacks, a child the writer drops
    that the GLB keeps, and a mesh binding present or absent against the rule are all failures."""
    check = result.check('nodes: names, transforms, layer-selected children and mesh bindings, coordinate parents')
    nodes = glb.json.get('nodes', [])
    parents = [n for n in nodes if str(n.get('name', '')).startswith('multitool coordinates/')]
    originals = nodes[:len(nodes) - len(parents)]
    if len(originals) != len(dump.nodes):
        check.fail(Mismatch('nodes', 'count', 'GLB has %d original nodes plus %d coordinate parents; dump has %d nodes' % (len(originals), len(parents), len(dump.nodes))))
        return
    mode = layer_selection_mode(glb)
    check.data['layerSelectionMode'] = mode
    if mode not in (None, 'Default'):
        check.skip('layer selection mode %r is not reproduced (only Default, the mode mesh convert runs, is)' % mode)
        return
    try:
        plan = rules.layer_plan(dump.nodes, dump.scenes, dump.skins, dump.document.get('layerSets') or [],
                                [any(p.purpose == 'Render' for p in m['primitives']) for m in dump.meshes])
    except ValueError as failure:
        check.fail(Mismatch('nodes', 'hierarchy', 'the writer refuses this hierarchy: %s' % failure))
        return
    check.data['layerPlan'] = {'layerSets': len(dump.document.get('layerSets') or []),
                               'nodesNotNeeded': int(sum(1 for v in plan['needed'] if not v)),
                               'childEdgesDropped': int(sum(len(d.get('children') or []) - len(c) for d, c in zip(dump.nodes, plan['children']))),
                               'meshBindingsDropped': int(sum(1 for d, draws in zip(dump.nodes, plan['drawsMesh']) if d.get('meshIndex') is not None and not draws))}
    for index, (gnode, dnode) in enumerate(zip(originals, dump.nodes)):
        if gnode.get('name') != dnode.get('name'):
            check.fail(Mismatch('nodes/%d' % index, 'name', 'GLB %r, dump %r' % (gnode.get('name'), dnode.get('name'))))
            return
        trs = dnode.get('localTrs')
        if trs is not None:
            expected = [np.float32(v) for v in trs['translation'] + trs['rotation'] + trs['scale']]
            actual = [np.float32(v) for v in gnode.get('translation', [0, 0, 0]) + gnode.get('rotation', [0, 0, 0, 1]) + gnode.get('scale', [1, 1, 1])]
            if not bits_equal(actual, expected):
                check.fail(Mismatch('nodes/%d' % index, 'trs', 'GLB %r, dump %r' % ([float(a) for a in actual], [float(e) for e in expected])))
                return
        else:
            expected = [np.float32(v) for v in dnode['localTransform']]
            identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
            if 'matrix' in gnode:
                if not bits_equal([np.float32(v) for v in gnode['matrix']], expected):
                    check.fail(Mismatch('nodes/%d' % index, 'matrix', 'GLB %r, dump %r' % (gnode['matrix'], dnode['localTransform'])))
                    return
            elif not bits_equal(expected, [np.float32(v) for v in identity]):
                check.fail(Mismatch('nodes/%d' % index, 'matrix', 'GLB node has no transform, dump localTransform is %r' % dnode['localTransform']))
                return
        # Children: the writer keeps only the needed children of a needed node (ModelGltfLayers.Plan lines 126-135)
        # and writes the property only when any remain (SceneGltfBuilder line 141). The dump's list is the source.
        expected_children = plan['children'][index]
        if list(gnode.get('children', [])) != expected_children:
            check.fail(Mismatch('nodes/%d' % index, 'children', 'GLB %r, expected %r (dump %r; layer selection %s)' % (
                gnode.get('children'), expected_children, dnode.get('children'),
                'keeps every child' if expected_children == list(dnode.get('children') or []) else 'drops the children the plan does not need')))
            return
        # Mesh and skin: emitted only for a node the draw selection draws (ModelGltfDrawSelection.Plan lines 99-102 and
        # 150-151, ModelGltfLowering.Nodes lines 224-226); the GLB mesh must be the dump mesh the node binds.
        draws = plan['drawsMesh'][index]
        if ('mesh' in gnode) != draws:
            reason = ('not reachable from the kept scene roots' if not plan['reachable'][index]
                      else 'a disabled layer member kept as transform-only' if not plan['keepMesh'][index]
                      else 'role %r does not draw' % dnode.get('role') if not rules.ordinary_role(dnode.get('role'))
                      else 'its mesh has no Render primitive') if dnode.get('meshIndex') is not None and not draws else ''
            check.fail(Mismatch('nodes/%d' % index, 'mesh', 'GLB mesh %r, dump meshIndex %r; the writer %s%s' % (
                gnode.get('mesh'), dnode.get('meshIndex'), 'draws it' if draws else 'omits it', (': ' + reason) if reason else '')))
            return
        if draws:
            if pairing is not None and pairing.mesh_map.get(gnode['mesh']) != dnode['meshIndex']:
                check.fail(Mismatch('nodes/%d' % index, 'mesh', 'GLB mesh %d pairs with dump mesh %r, node binds dump mesh %r' % (
                    gnode['mesh'], pairing.mesh_map.get(gnode['mesh']), dnode['meshIndex'])))
                return
            if ('skin' in gnode) != (dnode.get('skinIndex') is not None):
                check.fail(Mismatch('nodes/%d' % index, 'skin', 'GLB skin %r, dump skinIndex %r' % (gnode.get('skin'), dnode.get('skinIndex'))))
                return
        elif 'skin' in gnode:
            check.fail(Mismatch('nodes/%d' % index, 'skin', 'GLB binds skin %r on a node that draws no mesh' % gnode.get('skin')))
            return
    # Coordinate parents: one per kept scene root, matrix = orientation * scale(float32 factor).
    scale = 1.0
    mpu = (dump.units or {}).get('metersPerUnit')
    expected, rounded, limitation = rules.coordinate_parent_matrix(mpu, scale, dump.basis)
    check.data['expectedRootMatrix'] = expected
    check.data['roundedFactor'] = rounded
    if limitation:
        check.skip('coordinate plan unsupported: %s' % limitation)
        return
    roots = glb.scene_roots()
    if not parents:
        check.fail(Mismatch('coordinate-parent', 'absent', 'no "multitool coordinates/<root>" node in the GLB'))
        return
    for root in roots:
        node = nodes[root]
        if not str(node.get('name', '')).startswith('multitool coordinates/'):
            check.fail(Mismatch('scenes/roots', 'root', 'scene root %d (%r) is not a coordinate parent' % (root, node.get('name'))))
            return
    # The default scene keeps exactly the needed roots of the dump's default scene, in order (ModelGltfLayers.Plan
    # lines 87-95), each wrapped by its coordinate parent.
    wrapped = [int(str(nodes[root].get('name', '')).split('/')[-1]) for root in roots]
    expected_roots = plan['sceneRoots'][dump.default_scene_index] if plan['sceneRoots'] else []
    if wrapped != expected_roots:
        check.fail(Mismatch('scenes/roots', 'set', 'GLB default scene wraps %r, the layer selection keeps %r of the dump roots %r' % (
            wrapped, expected_roots, (dump.scenes[dump.default_scene_index].get('rootNodeIndices') if dump.scenes else None))))
        return
    for pnode in parents:
        matrix = pnode.get('matrix')
        if matrix is None:
            check.fail(Mismatch('coordinate-parent', 'matrix', '%r has no matrix property' % pnode.get('name')))
            return
        if not bits_equal([np.float32(v) for v in matrix], [np.float32(v) for v in expected]):
            check.fail(Mismatch('coordinate-parent', 'matrix', '%r has %r, expected %r' % (pnode.get('name'), matrix, expected)))
            return
        original = int(pnode['name'].split('/')[-1])
        if list(pnode.get('children', [])) != [original]:
            check.fail(Mismatch('coordinate-parent', 'children', '%r wraps %r, expected [%d]' % (pnode.get('name'), pnode.get('children'), original)))
            return
    check.ok('%d nodes (%d child edges and %d mesh bindings dropped by the layer selection), %d coordinate parents with matrix %s (factor %r on the parent, not on vertices)' % (
        len(originals), check.data['layerPlan']['childEdgesDropped'], check.data['layerPlan']['meshBindingsDropped'], len(parents), expected, rounded))


PLACEHOLDER_NORMAL = np.array([0.0, 1.0, 0.0], dtype=np.float32)  # Vector3.UnitY, SceneVertexNormals.cs line 141
PLACEHOLDER_STREAM = 'NORMAL (unreferenced zero-normal placeholders, expected (0, 1, 0))'
TANGENT_STREAM = 'TANGENT'
# The unreferenced zero-direction tangent lanes, compared as their own stream after the other TANGENT lanes
# (ModelGltfGeometry.NormalizeTangents line 281, pinned Shared 591d083: new Vector4(1, 0, 0, value.W)).
TANGENT_PLACEHOLDER_STREAM = 'TANGENT (unreferenced zero-tangent placeholders, expected (1, 0, 0, w))'
# A reconstructed normal is ((float)(x / length), (float)(y / length), (float)(z / length)) of a double unit direction
# (SceneMissingNormalAccumulator.Normalize, lines 64-74). Each float32 rounding carries a relative error of at most
# 2^-24, so the squared length measured in double from the stored float32 components deviates from 1 by at most
# 2 * 2^-24 + 3 * 2^-48 plus the double direction's own error (a few 2^-53): below 2^-23 = 1.19e-7. The check allows
# 2^-22 (2.38e-7), twice that bound. np.allclose with its defaults (used until 2026-09-24) admitted |squared - 1| up to
# 1e-6 + 1e-5, and so a lane scaled by 1 + 5.5e-6, which no float32 rounding of a unit direction can produce.
RECONSTRUCTED_UNIT_TOLERANCE = 2.0 ** -22


def zero_normal_lanes(prim):
    """The zero-normal lanes of a dump primitive and which of them a triangle references.

    SceneVertexNormals.ReconstructMissing (lines 99-154, fillUnreferenced true from ModelGltfGeometry lines 199-200):
    a lane is zero when its normal equals Vector3.Zero (line 115; -0 counts), ``referenced`` is marked for every
    index of every triangle, degenerate triangles included (lines 119-130); an unreferenced zero lane receives
    Vector3.UnitY (lines 139-144) and is counted as ``unreferencedCount``; a referenced zero lane is reconstructed
    from area-weighted incident triangles (line 145), or the item is refused when the area sum is zero.

    The reference marking is ``writer_rules.referenced_vertices``, the rule the tangent placeholders use as well
    (ModelGltfGeometry.ReferencedVertices, pinned Shared 591d083); an index outside the vertex domain raises there.

    Returns (zero mask, referenced mask) over the vertex lanes; Flat normal mode has no zero lanes to fill (the writer
    skips the vertex scan, ModelGltfGeometry line 180), so both masks are all False there."""
    count = prim.vertex_count
    zero = np.zeros(count, dtype=bool)
    referenced = np.zeros(count, dtype=bool)
    if prim.normal_mode == 'Flat':
        return zero, referenced
    n = prim.normals.astype(np.float64)
    zero = ((n[:, 0] * n[:, 0] + n[:, 1] * n[:, 1]) + n[:, 2] * n[:, 2]) == 0
    referenced = rules.referenced_vertices(prim.indices, count)
    return zero, referenced


def manifest_tangent_refusal(partial_reasons):
    """Which writer's tangent-basis refusal text (``writer_rules.TANGENT_REFUSAL_TEXTS``) the manifest's
    ``partialReasons`` quote: 'Shared 591d083', 'Shared 0fe3212', or None when they quote neither (or were not
    recorded). The artifacts manifest names no writer pin, so this is the evidence of which rule refused."""
    for pin, text in rules.TANGENT_REFUSAL_TEXTS:
        if any(text in str(reason) for reason in partial_reasons or ()):
            return pin
    return None


def dump_refusal_evidence(dump, partial_reasons=None):
    """What the dump says about a GLB the writer refused: the primitives whose tangents carry an invalid lane
    (``geometry.tangent-basis-unsupported``, no GLB written; at pinned Shared 591d083 a zero direction is invalid only
    when an index references its slot), those whose tangents the writer would normalize (a GLB is written, with
    ``geometry.tangents-normalized``) and those carrying unreferenced zero-direction lanes the writer fills with
    (1, 0, 0, w) (a GLB is written, with ``geometry.unreferenced-tangents-filled``). Recorded on a partial sample so a
    refusal can be tied to the rule that predicts it; the writer's other admission limits are not reproduced here.

    The artifacts manifest does not name the writer pin that produced it, so the evidence also records, as
    diagnostics, what the PREVIOUS pin's rule decides (``previousPinRule``: Shared 0fe3212 refused every zero-direction
    lane, referenced or not, ``writer_rules.previous_pin_invalid_tangents``) and which writer's tangent-basis refusal
    text the manifest's ``partialReasons`` quote (``manifestQuotesTangentRefusal``), so a receipt over an older
    writer's artifacts does not describe that writer's tangent refusal as absent."""
    invalid, changed, placeholders, previous = [], [], [], []
    for mi, mesh in enumerate(dump.meshes):
        for pi, prim in enumerate(mesh['primitives']):
            if prim.tangents is None:
                continue
            plan = rules.tangent_plan(prim.tangents, prim.indices)
            label = 'meshes/%d/primitives/%d (%s)' % (mi, pi, prim.name)
            if plan['invalidCount']:
                invalid.append({'primitive': label, 'invalidLanes': plan['invalidCount']})
            else:
                if plan['normalizedCount']:
                    changed.append({'primitive': label, 'maxDegrees': plan['maxDegrees']})
                if plan['unreferencedCount']:
                    placeholders.append({'primitive': label, 'placeholderLanes': plan['unreferencedCount']})
            previous_invalid = rules.previous_pin_invalid_tangents(prim.tangents)
            if previous_invalid:
                previous.append({'primitive': label, 'invalidLanes': previous_invalid})
    return {'invalidTangentPrimitives': invalid, 'normalizedTangentPrimitives': changed,
            'placeholderTangentPrimitives': placeholders, 'refusalPredictedByTangents': bool(invalid),
            'previousPinRule': {'pin': 'Shared 0fe3212', 'invalidTangentPrimitives': previous, 'refusalPredictedByTangents': bool(previous)},
            'manifestQuotesTangentRefusal': manifest_tangent_refusal(partial_reasons)}


def check_geometry(result, glb, dump, pairing, allow_signed_zero=False):
    """Bit-exact by default: the writer copies float bits verbatim (SceneGltfBuilder.cs lines 428-433) and the dump
    keeps the sign of zero, so a -0/+0 difference is a mismatch unless ``allow_signed_zero`` is passed explicitly."""
    check = result.check('accessors: positions, normals, colors, UVs, tangents, indices, skins, morphs')
    signed_zero_only = 0
    streams = 0
    colors_not_compared = []
    for gi, gmesh in enumerate(glb.json.get('meshes', [])):
        for pi, gprim in enumerate(gmesh['primitives']):
            di, dpi = pairing.primitive_map[(gi, pi)]
            prim = dump.meshes[di]['primitives'][dpi]
            label = 'meshes/%d/primitives/%d (%s)' % (gi, pi, prim.name)
            attributes = gprim['attributes']
            if gprim.get('mode', 4) != 4:
                check.fail(Mismatch(label, 'mode', 'primitive mode %r is not TRIANGLES' % gprim.get('mode')))
                return

            def compare_float(name, actual, expected):
                nonlocal signed_zero_only, streams
                streams += 1
                mismatch = first_bit_mismatch(actual, expected, label + '/' + name)
                if mismatch is None:
                    return True
                a = np.ascontiguousarray(actual, dtype=np.float32)
                e = np.ascontiguousarray(expected, dtype=np.float32)
                if a.shape == e.shape and np.array_equal(a, e) and allow_signed_zero:
                    signed_zero_only += int((a.view(np.uint32) != e.view(np.uint32)).sum())
                    return True
                check.fail(mismatch)
                return False

            if not compare_float('POSITION', glb.accessor(attributes['POSITION']), prim.positions):
                return
            expected_normals, max_degrees, changed, zero = rules.normalize_normals(prim.normals, prim.normal_mode)
            if expected_normals is None:
                if 'NORMAL' in attributes:
                    check.fail(Mismatch(label + '/NORMAL', 'presence', 'Flat normal mode but a NORMAL accessor is present'))
                    return
            else:
                if 'NORMAL' not in attributes:
                    check.fail(Mismatch(label + '/NORMAL', 'presence', 'NORMAL accessor absent'))
                    return
                actual = glb.accessor(attributes['NORMAL'])
                if zero:
                    # SceneVertexNormals.ReconstructMissing with fillUnreferenced: a zero lane no triangle index
                    # references is exactly UnitY; a referenced zero lane is reconstructed (unit length, the recipe
                    # itself not reproduced); every other lane is the normalized source lane.
                    zero_mask, referenced = zero_normal_lanes(prim)
                    keep = ~zero_mask
                    if not compare_float('NORMAL (non-zero source lanes)', actual[keep], expected_normals[keep]):
                        return
                    placeholder = zero_mask & ~referenced
                    reconstructed = zero_mask & referenced
                    if placeholder.any():
                        expected_placeholders = np.tile(PLACEHOLDER_NORMAL, (int(placeholder.sum()), 1))
                        if not compare_float(PLACEHOLDER_STREAM, actual[placeholder], expected_placeholders):
                            return
                    lanes = actual[reconstructed].astype(np.float64)
                    lengths = (lanes[:, 0] * lanes[:, 0] + lanes[:, 1] * lanes[:, 1]) + lanes[:, 2] * lanes[:, 2]
                    unit = bool(np.isfinite(lengths).all() and (np.abs(lengths - 1) <= RECONSTRUCTED_UNIT_TOLERANCE).all()) if reconstructed.any() else True
                    check.data.setdefault('reconstructedNormals', []).append({
                        'primitive': label, 'count': len(zero), 'unreferenced': int(placeholder.sum()),
                        'reconstructed': int(reconstructed.sum()), 'unitLength': unit})
                    if not unit:
                        worst = int(np.argmax(np.abs(lengths - 1))) if lengths.size else 0
                        lane = int(np.flatnonzero(reconstructed)[worst])
                        check.fail(Mismatch(label + '/NORMAL (referenced zero-normal lanes, reconstructed)', 'unit-length',
                                            'lane %d has squared length %r; a reconstructed normal must be unit length within %r of squared length (the float32 rounding bound of a double unit direction)'
                                            % (lane, float(lengths[worst]), RECONSTRUCTED_UNIT_TOLERANCE)))
                        return
                elif not compare_float('NORMAL', actual, expected_normals):
                    return
                check.data.setdefault('normalsMaxDegrees', {})[label] = max_degrees
            expected_colors, color_path = rules.expected_primary_colors(prim)
            encoding = 'FloatingPoint' if color_path == 'attribute' else prim.color_encoding
            if 'COLOR_0' not in attributes:
                check.fail(Mismatch(label + '/COLOR_0', 'presence', 'COLOR_0 accessor absent'))
                return
            color_accessor = glb.json['accessors'][attributes['COLOR_0']]
            consuming = dump.materials[prim.material_index] if prim.material_index is not None else {}
            if rules.blend_plan_applies(consuming):
                # ModelGltfBlendPreparation (line 69): on the transmission route COLOR_0 carries tint/coverage as float,
                # not a copy of the attribute; on the lit fallback it is unchanged. Compare, and when the copy does not
                # hold, record the stream as not compared rather than as a writer mismatch.
                stored = glb.accessor(attributes['COLOR_0'])
                if encoding == 'FloatingPoint' and color_accessor['componentType'] == 5126:
                    same = first_bit_mismatch(stored, expected_colors, label + '/COLOR_0') is None
                elif encoding != 'FloatingPoint' and color_accessor['componentType'] != 5126:
                    same = first_int_mismatch(stored, rules.encode_color_bytes(expected_colors, encoding), label + '/COLOR_0') is None
                else:
                    same = False
                if same:
                    streams += 1
                else:
                    colors_not_compared.append({'primitive': label, 'material': prim.material_index,
                                                'note': 'blend route (ModelGltfBlendPreparation): COLOR_0 may carry transmission tint, not the attribute'})
            elif encoding == 'FloatingPoint':
                if color_accessor['componentType'] != 5126:
                    check.fail(Mismatch(label + '/COLOR_0', 'type', 'componentType %d, expected FLOAT' % color_accessor['componentType']))
                    return
                if not compare_float('COLOR_0', glb.accessor(attributes['COLOR_0']), expected_colors):
                    return
            else:
                stored = glb.accessor(attributes['COLOR_0'])
                mismatch = first_int_mismatch(stored, rules.encode_color_bytes(expected_colors, encoding), label + '/COLOR_0')
                streams += 1
                if mismatch:
                    check.fail(mismatch)
                    return
            if not compare_float('TEXCOORD_0', glb.accessor(attributes['TEXCOORD_0']), prim.uv0):
                return
            for set_index, values in enumerate(prim.additional_uvs):
                key = 'TEXCOORD_%d' % (set_index + 1)
                if key not in attributes:
                    check.fail(Mismatch(label + '/' + key, 'presence', 'accessor absent'))
                    return
                if not compare_float(key, glb.accessor(attributes[key]), values):
                    return
            if prim.tangents is not None:
                # ModelGltfGeometry.NormalizeTangents (pinned Shared 591d083): a lane outside the float32 unit
                # tolerance is normalized in double (handedness kept); a zero-direction lane no index references is
                # filled with (1, 0, 0, w); an invalid lane (a REFERENCED zero direction, a nonfinite direction, a w
                # other than exactly +1/-1) makes the item Unsupported, so no GLB should exist.
                plan = rules.tangent_plan(prim.tangents, prim.indices)
                if plan['invalidCount']:
                    check.data.setdefault('invalidTangentLanes', {})[label] = plan['invalidCount']
                    check.fail(Mismatch(label + '/TANGENT', 'unsupported',
                                        '%d tangent lane(s) have a referenced zero direction, a nonfinite direction or a handedness other than exactly +1/-1: '
                                        'the writer declares geometry.tangent-basis-unsupported and writes no GLB, yet this GLB exists' % plan['invalidCount']))
                    return
                if 'TANGENT' not in attributes:
                    check.fail(Mismatch(label + '/TANGENT', 'presence', 'accessor absent'))
                    return
                actual = glb.accessor(attributes['TANGENT'])
                expected_tangents = plan['expected']
                placeholder = plan['placeholder']
                if placeholder.any() and actual.shape == expected_tangents.shape:
                    # The authored lanes first, under the stream label the w-flip control is judged on; then the
                    # placeholder lanes as their own stream, which only the exact (1, 0, 0, w) comparison can fail.
                    # A subset mismatch names its position in the subset; the accessor lane is appended.
                    for stream, mask in ((TANGENT_STREAM, ~placeholder), (TANGENT_PLACEHOLDER_STREAM, placeholder)):
                        if compare_float(stream, actual[mask], expected_tangents[mask]):
                            continue
                        differs = np.ascontiguousarray(actual[mask], dtype=np.float32).view(np.uint32) != \
                            np.ascontiguousarray(expected_tangents[mask], dtype=np.float32).view(np.uint32)
                        check.mismatches[-1].detail += ' (accessor lane %d; %d placeholder lane(s) compared separately)' % (
                            int(np.flatnonzero(mask)[int(np.argwhere(differs)[0][0])]), int(placeholder.sum()))
                        return
                elif not compare_float(TANGENT_STREAM, actual, expected_tangents):
                    return
                check.data.setdefault('tangentsMaxDegrees', {})[label] = plan['maxDegrees']
                check.data.setdefault('tangentsChanged', {})[label] = plan['changed']
                if plan['unreferencedCount']:
                    check.data.setdefault('tangentPlaceholders', []).append({
                        'primitive': label, 'placeholderLanes': plan['unreferencedCount'], 'normalizedLanes': plan['normalizedCount'],
                        'lanes': [int(i) for i in np.flatnonzero(placeholder)[:64]],
                        'handedness': {'+1': int((expected_tangents[placeholder, 3] == 1).sum()), '-1': int((expected_tangents[placeholder, 3] == -1).sum())}})
            elif 'TANGENT' in attributes:
                check.fail(Mismatch(label + '/TANGENT', 'presence', 'GLB has tangents, dump has none'))
                return
            # Indices, with the clockwise reversal declared by the consuming material.
            material = dump.materials[prim.material_index] if prim.material_index is not None else {}
            reverse = (material.get('renderState') or {}).get('frontFace') == 'Clockwise'
            expected_indices = rules.reverse_indices(prim.indices) if reverse else prim.indices
            actual_indices = glb.accessor(gprim['indices']).reshape(-1)
            index_accessor = glb.json['accessors'][gprim['indices']]
            expect_short = bool((prim.indices < 65535).all())
            if expect_short != (index_accessor['componentType'] == 5123):
                check.fail(Mismatch(label + '/indices', 'type', 'componentType %d; every index below 65535: %s' % (index_accessor['componentType'], expect_short)))
                return
            mismatch = first_int_mismatch(actual_indices, expected_indices, label + '/indices' + (' (reversed)' if reverse else ''))
            streams += 1
            if mismatch:
                check.fail(mismatch)
                return
            # Skin influences.
            if prim.influences_per_vertex:
                expected_weights, max_sum_error = rules.normalize_weights(prim.weights, prim.influences_per_vertex, prim.vertex_count)
                sets = rules.joint_weight_sets(prim.joint_indices, expected_weights, prim.influences_per_vertex, prim.vertex_count)
                for s, (joints, weights) in enumerate(sets):
                    jk, wk = 'JOINTS_%d' % s, 'WEIGHTS_%d' % s
                    if jk not in attributes or wk not in attributes:
                        check.fail(Mismatch(label, 'presence', '%s/%s absent for %d influences per vertex' % (jk, wk, prim.influences_per_vertex)))
                        return
                    mismatch = first_int_mismatch(glb.accessor(attributes[jk]), joints, label + '/' + jk)
                    streams += 1
                    if mismatch:
                        check.fail(mismatch)
                        return
                    if not compare_float(wk, glb.accessor(attributes[wk]), weights):
                        return
                check.data.setdefault('weightSumMaxError', {})[label] = max_sum_error
            elif any(k.startswith('JOINTS_') for k in attributes):
                check.fail(Mismatch(label, 'presence', 'GLB has JOINTS but the dump primitive has no influences'))
                return
            # Morph targets.
            targets = gprim.get('targets', [])
            if len(targets) != len(prim.morph_targets):
                check.fail(Mismatch(label + '/targets', 'count', 'GLB %d, dump %d' % (len(targets), len(prim.morph_targets))))
                return
            for ti, (gtarget, dtarget) in enumerate(zip(targets, prim.morph_targets)):
                tlabel = '%s/targets/%d' % (label, ti)
                expected_positions = dtarget['positionDeltas'] if dtarget['absolutePositions'] is None \
                    else rules.absolute_morph_deltas(prim.positions, dtarget['absolutePositions'])
                if not compare_float('targets/%d/POSITION' % ti, glb.accessor(gtarget['POSITION']), expected_positions):
                    return
                for key, values in (('NORMAL', dtarget['normalDeltas']), ('TANGENT', dtarget['tangentDeltas'])):
                    if values is None:
                        if key in gtarget:
                            check.fail(Mismatch(tlabel + '/' + key, 'presence', 'GLB target has %s, dump has none' % key))
                            return
                        continue
                    if key not in gtarget:
                        check.fail(Mismatch(tlabel + '/' + key, 'presence', 'absent'))
                        return
                    if not compare_float('targets/%d/%s' % (ti, key), glb.accessor(gtarget[key]), values):
                        return
    # Skins: joints and inverse bind matrices.
    gskins = glb.json.get('skins', [])
    dump_skin_names = [s.get('name') for s in dump.skins]
    cursor = 0
    for si, gskin in enumerate(gskins):
        hit = match_in_order(dump_skin_names, gskin.get('name'), cursor)
        if hit is None:
            check.fail(Mismatch('skins/%d' % si, 'name', 'no dump skin named %r' % gskin.get('name')))
            return
        cursor = hit + 1
        dskin = dump.skins[hit]
        if list(gskin['joints']) != list(dskin['jointNodeIndices']):
            check.fail(Mismatch('skins/%d' % si, 'joints', 'GLB %r, dump %r' % (gskin['joints'], dskin['jointNodeIndices'])))
            return
        if dskin.get('skeletonRootNodeIndex') is not None and gskin.get('skeleton') != dskin['skeletonRootNodeIndex']:
            check.fail(Mismatch('skins/%d' % si, 'skeleton', 'GLB %r, dump %r' % (gskin.get('skeleton'), dskin['skeletonRootNodeIndex'])))
            return
        binds = dskin.get('inverseBindMatrices') or []
        if dskin.get('bindMode') == 'JointLocalIdentity' and not binds:
            binds = [[1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]] * len(dskin['jointNodeIndices'])
        expected = np.array(binds, dtype=np.float64).astype(np.float32).reshape(-1, 16)
        if 'inverseBindMatrices' not in gskin:
            check.fail(Mismatch('skins/%d' % si, 'inverseBindMatrices', 'absent'))
            return
        if not compare_float('skins/%d/inverseBindMatrices' % si, glb.accessor(gskin['inverseBindMatrices']).reshape(-1, 16), expected):
            return
    check.data['streamsCompared'] = streams
    check.data['signedZeroOnlyDifferences'] = signed_zero_only
    check.data['colorsNotCompared'] = colors_not_compared
    check.ok('%d streams bit-exact after the declared conversions' % streams
             + (' (%d components differ only in the sign of zero, tolerated by request)' % signed_zero_only if signed_zero_only else '')
             + (', %d COLOR_0 streams not compared (blend route)' % len(colors_not_compared) if colors_not_compared else ''))


def check_materials(result, glb, dump, pairing):
    check = result.check('materials: alpha mode and cutoff, roughness, double-sided, factors')
    for gi, gmat in enumerate(glb.json.get('materials', [])):
        dmat = dump.materials[pairing.material_map[gi]]
        label = 'materials/%d (%s)' % (gi, gmat.get('name'))
        state = dmat.get('renderState')
        if rules.mask_rule_applies(state):
            raw = state['alphaTest']['rawReference']
            expected = rules.alpha_cutoff_eight_bit_greater(raw)
            if gmat.get('alphaMode') != 'MASK':
                check.fail(Mismatch(label + '/alphaMode', 'value', 'got %r, expected MASK for Greater(%d)' % (gmat.get('alphaMode'), raw)))
                return
            actual = f32(gmat.get('alphaCutoff', 0.5))
            if actual != expected:
                check.fail(Mismatch(label + '/alphaCutoff', 'value', 'got %r, expected (T+0.5)/255 = %r for T=%d' % (actual, expected, raw)))
                return
            check.data.setdefault('maskCutoff', {})[label] = {'raw': raw, 'cutoff': expected}
        source = dmat.get('source') or {}
        pbr = gmat.get('pbrMetallicRoughness') or {}
        gloss = source.get('glossiness')
        if gloss is not None and gloss >= 0 and not gmat.get('extensions', {}).get('KHR_materials_unlit'):
            expected = rules.roughness_from_glossiness(gloss)
            actual = f32(pbr.get('roughnessFactor', 1.0))
            if actual != expected:
                check.fail(Mismatch(label + '/roughnessFactor', 'value', 'got %r, expected sqrt(2/(g+2)) = %r for g=%r' % (actual, expected, gloss)))
                return
        if bool(gmat.get('doubleSided', False)) != rules.expected_double_sided(dmat):
            check.fail(Mismatch(label + '/doubleSided', 'value', 'got %r, expected %r' % (gmat.get('doubleSided', False), rules.expected_double_sided(dmat))))
            return
        base = [f32(v) for v in pbr.get('baseColorFactor', [1, 1, 1, 1])]
        if not bits_equal(base, [f32(v) for v in dmat.get('baseColor', [1, 1, 1, 1])]):
            check.fail(Mismatch(label + '/baseColorFactor', 'value', 'got %r, expected %r' % (base, dmat.get('baseColor'))))
            return
        metallic = f32(pbr.get('metallicFactor', 1.0))
        if metallic != f32(dmat.get('metallicFactor', 1.0) if dmat.get('metallicFactor') is not None else 1.0):
            check.fail(Mismatch(label + '/metallicFactor', 'value', 'got %r, expected %r' % (metallic, dmat.get('metallicFactor'))))
            return
    check.ok('%d materials' % len(glb.json.get('materials', [])))


def decode_payload(payload):
    """RGBA8 of one dump payload: ``dds_decode`` for a DDS container, Pillow otherwise. Returns (rgba, decode notes).
    A payload neither decoder can read raises OracleError (the oracle's limitation, recorded as such)."""
    container = (payload['container'] or '').lower()
    if 'dds' in container:
        return dds_decode.decode(payload['bytes'])
    from PIL import Image
    try:
        with Image.open(io.BytesIO(payload['bytes'])) as im:
            im.load()
            rgba = np.asarray(im.convert('RGBA'), dtype=np.uint8)
    except Exception as failure:  # noqa: BLE001 - Pillow's refusal is what the receipt must record
        raise OracleError('Pillow could not decode the %s payload: %s' % (container or 'unknown', failure)) from failure
    return rgba, {'format': container, 'decoder': 'pillow'}


def companion_packed(gmat, dmat):
    """Whether the writer packed the specular companion, on the writer's own evidence: the single non-alpha Specular
    layer exists (``rules.packed_specular_layer``) AND the GLB material binds KHR_materials_specular.specularTexture.
    ModelGltfNormalCompanion.Derive falls back to the strict summary, which binds no specular texture, when the
    candidate carries an issue on that layer; then the normal role must not pack. Returns (layer index, channel)."""
    layer_index, channel = rules.packed_specular_layer(dmat)
    if layer_index is None:
        return None, 'Alpha'
    binding = ((gmat.get('extensions') or {}).get('KHR_materials_specular') or {}).get('specularTexture')
    if binding is None:
        return None, 'Alpha'
    return layer_index, channel


def expected_image_pixels(dump, dmat, role, dump_image_index, packed=None):
    """The pixels the writer declares for one binding: decode, BC5 Z, and for the normal role (and the specular role
    when it rebinds to the prepared normal) the green inversion and companion packing. Returns (rgba, notes dict).

    ``packed`` is the (layer index, channel) pair from ``companion_packed`` when the caller holds the GLB material;
    None falls back to the layer shape alone (``rules.packed_specular_layer``)."""
    image = dump.images[dump_image_index]
    payload, selection = image.writer_selected_payload()
    if payload is None:
        raise OracleError('image %d has no payload (%s)' % (dump_image_index, selection))
    notes = {'selection': selection, 'container': payload['container'], 'sha256': payload['sha256']}
    rgba, notes['decode'] = decode_payload(payload)
    for transform in image.display_transforms:
        if transform.get('kind') == 'normalZReconstruction' and not transform.get('inputSigned'):
            rgba, observed = rules.reconstruct_normal_z(rgba, bool(transform.get('positiveZ')))
            notes['normalZObserved'] = observed
    if role in ('normal', 'specular') and dmat.get('normalTexture') is not None:
        green = (dmat.get('normalGreen') or (dmat.get('source') or {}).get('normalGreen') or {})
        green_down = green.get('green') == 'Down'
        layer_index, channel = packed if packed is not None else rules.packed_specular_layer(dmat)
        companion = None
        if layer_index is not None:
            binding = dmat['source']['layers'][layer_index]['binding']
            companion_image = dump.images[binding['imageIndex']]
            cpayload, cselection = companion_image.writer_selected_payload()
            if cpayload is None:
                raise OracleError('companion image %d has no payload (%s)' % (binding['imageIndex'], cselection))
            companion, _ = decode_payload(cpayload)
            for transform in companion_image.display_transforms:
                if transform.get('kind') == 'normalZReconstruction' and not transform.get('inputSigned'):
                    companion, _ = rules.reconstruct_normal_z(companion, bool(transform.get('positiveZ')))
            notes['companion'] = {'imageIndex': binding['imageIndex'], 'channel': channel}
        specular_index = (dmat.get('specularTexture') or {}).get('imageIndex')
        if role == 'specular' and layer_index is None and specular_index != dmat['normalTexture'].get('imageIndex'):
            return rgba, notes  # an independent specular image: no normal preparation applies
        if green_down or companion is not None:
            rgba = rules.prepare_normal_map(rgba, green_down, companion, channel)
            notes['greenInverted'] = green_down
    return rgba, notes


def check_images(result, glb, dump, pairing):
    """Every GLB texture binding's PNG against the expectation built from the dump. A binding whose expectation the
    oracle cannot build is a FAILED check (kind ``expectation``), never a silent skip; blend-route materials, whose
    images the writer derives, are listed as not compared and counted in the detail."""
    check = result.check('images: GLB PNG versus the Pillow decode of the dump payload (within 1/255)')
    from PIL import Image
    compared = 0
    worst = 0
    details = []
    for gi, gmat in enumerate(glb.json.get('materials', [])):
        dmat = dump.materials[pairing.material_map[gi]]
        if rules.blend_plan_applies(dmat):
            details.append({'material': gi, 'note': 'blend route: the transmission-route derived images are not reproduced'})
            continue
        packed = companion_packed(gmat, dmat)
        for role, getter, dump_key in GLB_BINDINGS:
            binding = getter(gmat)
            if binding is None:
                continue
            dbinding = dmat.get(dump_key)
            record = {'material': gi, 'role': role}
            if role == 'specular' and packed[0] is not None:
                # ModelGltfMaterials.Clone (line 440, packSpecular): the specular binding is remapped to the prepared
                # normal PNG; the dump's strict summary binds the companion, or nothing, as specularTexture.
                dump_image = dmat['normalTexture']['imageIndex']
                normal_binding = gmat.get('normalTexture')
                if normal_binding is None or glb.texture_image(binding['index'])[0] != glb.texture_image(normal_binding['index'])[0]:
                    check.fail(Mismatch('materials/%d/specular' % gi, 'binding', 'packed companion: the specular texture must be the prepared normal PNG'))
                    check.data['images'] = details
                    return
                record['packedCompanion'] = True
                record['dumpSpecularImage'] = (dbinding or {}).get('imageIndex')
            elif dbinding is None:
                check.fail(Mismatch('materials/%d/%s' % (gi, role), 'binding', 'GLB binds a texture, the dump material has none'))
                check.data['images'] = details
                return
            else:
                dump_image = dbinding['imageIndex']
            source_index, _ = glb.texture_image(binding['index'])
            png, mime = glb.image_bytes(source_index)
            if mime != 'image/png' or not png.startswith(b'\x89PNG\r\n\x1a\n'):
                check.fail(Mismatch('images/%d' % source_index, 'container', 'mimeType %r; the writer embeds PNG only' % mime))
                check.data['images'] = details
                return
            with Image.open(io.BytesIO(png)) as im:
                im.load()
                actual = np.asarray(im.convert('RGBA'), dtype=np.int16)
            record.update({'glbImage': source_index, 'dumpImage': dump_image, 'name': dump.images[dump_image].name})
            try:
                expected, notes = expected_image_pixels(dump, dmat, role, dump_image, packed=packed)
            except OracleError as failure:
                record['note'] = 'expectation unavailable: %s' % failure
                details.append(record)
                check.fail(Mismatch('materials/%d/%s' % (gi, role), 'expectation', 'binding not compared (dump image %d, %s): %s'
                                    % (dump_image, dump.images[dump_image].name, failure)))
                continue
            if actual.shape != expected.shape:
                check.fail(Mismatch('materials/%d/%s' % (gi, role), 'shape', 'PNG %s, expected %s' % (actual.shape, expected.shape)))
                check.data['images'] = details
                return
            diff = np.abs(actual - expected.astype(np.int16))
            per_channel = [int(diff[:, :, c].max()) for c in range(4)]
            differing = int((diff > 0).any(axis=2).sum())
            record.update({'maxDifferencePerChannel': per_channel, 'differingPixels': differing, 'notes': notes})
            details.append(record)
            compared += 1
            worst = max(worst, max(per_channel))
            if max(per_channel) > 1:
                check.fail(Mismatch('materials/%d/%s' % (gi, role), 'pixels', 'max per-channel difference %r on %d pixels (bound 1/255)' % (per_channel, differing)))
                check.data['images'] = details
                return
    not_compared = sum(1 for entry in details if 'note' in entry)
    check.data['images'] = details
    check.data['maxDifference'] = worst
    check.data['bindingsCompared'] = compared
    check.data['bindingsNotCompared'] = not_compared
    summary = '%d bindings compared, %d not compared' % (compared, not_compared)
    if check.passed is False:
        check.detail = summary
    elif compared == 0:
        # Reachable only by an untextured mesh or by materials that are all on the blend route: an unavailable
        # expectation has already failed the check above.
        check.skip('no textured material binding compared (%s)' % summary)
    else:
        check.ok('%s, max per-channel difference %d/255' % (summary, worst))


def load_fidelity_rows(fidelity_path):
    """Rows of the glb writer from ``mesh fidelity --format glb --json`` (schema multitool.mesh-info/1)."""
    with open(fidelity_path, 'rb') as f:
        document = json_loads(f.read().decode('utf-8'))
    writers = document.get('writers') or []
    for writer in writers:
        if writer.get('format') == 'glb':
            if writer.get('failure'):
                raise OracleError('mesh fidelity reports the glb writer failed: %s' % writer['failure'])
            fidelity = writer.get('fidelity') or {}
            return fidelity.get('rows') or [], fidelity, document
    raise OracleError('the fidelity JSON has no glb writer entry')


def row_key(row):
    target = row.get('target') or {}
    return (target.get('kind'), target.get('index'), target.get('primitiveIndex'), target.get('morphTargetIndex'),
            row.get('featureId'), row.get('outcome'), row.get('reasonCode'))


def expected_normals_row(prim):
    """The geometry/normals row ModelGltfGeometry.NormalizeNormals emits for one dump primitive (lines 167-241 at pinned
    Shared 591d083) and the zero-lane counts it declares. Flat mode skips the vertex scan (line 180):
    geometry.normals-preserved. Any zero lane takes the ``if (invalid)`` branch (lines 195-228) INSTEAD of the normalized
    or preserved rows:
    geometry.unreferenced-normals-filled when no zero lane is referenced (reconstructedCount == 0, line 222), else
    geometry.zero-normals-reconstructed; a referenced zero lane whose incident area sum is zero is
    geometry.normal-direction-absent and no GLB is written. Without a zero lane the row is geometry.normals-normalized
    when any lane changed (line 230) and geometry.normals-preserved otherwise (line 235). The two families are mutually
    exclusive, and a reconstruction row always declares M + N >= 1.

    Returns (reason code, referenced zero count M, unreferenced zero count N, changed flag)."""
    if prim.normal_mode == 'Flat':
        return 'geometry.normals-preserved', 0, 0, False
    zero_mask, referenced = zero_normal_lanes(prim)
    m = int((zero_mask & referenced).sum())
    n = int((zero_mask & ~referenced).sum())
    _, _, changed, _ = rules.normalize_normals(prim.normals, prim.normal_mode)
    if m + n:
        return ('geometry.unreferenced-normals-filled' if m == 0 else 'geometry.zero-normals-reconstructed'), m, n, changed
    return ('geometry.normals-normalized' if changed else 'geometry.normals-preserved'), 0, 0, changed


# The tangent rows of ModelGltfGeometry.NormalizeTangents (lines 299-321, pinned Shared 591d083) and the counts their
# descriptions interpolate (lines 308 and 315: "Filled {unreferencedCount} unused zero-tangent slots with UnitX ...",
# "Exactly {unreferencedCount} zero-tangent vertex slot(s) have no index references ...").
TANGENT_ROW_CODES = ('geometry.tangents-normalized', 'geometry.tangents-preserved', 'geometry.tangent-basis-unsupported',
                     'geometry.unreferenced-tangents-filled', 'geometry.unreferenced-tangent-placeholders')
TANGENT_ROW_FEATURES = ('geometry/tangents', 'geometry/unreferenced-tangent-slots', 'geometry/tangent-normalization')
FILLED_TANGENTS_TEXT = re.compile(r'Filled (\d+) unused zero-tangent slots')
PLACEHOLDER_TANGENTS_TEXT = re.compile(r'Exactly (\d+) zero-tangent vertex slot\(s\)')


def check_fidelity(result, glb, fidelity_path, dump=None, pairing=None):
    check = result.check('fidelity rows: report versus GLB carrier and declared values')
    rows, fidelity, document = load_fidelity_rows(fidelity_path)
    carrier = ((glb.json.get('extras') or {}).get('multitoolFidelity') or {}).get('rows')
    if carrier is None:
        check.fail(Mismatch('extras/multitoolFidelity', 'presence', 'absent from the GLB'))
        return
    # Rows are kept as a LIST of keys, never a dict keyed by row_key: a dict collapses a duplicated row to its last copy,
    # so a report carrying a row twice (or a wrong-count filled row ahead of the right one) would read as one agreeing
    # row. The report and the carrier are compared as multisets, every row is judged below, and the row-set checks
    # after the loop count occurrences (ModelGltfGeometry.NormalizeNormals / NormalizeTangents / LowerMorphs add each
    # of their featureIds at most once per target: lines 195-239, 299-321 and 434-441 at pinned Shared 591d083).
    report_list = []
    for row in rows:
        if row.get('isPending'):
            check.fail(Mismatch('fidelity/%s' % row.get('featureId'), 'pending', 'a resolved report still carries a pending row'))
            return
        report_list.append(row_key(row))
    carrier_list = [row_key(row) for row in carrier]
    report_counts = collections.Counter(report_list)
    carrier_counts = collections.Counter(carrier_list)

    def surplus(side, other):
        # A key absent from the other side is listed bare; a key present on both sides with fewer copies there is
        # listed with both counts.
        return [key if other.get(key, 0) == 0 else (key, 'x%d against x%d' % (side[key], other[key]))
                for key in side if side[key] > other.get(key, 0)]

    missing = surplus(report_counts, carrier_counts)
    extra = surplus(carrier_counts, report_counts)
    if missing or extra:
        check.fail(Mismatch('fidelity/rows', 'set', 'rows in the report but not the GLB: %r; in the GLB but not the report: %r' % (missing[:3], extra[:3])))
        return
    carrier_rows = collections.defaultdict(list)
    for row in carrier:
        carrier_rows[row_key(row)].append(row)
    paired = collections.Counter()
    for key, row in zip(report_list, rows):
        rb = row.get('bounds') or {}
        cb = carrier_rows[key][paired[key]].get('bounds') or {}  # the n-th report copy against the n-th carrier copy
        paired[key] += 1
        if (rb.get('observedError'), rb.get('maximumError')) != (cb.get('observedError'), cb.get('maximumError')):
            check.fail(Mismatch('fidelity/%s' % key[4], 'bounds', 'report %r, GLB %r' % (rb, cb)))
            return
    values = []
    check.data['declaredValues'] = values  # live: a failure keeps the rows judged so far, including its diagnostics
    nodes = glb.json.get('nodes', [])
    parents = [n for n in nodes if str(n.get('name', '')).startswith('multitool coordinates/')]
    for row in rows:
        code = row.get('reasonCode')
        if code == 'coordinates.units-float32':
            match = re.search(r'stored float32 factor ([-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)', row.get('description', ''))
            declared = f32(float(match.group(1))) if match else None
            found = any(abs(f32(p['matrix'][0])) == declared or abs(f32(p['matrix'][1])) == declared or abs(f32(p['matrix'][2])) == declared
                        for p in parents if 'matrix' in p)
            values.append({'row': code, 'declared': declared, 'present': found})
            if not found:
                check.fail(Mismatch('fidelity/coordinates/units', 'value', 'declared factor %r not on any coordinate parent' % declared))
                return
        elif code == 'material.alpha-eight-bit-greater' and dump is not None:
            index = row['target']['index']
            state = dump.materials[index].get('renderState') or {}
            raw = state['alphaTest']['rawReference']
            declared = rules.alpha_cutoff_eight_bit_greater(raw)
            gmats = [g for g, d in pairing.material_map.items() if d == index]
            found = all(glb.json['materials'][g].get('alphaMode') == 'MASK' and f32(glb.json['materials'][g].get('alphaCutoff', 0.5)) == declared for g in gmats)
            values.append({'row': code, 'material': index, 'declared': declared, 'present': found})
            if not found or not gmats:
                check.fail(Mismatch('fidelity/material/%d/alpha' % index, 'value', 'declared MASK cutoff %r (T=%d) not on the GLB material' % (declared, raw)))
                return
        elif code == 'material.glossiness-to-roughness' and dump is not None:
            index = row['target']['index']
            gloss = dump.materials[index]['source']['glossiness']
            declared = rules.roughness_from_glossiness(gloss)
            gmats = [g for g, d in pairing.material_map.items() if d == index]
            found = all(f32((glb.json['materials'][g].get('pbrMetallicRoughness') or {}).get('roughnessFactor', 1.0)) == declared for g in gmats)
            values.append({'row': code, 'material': index, 'declared': declared, 'present': found})
            if not found or not gmats:
                check.fail(Mismatch('fidelity/material/%d/glossiness' % index, 'value', 'declared roughness %r not on the GLB material' % declared))
                return
        elif code in ('geometry.normals-preserved', 'geometry.normal-direction-absent') and dump is not None:
            target = row['target']
            prim = dump.meshes[target['index']]['primitives'][target['primitiveIndex']]
            expected_code, expected_m, expected_n, changed = expected_normals_row(prim)
            values.append({'row': code, 'target': target, 'expectedRow': expected_code, 'measuredReconstructed': expected_m,
                           'measuredFilled': expected_n, 'changed': changed, 'present': code == expected_code and row.get('outcome') == 'Exact'})
            if code == 'geometry.normal-direction-absent':
                check.fail(Mismatch('fidelity/geometry/normals', 'unsupported', 'the report declares a referenced zero normal unreconstructible (no GLB is written) but a GLB exists; the oracle counts %d referenced and %d unreferenced zero lanes'
                                    % (expected_m, expected_n)))
                return
            if code != expected_code or row.get('outcome') != 'Exact':
                check.fail(Mismatch('fidelity/geometry/normals', 'row', 'report has %s (%s), the normals rule expects %s Exact (%d referenced and %d unreferenced zero lanes, changed %s)'
                                    % (code, row.get('outcome'), expected_code, expected_m, expected_n, changed)))
                return
        elif code == 'geometry.normals-normalized' and dump is not None:
            target = row['target']
            prim = dump.meshes[target['index']]['primitives'][target['primitiveIndex']]
            expected_code, expected_m, expected_n, _ = expected_normals_row(prim)
            if code != expected_code:
                # A zero lane takes the reconstruction branch instead (ModelGltfGeometry line 195); the writer never
                # normalizes and reconstructs on one primitive.
                values.append({'row': code, 'target': target, 'expectedRow': expected_code, 'measuredReconstructed': expected_m,
                               'measuredFilled': expected_n, 'present': False})
                check.fail(Mismatch('fidelity/geometry/normals', 'row', 'report has %s, the normals rule expects %s (%d referenced and %d unreferenced zero lanes take the reconstruction branch, not normalization)'
                                    % (code, expected_code, expected_m, expected_n)))
                return
            _, measured, _, _ = rules.normalize_normals(prim.normals, prim.normal_mode)
            bounds = row.get('bounds') or {}
            declared = bounds.get('observedError')
            close = declared is not None and abs(declared - measured) <= 1e-9
            expected_outcome = 'Approximated' if measured <= 0.1 else 'Degraded'
            entry = {'row': code, 'target': target, 'declared': declared, 'measured': measured, 'present': close,
                     'outcome': row.get('outcome'), 'expectedOutcome': expected_outcome}
            values.append(entry)
            if not close:
                # Diagnostics only: the previous pin (6c94992) measured this row with the acos form. A declared value
                # that equals it names the writer that produced the report; the expectation stays the pinned atan2 form.
                previous = rules.previous_pin_normal_degrees(prim.normals, prim.normal_mode)
                entry['measuredPreviousPinAcosForm'] = previous
                from_previous = declared is not None and abs(declared - previous) <= 1e-9
                entry['declaredMatchesPreviousPin'] = from_previous
                check.fail(Mismatch('fidelity/geometry/normals', 'value', 'declared %r degrees, measured %r (atan2 formula, since Shared 0fe3212)%s' % (
                    declared, measured, ('; the declared value equals the previous pin\'s acos-form measurement %r, so this report was written by the previous writer (6c94992)' % previous)
                    if from_previous else '; the previous pin\'s acos form gives %r, which does not match either' % previous)))
                return
            if bounds.get('maximumError') != 0.1 or row.get('outcome') != expected_outcome:
                check.fail(Mismatch('fidelity/geometry/normals', 'outcome', 'outcome %r with bound %r; %r degrees against the 0.1 threshold expects %s'
                                    % (row.get('outcome'), bounds.get('maximumError'), measured, expected_outcome)))
                return
        elif code in TANGENT_ROW_CODES and dump is not None:
            # ModelGltfGeometry.NormalizeTangents (pinned Shared 591d083): the row must be the one
            # writer_rules.expected_tangent_rows derives for this featureId, with its outcome, its bound, and for the
            # two placeholder rows the declared count equal to the oracle's count of unreferenced zero-direction lanes.
            target = row['target']
            feature = row.get('featureId')
            where = 'fidelity/%s' % feature
            prim = dump.meshes[target['index']]['primitives'][target['primitiveIndex']]
            if prim.tangents is None:
                check.fail(Mismatch(where, 'presence', 'row %s on a primitive whose dump has no tangents' % code))
                return
            plan = rules.tangent_plan(prim.tangents, prim.indices)
            measured = plan['maxDegrees']
            expected = next((r for r in plan['rows'] if r['featureId'] == feature), None)
            expected_code = expected['reasonCode'] if expected is not None else None
            counts = '%d invalid lanes, %d normalized, %d unreferenced zero-direction lanes, changed %s, %r degrees' % (
                plan['invalidCount'], plan['normalizedCount'], plan['unreferencedCount'], plan['changed'], measured)
            entry = {'row': code, 'featureId': feature, 'target': target, 'measured': measured, 'changed': plan['changed'],
                     'invalidLanes': plan['invalidCount'], 'normalizedLanes': plan['normalizedCount'], 'placeholderLanes': plan['unreferencedCount'],
                     'expectedRow': expected_code, 'expectedRows': [[r['featureId'], r['reasonCode']] for r in plan['rows']],
                     'present': code == expected_code}
            values.append(entry)
            if code == 'geometry.tangent-basis-unsupported':
                check.fail(Mismatch(where, 'unsupported', 'the report declares the tangent basis unsupported (no GLB is written) but a GLB exists; the oracle counts %d invalid lane(s)' % plan['invalidCount']))
                return
            if code != expected_code:
                check.fail(Mismatch(where, 'row', 'report has %s under %s, the tangent rule expects %s there (%s; expected rows %r)'
                                    % (code, feature, expected_code, counts, entry['expectedRows'])))
                return
            if code == 'geometry.tangents-normalized':
                bounds = row.get('bounds') or {}
                declared = bounds.get('observedError')
                close = declared is not None and abs(declared - measured) <= 1e-9
                expected_outcome = expected['outcome']
                entry.update({'declared': declared, 'present': close, 'outcome': row.get('outcome'), 'expectedOutcome': expected_outcome})
                if not close:
                    check.fail(Mismatch(where, 'value', 'declared %r degrees, measured %r (atan2 formula)' % (declared, measured)))
                    return
                if bounds.get('maximumError') != rules.MAXIMUM_TANGENT_DEGREES or row.get('outcome') != expected_outcome:
                    check.fail(Mismatch(where, 'outcome', 'outcome %r with bound %r; %r degrees against the 0.1 threshold expects %s'
                                        % (row.get('outcome'), bounds.get('maximumError'), measured, expected_outcome)))
                    return
                continue
            if row.get('outcome') != expected['outcome'] or row.get('bounds') is not None:
                check.fail(Mismatch(where, 'outcome', 'row %s is %r with bounds %r; the tangent rule expects %s with no bound'
                                    % (code, row.get('outcome'), row.get('bounds'), expected['outcome'])))
                return
            if code in ('geometry.unreferenced-tangents-filled', 'geometry.unreferenced-tangent-placeholders'):
                pattern = FILLED_TANGENTS_TEXT if code == 'geometry.unreferenced-tangents-filled' else PLACEHOLDER_TANGENTS_TEXT
                match = pattern.search(row.get('description', ''))
                declared = int(match.group(1)) if match else None
                agree = declared is not None and declared == expected['count'] == plan['unreferencedCount'] and declared > 0
                entry.update({'declaredCount': declared, 'measuredCount': plan['unreferencedCount'], 'present': agree})
                if not agree:
                    check.fail(Mismatch(where, 'count', 'row %s declares %r unused zero-tangent slot(s) (pattern %r); the oracle counts %d unreferenced zero-direction lanes'
                                        % (code, declared, pattern.pattern, plan['unreferencedCount'])))
                    return
        elif code in ('geometry.unreferenced-normals-filled', 'geometry.zero-normals-reconstructed') and dump is not None:
            target = row['target']
            prim = dump.meshes[target['index']]['primitives'][target['primitiveIndex']]
            # expected_code is a normalized or preserved code when the primitive has no zero lane: the writer emits a
            # reconstruction row only inside its zero-lane branch (ModelGltfGeometry line 195), so a row declaring
            # 'Reconstructed 0 ... Filled 0' there is a row mismatch, never an agreeing count.
            expected_code, expected_m, expected_n, _ = expected_normals_row(prim)
            reconstructed = re.search(r'Reconstructed (\d+) zero normal\(s\)', row.get('description', ''))
            filled = re.search(r'Filled (\d+) unused zero-normal slots', row.get('description', ''))
            declared_m = int(reconstructed.group(1)) if reconstructed else None
            declared_n = int(filled.group(1)) if filled else None
            agree = (declared_m, declared_n) == (expected_m, expected_n) and code == expected_code and row.get('outcome') == 'Degraded'
            values.append({'row': code, 'target': target, 'declaredReconstructed': declared_m, 'declaredFilled': declared_n,
                           'measuredReconstructed': expected_m, 'measuredFilled': expected_n, 'expectedRow': expected_code, 'present': agree})
            if not agree:
                check.fail(Mismatch('fidelity/geometry/normals', 'count', 'row %s (%s) declares %r reconstructed and %r filled; the oracle counts %d referenced and %d unreferenced zero lanes (expects %s)'
                                    % (code, row.get('outcome'), declared_m, declared_n, expected_m, expected_n, expected_code)))
                return
        elif code == 'geometry.unreferenced-normal-placeholders' and dump is not None:
            target = row['target']
            prim = dump.meshes[target['index']]['primitives'][target['primitiveIndex']]
            zero_mask, referenced = zero_normal_lanes(prim)
            expected_n = int((zero_mask & ~referenced).sum())
            match = re.search(r'Exactly (\d+) zero-normal vertex slot\(s\)', row.get('description', ''))
            declared_n = int(match.group(1)) if match else None
            agree = declared_n == expected_n and expected_n > 0 and row.get('outcome') == 'Degraded'
            values.append({'row': code, 'target': target, 'declaredFilled': declared_n, 'measuredFilled': expected_n, 'present': agree})
            if not agree:
                check.fail(Mismatch('fidelity/geometry/unreferenced-normal-slots', 'count', 'row declares %r placeholder slot(s) (%s); the oracle counts %d unreferenced zero lanes'
                                    % (declared_n, row.get('outcome'), expected_n)))
                return
        elif code == 'geometry.clockwise-front-reversed' and dump is not None:
            values.append({'row': code, 'target': row['target'], 'present': True, 'note': 'checked by the accessor comparison (indices reversed)'})
        elif code in ('normal-map.green-inverted', 'normal-map.scalar-packed', 'image.normal-z-quantized'):
            values.append({'row': code, 'target': row['target'], 'present': True, 'note': 'checked by the image comparison'})
    if dump is not None:
        # ModelGltfGeometry.LowerMorphs lines 438-441 (pinned Shared 591d083): the morph-tangent row exists exactly when
        # the base tangents changed (NormalizeTangents' ``changed``: a normalized lane or, since 1c2f5b7, a filled
        # unreferenced zero-direction lane, lines 277 and 287) and the target carries tangent deltas. Judged on every
        # primitive the writer evaluated (one that has a tangent row: geometry/tangents, or since 591d083
        # geometry/unreferenced-tangent-slots or geometry/tangent-normalization, so a report missing only its
        # geometry/tangents row is caught by the row-set check below, not here), over every morph target of its dump
        # primitive. The row is added at most once per target (lines 438-441), so a target carrying it twice fails too.
        evaluated = sorted({(k[1], k[2]) for k in report_list if k[0] == 'Primitive' and k[4] in TANGENT_ROW_FEATURES})
        morph_list = [(k[1], k[2], k[3]) for k in report_list
                      if k[0] == 'MorphTarget' and k[4] == 'geometry/morph-tangent-deformation' and k[6] == 'geometry.normalized-base-tangent-morph-unbounded']
        morph_rows = set(morph_list)
        morph_twice = sorted(k for k, n in collections.Counter(morph_list).items() if n > 1)
        expected_rows = set()
        targets_judged = 0
        for mi, pi in evaluated:
            prim = dump.meshes[mi]['primitives'][pi]
            changed = prim.tangents is not None and rules.tangent_plan(prim.tangents, prim.indices)['changed']
            for ti, dtarget in enumerate(prim.morph_targets):
                targets_judged += 1
                if changed and dtarget['tangentDeltas'] is not None:
                    expected_rows.add((mi, pi, ti))
        entry = {'row': 'geometry.normalized-base-tangent-morph-unbounded', 'morphTargetsJudged': targets_judged,
                 'expected': sorted(expected_rows), 'reported': sorted(morph_rows), 'present': expected_rows == morph_rows and not morph_twice}
        if morph_twice:
            entry['reportedMoreThanOnce'] = morph_twice
        values.append(entry)
        if expected_rows != morph_rows:
            check.fail(Mismatch('fidelity/geometry/morph-tangent-deformation', 'set', 'the rule expects the row on targets %r, the report carries it on %r'
                                % (sorted(expected_rows - morph_rows), sorted(morph_rows - expected_rows))))
            return
        if morph_twice:
            check.fail(Mismatch('fidelity/geometry/morph-tangent-deformation', 'set', 'the report carries the row more than once on targets %r; '
                                'the writer adds it at most once per target' % morph_twice))
            return
        # ModelGltfGeometry.NormalizeTangents emits exactly the rows writer_rules.expected_tangent_rows derives (lines
        # 299-321): one geometry/tangents row, the geometry/unreferenced-tangent-slots row exactly when N > 0, and the
        # normalization row under geometry/tangent-normalization when N > 0 (under geometry/tangents otherwise). Judged
        # as a set of (featureId, reason code, outcome) on every primitive that has any tangent row AND on every dump
        # primitive with tangents that a GLB primitive maps to (the writer lowered it), so a missing placeholder,
        # filled or normalization row fails although no such row is present to be judged above. ``reported`` keeps
        # every occurrence (a list, not a set), so a row the report carries twice fails as well: each featureId is added
        # at most once per primitive.
        tangent_evaluated = {(k[1], k[2]) for k in report_list if k[0] == 'Primitive' and k[4] in TANGENT_ROW_FEATURES}
        if pairing is not None:
            tangent_evaluated |= {mapped for mapped in pairing.primitive_map.values()
                                  if dump.meshes[mapped[0]]['primitives'][mapped[1]].tangents is not None}
        tangent_placeholder_primitives = []
        for mi, pi in sorted(tangent_evaluated):
            prim = dump.meshes[mi]['primitives'][pi]
            if prim.tangents is None:
                continue  # a tangent row on a primitive without tangents already failed above
            plan = rules.tangent_plan(prim.tangents, prim.indices)
            expected_set = sorted((r['featureId'], r['reasonCode'], r['outcome']) for r in plan['rows'])
            reported = sorted((k[4], k[6], k[5]) for k in report_list
                              if k[0] == 'Primitive' and k[1] == mi and k[2] == pi and k[4] in TANGENT_ROW_FEATURES)
            if reported != expected_set:
                values.append({'row': 'geometry/tangents (row set)', 'target': {'index': mi, 'primitiveIndex': pi},
                               'expected': expected_set, 'reported': reported, 'present': False})
                check.fail(Mismatch('fidelity/geometry/tangents', 'set', 'meshes/%d/primitives/%d carries %r, the tangent rule expects exactly %r '
                                    '(%d invalid, %d normalized, %d unreferenced zero-direction lanes)'
                                    % (mi, pi, reported, expected_set, plan['invalidCount'], plan['normalizedCount'], plan['unreferencedCount'])))
                return
            if plan['unreferencedCount']:
                tangent_placeholder_primitives.append([mi, pi, plan['unreferencedCount']])
        values.append({'row': 'geometry/tangents (row set)', 'primitivesJudged': len(tangent_evaluated),
                       'placeholderPrimitives': tangent_placeholder_primitives, 'present': True})
        # ModelGltfGeometry.NormalizeNormals emits exactly one geometry/normals row per evaluated primitive and the
        # geometry/unreferenced-normal-slots row exactly when N > 0 (lines 195-239). Judged on every primitive that has
        # a geometry/normals row, so a normalized or preserved row on a primitive with a zero lane, a reconstruction row
        # on one without, or an ABSENT placeholder row fails even though no such row is present to be judged above. Both
        # rows are added at most once per primitive, so every occurrence is counted: a duplicated geometry/normals row
        # fails the exactly-one comparison and a duplicated placeholder row fails below.
        evaluated = sorted({(k[1], k[2]) for k in report_list if k[0] == 'Primitive' and k[4] == 'geometry/normals'})
        placeholder_list = [(k[1], k[2]) for k in report_list if k[0] == 'Primitive' and k[4] == 'geometry/unreferenced-normal-slots']
        placeholder_rows = set(placeholder_list)
        placeholder_twice = sorted(k for k, n in collections.Counter(placeholder_list).items() if n > 1)
        expected_placeholders = set()
        for mi, pi in evaluated:
            prim = dump.meshes[mi]['primitives'][pi]
            expected_code, expected_m, expected_n, _ = expected_normals_row(prim)
            reported = sorted(k[6] for k in report_list if k[0] == 'Primitive' and k[1] == mi and k[2] == pi and k[4] == 'geometry/normals')
            if reported != [expected_code]:
                values.append({'row': 'geometry/normals', 'target': {'index': mi, 'primitiveIndex': pi}, 'expectedRow': expected_code,
                               'reported': reported, 'present': False})
                check.fail(Mismatch('fidelity/geometry/normals', 'row', 'meshes/%d/primitives/%d carries %r, the normals rule expects exactly [%s] (%d referenced and %d unreferenced zero lanes)'
                                    % (mi, pi, reported, expected_code, expected_m, expected_n)))
                return
            if expected_n > 0:
                expected_placeholders.add((mi, pi))
        entry = {'row': 'geometry.unreferenced-normal-placeholders', 'primitivesJudged': len(evaluated),
                 'expected': sorted(expected_placeholders), 'reported': sorted(placeholder_rows),
                 'present': expected_placeholders == placeholder_rows and not placeholder_twice}
        if placeholder_twice:
            entry['reportedMoreThanOnce'] = placeholder_twice
        values.append(entry)
        if expected_placeholders != placeholder_rows:
            check.fail(Mismatch('fidelity/geometry/unreferenced-normal-slots', 'set', 'the rule expects the placeholder row on primitives %r, the report carries it on %r'
                                % (sorted(expected_placeholders - placeholder_rows), sorted(placeholder_rows - expected_placeholders))))
            return
        if placeholder_twice:
            check.fail(Mismatch('fidelity/geometry/unreferenced-normal-slots', 'set', 'the report carries the placeholder row more than once on primitives %r; '
                                'the writer adds it at most once per primitive' % placeholder_twice))
            return
    check.data['declaredValues'] = values
    check.data['rowCount'] = len(rows)
    check.data['summary'] = {k: fidelity.get(k) for k in ('exact', 'converted', 'approximated', 'degraded', 'metadata', 'dropped', 'pending')}
    check.ok('%d rows agree with the GLB carrier; %d declared values checked' % (len(rows), len(values)))


def mutate_alpha_cutoff(glb_bytes, material_index, cutoff):
    """A copy of the GLB whose JSON chunk carries alphaCutoff = cutoff on one material (BIN chunk unchanged)."""
    glb = Glb(glb_bytes)
    document = json.loads(glb.json_bytes.decode('utf-8'))
    document['materials'][material_index]['alphaCutoff'] = cutoff
    return rebuild_glb(document, glb.bin)


def rebuild_glb(document, bin_chunk):
    json_bytes = json.dumps(document, separators=(',', ':')).encode('utf-8')
    json_bytes += b' ' * ((4 - len(json_bytes) % 4) % 4)
    bin_chunk = bytes(bin_chunk) + b'\0' * ((4 - len(bin_chunk) % 4) % 4)
    total = 12 + 8 + len(json_bytes) + (8 + len(bin_chunk) if bin_chunk else 0)
    out = struct.pack('<4sII', b'glTF', 2, total) + struct.pack('<I4s', len(json_bytes), b'JSON') + json_bytes
    if bin_chunk:
        out += struct.pack('<I4s', len(bin_chunk), b'BIN\0') + bin_chunk
    return out


def mutate_uv_shift(glb_bytes, texel):
    """A copy of the GLB with every TEXCOORD_0 u component shifted by one texel (1/width of the base texture)."""
    glb = Glb(glb_bytes)
    bin_chunk = bytearray(glb.bin)
    touched = 0
    for mesh in glb.json['meshes']:
        for primitive in mesh['primitives']:
            index = primitive['attributes'].get('TEXCOORD_0')
            if index is None:
                continue
            accessor = glb.json['accessors'][index]
            view = glb.json['bufferViews'][accessor['bufferView']]
            stride = view.get('byteStride', 8)
            base = view.get('byteOffset', 0) + accessor.get('byteOffset', 0)
            for vertex in range(accessor['count']):
                offset = base + vertex * stride
                u = struct.unpack_from('<f', bin_chunk, offset)[0]
                struct.pack_into('<f', bin_chunk, offset, np.float32(u + texel))
                touched += 1
    return rebuild_glb(json.loads(glb.json_bytes.decode('utf-8')), bytes(bin_chunk)), touched


def accessor_element_offset(glb, accessor_index, element):
    """The absolute BIN offset of one element of a bufferView-backed accessor (interleaved views through byteStride)."""
    accessor = glb.json['accessors'][accessor_index]
    view = glb.json['bufferViews'][accessor['bufferView']]
    from glb_reader import COMPONENT_DTYPES, TYPE_COMPONENTS
    element_bytes = COMPONENT_DTYPES[accessor['componentType']].itemsize * TYPE_COMPONENTS[accessor['type']]
    stride = view.get('byteStride') or element_bytes
    return view.get('byteOffset', 0) + accessor.get('byteOffset', 0) + element * stride


def tangent_placeholder_lanes(dump, pairing, gi, pi):
    """The unreferenced zero-direction tangent lanes (``writer_rules.tangent_plan``, the (1, 0, 0, w) placeholders) of
    the dump primitive GLB primitive (gi, pi) maps to, or None when it maps to none, carries no tangents, or its
    indices leave the vertex domain (the writer throws there; the accessor check reports it)."""
    if dump is None or pairing is None or (gi, pi) not in pairing.primitive_map:
        return None
    di, dpi = pairing.primitive_map[(gi, pi)]
    prim = dump.meshes[di]['primitives'][dpi]
    if prim.tangents is None:
        return None
    try:
        return rules.tangent_plan(prim.tangents, prim.indices)['placeholder']
    except ValueError:
        return None


def mutate_tangent_w(glb_bytes, dump=None, pairing=None):
    """A copy of the GLB with the handedness sign (w) of one TANGENT lane of the first tangent-bearing primitive flipped
    (its sign bit inverted, so +1 becomes -1 and -1 becomes +1): lane 0, or, when the dump says the primitive carries
    tangent placeholders (``tangent_placeholder_lanes``), the first lane that is NOT one, so the flip lands in the
    authored lanes the ``TANGENT`` stream compares (a flip on a placeholder lane is reported on the placeholder stream,
    which is the other control's). Returns (mutated bytes, description) or (None, None) when no primitive carries a
    TANGENT accessor with such a lane."""
    glb = Glb(glb_bytes)
    for gi, mesh in enumerate(glb.json.get('meshes', [])):
        for pi, primitive in enumerate(mesh['primitives']):
            index = primitive['attributes'].get('TANGENT')
            count = glb.json['accessors'][index]['count'] if index is not None else 0
            if index is None or count == 0:
                continue
            lane = 0
            placeholders = tangent_placeholder_lanes(dump, pairing, gi, pi)
            skipped = None
            if placeholders is not None and placeholders.size == count and placeholders.any():
                free = np.flatnonzero(~placeholders)
                if free.size == 0:
                    continue
                lane = int(free[0])
                skipped = int(placeholders.sum())
            bin_chunk = bytearray(glb.bin)
            offset = accessor_element_offset(glb, index, lane) + 12
            old = struct.unpack_from('<f', bin_chunk, offset)[0]
            bits = struct.unpack_from('<I', bin_chunk, offset)[0] ^ 0x80000000
            struct.pack_into('<I', bin_chunk, offset, bits)
            new = struct.unpack_from('<f', bin_chunk, offset)[0]
            description = {'mesh': gi, 'primitive': pi, 'lane': lane, 'wBefore': old, 'wAfter': new, 'where': 'meshes/%d/primitives/%d' % (gi, pi)}
            if skipped is not None:
                description['tangentPlaceholderLanes'] = skipped
            return rebuild_glb(json.loads(glb.json_bytes.decode('utf-8')), bytes(bin_chunk)), description
    return None, None


def mutate_placeholder_tangent(glb_bytes, dump, pairing):
    """A copy of the GLB in which the first unreferenced zero-direction tangent lane (a lane the writer fills with
    (1, 0, 0, w), ModelGltfGeometry line 281 at pinned Shared 591d083) of the first primitive that has one is rewritten
    as (0, 1, 0, w), its stored w kept: still a unit direction with an exact handedness, so a length or handedness test
    cannot see it and only the exact placeholder comparison can. Returns (mutated bytes, description) or (None, None)
    when no such lane exists."""
    glb = Glb(glb_bytes)
    for gi, mesh in enumerate(glb.json.get('meshes', [])):
        for pi, primitive in enumerate(mesh['primitives']):
            index = primitive['attributes'].get('TANGENT')
            if index is None:
                continue
            placeholders = tangent_placeholder_lanes(dump, pairing, gi, pi)
            if placeholders is None or not placeholders.any() or glb.json['accessors'][index]['count'] != placeholders.size:
                continue
            lane = int(np.flatnonzero(placeholders)[0])
            bin_chunk = bytearray(glb.bin)
            offset = accessor_element_offset(glb, index, lane)
            before = list(struct.unpack_from('<4f', bin_chunk, offset))
            struct.pack_into('<3f', bin_chunk, offset, 0.0, 1.0, 0.0)
            after = list(struct.unpack_from('<4f', bin_chunk, offset))
            description = {'mesh': gi, 'primitive': pi, 'lane': lane, 'before': before, 'after': after,
                           'placeholderLanes': int(placeholders.sum()), 'where': 'meshes/%d/primitives/%d' % (gi, pi)}
            return rebuild_glb(json.loads(glb.json_bytes.decode('utf-8')), bytes(bin_chunk)), description
    return None, None


def mutate_placeholder_normal(glb_bytes, dump, pairing):
    """A copy of the GLB in which the first unreferenced zero-normal lane (a lane the writer fills with UnitY) of the
    first primitive that has one is rewritten as (1, 0, 0): unit length, so a length check cannot see it and only the
    exact placeholder comparison can. Returns (mutated bytes, description) or (None, None) when no such lane exists."""
    glb = Glb(glb_bytes)
    for gi, mesh in enumerate(glb.json.get('meshes', [])):
        for pi, primitive in enumerate(mesh['primitives']):
            index = primitive['attributes'].get('NORMAL')
            if index is None or (gi, pi) not in pairing.primitive_map:
                continue
            di, dpi = pairing.primitive_map[(gi, pi)]
            prim = dump.meshes[di]['primitives'][dpi]
            zero_mask, referenced = zero_normal_lanes(prim)
            placeholders = np.flatnonzero(zero_mask & ~referenced)
            if placeholders.size == 0 or glb.json['accessors'][index]['count'] != prim.vertex_count:
                continue
            lane = int(placeholders[0])
            bin_chunk = bytearray(glb.bin)
            offset = accessor_element_offset(glb, index, lane)
            before = list(struct.unpack_from('<3f', bin_chunk, offset))
            struct.pack_into('<3f', bin_chunk, offset, 1.0, 0.0, 0.0)
            description = {'mesh': gi, 'primitive': pi, 'lane': lane, 'before': before, 'after': [1.0, 0.0, 0.0],
                           'placeholderLanes': int(placeholders.size), 'where': 'meshes/%d/primitives/%d' % (gi, pi)}
            return rebuild_glb(json.loads(glb.json_bytes.decode('utf-8')), bytes(bin_chunk)), description
    return None, None


def geometry_control_verdict(probe, pristine, stream, kind='value'):
    """Whether a mutated copy was detected AT THE MUTATED STREAM by a comparison the pristine check did not already fail.

    Detected (True) only when the probe's first mismatch is a ``kind`` mismatch (``value``: the element comparison)
    whose ``where`` is exactly ``stream``, the mutated primitive's own stream label, and the pristine accessor check on
    the untouched GLB did not itself fail at that stream. A ``presence`` or ``unsupported`` failure on the stream never
    compared the mutated bytes and is not a detection. When the pristine check fails at the same stream, or ahead of
    it, the probe reproduces the pristine failure and not the mutation, so the control cannot discriminate and reads
    not applicable (None) naming the masking mismatch. Otherwise it reads NOT detected (False)."""
    check = probe.checks[-1]
    first = check.mismatches[0] if check.mismatches else None
    masking = pristine.mismatches[0] if pristine is not None and pristine.passed is False and pristine.mismatches else None
    if masking is not None and (first is None or first.where == masking.where):
        return None, ('not applicable: the pristine accessor check already fails at %s (%s), so a mismatch there cannot be told apart from the mutation'
                      % (masking.where, masking.kind))
    if check.passed is False and first is not None and first.where == stream and first.kind == kind:
        return True, 'mismatch reported at %s' % first.where
    if masking is not None:
        return None, 'not applicable: the pristine accessor check already fails at %s, so the mutation cannot be told apart' % masking.where
    return False, 'NOT detected (probe %s%s; expected a %s mismatch at %s)' % (
        'passed' if check.passed else 'failed', (' at %s (%s)' % (first.where, first.kind)) if first is not None else '', kind, stream)


def mutated_stream(dump, pairing, description, stream):
    """The exact ``where`` label ``check_geometry`` gives one stream of the mutated GLB primitive
    (``meshes/<g>/primitives/<p> (<dump primitive name>)/<stream>``), so a control's verdict names the mutated stream
    itself and not a morph target's or another primitive's stream of the same name."""
    di, dpi = pairing.primitive_map[(description['mesh'], description['primitive'])]
    return 'meshes/%d/primitives/%d (%s)/%s' % (description['mesh'], description['primitive'], dump.meshes[di]['primitives'][dpi].name, stream)


CONTROL_TANGENT_W = 'handedness sign of one TANGENT lane flipped in a copy of the GLB'
CONTROL_PLACEHOLDER = 'one unreferenced zero-normal placeholder lane rewritten as (1, 0, 0) in a copy of the GLB'
CONTROL_TANGENT_PLACEHOLDER = 'one unreferenced zero-tangent placeholder lane rewritten as (0, 1, 0, w) in a copy of the GLB'

# The three accessor controls. Each names its mutation (a callable of (GLB bytes, dump, pairing) returning (mutated bytes,
# description) or (None, None) when the sample has nothing to mutate), the stream label the verdict must see the
# mismatch on, the file the mutated copy is saved as, the not-applicable reason, and how the description reads.
GEOMETRY_CONTROLS = {
    CONTROL_TANGENT_W: {
        # The tangent comparison keeps every w exactly (ModelGltfGeometry keeps the source w on every lane it writes:
        # lines 281, 286 and 296 at pinned Shared 591d083), so a flipped sign must be reported as a value mismatch on
        # that primitive's TANGENT stream; the flip avoids the placeholder lanes, which the next control owns.
        'mutate': mutate_tangent_w,
        'stream': TANGENT_STREAM,
        'file': 'control-tangent-w-flip.glb',
        'notApplicable': 'not applicable: no primitive carries a TANGENT accessor',
        'describe': lambda d: '%s lane %d w %r -> %r' % (d['where'], d['lane'], d['wBefore'], d['wAfter']),
    },
    CONTROL_PLACEHOLDER: {
        # The lane stays unit length, so only the exact (0, 1, 0) comparison of the placeholder lanes can report it, and
        # the verdict names that comparison's own stream label (a mismatch on the non-zero source lanes is not a detection).
        'mutate': mutate_placeholder_normal,
        'stream': PLACEHOLDER_STREAM,
        'file': 'control-placeholder-normal.glb',
        'notApplicable': 'not applicable: no primitive has a zero-normal lane that no triangle index references',
        'describe': lambda d: '%s lane %d (one of %d placeholder lanes) rewritten %r -> %r' % (
            d['where'], d['lane'], d['placeholderLanes'], d['before'], d['after']),
    },
    CONTROL_TANGENT_PLACEHOLDER: {
        # The rewritten lane keeps a unit direction and its exact handedness, so only the exact (1, 0, 0, w) comparison
        # of the tangent placeholder lanes can report it; the verdict names that comparison's own stream label (a
        # mismatch on the authored TANGENT lanes, compared first, is masking, not detection).
        'mutate': mutate_placeholder_tangent,
        'stream': TANGENT_PLACEHOLDER_STREAM,
        'file': 'control-placeholder-tangent.glb',
        'notApplicable': 'not applicable: no primitive has a zero-direction tangent lane that no triangle index references',
        'describe': lambda d: '%s lane %d (one of %d tangent placeholder lanes) rewritten %r -> %r' % (
            d['where'], d['lane'], d['placeholderLanes'], d['before'], d['after']),
    },
}
GEOMETRY_CONTROL_ORDER = (CONTROL_TANGENT_W, CONTROL_PLACEHOLDER, CONTROL_TANGENT_PLACEHOLDER)


def run_geometry_control(name, sample_id, glb_bytes, dump, pairing, pristine, work_dir):
    """One accessor control (``GEOMETRY_CONTROLS[name]``) on a copy of the GLB: mutate the copy, run ``check_geometry`` on
    it against the untouched dump, judge with ``geometry_control_verdict`` on the mutated primitive's own stream label,
    save the copy. ``pristine`` is the sample's own accessor check on the untouched GLB (None when it was not run).
    Returns the control dict the receipt records."""
    spec = GEOMETRY_CONTROLS[name]
    control = {'name': name, 'detected': None, 'detail': None}
    mutated, description = spec['mutate'](glb_bytes, dump, pairing)
    if mutated is None:
        control['detail'] = spec['notApplicable']
        return control
    probe = HopResult('B-control', sample_id)
    check_geometry(probe, Glb(mutated), dump, pairing)
    stream = mutated_stream(dump, pairing, description, spec['stream'])
    detected, verdict = geometry_control_verdict(probe, pristine, stream)
    control['expectedMismatchWhere'] = stream
    control['detected'] = detected
    control['detail'] = '%s: %s' % (spec['describe'](description), verdict)
    control['mutation'] = description
    control['firstMismatch'] = probe.checks[-1].mismatches[0].to_dict() if probe.checks[-1].mismatches else None
    os.makedirs(work_dir, exist_ok=True)
    path = os.path.join(work_dir, spec['file'])
    with open(path, 'wb') as f:
        f.write(mutated)
    control['files'] = {'glb': path, 'glbSha256': sha256_bytes(mutated)}
    return control


CONTROL_MASK = 'MASK alpha cutoff written as T/255 in a copy of the GLB'


def run_mask_control(sample_id, glb_bytes, glb, dump, pairing, work_dir):
    """The MASK control: the cutoff of the first material that takes the eight-bit Greater MASK rule is rewritten as
    T/255 in a copy of the GLB (``control-mask-cutoff.glb`` under ``work_dir``) and ``check_materials`` runs on it
    against the untouched dump; any failure reads detected (the corpus verdict since the first cut). The same function
    serves the corpus run (``run_controls``) and the synthetic exercise. Returns the control dict the receipt records."""
    control = {'name': CONTROL_MASK, 'detected': None, 'detail': None}
    mask_material = None
    for gi in range(len(glb.json.get('materials', []))):
        state = dump.materials[pairing.material_map[gi]].get('renderState')
        if rules.mask_rule_applies(state):
            mask_material = (gi, state['alphaTest']['rawReference'])
            break
    if mask_material is None:
        control['detail'] = 'not applicable: no material takes the eight-bit Greater MASK rule'
        return control
    gi, raw = mask_material
    mutated = mutate_alpha_cutoff(glb_bytes, gi, raw / 255.0)
    mutated_glb = Glb(mutated)
    probe = HopResult('B-control', sample_id)
    check_materials(probe, mutated_glb, dump, pairing)
    failed = probe.checks[-1].passed is False
    control['detected'] = failed
    control['detail'] = 'material %d cutoff rewritten to %r (T=%d): %s' % (gi, raw / 255.0, raw, 'mismatch reported' if failed else 'NOT detected')
    control['firstMismatch'] = probe.checks[-1].mismatches[0].to_dict() if probe.checks[-1].mismatches else None
    control['expectedMismatchWhere'] = 'materials/%d (%s)/alphaCutoff' % (gi, glb.json['materials'][gi].get('name'))
    path = os.path.join(work_dir, 'control-mask-cutoff.glb')
    with open(path, 'wb') as f:
        f.write(mutated)
    control['files'] = {'glb': path, 'glbSha256': sha256_bytes(mutated)}
    return control


# The controls ``synthetic_control_exercise`` can run on the gate1a_synthetic set: the three accessor controls and the
# MASK control. The two image controls and the UV shift are reached by every textured corpus sample.
SYNTHETIC_CONTROLS = GEOMETRY_CONTROL_ORDER + (CONTROL_MASK,)


def synthetic_control_exercise(control_name, work_dir=None, pristine_set=None):
    """Exercise one accessor control, or the MASK control, on the hand-built artifact set of ``gate1a_synthetic``, for a
    control no corpus sample reaches.

    Writes the synthetic dump, GLB and fidelity report into ``work_dir`` (a fresh temporary directory when None), runs
    the SAME pristine checks (``check_geometry`` on the accessors, ``check_fidelity`` on the rows, ``check_materials``
    on the materials), the SAME mutation (``GEOMETRY_CONTROLS[name]['mutate']``, or the cutoff rewrite of
    ``run_mask_control``) and the SAME verdict (``geometry_control_verdict`` through ``run_geometry_control``, or
    ``run_mask_control``'s) the corpus run uses; nothing is reimplemented. ``pristine_set`` is the set builder,
    ``gate1a_synthetic.write_pristine_set`` unless a self-check passes a drifted one.

    Returns None for a control that has no synthetic exercise. Otherwise a dict: ``detected`` (True only when the
    verdict is a detection AND both pristine checks on the untouched set pass; a masked or failed verdict reads False,
    and so does a detection on a set whose own pristine check fails: a set out of step with ``writer_rules`` proves
    nothing about the control, exactly as a pristine failure on a corpus sample fails its hop), ``verdict`` (the
    control's own verdict before that gate: True, False or None), ``pristinePassed``, ``detail``,
    ``expectedMismatchWhere``, ``artifactSha256`` (the pristine glb / dump / fidelity and the mutated copy),
    ``firstMismatch``, ``mutation``, ``pristineChecks`` (the accessor and fidelity checks on the untouched set),
    ``files`` and ``workDir``."""
    if control_name not in SYNTHETIC_CONTROLS:
        return None
    import gate1a_synthetic  # imported here: gate1a_synthetic takes rebuild_glb from this module
    build_set = pristine_set or gate1a_synthetic.write_pristine_set
    work_dir = work_dir or tempfile.mkdtemp(prefix='gate1a-synthetic-')
    artifacts = build_set(work_dir)
    glb_bytes = artifacts['glbBytes']
    glb = Glb(glb_bytes)
    dump = Dump(artifacts['dump'])
    pairing = GlbDumpPairing(glb, dump)
    baseline = HopResult('B-synthetic', 'synthetic')
    check_geometry(baseline, glb, dump, pairing)
    pristine = baseline.checks[-1]
    check_fidelity(baseline, glb, artifacts['fidelity'], dump, pairing)
    check_materials(baseline, glb, dump, pairing)
    if control_name in GEOMETRY_CONTROLS:
        control = run_geometry_control(control_name, 'synthetic', glb_bytes, dump, pairing, pristine, work_dir)
    else:
        os.makedirs(work_dir, exist_ok=True)
        control = run_mask_control('synthetic', glb_bytes, glb, dump, pairing, work_dir)
    verdict = control.get('detected')
    # The gate: a pristine failure on a corpus sample fails its hop, so a detection on a synthetic set whose own
    # pristine check fails must not stand in for one. The placeholder stream is compared ahead of TANGENT, so the
    # verdict alone would still read detected on a set whose stored tangents drifted; this closes that.
    failed_pristine = [c for c in baseline.checks if c.passed is not True]
    pristine_passed = not failed_pristine
    detected = verdict is True and pristine_passed
    if pristine_passed:
        pristine_state = 'pristine accessor, fidelity and material checks passed'
    else:
        pristine_state = 'pristine %s; the synthetic set is out of step with writer_rules, so the exercise reads NOT detected' % '; '.join(
            '%s check %s at %s' % (c.name.split(':')[0], 'FAILED' if c.passed is False else 'not run',
                                   c.mismatches[0] if c.mismatches else c.detail) for c in failed_pristine)
    files = control.get('files') or {}
    return {
        'control': control_name,
        'detected': detected,
        'verdict': verdict,
        'pristinePassed': pristine_passed,
        'detail': '%s (synthetic set; %s)' % (control['detail'], pristine_state),
        'expectedMismatchWhere': control.get('expectedMismatchWhere'),
        'artifactSha256': dict(artifacts['sha256'], mutatedGlb=files.get('glbSha256')),
        'firstMismatch': control.get('firstMismatch'),
        'mutation': control.get('mutation'),
        'pristineChecks': [{'name': c.name, 'status': c.to_dict()['status'], 'detail': c.detail,
                            'firstMismatch': c.mismatches[0].to_dict() if c.mismatches else None} for c in baseline.checks],
        'files': {'glb': artifacts['glb'], 'dump': artifacts['dump'], 'fidelity': artifacts['fidelity'], 'mutatedGlb': files.get('glb')},
        'workDir': work_dir,
    }


def run(sample, validator, work_dir):
    """Hop B over one sample dict with keys glb, dump (optional), fidelity (optional)."""
    result = HopResult('B', sample['id'])
    try:
        with open(sample['glb'], 'rb') as f:
            glb_bytes = f.read()
        glb = Glb(glb_bytes)
        check_extensions(result, glb)
        report = run_validator(validator, sample['glb'])
        check = result.check('Khronos validator: 0 errors')
        check.data['validatorVersion'] = report.get('validatorVersion')
        check.data['issues'] = {k: v for k, v in report.get('issues', {}).items() if k != 'messages'}
        check.data['messages'] = report.get('issues', {}).get('messages', [])[:50]
        check.data['info'] = report.get('info')
        os.makedirs(work_dir, exist_ok=True)
        report_path = os.path.join(work_dir, 'validator-report.json')
        with open(report_path, 'w', encoding='utf-8') as f:
            json.dump(report, f, indent=1)
        check.data['reportPath'] = report_path
        if report.get('issues', {}).get('numErrors', 1) != 0:
            check.fail(Mismatch('validator', 'errors', '%d errors: %r' % (report['issues']['numErrors'], report['issues']['messages'][:3])))
        else:
            check.ok('0 errors, %d warnings' % report['issues'].get('numWarnings', 0))
        dump = pairing = None
        if sample.get('dump'):
            dump = Dump(sample['dump'])
            pairing = GlbDumpPairing(glb, dump)
            check_nodes(result, glb, dump, pairing)
            check_geometry(result, glb, dump, pairing)
            check_materials(result, glb, dump, pairing)
            check_images(result, glb, dump, pairing)
        else:
            result.check('accessors, nodes, materials, images').skip('no dump supplied')
        if sample.get('fidelity'):
            check_fidelity(result, glb, sample['fidelity'], dump, pairing)
        else:
            result.check('fidelity rows').skip('no fidelity JSON supplied')
        if dump is not None:
            run_controls(result, glb_bytes, glb, dump, pairing, validator, work_dir)
    except OracleError as failure:
        result.error = str(failure)
    except Exception as failure:  # noqa: BLE001 - an oracle defect must reach the receipt, not abort the run
        result.error = 'oracle exception %s: %s' % (type(failure).__name__, failure)
        result.traceback = traceback.format_exc()
    return result


def check_extensions(result, glb):
    check = result.check('extensions recorded')
    check.data['extensionsUsed'] = glb.extensions_used
    check.data['extensionsRequired'] = glb.extensions_required
    check.data['sites'] = glb.extension_sites[:50]
    # KHR_animation_pointer is required by every animated property channel (cut 1b design: KHR_animation_pointer plus
    # KHR_node_visibility, the latter used but never required).
    unknown = [e for e in glb.extensions_required if e not in ('KHR_texture_transform', 'KHR_animation_pointer')]
    if unknown:
        check.fail(Mismatch('extensionsRequired', 'unexpected', 'required extensions the design does not declare: %r' % unknown))
    else:
        check.ok('used %r, required %r' % (glb.extensions_used, glb.extensions_required))


def mutate_image_png(glb_bytes, image_index, original_rgba, mutated_rgba):
    """A copy of the GLB whose image ``image_index`` PNG carries ``mutated_rgba`` wherever it differs from
    ``original_rgba``: the PNG is decoded with Pillow, the differing pixels spliced in, re-encoded losslessly, appended
    4-byte aligned to the BIN chunk, and the image's bufferView repointed at the new bytes (byteStride dropped).

    Returns (mutated GLB bytes, number of pixels spliced)."""
    from PIL import Image
    glb = Glb(glb_bytes)
    png, mime = glb.image_bytes(image_index)
    if mime != 'image/png':
        raise OracleError('image %d is %r, not PNG; the control cannot re-encode it' % (image_index, mime))
    with Image.open(io.BytesIO(png)) as im:
        im.load()
        pixels = np.array(im.convert('RGBA'), dtype=np.uint8)
    if pixels.shape != mutated_rgba.shape:
        raise OracleError('GLB PNG %s and the mutated decode %s differ in shape' % (pixels.shape, mutated_rgba.shape))
    mask = (mutated_rgba != original_rgba).any(axis=2)
    pixels[mask] = mutated_rgba[mask]
    buffer = io.BytesIO()
    Image.fromarray(pixels, 'RGBA').save(buffer, format='PNG', optimize=False)
    new_png = buffer.getvalue()
    document = json.loads(glb.json_bytes.decode('utf-8'))
    bin_chunk = bytes(glb.bin)
    bin_chunk += b'\0' * ((4 - len(bin_chunk) % 4) % 4)
    view_index = document['images'][image_index]['bufferView']
    view = document['bufferViews'][view_index]
    view['byteOffset'] = len(bin_chunk)
    view['byteLength'] = len(new_png)
    view.pop('byteStride', None)
    bin_chunk += new_png
    document['buffers'][0]['byteLength'] = len(bin_chunk) + ((4 - len(bin_chunk) % 4) % 4)
    return rebuild_glb(document, bin_chunk), int(mask.sum())


def undecodable_dump(dump, image_index):
    """A shallow copy of the dump whose image ``image_index`` cannot yield an expectation: a DDS payload has its FourCC
    rewritten to XXXX (and the FourCC flag set), any other payload is dropped. Returns (copy, description)."""
    mutated = copy.copy(dump)
    mutated.images = list(dump.images)
    image = copy.copy(dump.images[image_index])
    mutated.images[image_index] = image
    payload, selection = image.writer_selected_payload()
    attribute = {'original': 'original', 'standard': 'standard', 'recovery': 'derivation_payload', 'derived': 'derivation_payload'}.get(selection)
    if payload is not None and attribute is not None and 'dds' in (payload['container'] or '').lower() and len(payload['bytes']) >= 128:
        data = bytearray(payload['bytes'])
        data[84:88] = b'XXXX'
        struct.pack_into('<I', data, 80, struct.unpack_from('<I', data, 80)[0] | dds_decode.DDPF_FOURCC)
        setattr(image, attribute, {'container': payload['container'], 'bytes': bytes(data), 'sha256': sha256_bytes(bytes(data))})
        return mutated, 'FourCC of the %s DDS payload rewritten to XXXX' % selection
    image.original = None
    image.standard = None
    image.derivation_payload = None
    image.legacy_content = b''
    if image.source is None:
        image.source = {}
    return mutated, 'every payload of the image dropped (%s payload was %s)' % (selection, (payload or {}).get('container'))


def base_color_target(glb, dump, pairing, compressions):
    """The first GLB material whose base-color binding the image check compares (not on the blend route, whose derived
    PNGs ``check_images`` lists as not compared, so a mutation there could never be detected) and maps to a dump image
    with a DDS payload of one of ``compressions``. Returns (glb material index, dump image index, payload bytes) or None."""
    for gi, gmat in enumerate(glb.json.get('materials', [])):
        binding = (gmat.get('pbrMetallicRoughness') or {}).get('baseColorTexture')
        dmat = dump.materials[pairing.material_map[gi]]
        dbinding = dmat.get('texture')
        if binding is None or dbinding is None or rules.blend_plan_applies(dmat):
            continue
        payload, _ = dump.images[dbinding['imageIndex']].writer_selected_payload()
        if not payload or 'dds' not in (payload['container'] or '').lower():
            continue
        try:
            header = dds_decode.DdsHeader(payload['bytes'])
        except OracleError:
            continue
        if compressions is None or header.compression in compressions:
            return gi, dbinding['imageIndex'], payload['bytes']
    return None


def run_controls(result, glb_bytes, glb, dump, pairing, validator, work_dir):
    os.makedirs(work_dir, exist_ok=True)
    # Control 1: swapped BC1 endpoints, re-encoded into a copy of the GLB's base-color PNG; the pristine expectation
    # (built from the untouched dump) is compared against the mutated artifact. The mutated DDS is kept as provenance.
    control = {'name': 'swapped BC1 endpoints re-encoded into a copy of the GLB PNG', 'detected': None, 'detail': None}
    target = base_color_target(glb, dump, pairing, ('BC1', 'BC2', 'BC3'))
    if target is None:
        control['detail'] = 'not applicable: no compared base-color binding with a block-compressed payload'
    else:
        gi, image_index, payload_bytes = target
        original_rgba, _ = dds_decode.decode(payload_bytes)
        # The chooser measures the swap with this harness's block decoder; the splice below uses the pinned full decode,
        # which may round a palette entry differently, so the full-decode difference is confirmed before the block is
        # used and the next candidate is tried when it falls inside the 1/255 tolerance (bounded, then not applicable).
        mutated_dds, block, mutated_rgba, skipped = None, None, None, []
        start = 0
        for _ in range(8):
            candidate, block = dds_decode.swap_bc1_endpoints(payload_bytes, start)
            if candidate is None:
                block = None
                break
            rgba, _ = dds_decode.decode(candidate)
            if rgba.shape == original_rgba.shape and int(np.abs(rgba.astype(np.int16) - original_rgba.astype(np.int16)).max()) > 1:
                mutated_dds, mutated_rgba = candidate, rgba
                break
            skipped.append(block)
            start = block + 1
        if mutated_dds is None:
            control['detail'] = 'not applicable: no block whose endpoint exchange moves a decoded texel by more than 1/255%s' % (
                ' (candidates %r rejected by the full decode)' % skipped if skipped else '')
        else:
            dds_path = os.path.join(work_dir, 'control-bc1-endpoints.dds')
            with open(dds_path, 'wb') as f:
                f.write(mutated_dds)
            binding = glb.json['materials'][gi]['pbrMetallicRoughness']['baseColorTexture']
            source_index, _ = glb.texture_image(binding['index'])
            mutated_glb, spliced = mutate_image_png(glb_bytes, source_index, original_rgba, mutated_rgba)
            glb_path = os.path.join(work_dir, 'control-bc1-endpoints.glb')
            with open(glb_path, 'wb') as f:
                f.write(mutated_glb)
            probe = HopResult('B-control', result.sample_id)
            check_images(probe, Glb(mutated_glb), dump, pairing)
            failed = probe.checks[-1].passed is False
            control['detected'] = failed
            change = int(np.abs(mutated_rgba.astype(np.int16) - original_rgba.astype(np.int16)).max())
            control['detail'] = 'block %d endpoints swapped in %s (max texel change %d/255), %d pixels spliced into image %d of %s: %s' % (
                block, dds_path, change, spliced, source_index, glb_path, 'mismatch reported' if failed else 'NOT detected')
            control['firstMismatch'] = probe.checks[-1].mismatches[0].to_dict() if probe.checks[-1].mismatches else None
            control['files'] = {'dds': dds_path, 'ddsSha256': sha256_bytes(mutated_dds), 'glb': glb_path, 'glbSha256': sha256_bytes(mutated_glb)}
    result.controls.append(control)
    # Control 2: the MASK cutoff written as T/255 in a copy of the GLB (run_mask_control, shared with the synthetic
    # exercise).
    result.controls.append(run_mask_control(result.sample_id, glb_bytes, glb, dump, pairing, work_dir))
    # Control 3: a one-texel UV shift in a copy of the GLB.
    control = {'name': 'one-texel UV shift in a copy of the GLB', 'detected': None, 'detail': None}
    width = None
    for image in dump.images:
        if image.descriptor and image.descriptor.get('width'):
            width = image.descriptor['width']
            break
    texel = 1.0 / width if width else 1.0 / 256
    mutated, touched = mutate_uv_shift(glb_bytes, texel)
    if touched == 0:
        control['detail'] = 'not applicable: no TEXCOORD_0'
    else:
        mutated_glb = Glb(mutated)
        probe = HopResult('B-control', result.sample_id)
        check_geometry(probe, mutated_glb, dump, pairing)
        failed = probe.checks[-1].passed is False
        control['detected'] = failed
        control['detail'] = 'u shifted by %r (one texel of a %s-wide texture) on %d vertices: %s' % (texel, width, touched, 'mismatch reported' if failed else 'NOT detected')
        control['firstMismatch'] = probe.checks[-1].mismatches[0].to_dict() if probe.checks[-1].mismatches else None
        with open(os.path.join(work_dir, 'control-uv-shift.glb'), 'wb') as f:
            f.write(mutated)
    result.controls.append(control)
    # Control 4: the base-color payload made undecodable in a copy of the dump; an expectation the oracle cannot
    # build must FAIL the image check (never a silent skip). The GLB is untouched.
    control = {'name': 'base-color payload made undecodable in a copy of the dump', 'detected': None, 'detail': None}
    textured = None
    for gi, gmat in enumerate(glb.json.get('materials', [])):
        dbinding = dump.materials[pairing.material_map[gi]].get('texture')
        if (gmat.get('pbrMetallicRoughness') or {}).get('baseColorTexture') is not None and dbinding is not None \
                and not rules.blend_plan_applies(dump.materials[pairing.material_map[gi]]):
            textured = (gi, dbinding['imageIndex'])
            break
    if textured is None:
        control['detail'] = 'not applicable: no compared base-color binding'
    else:
        gi, image_index = textured
        mutated_dump, description = undecodable_dump(dump, image_index)
        probe = HopResult('B-control', result.sample_id)
        check_images(probe, glb, mutated_dump, pairing)
        failed = probe.checks[-1].passed is False
        control['detected'] = failed
        control['detail'] = 'image %d (%s) of a copy of the dump: %s: %s' % (image_index, dump.images[image_index].name, description,
                                                                          'failed check reported' if failed else 'NOT detected')
        control['firstMismatch'] = probe.checks[-1].mismatches[0].to_dict() if probe.checks[-1].mismatches else None
        control['probeDetail'] = probe.checks[-1].detail
    result.controls.append(control)
    pristine = next((c for c in result.checks if c.name.startswith('accessors:')), None)
    # Controls 5, 6 and 7: the handedness sign of one TANGENT lane flipped, one unreferenced zero-normal placeholder
    # lane rewritten as (1, 0, 0), and one unreferenced zero-tangent placeholder lane rewritten as (0, 1, 0, w), each in
    # a copy of the GLB (GEOMETRY_CONTROLS). The same helper serves the synthetic exercise, so a control the corpus
    # never reaches is judged by exactly this mutation, check and verdict.
    for name in GEOMETRY_CONTROL_ORDER:
        result.controls.append(run_geometry_control(name, result.sample_id, glb_bytes, dump, pairing, pristine, work_dir))
