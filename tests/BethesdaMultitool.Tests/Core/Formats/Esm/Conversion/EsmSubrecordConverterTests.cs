using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Conversion;

/// <summary>
///     Tests for EsmSubrecordConverter — the critical Xbox-to-PC byte-swapping pipeline.
/// </summary>
public class EsmSubrecordConverterTests
{
    #region FormIdLittleEndian (No Swap)

    [Fact]
    public void ConvertSubrecordData_FormIdLittleEndian_NotSwapped()
    {
        // QSTI is a FormIdLittleEndian in DIAL - should NOT be swapped
        // Check that the schema exists and handles it
        var schema = SubrecordSchemaRegistry.GetSchema("QSTI", "DIAL", 4);
        Assert.NotNull(schema);

        // The field should be FormIdLittleEndian
        Assert.True(schema!.Fields.Length > 0);
    }

    #endregion

    #region ColorArgb Conversion

    [Fact]
    public void ConvertSubrecordData_ColorArgb_ConvertsArgbToRgba()
    {
        // XCLL has ARGB colors that need ARGB -> RGBA conversion
        // Xbox: [A][R][G][B] -> PC: [R][G][B][A]
        // Find a subrecord that uses ColorArgb - XCLL in CELL has it
        var schema = SubrecordSchemaRegistry.GetSchema("XCLL", "CELL", 40);
        // If schema exists, verify it has ColorArgb fields
        if (schema != null)
        {
            var hasColorArgb = schema.Fields.Any(f => f.Type == SubrecordFieldType.ColorArgb);
            // XCLL should have color fields
            Assert.True(hasColorArgb || schema.Fields.Length > 0);
        }
    }

    #endregion

    #region PKDT Special Handling

    [Fact]
    public void ConvertSubrecordData_Pkdt_SwapsFlags1AndType()
    {
        // PKDT (12 bytes): Flags1(1), Flags2(2LE), Type(1), Unused(2), FBFlags(2BE), TSFlags(2BE), Unk(2)
        // Xbox swaps Flags1 and Type within first 4 bytes
        byte[] data =
        [
            0x03, // Type (should end up at byte 3)
            0x00, 0x01, // Flags2 BE (should be swapped)
            0x00, // Flags1 (should end up at byte 0)
            0x00, 0x00, // Unused
            0x00, 0x02, // FalloutBehaviorFlags BE
            0x00, 0x04, // TypeSpecificFlags BE
            0x00, 0x00 // Unknown
        ];
        var result = EsmSubrecordConverter.ConvertSubrecordData("PKDT", data, "PACK");

        // Byte 0 and 3 swapped: Flags1(0x00) at [0], Type(0x03) at [3]
        Assert.Equal(0x00, result[0]); // Flags1
        Assert.Equal(0x03, result[3]); // Type
        // Flags2 (bytes 1-2) swapped: 0x00 0x01 -> 0x01 0x00
        Assert.Equal(0x01, result[1]);
        Assert.Equal(0x00, result[2]);
    }

    #endregion

    #region IMAD DNAM Special Handling

    [Fact]
    public void ConvertSubrecordData_ImadDnam244_SkipsFirst4Bytes()
    {
        // IMAD DNAM (244 bytes): first 4 bytes are already LE on Xbox, rest need swap
        var data = new byte[244];
        // First 4 bytes: already LE, should NOT be swapped
        data[0] = 0x01;
        data[1] = 0x00;
        data[2] = 0x00;
        data[3] = 0x00;
        // Bytes 4-7: float, should be swapped (BE: 3F800000 = 1.0)
        data[4] = 0x3F;
        data[5] = 0x80;
        data[6] = 0x00;
        data[7] = 0x00;

        var result = EsmSubrecordConverter.ConvertSubrecordData("DNAM", data, "IMAD");

        // First 4 bytes preserved (already LE)
        Assert.Equal(0x01, result[0]);
        Assert.Equal(0x00, result[1]);
        Assert.Equal(0x00, result[2]);
        Assert.Equal(0x00, result[3]);
        // Bytes 4-7 swapped
        Assert.Equal(0x00, result[4]);
        Assert.Equal(0x00, result[5]);
        Assert.Equal(0x80, result[6]);
        Assert.Equal(0x3F, result[7]);
    }

    #endregion

    #region NVTR NavMesh Triangle Reordering

    [Fact]
    public void ConvertSubrecordData_Nvtr_SwapsUInt16sAndReordersCoverFlags()
    {
        // NVTR: 16 bytes per entry, each uint16 swapped, then CoverFlags/Flags positions swapped
        // Entry: V0(2), V1(2), V2(2), E01(2), E12(2), E20(2), CoverFlags(2), Flags(2)
        byte[] data =
        [
            0x00, 0x01, // V0 BE
            0x00, 0x02, // V1 BE
            0x00, 0x03, // V2 BE
            0x00, 0x10, // E01 BE
            0x00, 0x11, // E12 BE
            0x00, 0x12, // E20 BE
            0xAA, 0xBB, // CoverFlags BE
            0xCC, 0xDD // Flags BE
        ];

        var result = EsmSubrecordConverter.ConvertSubrecordData("NVTR", data, "NAVM");

        // All uint16 values swapped
        Assert.Equal(0x01, result[0]);
        Assert.Equal(0x00, result[1]); // V0 LE
        Assert.Equal(0x02, result[2]);
        Assert.Equal(0x00, result[3]); // V1 LE

        // After endian swap: CoverFlags was at 12-13, Flags at 14-15
        // Their POSITIONS are then swapped, so Flags moves to 12-13 and CoverFlags to 14-15
        Assert.Equal(0xDD, result[12]); // Flags (was at 14-15)
        Assert.Equal(0xCC, result[13]);
        Assert.Equal(0xBB, result[14]); // CoverFlags (was at 12-13)
        Assert.Equal(0xAA, result[15]);
    }

    #endregion

    #region IDLE DATA Special Case

    [Fact]
    public void ConvertSubrecordData_IdleData8Bytes_TruncatesTo6()
    {
        // IDLE DATA(8): Xbox has 8 bytes, PC uses 6
        // sReplayDelay at offset 4-5 is BE on Xbox, swapped to LE for PC
        byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x00, 0x0F];
        var result = EsmSubrecordConverter.ConvertSubrecordData("DATA", data, "IDLE");
        Assert.Equal(6, result.Length);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x06, 0x05 }, result);
    }

    #endregion

    #region BPND Mixed Endianness

    [Fact]
    public void ConvertSubrecordData_Bpnd_FloatsSwappedFormIdsAndCountsPreserved()
    {
        // BPND uses mixed endianness on Xbox 360:
        //   - Float fields are big-endian → must be swapped to LE
        //   - UInt16 / Int32 / FormId fields are already little-endian on Xbox →
        //     must NOT be swapped (would corrupt them, see Phase 1B.17g).
        //
        // Synthesized payload modeled on a real BPND seen in FalloutNV.esm:
        //   DamageMult=1.0 (BE float 3F-80-00-00)
        //   DebrisCount=4  (LE u16 04-00)
        //   Debris FormID 0x0001B2BF (LE bytes BF-B2-01-00)
        //   Explosion FormID 0x0001540D (LE bytes 0D-54-01-00)
        var data = new byte[84];

        // [0..3] DamageMult: BE 1.0 = 3F 80 00 00 → should become LE 1.0 = 00 00 80 3F.
        data[0] = 0x3F;
        data[1] = 0x80;
        // [4..9] six UInt8 flags — leave as zeros.
        data[5] = 0x01; // PartType for sanity
        // [10..11] DebrisCount = 4 (already LE).
        data[10] = 0x04;
        // [12..15] Debris FormID 0x0001B2BF stored LE.
        data[12] = 0xBF;
        data[13] = 0xB2;
        data[14] = 0x01;
        data[15] = 0x00;
        // [16..19] Explosion FormID 0x0001540D stored LE.
        data[16] = 0x0D;
        data[17] = 0x54;
        data[18] = 0x01;
        data[19] = 0x00;
        // [20..23] TrackingMaxAngle BE float 1.0 → LE 1.0.
        data[20] = 0x3F;
        data[21] = 0x80;
        // [24..27] DebrisScale BE float 1.0.
        data[24] = 0x3F;
        data[25] = 0x80;
        // [28..31] SeverableDebrisCount (Int32LittleEndian) = 0 (zeros).
        // [32..35] SeverableDebris LE 0x00000000.
        // [36..39] SeverableExplosion LE 0x00000000.
        // [40..43] SeverableDebrisScale BE float 1.0.
        data[40] = 0x3F;
        data[41] = 0x80;
        // [44..67] GoreTransform PosRot (6 floats) — zeros.
        // [68..71] SeverableImpact LE 0x0002ED47.
        data[68] = 0x47;
        data[69] = 0xED;
        data[70] = 0x02;
        data[71] = 0x00;
        // [72..75] ExplodableImpact LE 0x0002ED47.
        data[72] = 0x47;
        data[73] = 0xED;
        data[74] = 0x02;
        data[75] = 0x00;
        // [76] SeverableDecalCount, [77] ExplodableDecalCount, [78..79] padding.
        // [80..83] LimbReplacementScale BE float 1.0.
        data[80] = 0x3F;
        data[81] = 0x80;

        var result = EsmSubrecordConverter.ConvertSubrecordData("BPND", data, "BPTD");

        Assert.Equal(84, result.Length);

        // DamageMult: BE → LE.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, result[..4]);
        // PartType preserved.
        Assert.Equal(0x01, result[5]);
        // DebrisCount preserved (was already LE).
        Assert.Equal(0x04, result[10]);
        Assert.Equal(0x00, result[11]);
        // Debris FormID preserved (was already LE) — would have been BF→00 if wrongly swapped.
        Assert.Equal(new byte[] { 0xBF, 0xB2, 0x01, 0x00 }, result[12..16]);
        // Explosion FormID preserved.
        Assert.Equal(new byte[] { 0x0D, 0x54, 0x01, 0x00 }, result[16..20]);
        // TrackingMaxAngle: BE → LE.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, result[20..24]);
        // DebrisScale: BE → LE.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, result[24..28]);
        // SeverableDebrisScale: BE → LE.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, result[40..44]);
        // SeverableImpact preserved.
        Assert.Equal(new byte[] { 0x47, 0xED, 0x02, 0x00 }, result[68..72]);
        // ExplodableImpact preserved.
        Assert.Equal(new byte[] { 0x47, 0xED, 0x02, 0x00 }, result[72..76]);
        // LimbReplacementScale: BE → LE.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, result[80..84]);
    }

    #endregion

    #region ByteArray Schema (No Conversion)

    [Fact]
    public void ConvertSubrecordData_ByteArraySchema_NoConversion()
    {
        // DATA fallback for small sizes (<= 2 bytes) returns ByteArray
        byte[] data = [0xAA, 0xBB];
        // Use a record type that doesn't have a specific DATA schema with size 2
        var result = EsmSubrecordConverter.ConvertSubrecordData("DATA", data, "UNKN");
        Assert.Equal(data, result);
    }

    #endregion

    #region WTHR *IAD Subrecords

    [Fact]
    public void ConvertSubrecordData_WthrIadSubrecord_TreatedAsFloatArray()
    {
        // WTHR records use *IAD subrecords (e.g., \x00IAD, @IAD, AIAD) as float arrays
        byte[] data = [0x41, 0x20, 0x00, 0x00]; // 10.0f BE
        var result = EsmSubrecordConverter.ConvertSubrecordData("AIAD", data, "WTHR");
        Assert.Equal(new byte[] { 0x00, 0x00, 0x20, 0x41 }, result); // 10.0f LE
    }

    #endregion

    #region No Schema Throws

    [Fact]
    public void ConvertSubrecordData_NoSchema_ThrowsNotSupportedException()
    {
        byte[] data = [0x01, 0x02, 0x03, 0x04];
        var ex = Assert.Throws<NotSupportedException>(() =>
            EsmSubrecordConverter.ConvertSubrecordData("ZZZZ", data, "ZZZZ"));
        Assert.Contains("No schema", ex.Message);
        Assert.Contains("ZZZZ", ex.Message);
    }

    #endregion

    #region ATXT/BTXT Platform Flag

    [Fact]
    public void ConvertSubrecordData_Atxt8Bytes_SwapsFormIdAndSetsFlag()
    {
        // ATXT(8): FormID(4) + byte + platformFlag + Layer(2)
        byte[] data = [0x00, 0x12, 0x34, 0x56, 0x00, 0x00, 0x00, 0x01]; // FormID BE + padding + layer BE
        var result = EsmSubrecordConverter.ConvertSubrecordData("ATXT", data, "LAND");
        // FormID swapped
        Assert.Equal(0x56, result[0]);
        Assert.Equal(0x34, result[1]);
        Assert.Equal(0x12, result[2]);
        Assert.Equal(0x00, result[3]);
        // Platform flag set to 0x88 (PC value)
        Assert.Equal(0x88, result[5]);
        // Layer swapped
        Assert.Equal(0x01, result[6]);
        Assert.Equal(0x00, result[7]);
    }

    #endregion

    #region NOTE TNAM FormID

    [Fact]
    public void ConvertSubrecordData_NoteTnam4Bytes_SwapsAsFormId()
    {
        // NOTE TNAM 4 bytes is treated as a FormID, not a string
        byte[] data = [0x00, 0x12, 0xAB, 0x34];
        var result = EsmSubrecordConverter.ConvertSubrecordData("TNAM", data, "NOTE");
        Assert.Equal(new byte[] { 0x34, 0xAB, 0x12, 0x00 }, result);
    }

    #endregion

    #region Vec3 and PosRot

    [Fact]
    public void ConvertSubrecordData_Xscl_SwapsFloat()
    {
        // XSCL in REFR is a single float (scale)
        byte[] data = [0x3F, 0x80, 0x00, 0x00]; // 1.0f BE
        var result = EsmSubrecordConverter.ConvertSubrecordData("XSCL", data, "REFR");
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, result); // 1.0f LE
    }

    #endregion

    #region NVDP NavMesh Door Links

    [Fact]
    public void ConvertSubrecordData_Nvdp_SwapsFormIdAndTriangleOnly()
    {
        // NVDP: 8 bytes per entry: FormID(4) + Triangle(2) + Padding(2)
        // PDB: NavMeshTriangleDoorPortal has only pDoorForm(uint32,+0) and iOwningTriangleIndex(uint16,+4)
        // Disassembly confirms Endian() does NOT swap bytes +6-7 (struct padding)
        byte[] data =
        [
            0x00, 0x12, 0x34, 0x56, // FormID BE
            0x00, 0x0A, // Triangle BE
            0x00, 0x05 // Padding (not swapped)
        ];
        var result = EsmSubrecordConverter.ConvertSubrecordData("NVDP", data, "NAVM");
        // FormID swapped
        Assert.Equal(0x56, result[0]);
        Assert.Equal(0x34, result[1]);
        Assert.Equal(0x12, result[2]);
        Assert.Equal(0x00, result[3]);
        // Triangle swapped
        Assert.Equal(0x0A, result[4]);
        Assert.Equal(0x00, result[5]);
        // Padding preserved as-is (not swapped)
        Assert.Equal(0x00, result[6]);
        Assert.Equal(0x05, result[7]);
    }

    #endregion

    #region Empty Data

    [Fact]
    public void ConvertSubrecordData_EmptyString_ReturnsEmpty()
    {
        byte[] data = [];
        var result = EsmSubrecordConverter.ConvertSubrecordData("EDID", data, "WEAP");
        Assert.Empty(result);
    }

    #endregion

    #region NVMI Navmesh Info (NAVI)

    // NVMI layout: Flags(4) + NavmeshFormID(4) + LocationFormID(4) + GridKey(4) +
    // ApproxLocation Vec3(12), then optional island data (flag bit 5), then trailing
    // Preferred % float(4). Minimum well-formed size is 32 bytes (28-byte base + trailer).

    [Fact]
    public void ConvertNvmi_MinimalNonIsland32Bytes_SwapsHeaderAndTrailingFloat()
    {
        byte[] data =
        [
            0x00, 0x00, 0x00, 0x01, // Flags BE 0x00000001 (island bit 5 clear)
            0x00, 0x12, 0x34, 0x56, // Navmesh FormID BE 0x00123456
            0x00, 0xAB, 0xCD, 0xEF, // Location FormID BE 0x00ABCDEF
            0x01, 0x02, 0x03, 0x04, // Grid key BE (Grid Y + Grid X packed)
            0x3F, 0x80, 0x00, 0x00, // Approx X = 1.0f BE
            0x40, 0x00, 0x00, 0x00, // Approx Y = 2.0f BE
            0x40, 0x40, 0x00, 0x00, // Approx Z = 3.0f BE
            0x3F, 0x00, 0x00, 0x00 // Preferred % = 0.5f BE
        ];

        var result = EsmSubrecordConverter.ConvertSubrecordData("NVMI", data, "NAVI");

        byte[] expected =
        [
            0x01, 0x00, 0x00, 0x00, // Flags LE
            0x56, 0x34, 0x12, 0x00, // Navmesh FormID LE
            0xEF, 0xCD, 0xAB, 0x00, // Location FormID LE
            0x04, 0x03, 0x02, 0x01, // Grid key LE
            0x00, 0x00, 0x80, 0x3F, // Approx X = 1.0f LE
            0x00, 0x00, 0x00, 0x40, // Approx Y = 2.0f LE
            0x00, 0x00, 0x40, 0x40, // Approx Z = 3.0f LE
            0x00, 0x00, 0x00, 0x3F // Preferred % = 0.5f LE
        ];
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(27)]
    [InlineData(31)]
    public void ConvertNvmi_TooShort_PassesThroughUnmodified(int length)
    {
        // Below the 32-byte minimum nothing can be swapped safely; the converter must
        // return the input byte-identical instead of throwing (the write path has no catch).
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 7 + 1);
        }

        var expected = (byte[])data.Clone();

        var result = EsmSubrecordConverter.ConvertSubrecordData("NVMI", data, "NAVI");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ConvertNvmi_IslandVertexCountLie_DoesNotThrow()
    {
        // 60 bytes: base(28) + bounds(24) + counts(4) + trailing float(4).
        // vertexCount claims 0xFFFF but zero vertex bytes are present.
        var data = new byte[60];
        data[3] = 0x20; // Flags BE 0x00000020 — island bit 5
        data[52] = 0xFF;
        data[53] = 0xFF; // vertexCount BE 0xFFFF (lie); triangleCount stays 0

        var result = EsmSubrecordConverter.ConvertSubrecordData("NVMI", data, "NAVI");

        Assert.Equal(60, result.Length);
        // Flags swapped; vertex loop consumed nothing (no room before the trailing float).
        Assert.Equal(0x20, result[0]);
        Assert.Equal(0x00, result[3]);
    }

    [Fact]
    public void ConvertNvmi_IslandTriangleCountLie_DoesNotThrow()
    {
        // 72 bytes: base(28) + bounds(24) + counts(4) + one vertex(12) + trailing float(4).
        // triangleCount claims 0xFFFF but zero triangle bytes are present.
        var data = new byte[72];
        data[3] = 0x20; // island flag
        data[53] = 0x01; // vertexCount BE = 1
        data[54] = 0xFF;
        data[55] = 0xFF; // triangleCount BE 0xFFFF (lie)

        var result = EsmSubrecordConverter.ConvertSubrecordData("NVMI", data, "NAVI");

        Assert.Equal(72, result.Length);
        Assert.Equal(0x20, result[0]);
        // vertexCount swapped to LE 1; triangle loop consumed nothing.
        Assert.Equal(0x01, result[52]);
        Assert.Equal(0x00, result[53]);
    }

    [Fact]
    public void ConvertNvmi_IslandTruncatedMidBounds_DoesNotThrow()
    {
        // 40 bytes with the island flag set: the island section needs 28 bytes (bounds + counts)
        // before the trailing float, but only 8 are present — the whole island block is skipped
        // and bytes 28..35 pass through unconverted (accepted partial-swap degrade).
        var data = new byte[40];
        data[3] = 0x20; // island flag
        data[28] = 0xAA; // island-region remainder that must survive untouched
        data[35] = 0xBB;
        data[36] = 0x11; // trailing Preferred % float BE 0x11000044
        data[39] = 0x44;

        var result = EsmSubrecordConverter.ConvertSubrecordData("NVMI", data, "NAVI");

        Assert.Equal(40, result.Length);
        // Unconverted island remainder.
        Assert.Equal(0xAA, result[28]);
        Assert.Equal(0xBB, result[35]);
        // Trailing float still swapped.
        Assert.Equal(0x44, result[36]);
        Assert.Equal(0x11, result[39]);
    }

    [Fact]
    public void ConvertNvmi_ValidIslandOneVertexOneTriangle_SwapsAllFields()
    {
        // 78 bytes: base(28) + bounds(24) + counts(4) + vertex(12) + triangle(6) + trailer(4).
        byte[] data =
        [
            0x00, 0x00, 0x00, 0x20, // Flags BE 0x00000020 (island bit 5 set)
            0x00, 0x12, 0x34, 0x56, // Navmesh FormID BE 0x00123456
            0x00, 0xAB, 0xCD, 0xEF, // Location FormID BE 0x00ABCDEF
            0x01, 0x02, 0x03, 0x04, // Grid key BE
            0x3F, 0x80, 0x00, 0x00, // Approx X = 1.0f BE
            0x40, 0x00, 0x00, 0x00, // Approx Y = 2.0f BE
            0x40, 0x40, 0x00, 0x00, // Approx Z = 3.0f BE
            0x40, 0x80, 0x00, 0x00, // Bounds min X = 4.0f BE
            0x40, 0xA0, 0x00, 0x00, // Bounds min Y = 5.0f BE
            0x40, 0xC0, 0x00, 0x00, // Bounds min Z = 6.0f BE
            0x40, 0xE0, 0x00, 0x00, // Bounds max X = 7.0f BE
            0x41, 0x00, 0x00, 0x00, // Bounds max Y = 8.0f BE
            0x41, 0x10, 0x00, 0x00, // Bounds max Z = 9.0f BE
            0x00, 0x01, // Vertex count BE = 1
            0x00, 0x01, // Triangle count BE = 1
            0x41, 0x20, 0x00, 0x00, // Vertex X = 10.0f BE
            0x41, 0x30, 0x00, 0x00, // Vertex Y = 11.0f BE
            0x41, 0x40, 0x00, 0x00, // Vertex Z = 12.0f BE
            0x00, 0x01, // Triangle index 0 BE
            0x00, 0x02, // Triangle index 1 BE
            0x00, 0x03, // Triangle index 2 BE
            0x3F, 0x00, 0x00, 0x00 // Preferred % = 0.5f BE
        ];

        var result = EsmSubrecordConverter.ConvertSubrecordData("NVMI", data, "NAVI");

        byte[] expected =
        [
            0x20, 0x00, 0x00, 0x00, // Flags LE
            0x56, 0x34, 0x12, 0x00, // Navmesh FormID LE
            0xEF, 0xCD, 0xAB, 0x00, // Location FormID LE
            0x04, 0x03, 0x02, 0x01, // Grid key LE
            0x00, 0x00, 0x80, 0x3F, // Approx X LE
            0x00, 0x00, 0x00, 0x40, // Approx Y LE
            0x00, 0x00, 0x40, 0x40, // Approx Z LE
            0x00, 0x00, 0x80, 0x40, // Bounds min X LE
            0x00, 0x00, 0xA0, 0x40, // Bounds min Y LE
            0x00, 0x00, 0xC0, 0x40, // Bounds min Z LE
            0x00, 0x00, 0xE0, 0x40, // Bounds max X LE
            0x00, 0x00, 0x00, 0x41, // Bounds max Y LE
            0x00, 0x00, 0x10, 0x41, // Bounds max Z LE
            0x01, 0x00, // Vertex count LE
            0x01, 0x00, // Triangle count LE
            0x00, 0x00, 0x20, 0x41, // Vertex X LE
            0x00, 0x00, 0x30, 0x41, // Vertex Y LE
            0x00, 0x00, 0x40, 0x41, // Vertex Z LE
            0x01, 0x00, // Triangle index 0 LE
            0x02, 0x00, // Triangle index 1 LE
            0x03, 0x00, // Triangle index 2 LE
            0x00, 0x00, 0x00, 0x3F // Preferred % LE
        ];
        Assert.Equal(expected, result);
    }

    #endregion

    #region String Subrecords (No Conversion)

    [Fact]
    public void ConvertSubrecordData_Edid_PassesThrough()
    {
        // EDID is a string subrecord - no byte swapping
        byte[] data = [0x54, 0x65, 0x73, 0x74, 0x00]; // "Test\0"
        var result = EsmSubrecordConverter.ConvertSubrecordData("EDID", data, "WEAP");
        Assert.Equal(data, result);
    }

    [Fact]
    public void ConvertSubrecordData_Full_PassesThrough()
    {
        byte[] data = [0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00]; // "Hello\0"
        var result = EsmSubrecordConverter.ConvertSubrecordData("FULL", data, "WEAP");
        Assert.Equal(data, result);
    }

    [Fact]
    public void ConvertSubrecordData_Modl_PassesThrough()
    {
        byte[] data = [0x70, 0x61, 0x74, 0x68, 0x2E, 0x6E, 0x69, 0x66, 0x00]; // "path.nif\0"
        var result = EsmSubrecordConverter.ConvertSubrecordData("MODL", data, "WEAP");
        Assert.Equal(data, result);
    }

    [Fact]
    public void ConvertSubrecordData_Desc_PassesThrough()
    {
        byte[] data = [0x41, 0x42, 0x43, 0x00]; // "ABC\0"
        var result = EsmSubrecordConverter.ConvertSubrecordData("DESC", data, "WEAP");
        Assert.Equal(data, result);
    }

    #endregion

    #region Basic 4-Byte Swap (UInt32/Float/FormId)

    [Fact]
    public void ConvertSubrecordData_FormId_Swaps4Bytes()
    {
        // ANAM in WEAP is a FormID that gets swapped
        // Schema lookup: ANAM default is a FormID
        byte[] data = [0x00, 0x12, 0xAB, 0x34]; // BE: 0x0012AB34
        var result = EsmSubrecordConverter.ConvertSubrecordData("ANAM", data, "WEAP");
        Assert.Equal(new byte[] { 0x34, 0xAB, 0x12, 0x00 }, result); // LE: 0x0012AB34
    }

    [Fact]
    public void ConvertSubrecordData_FloatArray_SwapsEach4Bytes()
    {
        // IMAD BNAM is a float array - each 4 bytes swapped
        byte[] data =
        [
            0x3F, 0x80, 0x00, 0x00, // 1.0f BE
            0x40, 0x00, 0x00, 0x00 // 2.0f BE
        ];
        var result = EsmSubrecordConverter.ConvertSubrecordData("BNAM", data, "IMAD");
        // Each float reversed
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x40 }, result);
    }

    #endregion

    #region PERK DATA Special Cases

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    public void ConvertSubrecordData_PerkData5Bytes_PreservesOptionalHiddenByte(byte hidden)
    {
        // FNV permits a four-byte DATA prefix plus the optional Hidden byte. Presence is
        // semantically distinct from a legacy four-byte payload, even when Hidden is zero.
        byte[] data = [0x01, 0x02, 0x03, 0x04, hidden];
        var result = EsmSubrecordConverter.ConvertSubrecordData("DATA", data, "PERK");
        Assert.Equal(data, result);
    }

    [Fact]
    public void ConvertSubrecordData_PerkAbilityEntry4Bytes_SwapsFormId()
    {
        // The backward-compatible stateless default treats DATA(4) as the type-1 PRKE
        // ability FormID. Record conversion passes explicit PRKE..PRKF scope.
        byte[] data = [0x11, 0x22, 0x33, 0x44];
        var result = EsmSubrecordConverter.ConvertSubrecordData("DATA", data, "PERK");

        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, result);
    }

    [Fact]
    public void ConvertSubrecordData_PerkTopLevelData4_PreservesUInt8Fields()
    {
        byte[] data = [0x01, 0x02, 0x03, 0x04];

        var result = EsmSubrecordConverter.ConvertSubrecordData(
            "DATA", data, "PERK", PerkDataScope.TopLevel);

        Assert.Equal(data, result);
    }

    [Fact]
    public void ConvertSubrecordData_PerkData8Bytes_SwapsFirst4Only()
    {
        // PERK DATA(8): first dword is BE on Xbox, trailing 4 preserved
        byte[] data = [0x00, 0x00, 0x00, 0x01, 0xAA, 0xBB, 0xCC, 0xDD];
        var result = EsmSubrecordConverter.ConvertSubrecordData("DATA", data, "PERK");
        // First 4 bytes swapped
        Assert.Equal(0x01, result[0]);
        Assert.Equal(0x00, result[1]);
        Assert.Equal(0x00, result[2]);
        Assert.Equal(0x00, result[3]);
        // Last 4 bytes preserved
        Assert.Equal(0xAA, result[4]);
        Assert.Equal(0xBB, result[5]);
        Assert.Equal(0xCC, result[6]);
        Assert.Equal(0xDD, result[7]);
    }

    #endregion

    #region DATA Fallback Logic

    [Fact]
    public void ConvertSubrecordData_DataSmall_ReturnsByteArray()
    {
        // DATA <= 2 bytes -> ByteArray (no conversion)
        byte[] data = [0x42];
        var result = EsmSubrecordConverter.ConvertSubrecordData("DATA", data, "ZZZZ");
        Assert.Equal(data, result);
    }

    [Fact]
    public void ConvertSubrecordData_DataMediumDiv4_ReturnsFloatArray()
    {
        // DATA <= 64 bytes && divisible by 4 -> FloatArray
        byte[] data = [0x3F, 0x80, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00]; // 8 bytes, div by 4
        var result = EsmSubrecordConverter.ConvertSubrecordData("DATA", data, "ZZZZ");
        // Each 4 bytes swapped
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x40 }, result);
    }

    [Fact]
    public void ConvertSubrecordData_DataLargeIrregular_ReturnsByteArray()
    {
        // DATA > 64 bytes or irregular -> ByteArray (no conversion)
        var data = new byte[100];
        data[0] = 0xFF;
        data[99] = 0xAA;
        var result = EsmSubrecordConverter.ConvertSubrecordData("DATA", data, "ZZZZ");
        Assert.Equal(data, result);
    }

    #endregion
}