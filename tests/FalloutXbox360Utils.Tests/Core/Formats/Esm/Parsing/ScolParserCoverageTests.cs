using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.EsmTestRecordBuilder;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Parsing;

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
        Assert.Single(scol.Parts[0].Placements);
        Assert.Equal(42.5f, scol.Parts[0].Placements[0].X);
        Assert.Equal(2.0f, scol.Parts[0].Placements[0].Scale);
    }

    [Fact]
    public void ParseStaticCollections_UnknownSubrecord_IsSilentlyDroppedWithoutCrashing()
    {
        // Inject an unexpected signature (XXXX, 4 bytes of garbage) between MODL and ONAM.
        // The parser logs a debug message and continues — verifies the no-surprise guard.
        var edid = NullTermString("FutureScol");
        var modl = NullTermString("m.nif");
        var onam = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(onam, 0xAAAAu);
        var data = BuildPlacementBytes(new[] { (0f, 0f, 0f, 0f, 0f, 0f, 1f) }, bigEndian: false);
        var unknown = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var scolBytes = BuildRecordBytes(0x00050200, "SCOL", bigEndian: false,
            ("EDID", edid),
            ("MODL", modl),
            ("XXXX", unknown),
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
        }, bigEndian: false);

        var onam2 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(onam2, 0x0017B668u);
        var data2 = BuildPlacementBytes(new[]
        {
            (0f, -500f, 0f, 0f, 0f, 0f, 1.0f)
        }, bigEndian: false);

        return BuildRecordBytes(0x00050100, "SCOL", bigEndian: false,
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
        var data = BuildPlacementBytes(new[]
        {
            (42.5f, 0f, 0f, 0f, 0f, 0f, 2.0f)
        }, bigEndian: true);

        return BuildRecordBytes(0x0003D377, "SCOL", bigEndian: true,
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
