# SPDX-License-Identifier: 0BSD
"""Self-check of the tangent and direction rules in ``writer_rules.py`` on hand vectors.

Pins ``normalize_tangents`` / ``classify_tangents`` / ``tangent_plan`` / ``expected_tangent_rows`` /
``referenced_vertices`` (ModelGltfGeometry.NormalizeTangents and ReferencedVertices, pinned Shared 591d083) and
``direction_degrees`` (ModelGltfGeometry.DirectionDegrees, the atan2 form) against values worked out by hand:

  * (2, 0, 0, 1) is normalized to (1, 0, 0, 1) at exactly 0 degrees;
  * (0.6, 0.8, 0, -1) is unchanged bit for bit, its handedness kept;
  * the float32 tolerance boundary: a direction whose float32 squared length is 838 ulps above 1 is inside
    0.0001f (838.86 ulps) and unchanged, one 840 ulps above (or below) is outside and normalized;
  * a REFERENCED zero direction (either zero sign), a NaN component, an infinite component, and a handedness of 0.5,
    0 or NaN are invalid;
  * (3, 4, 0, 1) normalizes to (0.6f, 0.8f, 0, 1) with a direction error the atan2 form measures as about
    6.8e-7 degrees while the previous acos form measures 0;
  * direction_degrees((1, 1e-9, 0), (1, 0, 0)) is about 5.7e-8 degrees under atan2 and 0 under acos;
  * the previous pin's quantization steps are acos(1 - k * 2^-53): 8.5377e-7 degrees at k = 1 (doubles below 1 are
    spaced 2^-53, so the odd-k values exist; 2^-52 would give only the even ones);
  * exact angles: 0, 45, 90 and 180 degrees;
  * unreferenced zero-direction lanes (1c2f5b7): (0, 0, 0, +1) becomes exactly (1, +0, +0, +1) and (-0, 0, -0, -1)
    exactly (1, +0, +0, -1) (bits 0x3F800000 0 0 0x3F800000 / 0xBF800000), counted as placeholders and setting the
    changed flag; the same lane referenced by any index, a degenerate triangle included, stays invalid; an unreferenced
    zero lane whose w is 0, 0.5, -0.999, NaN or infinite, or whose direction is not finite, stays invalid (the
    handedness and finiteness test runs first); the smallest subnormal direction is not a zero direction (its double
    squared length is not 0) and is normalized to (1, 0, 0) instead; an out-of-domain or negative index raises only when
    a zero lane makes the writer build its reference mask (line 274);
  * the rows for every branch: preserved; normalized under geometry/tangents; filled + placeholder rows; filled +
    placeholder + normalization under geometry/tangent-normalization; unsupported alone; unsupported + placeholder row
    (line 312 has no invalid guard) without a normalization row; Approximated at exactly 0.1 degrees and Degraded
    above it, under either featureId;
  * hop E's use of the rule (``hop_e.compare_geometry`` on the ``gate1a_synthetic`` GLB and a package of the same
    source lanes, ``gate1a_synthetic.build_package``): the GLB whose unreferenced zero lanes carry (1, 0, 0, w), written
    out by hand, passes with TANGENT compared on both primitives; a wrong placeholder direction, the source zero lane
    copied through and a flipped placeholder handedness each fail as a TANGENT value mismatch on that lane; and a zero
    lane that the PACKAGE's index stream references (a real triangle, or only a degenerate one) is refused as
    unsupported even though the GLB carries the placeholder there, so hop E cannot pass a referenced zero tangent by
    treating every lane as unreferenced.

Exit status 1 on any failure (every failed expectation is printed), 0 when all hold.
"""

import math
import os
import sys
import tempfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import gate1a_synthetic as synthetic  # noqa: E402
import hop_e  # noqa: E402
import writer_rules as rules  # noqa: E402
from gate1a_common import HopResult  # noqa: E402
from glb_reader import Glb  # noqa: E402
from package_reader import Package  # noqa: E402

FAILURES = []


def expect(condition, message):
    if not condition:
        FAILURES.append(message)
        print('FAIL: ' + message)
    else:
        print('ok:   ' + message)


def bits(values):
    return np.ascontiguousarray(values, dtype=np.float32).reshape(-1).view(np.uint32)


def same_bits(actual, expected):
    a = bits(actual)
    e = bits(expected)
    return a.shape == e.shape and bool(np.array_equal(a, e))


def f32_from_ulps_above_one(ulps):
    """The float32 exactly ``ulps`` ulps above 1.0 (positive) or below 1.0 (negative; the spacing below 1 is 2^-24)."""
    if ulps >= 0:
        return np.array([0x3F800000 + ulps], dtype=np.uint32).view(np.float32)[0]
    return np.array([0x3F800000 - (-ulps)], dtype=np.uint32).view(np.float32)[0]


def acos_form_degrees(source, output):
    """The previous writer's formula: acos(clamp(dot / sqrt(|s|^2 |o|^2))) in double, for the comparison only."""
    s = np.asarray(source, dtype=np.float32).astype(np.float64)
    o = np.asarray(output, dtype=np.float32).astype(np.float64)
    dot = float((s * o).sum())
    denominator = math.sqrt(float((s * s).sum()) * float((o * o).sum()))
    return math.degrees(math.acos(max(-1.0, min(1.0, dot / denominator))))


def check_single(vector, expected, changed, invalid, message):
    """One lane that a triangle (0, 0, 0) references."""
    out, degrees, is_changed, invalid_count = rules.normalize_tangents(np.array([vector], dtype=np.float32), [0, 0, 0])
    if invalid:
        expect(out is None and invalid_count == 1 and degrees == 0.0, message + ': invalid (expected None, 1 invalid lane)')
        return None
    expect(out is not None and invalid_count == 0, message + ': valid')
    if out is None:
        return None
    expect(same_bits(out[0], np.array(expected, dtype=np.float32)), message + ': output bits %r (got %r)' % (expected, out[0].tolist()))
    expect(bool(is_changed) == changed, message + ': changed flag %r' % changed)
    return degrees


def main():
    # 1. A direction of length 2 normalizes exactly, at 0 degrees.
    degrees = check_single([2, 0, 0, 1], [1, 0, 0, 1], True, False, '(2,0,0,1) -> (1,0,0,1)')
    expect(degrees == 0.0, '(2,0,0,1): 0 degrees exactly (got %r)' % degrees)

    # 2. A unit direction with negative handedness is untouched, w included.
    vector = np.array([0.6, 0.8, 0, -1], dtype=np.float32)
    degrees = check_single(vector, vector, False, False, '(0.6,0.8,0,-1) unchanged')
    expect(degrees == 0.0, '(0.6,0.8,0,-1): 0 degrees (got %r)' % degrees)

    # 3. The float32 tolerance boundary. x = 1 + k ulps squares to 1 + 2k ulps (k^2 * 2^-46 is far below one ulp), and
    #    0.0001f is 838.86 ulps of 1, so k = 419 (838 ulps) is inside and k = 420 (840 ulps) is outside.
    inside = f32_from_ulps_above_one(419)
    outside = f32_from_ulps_above_one(420)
    squared_inside = (inside * inside + np.float32(0) * np.float32(0)) + np.float32(0) * np.float32(0)
    squared_outside = (outside * outside + np.float32(0) * np.float32(0)) + np.float32(0) * np.float32(0)
    expect(np.abs(squared_inside - np.float32(1)) <= np.float32(0.0001), 'boundary: 1 + 419 ulps squares inside 0.0001f (|s-1| = %r)' % float(np.abs(squared_inside - np.float32(1))))
    expect(np.abs(squared_outside - np.float32(1)) > np.float32(0.0001), 'boundary: 1 + 420 ulps squares outside 0.0001f (|s-1| = %r)' % float(np.abs(squared_outside - np.float32(1))))
    check_single([inside, 0, 0, 1], [inside, 0, 0, 1], False, False, 'boundary inside: (1 + 419 ulps, 0, 0, 1) unchanged')
    degrees = check_single([outside, 0, 0, 1], [1, 0, 0, 1], True, False, 'boundary outside: (1 + 420 ulps, 0, 0, 1) -> (1, 0, 0, 1)')
    expect(degrees == 0.0, 'boundary outside: 0 degrees (got %r)' % degrees)
    below = f32_from_ulps_above_one(-840)  # 1 - 840 * 2^-24 squares to 1 - 840 * 2^-23: outside on the low side
    degrees = check_single([0, below, 0, -1], [0, 1, 0, -1], True, False, 'boundary below: (0, 1 - 840 ulps, 0, -1) -> (0, 1, 0, -1)')
    expect(degrees == 0.0, 'boundary below: 0 degrees (got %r)' % degrees)
    check_single([0, f32_from_ulps_above_one(-838), 0, -1], [0, f32_from_ulps_above_one(-838), 0, -1], False, False,
                 'boundary below inside: (0, 1 - 838 ulps, 0, -1) unchanged')

    # 4. Invalid lanes: the item is unsupported, no expected array. The lane is referenced (check_single), so a zero
    #    direction stays invalid at pinned Shared 591d083 (ModelGltfGeometry line 275).
    check_single([0, 0, 0, 1], None, False, True, 'referenced zero direction')
    check_single([-0.0, 0, -0.0, -1], None, False, True, 'referenced negative-zero direction')
    check_single([float('nan'), 0, 0, 1], None, False, True, 'NaN component')
    check_single([1, float('inf'), 0, 1], None, False, True, 'infinite component')
    check_single([1, 0, 0, 0.5], None, False, True, 'w = 0.5')
    check_single([1, 0, 0, 0], None, False, True, 'w = 0')
    check_single([1, 0, 0, float('nan')], None, False, True, 'w = NaN')

    # 5. Normalization with a measurable float32 rounding: (3, 4, 0) -> (0.6f, 0.8f, 0). The cross product of the
    #    source and the rounded output is about 6e-8 while the cosine rounds to 1.0, so acos reads 0 and atan2 does not.
    degrees = check_single([3, 4, 0, 1], [0.6, 0.8, 0, 1], True, False, '(3,4,0,1) -> (0.6f, 0.8f, 0, 1)')
    expect(degrees is not None and 6.8e-7 < degrees < 6.9e-7, '(3,4,0,1): atan2 direction error about 6.83e-7 degrees (got %r)' % degrees)
    acos_degrees = acos_form_degrees([3, 4, 0], [np.float32(0.6), np.float32(0.8), 0])
    expect(acos_degrees == 0.0, '(3,4,0,1): the previous acos form reads 0 for the same pair (got %r)' % acos_degrees)
    direct = rules.direction_degrees([3, 4, 0], [np.float32(0.6), np.float32(0.8), 0])
    expect(direct == degrees, '(3,4,0,1): direction_degrees agrees with the normalization measurement (%r vs %r)' % (direct, degrees))

    # 6. The tiny-angle case on direction_degrees itself.
    tiny = rules.direction_degrees([1, 1e-9, 0], [1, 0, 0])
    expect(5.7e-8 < tiny < 5.8e-8, 'direction_degrees((1,1e-9,0),(1,0,0)) about 5.73e-8 degrees under atan2 (got %r)' % tiny)
    expect(acos_form_degrees([1, 1e-9, 0], [1, 0, 0]) == 0.0, 'the acos form reads 0 degrees for (1,1e-9,0) versus (1,0,0)')
    expect(rules.previous_pin_direction_degrees([1, 1e-9, 0], [1, 0, 0]) == 0.0 and
           rules.previous_pin_direction_degrees([3, 4, 0], [np.float32(0.6), np.float32(0.8), 0]) == 0.0 and
           abs(rules.previous_pin_direction_degrees([1, 1, 0], [1, 0, 0]) - 45.0) < 1e-12,
           'the diagnostic previous-pin acos form (writer_rules) agrees with this file\'s independent acos form: 0, 0 and 45 degrees')
    expect(rules.previous_pin_normal_degrees([[0, 0, 2], [3, 4, 0]], 'Smooth') == 0.0 and
           rules.normalize_normals([[0, 0, 2], [3, 4, 0]], 'Smooth')[1] == rules.direction_degrees([3, 4, 0], [np.float32(0.6), np.float32(0.8), 0]),
           'normal rows: the previous pin reads 0 for (3,4,0) while the pinned atan2 measurement is about 6.83e-7 degrees')
    expect(tiny == float(np.float32(1e-9)) * (180 / math.pi) or abs(tiny - float(np.float32(1e-9)) * (180 / math.pi)) < 1e-22,
           'the tiny angle equals atan2(f32(1e-9), 1) in degrees')

    # 6b. The previous pin's quantization: the smallest non-zero acos-form reading is acos(1 - 2^-53) = 2^-26 radians,
    #     8.5377e-7 degrees, and the six values the 2026-09-24 reports declare are k = 1, 2, 3, 4, 6, 8 of that step.
    steps = {k: math.degrees(math.acos(1 - k * 2 ** -53)) for k in (1, 2, 3, 4, 6, 8)}
    expect(abs(steps[1] - 8.5377e-7) < 1e-11 and abs(math.degrees(2 ** -26) - steps[1]) < 1e-15,
           'acos(1 - 2^-53) is 2^-26 radians = 8.5377e-7 degrees (got %r)' % steps[1])
    expect(all(abs(steps[k] - v) < 1e-10 for k, v in ((2, 1.2074e-6), (3, 1.4788e-6), (4, 1.7075e-6), (6, 2.0913e-6), (8, 2.4148e-6))),
           'the quantized acos-form values for k = 2, 3, 4, 6, 8 are 1.2074e-6, 1.4788e-6, 1.7075e-6, 2.0913e-6, 2.4148e-6 degrees')
    expect(math.degrees(math.acos(1 - 2 ** -52)) == steps[2], 'acos(1 - 2^-52) is the k = 2 step, not the smallest one')

    # 7. Exact angles.
    expect(rules.direction_degrees([1, 0, 0], [1, 0, 0]) == 0.0, 'identical directions: 0 degrees')
    expect(rules.direction_degrees([2, 0, 0], [1, 0, 0]) == 0.0, 'parallel directions of different length: 0 degrees')
    expect(abs(rules.direction_degrees([1, 1, 0], [1, 0, 0]) - 45.0) < 1e-12, '(1,1,0) versus (1,0,0): 45 degrees')
    expect(abs(rules.direction_degrees([1, 0, 0], [0, 1, 0]) - 90.0) < 1e-12, 'perpendicular directions: 90 degrees')
    expect(abs(rules.direction_degrees([1, 0, 0], [-1, 0, 0]) - 180.0) < 1e-12, 'opposite directions: 180 degrees')
    array = rules.direction_degrees(np.array([[1, 0, 0], [1, 1, 0]], dtype=np.float32), np.array([[1, 0, 0], [1, 0, 0]], dtype=np.float32))
    expect(array.shape == (2,) and array[0] == 0.0 and abs(array[1] - 45.0) < 1e-12, 'direction_degrees over an (N, 3) array')

    # 8. A stacked array: the maximum is over the changed lanes, an invalid lane voids the whole array, and the lane
    #    classes agree with the single-lane results.
    stacked = np.array([[2, 0, 0, 1], [0.6, 0.8, 0, -1], [3, 4, 0, 1], [inside, 0, 0, 1]], dtype=np.float32)
    every = [0, 1, 2, 1, 2, 3]
    out, degrees, changed, invalid = rules.normalize_tangents(stacked, every)
    expect(invalid == 0 and changed and out is not None, 'stacked valid lanes: changed, none invalid')
    expect(out is not None and same_bits(out, np.array([[1, 0, 0, 1], [0.6, 0.8, 0, -1], [0.6, 0.8, 0, 1], [inside, 0, 0, 1]], dtype=np.float32)),
           'stacked valid lanes: expected bits per lane')
    expect(6.8e-7 < degrees < 6.9e-7, 'stacked valid lanes: maximum degrees is the (3,4,0) lane (got %r)' % degrees)
    invalid_mask, changed_mask, placeholder_mask, squared = rules.classify_tangents(stacked, every)
    expect(invalid_mask.tolist() == [False] * 4 and changed_mask.tolist() == [True, False, True, False] and placeholder_mask.tolist() == [False] * 4
           and squared.tolist() == [4.0, float(np.float64(np.float32(0.6)) ** 2 + np.float64(np.float32(0.8)) ** 2), 25.0, float(np.float64(inside) ** 2)],
           'classify_tangents: masks and double squared lengths')
    with_invalid = np.vstack([stacked, np.array([[0, 0, 0, 1]], dtype=np.float32)])
    out, degrees, changed, invalid = rules.normalize_tangents(with_invalid, every + [4, 4, 4])
    expect(out is None and invalid == 1 and changed is True, 'one invalid lane (a referenced zero direction) voids the array (None, 1 invalid) while the changed flag still reports the other lanes')
    out, degrees, changed, invalid = rules.normalize_tangents(np.zeros((0, 4), dtype=np.float32), [])
    expect(out is not None and out.shape == (0, 4) and degrees == 0.0 and not changed and invalid == 0, 'an empty tangent set is valid and unchanged')

    # 9. Unreferenced zero-direction lanes (pinned Shared 591d083, 1c2f5b7). A four-lane primitive whose triangle
    #    (0, 1, 2) leaves lane 3 unreferenced; lanes 0-2 are unit and unchanged, so every difference is lane 3's.
    unit = [[1, 0, 0, 1], [0, 1, 0, -1], [0.6, 0.8, 0, 1]]
    triangle = [0, 1, 2]

    def lane3(vector, indices=triangle):
        return rules.tangent_plan(np.array(unit + [vector], dtype=np.float32), indices)

    plan = lane3([0, 0, 0, 1])
    expect(plan['invalidCount'] == 0 and plan['unreferencedCount'] == 1 and plan['normalizedCount'] == 0 and plan['changed'] is True
           and plan['placeholder'].tolist() == [False, False, False, True] and plan['maxDegrees'] == 0.0,
           'unreferenced (0, 0, 0, +1): one placeholder, nothing normalized, changed (line 277)')
    expect(plan['expected'] is not None and bits(plan['expected'][3]).tolist() == [0x3F800000, 0, 0, 0x3F800000]
           and same_bits(plan['expected'][:3], np.array(unit, dtype=np.float32)),
           'unreferenced (0, 0, 0, +1) -> (1, +0, +0, +1) bit for bit (0x3F800000 0 0 0x3F800000); the other lanes keep their bits')
    plan = lane3([-0.0, 0, -0.0, -1])
    expect(plan['expected'] is not None and bits(plan['expected'][3]).tolist() == [0x3F800000, 0, 0, 0xBF800000],
           'unreferenced (-0, 0, -0, -1) -> (1, +0, +0, -1): the negative zeros do not survive, the authored -1 does (0xBF800000)')
    out, degrees, changed, invalid = rules.normalize_tangents(np.array(unit + [[0, 0, 0, -1]], dtype=np.float32), triangle)
    expect(out is not None and invalid == 0 and changed is True and degrees == 0.0 and out[3].tolist() == [1, 0, 0, -1],
           'normalize_tangents carries the placeholder and the changed flag')
    for w in (0.0, 0.5, -0.999, float('nan'), float('inf')):
        plan = lane3([0, 0, 0, w])
        expect(plan['invalidCount'] == 1 and plan['unreferencedCount'] == 0 and plan['expected'] is None,
               'unreferenced zero direction with w = %r stays invalid (the handedness test, line 270, runs first)' % w)
    for vector in ([float('nan'), 0, 0, 1], [0, float('inf'), 0, -1], [0, 0, float('-inf'), 1]):
        plan = lane3(vector)
        expect(plan['invalidCount'] == 1 and plan['unreferencedCount'] == 0, 'unreferenced nonfinite direction %r stays invalid' % vector)
    plan = lane3([0, 0, 0, 1], [0, 1, 2, 0, 3, 3])
    expect(plan['invalidCount'] == 1 and plan['unreferencedCount'] == 0 and plan['expected'] is None,
           'a zero lane referenced only by a degenerate triangle (0, 3, 3) is referenced, so invalid (every index counts)')
    tiny = np.array([0x00000001], dtype=np.uint32).view(np.float32)[0]
    plan = lane3([tiny, 0, 0, 1])
    expect(plan['unreferencedCount'] == 0 and plan['normalizedCount'] == 1 and plan['invalidCount'] == 0
           and plan['expected'][3].tolist() == [1.0, 0.0, 0.0, 1.0] and [(r['featureId'], r['reasonCode']) for r in plan['rows']] == [('geometry/tangents', 'geometry.tangents-normalized')],
           'the smallest subnormal direction (squared 1.96e-90 in double, not 0) is normalized to (1, 0, 0), not a placeholder')
    raised = []
    for indices in ([0, 1, 4], [0, 1, -1]):
        try:
            lane3([0, 0, 0, 1], indices)
        except ValueError as failure:
            raised.append(str(failure)[:40])
    expect(len(raised) == 2, 'an out-of-domain index (4) and a negative index (-1) raise when a zero lane builds the mask: %r' % raised)
    plan = rules.tangent_plan(np.array(unit + [[1, 0, 0, 1]], dtype=np.float32), [0, 1, 4])
    expect(plan['invalidCount'] == 0 and plan['unreferencedCount'] == 0, 'without a zero lane no mask is built (line 274), so the same bad index does not raise in the tangent rule')
    try:
        rules.referenced_vertices([0, 5], 4)
        bad = False
    except ValueError:
        bad = True
    expect(bad and rules.referenced_vertices([0, 2, 2], 4).tolist() == [True, False, True, False] and rules.referenced_vertices([], 3).tolist() == [False] * 3,
           'referenced_vertices marks every index and rejects an index outside the domain')

    # 10. The rows of every branch (lines 299-321), through tangent_plan on hand-built primitives and directly.
    def shape(rows):
        return [(r['featureId'], r['outcome'], r['reasonCode'], r['count'], None if r['bounds'] is None else r['bounds']['maximumError']) for r in rows]

    three = [0, 1, 2]
    plan = rules.tangent_plan(np.array(unit + [[1, 0, 0, 1]], dtype=np.float32), three)
    expect(shape(plan['rows']) == [('geometry/tangents', 'Exact', 'geometry.tangents-preserved', None, None)], 'rows: all unit -> preserved only')
    plan = rules.tangent_plan(np.array(unit + [[3, 4, 0, -1]], dtype=np.float32), three)
    expect(shape(plan['rows']) == [('geometry/tangents', 'Approximated', 'geometry.tangents-normalized', None, 0.1)]
           and plan['rows'][0]['bounds']['observedError'] == plan['maxDegrees'] and 6.8e-7 < plan['maxDegrees'] < 6.9e-7,
           'rows: one normalized lane, no placeholder -> normalized under geometry/tangents with the measured bound')
    plan = rules.tangent_plan(np.array(unit + [[0, 0, 0, 1], [0, 0, 0, -1]], dtype=np.float32), three)
    expect(shape(plan['rows']) == [('geometry/tangents', 'Degraded', 'geometry.unreferenced-tangents-filled', 2, None),
                                   ('geometry/unreferenced-tangent-slots', 'Degraded', 'geometry.unreferenced-tangent-placeholders', 2, None)],
           'rows: two placeholders, nothing normalized -> filled + placeholder rows, both counting 2, no normalization row')
    plan = rules.tangent_plan(np.array([[1, 0, 0, 1], [3, 4, 0, 1], [0.6, 0.8, 0, -1], [0, 0, 0, 1], [0, 0, 0, -1]], dtype=np.float32), three)
    expect(shape(plan['rows']) == [('geometry/tangents', 'Degraded', 'geometry.unreferenced-tangents-filled', 2, None),
                                   ('geometry/unreferenced-tangent-slots', 'Degraded', 'geometry.unreferenced-tangent-placeholders', 2, None),
                                   ('geometry/tangent-normalization', 'Approximated', 'geometry.tangents-normalized', None, 0.1)]
           and plan['expected'][3].tolist() == [1, 0, 0, 1] and plan['expected'][4].tolist() == [1, 0, 0, -1],
           'rows: placeholders and a normalized lane -> filled, placeholder, then normalization under geometry/tangent-normalization (line 317)')
    plan = rules.tangent_plan(np.array([[0, 0, 0, 1], [3, 4, 0, 1], [0.6, 0.8, 0, -1], [0, 0, 0, 1]], dtype=np.float32), three)
    expect(shape(plan['rows']) == [('geometry/tangents', 'Degraded', 'geometry.tangent-basis-unsupported', None, None),
                                   ('geometry/unreferenced-tangent-slots', 'Degraded', 'geometry.unreferenced-tangent-placeholders', 1, None)]
           and plan['expected'] is None and plan['invalidCount'] == 1 and plan['changed'] is True,
           'rows: a referenced zero lane beside a placeholder and a normalized lane -> unsupported + placeholder row (line 312 has no invalid guard), no normalization row (line 316)')
    expect(shape(rules.expected_tangent_rows(1, 0, 0, 0.0)) == [('geometry/tangents', 'Degraded', 'geometry.tangent-basis-unsupported', None, None)],
           'rows: invalid alone -> unsupported only')
    expect(rules.expected_tangent_rows(0, 1, 0, 0.1)[0]['outcome'] == 'Approximated' and rules.expected_tangent_rows(0, 1, 0, 0.1000001)[0]['outcome'] == 'Degraded'
           and rules.expected_tangent_rows(0, 1, 3, 0.2)[2]['outcome'] == 'Degraded' and rules.expected_tangent_rows(0, 1, 3, 0.2)[2]['featureId'] == 'geometry/tangent-normalization'
           and rules.expected_tangent_rows(0, 1, 3, 0.2)[2]['bounds']['observedError'] == 0.2,
           'rows: Approximated at exactly 0.1 degrees, Degraded above it, under either featureId, with the observed error in the bound')

    # 11. Hop E applies the rule with the PACKAGE's own index stream (hop_e.compare_geometry): the package keeps the
    #     source lanes, the GLB stores (1, 0, 0, w) on the zero-direction lanes no package index references, and a
    #     zero lane a package index references is refused (a GLB that exists beside it fails as unsupported). The set is
    #     gate1a_synthetic's (prim: triangle (0, 1, 2), zero tangent lanes 3 (0, 0, 0, -1) and 4 (-0, 0, -0, +1)); the
    #     GLB's TANGENT lanes are written out by hand from ModelGltfGeometry lines 281 and 289-296, not taken from
    #     writer_rules, so the hop's use of the rule is what is tested.
    work = tempfile.mkdtemp(prefix='gate1a-hop-e-tangents-')
    hand_ta = [[1, 0, 0, 1], [np.float32(0.6), np.float32(0.8), 0, 1], [0.6, 0.8, 0, -1], [1, 0, 0, -1], [1, 0, 0, 1]]
    normals = [synthetic.A_WRITTEN_NORMALS, synthetic.B_WRITTEN_NORMALS]
    label_e = 'meshes/0/primitives/0 (prim)/TANGENT'

    def hop_e_geometry(name, glb_tangents, prims=None):
        glb = Glb(synthetic.build_glb([glb_tangents, synthetic.B['tangents']], normals, [], prims=prims))
        package = Package(synthetic.build_package(os.path.join(work, name + '.zip'), prims=prims))
        try:
            r = HopResult('E', 'synthetic')
            hop_e.compare_geometry(r, glb, package, hop_e.GlbPackagePairing(glb, package))
        finally:
            package.close()
        c = r.checks[-1]
        m = c.mismatches[0] if c.mismatches else None
        return c.passed, m, c.data, (m.where + ' ' + m.kind + ': ' + m.detail) if m else c.detail

    expect(bits(hand_ta[3]).tolist() == [0x3F800000, 0, 0, 0xBF800000] and bits(hand_ta[4]).tolist() == [0x3F800000, 0, 0, 0x3F800000]
           and synthetic.A['indices'] == [0, 1, 2], 'hop E set: prim lanes 3 and 4 are unreferenced by (0, 1, 2) and written (1, +0, +0, -1) / (1, +0, +0, +1) by hand')
    passed, m, data, detail = hop_e_geometry('pristine', hand_ta)
    expect(passed is True and data.get('streamsCompared') == 10,
           'hop E: the GLB with the placeholders passes against the package of source lanes, TANGENT compared on both primitives (10 streams): %s' % detail)
    for lane, value, what in ((3, [0, 1, 0, -1], '(0, 1, 0, -1), unit with the authored w'), (3, [0, 0, 0, -1], 'the source zero lane copied through'),
                              (4, [1, 0, 0, -1], '(1, 0, 0, -1), the authored +1 flipped')):
        variant = [list(v) for v in hand_ta]
        variant[lane] = value
        passed, m, _, detail = hop_e_geometry('placeholder-lane-%d' % lane, variant)
        expect(passed is False and m is not None and m.where == label_e and m.kind == 'value' and ('at [%d, ' % lane) in m.detail,
               'hop E: unreferenced lane %d written %s is a TANGENT value mismatch on that lane: %s' % (lane, what, detail))
    for indices, what in (([0, 1, 2, 2, 3, 0], 'triangle (2, 3, 0)'), ([0, 1, 2, 0, 4, 4], 'the degenerate triangle (0, 4, 4)')):
        prims = [('prim', dict(synthetic.A, indices=indices)), ('plain', synthetic.B)]
        passed, m, _, detail = hop_e_geometry('referenced-%d' % indices[4], hand_ta, prims=prims)
        expect(passed is False and m is not None and m.where == label_e and m.kind == 'unsupported' and '1 package tangent lane(s)' in m.detail,
               'hop E: a zero lane the package indices reference through %s is refused even when the GLB carries the placeholder there: %s' % (what, detail))

    if FAILURES:
        print('%d expectation(s) failed' % len(FAILURES))
        return 1
    print('all expectations hold')
    return 0


if __name__ == '__main__':
    sys.exit(main())
