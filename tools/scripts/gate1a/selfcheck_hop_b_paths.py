# SPDX-License-Identifier: 0BSD
"""Self-check of hop B's zero-normal, tangent, fidelity-row and control paths on a hand-built GLB, dump and report.

No produced artifact of the 2026-09-24 manifest reaches most of these paths (no GLB sample has a zero-normal lane or
a tangent placeholder, and no dump carries a morph target with tangent deltas; the one sample that reaches the
placeholders at pinned Shared 591d083, FNV combat-armor go.nif, is outside the manifest), so this file is the evidence
that they behave. Exit status 1 on any failed expectation.

The artifact set (the dump, the GLB the writer would emit for it, the fidelity report) is built by ``gate1a_synthetic``,
the one implementation shared with ``hop_b.synthetic_control_exercise`` (which the gate driver runs for a control no
corpus sample reaches); this file exercises its variants and, in its last sections, the driver's exercise itself.
``gate1a_synthetic`` describes the two primitives ``prim`` and ``plain`` and the MASK material.

Cases: the pristine accessor check passes and records the counts; an unnormalized tangent, a wrong normal placeholder
(also a -0 placeholder), a non-unit reconstructed lane (scaled by 1 + 5e-6, inside the old np.allclose tolerance) fail
while the float32 rounding of a double unit direction passes; the tangent placeholder lanes (pinned Shared 591d083)
must be exactly (1, +0, +0, w): (0, 1, 0, w), the source's (0, 0, 0, w), a (1, -0, 0, w) and a flipped w each fail on
the placeholder stream, naming the accessor lane; an invalid tangent in the dump beside an existing GLB fails as
unsupported (a referenced zero direction, an unreferenced zero direction without an exact handedness, a zero lane
referenced only by a degenerate triangle); the fidelity rows the writer would emit pass, and every row the writer can
never emit fails (a normalized or preserved row on the zero-lane primitive, a reconstruction row with zero counts or a
placeholder row on the plain primitive, a missing placeholder row, a normal-direction-absent row, a morph-tangent row
on a target without tangent deltas or on a primitive whose tangents did not change, a missing one on t2; for tangents a
wrong Filled or Exactly count, a count the text does not carry, a missing filled, placeholder or normalization row, the
normalization row under geometry/tangents beside placeholders, the previous writer's row set, a wrong outcome or a
bound on a placeholder row, a placeholder row on the plain primitive, an unsupported row, a primitive with no tangent
row at all); a duplicated row fails (in the report only, or in both the report and the carrier: a filled, tangent
placeholder, geometry/normals, normal placeholder or morph-tangent row, and an extra filled row declaring the wrong
count ahead of the right one); the refusal evidence of a dump records the previous pin's (0fe3212) verdict beside the
pinned rule's and which writer's tangent-basis refusal text a manifest's partial reason quotes; placeholders alone (no normalized lane) give the filled and placeholder rows and still drive the
morph-tangent row; the three accessor controls are detected at exactly the mutated stream, and read not applicable when
the pristine check already fails at that stream or ahead of it; the w flip avoids a placeholder lane (flipping lane 0
when it is one reads NOT detected on the TANGENT stream, which is why the lane is chosen); the MASK control is detected;
the driver's synthetic exercise detects all four controls on the pristine set and reads NOT detected on a set that
drifted from writer_rules (a wrong stored TANGENT lane, a wrong stored tangent placeholder, a fidelity report missing a
row, a wrong MASK cutoff) even where the control's own verdict is a detection.
"""

import copy
import json
import os
import struct
import sys
import tempfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import gate1a_synthetic as synthetic  # noqa: E402
import hop_b  # noqa: E402
import writer_rules as rules  # noqa: E402
from dump_reader import Dump  # noqa: E402
from gate1a_common import HopResult, bits_equal, sha256_bytes, sha256_file  # noqa: E402
from gate1a_synthetic import A, B, build_glb, row  # noqa: E402
from glb_reader import Glb  # noqa: E402

FAILURES = []


def expect(condition, message):
    if not condition:
        FAILURES.append(message)
        print('FAIL: ' + message)
    else:
        print('ok:   ' + message)


def lane_bits(values):
    return np.ascontiguousarray(values, dtype=np.float32).reshape(-1).view(np.uint32).tolist()


def main():
    work = tempfile.mkdtemp(prefix='gate1a-hop-b-paths-')
    pristine = synthetic.write_pristine_set(work)
    dump = synthetic.dump_document()
    d = pristine['dumpObject']
    prim_a = d.meshes[0]['primitives'][0]
    prim_b = d.meshes[0]['primitives'][1]
    expected_ta, t_deg, t_changed, t_invalid = rules.normalize_tangents(prim_a.tangents, prim_a.indices)
    expected_tb, _, b_changed, _ = rules.normalize_tangents(prim_b.tangents, prim_b.indices)
    plan_a = rules.tangent_plan(prim_a.tangents, prim_a.indices)
    expected_na, _, _, zero = rules.normalize_normals(prim_a.normals, prim_a.normal_mode)
    expect(t_changed and t_invalid == 0 and 6.8e-7 < t_deg < 6.9e-7 and expected_ta[1].tolist() == [np.float32(0.6), np.float32(0.8), 0, 1],
           'prim tangent rule: lane 1 normalized to (0.6f, 0.8f, 0, 1), %r degrees' % t_deg)
    # Worked by hand from ModelGltfGeometry line 281 (new Vector4(1, 0, 0, value.W)): lane 3 (0, 0, 0, -1) and lane 4
    # (-0, 0, -0, +1) are zero directions no index references.
    expect(lane_bits(expected_ta[3]) == [0x3F800000, 0, 0, 0xBF800000] and lane_bits(expected_ta[4]) == [0x3F800000, 0, 0, 0x3F800000],
           'prim tangent rule: placeholder lanes 3 and 4 are (1, +0, +0, -1) and (1, +0, +0, +1) bit for bit')
    expect(plan_a['unreferencedCount'] == 2 and plan_a['normalizedCount'] == 1 and plan_a['invalidCount'] == 0
           and [(r['featureId'], r['reasonCode'], r['count']) for r in plan_a['rows']] == [
               ('geometry/tangents', 'geometry.unreferenced-tangents-filled', 2),
               ('geometry/unreferenced-tangent-slots', 'geometry.unreferenced-tangent-placeholders', 2),
               ('geometry/tangent-normalization', 'geometry.tangents-normalized', None)],
           'prim tangent rows: filled 2, placeholders 2, normalization under geometry/tangent-normalization')
    expect(not b_changed, 'plain tangent rule: nothing changes')
    expect(zero == [2, 3] and expected_na[1].tolist() == [0, 0, 1], 'prim normal rule: zero lanes [2, 3], lane 1 normalized')
    expect(hop_b.expected_normals_row(prim_a) == ('geometry.zero-normals-reconstructed', 1, 1, True), 'expected_normals_row(prim): reconstructed, M 1, N 1, changed')
    expect(hop_b.expected_normals_row(prim_b) == ('geometry.normals-preserved', 0, 0, False), 'expected_normals_row(plain): preserved')

    expect(bits_equal(expected_ta, pristine['expectedTangents'][0]) and bits_equal(expected_tb, pristine['expectedTangents'][1])
           and pristine['tangentDegrees'] == t_deg, 'the synthetic set carries the tangents and direction error the rule derives')
    a_normals = synthetic.A_WRITTEN_NORMALS  # lane 2 "reconstructed" (unit), lane 3 UnitY
    b_normals = synthetic.B_WRITTEN_NORMALS
    good_rows = pristine['rows']
    good_glb = pristine['glbBytes']
    expect(good_glb == build_glb([expected_ta, expected_tb], [a_normals, b_normals], good_rows) and sha256_bytes(good_glb) == pristine['sha256']['glb'],
           'the synthetic set GLB is build_glb of the rule values, SHA-256 %s' % pristine['sha256']['glb'])
    glb = Glb(good_glb)
    pairing = hop_b.GlbDumpPairing(glb, d)
    label_a = 'meshes/0/primitives/0 (prim)'

    def normals_with(lane, value):
        values = [list(v) for v in a_normals]
        values[lane] = value
        return values

    def tangents_with(lane, value):
        values = [list(map(float, v)) for v in expected_ta]
        values[lane] = value
        return values

    def geometry(glb_bytes, dump_obj=d, pair=pairing):
        r = HopResult('B', 'synthetic')
        hop_b.check_geometry(r, Glb(glb_bytes), dump_obj, pair)
        c = r.checks[-1]
        return c.passed, (c.mismatches[0].where + ': ' + c.mismatches[0].detail) if c.mismatches else c.detail, c.data, c

    # 1. Accessors.
    p, detail, data, _ = geometry(good_glb)
    expect(p is True, 'pristine GLB passes the accessor check: %s' % detail)
    expect(data.get('tangentsMaxDegrees', {}).get(label_a) == t_deg, 'tangentsMaxDegrees recorded (%r)' % data.get('tangentsMaxDegrees'))
    expect(data.get('reconstructedNormals') == [{'primitive': label_a, 'count': 2, 'unreferenced': 1, 'reconstructed': 1, 'unitLength': True}],
           'zero-normal counts recorded: %r' % data.get('reconstructedNormals'))
    expect(data.get('tangentPlaceholders') == [{'primitive': label_a, 'placeholderLanes': 2, 'normalizedLanes': 1, 'lanes': [3, 4], 'handedness': {'+1': 1, '-1': 1}}],
           'tangent placeholder counts recorded: %r' % data.get('tangentPlaceholders'))
    p, detail, _, _ = geometry(build_glb([A['tangents'], expected_tb], [a_normals, b_normals], good_rows))
    expect(p is False and detail.startswith(label_a + '/TANGENT:') and 'at [1, 0]' in detail and 'accessor lane 1' in detail,
           'unnormalized tangent lane 1 is a TANGENT value mismatch naming accessor lane 1: %s' % detail)
    p, detail, _, _ = geometry(build_glb([expected_ta, expected_tb], [normals_with(3, [1, 0, 0]), b_normals], good_rows))
    expect(p is False and detail.startswith(label_a + '/' + hop_b.PLACEHOLDER_STREAM + ':'), 'placeholder (1, 0, 0) is a placeholder-stream mismatch: %s' % detail)
    p, detail, _, _ = geometry(build_glb([expected_ta, expected_tb], [normals_with(3, [-0.0, 1, 0]), b_normals], good_rows))
    expect(p is False and hop_b.PLACEHOLDER_STREAM in detail, 'placeholder (-0, 1, 0) differs from UnitY in the sign of zero: %s' % detail)
    scaled = [float(np.float32(v * (1 + 5e-6))) for v in (0.6, 0.8, 0)]
    scaled_squared = sum(float(np.float32(v)) ** 2 for v in scaled)
    expect(abs(scaled_squared - 1) <= 1e-6 + 1e-5, 'a reconstructed lane scaled by 1 + 5e-6 (squared %r) was inside np.allclose\'s default tolerance' % scaled_squared)
    p, detail, _, _ = geometry(build_glb([expected_ta, expected_tb], [normals_with(2, scaled), b_normals], good_rows))
    expect(p is False and 'must be unit length' in detail, 'that lane now fails the 2^-22 bound: %s' % detail)
    unit = (np.array([1.0, 1.0, 1.0]) / np.sqrt(3.0)).astype(np.float32)  # the float32 rounding of a double unit direction
    unit_squared = float(sum(np.float64(v) * np.float64(v) for v in unit))
    expect(abs(unit_squared - 1) <= hop_b.RECONSTRUCTED_UNIT_TOLERANCE, 'float32(1/sqrt(3)) x3 squares within 2^-22 of 1 (%r)' % unit_squared)
    p, detail, _, _ = geometry(build_glb([expected_ta, expected_tb], [normals_with(2, unit.tolist()), b_normals], good_rows))
    expect(p is True, 'a reconstructed lane that is the float32 rounding of a double unit direction passes: %s' % detail)
    p, detail, _, _ = geometry(build_glb([expected_ta, expected_tb], [normals_with(2, [0.6, 0.8, 0.5]), b_normals], good_rows))
    expect(p is False and 'must be unit length' in detail, 'a non-unit reconstructed lane fails: %s' % detail)

    # 1b. Tangent placeholder lanes (pinned Shared 591d083): exactly (1, +0, +0, w), compared as their own stream after
    # the authored lanes, the mismatch naming the accessor lane.
    placeholder_where = label_a + '/' + hop_b.TANGENT_PLACEHOLDER_STREAM + ':'
    for lane, value, what in ((3, [0.0, 1.0, 0.0, -1.0], '(0, 1, 0, -1): unit, handedness kept'),
                              (3, [0.0, 0.0, 0.0, -1.0], 'the source (0, 0, 0, -1) copied through'),
                              (4, [-0.0, 0.0, -0.0, 1.0], 'the source (-0, 0, -0, +1) copied through'),
                              (4, [1.0, -0.0, 0.0, 1.0], '(1, -0, 0, +1): the writer writes +0'),
                              (3, [1.0, 0.0, 0.0, 1.0], '(1, 0, 0, +1): the authored -1 flipped')):
        p, detail, _, _ = geometry(build_glb([tangents_with(lane, value), expected_tb], [a_normals, b_normals], good_rows))
        expect(p is False and detail.startswith(placeholder_where) and ('accessor lane %d' % lane) in detail,
               'tangent placeholder lane %d written %s fails on the placeholder stream: %s' % (lane, what, detail))

    # 2. Invalid tangents in the dump while a GLB exists.
    def dump_variant(name, mutate):
        variant = json.loads(json.dumps(dump))
        mutate(variant['document']['meshes'][0]['primitives'][0])
        path = os.path.join(work, name)
        with open(path, 'w') as f:
            json.dump(variant, f)
        dv = Dump(path)
        return dv, hop_b.GlbDumpPairing(glb, dv)

    d_bad, pairing_bad = dump_variant('dump-invalid.json', lambda p: p['tangents']['values'].__setitem__(0, [0, 0, 0, 1]))
    p, detail, _, c = geometry(good_glb, d_bad, pairing_bad)
    expect(p is False and c.mismatches[0].kind == 'unsupported' and c.mismatches[0].where == label_a + '/TANGENT' and 'referenced zero direction' in detail,
           'a referenced zero tangent (lane 0) beside an existing GLB fails as unsupported on the TANGENT stream: %s' % detail)
    ev = hop_b.dump_refusal_evidence(d_bad)
    expect(ev['refusalPredictedByTangents'] and ev['invalidTangentPrimitives'][0]['invalidLanes'] == 1 and ev['placeholderTangentPrimitives'] == []
           and ev['previousPinRule'] == {'pin': 'Shared 0fe3212', 'invalidTangentPrimitives': [{'primitive': label_a, 'invalidLanes': 3}], 'refusalPredictedByTangents': True}
           and ev['manifestQuotesTangentRefusal'] is None,
           'refusal evidence: the referenced zero lane 0 is invalid under the pinned rule; the previous pin (0fe3212) refused lanes 0, 3 and 4: %r' % ev)
    expect(hop_b.dump_refusal_evidence(d) == {'invalidTangentPrimitives': [], 'normalizedTangentPrimitives': [{'primitive': label_a, 'maxDegrees': t_deg}],
                                              'placeholderTangentPrimitives': [{'primitive': label_a, 'placeholderLanes': 2}],
                                              'refusalPredictedByTangents': False,
                                              'previousPinRule': {'pin': 'Shared 0fe3212', 'invalidTangentPrimitives': [{'primitive': label_a, 'invalidLanes': 2}],
                                                                  'refusalPredictedByTangents': True},
                                              'manifestQuotesTangentRefusal': None},
           'refusal evidence on the valid dump names the normalized primitive and its placeholders and predicts no refusal under the pinned rule, '
           'while recording that the previous pin (0fe3212) refused the 2 unreferenced zero lanes')
    # Which writer's refusal text a manifest quotes (ModelGltfGeometry line 301 at 591d083, line 284 at 0fe3212). The
    # 0fe3212 string is the wastelandsettler03/m/go.nif partial reason of the 0fe3212 gate manifest, verbatim up to the
    # quoted text; the Blend string is the same file's 591d083 reason.
    quoted = [hop_b.manifest_tangent_refusal(reasons) for reasons in (
        ['convert: Unsupported: meshes/armor/wastelandsettler03/m/go.nif [occurrence: none] | Zero or nonfinite tangent directions or absent handedness '
         'cannot produce a strict tangent basis without inventing source data'],
        ['convert: Unsupported: x.nif [occurrence: none] | Referenced zero tangent directions, nonfinite directions, or absent handedness cannot '
         'produce a strict tangent basis without inventing source data.'],
        ['convert: Unsupported: meshes/armor/wastelandsettler03/m/go.nif [occurrence: none] | The recognized RGB candidate is Blend. The separate '
         'alpha equation does not match the candidate\'s alpha behavior.'],
        None)]
    expect(quoted == ['Shared 0fe3212', 'Shared 591d083', None, None],
           'manifest_tangent_refusal names the writer whose tangent-basis text a partial reason quotes, and None for another reason or none: %r' % quoted)
    d_w0, pairing_w0 = dump_variant('dump-unreferenced-w0.json', lambda p: p['tangents']['values'].__setitem__(3, [0, 0, 0, 0]))
    p, detail, _, c = geometry(good_glb, d_w0, pairing_w0)
    expect(p is False and c.mismatches[0].kind == 'unsupported' and d_w0.meshes[0]['primitives'][0].indices.tolist() == [0, 1, 2],
           'an UNREFERENCED zero tangent with w = 0 is still unsupported (the handedness test runs first): %s' % detail)
    d_ref, pairing_ref = dump_variant('dump-degenerate-reference.json', lambda p: p.__setitem__('indices', [0, 1, 2, 0, 4, 4]))
    p, detail, _, c = geometry(good_glb, d_ref, pairing_ref)
    expect(p is False and c.mismatches[0].kind == 'unsupported' and c.mismatches[0].where == label_a + '/TANGENT',
           'lane 4 referenced only by a degenerate triangle (0, 4, 4) is invalid, so unsupported: %s' % detail)

    # 3. Fidelity rows.
    def fidelity(rows, glb_bytes, dump_obj=d, pair=pairing):
        fp = os.path.join(work, 'fidelity-variant.json')
        with open(fp, 'w') as f:
            json.dump({'writers': [{'format': 'glb', 'fidelity': {'rows': rows}}]}, f)
        r = HopResult('B', 'synthetic')
        hop_b.check_fidelity(r, Glb(glb_bytes), fp, dump_obj, pair)
        c = r.checks[-1]
        return c.passed, (c.mismatches[0].where + ': ' + c.mismatches[0].detail) if c.mismatches else c.detail, c.data

    p, detail, data = fidelity(good_rows, good_glb)
    expect(p is True, 'the rows the writer would emit pass: %s' % detail)
    morph = [v for v in data['declaredValues'] if v.get('row') == 'geometry.normalized-base-tangent-morph-unbounded']
    expect(len(morph) == 1 and morph[0]['present'] and morph[0]['expected'] == [(0, 0, 0), (0, 0, 2)] and morph[0]['morphTargetsJudged'] == 4,
           'morph-tangent row expected exactly on t0 and t2 (tangent deltas, changed base), 4 targets judged: %r' % morph)
    placeholders = [v for v in data['declaredValues'] if v.get('row') == 'geometry.unreferenced-normal-placeholders' and 'primitivesJudged' in v]
    expect(len(placeholders) == 1 and placeholders[0]['present'] and placeholders[0]['expected'] == [(0, 0)] and placeholders[0]['primitivesJudged'] == 2,
           'placeholder row expected exactly on prim, both primitives judged: %r' % placeholders)
    tangent_set = [v for v in data['declaredValues'] if v.get('row') == 'geometry/tangents (row set)']
    expect(len(tangent_set) == 1 and tangent_set[0]['present'] and tangent_set[0]['primitivesJudged'] == 2 and tangent_set[0]['placeholderPrimitives'] == [[0, 0, 2]],
           'tangent row set judged on both primitives, placeholders on prim (2 lanes): %r' % tangent_set)
    counted = {v['row']: v for v in data['declaredValues'] if v.get('row') in ('geometry.unreferenced-tangents-filled', 'geometry.unreferenced-tangent-placeholders')}
    expect(counted.get('geometry.unreferenced-tangents-filled', {}).get('declaredCount') == 2 and counted.get('geometry.unreferenced-tangent-placeholders', {}).get('declaredCount') == 2
           and all(v['measuredCount'] == 2 and v['present'] for v in counted.values()),
           'Filled 2 and Exactly 2 parsed from the row texts and equal to the oracle\'s 2 unreferenced zero-direction lanes')

    def variant(mutator, rows=None):
        rows = copy.deepcopy(rows if rows is not None else good_rows)
        mutator(rows)
        return fidelity(rows, build_glb([expected_ta, expected_tb], [a_normals, b_normals], rows))

    p, detail, _ = variant(lambda rows: rows[0]['bounds'].__setitem__('observedError', t_deg + 1e-6))
    expect(p is False and 'declared' in detail, 'tangent observedError off by 1e-6 degrees fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[0].__setitem__('outcome', 'Degraded'))
    expect(p is False and 'threshold' in detail, 'Degraded below the 0.1 threshold fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[0].update({'reasonCode': 'geometry.tangents-preserved', 'outcome': 'Exact', 'bounds': None}))
    expect(p is False and 'expects geometry.tangents-normalized' in detail, 'tangents-preserved on a changed primitive fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[1].__setitem__('description', rows[1]['description'].replace('Filled 1', 'Filled 2')))
    expect(p is False and 'declares 1 reconstructed and 2 filled' in detail, 'wrong Filled count fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[1].update({'reasonCode': 'geometry.unreferenced-normals-filled', 'description': rows[1]['description'].replace('Reconstructed 1', 'Reconstructed 0')}))
    expect(p is False and 'expects geometry.zero-normals-reconstructed' in detail, 'unreferenced-normals-filled with a referenced zero lane fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[2].__setitem__('description', 'Exactly 3 zero-normal vertex slot(s) have no index references'))
    expect(p is False and 'declares 3' in detail, 'wrong placeholder count fails: %s' % detail)
    # Rows the writer can never emit (ModelGltfGeometry lines 195-239: the reconstruction branch and the normalized /
    # preserved rows are mutually exclusive, the placeholder row exists exactly when N > 0).
    p, detail, _ = variant(lambda rows: rows[1].update({'reasonCode': 'geometry.normals-normalized', 'outcome': 'Approximated',
                                                        'bounds': {'domain': 'normal direction', 'unit': 'degrees', 'maximumError': 0.1, 'observedError': 0.0, 'isWithinLimit': True}}))
    expect(p is False and 'reconstruction branch' in detail, 'normals-normalized on the zero-lane primitive fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[1].update({'reasonCode': 'geometry.normals-preserved', 'outcome': 'Exact'}))
    expect(p is False and 'expects geometry.zero-normals-reconstructed' in detail, 'normals-preserved on the zero-lane primitive fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[6].update({'reasonCode': 'geometry.unreferenced-normals-filled', 'outcome': 'Degraded',
                                                        'description': 'Reconstructed 0 zero normal(s) from area-weighted incident triangles. Filled 0 unused zero-normal slots with UnitY.'}))
    expect(p is False and 'declares 0 reconstructed and 0 filled' in detail and 'expects geometry.normals-preserved' in detail,
           'a reconstruction row declaring 0 and 0 on a primitive with no zero lane fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[6].update({'reasonCode': 'geometry.normals-normalized', 'outcome': 'Approximated',
                                                        'bounds': {'domain': 'normal direction', 'unit': 'degrees', 'maximumError': 0.1, 'observedError': 0.0, 'isWithinLimit': True}}))
    expect(p is False and 'expects geometry.normals-preserved' in detail, 'normals-normalized on an unchanged primitive fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[6].update({'reasonCode': 'geometry.normal-direction-absent', 'outcome': 'Degraded'}))
    expect(p is False and 'unreconstructible' in detail, 'normal-direction-absent beside an existing GLB fails as unsupported: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.pop(2))
    expect(p is False and 'expects the placeholder row on primitives [(0, 0)]' in detail, 'a missing placeholder row fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.append(row('Primitive', 0, 1, None, 'geometry/unreferenced-normal-slots', 'Degraded', 'geometry.unreferenced-normal-placeholders',
                                                        'Exactly 1 zero-normal vertex slot(s) have no index references')))
    expect(p is False and 'the oracle counts 0 unreferenced zero lanes' in detail, 'a placeholder row on a primitive without zero lanes fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.append(row('MorphTarget', 0, 0, 1, 'geometry/morph-tangent-deformation', 'Degraded', 'geometry.normalized-base-tangent-morph-unbounded', 'x')))
    expect(p is False and 'carries it on [(0, 0, 1)]' in detail, 'a morph-tangent row on a target with normal deltas only fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.append(row('MorphTarget', 0, 1, 0, 'geometry/morph-tangent-deformation', 'Degraded', 'geometry.normalized-base-tangent-morph-unbounded', 'x')))
    expect(p is False and 'carries it on [(0, 1, 0)]' in detail, 'a morph-tangent row on a primitive whose tangents did not change fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.pop(4))
    expect(p is False and 'expects the row on targets [(0, 0, 2)]' in detail, 'a missing morph-tangent row on t2 fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.append(row('MorphTarget', 0, 0, 1, 'geometry/morph-normal-deformation', 'Degraded', 'geometry.normalized-base-morph-unbounded', 'x')))
    expect(p is True, 'an unrelated morph-normal row is not judged: %s' % detail)

    # 3b. The tangent rows of pinned Shared 591d083 (ModelGltfGeometry lines 299-321). Row 7 is prim's filled row, row 8
    # its placeholder row, row 0 its normalization row under geometry/tangent-normalization.
    p, detail, _ = variant(lambda rows: rows[7].__setitem__('description', rows[7]['description'].replace('Filled 2', 'Filled 3')))
    expect(p is False and 'declares 3 unused zero-tangent slot(s)' in detail and 'counts 2' in detail, 'a wrong Filled count on the filled row fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[8].__setitem__('description', rows[8]['description'].replace('Exactly 2', 'Exactly 1')))
    expect(p is False and 'declares 1 unused zero-tangent slot(s)' in detail, 'a wrong Exactly count on the tangent placeholder row fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[8].__setitem__('description', 'Two zero-tangent vertex slots receive (1, 0, 0).'))
    expect(p is False and 'declares None' in detail, 'a placeholder row whose text carries no count fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.pop(8))
    expect(p is False and detail.startswith('fidelity/geometry/tangents: ') and 'expects exactly' in detail, 'a missing tangent placeholder row fails on the row set: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.pop(7))
    expect(p is False and 'expects exactly' in detail, 'a missing filled row (no geometry/tangents row beside the others) fails on the row set: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.pop(0))
    expect(p is False and 'expects exactly' in detail, 'a missing normalization row fails on the row set: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[0].__setitem__('featureId', 'geometry/tangents'))
    expect(p is False and 'under geometry/tangents, the tangent rule expects geometry.unreferenced-tangents-filled there' in detail,
           'the normalization row under geometry/tangents beside placeholders fails (line 317 moves it): %s' % detail)
    p, detail, _ = variant(lambda rows: (rows[0].__setitem__('featureId', 'geometry/tangents'), rows.pop(8), rows.pop(7)))
    expect(p is False and 'expects geometry.unreferenced-tangents-filled' in detail, 'the previous writer\'s row set (normalized only) fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[7].__setitem__('outcome', 'Approximated'))
    expect(p is False and 'expects Degraded with no bound' in detail, 'the filled row as Approximated fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[8].__setitem__('bounds', {'domain': 'tangent direction', 'unit': 'degrees', 'maximumError': 0.1, 'observedError': 0.0}))
    expect(p is False and 'expects Degraded with no bound' in detail, 'a bound on the tangent placeholder row fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.append(row('Primitive', 0, 1, None, 'geometry/unreferenced-tangent-slots', 'Degraded', 'geometry.unreferenced-tangent-placeholders',
                                                        'Exactly 1 zero-tangent vertex slot(s) have no index references')))
    expect(p is False and 'the tangent rule expects None there' in detail, 'a tangent placeholder row on the plain primitive fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows[7].update({'reasonCode': 'geometry.tangent-basis-unsupported', 'description': 'x'}))
    expect(p is False and 'declares the tangent basis unsupported' in detail, 'an unsupported row beside an existing GLB fails: %s' % detail)
    p, detail, _ = variant(lambda rows: [rows.pop(i) for i in (8, 7, 4, 3, 0)])
    expect(p is False and 'meshes/0/primitives/0 carries []' in detail, 'a primitive with tangents and no tangent row at all fails on the row set (the pairing names it): %s' % detail)

    # 3b'. Duplicated rows. The writer adds each geometry featureId at most once per target (ModelGltfGeometry lines
    # 195-239, 299-321 and 438-441), and the report and the carrier are one row list, so a duplicate must fail: until
    # 2026-09-25 rows were keyed by (target, featureId, outcome, reason) in a dict, which kept only the last copy, and
    # every case below passed.
    p, detail, _ = fidelity(good_rows + [copy.deepcopy(good_rows[7])], good_glb)
    expect(p is False and detail.startswith('fidelity/rows: ') and 'x2 against x1' in detail,
           'a filled row the report carries twice and the GLB carrier once fails the report-versus-carrier multiset: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.append(copy.deepcopy(rows[7])))
    expect(p is False and detail.startswith('fidelity/geometry/tangents: ') and 'expects exactly' in detail,
           'a filled row carried twice by both the report and the carrier fails the tangent row set: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.insert(7, dict(copy.deepcopy(rows[7]), description=rows[7]['description'].replace('Filled 2', 'Filled 7'))))
    expect(p is False and 'declares 7 unused zero-tangent slot(s)' in detail,
           'an extra filled row declaring Filled 7 ahead of the right Filled 2 row is judged itself and fails its count: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.append(copy.deepcopy(rows[8])))
    expect(p is False and detail.startswith('fidelity/geometry/tangents: ') and 'expects exactly' in detail,
           'a tangent placeholder row carried twice fails the tangent row set: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.append(copy.deepcopy(rows[1])))
    expect(p is False and detail.startswith('fidelity/geometry/normals: ') and 'expects exactly' in detail,
           'a geometry/normals row carried twice fails the exactly-one normals comparison: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.append(copy.deepcopy(rows[2])))
    expect(p is False and 'more than once on primitives [(0, 0)]' in detail, 'a normal placeholder row carried twice fails: %s' % detail)
    p, detail, _ = variant(lambda rows: rows.append(copy.deepcopy(rows[3])))
    expect(p is False and 'more than once on targets [(0, 0, 0)]' in detail, 'a morph-tangent row carried twice on t0 fails: %s' % detail)

    # 3c. Placeholders alone (lane 1 authored unit, so nothing is normalized): the rows are filled + placeholder with NO
    # normalization row, and the base still counts as changed (line 277), so t0 and t2 keep the morph-tangent row.
    a_unit = dict(A, tangents=[[1, 0, 0, 1], [0, 1, 0, 1], [0.6, 0.8, 0, -1], [0, 0, 0, -1], [-0.0, 0, -0.0, 1]])
    prims_unit = [('prim', a_unit), ('plain', B)]
    unit_dump_path = os.path.join(work, 'dump-placeholders-only.json')
    synthetic.write_json(unit_dump_path, synthetic.dump_document(prims_unit))
    d_unit = Dump(unit_dump_path)
    unit_ta, unit_deg, unit_changed, _ = rules.normalize_tangents(d_unit.meshes[0]['primitives'][0].tangents, d_unit.meshes[0]['primitives'][0].indices)
    expect(unit_changed and unit_deg == 0.0 and lane_bits(unit_ta[3]) == [0x3F800000, 0, 0, 0xBF800000] and unit_ta[1].tolist() == [0, 1, 0, 1],
           'placeholders-only prim: lanes 3 and 4 filled, lane 1 unchanged, changed flag set')
    unit_rows = [r for i, r in enumerate(copy.deepcopy(good_rows)) if i != 0]
    unit_glb = build_glb([unit_ta, expected_tb], [a_normals, b_normals], unit_rows, prims=prims_unit)
    unit_pairing = hop_b.GlbDumpPairing(Glb(unit_glb), d_unit)
    p, detail, _, _ = geometry(unit_glb, d_unit, unit_pairing)
    expect(p is True, 'placeholders-only GLB passes the accessor check: %s' % detail)
    p, detail, data = fidelity(unit_rows, unit_glb, d_unit, unit_pairing)
    expect(p is True, 'placeholders-only rows (filled, placeholder, no normalization row, morph rows on t0 and t2) pass: %s' % detail)
    with_normalization = unit_rows + [row('Primitive', 0, 0, None, 'geometry/tangent-normalization', 'Approximated', 'geometry.tangents-normalized', 'x',
                                          {'domain': 'tangent direction', 'unit': 'degrees', 'maximumError': 0.1, 'observedError': 0.0, 'isWithinLimit': True})]
    p, detail, _ = fidelity(with_normalization, build_glb([unit_ta, expected_tb], [a_normals, b_normals], with_normalization, prims=prims_unit), d_unit, unit_pairing)
    expect(p is False and 'the tangent rule expects None there' in detail, 'a normalization row where no lane was normalized fails: %s' % detail)
    no_morph = [r for r in unit_rows if r['featureId'] != 'geometry/morph-tangent-deformation']
    p, detail, _ = fidelity(no_morph, build_glb([unit_ta, expected_tb], [a_normals, b_normals], no_morph, prims=prims_unit), d_unit, unit_pairing)
    expect(p is False and 'expects the row on targets [(0, 0, 0), (0, 0, 2)]' in detail,
           'placeholders alone change the base (line 277), so dropping the morph-tangent rows fails: %s' % detail)

    # 4. Controls on the pristine GLB: each detected at exactly the mutated stream.
    def controls(glb_bytes, dump_obj=d, pair=pairing, name='controls'):
        r = HopResult('B', 'synthetic')
        hop_b.check_geometry(r, Glb(glb_bytes), dump_obj, pair)
        hop_b.run_controls(r, glb_bytes, Glb(glb_bytes), dump_obj, pair, None, os.path.join(work, name))
        by_name = {c['name']: c for c in r.controls}
        return (by_name[hop_b.CONTROL_TANGENT_W], by_name[hop_b.CONTROL_PLACEHOLDER], by_name[hop_b.CONTROL_TANGENT_PLACEHOLDER], by_name[hop_b.CONTROL_MASK])

    w, ph, tph, mask = controls(good_glb)
    expect(w['detected'] is True and w['mutation']['lane'] == 0 and w['mutation']['wBefore'] == 1.0 and w['mutation']['wAfter'] == -1.0
           and w['mutation'].get('tangentPlaceholderLanes') == 2 and w['expectedMismatchWhere'] == label_a + '/TANGENT'
           and w['firstMismatch']['where'] == label_a + '/TANGENT' and w['firstMismatch']['kind'] == 'value',
           'tangent w-flip control detected as a value mismatch at %s: %s' % (label_a + '/TANGENT', w['detail']))
    expect(ph['detected'] is True and ph['mutation']['lane'] == 3 and ph['firstMismatch']['where'] == label_a + '/' + hop_b.PLACEHOLDER_STREAM,
           'placeholder control detected on lane 3 at the placeholder stream: %s' % ph['detail'])
    expect(tph['detected'] is True and tph['mutation']['lane'] == 3 and tph['mutation']['placeholderLanes'] == 2
           and tph['mutation']['before'] == [1.0, 0.0, 0.0, -1.0] and tph['mutation']['after'] == [0.0, 1.0, 0.0, -1.0]
           and tph['expectedMismatchWhere'] == label_a + '/' + hop_b.TANGENT_PLACEHOLDER_STREAM
           and tph['firstMismatch']['where'] == tph['expectedMismatchWhere'] and tph['firstMismatch']['kind'] == 'value',
           'tangent placeholder control detected on lane 3 at the tangent placeholder stream: %s' % tph['detail'])
    expect(mask['detected'] is True and mask['expectedMismatchWhere'] == 'materials/0 (mask)/alphaCutoff'
           and mask['firstMismatch']['where'] == mask['expectedMismatchWhere'], 'MASK control detected at the cutoff: %s' % mask['detail'])
    with open(ph['files']['glb'], 'rb') as f:
        saved = Glb(f.read())
    expect(saved.accessor(1)[3].tolist() == [1.0, 0.0, 0.0], 'the saved placeholder-control GLB carries (1, 0, 0) on lane 3')
    with open(tph['files']['glb'], 'rb') as f:
        saved = Glb(f.read())
    expect(saved.accessor(4)[3].tolist() == [0.0, 1.0, 0.0, -1.0] and saved.accessor(4)[4].tolist() == [1.0, 0.0, 0.0, 1.0],
           'the saved tangent-placeholder-control GLB carries (0, 1, 0, -1) on lane 3 and leaves lane 4 alone')

    # 5. Masking: a pristine failure at or ahead of the mutated stream reads not applicable, never detected.
    broken = bytearray(good_glb)
    struct.pack_into('<f', broken, Glb(good_glb).chunks[1][1], 9.0)  # POSITION lane 0 x of prim
    w, ph, tph, _ = controls(bytes(broken), name='controls-position')
    expect(w['detected'] is None and 'pristine accessor check already fails' in w['detail'] and '/POSITION' in w['detail'],
           'a POSITION failure ahead of TANGENT: w-flip not applicable: %s' % w['detail'])
    expect(ph['detected'] is None and '/POSITION' in ph['detail'], 'a POSITION failure ahead of NORMAL: placeholder not applicable: %s' % ph['detail'])
    expect(tph['detected'] is None and '/POSITION' in tph['detail'], 'a POSITION failure ahead of TANGENT: tangent placeholder not applicable: %s' % tph['detail'])
    w, ph, tph, _ = controls(good_glb, d_bad, pairing_bad, name='controls-unsupported')
    expect(w['detected'] is None and 'already fails at %s/TANGENT (unsupported)' % label_a in w['detail'],
           'an unsupported tangent basis in the dump masks the w-flip (the old verdict read it as detected): %s' % w['detail'])
    expect(tph['detected'] is None and 'already fails at %s/TANGENT (unsupported)' % label_a in tph['detail'],
           'the same unsupported basis masks the tangent placeholder control: %s' % tph['detail'])
    expect(ph['detected'] is True, 'the placeholder control, ahead of the TANGENT failure, is still detected: %s' % ph['detail'])
    wrong_lane = build_glb([expected_ta, expected_tb], [normals_with(1, [0, 0, 9]), b_normals], good_rows)  # non-zero lane 1 wrong
    w, ph, tph, _ = controls(wrong_lane, name='controls-normal')
    expect(ph['detected'] is None and 'NORMAL (non-zero source lanes) (value)' in ph['detail'],
           'a wrong non-zero normal lane masks the placeholder control (the old verdict read it as detected): %s' % ph['detail'])
    expect(w['detected'] is None and 'non-zero source lanes' in w['detail'], 'the same failure, ahead of TANGENT, masks the w-flip: %s' % w['detail'])
    expect(tph['detected'] is None and 'non-zero source lanes' in tph['detail'], 'and masks the tangent placeholder control: %s' % tph['detail'])
    other_tangent = tangents_with(2, [0.6, 0.8, 0.5, -1])  # lane 2 wrong, lane 0 (the flipped one) and the placeholders right
    w, ph, tph, _ = controls(build_glb([other_tangent, expected_tb], [a_normals, b_normals], good_rows), name='controls-tangent')
    expect(w['detected'] is None and 'already fails at %s/TANGENT (value)' % label_a in w['detail'],
           'a pristine value mismatch on another lane of the same TANGENT stream masks the w-flip: %s' % w['detail'])
    expect(tph['detected'] is None and 'already fails at %s/TANGENT (value)' % label_a in tph['detail'],
           'the authored TANGENT lanes are compared ahead of the placeholders, so the same mismatch masks the tangent placeholder control: %s' % tph['detail'])
    expect(ph['detected'] is True, 'the placeholder control, ahead of that TANGENT failure, is still detected: %s' % ph['detail'])
    wrong_placeholder = tangents_with(4, [1.0, 0.0, 0.0, -1.0])  # the stored placeholder lane 4 has the wrong handedness
    w, ph, tph, _ = controls(build_glb([wrong_placeholder, expected_tb], [a_normals, b_normals], good_rows), name='controls-placeholder-tangent')
    expect(tph['detected'] is None and 'already fails at %s/%s (value)' % (label_a, hop_b.TANGENT_PLACEHOLDER_STREAM) in tph['detail'],
           'a wrong stored tangent placeholder masks the tangent placeholder control: %s' % tph['detail'])
    expect(w['detected'] is True, 'the w-flip, on the authored lanes compared first, is still detected: %s' % w['detail'])

    # 5b. The w flip avoids a placeholder lane. A primitive whose triangle (1, 2, 3) leaves lane 0 unreferenced with a
    # zero tangent: the flip lands on lane 1 and is detected at TANGENT; flipping lane 0 (what the control did before the
    # placeholders existed) reports on the placeholder stream, so the verdict for TANGENT reads NOT detected.
    c_spec = {'positions': [[0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1]], 'normals': [[0, 0, 1]] * 4,
              'tangents': [[0, 0, 0, 1], [1, 0, 0, 1], [1, 0, 0, -1], [0, 1, 0, 1]], 'uvs': [[0, 0], [1, 0], [0, 1], [1, 1]],
              'indices': [1, 2, 3], 'targets': []}
    prims_c = [('prim', c_spec), ('plain', B)]
    c_dump_path = os.path.join(work, 'dump-lane0-placeholder.json')
    synthetic.write_json(c_dump_path, synthetic.dump_document(prims_c))
    d_c = Dump(c_dump_path)
    c_ta, _, _, _ = rules.normalize_tangents(d_c.meshes[0]['primitives'][0].tangents, d_c.meshes[0]['primitives'][0].indices)
    c_glb = build_glb([c_ta, expected_tb], [c_spec['normals'], b_normals], [], prims=prims_c)
    c_pairing = hop_b.GlbDumpPairing(Glb(c_glb), d_c)
    c_pristine = HopResult('B', 'synthetic')
    hop_b.check_geometry(c_pristine, Glb(c_glb), d_c, c_pairing)
    expect(c_pristine.checks[-1].passed is True and c_ta[0].tolist() == [1, 0, 0, 1], 'lane-0 placeholder set passes its pristine accessor check: %s' % c_pristine.checks[-1].detail)
    control = hop_b.run_geometry_control(hop_b.CONTROL_TANGENT_W, 'synthetic', c_glb, d_c, c_pairing, c_pristine.checks[-1], os.path.join(work, 'controls-lane0'))
    expect(control['detected'] is True and control['mutation']['lane'] == 1 and control['mutation']['tangentPlaceholderLanes'] == 1,
           'the w flip skips placeholder lane 0 and is detected on lane 1: %s' % control['detail'])
    flipped_lane0, description = hop_b.mutate_tangent_w(c_glb)
    probe = HopResult('B', 'synthetic')
    hop_b.check_geometry(probe, Glb(flipped_lane0), d_c, c_pairing)
    verdict, why = hop_b.geometry_control_verdict(probe, c_pristine.checks[-1], hop_b.mutated_stream(d_c, c_pairing, description, hop_b.TANGENT_STREAM))
    expect(description['lane'] == 0 and verdict is False and hop_b.TANGENT_PLACEHOLDER_STREAM in why,
           'flipping lane 0 without the dump lands on the placeholder and reads NOT detected at TANGENT (why the lane is chosen): %s' % why)

    # 6. The driver's synthetic exercise (hop_b.synthetic_control_exercise) is this same set, mutation and verdict:
    # the three accessor controls are detected at the mutated stream and the MASK control at the cutoff, on artifacts
    # whose SHA-256 is the pristine set's.
    for name, where in ((hop_b.CONTROL_TANGENT_W, label_a + '/' + hop_b.TANGENT_STREAM),
                        (hop_b.CONTROL_PLACEHOLDER, label_a + '/' + hop_b.PLACEHOLDER_STREAM),
                        (hop_b.CONTROL_TANGENT_PLACEHOLDER, label_a + '/' + hop_b.TANGENT_PLACEHOLDER_STREAM),
                        (hop_b.CONTROL_MASK, 'materials/0 (mask)/alphaCutoff')):
        ex = hop_b.synthetic_control_exercise(name, os.path.join(work, 'synthetic', name[:24].replace(' ', '_')))
        expect(ex['detected'] is True and ex['expectedMismatchWhere'] == where
               and ex['firstMismatch']['where'] == ex['expectedMismatchWhere'] and ex['firstMismatch']['kind'] == 'value',
               'synthetic exercise of %r detected at %s: %s' % (name, ex['expectedMismatchWhere'], ex['detail']))
        expect(ex['artifactSha256']['glb'] == sha256_bytes(good_glb) and ex['artifactSha256']['dump'] == sha256_file(pristine['dump'])
               and ex['artifactSha256']['fidelity'] == sha256_file(pristine['fidelity']) and ex['artifactSha256']['mutatedGlb'] not in (None, ex['artifactSha256']['glb']),
               'synthetic exercise artifacts are the pristine set (mutated copy differs): %r' % ex['artifactSha256'])
        expect([c['status'] for c in ex['pristineChecks']] == ['pass', 'pass', 'pass'] and ex['pristinePassed'] is True and ex['verdict'] is True,
               'synthetic exercise pristine accessor, fidelity and material checks pass: %r' % [c['name'] for c in ex['pristineChecks']])
    expect(hop_b.synthetic_control_exercise('swapped BC1 endpoints re-encoded into a copy of the GLB PNG') is None,
           'a control without a synthetic exercise returns None')

    # 7. A synthetic set that drifted out of step with writer_rules cannot carry a control: the exercise runs the same
    # verdict, but a detection on a set whose own pristine check fails reads NOT detected (the corpus path fails the hop
    # on a pristine failure; the synthetic path must not be softer). Four drifts: prim's stored TANGENT lane 0 wrong (the
    # accessor check fails at TANGENT, which is compared AFTER the placeholder stream, so the placeholder verdict alone
    # still says detected), a stored tangent placeholder that is the source's zero lane, a fidelity report missing a row
    # (the accessor check passes, the fidelity check fails), and a wrong stored MASK cutoff.
    def drifted_tangent(work_dir, lane=0, value=(0.0, 1.0, 0.0, 1.0)):
        a = synthetic.write_pristine_set(work_dir)
        ta = [list(map(float, v)) for v in a['expectedTangents'][0]]
        ta[lane] = list(value)
        glb_bytes = build_glb([ta, a['expectedTangents'][1]], a['writtenNormals'], a['rows'])
        with open(a['glb'], 'wb') as f:
            f.write(glb_bytes)
        a['glbBytes'] = glb_bytes
        a['sha256']['glb'] = sha256_bytes(glb_bytes)
        return a

    def drifted_fidelity(work_dir):
        a = synthetic.write_pristine_set(work_dir)
        synthetic.write_json(a['fidelity'], synthetic.fidelity_report(a['rows'][:-1]))
        a['sha256']['fidelity'] = sha256_file(a['fidelity'])
        return a

    def drifted_mask(work_dir):
        a = synthetic.write_pristine_set(work_dir)
        materials = [dict(synthetic.GLB_MATERIALS[0], alphaCutoff=synthetic.MASK_RAW / 255.0)]
        glb_bytes = build_glb(a['expectedTangents'], a['writtenNormals'], a['rows'], materials=materials)
        with open(a['glb'], 'wb') as f:
            f.write(glb_bytes)
        a['glbBytes'] = glb_bytes
        a['sha256']['glb'] = sha256_bytes(glb_bytes)
        return a

    ex = hop_b.synthetic_control_exercise(hop_b.CONTROL_PLACEHOLDER, os.path.join(work, 'synthetic-drift', 'tangent'), pristine_set=drifted_tangent)
    expect(ex['verdict'] is True and ex['detected'] is False and ex['pristinePassed'] is False
           and [c['status'] for c in ex['pristineChecks']] == ['fail', 'pass', 'pass']
           and ex['pristineChecks'][0]['firstMismatch']['where'] == label_a + '/TANGENT' and 'out of step with writer_rules' in ex['detail'],
           'a drifted TANGENT lane: the placeholder verdict alone says detected, the exercise reads NOT detected: %s' % ex['detail'])
    ex = hop_b.synthetic_control_exercise(hop_b.CONTROL_TANGENT_W, os.path.join(work, 'synthetic-drift', 'tangent-w'), pristine_set=drifted_tangent)
    expect(ex['verdict'] is None and ex['detected'] is False and ex['pristinePassed'] is False,
           'the same drift masks the w-flip (not applicable) and the exercise reads NOT detected: %s' % ex['detail'])
    ex = hop_b.synthetic_control_exercise(hop_b.CONTROL_TANGENT_PLACEHOLDER, os.path.join(work, 'synthetic-drift', 'tangent-placeholder'), pristine_set=drifted_tangent)
    expect(ex['verdict'] is None and ex['detected'] is False, 'the same drift masks the tangent placeholder control: %s' % ex['detail'])

    def drifted_placeholder(work_dir):
        return drifted_tangent(work_dir, lane=3, value=(0.0, 0.0, 0.0, -1.0))
    ex = hop_b.synthetic_control_exercise(hop_b.CONTROL_TANGENT_PLACEHOLDER, os.path.join(work, 'synthetic-drift', 'placeholder-source'), pristine_set=drifted_placeholder)
    expect(ex['verdict'] is None and ex['detected'] is False and ex['pristinePassed'] is False
           and ex['pristineChecks'][0]['firstMismatch']['where'] == label_a + '/' + hop_b.TANGENT_PLACEHOLDER_STREAM,
           'a stored tangent placeholder left as the source zero lane masks its control and the exercise reads NOT detected: %s' % ex['detail'])
    ex = hop_b.synthetic_control_exercise(hop_b.CONTROL_PLACEHOLDER, os.path.join(work, 'synthetic-drift', 'placeholder-source-normal'), pristine_set=drifted_placeholder)
    expect(ex['verdict'] is True and ex['detected'] is False, 'on that drift the normal placeholder verdict is a detection, the exercise still reads NOT detected: %s' % ex['detail'])
    ex = hop_b.synthetic_control_exercise(hop_b.CONTROL_PLACEHOLDER, os.path.join(work, 'synthetic-drift', 'fidelity'), pristine_set=drifted_fidelity)
    expect(ex['verdict'] is True and ex['detected'] is False and ex['pristinePassed'] is False
           and [c['status'] for c in ex['pristineChecks']] == ['pass', 'fail', 'pass'] and ex['pristineChecks'][1]['firstMismatch']['where'] == 'fidelity/rows',
           'a drifted fidelity report: the accessor check passes, the fidelity check fails, the exercise reads NOT detected: %s' % ex['detail'])
    ex = hop_b.synthetic_control_exercise(hop_b.CONTROL_MASK, os.path.join(work, 'synthetic-drift', 'mask'), pristine_set=drifted_mask)
    expect(ex['verdict'] is True and ex['detected'] is False and ex['pristinePassed'] is False and ex['pristineChecks'][2]['status'] == 'fail'
           and ex['pristineChecks'][2]['firstMismatch']['where'] == 'materials/0 (mask)/alphaCutoff',
           'a drifted MASK cutoff: the MASK verdict is a detection, the material check fails, the exercise reads NOT detected: %s' % ex['detail'])

    if FAILURES:
        print('%d expectation(s) failed (work dir %s)' % (len(FAILURES), work))
        return 1
    print('all expectations hold (work dir %s)' % work)
    return 0


if __name__ == '__main__':
    sys.exit(main())
