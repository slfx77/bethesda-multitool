using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A typed view of one NiSkinData block (nif.xml:12102-12126 with BoneData 6986-7009) at 20.2.0.7: the overall Skin
///     Transform, the Has Vertex Weights byte as stored, and one <see cref="NifSkinBoneView" /> per Bone List entry. It
///     interprets the decoded value tree and never computes an offset; a field the view needs that did not decode, or
///     decoded with another shape, is corrupt input for a typed block.
/// </summary>
internal sealed class NifSkinDataView
{
    private NifSkinDataView(int blockIndex, NifSkinTransformView overall, long hasVertexWeights,
        IReadOnlyList<NifSkinBoneView> bones)
    {
        BlockIndex = blockIndex;
        Overall = overall;
        HasVertexWeights = hasVertexWeights;
        Bones = bones;
    }

    /// <summary>The NiSkinData block index.</summary>
    public int BlockIndex { get; }

    /// <summary>The overall Skin Transform (nif.xml: "offset of the skin from this bone in bind position").</summary>
    public NifSkinTransformView Overall { get; }

    /// <summary>The Has Vertex Weights byte exactly as stored (a bool is not always 0 or 1).</summary>
    public long HasVertexWeights { get; }

    /// <summary>True when the bones store their vertex weights (Has Vertex Weights not 0).</summary>
    public bool StoresWeights => HasVertexWeights != 0;

    /// <summary>The Bone List in stored order, parallel to the skin instance's Bones.</summary>
    public IReadOnlyList<NifSkinBoneView> Bones { get; }

    /// <summary>Reads the view from a block that decoded exactly.</summary>
    /// <exception cref="InvalidDataException">A needed field did not decode with the expected shape.</exception>
    public static NifSkinDataView Read(NifDecodedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var root = block.Root;
        var overall = NifSkinTransformView.Read(block, root, "Skin Transform", "Skin Transform");
        var hasWeights = root.TryGet("Has Vertex Weights", out var flag) && flag is NifIntegerValue flagValue
            ? flagValue.Value
            : throw Missing(block, "Has Vertex Weights");
        if (!root.TryGet("Bone List", out var listValue) || listValue is not NifArrayValue { IsRows: false } list)
        {
            throw Missing(block, "Bone List");
        }

        var bones = new NifSkinBoneView[list.Count];
        for (var b = 0; b < bones.Length; b++)
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"Bone List[{b}]");
            if (list.Items[b] is not NifStructValue bone)
            {
                throw Missing(block, path);
            }

            var transform = NifSkinTransformView.Read(block, bone, "Skin Transform", path + ".Skin Transform");
            var stored = bone.TryGet("Num Vertices", out var countValue) && countValue is NifIntegerValue count
                ? checked((int)count.Value)
                : throw Missing(block, path + ".Num Vertices");
            var weights = Array.Empty<(int, float)>();
            if (bone.TryGet("Vertex Weights", out var weightsValue))
            {
                weights = ReadWeights(block, weightsValue, path, stored);
            }
            else if (hasWeights != 0)
            {
                throw Missing(block, path + ".Vertex Weights");
            }

            bones[b] = new NifSkinBoneView(transform, stored, weights);
        }

        return new NifSkinDataView(block.Index, overall, hasWeights, bones);
    }

    private static (int, float)[] ReadWeights(NifDecodedBlock block, NifValue value, string path, int stored)
    {
        if (value is not NifArrayValue { IsRows: false } array || array.Count != stored)
        {
            throw Missing(block, path + ".Vertex Weights");
        }

        var weights = new (int, float)[array.Count];
        for (var w = 0; w < weights.Length; w++)
        {
            if (array.Items[w] is not NifStructValue entry ||
                !entry.TryGet("Index", out var indexValue) || indexValue is not NifIntegerValue index ||
                !entry.TryGet("Weight", out var weightValue) || weightValue is not NifFloatValue weight)
            {
                throw Missing(block, string.Create(CultureInfo.InvariantCulture, $"{path}.Vertex Weights[{w}]"));
            }

            weights[w] = (checked((int)index.Value), weight.Value);
        }

        return weights;
    }

    private static InvalidDataException Missing(NifDecodedBlock block, string field)
    {
        return new InvalidDataException(
            $"NIF block {block.Index} ({block.Type}) did not decode '{field}' with the shape a typed skin needs.");
    }
}
