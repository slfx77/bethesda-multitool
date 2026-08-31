using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;

/// <summary>
///     Bounded Fallout 76 <c>BSSkin::Instance</c>/<c>BSSkin::BoneData</c> extraction for the
///     self-contained <c>BSTriShape</c> family. This deliberately does not generalize the layout to
///     Starfield or to the older <c>NiSkinInstance</c> family.
/// </summary>
internal static class Fo76BsSkinBindingExtractor
{
    private const int BoneTransformSize = 68;
    private const int MaximumDirectBoneCount = byte.MaxValue + 1;
    private const ulong SkinnedVertexAttribute = 1UL << 6;

    private static readonly HashSet<string> SupportedShapeTypes =
    [
        "BSTriShape", "BSSubIndexTriShape", "BSMeshLODTriShape", "BSDynamicTriShape"
    ];

    /// <summary>
    ///     Extract one FO76 packed shape's rest-pose skin. Inverse bind matrices remain in NIF
    ///     coordinates because <c>GlbWriter</c> converts them at the same boundary as node matrices;
    ///     <see cref="Fo76BsSkinBinding.CreateGltfInverseBindMatrices" /> is exposed for consumers
    ///     that write glTF directly.
    /// </summary>
    internal static Fo76BsSkinBindingResult Extract(
        byte[] data,
        NifInfo nif,
        int shapeBlockIndex,
        IReadOnlyDictionary<int, List<int>> nodeChildren)
    {
        if (nif.BsVersion < 155 ||
            shapeBlockIndex < 0 ||
            shapeBlockIndex >= nif.Blocks.Count ||
            !SupportedShapeTypes.Contains(nif.Blocks[shapeBlockIndex].TypeName))
        {
            return Fo76BsSkinBindingResult.NotApplicable();
        }

        var shapeBlock = nif.Blocks[shapeBlockIndex];
        if (!IsBlockRangeValid(data, shapeBlock))
        {
            return Fo76BsSkinBindingResult.Failed(
                Fo76BsSkinBindingStatus.MalformedData,
                $"Shape block {shapeBlockIndex} is outside the NIF payload.");
        }

        var parsedShape = NifSceneGraphBlockReader.ParseBsTriShape(
            data,
            shapeBlock,
            nif.BsVersion,
            nif.BinaryVersion,
            nif.IsBigEndian);
        if (parsedShape is not { } shape)
        {
            return Fo76BsSkinBindingResult.Failed(
                Fo76BsSkinBindingStatus.MalformedData,
                $"Shape block {shapeBlockIndex} has an invalid BSTriShape payload.");
        }

        if (shape.SkinRef < 0)
        {
            return Fo76BsSkinBindingResult.NotSkinned();
        }

        if (shape.SkinRef >= nif.Blocks.Count)
        {
            return Fo76BsSkinBindingResult.Failed(
                Fo76BsSkinBindingStatus.MalformedData,
                $"Shape block {shapeBlockIndex} has out-of-range SkinRef {shape.SkinRef}.");
        }

        var instanceBlock = nif.Blocks[shape.SkinRef];
        if (instanceBlock.TypeName != "BSSkin::Instance")
        {
            return Fo76BsSkinBindingResult.Failed(
                Fo76BsSkinBindingStatus.UnsupportedSkinInstance,
                $"FO76 shape {shapeBlockIndex} uses unsupported skin block {instanceBlock.TypeName}.");
        }

        var instanceRead = ReadInstance(data, nif, instanceBlock);
        if (instanceRead.Status != Fo76BsSkinBindingStatus.Success ||
            instanceRead.Instance is not { } instance)
        {
            return Fo76BsSkinBindingResult.Failed(instanceRead.Status, instanceRead.Diagnostic);
        }

        if (!TryValidateSkeleton(
                data,
                nif,
                instance.SkeletonRootRef,
                instance.BoneRefs,
                nodeChildren,
                out var boneNames,
                out var skeletonDiagnostic))
        {
            return Fo76BsSkinBindingResult.Failed(
                Fo76BsSkinBindingStatus.UnsupportedSkeleton,
                skeletonDiagnostic);
        }

        if (instance.DataRef < 0 || instance.DataRef >= nif.Blocks.Count)
        {
            return Fo76BsSkinBindingResult.Failed(
                Fo76BsSkinBindingStatus.MalformedData,
                $"BSSkin::Instance {shape.SkinRef} has out-of-range Data ref {instance.DataRef}.");
        }

        var boneDataBlock = nif.Blocks[instance.DataRef];
        if (boneDataBlock.TypeName != "BSSkin::BoneData")
        {
            return Fo76BsSkinBindingResult.Failed(
                Fo76BsSkinBindingStatus.UnsupportedSkinInstance,
                $"BSSkin::Instance {shape.SkinRef} references {boneDataBlock.TypeName}, not BSSkin::BoneData.");
        }

        var boneDataRead = ReadBoneData(data, nif, boneDataBlock, instance.BoneRefs.Length);
        if (boneDataRead.Matrices is not { } inverseBindMatrices)
        {
            return Fo76BsSkinBindingResult.Failed(
                Fo76BsSkinBindingStatus.MalformedData,
                boneDataRead.Diagnostic);
        }

        var influenceRead = DecodePackedInfluences(data, shape, instance.BoneRefs.Length, nif.IsBigEndian);
        if (influenceRead.Status != Fo76BsSkinBindingStatus.Success ||
            influenceRead.PerVertexInfluences is not { } perVertexInfluences)
        {
            return Fo76BsSkinBindingResult.Failed(influenceRead.Status, influenceRead.Diagnostic);
        }

        return Fo76BsSkinBindingResult.Succeeded(new Fo76BsSkinBinding
        {
            ShapeBlockIndex = shapeBlockIndex,
            SkinInstanceBlockIndex = shape.SkinRef,
            SkeletonRootBlockIndex = instance.SkeletonRootRef,
            BoneBlockIndices = instance.BoneRefs,
            BoneNames = boneNames,
            InverseBindMatrices = inverseBindMatrices,
            PerVertexInfluences = perVertexInfluences,
            MaxStoredFourthWeightError = influenceRead.MaxStoredFourthWeightError,
            MaxGltfNormalizationDelta = influenceRead.MaxGltfNormalizationDelta
        });
    }

    /// <summary>
    ///     True only when a FO76 BSTriShape directly points at <c>BSSkin::Instance</c>. This is a
    ///     structural routing predicate, not a claim that the full binding is supported.
    /// </summary>
    internal static bool IsCandidate(byte[] data, NifInfo nif, int shapeBlockIndex)
    {
        if (nif.BsVersion < 155 ||
            shapeBlockIndex < 0 ||
            shapeBlockIndex >= nif.Blocks.Count ||
            !SupportedShapeTypes.Contains(nif.Blocks[shapeBlockIndex].TypeName))
        {
            return false;
        }

        var block = nif.Blocks[shapeBlockIndex];
        var parsed = IsBlockRangeValid(data, block)
            ? NifSceneGraphBlockReader.ParseBsTriShape(
                data, block, nif.BsVersion, nif.BinaryVersion, nif.IsBigEndian)
            : null;
        if (parsed is not { } shape || shape.SkinRef < 0 || shape.SkinRef >= nif.Blocks.Count)
        {
            return false;
        }

        return nif.Blocks[shape.SkinRef].TypeName == "BSSkin::Instance";
    }

    /// <summary>
    ///     Decode FO76's packed shader inputs: three half weights plus a fourth residual weight,
    ///     and four direct byte indices into the BSSkin bone array. The serialized fourth half is
    ///     measured for diagnostics but is not consumed by the retail shader.
    /// </summary>
    internal static Fo76PackedInfluenceDecodeResult DecodePackedInfluences(
        byte[] data,
        NifSceneGraphBlockReader.BsTriShapeInfo shape,
        int boneCount,
        bool be)
    {
        var vertexSize = (int)(shape.VertexDesc & 0xF) * 4;
        var attributes = (shape.VertexDesc >> 44) & 0xFFF;
        var skinOffset = (int)((shape.VertexDesc >> 28) & 0xF) * 4;
        if ((attributes & SkinnedVertexAttribute) == 0 ||
            vertexSize <= 0 ||
            skinOffset + 12 > vertexSize)
        {
            return Fo76PackedInfluenceDecodeResult.Failed(
                Fo76BsSkinBindingStatus.UnsupportedVertexLayout,
                $"Packed FO76 skin lanes do not fit descriptor 0x{shape.VertexDesc:X16}.");
        }

        if (boneCount is <= 0 or > MaximumDirectBoneCount)
        {
            return Fo76PackedInfluenceDecodeResult.Failed(
                Fo76BsSkinBindingStatus.UnsupportedSkeleton,
                $"Direct byte joint indices require 1..{MaximumDirectBoneCount} bones, found {boneCount}.");
        }

        var vertexBytes = (long)shape.NumVertices * vertexSize;
        if (shape.NumVertices <= 0 ||
            shape.VertexBufferOffset < 0 ||
            shape.VertexBufferOffset + vertexBytes > data.Length)
        {
            return Fo76PackedInfluenceDecodeResult.Failed(
                Fo76BsSkinBindingStatus.MalformedData,
                "Packed FO76 vertex buffer lies outside the NIF payload.");
        }

        var influences = new (int BoneIdx, float Weight)[shape.NumVertices][];
        var maxStoredFourthError = 0f;
        var maxNormalizationDelta = 0f;
        Span<float> engineWeights = stackalloc float[4];
        for (var vertexIndex = 0; vertexIndex < shape.NumVertices; vertexIndex++)
        {
            var skinBase = shape.VertexBufferOffset + vertexIndex * vertexSize + skinOffset;
            engineWeights[0] = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, skinBase, be));
            engineWeights[1] = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, skinBase + 2, be));
            engineWeights[2] = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, skinBase + 4, be));
            var storedFourth = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, skinBase + 6, be));

            if (!float.IsFinite(storedFourth) ||
                !float.IsFinite(engineWeights[0]) ||
                !float.IsFinite(engineWeights[1]) ||
                !float.IsFinite(engineWeights[2]) ||
                engineWeights[0] < 0f ||
                engineWeights[1] < 0f ||
                engineWeights[2] < 0f)
            {
                return Fo76PackedInfluenceDecodeResult.Failed(
                    Fo76BsSkinBindingStatus.MalformedData,
                    $"Vertex {vertexIndex} contains a non-finite or negative packed skin weight.");
            }

            // Retail DFPrePass: w3 = 1 - saturate(w0 + w1 + w2). The serialized fourth half is
            // redundant and can differ by half-float rounding, so it must not drive the deformation.
            var firstThreeSum = engineWeights[0] + engineWeights[1] + engineWeights[2];
            engineWeights[3] = 1f - Math.Clamp(firstThreeSum, 0f, 1f);
            maxStoredFourthError = Math.Max(
                maxStoredFourthError,
                Math.Abs(storedFourth - engineWeights[3]));

            // glTF requires normalized weights. Derive the shader's fourth lane first, then apply
            // the smallest normalization needed for half-float sums slightly above one.
            var engineTotal = engineWeights[0] + engineWeights[1] + engineWeights[2] + engineWeights[3];
            if (!float.IsFinite(engineTotal) || engineTotal <= 0f)
            {
                return Fo76PackedInfluenceDecodeResult.Failed(
                    Fo76BsSkinBindingStatus.MalformedData,
                    $"Vertex {vertexIndex} has an invalid total skin weight {engineTotal}.");
            }

            var normalization = 1f / engineTotal;
            var vertexInfluences = new List<(int BoneIdx, float Weight)>(4);
            for (var lane = 0; lane < 4; lane++)
            {
                var normalizedWeight = engineWeights[lane] * normalization;
                maxNormalizationDelta = Math.Max(
                    maxNormalizationDelta,
                    Math.Abs(normalizedWeight - engineWeights[lane]));
                if (normalizedWeight <= 0f)
                {
                    continue;
                }

                // Retail shader converts the byte lane directly to an integer palette index; there
                // is no NiSkinPartition/local-palette remap for this BSTriShape layout.
                var directBoneIndex = data[skinBase + 8 + lane];
                if (directBoneIndex >= boneCount)
                {
                    return Fo76PackedInfluenceDecodeResult.Failed(
                        Fo76BsSkinBindingStatus.MalformedData,
                        $"Vertex {vertexIndex} lane {lane} references direct bone {directBoneIndex}, " +
                        $"but the BSSkin array has {boneCount} entries.");
                }

                vertexInfluences.Add((directBoneIndex, normalizedWeight));
            }

            influences[vertexIndex] = vertexInfluences.ToArray();
        }

        return Fo76PackedInfluenceDecodeResult.Succeeded(
            influences,
            maxStoredFourthError,
            maxNormalizationDelta);
    }

    private static Fo76InstanceReadResult ReadInstance(byte[] data, NifInfo nif, BlockInfo block)
    {
        if (!IsBlockRangeValid(data, block) || block.Size < 16)
        {
            return Fo76InstanceReadResult.Failed(
                Fo76BsSkinBindingStatus.MalformedData,
                $"BSSkin::Instance {block.Index} is truncated.");
        }

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        var skeletonRootRef = BinaryUtils.ReadInt32(data, pos, nif.IsBigEndian);
        var dataRef = BinaryUtils.ReadInt32(data, pos + 4, nif.IsBigEndian);
        var boneCount = BinaryUtils.ReadUInt32(data, pos + 8, nif.IsBigEndian);
        pos += 12;
        if (boneCount is 0 or > MaximumDirectBoneCount)
        {
            return Fo76InstanceReadResult.Failed(
                Fo76BsSkinBindingStatus.UnsupportedSkeleton,
                $"BSSkin::Instance {block.Index} declares unsupported bone count {boneCount}.");
        }

        if (pos + (long)boneCount * sizeof(int) + sizeof(uint) > end)
        {
            return Fo76InstanceReadResult.Failed(
                Fo76BsSkinBindingStatus.MalformedData,
                $"BSSkin::Instance {block.Index} truncates its {boneCount}-bone reference array.");
        }

        var boneRefs = new int[(int)boneCount];
        for (var boneIndex = 0; boneIndex < boneRefs.Length; boneIndex++)
        {
            boneRefs[boneIndex] = BinaryUtils.ReadInt32(data, pos, nif.IsBigEndian);
            pos += sizeof(int);
        }

        var scaleCount = BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian);
        pos += sizeof(uint);
        var scaleBytes = (long)scaleCount * 12;
        if (scaleCount > MaximumDirectBoneCount || pos + scaleBytes != end)
        {
            return Fo76InstanceReadResult.Failed(
                Fo76BsSkinBindingStatus.MalformedData,
                $"BSSkin::Instance {block.Index} has an invalid NumScales payload ({scaleCount}).");
        }

        if (scaleCount > 0)
        {
            // The retail femalebody fixture is NumScales == 0. The meaning/composition point of the
            // optional scale vectors has not been established from the engine, so fail closed.
            return Fo76InstanceReadResult.Failed(
                Fo76BsSkinBindingStatus.UnsupportedNonZeroScales,
                $"BSSkin::Instance {block.Index} has NumScales={scaleCount}; only zero is verified.");
        }

        return Fo76InstanceReadResult.Succeeded(new Fo76BsSkinInstanceData(
            skeletonRootRef,
            dataRef,
            boneRefs));
    }

    private static (Matrix4x4[]? Matrices, string Diagnostic) ReadBoneData(
        byte[] data,
        NifInfo nif,
        BlockInfo block,
        int expectedBoneCount)
    {
        if (!IsBlockRangeValid(data, block) || block.Size < sizeof(uint))
        {
            return (null, $"BSSkin::BoneData {block.Index} is truncated.");
        }

        var declaredBoneCount = BinaryUtils.ReadUInt32(data, block.DataOffset, nif.IsBigEndian);
        var expectedSize = sizeof(uint) + (long)declaredBoneCount * BoneTransformSize;
        if (declaredBoneCount != expectedBoneCount || expectedSize != block.Size)
        {
            return (null,
                $"BSSkin::BoneData {block.Index} count/size mismatch: instance={expectedBoneCount}, " +
                $"data={declaredBoneCount}, bytes={block.Size}.");
        }

        var matrices = new Matrix4x4[expectedBoneCount];
        var pos = block.DataOffset + sizeof(uint);
        for (var boneIndex = 0; boneIndex < matrices.Length; boneIndex++)
        {
            // BSSkinBoneTrans = NiBound (16 bytes) + NiTransform (52 bytes). Engine
            // CalculateBoneMatrices composes this stored transform with the live bone world matrix;
            // therefore this is already the skin/mesh-to-bone inverse bind and must not be inverted.
            var inverseBind = NifSkinBlockParser.ParseNiTransform(data, pos + 16, nif.IsBigEndian);
            var determinant = inverseBind.GetDeterminant();
            if (!IsFinite(inverseBind) || !float.IsFinite(determinant) || Math.Abs(determinant) < 1e-8f)
            {
                return (null, $"BSSkin::BoneData {block.Index} bone {boneIndex} has a singular transform.");
            }

            matrices[boneIndex] = inverseBind;
            pos += BoneTransformSize;
        }

        return (matrices, string.Empty);
    }

    private static bool TryValidateSkeleton(
        byte[] data,
        NifInfo nif,
        int skeletonRootRef,
        int[] boneRefs,
        IReadOnlyDictionary<int, List<int>> nodeChildren,
        out string[] boneNames,
        out string diagnostic)
    {
        boneNames = [];
        if (skeletonRootRef < 0 ||
            skeletonRootRef >= nif.Blocks.Count ||
            !NifSceneGraphWalker.NodeTypes.Contains(nif.Blocks[skeletonRootRef].TypeName))
        {
            diagnostic = $"Skeleton root {skeletonRootRef} is not a supported scene node.";
            return false;
        }

        if (boneRefs.Distinct().Count() != boneRefs.Length)
        {
            diagnostic = "BSSkin bone references contain duplicates.";
            return false;
        }

        var reachable = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(skeletonRootRef);
        while (pending.TryPop(out var current))
        {
            if (!reachable.Add(current) || !nodeChildren.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (child >= 0 && child < nif.Blocks.Count &&
                    NifSceneGraphWalker.NodeTypes.Contains(nif.Blocks[child].TypeName))
                {
                    pending.Push(child);
                }
            }
        }

        boneNames = new string[boneRefs.Length];
        var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var boneIndex = 0; boneIndex < boneRefs.Length; boneIndex++)
        {
            var boneRef = boneRefs[boneIndex];
            if (boneRef < 0 ||
                boneRef >= nif.Blocks.Count ||
                !NifSceneGraphWalker.NodeTypes.Contains(nif.Blocks[boneRef].TypeName) ||
                !reachable.Contains(boneRef))
            {
                diagnostic = $"BSSkin bone {boneIndex} ref {boneRef} is outside the skeleton-root subtree.";
                return false;
            }

            var name = NifBlockParsers.ReadBlockName(data, nif.Blocks[boneRef], nif);
            if (string.IsNullOrWhiteSpace(name) || !uniqueNames.Add(name))
            {
                diagnostic = $"BSSkin bone {boneIndex} has a missing or duplicate name; name binding is ambiguous.";
                return false;
            }

            boneNames[boneIndex] = name;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool IsBlockRangeValid(byte[] data, BlockInfo block)
    {
        return block.DataOffset >= 0 &&
               block.Size >= 0 &&
               block.DataOffset <= data.Length - block.Size;
    }

    private static bool IsFinite(Matrix4x4 matrix)
    {
        return float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
               float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
               float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) &&
               float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
               float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) &&
               float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
               float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) &&
               float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);
    }

    private sealed record Fo76BsSkinInstanceData(
        int SkeletonRootRef,
        int DataRef,
        int[] BoneRefs);

    private readonly record struct Fo76InstanceReadResult(
        Fo76BsSkinBindingStatus Status,
        Fo76BsSkinInstanceData? Instance,
        string Diagnostic)
    {
        internal static Fo76InstanceReadResult Succeeded(Fo76BsSkinInstanceData instance) =>
            new(Fo76BsSkinBindingStatus.Success, instance, string.Empty);

        internal static Fo76InstanceReadResult Failed(Fo76BsSkinBindingStatus status, string diagnostic) =>
            new(status, null, diagnostic);
    }
}

internal enum Fo76BsSkinBindingStatus
{
    Success,
    NotApplicable,
    NotSkinned,
    UnsupportedSkinInstance,
    UnsupportedNonZeroScales,
    UnsupportedSkeleton,
    UnsupportedVertexLayout,
    MalformedData
}

/// <summary>A supported FO76 binding in NIF coordinates, ready for the existing GLB scene adapter.</summary>
internal sealed class Fo76BsSkinBinding
{
    public required int ShapeBlockIndex { get; init; }

    public required int SkinInstanceBlockIndex { get; init; }

    public required int SkeletonRootBlockIndex { get; init; }

    public required int[] BoneBlockIndices { get; init; }

    public required string[] BoneNames { get; init; }

    /// <summary>Skin/mesh-to-bone inverse binds in Bethesda's Z-up NIF basis.</summary>
    public required Matrix4x4[] InverseBindMatrices { get; init; }

    /// <summary>Normalized glTF-ready weights, indexed directly into <see cref="BoneNames" />.</summary>
    public required (int BoneIdx, float Weight)[][] PerVertexInfluences { get; init; }

    public required float MaxStoredFourthWeightError { get; init; }

    public required float MaxGltfNormalizationDelta { get; init; }

    /// <summary>
    ///     Basis-conjugates the inverse binds exactly as the existing GLB writer does. Callers that
    ///     pass <see cref="InverseBindMatrices" /> to that writer must not call this first.
    /// </summary>
    internal Matrix4x4[] CreateGltfInverseBindMatrices()
    {
        return InverseBindMatrices.Select(GltfCoordinateAdapter.ConvertMatrix).ToArray();
    }
}

internal readonly record struct Fo76BsSkinBindingResult(
    Fo76BsSkinBindingStatus Status,
    Fo76BsSkinBinding? Binding,
    string Diagnostic)
{
    internal bool IsHardFailure =>
        Status is not (Fo76BsSkinBindingStatus.Success or
            Fo76BsSkinBindingStatus.NotApplicable or
            Fo76BsSkinBindingStatus.NotSkinned);

    internal static Fo76BsSkinBindingResult Succeeded(Fo76BsSkinBinding binding) =>
        new(Fo76BsSkinBindingStatus.Success, binding, string.Empty);

    internal static Fo76BsSkinBindingResult NotApplicable() =>
        new(Fo76BsSkinBindingStatus.NotApplicable, null, string.Empty);

    internal static Fo76BsSkinBindingResult NotSkinned() =>
        new(Fo76BsSkinBindingStatus.NotSkinned, null, string.Empty);

    internal static Fo76BsSkinBindingResult Failed(Fo76BsSkinBindingStatus status, string diagnostic) =>
        new(status, null, diagnostic);
}

internal readonly record struct Fo76PackedInfluenceDecodeResult(
    Fo76BsSkinBindingStatus Status,
    (int BoneIdx, float Weight)[][]? PerVertexInfluences,
    float MaxStoredFourthWeightError,
    float MaxGltfNormalizationDelta,
    string Diagnostic)
{
    internal static Fo76PackedInfluenceDecodeResult Succeeded(
        (int BoneIdx, float Weight)[][] influences,
        float maxStoredFourthWeightError,
        float maxGltfNormalizationDelta) =>
        new(
            Fo76BsSkinBindingStatus.Success,
            influences,
            maxStoredFourthWeightError,
            maxGltfNormalizationDelta,
            string.Empty);

    internal static Fo76PackedInfluenceDecodeResult Failed(
        Fo76BsSkinBindingStatus status,
        string diagnostic) =>
        new(status, null, 0f, 0f, diagnostic);
}
