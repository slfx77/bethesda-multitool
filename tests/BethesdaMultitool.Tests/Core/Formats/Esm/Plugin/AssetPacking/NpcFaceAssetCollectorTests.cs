using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.AssetPacking;

public sealed class NpcFaceAssetCollectorTests
{
    [Fact]
    public void Collect_MasterOverlays_RebasesMaleAndFemaleSidecarsToOutputPlugin()
    {
        var records = new RecordCollection
        {
            Npcs =
            [
                new NpcRecord { FormId = 0x00104F09, Stats = Stats(0) },
                new NpcRecord { FormId = 0x00104E84, Stats = Stats(1) }
            ]
        };

        var result = NpcFaceAssetCollector.Collect(records, new Dictionary<uint, uint>(), "xex21.v124.esm");

        Assert.Equal(4, result.SourcePaths.Count);
        Assert.Equal(
            "textures\\characters\\facemods\\xex21.v124.esm\\00104f09_0.dds",
            result.PackPathRenames[
                "textures\\characters\\facemods\\falloutnv.esm\\00104f09_0.dds"]);
        Assert.Equal(
            "textures\\characters\\bodymods\\xex21.v124.esm\\00104f09modbodymale.dds",
            result.PackPathRenames[
                "textures\\characters\\bodymods\\falloutnv.esm\\00104f09modbodymale.dds"]);
        Assert.Equal(
            "textures\\characters\\bodymods\\xex21.v124.esm\\00104e84modbodyfemale.dds",
            result.PackPathRenames[
                "textures\\characters\\bodymods\\falloutnv.esm\\00104e84modbodyfemale.dds"]);
    }

    [Fact]
    public void Collect_AllocatedNpc_UsesSourceIdentityAndTargetLocalFormId()
    {
        var records = new RecordCollection
        {
            Npcs = [new NpcRecord { FormId = 0x010030AB, Stats = Stats(0) }]
        };
        var aliases = new Dictionary<uint, uint> { [0x00125126] = 0x010030AB };

        var result = NpcFaceAssetCollector.Collect(records, aliases, "Example.ESM");

        Assert.Equal(
            "textures\\characters\\facemods\\example.esm\\000030ab_0.dds",
            result.PackPathRenames[
                "textures\\characters\\facemods\\falloutnv.esm\\00125126_0.dds"]);
        Assert.Equal(
            "textures\\characters\\bodymods\\example.esm\\000030abmodbodymale.dds",
            result.PackPathRenames[
                "textures\\characters\\bodymods\\falloutnv.esm\\00125126modbodymale.dds"]);
    }

    [Fact]
    public void Collect_ZeroFormId_IsIgnored()
    {
        var records = new RecordCollection { Npcs = [new NpcRecord()] };

        var result = NpcFaceAssetCollector.Collect(records, new Dictionary<uint, uint>(), "output.esm");

        Assert.Empty(result.SourcePaths);
        Assert.Empty(result.PackPathRenames);
    }

    private static ActorBaseSubrecord Stats(uint flags)
    {
        return new ActorBaseSubrecord(flags, 0, 0, 1, 0, 0, 100, 0, 0, 0, 0, false);
    }
}