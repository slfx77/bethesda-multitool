using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Phase A regression: SCOL parser handles every subrecord signature that appears
///     in vanilla FNV SCOLs. Empirical scan of `Sample/ESM/pc_final/FalloutNV.esm`
///     enumerated exactly: EDID, OBND, MODL, MODT, ONAM, DATA (98 records). Any new
///     signature surfacing in real data (or in a mod) will trigger a debug warning
///     in MiscStaticObjectHandler.ParseScolFromAccessor; this test pins the
///     known-good set so coverage regressions are caught in CI.
/// </summary>
public class ScolParserCoverageTests
{
    [Fact]
    public void ParseStaticCollections_HandlesAllVanillaSubrecordSignatures_LittleEndian()
    {
        var scolBytes = BuildSyntheticScolLE();

        var mainRecord = new DetectedMainRecord(
            "SCOL", (uint)(scolBytes.Length - 24), 0, 0x00050100, 0, false);
        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, scolBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, scolBytes.Length);
        accessor.WriteArray(0, scolBytes, 0, scolBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: scolBytes.Length);
        var scols = parser.ParseStaticCollections();

        var scol = Assert.Single(scols);
        Assert.Equal(0x00050100u, scol.FormId);
        Assert.Equal("ScolFixture", scol.EditorId);
        Assert.Equal("meshes/test/scol.nif", scol.ModelPath);
        Assert.NotNull(scol.TextureHashData);
        Assert.Equal(8, scol.TextureHashData!.Length); // MODT we wrote: 8 bytes of opaque hash data
        Assert.NotNull(scol.Bounds);
        Assert.Equal(-10, scol.Bounds!.X1);
        Assert.Equal(20, scol.Bounds.X2);

        // Two ONAM/DATA part pairs preserved in stream order.
        Assert.Equal(2, scol.Parts.Count);
        Assert.Equal(0x0017B667u, scol.Parts[0].OnamFormId);
        Assert.Equal(2, scol.Parts[0].Placements.Count);
        Assert.Equal(100f, scol.Parts[0].Placements[0].X);
        Assert.Equal(1.5f, scol.Parts[0].Placements[0].Scale);
        Assert.Equal(200f, scol.Parts[0].Placements[1].X);

        Assert.Equal(0x0017B668u, scol.Parts[1].OnamFormId);
        Assert.Single(scol.Parts[1].Placements);
        Assert.Equal(-500f, scol.Parts[1].Placements[0].Y);
    }

    [Fact]
    public void ParseStaticCollections_BigEndian_DecodesXboxFormat()
    {
        var scolBytes = BuildSyntheticScolBE();

        var mainRecord = new DetectedMainRecord(
            "SCOL", (uint)(scolBytes.Length - 24), 0, 0x0003D377, 0, true);
        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, scolBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, scolBytes.Length);
        accessor.WriteArray(0, scolBytes, 0, scolBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: scolBytes.Length);
        var scols = parser.ParseStaticCollections();

        var scol = Assert.Single(scols);
        Assert.True(scol.IsBigEndian);
        Assert.Equal("SCOLParkingLotChunk03", scol.EditorId);
        Assert.Single(scol.Parts);
        Assert.Equal(0xDEADBEEFu, scol.Parts[0].OnamFormId);
        var placement = Assert.Single(scol.Parts[0].Placements);
        Assert.Equal(42.5f, placement.X);
        Assert.Equal(-7.25f, placement.Y);
        Assert.Equal(13.5f, placement.Z);
        Assert.Equal(0.5f, placement.RotX);
        Assert.Equal(1.25f, placement.RotY);
        Assert.Equal(-2.75f, placement.RotZ);
        Assert.Equal(2.0f, placement.Scale);
    }

    [Fact]
    public void ParseStaticCollections_UnknownSubrecord_IsSilentlyDroppedWithoutCrashing()
    {
        // Inject an unexpected signature (ZZZZ, 4 bytes of garbage) between MODL and ONAM.
        // The parser logs a debug message and continues — verifies the no-surprise guard.
        // (Deliberately NOT "XXXX" — that is the reserved extended-size marker, which the subrecord
        //  iterator legitimately consumes as the true length of the following subrecord.)
        var edid = NullTermString("FutureScol");
        var modl = NullTermString("m.nif");
        var onam = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(onam, 0xAAAAu);
        var data = BuildPlacementBytes(new[] { (0f, 0f, 0f, 0f, 0f, 0f, 1f) }, false);
        var unknown = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var scolBytes = BuildRecordBytes(0x00050200, "SCOL", false,
            ("EDID", edid),
            ("MODL", modl),
            ("ZZZZ", unknown),
            ("ONAM", onam),
            ("DATA", data));

        var mainRecord = new DetectedMainRecord(
            "SCOL", (uint)(scolBytes.Length - 24), 0, 0x00050200, 0, false);
        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, scolBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, scolBytes.Length);
        accessor.WriteArray(0, scolBytes, 0, scolBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: scolBytes.Length);
        var scols = parser.ParseStaticCollections();

        var scol = Assert.Single(scols);
        Assert.Equal("FutureScol", scol.EditorId);
        Assert.Single(scol.Parts);
        Assert.Equal(0xAAAAu, scol.Parts[0].OnamFormId);
    }

    /// <summary>
    ///     Fallout 76 widened ONAM from one FormID to two: the base object followed by an optional
    ///     override. Accepting only the 4-byte form dropped every part of all 17,384 SeventySix.esm
    ///     collections — and, because a DATA block attaches to the part its ONAM just opened, all
    ///     119,958 placement blocks with them, so the whole collection graph parsed empty.
    ///     Byte pattern taken from Burn_AshCave_RockCliff10 (0x008A29CF): ONAM
    ///     <c>71210000 a6e97c00</c> then a 56-byte (2 placement) DATA, and a part whose second
    ///     FormID is absent — the shape of 90,208 of the file's 119,954 eight-byte ONAMs.
    /// </summary>
    [Fact]
    public void ParseStaticCollections_Fallout76EightByteOnam_KeepsPartsAndPlacements()
    {
        var partWithOverride = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(partWithOverride.AsSpan(0), 0x00002171u);
        BinaryPrimitives.WriteUInt32LittleEndian(partWithOverride.AsSpan(4), 0x007CE9A6u);

        // Second part: object present, override zero — must read as "no override", not as FormID 0.
        var partNoOverride = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(partNoOverride.AsSpan(0), 0x000C565Bu);
        BinaryPrimitives.WriteUInt32LittleEndian(partNoOverride.AsSpan(4), 0u);

        var scolBytes = BuildRecordBytes(0x008A29CF, "SCOL", false,
            ("EDID", NullTermString("Burn_AshCave_RockCliff10")),
            ("MODL", NullTermString(@"SCOL\SeventySix.esm\CM008A29CF.NIF")),
            ("ONAM", partWithOverride),
            ("DATA", BuildPlacementBytes(new[]
            {
                (377.8f, -852.2f, -86.1f, 2.951f, -0.319f, -0.969f, 1.0f),
                (773.9f, -1314.6f, -103.7f, 2.919f, -0.299f, -0.866f, 1.0f)
            }, false)),
            ("ONAM", partNoOverride),
            ("DATA", BuildPlacementBytes(new[]
            {
                (0f, 0f, 0f, 0f, 0f, 0f, 1.24f)
            }, false)));

        var mainRecord = new DetectedMainRecord(
            "SCOL", (uint)(scolBytes.Length - 24), 0, 0x008A29CF, 0, false);
        using var mmf = MemoryMappedFile.CreateNew(null, scolBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, scolBytes.Length);
        accessor.WriteArray(0, scolBytes, 0, scolBytes.Length);

        var parser = new RecordParser(
            MakeScanResult([mainRecord]), accessor: accessor, fileSize: scolBytes.Length);
        var scol = Assert.Single(parser.ParseStaticCollections());

        Assert.Equal(2, scol.Parts.Count);

        Assert.Equal(0x00002171u, scol.Parts[0].OnamFormId);
        Assert.Equal(0x007CE9A6u, scol.Parts[0].SecondaryFormId);
        Assert.Equal(2, scol.Parts[0].Placements.Count);
        Assert.Equal(377.8f, scol.Parts[0].Placements[0].X, 3);
        Assert.Equal(-1314.6f, scol.Parts[0].Placements[1].Y, 3);

        Assert.Equal(0x000C565Bu, scol.Parts[1].OnamFormId);
        Assert.Null(scol.Parts[1].SecondaryFormId);
        Assert.Equal(1.24f, Assert.Single(scol.Parts[1].Placements).Scale, 3);
    }

    /// <summary>The 4-byte ONAM of Fallout 3/New Vegas must keep parsing, with no override.</summary>
    [Fact]
    public void ParseStaticCollections_LegacyFourByteOnam_LeavesSecondaryNull()
    {
        var scolBytes = BuildSyntheticScolLE();
        var mainRecord = new DetectedMainRecord(
            "SCOL", (uint)(scolBytes.Length - 24), 0, 0x00050100, 0, false);
        using var mmf = MemoryMappedFile.CreateNew(null, scolBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, scolBytes.Length);
        accessor.WriteArray(0, scolBytes, 0, scolBytes.Length);

        var parser = new RecordParser(
            MakeScanResult([mainRecord]), accessor: accessor, fileSize: scolBytes.Length);
        var scol = Assert.Single(parser.ParseStaticCollections());

        Assert.All(scol.Parts, part => Assert.Null(part.SecondaryFormId));
    }

    private static byte[] BuildSyntheticScolLE()
    {
        var edid = NullTermString("ScolFixture");

        var obnd = new byte[12];
        BinaryPrimitives.WriteInt16LittleEndian(obnd.AsSpan(0), -10);
        BinaryPrimitives.WriteInt16LittleEndian(obnd.AsSpan(2), -5);
        BinaryPrimitives.WriteInt16LittleEndian(obnd.AsSpan(4), -5);
        BinaryPrimitives.WriteInt16LittleEndian(obnd.AsSpan(6), 20);
        BinaryPrimitives.WriteInt16LittleEndian(obnd.AsSpan(8), 10);
        BinaryPrimitives.WriteInt16LittleEndian(obnd.AsSpan(10), 15);

        var modl = NullTermString("meshes/test/scol.nif");
        var modt = new byte[8] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };

        var onam1 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(onam1, 0x0017B667u);
        var data1 = BuildPlacementBytes(new[]
        {
            (100f, 0f, 0f, 0f, 0f, 0f, 1.5f),
            (200f, 0f, 0f, 0f, 0f, 0f, 1.0f)
        }, false);

        var onam2 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(onam2, 0x0017B668u);
        var data2 = BuildPlacementBytes(new[]
        {
            (0f, -500f, 0f, 0f, 0f, 0f, 1.0f)
        }, false);

        return BuildRecordBytes(0x00050100, "SCOL", false,
            ("EDID", edid),
            ("OBND", obnd),
            ("MODL", modl),
            ("MODT", modt),
            ("ONAM", onam1),
            ("DATA", data1),
            ("ONAM", onam2),
            ("DATA", data2));
    }

    private static byte[] BuildSyntheticScolBE()
    {
        var edid = NullTermString("SCOLParkingLotChunk03");

        var onam = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(onam, 0xDEADBEEFu);

        // Distinct values in all seven placement slots so a swapped/mis-offset field
        // (X/Y/Z vs RotX/RotY/RotZ vs Scale) cannot decode to a passing value.
        var data = BuildPlacementBytes(new[]
        {
            (42.5f, -7.25f, 13.5f, 0.5f, 1.25f, -2.75f, 2.0f)
        }, true);

        return BuildRecordBytes(0x0003D377, "SCOL", true,
            ("EDID", edid),
            ("ONAM", onam),
            ("DATA", data));
    }

    private static byte[] BuildPlacementBytes(
        (float X, float Y, float Z, float RotX, float RotY, float RotZ, float Scale)[] placements,
        bool bigEndian)
    {
        var bytes = new byte[placements.Length * 28];
        for (var i = 0; i < placements.Length; i++)
        {
            var span = bytes.AsSpan(i * 28, 28);
            WriteFloat(span, 0, placements[i].X, bigEndian);
            WriteFloat(span, 4, placements[i].Y, bigEndian);
            WriteFloat(span, 8, placements[i].Z, bigEndian);
            WriteFloat(span, 12, placements[i].RotX, bigEndian);
            WriteFloat(span, 16, placements[i].RotY, bigEndian);
            WriteFloat(span, 20, placements[i].RotZ, bigEndian);
            WriteFloat(span, 24, placements[i].Scale, bigEndian);
        }

        return bytes;
    }

    private static void WriteFloat(Span<byte> dest, int offset, float value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(dest.Slice(offset, 4), value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(offset, 4), value);
        }
    }
}