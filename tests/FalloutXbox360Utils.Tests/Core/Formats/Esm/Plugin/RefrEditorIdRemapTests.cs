using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Tests for the EditorID-stem rename-rescue predicate. Exercises the static
///     <see cref="PluginBuilder.TryFindMasterBaseByEditorIdStem(System.Collections.Generic.Dictionary{string, System.Collections.Generic.Dictionary{string, System.Collections.Generic.List{uint}}}, string, string, out bool, out System.Collections.Generic.List{uint})"/>
///     overload directly so we don't need to spin up a full PluginBuilder run.
/// </summary>
public class RefrEditorIdRemapTests
{
    [Fact]
    public void UniqueStemMatch_ReturnsMasterFormId()
    {
        var lookup = new Dictionary<string, Dictionary<string, List<uint>>>(StringComparer.Ordinal)
        {
            ["SCOL"] = new(StringComparer.Ordinal)
            {
                ["scolparkinglotchunk03"] = [0xCAFEBABEu]
            }
        };

        var result = PluginBuilder.TryFindMasterBaseByEditorIdStem(
            lookup, "SCOLParkingLotChunk03", "SCOL", out var ambiguous, out var candidates);

        Assert.Equal(0xCAFEBABEu, result);
        Assert.False(ambiguous);
        Assert.Null(candidates);
    }

    [Fact]
    public void AmbiguousStemMatch_SetsAmbiguousAndReturnsNull()
    {
        var lookup = new Dictionary<string, Dictionary<string, List<uint>>>(StringComparer.Ordinal)
        {
            ["SCOL"] = new(StringComparer.Ordinal)
            {
                ["scolparkinglotchunk03"] = [0xAAA1u, 0xAAA2u, 0xAAA3u]
            }
        };

        var result = PluginBuilder.TryFindMasterBaseByEditorIdStem(
            lookup, "SCOLParkingLotChunk03", "SCOL", out var ambiguous, out var candidates);

        Assert.Null(result);
        Assert.True(ambiguous);
        Assert.NotNull(candidates);
        Assert.Equal(3, candidates!.Count);
    }

    [Fact]
    public void NoStemMatch_ReturnsNullWithoutAmbiguity()
    {
        var lookup = new Dictionary<string, Dictionary<string, List<uint>>>(StringComparer.Ordinal)
        {
            ["SCOL"] = new(StringComparer.Ordinal)
            {
                ["lucky38base"] = [0xBABE0001u]
            }
        };

        var result = PluginBuilder.TryFindMasterBaseByEditorIdStem(
            lookup, "MyRandomThing03", "SCOL", out var ambiguous, out _);

        Assert.Null(result);
        Assert.False(ambiguous);
    }

    [Fact]
    public void DifferentBaseType_DoesNotCrossMatch()
    {
        // Master STAT has a record whose stem matches, but the prototype base type is SCOL.
        // The lookup is per-type so the SCOL-side lookup yields no hit and we return null.
        var lookup = new Dictionary<string, Dictionary<string, List<uint>>>(StringComparer.Ordinal)
        {
            ["STAT"] = new(StringComparer.Ordinal)
            {
                ["scolparkinglotchunk03"] = [0x1111u]
            }
        };

        var result = PluginBuilder.TryFindMasterBaseByEditorIdStem(
            lookup, "SCOLParkingLotChunk03", "SCOL", out var ambiguous, out _);

        Assert.Null(result);
        Assert.False(ambiguous);
    }

    [Fact]
    public void IneligibleBaseType_ReturnsNull()
    {
        // CELL is not in the REFR-base-eligible allowlist — even if the lookup happened to
        // contain a CELL stem entry, the predicate refuses the remap.
        var lookup = new Dictionary<string, Dictionary<string, List<uint>>>(StringComparer.Ordinal)
        {
            ["CELL"] = new(StringComparer.Ordinal)
            {
                ["mystem"] = [0xDDDDu]
            }
        };

        var result = PluginBuilder.TryFindMasterBaseByEditorIdStem(
            lookup, "MyStem03", "CELL", out var ambiguous, out _);

        Assert.Null(result);
        Assert.False(ambiguous);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmptyPrototypeEditorId_ReturnsNull(string? editorId)
    {
        var lookup = new Dictionary<string, Dictionary<string, List<uint>>>(StringComparer.Ordinal)
        {
            ["SCOL"] = new(StringComparer.Ordinal) { ["foo"] = [0x123u] }
        };

        var result = PluginBuilder.TryFindMasterBaseByEditorIdStem(
            lookup, editorId, "SCOL", out var ambiguous, out _);

        Assert.Null(result);
        Assert.False(ambiguous);
    }

    [Fact]
    public void NvRenameSuffix_NormalizesAndMatches()
    {
        var lookup = new Dictionary<string, Dictionary<string, List<uint>>>(StringComparer.Ordinal)
        {
            ["STAT"] = new(StringComparer.Ordinal)
            {
                ["monorailplatform"] = [0xAAAAu]
            }
        };

        var result = PluginBuilder.TryFindMasterBaseByEditorIdStem(
            lookup, "MonorailPlatform_NV", "STAT", out _, out _);

        Assert.Equal(0xAAAAu, result);
    }
}
