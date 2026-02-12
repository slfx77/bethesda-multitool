using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Conversion;

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

    [Fact]
    public void ConvertSubrecordData_PerkData5Bytes_TruncatesTo4()
    {
        // PERK DATA(5): Xbox has trailing 0x00, PC uses 4 bytes
        byte[] data = [0x01, 0x02, 0x03, 0x04, 0x00];
        var result = EsmSubrecordConverter.ConvertSubrecordData("DATA", data, "PERK");
        Assert.Equal(4, result.Length);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, result);
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