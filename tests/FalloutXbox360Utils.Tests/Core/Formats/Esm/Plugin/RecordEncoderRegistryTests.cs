using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class RecordEncoderRegistryTests
{
    [Fact]
    public void CreateDefault_RegistersSimpleTopLevelTypes()
    {
        var registry = RecordEncoderRegistry.CreateDefault();

        var supported = registry.SupportedRecordTypes;

        // Spot-check across record-type families.
        var expected = new[]
        {
            "GMST", "GLOB", "FLST",                 // Misc
            "WEAP", "ARMO", "AMMO", "ALCH", "BOOK", "MISC", "KEYM", "CONT",   // Item
            "SPEL", "ENCH", "MGEF", "PERK",         // Magic
            "NPC_", "CREA", "RACE", "FACT",         // Character
            "QUST", "DIAL", "INFO", "SCPT", "MESG", // Quest / Dialogue
            "WRLD", "STAT", "DOOR", "LIGH",         // World
        };
        foreach (var type in expected)
        {
            Assert.Contains(type, supported);
        }
    }

    [Fact]
    public void CreateDefault_RegistersPlacedRefEncoders()
    {
        var registry = RecordEncoderRegistry.CreateDefault();
        var supported = registry.SupportedRecordTypes;

        Assert.Contains("REFR", supported);
        Assert.Contains("ACHR", supported);
        Assert.Contains("ACRE", supported);
    }

    [Fact]
    public void CreateDefault_RegistersCellEncoder()
    {
        var registry = RecordEncoderRegistry.CreateDefault();

        Assert.Contains("CELL", registry.SupportedRecordTypes);
    }

    [Fact]
    public void CreateDefault_RegistersLeveledListUnderAllThreeSignatures()
    {
        var registry = RecordEncoderRegistry.CreateDefault();

        Assert.True(registry.TryGet("LVLI", out var lvli));
        Assert.True(registry.TryGet("LVLN", out var lvln));
        Assert.True(registry.TryGet("LVLC", out var lvlc));

        Assert.NotNull(lvli);
        Assert.Same(lvli, lvln);
        Assert.Same(lvli, lvlc);
    }

    [Fact]
    public void CreateDefault_RegistersSurvivalStageUnderAllFourSignatures()
    {
        var registry = RecordEncoderRegistry.CreateDefault();

        Assert.True(registry.TryGet("RADS", out var rads));
        Assert.True(registry.TryGet("DEHY", out var dehy));
        Assert.True(registry.TryGet("HUNG", out var hung));
        Assert.True(registry.TryGet("SLPD", out var slpd));

        Assert.NotNull(rads);
        Assert.Same(rads, dehy);
        Assert.Same(rads, hung);
        Assert.Same(rads, slpd);
    }

    [Fact]
    public void Register_WithExplicitKey_OverridesEncoderRecordType()
    {
        var registry = new RecordEncoderRegistry();
        var encoder = new LvliEncoder(); // RecordType == "LVLI"
        registry.Register("LVLN", encoder);

        Assert.True(registry.TryGet("LVLN", out var found));
        Assert.Same(encoder, found);
        Assert.False(registry.TryGet("LVLI", out _));
    }

    [Fact]
    public void IsCellChildRecordType_ReturnsTrueForPlacedRefTypes()
    {
        Assert.True(RecordEncoderRegistry.IsCellChildRecordType("REFR"));
        Assert.True(RecordEncoderRegistry.IsCellChildRecordType("ACHR"));
        Assert.True(RecordEncoderRegistry.IsCellChildRecordType("ACRE"));
        Assert.False(RecordEncoderRegistry.IsCellChildRecordType("WEAP"));
        Assert.False(RecordEncoderRegistry.IsCellChildRecordType("CELL"));
    }

    [Fact]
    public void IsCellRecordType_ReturnsTrueForCellOnly()
    {
        Assert.True(RecordEncoderRegistry.IsCellRecordType("CELL"));
        Assert.False(RecordEncoderRegistry.IsCellRecordType("REFR"));
        Assert.False(RecordEncoderRegistry.IsCellRecordType("WEAP"));
    }
}
