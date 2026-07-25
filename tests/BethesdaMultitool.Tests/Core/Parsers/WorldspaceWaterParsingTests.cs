using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
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
    private static List<WorldspaceRecord> ParseWorldspace(
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

    private static List<WorldspaceRecord>
        ParseWorldspaceSet(params (uint FormId, byte[] Bytes)[] records)
    {
        var totalLength = records.Sum(r => r.Bytes.Length);
        var buffer = new byte[totalLength];
        var mains = new List<DetectedMainRecord>(records.Length);
        var offset = 0;
        foreach (var (formId, bytes) in records)
        {
            Array.Copy(bytes, 0, buffer, offset, bytes.Length);
            mains.Add(new DetectedMainRecord("WRLD", (uint)(bytes.Length - 24), 0, formId, offset, false));
            offset += bytes.Length;
        }

        var scanResult = MakeScanResult(mains);

        using var mmf = MemoryMappedFile.CreateNew(null, buffer.Length);
        using var accessor = mmf.CreateViewAccessor(0, buffer.Length);
        accessor.WriteArray(0, buffer, 0, buffer.Length);

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: buffer.Length);
        return parser.ParseWorldspaces();
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
    public void ParseWorldspaces_TES4ChildWorldspace_InheritsParentWater()
    {
        // BravilWorld (0x0001C319) authors neither NAM2 nor DNAM — only WNAM=Tamriel. The engine
        // inherits water from the WNAM parent implicitly; the parser mirrors that after parsing.
        var tamriel = BuildRecordBytes(0x0000003C, "WRLD", false,
            ("EDID", NullTermString("Tamriel")),
            ("NAM2", FormIdBytes(0x00000018)));
        var bravil = BuildRecordBytes(0x0001C319, "WRLD", false,
            ("EDID", NullTermString("BravilWorld")),
            ("WNAM", FormIdBytes(0x0000003C)));

        var worldspaces = ParseWorldspaceSet((0x0000003C, tamriel), (0x0001C319, bravil));

        var parent = Assert.Single(worldspaces, w => w.FormId == 0x0000003C);
        var child = Assert.Single(worldspaces, w => w.FormId == 0x0001C319);
        Assert.False(parent.WaterFromParentWorldspace);
        Assert.Equal(0f, child.DefaultWaterHeight);
        Assert.Equal(0x00000018u, child.WaterFormId);
        Assert.True(child.WaterFromParentWorldspace);
    }

    [Fact]
    public void ParseWorldspaces_TES4GrandchildWorldspace_WalksWnamChain()
    {
        // A grandchild whose direct parent is itself waterless must keep walking WNAM up to the
        // first ancestor with water (list order deliberately grandchild-first).
        var grandchild = BuildRecordBytes(0x00000300, "WRLD", false,
            ("EDID", NullTermString("GrandchildWorld")),
            ("WNAM", FormIdBytes(0x00000200)));
        var parent = BuildRecordBytes(0x00000200, "WRLD", false,
            ("EDID", NullTermString("MiddleWorld")),
            ("WNAM", FormIdBytes(0x00000100)));
        var root = BuildRecordBytes(0x00000100, "WRLD", false,
            ("EDID", NullTermString("RootWorld")),
            ("NAM2", FormIdBytes(0x00000018)));

        var worldspaces = ParseWorldspaceSet(
            (0x00000300, grandchild), (0x00000200, parent), (0x00000100, root));

        var resolvedGrandchild = Assert.Single(worldspaces, w => w.FormId == 0x00000300);
        Assert.Equal(0f, resolvedGrandchild.DefaultWaterHeight);
        Assert.Equal(0x00000018u, resolvedGrandchild.WaterFormId);
        Assert.True(resolvedGrandchild.WaterFromParentWorldspace);
    }

    [Fact]
    public void ParseWorldspaces_WnamCycle_TerminatesAndStaysDry()
    {
        // A malformed WNAM cycle (A→B→A) with no water anywhere must terminate and leave both dry.
        var a = BuildRecordBytes(0x00000400, "WRLD", false,
            ("EDID", NullTermString("CycleA")),
            ("WNAM", FormIdBytes(0x00000500)));
        var b = BuildRecordBytes(0x00000500, "WRLD", false,
            ("EDID", NullTermString("CycleB")),
            ("WNAM", FormIdBytes(0x00000400)));

        var worldspaces = ParseWorldspaceSet((0x00000400, a), (0x00000500, b));

        Assert.All(worldspaces, w =>
        {
            Assert.Null(w.DefaultWaterHeight);
            Assert.False(w.WaterFromParentWorldspace);
        });
    }

    [Fact]
    public void ParseWorldspaces_ChildOfWaterlessParent_StaysDry()
    {
        // A child of a genuinely waterless parent inherits nothing — no synthesized sea level.
        var parent = BuildRecordBytes(0x00000600, "WRLD", false,
            ("EDID", NullTermString("DryParent")));
        var child = BuildRecordBytes(0x00000700, "WRLD", false,
            ("EDID", NullTermString("DryChild")),
            ("WNAM", FormIdBytes(0x00000600)));

        var worldspaces = ParseWorldspaceSet((0x00000600, parent), (0x00000700, child));

        var resolvedChild = Assert.Single(worldspaces, w => w.FormId == 0x00000700);
        Assert.Null(resolvedChild.DefaultWaterHeight);
        Assert.False(resolvedChild.WaterFromParentWorldspace);
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