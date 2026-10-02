using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A typed view of one NiSkinInstance (nif.xml:12128-12150) or BSDismemberSkinInstance (nif.xml:16084-16090) block at
///     20.2.0.7: the Data and Skin Partition links, the Skeleton Root and Bones back pointers, and for the Bethesda
///     subclass its body-part list. Links are kept as stored (-1 for none); resolving them is the skin reader's work.
/// </summary>
internal sealed class NifSkinInstanceView
{
    private NifSkinInstanceView(int blockIndex, string type, int dataLink, int partitionLink, int skeletonRoot,
        IReadOnlyList<int> bones, IReadOnlyList<NifSkinBodyPart>? bodyParts)
    {
        BlockIndex = blockIndex;
        Type = type;
        DataLink = dataLink;
        PartitionLink = partitionLink;
        SkeletonRoot = skeletonRoot;
        Bones = bones;
        BodyParts = bodyParts;
    }

    /// <summary>The skin instance block index.</summary>
    public int BlockIndex { get; }

    /// <summary>The block type (NiSkinInstance or BSDismemberSkinInstance).</summary>
    public string Type { get; }

    /// <summary>The Data link (NiSkinData), -1 for none.</summary>
    public int DataLink { get; }

    /// <summary>The Skin Partition link (NiSkinPartition), -1 for none.</summary>
    public int PartitionLink { get; }

    /// <summary>The Skeleton Root back pointer, -1 for none.</summary>
    public int SkeletonRoot { get; }

    /// <summary>The Bones back pointers in stored order; bone k is joint k of the skin.</summary>
    public IReadOnlyList<int> Bones { get; }

    /// <summary>The BSDismemberSkinInstance body-part list (entry k for partition k); null for a plain NiSkinInstance.</summary>
    public IReadOnlyList<NifSkinBodyPart>? BodyParts { get; }

    /// <summary>Reads the view from a skin instance block that decoded exactly.</summary>
    /// <param name="block">The block.</param>
    /// <param name="isDismember">True when the block is a BSDismemberSkinInstance (or subclass).</param>
    /// <exception cref="InvalidDataException">A needed field did not decode with the expected shape.</exception>
    public static NifSkinInstanceView Read(NifDecodedBlock block, bool isDismember)
    {
        ArgumentNullException.ThrowIfNull(block);
        var root = block.Root;
        var data = Link(block, root, "Data");
        var partition = root.Contains("Skin Partition") ? Link(block, root, "Skin Partition") : -1;
        var skeleton = Link(block, root, "Skeleton Root");
        if (!root.TryGet("Bones", out var bonesValue) || bonesValue is not NifArrayValue { IsRows: false } bones)
        {
            throw Missing(block, "Bones");
        }

        var boneLinks = new int[bones.Count];
        for (var i = 0; i < boneLinks.Length; i++)
        {
            boneLinks[i] = bones.Items[i] is NifRefValue reference
                ? reference.Index
                : throw Missing(block, string.Create(CultureInfo.InvariantCulture, $"Bones[{i}]"));
        }

        IReadOnlyList<NifSkinBodyPart>? bodyParts = null;
        if (isDismember)
        {
            if (!root.TryGet("Partitions", out var partsValue) || partsValue is not NifArrayValue { IsRows: false } parts)
            {
                throw Missing(block, "Partitions");
            }

            var list = new NifSkinBodyPart[parts.Count];
            for (var i = 0; i < list.Length; i++)
            {
                if (parts.Items[i] is not NifStructValue entry ||
                    !entry.TryGet("Part Flag", out var flagValue) || flagValue is not NifIntegerValue flag ||
                    !entry.TryGet("Body Part", out var partValue) || partValue is not NifIntegerValue part)
                {
                    throw Missing(block, string.Create(CultureInfo.InvariantCulture, $"Partitions[{i}]"));
                }

                list[i] = new NifSkinBodyPart(unchecked((ushort)flag.RawBits), unchecked((ushort)part.RawBits));
            }

            bodyParts = list;
        }

        return new NifSkinInstanceView(block.Index, block.Type, data, partition, skeleton, boneLinks, bodyParts);
    }

    /// <summary>
    ///     The links a skin instance names, read without requiring an exact decode (for coverage of blocks whose geometry
    ///     is not typed): Data and Skin Partition when they decoded as links into the file, else null.
    /// </summary>
    public static (int? Data, int? Partition) TryReadLinks(NifDecodedBlock block, int blockCount)
    {
        ArgumentNullException.ThrowIfNull(block);
        return (TryLink(block.Root, "Data", blockCount), TryLink(block.Root, "Skin Partition", blockCount));
    }

    private static int? TryLink(NifStructValue root, string field, int blockCount)
    {
        return root.TryGet(field, out var value) && value is NifRefValue { IsNone: false } link &&
               (uint)link.Index < (uint)blockCount
            ? link.Index
            : null;
    }

    private static int Link(NifDecodedBlock block, NifStructValue root, string field)
    {
        return root.TryGet(field, out var value) && value is NifRefValue link
            ? link.Index
            : throw Missing(block, field);
    }

    private static InvalidDataException Missing(NifDecodedBlock block, string field)
    {
        return new InvalidDataException(
            $"NIF block {block.Index} ({block.Type}) did not decode '{field}' with the shape a typed skin needs.");
    }
}
