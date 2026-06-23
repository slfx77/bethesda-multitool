using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Parsers;

/// <summary>
///     Regression tests for the worldspace water-height default. Oblivion (TES4) worldspaces have no
///     DNAM land/water-height subrecord — that field was a Fallout 3 addition (the
///     <c>GameProfile.HasWorldspaceDefaultWaterHeight</c> capability) — so their ocean cells carry no
///     XCLW and the engine renders water at Z 0 by convention. The parser fills in
///     <see cref="BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WorldspaceRecord.DefaultWaterHeight" />
///     = 0 for such worldspaces (only when they actually reference water via NAM2) so the viewer's
///     fallback paints the coast instead of leaving it dry. The decision is keyed on the detected game:
///     FO3/FNV/Skyrim+ are flagged as having the field, so they never get the synthesized default (and
///     always emit a real DNAM anyway). These synthetic records carry no TES4/HEDR, so the parser
///     detects <c>BethesdaGame.Unknown</c> — whose profile also lacks the field — which is exactly the
///     structural fallback that keeps undetected files behaving correctly.
/// </summary>
public class WorldspaceWaterParsingTests
{
    private static List<BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WorldspaceRecord> ParseWorldspace(
        byte[] recordBytes, uint formId)
    {
        var mainRecord = new DetectedMainRecord("WRLD",
            (uint)(recordBytes.Length - 24), 0, formId, 0, false);
        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        return parser.ParseWorldspaces();
    }

    private static byte[] FormIdBytes(uint value)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        return buf;
    }

    [Fact]
    public void ParseWorldspaces_TES4Style_NoDnamWithNam2_DefaultsWaterToZero()
    {
        // Oblivion Tamriel: EDID + NAM2 (water FormID 0x18) and NO DNAM.
        var recordBytes = BuildRecordBytes(0x0000003C, "WRLD", false,
            ("EDID", NullTermString("Tamriel")),
            ("NAM2", FormIdBytes(0x00000018)));

        var ws = Assert.Single(ParseWorldspace(recordBytes, 0x0000003C));

        Assert.Equal(0x00000018u, ws.WaterFormId);
        Assert.Null(ws.DefaultLandHeight);
        Assert.Equal(0f, ws.DefaultWaterHeight);
    }

    [Fact]
    public void ParseWorldspaces_NoDnamNoNam2_LeavesWaterNull()
    {
        // A waterless TES4 test world (no DNAM, no NAM2) must stay dry — no synthesized sea level.
        var recordBytes = BuildRecordBytes(0x00023ECF, "WRLD", false,
            ("EDID", NullTermString("WaterlessTestWorld")));

        var ws = Assert.Single(ParseWorldspace(recordBytes, 0x00023ECF));

        Assert.Null(ws.WaterFormId);
        Assert.Null(ws.DefaultWaterHeight);
    }

    [Fact]
    public void ParseWorldspaces_FnvStyleWithDnam_PreservesDnamDefault()
    {
        // FO3/FNV WastelandNV: DNAM carries land=-2500, water=-2300. The TES4 zero-default must NOT
        // overwrite a real DNAM value.
        var dnam = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(dnam.AsSpan(0), -2500f);
        BinaryPrimitives.WriteSingleLittleEndian(dnam.AsSpan(4), -2300f);

        var recordBytes = BuildRecordBytes(0x000DA726, "WRLD", false,
            ("EDID", NullTermString("WastelandNV")),
            ("DNAM", dnam),
            ("NAM2", FormIdBytes(0x00030009)));

        var ws = Assert.Single(ParseWorldspace(recordBytes, 0x000DA726));

        Assert.Equal(-2500f, ws.DefaultLandHeight);
        Assert.Equal(-2300f, ws.DefaultWaterHeight);
        Assert.Equal(0x00030009u, ws.WaterFormId);
    }
}
