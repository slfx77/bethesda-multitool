# SPDX-License-Identifier: 0BSD
"""The skin influence lanes the Blender package writes for one primitive (gate 1a hop C; gate 1b).

Restated from Shared ``Slfx77.Multitool.Media.Blender/BlendSkinLaneLowering.cs`` (canonical main fa0ae8c, read, never
run). The package writes the document's own lanes except where Blender cannot store them:

* A vertex is admitted when every weight is finite and at most 1, no joint carries two positive lanes, no negative
  weight sits on a joint a positive lane uses, and the sequential binary64 sum of all lanes is within
  ``min(positive lanes, 32) x 2^-17`` of one (the unchanged Blender weight-sum tolerance).
* An admitted vertex with a signed lane (a negative weight on a joint no positive lane uses: a console engine lane) is
  lowered: each signed lane is written as weight 0 on its own joint and each positive lane w as ``(float)(w / P)``, P
  being the binary64 sum of the positive lanes (in lane order); zero lanes stay as they are.
* Welded copies (vertices sharing a point index): a copy whose written lanes differ from its point's first copy (the
  lowest vertex index), both admitted, takes the first copy's written lanes and joints when the two copies' source
  per-joint sums (sequential binary64, signed lanes included) differ on exactly two joints with opposite signs, and the
  larger of the two magnitudes is at most 3 x 2^-13 (one console engine residual moved between two joints).

Anything else is written unchanged (a refused vertex refuses the whole package, so it never reaches a package the gate
reads). This module is the oracle's independent restatement; ``hop_c`` compares the package stream with it.
"""

import numpy as np

WEIGHT_SUM_TOLERANCE_PER_LANE = 1.0 / 131072
MAXIMUM_TOLERANCE_LANES = 32
MAXIMUM_WELD_TRANSFER = 3.0 / 8192


def _tolerance(positive_lanes):
    return min(max(positive_lanes, 0), MAXIMUM_TOLERANCE_LANES) * WEIGHT_SUM_TOLERANCE_PER_LANE


def _sums(joints, weights, stride, vertex):
    sums = {}
    for lane in range(stride):
        joint = int(joints[vertex * stride + lane])
        sums[joint] = sums.get(joint, 0.0) + float(weights[vertex * stride + lane])
    return sums


def _transfer(joints, weights, stride, vertex, representative):
    source = _sums(joints, weights, stride, representative)
    copy = _sums(joints, weights, stride, vertex)
    gained = lost = 0.0
    changed = 0
    differences = [copy[j] - source.get(j, 0.0) for j in copy] + [-source[j] for j in source if j not in copy]
    for difference in differences:
        if difference == 0:
            continue
        changed += 1
        if changed > 2:
            return None
        if difference > 0:
            gained = difference
        else:
            lost = -difference
    return max(gained, lost) if changed == 2 and gained > 0 and lost > 0 else None


def written_lanes(joints, weights, stride, point_indices=None):
    """(joints, weights, signed vertex count, welded vertex count) as the Blender package writes them. ``joints`` and
    ``weights`` are the document's flat lanes; ``point_indices`` the primitive's point index per vertex, or None."""
    joints = np.asarray(joints, dtype=np.int64).reshape(-1)
    weights = np.asarray(weights, dtype=np.float32).reshape(-1)
    count = len(weights) // stride if stride > 0 else 0
    if stride <= 0 or count * stride != len(weights) or len(joints) != len(weights):
        return joints, weights, 0, 0
    emitted_w = weights.copy()
    emitted_j = joints.copy()
    admitted = np.zeros(count, dtype=bool)
    signed = 0
    for vertex in range(count):
        lanes = [float(weights[vertex * stride + lane]) for lane in range(stride)]
        lane_joints = [int(joints[vertex * stride + lane]) for lane in range(stride)]
        invalid = negative = False
        total = positive_sum = 0.0
        positive = set()
        positive_count = 0
        for weight, joint in zip(lanes, lane_joints):
            if not np.isfinite(weight) or weight > 1:
                invalid = True
            elif weight > 0:
                if joint in positive:
                    invalid = True
                positive.add(joint)
                positive_count += 1
                positive_sum += weight
            elif weight < 0:
                negative = True
            total += weight
        if not invalid and negative and any(w < 0 and j in positive for w, j in zip(lanes, lane_joints)):
            invalid = True
        if invalid or not abs(total - 1) <= _tolerance(positive_count):
            continue
        admitted[vertex] = True
        if not negative:
            continue
        for lane, weight in enumerate(lanes):
            index = vertex * stride + lane
            emitted_w[index] = np.float32(0.0) if weight < 0 else np.float32(weight / positive_sum) if weight > 0 else np.float32(weight)
        signed += 1
    welded = 0
    if point_indices is not None and len(point_indices) == count:
        first = {}
        for vertex in range(count):
            point = int(point_indices[vertex])
            if point not in first:
                first[point] = vertex
                continue
            representative = first[point]
            a = slice(vertex * stride, vertex * stride + stride)
            b = slice(representative * stride, representative * stride + stride)
            same = np.array_equal(emitted_j[a], emitted_j[b]) and np.array_equal(emitted_w[a], emitted_w[b])  # by value, as C#
            if same or not admitted[vertex] or not admitted[representative]:
                continue
            transfer = _transfer(joints, weights, stride, vertex, representative)
            if transfer is None or not transfer <= MAXIMUM_WELD_TRANSFER:
                continue
            emitted_w[a] = emitted_w[b]
            emitted_j[a] = emitted_j[b]
            welded += 1
    return emitted_j, emitted_w, signed, welded
