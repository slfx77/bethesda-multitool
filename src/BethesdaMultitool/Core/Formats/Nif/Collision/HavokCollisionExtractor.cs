using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Collision;

/// <summary>
///     Decodes a NIF's Havok (<c>bhk*</c>) collision geometry into a triangle soup for walk-mode
///     ground/ceiling sampling. The visual mesh has gaps (between planks of a bridge, slats of a
///     catwalk) that the camera falls through; the Havok collision mesh is gapless, so sampling it
///     instead fixes those falls.
///     <para>
///         Walks <c>bhkCollisionObject → bhkRigidBody[T] → shape tree</c> and reads the packed
///         tri-strip leaves (<c>hkPackedNiTriStripsData</c>) plus the primitive shapes used heavily by
///         Xbox 360 clutter and compound collision: convex vertices, boxes, spheres, capsules, and
///         transformed children. Output is in the NIF root-local <c>treatRootsAsIdentity</c> frame,
///         matching the visual collision soup, so the existing placement raycast is unchanged.
///     </para>
///     <para>
///         The live decode pipeline converts Xbox (big-endian) NIFs to little-endian first, and that
///         converter also decompresses <c>hkPackedNiTriStripsData</c> — so the production path reads
///         little-endian, uncompressed data. The big-endian / compressed (HalfVector3) path is kept
///         for the raw-file diagnostic. Field offsets are ported from
///         <c>tools/NifAnalyzer/Commands/HavokCommands.cs</c> (verified against real NIFs).
///     </para>
/// </summary>
internal static class HavokCollisionExtractor
{
    /// <summary>
    ///     Bethesda Oblivion/FO3/FNV Havok-to-world scale: <c>bhk*</c> collision vertices are stored in
    ///     Havok units; multiply by 7 to reach NIF/world units. (nifskope <c>havokConst</c>.)
    /// </summary>
    private const float HavokToWorldScale = 7f;

    /// <summary>
    ///     <c>OL_NONCOLLIDABLE</c> / <c>FOL_NONCOLLIDABLE</c> — Havok layer 15 in every Bethesda
    ///     layer table (Oblivion through Fallout 4; see <c>nif.xml</c>). Bodies on this layer exist
    ///     in the Havok world but collide with nothing.
    /// </summary>
    private const byte NoncollidableHavokLayer = 15;

    private const int MaxShapeDepth = 16;
    private const int PrimitiveRadialSegments = 12;
    private const int SphereStacks = 6;
    private const int CapsuleHemisphereRings = 4;

    private static readonly HashSet<string> CollisionObjectTypes =
        ["bhkCollisionObject", "bhkBlendCollisionObject", "bhkSPCollisionObject"];

    /// <summary>
    ///     Extracts the collision soup using the NIF's own endianness — the live path, where
    ///     <paramref name="data" />/<paramref name="nif" /> are the post-conversion little-endian buffer.
    /// </summary>
    public static HavokTriangleSoup? TryExtract(byte[] data, NifInfo nif)
        => TryExtract(data, nif, nif.IsBigEndian);

    /// <summary>
    ///     Extracts the collision soup with an explicit endianness, for the raw-file diagnostic (which
    ///     may feed a big-endian Xbox NIF with compressed vertices straight in).
    /// </summary>
    internal static HavokTriangleSoup? TryExtract(byte[] data, NifInfo nif, bool bigEndian)
    {
        if (nif.Blocks.Count == 0) return null;

        // Early-out: most decorative NIFs carry no bhk collision at all. Skip the scene-graph transform
        // walk below unless there's at least one collision object to attach geometry to.
        var hasCollisionObject = false;
        foreach (var block in nif.Blocks)
        {
            if (CollisionObjectTypes.Contains(block.TypeName)) { hasCollisionObject = true; break; }
        }

        if (!hasCollisionObject) return null;

        // Per-node world transforms in the SAME frame the visual mesh uses (treatRootsAsIdentity), so
        // the collision triangles overlay the visual submeshes and the placement world matrix applies
        // identically. Only node children are needed for the transform walk.
        var nodeChildren = new Dictionary<int, List<int>>();
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            var block = nif.Blocks[i];
            if (!NifSceneGraphWalker.NodeTypes.Contains(block.TypeName)) continue;
            var children = NifBlockParsers.ParseNodeChildren(data, block, nif.BsVersion, nif.BinaryVersion, bigEndian, nif.HasInlineStrings);
            if (children != null) nodeChildren[i] = children;
        }

        var worldTransforms = new Dictionary<int, Matrix4x4>();
        NifSceneGraphWalker.ComputeWorldTransforms(data, nif, nodeChildren, worldTransforms, treatRootsAsIdentity: true);

        var positions = new List<Vector3>();
        var triangles = new List<int>();

        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (!CollisionObjectTypes.Contains(nif.Blocks[i].TypeName)) continue;
            if (!TryReadCollisionObject(data, nif.Blocks[i], bigEndian, out var targetIdx, out var bodyIdx)) continue;
            if (bodyIdx < 0 || bodyIdx >= nif.Blocks.Count) continue;

            var bodyBlock = nif.Blocks[bodyIdx];
            if (bodyBlock.TypeName is not ("bhkRigidBody" or "bhkRigidBodyT")) continue;

            // bhkWorldObject.Shape is the rigid body's first field.
            if (!TryReadInt32(data, bodyBlock, 0, bigEndian, out var shapeRef)) continue;

            // bhkWorldObject.HavokFilter.Layer is the byte immediately after the shape ref. A body
            // on the NONCOLLIDABLE layer is registered with the Havok world but collides with
            // nothing, so the engine lets the player walk straight through it — harvestable flora
            // (clutter/junk/NV/WhiteHorseNettle.nif: layer 15) is authored exactly this way. Its
            // shape is still a real bhkBoxShape, so extracting it produced collision (and a debug
            // outline) around plants that have none in game.
            if (TryReadByte(data, bodyBlock, 4, out var havokLayer) &&
                havokLayer == NoncollidableHavokLayer)
            {
                continue;
            }

            var nodeWorld = worldTransforms.TryGetValue(targetIdx, out var w) ? w : Matrix4x4.Identity;
            Matrix4x4 rbTransform;
            if (bodyBlock.TypeName == "bhkRigidBodyT")
            {
                // A bhkRigidBodyT whose transform can't be read (truncated/garbage) must NOT emit
                // geometry at identity — that places collision at the wrong spot. Skip this body;
                // if no other collision body is usable, the caller's visual-mesh fallback engages.
                var t = TryReadRigidBodyTTransform(data, bodyBlock, bigEndian, nif.BinaryVersion);
                if (t is null) continue;
                rbTransform = t.Value;
            }
            else
            {
                rbTransform = Matrix4x4.Identity;
            }

            var shapeToWorld = rbTransform * nodeWorld;

            // Fresh visited set per collision object so a shape shared between bodies (with different
            // transforms) is still emitted for each; the set only guards against cycles within one walk.
            AppendShape(data, nif, shapeRef, bigEndian, shapeToWorld, Vector3.One, positions, triangles,
                new HashSet<int>(), depth: 0);
        }

        if (triangles.Count < 3) return null;

        // A soup with any non-finite vertex is worse than no soup: it is preferred over the visual-mesh
        // fallback yet draws nothing and fails every raycast (NaN poisons the AABB slab test). Degrade
        // to null so callers fall back to the visual mesh.
        foreach (var p in positions)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) return null;
        }

        return new HavokTriangleSoup(positions.ToArray(), triangles.ToArray());
    }

    // bhkCollisionObject: Target int32 @0, Flags ushort @4, Body int32 @6 (needs 10 bytes).
    private static bool TryReadCollisionObject(byte[] data, BlockInfo block, bool be, out int target, out int body)
    {
        target = -1;
        body = -1;
        if (block.Size < 10) return false;
        target = BinaryUtils.ReadInt32(data, block.DataOffset, be);
        body = BinaryUtils.ReadInt32(data, block.DataOffset + 6, be);
        return true;
    }

    // bhkRigidBodyT CInfo (nif.xml bhkRigidBodyCInfo550_660): Translation Vector4 @52, Rotation
    // hkQuaternion (XYZW) @68 — for ≥10.1.0.0. The CInfo's first five header fields (16 bytes:
    // Unused01/HavokFilter/Unused02/CollisionResponse+Unused03/CallbackDelay) are since="10.1.0.0",
    // so Oblivion's oldest 10.0.1.x meshes put Translation @36 / Rotation @52. On FNV files, wrong
    // offsets 28/44 would land on CollisionResponse/ProcessContactCallbackDelay=0xFFFF — a
    // guaranteed-NaN float canary. A plain bhkRigidBody ignores the transform (engine-side), so
    // only bhkRigidBodyT reaches here.
    private static Matrix4x4? TryReadRigidBodyTTransform(byte[] data, BlockInfo block, bool be,
        uint binaryVersion)
    {
        var translationOffset = binaryVersion >= NifVersions.Gamebryo10100 ? 52 : 36;
        var rotationOffset = translationOffset + 16;
        // Need the rotation quaternion's last float at rotationOffset+12 (read 4 bytes).
        if (block.Size < rotationOffset + 16) return null;
        var tx = BinaryUtils.ReadFloat(data, block.DataOffset + translationOffset, be);
        var ty = BinaryUtils.ReadFloat(data, block.DataOffset + translationOffset + 4, be);
        var tz = BinaryUtils.ReadFloat(data, block.DataOffset + translationOffset + 8, be);
        var qx = BinaryUtils.ReadFloat(data, block.DataOffset + rotationOffset, be);
        var qy = BinaryUtils.ReadFloat(data, block.DataOffset + rotationOffset + 4, be);
        var qz = BinaryUtils.ReadFloat(data, block.DataOffset + rotationOffset + 8, be);
        var qw = BinaryUtils.ReadFloat(data, block.DataOffset + rotationOffset + 12, be);

        // A non-finite transform would poison every collision vertex AND still count as a "present"
        // soup, blocking the visual-mesh fallback — reject it here so the fallback engages.
        if (!float.IsFinite(tx) || !float.IsFinite(ty) || !float.IsFinite(tz) ||
            !float.IsFinite(qx) || !float.IsFinite(qy) || !float.IsFinite(qz) || !float.IsFinite(qw))
        {
            return null;
        }

        var q = new Quaternion(qx, qy, qz, qw);
        q = q.LengthSquared() > 1e-6f ? Quaternion.Normalize(q) : Quaternion.Identity;
        // Translation is in Havok units too — scale to world. Rotate-then-translate (row-vector order).
        var translation = new Vector3(tx, ty, tz) * HavokToWorldScale;
        return Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(translation);
    }

    private static void AppendShape(byte[] data, NifInfo nif, int shapeIdx, bool be, Matrix4x4 toWorld,
        Vector3 accumScale, List<Vector3> positions, List<int> triangles, HashSet<int> visited, int depth)
    {
        if (depth > MaxShapeDepth) return;
        if (shapeIdx < 0 || shapeIdx >= nif.Blocks.Count) return;
        if (!visited.Add(shapeIdx)) return;

        try
        {
            var block = nif.Blocks[shapeIdx];
            switch (block.TypeName)
            {
                case "bhkMoppBvTreeShape":
                {
                    // Child Shape ref @0; scalar Scale @16 (after a 12-byte Unused01). The MOPP accel tree
                    // is a broadphase BV tree over the same triangles — ignore it, read the wrapped shape.
                    if (!TryReadInt32(data, block, 0, be, out var childRef)) return;
                    var moppScale = TryReadFloat(data, block, 16, be) ?? 1f;
                    var next = moppScale > 0f ? accumScale * moppScale : accumScale;
                    AppendShape(data, nif, childRef, be, toWorld, next, positions, triangles, visited, depth + 1);
                    break;
                }
                case "bhkPackedNiTriStripsShape":
                {
                    // Shared layout: UserData@0, Unused@4, Radius@8, Unused@12, Scale Vector4 @16,
                    // RadiusCopy@32, ScaleCopy@36, Data ref @52 — but TES4-era files (≤20.0.0.5)
                    // PREFIX the block with Num Sub Shapes (ushort) + hkSubPartData[] (12 bytes each:
                    // HavokFilter + Num Vertices + HavokMaterial), shifting every field by 2 + N*12;
                    // 20.2.0.7 moved that sub-shape array into hkPackedNiTriStripsData instead.
                    // Prefer ScaleCopy: retail Xbox files store the primary vector in PC byte order,
                    // while the converter swaps the copy into the post-conversion file's byte order.
                    // Native PC files author both identically. Falling back preserves older synthetic/
                    // truncated inputs that contain only the primary vector.
                    var basePos = 0;
                    if (NifVersions.IsTes4Era(nif.BinaryVersion))
                    {
                        if (block.Size < 2) return;
                        var numSubShapes = BinaryUtils.ReadUInt16(data, block.DataOffset, be);
                        basePos = 2 + numSubShapes * 12;
                    }

                    var sx = ReadPackedScaleComponent(data, block, be, basePos + 36, basePos + 16);
                    var sy = ReadPackedScaleComponent(data, block, be, basePos + 40, basePos + 20);
                    var sz = ReadPackedScaleComponent(data, block, be, basePos + 44, basePos + 24);
                    var shapeScale = accumScale * new Vector3(NonZero(sx), NonZero(sy), NonZero(sz));
                    if (!TryReadInt32(data, block, basePos + 52, be, out var dataRef)) return;
                    AppendPackedData(data, nif, dataRef, be, toWorld, shapeScale, positions, triangles);
                    break;
                }
                case "bhkListShape":
                {
                    // Num Sub Shapes (uint @0) then that many int32 shape refs.
                    if (!TryReadUInt32(data, block, 0, be, out var numSub)) return;
                    var cap = Math.Min((int)Math.Min(numSub, int.MaxValue), Math.Max(0, (block.Size - 4) / 4));
                    for (var s = 0; s < cap; s++)
                    {
                        if (!TryReadInt32(data, block, 4 + s * 4, be, out var subRef)) break;
                        AppendShape(data, nif, subRef, be, toWorld, accumScale, positions, triangles, visited,
                            depth + 1);
                    }

                    break;
                }
                case "bhkTransformShape":
                case "bhkConvexTransformShape":
                {
                    // Shape ref @0, Material @4, Radius @8, Unused[8] @12, Matrix44 @20. Matrix44 is
                    // serialized column-major; feeding its 16 values in order to System.Numerics' row-
                    // vector matrix gives the required transpose. Unlike bhkRigidBodyT, this matrix's
                    // translation is already in NIF/world units (retail tlfencebackyard.nif uses a 1/7
                    // linear basis around packed world-unit vertices), so do not multiply it by seven.
                    if (!TryReadInt32(data, block, 0, be, out var childRef)) return;
                    if (TryReadShapeTransform(data, block, be) is not { } shapeTransform) return;
                    AppendShape(data, nif, childRef, be, shapeTransform * toWorld, accumScale, positions,
                        triangles, visited, depth + 1);
                    break;
                }
                case "bhkNiTriStripsShape":
                    AppendNiTriStripsShape(data, nif, block, be, toWorld, accumScale, positions, triangles);
                    break;
                case "bhkConvexVerticesShape":
                    AppendConvexVertices(data, block, be, toWorld, accumScale, positions, triangles);
                    break;
                case "bhkBoxShape":
                    AppendBox(data, block, be, toWorld, accumScale, positions, triangles);
                    break;
                case "bhkSphereShape":
                    AppendSphere(data, block, be, toWorld, accumScale, positions, triangles);
                    break;
                case "bhkCapsuleShape":
                    AppendCapsule(data, block, be, toWorld, accumScale, positions, triangles);
                    break;
            }
        }
        finally
        {
            // This is a recursion-stack guard, not a global de-duplicator: a shared child referenced by
            // two differently transformed wrappers must be emitted twice.
            visited.Remove(shapeIdx);
        }
    }

    /// <summary>
    ///     <c>bhkNiTriStripsShape</c> — TES4-era clutter/architecture collision (the Ayleid ring walls,
    ///     stone pedestals). Its geometry is plain <c>NiTriStripsData</c> in MODEL units — retail
    ///     arringouterwall01's collision vertices span the same range as its visual mesh — so unlike the
    ///     packed/convex/primitive shapes there is NO ×7 Havok scale on the vertices (the rigid body's
    ///     translation stays Havok-scaled as usual). Layout: Material@0, Radius@4, Unused[20]@8,
    ///     GrowBy@28, Scale Vector4 @32 (since 10.1.0.0), Num Strips Data, then the data refs.
    /// </summary>
    private static void AppendNiTriStripsShape(byte[] data, NifInfo nif, BlockInfo block, bool be,
        Matrix4x4 toWorld, Vector3 accumScale, List<Vector3> positions, List<int> triangles)
    {
        var hasScaleField = nif.BinaryVersion >= NifVersions.Gamebryo10100;
        var scale = Vector3.One;
        if (hasScaleField)
        {
            var sx = TryReadFloat(data, block, 32, be) ?? 1f;
            var sy = TryReadFloat(data, block, 36, be) ?? 1f;
            var sz = TryReadFloat(data, block, 40, be) ?? 1f;
            if (float.IsFinite(sx) && float.IsFinite(sy) && float.IsFinite(sz) &&
                MathF.Abs(sx) > 1e-8f && MathF.Abs(sy) > 1e-8f && MathF.Abs(sz) > 1e-8f)
            {
                scale = new Vector3(sx, sy, sz);
            }
        }

        var numOffset = hasScaleField ? 48 : 32;
        if (!TryReadUInt32(data, block, numOffset, be, out var numStrips)) return;
        var cap = Math.Min((int)Math.Min(numStrips, int.MaxValue),
            Math.Max(0, (block.Size - numOffset - 4) / 4));
        var meshScale = scale * accumScale;
        for (var s = 0; s < cap; s++)
        {
            if (!TryReadInt32(data, block, numOffset + 4 + s * 4, be, out var dataRef)) break;
            if (dataRef < 0 || dataRef >= nif.Blocks.Count) continue;
            var dataBlock = nif.Blocks[dataRef];
            if (dataBlock.TypeName != "NiTriStripsData") continue;

            var submesh = NifBlockParsers.ExtractTriStripsData(data, dataBlock, be, nif.BsVersion,
                nif.BinaryVersion, Matrix4x4.Identity);
            if (submesh is null || submesh.Positions.Length < 9 || submesh.Triangles.Length < 3) continue;

            var baseIndex = positions.Count;
            for (var i = 0; i + 2 < submesh.Positions.Length; i += 3)
            {
                var p = new Vector3(submesh.Positions[i], submesh.Positions[i + 1],
                    submesh.Positions[i + 2]) * meshScale;
                positions.Add(Vector3.Transform(p, toWorld));
            }

            var vertexCount = submesh.Positions.Length / 3;
            for (var i = 0; i + 2 < submesh.Triangles.Length; i += 3)
            {
                int a = submesh.Triangles[i], b = submesh.Triangles[i + 1], c = submesh.Triangles[i + 2];
                if (a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
                triangles.Add(baseIndex + a);
                triangles.Add(baseIndex + b);
                triangles.Add(baseIndex + c);
            }
        }
    }

    private static Matrix4x4? TryReadShapeTransform(byte[] data, BlockInfo block, bool be)
    {
        const int matrixOffset = 20;
        const int matrixBytes = 64;
        if (matrixOffset + matrixBytes > block.Size) return null;

        Span<float> m = stackalloc float[16];
        for (var i = 0; i < m.Length; i++)
        {
            m[i] = BinaryUtils.ReadFloat(data, block.DataOffset + matrixOffset + i * 4, be);
            if (!float.IsFinite(m[i])) return null;
        }

        // Havok commonly writes the affine matrix's unused final W as zero. Normalize the homogeneous
        // row so composing with the target node's world matrix still carries node translation.
        return new Matrix4x4(
            m[0], m[1], m[2], 0f,
            m[4], m[5], m[6], 0f,
            m[8], m[9], m[10], 0f,
            m[12], m[13], m[14], 1f);
    }

    private static float ReadPackedScaleComponent(byte[] data, BlockInfo block, bool be, int copyOffset,
        int primaryOffset)
    {
        var copy = TryReadFloat(data, block, copyOffset, be);
        if (copy is { } copyValue && float.IsFinite(copyValue) && MathF.Abs(copyValue) > 1e-8f)
        {
            return copyValue;
        }

        return TryReadFloat(data, block, primaryOffset, be) ?? 1f;
    }

    private static void AppendConvexVertices(byte[] data, BlockInfo block, bool be, Matrix4x4 toWorld,
        Vector3 scale, List<Vector3> positions, List<int> triangles)
    {
        // bhkConvexShape: Material@0, Radius@4. Then two 12-byte bhkWorldObjCInfoProperty values,
        // NumVertices@32, Vector4 vertices@36, NumNormals, and Vector4 plane equations.
        if (!TryReadUInt32(data, block, 32, be, out var numVertices)) return;
        if (numVertices < 4 || numVertices > int.MaxValue) return;
        var vertexCount = (int)numVertices;
        var vertexBytes = (long)vertexCount * 16;
        const int vertexOffset = 36;
        if (vertexOffset + vertexBytes + 4 > block.Size) return;

        var localPositions = new Vector3[vertexCount];
        var maxAbsCoordinate = 1f;
        for (var i = 0; i < vertexCount; i++)
        {
            var rel = vertexOffset + i * 16;
            var p = new Vector3(
                BinaryUtils.ReadFloat(data, block.DataOffset + rel, be),
                BinaryUtils.ReadFloat(data, block.DataOffset + rel + 4, be),
                BinaryUtils.ReadFloat(data, block.DataOffset + rel + 8, be));
            if (!IsFinite(p)) return;
            localPositions[i] = p;
            maxAbsCoordinate = MathF.Max(maxAbsCoordinate,
                MathF.Max(MathF.Abs(p.X), MathF.Max(MathF.Abs(p.Y), MathF.Abs(p.Z))));
        }

        var normalCountOffset = vertexOffset + (int)vertexBytes;
        if (!TryReadUInt32(data, block, normalCountOffset, be, out var numNormals)) return;
        if (numNormals > int.MaxValue) return;
        var normalCount = (int)numNormals;
        var normalsOffset = normalCountOffset + 4;
        if (normalsOffset + (long)normalCount * 16 > block.Size) return;

        var localTriangles = new List<int>(Math.Max(12, normalCount * 6));
        var emittedFaces = new HashSet<string>(StringComparer.Ordinal);
        var face = new List<(int Index, float Angle)>(vertexCount);
        var sortedFaceIndices = new int[vertexCount];

        for (var planeIndex = 0; planeIndex < normalCount; planeIndex++)
        {
            var rel = normalsOffset + planeIndex * 16;
            var normal = new Vector3(
                BinaryUtils.ReadFloat(data, block.DataOffset + rel, be),
                BinaryUtils.ReadFloat(data, block.DataOffset + rel + 4, be),
                BinaryUtils.ReadFloat(data, block.DataOffset + rel + 8, be));
            var distance = BinaryUtils.ReadFloat(data, block.DataOffset + rel + 12, be);
            var normalLength = normal.Length();
            if (!IsFinite(normal) || !float.IsFinite(distance) || normalLength < 1e-6f) continue;

            var planeTolerance = 1e-4f * maxAbsCoordinate * MathF.Max(1f, normalLength);
            face.Clear();
            var centroid = Vector3.Zero;
            for (var i = 0; i < localPositions.Length; i++)
            {
                if (MathF.Abs(Vector3.Dot(normal, localPositions[i]) + distance) > planeTolerance) continue;
                centroid += localPositions[i];
                face.Add((i, 0f));
            }

            if (face.Count < 3) continue;
            centroid /= face.Count;
            normal /= normalLength;
            var helper = MathF.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
            var tangent = Vector3.Normalize(Vector3.Cross(normal, helper));
            var bitangent = Vector3.Cross(normal, tangent);
            for (var i = 0; i < face.Count; i++)
            {
                var delta = localPositions[face[i].Index] - centroid;
                face[i] = (face[i].Index,
                    MathF.Atan2(Vector3.Dot(delta, bitangent), Vector3.Dot(delta, tangent)));
                sortedFaceIndices[i] = face[i].Index;
            }

            face.Sort(static (a, b) => a.Angle.CompareTo(b.Angle));
            Array.Sort(sortedFaceIndices, 0, face.Count);
            var faceKey = string.Join(',', sortedFaceIndices.AsSpan(0, face.Count).ToArray());
            if (!emittedFaces.Add(faceKey)) continue;

            for (var i = 1; i + 1 < face.Count; i++)
            {
                localTriangles.Add(face[0].Index);
                localTriangles.Add(face[i].Index);
                localTriangles.Add(face[i + 1].Index);
            }
        }

        if (localTriangles.Count < 3) return;
        AppendPrimitiveMesh(localPositions, localTriangles, toWorld, scale, positions, triangles);
    }

    private static void AppendBox(byte[] data, BlockInfo block, bool be, Matrix4x4 toWorld,
        Vector3 scale, List<Vector3> positions, List<int> triangles)
    {
        // Material@0, convex Radius@4, Unused[8]@8, half-extents Vector3@16.
        var dimensions = new Vector3(
            MathF.Abs(TryReadFloat(data, block, 16, be) ?? 0f),
            MathF.Abs(TryReadFloat(data, block, 20, be) ?? 0f),
            MathF.Abs(TryReadFloat(data, block, 24, be) ?? 0f));
        if (!IsFinite(dimensions) || dimensions.X <= 0f || dimensions.Y <= 0f || dimensions.Z <= 0f) return;

        Vector3[] localPositions =
        [
            new(-dimensions.X, -dimensions.Y, -dimensions.Z),
            new( dimensions.X, -dimensions.Y, -dimensions.Z),
            new( dimensions.X,  dimensions.Y, -dimensions.Z),
            new(-dimensions.X,  dimensions.Y, -dimensions.Z),
            new(-dimensions.X, -dimensions.Y,  dimensions.Z),
            new( dimensions.X, -dimensions.Y,  dimensions.Z),
            new( dimensions.X,  dimensions.Y,  dimensions.Z),
            new(-dimensions.X,  dimensions.Y,  dimensions.Z),
        ];
        int[] localTriangles =
        [
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5,
            2, 3, 7, 2, 7, 6,
            3, 0, 4, 3, 4, 7,
        ];
        AppendPrimitiveMesh(localPositions, localTriangles, toWorld, scale, positions, triangles);
    }

    private static void AppendSphere(byte[] data, BlockInfo block, bool be, Matrix4x4 toWorld,
        Vector3 scale, List<Vector3> positions, List<int> triangles)
    {
        // bhkSphereShape has no fields beyond bhkConvexShape; its inherited Radius@4 is the sphere.
        var radius = MathF.Abs(TryReadFloat(data, block, 4, be) ?? 0f);
        if (!float.IsFinite(radius) || radius <= 0f) return;
        var localPositions = new List<Vector3>();
        var localTriangles = new List<int>();
        BuildSphere(Vector3.Zero, radius, localPositions, localTriangles);
        AppendPrimitiveMesh(localPositions, localTriangles, toWorld, scale, positions, triangles);
    }

    private static void AppendCapsule(byte[] data, BlockInfo block, bool be, Matrix4x4 toWorld,
        Vector3 scale, List<Vector3> positions, List<int> triangles)
    {
        // Material@0, convex Radius@4, Unused[8]@8, Point1@16, Radius1@28, Point2@32, Radius2@44.
        var point1 = TryReadVector3(data, block, 16, be);
        var point2 = TryReadVector3(data, block, 32, be);
        var radius1 = MathF.Abs(TryReadFloat(data, block, 28, be) ?? 0f);
        var radius2 = MathF.Abs(TryReadFloat(data, block, 44, be) ?? 0f);
        if (point1 is not { } p1 || point2 is not { } p2 ||
            !float.IsFinite(radius1) || !float.IsFinite(radius2) || MathF.Max(radius1, radius2) <= 0f)
        {
            return;
        }

        var axisVector = p2 - p1;
        var axisLength = axisVector.Length();
        var localPositions = new List<Vector3>();
        var localTriangles = new List<int>();
        if (axisLength < 1e-6f)
        {
            BuildSphere((p1 + p2) * 0.5f, MathF.Max(radius1, radius2), localPositions, localTriangles);
        }
        else
        {
            BuildCapsule(p1, p2, radius1, radius2, axisVector / axisLength, localPositions, localTriangles);
        }

        AppendPrimitiveMesh(localPositions, localTriangles, toWorld, scale, positions, triangles);
    }

    private static void BuildSphere(Vector3 center, float radius, List<Vector3> positions, List<int> triangles)
    {
        var top = positions.Count;
        positions.Add(center + Vector3.UnitZ * radius);
        var firstRing = positions.Count;
        for (var stack = 1; stack < SphereStacks; stack++)
        {
            var latitude = MathF.PI * 0.5f - MathF.PI * stack / SphereStacks;
            AddRing(center + Vector3.UnitZ * (MathF.Sin(latitude) * radius), Vector3.UnitX, Vector3.UnitY,
                MathF.Cos(latitude) * radius, positions);
        }

        var bottom = positions.Count;
        positions.Add(center - Vector3.UnitZ * radius);

        for (var segment = 0; segment < PrimitiveRadialSegments; segment++)
        {
            var next = (segment + 1) % PrimitiveRadialSegments;
            triangles.Add(top);
            triangles.Add(firstRing + segment);
            triangles.Add(firstRing + next);
        }

        for (var ring = 0; ring + 1 < SphereStacks - 1; ring++)
        {
            ConnectRings(firstRing + ring * PrimitiveRadialSegments,
                firstRing + (ring + 1) * PrimitiveRadialSegments, triangles);
        }

        var lastRing = firstRing + (SphereStacks - 2) * PrimitiveRadialSegments;
        for (var segment = 0; segment < PrimitiveRadialSegments; segment++)
        {
            var next = (segment + 1) % PrimitiveRadialSegments;
            triangles.Add(lastRing + next);
            triangles.Add(lastRing + segment);
            triangles.Add(bottom);
        }
    }

    private static void BuildCapsule(Vector3 point1, Vector3 point2, float radius1, float radius2,
        Vector3 axis, List<Vector3> positions, List<int> triangles)
    {
        var helper = MathF.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
        var tangent = Vector3.Normalize(Vector3.Cross(axis, helper));
        var bitangent = Vector3.Cross(axis, tangent);

        var startPole = positions.Count;
        positions.Add(point1 - axis * radius1);
        var firstRing = positions.Count;
        for (var ring = 1; ring <= CapsuleHemisphereRings; ring++)
        {
            var angle = -MathF.PI * 0.5f + MathF.PI * 0.5f * ring / CapsuleHemisphereRings;
            AddRing(point1 + axis * (MathF.Sin(angle) * radius1), tangent, bitangent,
                MathF.Cos(angle) * radius1, positions);
        }

        var point2Ring = positions.Count;
        AddRing(point2, tangent, bitangent, radius2, positions);
        for (var ring = 1; ring < CapsuleHemisphereRings; ring++)
        {
            var angle = MathF.PI * 0.5f * ring / CapsuleHemisphereRings;
            AddRing(point2 + axis * (MathF.Sin(angle) * radius2), tangent, bitangent,
                MathF.Cos(angle) * radius2, positions);
        }

        var endPole = positions.Count;
        positions.Add(point2 + axis * radius2);

        for (var segment = 0; segment < PrimitiveRadialSegments; segment++)
        {
            var next = (segment + 1) % PrimitiveRadialSegments;
            triangles.Add(startPole);
            triangles.Add(firstRing + next);
            triangles.Add(firstRing + segment);
        }

        var ringCount = CapsuleHemisphereRings * 2;
        for (var ring = 0; ring + 1 < ringCount; ring++)
        {
            ConnectRings(firstRing + ring * PrimitiveRadialSegments,
                firstRing + (ring + 1) * PrimitiveRadialSegments, triangles);
        }

        var lastRing = point2Ring + (CapsuleHemisphereRings - 1) * PrimitiveRadialSegments;
        for (var segment = 0; segment < PrimitiveRadialSegments; segment++)
        {
            var next = (segment + 1) % PrimitiveRadialSegments;
            triangles.Add(lastRing + segment);
            triangles.Add(lastRing + next);
            triangles.Add(endPole);
        }
    }

    private static void AddRing(Vector3 center, Vector3 tangent, Vector3 bitangent, float radius,
        List<Vector3> positions)
    {
        for (var segment = 0; segment < PrimitiveRadialSegments; segment++)
        {
            var angle = MathF.Tau * segment / PrimitiveRadialSegments;
            positions.Add(center + (tangent * MathF.Cos(angle) + bitangent * MathF.Sin(angle)) * radius);
        }
    }

    private static void ConnectRings(int first, int second, List<int> triangles)
    {
        for (var segment = 0; segment < PrimitiveRadialSegments; segment++)
        {
            var next = (segment + 1) % PrimitiveRadialSegments;
            triangles.Add(first + segment);
            triangles.Add(second + segment);
            triangles.Add(first + next);
            triangles.Add(first + next);
            triangles.Add(second + segment);
            triangles.Add(second + next);
        }
    }

    private static void AppendPrimitiveMesh(IReadOnlyList<Vector3> localPositions,
        IReadOnlyList<int> localTriangles, Matrix4x4 toWorld, Vector3 scale,
        List<Vector3> positions, List<int> triangles)
    {
        if (localPositions.Count == 0 || localTriangles.Count < 3) return;
        var baseIndex = positions.Count;
        var fullScale = scale * HavokToWorldScale;
        for (var i = 0; i < localPositions.Count; i++)
        {
            positions.Add(Vector3.Transform(localPositions[i] * fullScale, toWorld));
        }

        for (var i = 0; i + 2 < localTriangles.Count; i += 3)
        {
            var a = localTriangles[i];
            var b = localTriangles[i + 1];
            var c = localTriangles[i + 2];
            if ((uint)a >= (uint)localPositions.Count || (uint)b >= (uint)localPositions.Count ||
                (uint)c >= (uint)localPositions.Count) continue;
            triangles.Add(baseIndex + a);
            triangles.Add(baseIndex + b);
            triangles.Add(baseIndex + c);
        }
    }

    private static void AppendPackedData(byte[] data, NifInfo nif, int dataIdx, bool be, Matrix4x4 toWorld,
        Vector3 scale, List<Vector3> positions, List<int> triangles)
    {
        if (dataIdx < 0 || dataIdx >= nif.Blocks.Count) return;
        var block = nif.Blocks[dataIdx];
        if (block.TypeName != "hkPackedNiTriStripsData") return;

        var start = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (start + 4 > end) return;
        var numTriangles = BinaryUtils.ReadUInt32(data, start, be);
        var triStart = start + 4;
        // TriangleData = 3 ushort indices + 1 ushort weld, plus a trailing Vector3 normal on TES4-era
        // files only (nif.xml TriangleData Normal "until 20.0.0.5") — stride 20 vs 8. The Compressed
        // bool is "since 20.2.0.7": reading it on a TES4 file eats the first vertex byte.
        var tes4Era = NifVersions.IsTes4Era(nif.BinaryVersion);
        var triStride = tes4Era ? 20 : 8;
        var triBytes = (long)numTriangles * triStride;

        // triangles + NumVertices(4) + Compressed(1, modern only)
        if (triStart + triBytes + (tes4Era ? 4 : 5) > end) return;
        var pos = triStart + (int)triBytes;

        var numVertices = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        byte compressed = 0;
        if (!tes4Era)
        {
            compressed = data[pos];
            pos += 1;
        }

        var vertBytes = compressed != 0 ? (long)numVertices * 6 : (long)numVertices * 12;
        if (pos + vertBytes > end) return;

        var baseIndex = positions.Count;
        var fullScale = scale * HavokToWorldScale;
        if (compressed != 0)
        {
            // HalfVector3 (6 bytes/vertex) — raw/BE diagnostic path; the live converter decompresses.
            for (var v = 0; v < numVertices; v++, pos += 6)
            {
                var x = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, pos, be));
                var y = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, pos + 2, be));
                var z = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, pos + 4, be));
                positions.Add(Vector3.Transform(new Vector3(x * fullScale.X, y * fullScale.Y, z * fullScale.Z), toWorld));
            }
        }
        else
        {
            for (var v = 0; v < numVertices; v++, pos += 12)
            {
                var x = BinaryUtils.ReadFloat(data, pos, be);
                var y = BinaryUtils.ReadFloat(data, pos + 4, be);
                var z = BinaryUtils.ReadFloat(data, pos + 8, be);
                positions.Add(Vector3.Transform(new Vector3(x * fullScale.X, y * fullScale.Y, z * fullScale.Z), toWorld));
            }
        }

        // Triangle indices (skip each entry's trailing weld ushort + TES4 normal); drop any
        // out-of-range index.
        var tp = triStart;
        for (var t = 0; t < numTriangles; t++, tp += triStride)
        {
            var a = BinaryUtils.ReadUInt16(data, tp, be);
            var b = BinaryUtils.ReadUInt16(data, tp + 2, be);
            var c = BinaryUtils.ReadUInt16(data, tp + 4, be);
            if (a >= numVertices || b >= numVertices || c >= numVertices) continue;
            triangles.Add(baseIndex + a);
            triangles.Add(baseIndex + b);
            triangles.Add(baseIndex + c);
        }
    }

    // Substitutes 1 for an exact zero to guard a subsequent division; a non-zero (even tiny)
    // scale divides fine, so only the exact-0f case needs replacing — hence the equality test.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell",
        "S1244:Floating point numbers should not be tested for equality",
        Justification = "Only an exact 0f divisor must be replaced; near-zero scales are acceptable divisors.")]
    private static float NonZero(float v) => v == 0f ? 1f : v;

    private static bool TryReadInt32(byte[] data, BlockInfo block, int rel, bool be, out int value)
    {
        value = 0;
        if (rel < 0 || rel + 4 > block.Size) return false;
        value = BinaryUtils.ReadInt32(data, block.DataOffset + rel, be);
        return true;
    }

    /// <summary>Reads one byte at a block-relative offset. Endian-independent by construction.</summary>
    private static bool TryReadByte(byte[] data, BlockInfo block, int rel, out byte value)
    {
        value = 0;
        if (rel < 0 || rel + 1 > block.Size) return false;
        value = data[block.DataOffset + rel];
        return true;
    }

    private static bool TryReadUInt32(byte[] data, BlockInfo block, int rel, bool be, out uint value)
    {
        value = 0;
        if (rel < 0 || rel + 4 > block.Size) return false;
        value = BinaryUtils.ReadUInt32(data, block.DataOffset + rel, be);
        return true;
    }

    private static float? TryReadFloat(byte[] data, BlockInfo block, int rel, bool be)
    {
        if (rel < 0 || rel + 4 > block.Size) return null;
        return BinaryUtils.ReadFloat(data, block.DataOffset + rel, be);
    }

    private static Vector3? TryReadVector3(byte[] data, BlockInfo block, int rel, bool be)
    {
        if (rel < 0 || rel + 12 > block.Size) return null;
        var value = new Vector3(
            BinaryUtils.ReadFloat(data, block.DataOffset + rel, be),
            BinaryUtils.ReadFloat(data, block.DataOffset + rel + 4, be),
            BinaryUtils.ReadFloat(data, block.DataOffset + rel + 8, be));
        return IsFinite(value) ? value : null;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

