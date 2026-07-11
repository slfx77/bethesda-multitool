using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Covers <see cref="NifSceneGraphWalker.CollectSkinBoneNodeIndices" />: the set of node indices that
///     act as skinning bones for any skinned shape. Geometry hung directly off those nodes is a rig
///     helper / physics proxy (e.g. the per-bone boxes on FNV animated flags) that the worldspace
///     reference render path drops. The skeleton-root node is included too — except block 0 (the Scene
///     Root), where legitimate static geometry lives and must never be dropped.
/// </summary>
public sealed class NifSceneGraphWalkerSkinBoneTests
{
    [Fact]
    public void CollectSkinBoneNodeIndices_GathersBoneRefs_AndNonZeroSkeletonRoot()
    {
        // Skin instance at block 1: data=2, skinPartition=3, skeletonRoot=4, bones=[5,6,7]. Bone/root
        // refs must be valid block indices (production filters out-of-range refs), so pad to 12 blocks.
        var (data, nif) = BuildNif(
        [
            ("NiNode", new byte[4]), // 0 = scene root (unused here)
            SkinInstance(2, 3, 4, [5, 6, 7]), // 1
            .. PadNodes(10)
        ]); // 2..11

        var bones = NifSceneGraphWalker.CollectSkinBoneNodeIndices(
            data, nif, new Dictionary<int, int> { [99] = 1 }); // shape 99 → skin instance block 1

        Assert.Equal([4, 5, 6, 7], bones.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void CollectSkinBoneNodeIndices_ExcludesSceneRootSkeletonRoot()
    {
        // skeletonRoot = 0 (the Scene Root) must NOT be collected — static geometry hangs there.
        var (data, nif) = BuildNif(
        [
            ("NiNode", new byte[4]),
            SkinInstance(2, 3, 0, [5, 6]),
            .. PadNodes(10)
        ]);

        var bones = NifSceneGraphWalker.CollectSkinBoneNodeIndices(
            data, nif, new Dictionary<int, int> { [99] = 1 });

        Assert.Equal([5, 6], bones.OrderBy(x => x).ToArray());
        Assert.DoesNotContain(0, bones);
    }

    [Fact]
    public void CollectSkinBoneNodeIndices_NoSkinnedShapes_ReturnsEmpty()
    {
        var (data, nif) = BuildNif(("NiNode", new byte[4]));
        Assert.Empty(NifSceneGraphWalker.CollectSkinBoneNodeIndices(data, nif, new Dictionary<int, int>()));
    }

    [Fact]
    public void CollectSkinBoneNodeIndices_DedupesSharedBonesAcrossTwoSkins()
    {
        // Two skinned shapes reference two skin instances with overlapping bones — bone set is a union.
        var (data, nif) = BuildNif(
        [
            ("NiNode", new byte[4]), // 0
            SkinInstance(4, 5, 6, [7, 8]), // 1
            SkinInstance(4, 5, 6, [8, 9]), // 2
            .. PadNodes(10)
        ]); // 3..12

        var bones = NifSceneGraphWalker.CollectSkinBoneNodeIndices(
            data, nif, new Dictionary<int, int> { [40] = 1, [41] = 2 });

        Assert.Equal([6, 7, 8, 9], bones.OrderBy(x => x).ToArray());
    }

    // ---- buffer builders --------------------------------------------------------------------

    private static (byte[] data, NifInfo nif) BuildNif(params (string type, byte[] payload)[] blocks)
    {
        // The SkinInstance fixtures encode the modern layout (Skin Partition ref present, since
        // 10.1.0.101) — declare the FNV-era version so the version-gated parser picks it.
        var nif = new NifInfo { IsBigEndian = false, BlockCount = blocks.Length, BinaryVersion = 0x14020007 };
        using var ms = new MemoryStream();
        var offsets = new int[blocks.Length];
        for (var i = 0; i < blocks.Length; i++)
        {
            offsets[i] = (int)ms.Length;
            ms.Write(blocks[i].payload);
        }

        var data = ms.ToArray();
        for (var i = 0; i < blocks.Length; i++)
        {
            nif.Blocks.Add(new BlockInfo
            {
                Index = i,
                TypeName = blocks[i].type,
                DataOffset = offsets[i],
                Size = blocks[i].payload.Length
            });
        }

        return (data, nif);
    }

    private static (string type, byte[] payload)[] PadNodes(int count)
    {
        return Enumerable.Range(0, count).Select(_ => ("NiNode", new byte[4])).ToArray();
    }

    private static (string, byte[]) SkinInstance(int dataRef, int skinPartition, int skeletonRoot, int[] bones)
    {
        // NiSkinInstance: Data@0, SkinPartition@4, SkeletonRoot@8, NumBones@12, Bones[]@16.
        var b = new byte[16 + bones.Length * 4];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0), dataRef);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), skinPartition);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), skeletonRoot);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)bones.Length);
        for (var i = 0; i < bones.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16 + i * 4), bones[i]);
        }

        return ("NiSkinInstance", b);
    }
}

