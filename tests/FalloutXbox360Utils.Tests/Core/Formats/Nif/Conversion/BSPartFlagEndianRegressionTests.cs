using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Conversion;

/// <summary>
///     Regression for the Ulysses-prototype gore-cap visibility bug. Xbox 360 NIF tools
///     wrote <c>BSPartFlag</c> (the <c>PartFlag</c> field inside
///     <c>BSDismemberSkinInstance.Partitions</c>) as a native byte pair, not as a big-endian
///     ushort. The default per-field endian swap inverted the bit positions of
///     <c>PF_EDITOR_VISIBLE</c> (bit 0) and <c>PF_START_NET_BONESET</c> (bit 8) — so converted
///     prototype NPCs had body limb partitions with the visible flag stripped and gore caps
///     with the visible flag *added*. The fix is to opt <c>BSPartFlag</c> out of the swap in
///     <see cref="NifSchemaConverter" />; these tests pin that opt-out down by reading the
///     post-conversion partition bytes from a real prototype NIF and asserting they match
///     PC vanilla semantics (limbs visible, gore caps not).
/// </summary>
public sealed class BSPartFlagEndianRegressionTests
{
    private const string UlyssesProtoNifPath =
        @"Sample\Meshes\meshes_360_proto\meshes\armor\ulysses\ulysses.nif";

    [Fact]
    public void Convert_PrototypeOutfitBody_KeepsPfEditorVisibleOnLimbPartitions()
    {
        var nifBytes = LoadSamplePrototypeNif();
        if (nifBytes is null)
        {
            return;
        }

        var (info, converted) = ConvertAndReparseAsPc(nifBytes);

        var bodyDismember = info.Blocks.First(b => b.TypeName == "BSDismemberSkinInstance");
        var partitions = ReadPartitions(converted, bodyDismember.DataOffset);

        // Partition[0] is the torso shape itself — it carries both PF_EDITOR_VISIBLE (bit 0,
        // value 0x0001) and PF_START_NET_BONESET (bit 8, value 0x0100). The sibling limb
        // partitions are body parts 7000/3000/3/5000/5 (arms/hands/legs) and must keep the
        // visible flag or the engine culls them.
        Assert.True(partitions.Count >= 2, "Expected at least 2 partitions in body BSDismemberSkinInstance");
        Assert.Equal(0x0101, partitions[0].PartFlag);
        Assert.Equal(0x0001, partitions[1].PartFlag);
    }

    [Fact]
    public void Convert_PrototypeOutfitGoreCap_LeavesPfEditorVisibleClearedOnGoreCapPartitions()
    {
        var nifBytes = LoadSamplePrototypeNif();
        if (nifBytes is null)
        {
            return;
        }

        var (info, converted) = ConvertAndReparseAsPc(nifBytes);

        // Gore-cap BSDismemberSkinInstance blocks live after the body block; identify them
        // by their first partition's BodyPart id sitting in the gore-cap range (Bethesda
        // uses 200+ for the dismemberment cap IDs visible in NifSkope's dropdown).
        var goreCapDismember = info.Blocks
            .Where(b => b.TypeName == "BSDismemberSkinInstance")
            .Select(b => new { Block = b, Partitions = ReadPartitions(converted, b.DataOffset) })
            .First(x => x.Partitions.Count > 0 && x.Partitions[0].BodyPart >= 200);

        // Gore caps must NOT have PF_EDITOR_VISIBLE — they only show when the limb they
        // back is severed. Partition[0] only carries PF_START_NET_BONESET (0x0100).
        Assert.Equal(0x0100, goreCapDismember.Partitions[0].PartFlag);

        // Subsequent partitions of the same gore-cap share the boneset and have neither
        // flag set (0x0000). At least one such partition must exist or the cap would have
        // collapsed to a single boneset and we're reading the wrong block.
        Assert.True(goreCapDismember.Partitions.Count >= 2,
            "Gore-cap dismember block should have multiple partitions sharing the boneset");
        Assert.Equal(0x0000, goreCapDismember.Partitions[1].PartFlag);
    }

    private static byte[]? LoadSamplePrototypeNif()
    {
        var path = SampleFileFixture.FindSamplePath(UlyssesProtoNifPath);
        Assert.SkipWhen(path is null, $"Sample NIF not available: {UlyssesProtoNifPath}");
        return File.ReadAllBytes(path!);
    }

    private static (NifInfo Info, byte[] Bytes) ConvertAndReparseAsPc(byte[] xboxBytes)
    {
        var result = NifConverter.Convert(xboxBytes);
        Assert.True(result.Success, $"NifConverter failed: {result.ErrorMessage}");
        Assert.NotNull(result.OutputData);
        var info = NifParser.Parse(result.OutputData!);
        Assert.NotNull(info);
        Assert.False(info!.IsBigEndian, "Converted NIF must be little-endian");
        return (info, result.OutputData!);
    }

    /// <summary>
    ///     Read the <c>Partitions</c> array (numPartitions u32 + N × (PartFlag u16 + BodyPart u16))
    ///     out of a BSDismemberSkinInstance whose block-data starts at <paramref name="offset" />.
    ///     The block layout (from nif.xml inheritance): Data ref u32, SkinPartition ref u32,
    ///     SkeletonRoot ptr u32, NumBones u32, Bones[NumBones] u32, NumPartitions u32,
    ///     Partitions[NumPartitions]. We walk the header to find Partitions rather than
    ///     hardcoding offsets so the test stays robust to different bone counts.
    /// </summary>
    private static List<(ushort PartFlag, ushort BodyPart)> ReadPartitions(byte[] data, int offset)
    {
        var pos = offset + 12; // skip Data + SkinPartition + SkeletonRoot
        var numBones = BitConverter.ToInt32(data, pos);
        pos += 4 + numBones * 4;
        var numPartitions = BitConverter.ToInt32(data, pos);
        pos += 4;

        var partitions = new List<(ushort, ushort)>(numPartitions);
        for (var i = 0; i < numPartitions; i++)
        {
            var partFlag = BitConverter.ToUInt16(data, pos);
            var bodyPart = BitConverter.ToUInt16(data, pos + 2);
            partitions.Add((partFlag, bodyPart));
            pos += 4;
        }

        return partitions;
    }
}
