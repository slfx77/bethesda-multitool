# SPDX-License-Identifier: 0BSD
"""The GLB writer's declared conversions, re-derived independently in Python.

Each function names the Shared source it was read from (paths under
``shared/Multitool.Shared/src/Slfx77.Multitool.Media/Models/``). No writer code is imported or executed;
the arithmetic is reproduced from the source text, including its float32 rounding points.

Order of operations in the writer (ModelGltfLowering.Apply, lines 18-62):
  1. ModelGltfLayers.Apply selects the visible nodes.
  2. Images are prepared (ModelGltfImages / ModelGltfImageWorkspace): level zero decoded to RGBA8, BC5 Z
     reconstructed, PNG encoded. Prepared normal maps (green inversion, companion packing) are appended.
  3. Materials are lowered (ModelGltfMaterials.PlanCore / Apply).
  4. Meshes are lowered per primitive (ModelGltfPrimitives.Apply -> ModelGltfGeometry.Apply): non-unit normals
     normalized and zero normals reconstructed or filled, tangents outside the float32 unit tolerance normalized
     (since Shared 0fe3212; the pin before it refused them) and zero-direction tangents that no index references
     filled with (1, 0, 0) and their authored handedness (since Shared 1c2f5b7, pinned 591d083; 0fe3212 refused them),
     weights normalized, absolute morphs turned into float32 deltas, then triangle winding reversed for clockwise
     materials (ModelGltfWinding.Indices).
  5. The units factor and the basis rotation are applied ONCE, on a new "multitool coordinates/<root>" parent
     node per scene root (ModelGltfCoordinates.Apply, line 100). Vertex data stays in source units and in the
     source basis; nothing rotates or scales the accessors themselves.
  6. SceneGltfBuilder writes vertices interleaved (stride 32 + color size [+16 tangent]), COLOR_0 as float VEC4
     for FloatingPoint encoding, indices as uint16 when every index is below 65535, joints/weights in four-slot
     sets (SceneGltfSkinBuilder.AddInfluences), node matrices as M11..M44 (MatrixJson).
"""

import math

import numpy as np

from gate1a_common import f32

UNIT = {'x': np.array([1.0, 0.0, 0.0]), 'y': np.array([0.0, 1.0, 0.0]), 'z': np.array([0.0, 0.0, 1.0])}


def _is_cardinal(v):
    v = np.asarray(v, dtype=np.float64)
    return any(np.array_equal(v, s * UNIT[a]) for a in 'xyz' for s in (1.0, -1.0))


def orientation(basis):
    """ModelGltfCoordinates.Orientation (lines 122-170): a signed permutation as rows X', Y', Z', or None with a reason.

    Returns (matrix rows as a 3x3 float array in row-vector convention, limitation or None, description).
    """
    if basis is None or basis.get('up') is None or basis.get('forward') is None:
        if basis is not None and basis.get('handedness') == 'LeftHanded':
            return None, 'coordinates.reflection-axis-unknown', 'left-handed without a forward axis'
        return np.eye(3), None, 'identity fallback (basis unknown)'
    up = np.asarray(basis['up'], dtype=np.float64)
    forward = np.asarray(basis['forward'], dtype=np.float64)
    right = np.asarray(basis['right'], dtype=np.float64) if basis.get('right') is not None else None
    if not _is_cardinal(up) or not _is_cardinal(forward) or float(np.dot(up, forward)) != 0 or \
            (right is not None and (not _is_cardinal(right) or float(np.dot(right, up)) != 0 or float(np.dot(right, forward)) != 0)):
        return None, 'coordinates.basis-not-orthogonal-cardinal', 'non-cardinal basis'
    unit_y = UNIT['y']
    if np.array_equal(up, unit_y):
        rotation = np.eye(3)
    elif np.array_equal(up, -unit_y):
        axis = right if right is not None else forward
        rotation = np.stack([2 * axis * float(np.dot(axis, UNIT[a])) - UNIT[a] for a in 'xyz'])
    else:
        axis = np.cross(up, unit_y)
        rotation = np.stack([np.cross(axis, UNIT[a]) + axis * float(np.dot(axis, UNIT[a])) for a in 'xyz'])
    if basis.get('handedness') == 'LeftHanded':
        reflection = np.diag([1 - 2 * forward[0] ** 2, 1 - 2 * forward[1] ** 2, 1 - 2 * forward[2] ** 2])
        rotation = reflection @ rotation  # .NET row-vector: reflection * rotation applies the reflection first
    return rotation, None, 'exact minimum cardinal rotation'


def coordinate_parent_matrix(meters_per_unit, option_scale, basis):
    """ModelGltfCoordinates.Plan lines 34-35 and 59: factor = mpu * scale (double), rounded = (float)factor,
    RootTransform = orientation * CreateScale(rounded), written by SceneGltfBuilder.MatrixJson as M11..M44.

    Returns (16 float32 values in glTF ``matrix`` order, rounded factor, limitation)."""
    mpu = meters_per_unit if meters_per_unit is not None else 1.0
    factor = mpu * option_scale
    rounded = f32(factor)
    rotation, limitation, _ = orientation(basis)
    if limitation is not None:
        return None, rounded, limitation
    m = np.eye(4, dtype=np.float64)
    m[:3, :3] = rotation
    # Uniform scale on the right of a row-vector product: every rotation entry times the float32 factor.
    scaled = (m[:3, :3].astype(np.float32) * np.float32(rounded)).astype(np.float32)
    # Matrix4x4 operator* sums four products per entry; the k = 3 product is (+0)(+0) = +0, so an entry that is
    # zero is always +0 in the writer (IEEE: -0 + +0 = +0). Only nonzero entries carry a sign.
    values = []
    for row in range(4):
        for column in range(4):
            if row < 3 and column < 3:
                value = float(scaled[row, column])
                values.append(value if value != 0 else 0.0)
            else:
                values.append(1.0 if row == column else 0.0)
    return values, rounded, None


def direction_degrees(source, output):
    """ModelGltfGeometry.DirectionDegrees (lines 555-562 at pinned Shared 591d083; lines 508-515 at 0fe3212, unchanged).

    The angle between a source direction and its float32 normalization, as the writer measures it::

        dot = (double)s.X * o.X + (double)s.Y * o.Y + (double)s.Z * o.Z
        x = (double)s.Y * o.Z - (double)s.Z * o.Y
        y = (double)s.Z * o.X - (double)s.X * o.Z
        z = (double)s.X * o.Y - (double)s.Y * o.X
        Math.Atan2(Math.Sqrt(x * x + y * y + z * z), dot) * (180 / Math.PI)

    Every product is a double product of two exact float32 values, the three-term sums are left to right
    ((a + b) + c), and the constant is the double 180 / pi. The previous writer used
    acos(clamp(dot / sqrt(|s|^2 |o|^2))), which rounds an angle below about 1e-8 radians to zero because the cosine
    rounds to 1.0 in double; atan2 of the cross-product magnitude keeps it (see ``selfcheck_tangents.py``).

    ``source`` and ``output`` are float32 triples or (N, 3) arrays; the result is a double, or an (N,) double array."""
    s = np.asarray(source, dtype=np.float32).astype(np.float64)
    o = np.asarray(output, dtype=np.float32).astype(np.float64)
    sx, sy, sz = s[..., 0], s[..., 1], s[..., 2]
    ox, oy, oz = o[..., 0], o[..., 1], o[..., 2]
    dot = (sx * ox + sy * oy) + sz * oz
    x = sy * oz - sz * oy
    y = sz * ox - sx * oz
    z = sx * oy - sy * ox
    degrees = np.arctan2(np.sqrt((x * x + y * y) + z * z), dot) * (180 / math.pi)
    return float(degrees) if degrees.ndim == 0 else degrees


def previous_pin_direction_degrees(source, output):
    """The PREVIOUS pin's (Shared 6c94992) DirectionDegrees: ``acos(clamp(dot / sqrt(|s|^2 |o|^2), -1, 1))`` in double,
    in degrees. Diagnostics only, never an expectation: a declared error that equals this value and not
    ``direction_degrees`` identifies a fidelity report written by the previous writer. The cosine of an angle below
    about 1e-8 radians rounds to 1.0 in double, so this form reads 0 there and is quantized to acos(1 - k * 2^-53)
    just above it, the doubles below 1 being spaced 2^-53: 8.5377e-7, 1.2074e-6, 1.4788e-6, 1.7075e-6 degrees for
    k = 1..4, 2.0913e-6 for k = 6 and 2.4148e-6 for k = 8. The 2026-09-24 fidelity reports of the previous writer
    declare exactly those six values (and 0) on the 20 samples whose normals it normalized; an earlier revision of this
    note listed only the even k as acos(1 - k * 2^-52), which misses the odd-k values 8.5377e-7 and 1.4788e-6."""
    s = np.asarray(source, dtype=np.float32).astype(np.float64)
    o = np.asarray(output, dtype=np.float32).astype(np.float64)
    dot = (s * o).sum(axis=-1)
    denominator = np.sqrt((s * s).sum(axis=-1) * (o * o).sum(axis=-1))
    degrees = np.degrees(np.arccos(np.clip(dot / denominator, -1, 1)))
    return float(degrees) if degrees.ndim == 0 else degrees


def _normalize_directions(source):
    """The shared arithmetic of NormalizeNormals: double squared length (``LengthSquared``, line 549 at 591d083:
    ``(double)X * X + (double)Y * Y + (double)Z * Z``), a lane whose squared length is neither 0 nor exactly 1
    becomes ``(float)(component / Math.Sqrt(squared))`` per component (lines 185-187).

    Returns (expected float32 lanes, change mask, double squared lengths)."""
    s64 = source.astype(np.float64)
    squared = (s64[:, 0] * s64[:, 0] + s64[:, 1] * s64[:, 1]) + s64[:, 2] * s64[:, 2]
    change = (squared != 1) & (squared != 0)
    out = source.copy()
    if change.any():
        out[change] = (s64[change] / np.sqrt(squared[change])[:, None]).astype(np.float32)
    return out, change, squared


def normalize_normals(normals, normal_mode):
    """ModelGltfGeometry.NormalizeNormals (lines 167-241 at 591d083).

    A normal whose double squared length is neither 0 nor exactly 1 is normalized (``_normalize_directions``); the
    direction change is measured by ``direction_degrees`` (line 188). Zero normals are reconstructed or filled by
    ``SceneVertexNormals.ReconstructMissing`` (see ``hop_b.zero_normal_lanes``), which this module does not reproduce.

    Returns (expected float32 normals or None when NormalMode is Flat, max direction change in degrees,
    changed flag, zero-normal vertex indices)."""
    if normal_mode == 'Flat':
        return None, 0.0, False, []
    source = np.asarray(normals, dtype=np.float32)
    out, change, squared = _normalize_directions(source)
    max_degrees = float(direction_degrees(source[change], out[change]).max()) if change.any() else 0.0
    return out, max_degrees, bool(change.any()), [int(i) for i in np.flatnonzero(squared == 0)]


def previous_pin_normal_degrees(normals, normal_mode):
    """Diagnostics only: the maximum normal direction change the PREVIOUS pin's acos form would have declared for the
    same normalization, so a mismatch against ``normalize_normals`` can say whether the report came from that writer."""
    if normal_mode == 'Flat':
        return 0.0
    source = np.asarray(normals, dtype=np.float32)
    out, change, _ = _normalize_directions(source)
    return float(previous_pin_direction_degrees(source[change], out[change]).max()) if change.any() else 0.0


# ModelGltfGeometry.MaximumTangentDegrees (line 17): the bound of the geometry.tangents-normalized row.
MAXIMUM_TANGENT_DEGREES = 0.1
# The direction an unreferenced zero-direction tangent lane receives: ``new Vector4(1, 0, 0, value.W)`` (line 281),
# +0 in y and z whatever zero sign the source lane carried; w is the authored +1 or -1, bit for bit.
TANGENT_PLACEHOLDER_DIRECTION = (1.0, 0.0, 0.0)


def referenced_vertices(indices, vertex_count):
    """ModelGltfGeometry.ReferencedVertices (lines 331-345, pinned Shared 591d083): one flag per vertex slot, set for
    every entry of the primitive's index list (every corner of every triangle, degenerate triangles included); nothing
    is inferred from coincident positions. This is the same marking the normal placeholders use
    (SceneVertexNormals.ReconstructMissing lines 119-130, ``hop_b.zero_normal_lanes``).

    An index outside ``[0, vertex_count)`` raises ValueError, as the writer's ``(uint)vertex >= (uint)referenced.Length``
    test (line 339) throws InvalidDataException; a negative index is out of range there too (numpy fancy indexing would
    silently wrap it, which is why the bound is tested before marking)."""
    idx = np.asarray(indices, dtype=np.int64).reshape(-1)
    referenced = np.zeros(int(vertex_count), dtype=bool)
    if idx.size:
        if int(idx.min()) < 0 or int(idx.max()) >= vertex_count:
            raise ValueError('an index lies outside the original vertex domain [0, %d): min %d, max %d (ModelGltfGeometry line 339 '
                             'throws InvalidDataException)' % (vertex_count, int(idx.min()), int(idx.max())))
        referenced[idx] = True
    return referenced


def classify_tangents(tangents, indices):
    """The per-lane classes of ModelGltfGeometry.NormalizeTangents (lines 265-298, pinned Shared 591d083), in the
    writer's order of tests.

    Per tangent (x, y, z, w), all float32:
      1. INVALID when x, y or z is not finite, or w is not exactly +1 or -1 (line 270; a NaN w is neither). This test
         comes FIRST, so a zero direction without an exact handedness is invalid even in a slot no index references.
      2. A zero direction (the double ``LengthSquared`` is 0, line 272; only an all-zero direction, either sign of zero,
         since the smallest float32 subnormal squared is representable in double) is INVALID when an index references
         the slot (line 275) and a PLACEHOLDER otherwise (lines 276-283: ``(1, 0, 0, w)``). The reference mask
         (``referenced_vertices``) is built lazily, only when such a lane is reached (line 274), so an out-of-domain
         index raises here exactly when the writer's tangent scan would throw.
      3. UNCHANGED when ``MathF.Abs(direction.LengthSquared() - 1) <= 0.0001f`` (line 286): the float32
         ``System.Numerics.Vector3.LengthSquared``, the float32 subtraction of 1, the float32 absolute value, compared
         with the float32 0.0001f (9.99999974737875e-05);
      4. NORMALIZED otherwise (lines 287-297).

    Float32 ``Vector3.LengthSquared`` is reproduced as ``(x*x + y*y) + z*z`` with every product and sum rounded to
    float32. Caveat: RyuJIT does not contract ``a*b + c`` into a fused multiply-add, and its SIMD lowerings of
    ``Vector3.Dot`` (``dpps`` or multiply plus horizontal adds) sum as ``(xx + yy) + (zz + 0)``, which equals the
    sequential sum when the fourth lane is zero; this was read from the runtime's documented behavior, not measured
    on the producing host. A fused product would differ from this reproduction by at most one float32 ulp of the
    squared length, which can only move a lane whose |squared - 1| lies within one ulp of 0.0001f.

    ``indices`` is the primitive's complete index list (the dump's, or the package's, which hop C proves equal).

    Returns (invalid mask, normalized mask, placeholder mask, double squared lengths) as arrays over the lanes."""
    t = np.asarray(tangents, dtype=np.float32).reshape(-1, 4)
    x, y, z, w = t[:, 0], t[:, 1], t[:, 2], t[:, 3]
    finite = np.isfinite(x) & np.isfinite(y) & np.isfinite(z)
    handed = (w == np.float32(1)) | (w == np.float32(-1))
    x64, y64, z64 = x.astype(np.float64), y.astype(np.float64), z.astype(np.float64)
    squared = (x64 * x64 + y64 * y64) + z64 * z64
    invalid = ~finite | ~handed
    zero = ~invalid & (squared == 0)
    placeholder = np.zeros(len(t), dtype=bool)
    if zero.any():
        referenced = referenced_vertices(indices, len(t))
        invalid |= zero & referenced
        placeholder = zero & ~referenced
    squared32 = (x * x + y * y) + z * z
    with np.errstate(invalid='ignore'):
        unchanged = np.abs(squared32 - np.float32(1)) <= np.float32(0.0001)
    normalized = ~invalid & ~placeholder & ~unchanged
    return invalid, normalized, placeholder, squared


def expected_tangent_rows(invalid_count, normalized_count, unreferenced_count, max_degrees):
    """The rows ModelGltfGeometry.NormalizeTangents emits for one primitive (lines 299-321, pinned Shared 591d083), in
    emission order. Each row is a dict: ``featureId``, ``outcome``, ``reasonCode``, ``bounds`` (None, or the
    ("tangent direction", "degrees", 0.1, maximum) bound as ``domain`` / ``unit`` / ``maximumError`` /
    ``observedError``) and ``count`` (the ``{unreferencedCount}`` the row's description interpolates, or None).

      * any invalid lane (lines 299-304): geometry/tangents Degraded geometry.tangent-basis-unsupported, no bound (the
        item is Unsupported and no GLB is written);
      * else any placeholder (lines 305-308): geometry/tangents Degraded geometry.unreferenced-tangents-filled, no
        bound, "Filled {N} unused zero-tangent slots with UnitX ...";
      * else no normalized lane (lines 309-311): geometry/tangents Exact geometry.tangents-preserved, no bound;
      * then, whenever N > 0 and INVALID OR NOT (line 312 has no ``!invalid`` guard): geometry/unreferenced-tangent-slots
        Degraded geometry.unreferenced-tangent-placeholders, no bound, "Exactly {N} zero-tangent vertex slot(s) ...";
      * then, when a lane was normalized and none is invalid (lines 316-321): geometry.tangents-normalized, Approximated
        when the maximum direction change is <= 0.1 degrees and Degraded otherwise, with the bound, under featureId
        geometry/tangent-normalization when N > 0 and geometry/tangents when N is 0 (line 317).

    So exactly one geometry/tangents row exists per evaluated primitive, the placeholder row exactly when N > 0, and the
    normalization row moves to its own featureId exactly when placeholders share the primitive."""
    rows = []
    invalid = invalid_count > 0

    def row(feature, outcome, code, bounds=None, count=None):
        rows.append({'featureId': feature, 'outcome': outcome, 'reasonCode': code, 'bounds': bounds, 'count': count})

    if invalid:
        row('geometry/tangents', 'Degraded', 'geometry.tangent-basis-unsupported')
    elif unreferenced_count > 0:
        row('geometry/tangents', 'Degraded', 'geometry.unreferenced-tangents-filled', count=unreferenced_count)
    elif normalized_count == 0:
        row('geometry/tangents', 'Exact', 'geometry.tangents-preserved')
    if unreferenced_count > 0:
        row('geometry/unreferenced-tangent-slots', 'Degraded', 'geometry.unreferenced-tangent-placeholders', count=unreferenced_count)
    if normalized_count > 0 and not invalid:
        row('geometry/tangent-normalization' if unreferenced_count > 0 else 'geometry/tangents',
            'Approximated' if max_degrees <= MAXIMUM_TANGENT_DEGREES else 'Degraded', 'geometry.tangents-normalized',
            bounds={'domain': 'tangent direction', 'unit': 'degrees', 'maximumError': MAXIMUM_TANGENT_DEGREES, 'observedError': max_degrees})
    return rows


def tangent_plan(tangents, indices):
    """ModelGltfGeometry.NormalizeTangents (lines 252-324), pinned Shared 591d083 (1c2f5b7 "Preserve unused zero-tangent
    slots in GLB exports"), for one primitive.

    Lanes are classified by ``classify_tangents``. A NORMALIZED lane becomes ``((float)(x / length), (float)(y / length),
    (float)(z / length), w)`` with ``length = Math.Sqrt(squared)`` in double (lines 289-291, 296); its direction change
    is ``direction_degrees(direction, normalized)`` (line 292). A PLACEHOLDER lane becomes ``(1, 0, 0, w)`` (line 281)
    with the authored w. An UNCHANGED lane keeps its bits. Any INVALID lane (a referenced zero direction, a nonfinite
    direction, a w other than exactly +1 or -1) makes the item Unsupported (line 301): no GLB is written, so there is no
    expected output. ``changed`` is the writer's ``changed`` out-parameter (lines 277 and 287: a placeholder or a
    normalized lane, set even when another lane is invalid), which drives the morph-tangent row (LowerMorphs lines
    438-441). The rows are ``expected_tangent_rows``.

    Returns a dict: ``expected`` (float32 (N, 4), or None when any lane is invalid), ``maxDegrees`` (over the normalized
    lanes; 0.0 when none), ``changed``, ``invalidCount``, ``normalizedCount``, ``unreferencedCount`` (the placeholder
    lanes, the writer's ``unreferencedCount``), the masks ``invalid`` / ``normalized`` / ``placeholder``, and ``rows``."""
    source = np.asarray(tangents, dtype=np.float32).reshape(-1, 4)
    invalid, normalize, placeholder, squared = classify_tangents(source, indices)
    invalid_count = int(invalid.sum())
    normalized_count = int(normalize.sum())
    unreferenced_count = int(placeholder.sum())
    max_degrees = 0.0
    normalized = None
    if normalized_count:
        direction = source[normalize, :3]
        length = np.sqrt(squared[normalize])
        normalized = (direction.astype(np.float64) / length[:, None]).astype(np.float32)
        max_degrees = float(direction_degrees(direction, normalized).max())
    expected = None
    if not invalid_count:
        expected = source.copy()
        if unreferenced_count:
            expected[placeholder, :3] = np.array(TANGENT_PLACEHOLDER_DIRECTION, dtype=np.float32)
        if normalized_count:
            expected[normalize, :3] = normalized
    return {
        'expected': expected, 'maxDegrees': max_degrees, 'changed': bool(normalized_count or unreferenced_count),
        'invalidCount': invalid_count, 'normalizedCount': normalized_count, 'unreferencedCount': unreferenced_count,
        'invalid': invalid, 'normalized': normalize, 'placeholder': placeholder,
        'rows': expected_tangent_rows(invalid_count, normalized_count, unreferenced_count, max_degrees),
    }


# The tangent-basis refusal reasons (NormalizeTangents' ``unsupported`` text) as a producing command's output quotes
# them in the manifest's ``partialReasons``: pinned Shared 591d083 (line 301) and the previous pin 0fe3212 (line 284 at
# 0fe3212, read with ``git show 0fe3212:src/Slfx77.Multitool.Media/Models/ModelGltfGeometry.cs``). Diagnostics only.
TANGENT_REFUSAL_TEXTS = (
    ('Shared 591d083', 'Referenced zero tangent directions, nonfinite directions, or absent handedness cannot produce a strict tangent basis'),
    ('Shared 0fe3212', 'Zero or nonfinite tangent directions or absent handedness cannot produce a strict tangent basis'),
)


def previous_pin_invalid_tangents(tangents):
    """Diagnostics only: the number of lanes ModelGltfGeometry.NormalizeTangents refused at the PREVIOUS pin, Shared
    0fe3212 (lines 266-268 there): a nonfinite direction, a w other than exactly +1 or -1, or a zero direction (double
    squared length 0) WHETHER OR NOT an index references its slot. It lets a refusal in artifacts that writer produced be
    described as that writer decided it; every expectation stays the pinned rule (``tangent_plan``)."""
    t = np.asarray(tangents, dtype=np.float32).reshape(-1, 4)
    x64, y64, z64 = (t[:, i].astype(np.float64) for i in range(3))
    finite = np.isfinite(t[:, 0]) & np.isfinite(t[:, 1]) & np.isfinite(t[:, 2])
    handed = (t[:, 3] == np.float32(1)) | (t[:, 3] == np.float32(-1))
    with np.errstate(invalid='ignore', over='ignore'):
        zero = ((x64 * x64 + y64 * y64) + z64 * z64) == 0
    return int((~finite | ~handed | zero).sum())


def normalize_tangents(tangents, indices):
    """The expected TANGENT accessor of ``tangent_plan`` in the shape the callers use.

    Returns (expected float32 (N, 4) array, or None when any lane is invalid; maximum direction change in degrees over
    the normalized lanes, 0.0 when any lane is invalid; the writer's ``changed`` flag (a normalized or placeholder lane);
    the number of invalid lanes)."""
    plan = tangent_plan(tangents, indices)
    if plan['invalidCount']:
        return None, 0.0, plan['changed'], plan['invalidCount']
    return plan['expected'], plan['maxDegrees'], plan['changed'], 0


def normalize_weights(weights, stride, vertex_count):
    """ModelGltfGeometry.NormalizeWeights (lines 355-412 at 591d083): per vertex a double sum; sum == 1 keeps the slots,
    otherwise every slot becomes (float)(weight / sum). Returns (expected float32 weights, max |sum - 1|)."""
    w = np.asarray(weights, dtype=np.float32).reshape(vertex_count, stride)
    sums = w.astype(np.float64).sum(axis=1)
    out = w.copy()
    change = sums != 1
    if change.any():
        out[change] = (w[change].astype(np.float64) / sums[change][:, None]).astype(np.float32)
    return out.reshape(-1), float(np.abs(sums - 1).max()) if vertex_count else 0.0


def joint_weight_sets(joints, weights, stride, vertex_count):
    """SceneGltfSkinBuilder.AddInfluences (lines 63-93): sets of four slots, absent trailing slots zero."""
    sets = (stride - 1) // 4 + 1
    j = np.asarray(joints, dtype=np.int64).reshape(vertex_count, stride)
    w = np.asarray(weights, dtype=np.float32).reshape(vertex_count, stride)
    result = []
    for s in range(sets):
        joint_set = np.zeros((vertex_count, 4), dtype=np.uint16)
        weight_set = np.zeros((vertex_count, 4), dtype=np.float32)
        for slot in range(4):
            influence = s * 4 + slot
            if influence >= stride:
                continue
            joint_set[:, slot] = j[:, influence]
            weight_set[:, slot] = w[:, influence]
        result.append((joint_set, weight_set))
    return result


def reverse_indices(indices):
    """ModelGltfWinding.Indices (lines 32-44): first corner kept, second and third exchanged."""
    tri = np.asarray(indices, dtype=np.int64).reshape(-1, 3)
    return tri[:, [0, 2, 1]].reshape(-1)


def alpha_cutoff_eight_bit_greater(raw_reference):
    """ModelGltfMaterials.Alpha line 245: cutoff = (float)((raw + .5d) / 255d)."""
    return f32((raw_reference + 0.5) / 255.0)


def mask_rule_applies(render_state):
    """ModelGltfMaterials.Alpha lines 240-242: blend not enabled, alpha test enabled, Greater, raw <= 254, reference == raw/255."""
    if not render_state:
        return False
    blend = render_state.get('blend')
    test = render_state.get('alphaTest')
    if blend is not None and blend.get('enabled'):
        return False
    if not test or not test.get('enabled') or test.get('compare') != 'Greater':
        return False
    raw = test.get('rawReference')
    reference = test.get('reference')
    if raw is None or raw > 254 or reference is None:
        return False  # a dump without the float reference cannot confirm the rule's equality
    return float(reference) == raw / 255.0


def roughness_from_glossiness(glossiness):
    """ModelGltfMaterials.PlanCore line 74: (float)Math.Sqrt(2d / (gloss + 2d))."""
    return f32(math.sqrt(2.0 / (glossiness + 2.0)))


def srgb_to_linear(sample):
    """ModelGltfVertexColors.ToLinear (line 216): the float32 sample compared with 0.04045f, then double math."""
    s = np.float32(sample)
    if s <= np.float32(0.04045):
        return f32(float(s) / 12.92)
    return f32(((float(s) + 0.055) / 1.055) ** 2.4)


def read_component(raw, component_type):
    """ModelGltfVertexColors.ReadComponent (lines 187-199) for one integer or float sample."""
    if component_type == 'Float32':
        return np.float32(raw)
    if component_type == 'UInt8':
        return np.float32(raw) / np.float32(255)
    if component_type == 'UInt16':
        return np.float32(raw) / np.float32(65535)
    if component_type == 'Int8':
        return np.float32(max(float(raw) / 127.0, -1.0))
    if component_type == 'Int16':
        return np.float32(max(float(raw) / 32767.0, -1.0))
    if component_type == 'Int32':
        return np.float32(max(float(raw) / 2147483647.0, -1.0))
    if component_type == 'UInt32':
        return np.float32(float(raw) / 4294967295.0)
    if component_type == 'Int64':
        return np.float32(max(float(raw) / 9223372036854775807.0, -1.0))
    if component_type == 'UInt64':
        return np.float32(float(raw) / 18446744073709551615.0)
    raise ValueError('component type %s is not admitted for primary colors' % component_type)


def expected_primary_colors(primitive):
    """The COLOR_0 values the writer stores for one primitive.

    Legacy path (no primary attribute): the dump vertex colors unchanged (ModelGltfPrimitives.Apply line 94).
    Attribute path (ModelGltfVertexColors.Apply lines 116-133): the selected stream's tuples through the vertex
    or point mapping, clamped to [0,1], RGB through the sRGB EOTF when the stream declares Srgb."""
    index = primitive.primary_color_attribute_index
    if index is None:
        return primitive.colors.astype(np.float32), 'legacy'
    stream = primitive.attributes[index]
    tuples = primitive.attribute_tuples(index)
    mapping = primitive.point_indices['values'] if stream['domain'] == 'Point' else np.arange(primitive.vertex_count)
    out = np.zeros((primitive.vertex_count, 4), dtype=np.float32)
    for vertex in range(primitive.vertex_count):
        tuple_values = tuples[mapping[vertex]]
        components = [read_component(tuple_values[c], stream['componentType']) for c in range(min(3, stream['components']))]
        alpha = read_component(tuple_values[3], stream['componentType']) if stream['components'] == 4 else np.float32(1)
        color = np.clip(np.array(components + [alpha], dtype=np.float32), 0, 1)
        if stream['colorSpace'] == 'Srgb':
            color = np.array([srgb_to_linear(color[0]), srgb_to_linear(color[1]), srgb_to_linear(color[2]), color[3]], dtype=np.float32)
        out[vertex] = color
    return out, 'attribute'


def encode_color_bytes(colors, encoding):
    """SceneGltfBuilder.WriteColor (lines 756-774): MathF.Round (to even) times the integer maximum."""
    if encoding == 'UnsignedByteNormalized':
        return np.rint(colors.astype(np.float32) * np.float32(255)).astype(np.uint8)
    if encoding == 'UnsignedShortNormalized':
        return np.rint(colors.astype(np.float32) * np.float32(65535)).astype(np.uint16)
    return colors.astype(np.float32)


def absolute_morph_deltas(base_positions, absolute_positions):
    """ModelGltfGeometry.LowerMorphs lines 461-462 at 591d083: delta = (float)((double)position - basis) per component."""
    return (np.asarray(absolute_positions, dtype=np.float64) - np.asarray(base_positions, dtype=np.float64)).astype(np.float32)


def reconstruct_normal_z(rgba, positive_z):
    """ModelGltfImages.ReconstructNormalZ (lines 360-376). Returns (pixels with B replaced, observed max error)."""
    out = rgba.copy()
    x = 2 * (rgba[:, :, 0].astype(np.float64) / 255.0) - 1
    y = 2 * (rgba[:, :, 1].astype(np.float64) / 255.0) - 1
    z = np.sqrt(np.maximum(0, 1 - x * x - y * y))
    normalized = ((z if positive_z else -z) + 1) / 2
    scaled = normalized * 255
    rounded = np.sign(scaled) * np.floor(np.abs(scaled) + 0.5)  # MidpointRounding.AwayFromZero
    quantized = np.clip(rounded, 0, 255).astype(np.uint8)
    out[:, :, 2] = quantized
    observed = float((np.abs(quantized.astype(np.float64) - scaled) / 255).max()) if scaled.size else 0.0
    return out, observed


def prepare_normal_map(rgba, green_down, companion=None, channel='Red'):
    """ModelGltfNormalMaps.Apply (lines 66-93): G inverted when the declared green is Down; alpha replaced by the
    companion's selected channel when a companion was planned; R and B unchanged."""
    out = rgba.copy()
    if green_down:
        out[:, :, 1] = 255 - rgba[:, :, 1]
    if companion is not None:
        selector = {'Red': 0, 'Green': 1, 'Blue': 2, 'Alpha': 3}
        if channel in selector:
            out[:, :, 3] = companion[:, :, selector[channel]]
        elif channel == 'Zero':
            out[:, :, 3] = 0
        elif channel == 'One':
            out[:, :, 3] = 255
        else:
            raise ValueError('companion channel %s is not admitted' % channel)
    return out


def packed_specular_layer(material):
    """ModelGltfNormalCompanion.Derive (lines 21-52): the single Specular layer whose swizzle alpha is not Alpha.

    Returns (layer index, channel name) or (None, 'Alpha'). Whether Core's summary accepts the candidate is
    not reproduced; the fidelity row ``normal-map.scalar-packed`` says whether the writer packed it."""
    source = material.get('source')
    if not source or material.get('normalTexture') is None:
        return None, 'Alpha'
    layers = source.get('layers') or []
    specular = [i for i, layer in enumerate(layers) if layer.get('role') == 'Specular']
    if len(specular) != 1:
        return None, 'Alpha'
    layer = layers[specular[0]]
    alpha = (layer.get('swizzle') or {}).get('alpha', 'Alpha')
    if alpha == 'Alpha':
        return None, 'Alpha'
    return specular[0], alpha


def summary_double_sided(material):
    """SceneMaterialSummary.Derive (Core, lines 99-100): the candidate's DoubleSided when the material has source form,
    ``cull == None || (cull is null && stencil.DrawMode == Both)``; without source form the writer starts from the
    stored material (ModelGltfMaterials.PlanCore lines 53-56: ``candidate = summary?.Material ?? original``)."""
    if material.get('source') is None:
        return bool(material.get('doubleSided'))
    state = material.get('renderState') or {}
    cull = state.get('cull')
    stencil = state.get('stencil') or {}
    return cull == 'None' or (cull is None and stencil.get('drawMode') == 'Both')


def blend_plan_applies(material):
    """ModelGltfBlendPlan.TryCreate (lines 16-33): the material takes the blend route (transmission or its fallback).

    True when the render state declares blend enabled, alpha test not enabled, an Add color equation whose source
    and destination terms the writer admits and which is not one of the two ordinary coverage paths; or for a
    source-free, state-free Additive material. On that route ModelGltfBlendPreparation may replace COLOR_0
    (transmission tint) or keep it (the lit fallback); which of the two the writer took is not reproduced here."""
    state = material.get('renderState')
    if material.get('source') is None and state is None:
        return material.get('alphaMode') == 'Additive'
    blend = (state or {}).get('blend') or {}
    test = (state or {}).get('alphaTest') or {}
    if not blend.get('enabled') or test.get('enabled'):
        return False
    equation = blend.get('colorEquation') or {}
    if equation.get('operation') != 'Add':
        return False

    def rgb(value, expected):
        return value is not None and all(float(value[c]) == expected for c in range(3))

    def constant(term, value):
        return rgb(term.get('constant'), value) and (rgb(term.get('scale'), 0) or term.get('input') == 'Zero')

    def source_term(term):
        if constant(term, 0):
            return 'Zero'
        if constant(term, 1):
            return 'Color'
        if not rgb(term.get('constant'), 0) or not rgb(term.get('scale'), 1):
            return None
        return {'SourceColor': 'ColorSquared', 'SourceAlpha': 'ColorTimesAlpha'}.get(term.get('input'))

    def destination_term(term):
        if constant(term, 0):
            return 'Zero'
        if constant(term, 1):
            return 'One'
        if rgb(term.get('constant'), 0) and rgb(term.get('scale'), 1) and term.get('input') == 'SourceColor':
            return 'Color'
        if rgb(term.get('constant'), 1) and rgb(term.get('scale'), -1) and term.get('input') == 'SourceAlpha':
            return 'OneMinusAlpha'
        return None

    source = source_term(equation.get('sourceFactor') or {})
    destination = destination_term(equation.get('destinationFactor') or {})
    if source is None or destination is None:
        return False
    if (source, destination) in (('Color', 'Zero'), ('ColorTimesAlpha', 'OneMinusAlpha')):
        return False
    return True


def ordinary_role(role):
    """ModelGltfDrawSelection.Ordinary (line 308): only a null, Transform or Joint role may draw its own mesh."""
    return role in (None, 'Transform', 'Joint')


def layer_plan(nodes, scenes, skins, layer_sets, mesh_has_render):
    """The static layer selection and draw selection of the GLB writer in Default mode, re-derived from
    ModelGltfLayers.Plan (lines 27-141) and ModelGltfDrawSelection.Plan (lines 83-174). The fidelity row
    ``layers.static-selection`` states the same policy: "direct overlapping memberships use any-selected inclusion;
    other nodes inherit visibility. Selected descendants retain disabled ancestors as transform-only. Unlayered base
    roots remain. Original skin joint ancestry is retained."

    Per node, in the writer's order:
      1. ``declared``: the node is a member of any layer set (ValidateLayers, lines 257-262).
      2. ``active``: a member of a selected layer (Default mode selects every defaultOn layer, SelectLayers line 290,
         lines 64-69); an undeclared node inherits its parent's value, an undeclared root is active (line 73).
      3. ``needed``: active, or an ancestor-or-self of a skin joint or skeleton root (RequiredJointAncestors,
         lines 303-330; line 74), then propagated to every ancestor (lines 76-81).
      4. A scene keeps only its needed roots (lines 87-95); a node whose root no scene keeps gets no decision
         (line 103: globalNeeded stays false).
      5. A needed node keeps only its needed children (lines 126-135); any other node keeps every child.
         KeepMesh = not needed or active (line 136): a disabled ancestor survives as transform-only.
      6. Draw selection: a node is reachable when the kept scene roots reach it through the kept child edges
         (ReachableNodes, lines 181-208); it draws its mesh when reachable, KeepMesh, its role is ordinary, and the mesh
         has at least one Render-purpose primitive (lines 99-102, 115, 123, 150-151). ModelGltfLowering.Nodes
         (lines 224-226) then emits the mesh, the skin and the morph weights only for a drawing node, and
         SceneGltfBuilder (line 141) writes ``children`` only when there are any.

    ``nodes``/``scenes``/``skins``/``layer_sets`` are the dump's lists; ``mesh_has_render`` one bool per dump mesh.
    Returns a dict of per-node lists (``needed``, ``active``, ``keepMesh``, ``children``, ``reachable``, ``drawsMesh``)
    and ``sceneRoots``, the kept roots of every scene. Raises ValueError on a hierarchy the writer refuses."""
    count = len(nodes)
    parents = [-1] * count
    for index, node in enumerate(nodes):
        for child in node.get('children') or []:
            if parents[child] >= 0:
                raise ValueError('node %d has two parents' % child)
            parents[child] = index
    order = [index for index in range(count) if parents[index] < 0]
    roots = {index: index for index in order}
    head = 0
    while head < len(order):
        for child in nodes[order[head]].get('children') or []:
            roots[child] = roots[order[head]]
            order.append(child)
        head += 1
    if len(order) != count:
        raise ValueError('cyclic hierarchy')
    declared = [False] * count
    active = [False] * count
    for layer in layer_sets or []:
        for member in layer.get('members') or []:
            declared[member] = True
            if layer.get('defaultOn'):
                active[member] = True
    required = [False] * count
    for skin in skins or []:
        starts = list(skin.get('jointNodeIndices') or [])
        if skin.get('skeletonRootNodeIndex') is not None:
            starts.append(skin['skeletonRootNodeIndex'])
        for start in starts:
            node = start
            while node >= 0 and not required[node]:
                required[node] = True
                node = parents[node]
    needed = [False] * count
    for node in order:
        if not declared[node]:
            active[node] = parents[node] < 0 or active[parents[node]]
        needed[node] = active[node] or required[node]
    for node in reversed(order):
        if needed[node] and parents[node] >= 0:
            needed[parents[node]] = True
    scene_roots = []
    included_roots = set()
    for scene in scenes or []:
        kept = [root for root in scene.get('rootNodeIndices') or [] if needed[root]]
        scene_roots.append(kept)
        included_roots.update(kept)
    global_needed = [needed[node] and roots[node] in included_roots for node in range(count)]
    global_active = [active[node] and roots[node] in included_roots for node in range(count)]
    children = []
    keep_mesh = []
    for index, node in enumerate(nodes):
        source = list(node.get('children') or [])
        children.append([child for child in source if global_needed[child]] if global_needed[index] else source)
        keep_mesh.append(not global_needed[index] or global_active[index])
    reachable = [False] * count
    queue = []
    for kept in scene_roots:
        for root in kept:
            if not reachable[root]:
                reachable[root] = True
                queue.append(root)
    while queue:
        node = queue.pop(0)
        for child in children[node]:
            if not reachable[child]:
                reachable[child] = True
                queue.append(child)
    draws = []
    for index, node in enumerate(nodes):
        mesh = node.get('meshIndex')
        draws.append(mesh is not None and reachable[index] and keep_mesh[index] and ordinary_role(node.get('role'))
                     and bool(mesh_has_render[mesh]))
    return {'needed': needed, 'active': active, 'keepMesh': keep_mesh, 'children': children, 'reachable': reachable,
            'drawsMesh': draws, 'sceneRoots': scene_roots}


def expected_double_sided(material):
    """ModelGltfMaterials.Raster (lines 302-344): cull None -> doubleSided, stencil Both -> doubleSided, applied to
    the starting value of ``summary_double_sided``."""
    value = summary_double_sided(material)
    state = material.get('renderState') or {}
    cull = state.get('cull')
    if cull in ('None', 'Back'):
        value = cull == 'None'
    stencil = state.get('stencil')
    if stencil and stencil.get('drawMode') in ('Both', 'CounterClockwise'):
        if stencil['drawMode'] == 'CounterClockwise' and state.get('frontFace') == 'Clockwise':
            return value  # blocked by the writer; not reproduced
        value = stencil['drawMode'] == 'Both'
    return value
