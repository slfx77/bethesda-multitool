using System.Numerics;
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
///         tri-strip leaves (<c>hkPackedNiTriStripsData</c>) — the shape kind FNV floors/walkways/
///         bridges use. Convex/box/sphere shapes are skipped in v1 (callers fall back to the visual
///         mesh, exactly as before). Output is in the NIF root-local <c>treatRootsAsIdentity</c>
///         frame, matching the visual collision soup, so the existing placement raycast is unchanged.
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

    private const int MaxShapeDepth = 16;

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
            var children = NifBlockParsers.ParseNodeChildren(data, block, nif.BsVersion, bigEndian, nif.HasInlineStrings);
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

            var nodeWorld = worldTransforms.TryGetValue(targetIdx, out var w) ? w : Matrix4x4.Identity;
            var rbTransform = bodyBlock.TypeName == "bhkRigidBodyT"
                ? TryReadRigidBodyTTransform(data, bodyBlock, bigEndian) ?? Matrix4x4.Identity
                : Matrix4x4.Identity;
            var shapeToWorld = rbTransform * nodeWorld;

            // Fresh visited set per collision object so a shape shared between bodies (with different
            // transforms) is still emitted for each; the set only guards against cycles within one walk.
            AppendShape(data, nif, shapeRef, bigEndian, shapeToWorld, Vector3.One, positions, triangles,
                new HashSet<int>(), depth: 0);
        }

        if (triangles.Count < 3) return null;
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

    // bhkRigidBodyT CInfo (FNV 550_660): Translation Vector4 @28, Rotation QuaternionXYZW @44.
    // A plain bhkRigidBody ignores these (engine-side), so only bhkRigidBodyT reaches here.
    private static Matrix4x4? TryReadRigidBodyTTransform(byte[] data, BlockInfo block, bool be)
    {
        // Need the rotation quaternion's last float at offset 44+12 = 56 (read 4 bytes → 60).
        if (block.Size < 60) return null;
        var tx = BinaryUtils.ReadFloat(data, block.DataOffset + 28, be);
        var ty = BinaryUtils.ReadFloat(data, block.DataOffset + 32, be);
        var tz = BinaryUtils.ReadFloat(data, block.DataOffset + 36, be);
        var qx = BinaryUtils.ReadFloat(data, block.DataOffset + 44, be);
        var qy = BinaryUtils.ReadFloat(data, block.DataOffset + 48, be);
        var qz = BinaryUtils.ReadFloat(data, block.DataOffset + 52, be);
        var qw = BinaryUtils.ReadFloat(data, block.DataOffset + 56, be);

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
                // FNV layout (no pre-20.0.0.5 sub-shape header): UserData@0, Unused@4, Radius@8,
                // Unused@12, Scale Vector4 @16, RadiusCopy@32, ScaleCopy@36, Data ref @52.
                var sx = TryReadFloat(data, block, 16, be) ?? 1f;
                var sy = TryReadFloat(data, block, 20, be) ?? 1f;
                var sz = TryReadFloat(data, block, 24, be) ?? 1f;
                var shapeScale = accumScale * new Vector3(NonZero(sx), NonZero(sy), NonZero(sz));
                if (!TryReadInt32(data, block, 52, be, out var dataRef)) return;
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
                    AppendShape(data, nif, subRef, be, toWorld, accumScale, positions, triangles, visited, depth + 1);
                }

                break;
            }
            // Convex hulls / boxes / spheres / transform shapes are not the gappy plank meshes this fix
            // targets; skip them in v1 (the caller falls back to the visual mesh, as before).
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
        var triBytes = (long)numTriangles * 8; // each TriangleData = 3 ushort indices + 1 ushort weld

        // triangles + NumVertices(4) + Compressed(1)
        if (triStart + triBytes + 5 > end) return;
        var pos = triStart + (int)triBytes;

        var numVertices = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        var compressed = data[pos];
        pos += 1;

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

        // Triangle indices (skip the trailing weld ushort of each entry); drop any out-of-range index.
        var tp = triStart;
        for (var t = 0; t < numTriangles; t++, tp += 8)
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
}
