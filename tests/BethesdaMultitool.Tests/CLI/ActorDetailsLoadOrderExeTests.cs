using System.Buffers.Binary;
using System.Text.Json;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>Exercises master-aware actor inspection through the executable's real loader and JSON runtime.</summary>
public sealed class ActorDetailsLoadOrderExeTests
{
    [Fact]
    public async Task MissingMasterIsExplicit_AndSupplyingItResolvesTheTemplate()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var master = Path.Combine(directory.Path, "FalloutNV.esm");
        var dlc = Path.Combine(directory.Path, "OldWorldBlues.esm");
        File.WriteAllBytes(master, new EsmTestFileBuilder()
            .AddTopLevelGrup("CREA", Creature(0x00001000, "BaseRat", 75, 0)).Build());
        File.WriteAllBytes(dlc, new EsmTestFileBuilder().WithMasters("FalloutNV.esm")
            .AddTopLevelGrup("CREA", Creature(0x01001000, "ChildRat", 999, 2, 0x00001000)).Build());
        var cancellation = TestContext.Current.CancellationToken;
        var single = await CliExeRunner.RunAsync(
            ["esm", "actor-details", dlc, "0x01001000"], cancellation);
        Assert.True(single.ExitCode == 0, single.Describe());
        using (var doc = JsonDocument.Parse(single.StandardOutput))
        {
            var group = Assert.Single(doc.RootElement.GetProperty("templateGroups").EnumerateArray(),
                g => g.GetProperty("group").GetString() == "UseStats");
            Assert.Equal("MasterNotLoaded", group.GetProperty("status").GetString());
        }
        var merged = await CliExeRunner.RunAsync(
            ["esm", "actor-details", dlc, "OldWorldBlues.esm:0x01001000", "--load-order", master], cancellation);
        Assert.True(merged.ExitCode == 0, merged.Describe());
        using var resolved = JsonDocument.Parse(merged.StandardOutput);
        var health = Assert.Single(resolved.RootElement.GetProperty("effectiveStatistics").EnumerateArray(),
            s => s.GetProperty("key").GetString() == "Health");
        Assert.Equal("75", health.GetProperty("value").GetString());
        Assert.Equal("Inherited", health.GetProperty("provenance").GetString());
        Assert.Equal("FalloutNV.esm", health.GetProperty("sourcePlugin").GetString());
        Assert.True(resolved.RootElement.TryGetProperty("actorProvenance", out _));
        Assert.Equal(2, resolved.RootElement.GetProperty("loadOrder").GetArrayLength());
    }

    private static byte[] Creature(uint id, string editorId, int health, ushort templateFlags, uint? template = null)
    {
        var acbs = new byte[24];
        BinaryPrimitives.WriteInt16LittleEndian(acbs.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(acbs.AsSpan(22), templateFlags);
        var data = new byte[17];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), health);
        var parts = new List<(string, byte[])> { ("EDID", NullTermString(editorId)), ("ACBS", acbs), ("DATA", data) };
        if (template is { } parent)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, parent);
            parts.Add(("TPLT", bytes));
        }
        return BuildRecordBytes(id, "CREA", false, parts.ToArray());
    }
}
