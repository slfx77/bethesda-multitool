using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class RecordEncoderRegistryTests
{
    [Fact]
    public void CreateV1Default_RegistersAllExpectedTypes()
    {
        var registry = RecordEncoderRegistry.CreateV1Default();

        var supported = registry.SupportedRecordTypes;

        // CONT is intentionally not in the registry — see RecordEncoderRegistry.CreateV1Default
        // for the rationale (ContainerRecord lacks the Weight field).
        var expected = new[] { "GMST", "GLOB", "WEAP", "ARMO", "AMMO", "ALCH", "BOOK", "MISC", "KEYM", "FACT", "NPC_" };
        foreach (var type in expected)
        {
            Assert.Contains(type, supported);
        }

        Assert.DoesNotContain("CONT", supported);
        Assert.DoesNotContain("REFR", supported);
        Assert.Equal(expected.Length, supported.Count);
    }

    [Fact]
    public void CreateV2Default_AddsPlacedRefEncoders()
    {
        var registry = RecordEncoderRegistry.CreateV2Default();
        var supported = registry.SupportedRecordTypes;

        Assert.Contains("REFR", supported);
        Assert.Contains("ACHR", supported);
        Assert.Contains("ACRE", supported);

        // v1 types still registered.
        Assert.Contains("WEAP", supported);
        Assert.Contains("NPC_", supported);
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
    public void CreateV3Default_AddsCellEncoder()
    {
        var registry = RecordEncoderRegistry.CreateV3Default();

        Assert.Contains("CELL", registry.SupportedRecordTypes);
        // Sanity: v2 encoders still present.
        Assert.Contains("REFR", registry.SupportedRecordTypes);
        Assert.Contains("WEAP", registry.SupportedRecordTypes);
    }

    [Fact]
    public void IsCellRecordType_ReturnsTrueForCellOnly()
    {
        Assert.True(RecordEncoderRegistry.IsCellRecordType("CELL"));
        Assert.False(RecordEncoderRegistry.IsCellRecordType("REFR"));
        Assert.False(RecordEncoderRegistry.IsCellRecordType("WEAP"));
    }
}
