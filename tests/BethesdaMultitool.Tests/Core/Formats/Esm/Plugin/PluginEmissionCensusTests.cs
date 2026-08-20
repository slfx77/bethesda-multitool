using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Validation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     The TES4 HEDR record count and the run's emitted stats are derived from the assembled
///     plugin bytes, not from per-write-site counters — those drift whenever a later pass
///     discards records (cell gates clear buckets after their children encode) or an encoder
///     declines an override, which left the shipped HEDR ~35% low. These tests pin the
///     retail counting contract: records + GRUP headers, excluding TES4.
/// </summary>
public class PluginEmissionCensusTests
{
    [Fact]
    public void Census_Counts_Records_And_Grups_Excluding_Tes4()
    {
        var plugin = BuildPlugin(
            Tes4(0),
            Grup("STAT",
                Record("STAT", 0x01000800),
                Record("STAT", 0x000A0001)),
            Grup("WEAP",
                Record("WEAP", 0x01000801)));

        var census = InvokeCount(plugin);

        Assert.Equal(3, census.Records);
        Assert.Equal(2, census.Groups);
        Assert.Equal(5, census.HedrRecordCount); // retail contract: records + groups
    }

    [Fact]
    public void Census_Splits_New_From_Override_By_Load_Order_Byte()
    {
        var plugin = BuildPlugin(
            Tes4(0),
            Grup("STAT",
                Record("STAT", 0x01000800), // plugin range → new
                Record("STAT", 0x01000801), // plugin range → new
                Record("STAT", 0x000A0001))); // master range → override

        var census = InvokeCount(plugin);

        Assert.Equal(2, census.NewRecords);
        Assert.Equal(1, census.OverrideRecords);
        Assert.Equal(3, census.ByType["STAT"]);
    }

    [Fact]
    public void Census_Counts_Nested_Grups_At_Every_Depth()
    {
        // Cell hierarchy shape: top-level GRUP → block → sub-block → cell children.
        var plugin = BuildPlugin(
            Tes4(0),
            Grup("CELL",
                Grup("CELL",
                    Record("CELL", 0x000A0010),
                    Grup("CELL",
                        Record("REFR", 0x01000900)))));

        var census = InvokeCount(plugin);

        Assert.Equal(2, census.Records);
        Assert.Equal(3, census.Groups);
        Assert.Equal(5, census.HedrRecordCount);
    }

    [Fact]
    public void Validator_Flags_A_Hedr_Count_That_Disagrees_With_The_File()
    {
        var plugin = BuildPlugin(
            Tes4(999),
            Grup("STAT", Record("STAT", 0x01000800)));

        var result = PluginSemanticValidator.Validate(plugin);

        Assert.Contains("TES4 HEDR record count is 999", result.Report);
        Assert.True(result.WarningCount > 0);
    }

    [Fact]
    public void Validator_Accepts_A_Hedr_Count_Matching_The_File()
    {
        // 1 record + 1 GRUP = 2.
        var plugin = BuildPlugin(
            Tes4(2),
            Grup("STAT", Record("STAT", 0x01000800)));

        var result = PluginSemanticValidator.Validate(plugin);

        Assert.DoesNotContain("HEDR record count", result.Report);
    }

    // --- byte builders -------------------------------------------------------------

    /// <summary>Invokes the internal census through the same assembly the tests already see.</summary>
    private static (int Records, int Groups, int NewRecords, int OverrideRecords,
        IReadOnlyDictionary<string, int> ByType, int HedrRecordCount) InvokeCount(byte[] plugin)
    {
        var census = PluginEmissionCensus.Count(plugin);
        return (census.Records, census.Groups, census.NewRecords, census.OverrideRecords,
            census.ByType, census.HedrRecordCount);
    }

    private static byte[] BuildPlugin(params byte[][] parts)
    {
        using var stream = new MemoryStream();
        foreach (var part in parts)
        {
            stream.Write(part);
        }

        return stream.ToArray();
    }

    private static byte[] Tes4(uint hedrRecordCount)
    {
        var hedr = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(hedr.AsSpan(0, 4), 1.34f);
        BinaryPrimitives.WriteUInt32LittleEndian(hedr.AsSpan(4, 4), hedrRecordCount);
        BinaryPrimitives.WriteUInt32LittleEndian(hedr.AsSpan(8, 4), 0x800u);
        return Record("TES4", 0, ("HEDR", hedr));
    }

    private static byte[] Record(string signature, uint formId, params (string Sig, byte[] Data)[] subs)
    {
        var dataSize = subs.Sum(s => 6 + s.Data.Length);
        var bytes = new byte[24 + dataSize];
        Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), formId);

        var pos = 24;
        foreach (var (sig, data) in subs)
        {
            Encoding.ASCII.GetBytes(sig).CopyTo(bytes, pos);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(pos + 4, 2), (ushort)data.Length);
            data.CopyTo(bytes, pos + 6);
            pos += 6 + data.Length;
        }

        return bytes;
    }

    private static byte[] Grup(string label, params byte[][] contents)
    {
        var body = contents.Sum(c => c.Length);
        var header = new byte[24];
        Encoding.ASCII.GetBytes("GRUP").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), (uint)(24 + body));
        Encoding.ASCII.GetBytes(label).CopyTo(header, 8);

        using var stream = new MemoryStream();
        stream.Write(header);
        foreach (var c in contents)
        {
            stream.Write(c);
        }

        return stream.ToArray();
    }
}