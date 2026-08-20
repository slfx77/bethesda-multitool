using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     TREE parser coverage. Pins two PDB-derived decode facts (CNAM's field at +20 is the
///     SIGNED INT32 <c>OBJ_TREE.iCanopyShadowRadius</c>, not a float; SNAM is a packed uint32
///     seed array) and the exact-length guards: this handler is game-agnostic and Skyrim's
///     TREE CNAM is a 48-byte struct with no BNAM, which must be skipped rather than
///     silently mis-decoded as FNV's 32-byte OBJ_TREE.
/// </summary>
public class TreeParserCoverageTests
{
    private const uint TreeFormId = 0x0003C356;

    [Fact]
    public void ParseTrees_LittleEndian_DecodesAllSubrecords()
    {
        var bytes = BuildTree(false,
            ("EDID", NullTermString("WhiteOak01")),
            ("MODL", NullTermString(@"\WhiteOak01.spt")),
            ("ICON", NullTermString(@"Landscape\Trees\WhiteOakBillboard.dds")),
            ("SNAM", SeedBytes(false, 301409, 363767)),
            ("CNAM", CnamBytes(false, 512)),
            ("BNAM", BnamBytes(false, 1521.7f, 821.3f)));

        var tree = ParseSingle(bytes, false);

        Assert.Equal("WhiteOak01", tree.EditorId);
        Assert.Equal(@"\WhiteOak01.spt", tree.ModelPath);
        Assert.Equal([301409u, 363767u], tree.Seeds);
        Assert.NotNull(tree.Data);
        Assert.Equal(512, tree.Data!.ShadowRadius);
        Assert.Equal(2.5f, tree.Data.LeafCurvature);
        Assert.Equal(1.0f, tree.Data.RustleSpeed);
        Assert.NotNull(tree.BillboardSize);
        Assert.Equal(1521.7f, tree.BillboardSize!.Width);
        Assert.Equal(821.3f, tree.BillboardSize.Height);
    }

    [Fact]
    public void ParseTrees_BigEndian_DecodesXboxFormat()
    {
        var bytes = BuildTree(true,
            ("EDID", NullTermString("OasisElm01")),
            ("SNAM", SeedBytes(true, 844198)),
            ("CNAM", CnamBytes(true, 128)),
            ("BNAM", BnamBytes(true, 100f, 200f)));

        var tree = ParseSingle(bytes, true);

        Assert.True(tree.IsBigEndian);
        Assert.Equal([844198u], tree.Seeds);
        // The int32-not-float fact: as a float these bytes would be a ~1.8e-43 denormal.
        Assert.Equal(128, tree.Data!.ShadowRadius);
        Assert.Equal(100f, tree.BillboardSize!.Width);
    }

    [Fact]
    public void ParseTrees_SkyrimSized48ByteCnam_IsSkippedNotMisdecoded()
    {
        var bytes = BuildTree(false,
            ("EDID", NullTermString("SkyrimTree")),
            ("CNAM", new byte[48]));

        var tree = ParseSingle(bytes, false);

        Assert.Null(tree.Data);
    }

    [Fact]
    public void ParseTrees_OversizedBnam_IsSkipped()
    {
        var bytes = BuildTree(false,
            ("EDID", NullTermString("BadBillboard")),
            ("BNAM", new byte[12]));

        var tree = ParseSingle(bytes, false);

        Assert.Null(tree.BillboardSize);
    }

    [Theory]
    [InlineData(0)] // empty
    [InlineData(6)] // truncated tail — not a multiple of 4
    public void ParseTrees_MalformedSnamLength_IsSkipped(int snamLength)
    {
        var bytes = BuildTree(false,
            ("EDID", NullTermString("BadSeeds")),
            ("SNAM", new byte[snamLength]));

        var tree = ParseSingle(bytes, false);

        Assert.Null(tree.Seeds);
    }

    private static TreeRecord ParseSingle(
        byte[] recordBytes, bool bigEndian)
    {
        var mainRecord = new DetectedMainRecord(
            "TREE", (uint)(recordBytes.Length - 24), 0, TreeFormId, 0, bigEndian);
        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        return Assert.Single(parser.ParseTrees());
    }

    private static byte[] BuildTree(bool bigEndian, params (string sig, byte[] data)[] subrecords)
    {
        return BuildRecordBytes(TreeFormId, "TREE", bigEndian, subrecords);
    }

    private static byte[] SeedBytes(bool bigEndian, params uint[] seeds)
    {
        var bytes = new byte[seeds.Length * 4];
        for (var i = 0; i < seeds.Length; i++)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4, 4), seeds[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), seeds[i]);
            }
        }

        return bytes;
    }

    /// <summary>OBJ_TREE (32 B): 5 floats, int32 shadow radius at +20, 2 floats.</summary>
    private static byte[] CnamBytes(bool bigEndian, int shadowRadius)
    {
        var bytes = new byte[32];
        WriteFloat(bytes, 0, 2.5f, bigEndian); // LeafCurvature
        WriteFloat(bytes, 4, 5.0f, bigEndian); // MinLeafAngle
        WriteFloat(bytes, 8, 85.0f, bigEndian); // MaxLeafAngle
        WriteFloat(bytes, 12, 0.2f, bigEndian); // BranchDimmingValue
        WriteFloat(bytes, 16, 0.2f, bigEndian); // LeafDimmingValue
        if (bigEndian)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), shadowRadius);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20, 4), shadowRadius);
        }

        WriteFloat(bytes, 24, 1.0f, bigEndian); // RockSpeed
        WriteFloat(bytes, 28, 1.0f, bigEndian); // RustleSpeed
        return bytes;
    }

    private static byte[] BnamBytes(bool bigEndian, float width, float height)
    {
        var bytes = new byte[8];
        WriteFloat(bytes, 0, width, bigEndian);
        WriteFloat(bytes, 4, height, bigEndian);
        return bytes;
    }

    private static void WriteFloat(byte[] dest, int offset, float value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(dest.AsSpan(offset, 4), value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(dest.AsSpan(offset, 4), value);
        }
    }
}