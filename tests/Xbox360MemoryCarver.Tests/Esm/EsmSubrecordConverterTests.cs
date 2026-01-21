using EsmAnalyzer.Conversion;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Esm;

/// <summary>
///     Tests for EsmSubrecordConverter.
///     These tests capture the expected behavior of subrecord conversion
///     to prevent regressions during refactoring.
/// </summary>
public sealed class EsmSubrecordConverterTests
{
    #region Simple FormID Subrecords

    [Fact]
    public void ConvertSubrecordData_NAME_SwapsFormId()
    {
        // NAME is a FormID (4 bytes) - should swap all 4 bytes
        var data = new byte[] { 0x00, 0x01, 0x23, 0x45 }; // FormID 0x00012345 in big-endian

        var result = EsmSubrecordConverter.ConvertSubrecordData("NAME", data, "REFR");

        // Should be little-endian: 0x45230100
        Assert.Equal(new byte[] { 0x45, 0x23, 0x01, 0x00 }, result);
    }

    [Fact]
    public void ConvertSubrecordData_TPLT_SwapsFormId()
    {
        var data = new byte[] { 0x00, 0x0A, 0xBC, 0xDE };

        var result = EsmSubrecordConverter.ConvertSubrecordData("TPLT", data, "NPC_");

        Assert.Equal(new byte[] { 0xDE, 0xBC, 0x0A, 0x00 }, result);
    }

    #endregion

    #region String Subrecords (No Conversion)

    [Fact]
    public void ConvertSubrecordData_EDID_NoConversion()
    {
        // EDID is a string - should not be modified
        var data = "TestEditorID\0"u8.ToArray();

        var result = EsmSubrecordConverter.ConvertSubrecordData("EDID", data, "NPC_");

        Assert.Equal(data, result);
    }

    [Fact]
    public void ConvertSubrecordData_FULL_NoConversion()
    {
        var data = "Full Name\0"u8.ToArray();

        var result = EsmSubrecordConverter.ConvertSubrecordData("FULL", data, "NPC_");

        Assert.Equal(data, result);
    }

    [Fact]
    public void ConvertSubrecordData_MODL_NoConversion()
    {
        var data = "meshes\\test\\model.nif\0"u8.ToArray();

        var result = EsmSubrecordConverter.ConvertSubrecordData("MODL", data, "WEAP");

        Assert.Equal(data, result);
    }

    [Fact]
    public void ConvertSubrecordData_ICON_NoConversion()
    {
        var data = "textures\\interface\\icon.dds\0"u8.ToArray();

        var result = EsmSubrecordConverter.ConvertSubrecordData("ICON", data, "MISC");

        Assert.Equal(data, result);
    }

    [Fact]
    public void ConvertSubrecordData_SCTX_NoConversion_InSCPT()
    {
        var data = "scn TestScript\r\nbegin gamemode\r\nend"u8.ToArray();

        var result = EsmSubrecordConverter.ConvertSubrecordData("SCTX", data, "SCPT");

        Assert.Equal(data, result);
    }

    #endregion

    #region OBND (Object Bounds)

    [Fact]
    public void ConvertSubrecordData_OBND_Swaps6Int16s()
    {
        // OBND: 6 x int16 for bounding box (min X,Y,Z + max X,Y,Z)
        // Big-endian: -10, -20, -30, 10, 20, 30
        var data = new byte[]
        {
            0xFF, 0xF6, // -10 big-endian
            0xFF, 0xEC, // -20 big-endian
            0xFF, 0xE2, // -30 big-endian
            0x00, 0x0A, // +10 big-endian
            0x00, 0x14, // +20 big-endian
            0x00, 0x1E  // +30 big-endian
        };

        var result = EsmSubrecordConverter.ConvertSubrecordData("OBND", data, "WEAP");

        // Verify each int16 was swapped
        Assert.Equal(-10, BitConverter.ToInt16(result, 0));
        Assert.Equal(-20, BitConverter.ToInt16(result, 2));
        Assert.Equal(-30, BitConverter.ToInt16(result, 4));
        Assert.Equal(10, BitConverter.ToInt16(result, 6));
        Assert.Equal(20, BitConverter.ToInt16(result, 8));
        Assert.Equal(30, BitConverter.ToInt16(result, 10));
    }

    #endregion

    #region ACBS (Actor Base Stats)

    [Fact]
    public void ConvertSubrecordData_ACBS_24Bytes_ConvertsAllFields()
    {
        // ACBS: 24 bytes
        // uint32 flags, uint16 fatigue, uint16 barter, int16 level, uint16 calcMin,
        // uint16 calcMax, uint16 speedMult, float karma, uint16 dispBase, uint16 templateFlags
        // All values in big-endian
        var data = new byte[]
        {
            0x00, 0x00, 0x00, 0x01, // Flags (BE: 0x00000001)
            0x00, 0x64, // Fatigue (BE: 100)
            0x00, 0xC8, // Barter Gold (BE: 200)
            0x00, 0x0A, // Level (BE: 10)
            0x00, 0x01, // Calc min (BE: 1)
            0x00, 0x14, // Calc max (BE: 20)
            0x00, 0x64, // Speed mult (BE: 100)
            0x00, 0x00, 0x00, 0x00, // Karma (float, 0.0)
            0x00, 0x32, // Disp base (BE: 50)
            0x00, 0x00  // Template flags (BE: 0)
        };

        var result = EsmSubrecordConverter.ConvertSubrecordData("ACBS", data, "NPC_");

        // Check flags (uint32) - swapped to LE
        Assert.Equal(1u, BitConverter.ToUInt32(result, 0));
        // Check fatigue (uint16) - swapped to LE
        Assert.Equal(100, BitConverter.ToUInt16(result, 4));
        // Check barter gold (uint16) - swapped to LE
        Assert.Equal(200, BitConverter.ToUInt16(result, 6));
        // Check level (int16) - swapped to LE
        Assert.Equal(10, BitConverter.ToInt16(result, 8));
    }

    #endregion

    #region CNTO (Container Item)

    [Fact]
    public void ConvertSubrecordData_CNTO_8Bytes_SwapsFormIdAndCount()
    {
        // CNTO: FormID (4) + Count (4)
        // Big-endian bytes: 0x00012345 as FormID, 0x00000005 as Count
        var data = new byte[]
        {
            0x00, 0x01, 0x23, 0x45, // FormID (BE: 0x00012345)
            0x00, 0x00, 0x00, 0x05  // Count (BE: 5)
        };

        var result = EsmSubrecordConverter.ConvertSubrecordData("CNTO", data, "CONT");

        // After swap to LE, BitConverter reads correctly
        Assert.Equal(0x00012345u, BitConverter.ToUInt32(result, 0)); // FormID swapped
        Assert.Equal(5u, BitConverter.ToUInt32(result, 4)); // Count swapped
    }

    #endregion

    #region CTDA (Condition Data)

    [Fact]
    public void ConvertSubrecordData_CTDA_28Bytes_ConvertsCorrectly()
    {
        // CTDA: 28 bytes of condition data
        // byte type, 3 unused, float compValue, uint16 function, 2 unused,
        // FormID param1, FormID param2, uint32 runOn, FormID reference
        // All multi-byte values in big-endian
        var data = new byte[]
        {
            0x00, 0x00, 0x00, 0x00, // Type + unused (bytes, no swap)
            0x3F, 0x80, 0x00, 0x00, // CompValue = 1.0f (BE)
            0x00, 0x49, // Function = 73 (BE)
            0x00, 0x00, // Unused
            0x00, 0x01, 0x00, 0x00, // Param1 FormID (BE: 0x00010000)
            0x00, 0x02, 0x00, 0x00, // Param2 FormID (BE: 0x00020000)
            0x00, 0x00, 0x00, 0x00, // RunOn (BE: 0)
            0x00, 0x03, 0x00, 0x00  // Reference FormID (BE: 0x00030000)
        };

        var result = EsmSubrecordConverter.ConvertSubrecordData("CTDA", data, "NPC_");

        // Check compValue (float at offset 4) - swapped to LE
        Assert.Equal(1.0f, BitConverter.ToSingle(result, 4));
        // Check function (uint16 at offset 8) - swapped to LE
        Assert.Equal(73, BitConverter.ToUInt16(result, 8));
        // Check param1 (FormID at offset 12) - swapped to LE
        Assert.Equal(0x00010000u, BitConverter.ToUInt32(result, 12));
    }

    #endregion

    #region MODT (Model Texture Hashes)

    [Fact]
    public void ConvertSubrecordData_MODT_Swaps8ByteHashes()
    {
        // MODT: Array of 8-byte texture hashes
        var data = new byte[]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, // Hash 1
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18  // Hash 2
        };

        var result = EsmSubrecordConverter.ConvertSubrecordData("MODT", data, "WEAP");

        // Each 8-byte hash should be reversed
        Assert.Equal(new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 }, result[..8]);
        Assert.Equal(new byte[] { 0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11 }, result[8..16]);
    }

    #endregion

    #region FGGS/FGGA/FGTS (FaceGen Data)

    [Fact]
    public void ConvertSubrecordData_FGGS_200Bytes_SwapsAllFloats()
    {
        // FGGS: 50 floats = 200 bytes (FaceGen Symmetric Geometry)
        var data = new byte[200];
        // Set first float to 1.0f in big-endian
        data[0] = 0x3F;
        data[1] = 0x80;
        data[2] = 0x00;
        data[3] = 0x00;

        var result = EsmSubrecordConverter.ConvertSubrecordData("FGGS", data, "NPC_");

        // First float should now read as 1.0f in little-endian
        Assert.Equal(1.0f, BitConverter.ToSingle(result, 0));
    }

    #endregion

    #region Float Arrays

    [Fact]
    public void ConvertSubrecordData_FloatArray_SwapsAllFloats()
    {
        // Generic float array test (like RPLD)
        var data = new byte[]
        {
            0x40, 0x00, 0x00, 0x00, // 2.0f big-endian
            0x40, 0x80, 0x00, 0x00, // 4.0f big-endian
            0x40, 0xC0, 0x00, 0x00  // 6.0f big-endian
        };

        var result = EsmSubrecordConverter.ConvertSubrecordData("RPLD", data, "REGN");

        Assert.Equal(2.0f, BitConverter.ToSingle(result, 0));
        Assert.Equal(4.0f, BitConverter.ToSingle(result, 4));
        Assert.Equal(6.0f, BitConverter.ToSingle(result, 8));
    }

    #endregion

    #region Record-Type Specific Behavior

    [Fact]
    public void ConvertSubrecordData_RNAM_INFO_NoConversion_String()
    {
        // RNAM in INFO is a string (result name)
        var data = "Result Text\0"u8.ToArray();

        var result = EsmSubrecordConverter.ConvertSubrecordData("RNAM", data, "INFO");

        Assert.Equal(data, result);
    }

    [Fact]
    public void ConvertSubrecordData_RNAM_CHAL_NoConversion_String()
    {
        // RNAM in CHAL is also a string
        var data = "Challenge Result\0"u8.ToArray();

        var result = EsmSubrecordConverter.ConvertSubrecordData("RNAM", data, "CHAL");

        Assert.Equal(data, result);
    }

    [Fact]
    public void ConvertSubrecordData_RNAM_Other_SwapsFormId()
    {
        // RNAM in other records is a FormID
        var data = new byte[] { 0x00, 0x01, 0x23, 0x45 };

        var result = EsmSubrecordConverter.ConvertSubrecordData("RNAM", data, "NPC_");

        Assert.Equal(new byte[] { 0x45, 0x23, 0x01, 0x00 }, result);
    }

    [Fact]
    public void ConvertSubrecordData_MNAM_FACT_NoConversion_String()
    {
        // MNAM in FACT is male rank title string
        var data = "Knight\0"u8.ToArray();

        var result = EsmSubrecordConverter.ConvertSubrecordData("MNAM", data, "FACT");

        Assert.Equal(data, result);
    }

    [Fact]
    public void ConvertSubrecordData_FNAM_FACT_NoConversion_String()
    {
        // FNAM in FACT is female rank title string
        var data = "Dame\0"u8.ToArray();

        var result = EsmSubrecordConverter.ConvertSubrecordData("FNAM", data, "FACT");

        Assert.Equal(data, result);
    }

    #endregion

    #region SCHR (Script Header)

    [Fact]
    public void ConvertSubrecordData_SCHR_20Bytes_ConvertsCorrectly()
    {
        // SCHR: 20 bytes when full header
        // 4 unused, uint32 refCount, uint32 compiledSize, uint32 varCount,
        // uint16 type, uint16 flags
        var data = new byte[]
        {
            0x00, 0x00, 0x00, 0x00, // Unused
            0x00, 0x00, 0x00, 0x05, // RefCount = 5
            0x00, 0x00, 0x00, 0x64, // CompiledSize = 100
            0x00, 0x00, 0x00, 0x0A, // VarCount = 10
            0x00, 0x01, // Type = 1
            0x00, 0x00  // Flags = 0
        };

        var result = EsmSubrecordConverter.ConvertSubrecordData("SCHR", data, "SCPT");

        Assert.Equal(5u, BitConverter.ToUInt32(result, 4));   // RefCount
        Assert.Equal(100u, BitConverter.ToUInt32(result, 8)); // CompiledSize
        Assert.Equal(10u, BitConverter.ToUInt32(result, 12)); // VarCount
        Assert.Equal(1, BitConverter.ToUInt16(result, 16));   // Type
    }

    [Fact]
    public void ConvertSubrecordData_SCHR_4Bytes_SwapsFormId()
    {
        // SCHR: 4 bytes = just a FormID
        var data = new byte[] { 0x00, 0x01, 0x23, 0x45 };

        var result = EsmSubrecordConverter.ConvertSubrecordData("SCHR", data, "QUST");

        Assert.Equal(new byte[] { 0x45, 0x23, 0x01, 0x00 }, result);
    }

    #endregion

    #region XTEL (Teleport Destination)

    [Fact]
    public void ConvertSubrecordData_XTEL_32Bytes_ConvertsAllFields()
    {
        // XTEL: Door FormID + position (3 floats) + rotation (3 floats) + flags
        var data = new byte[32];
        // Set door FormID (BE: 0x00012345)
        data[0] = 0x00; data[1] = 0x01; data[2] = 0x23; data[3] = 0x45;
        // Set X position to 100.0f in big-endian
        var posX = BitConverter.GetBytes(100.0f);
        if (BitConverter.IsLittleEndian) Array.Reverse(posX);
        Array.Copy(posX, 0, data, 4, 4);

        var result = EsmSubrecordConverter.ConvertSubrecordData("XTEL", data, "REFR");

        // After swap to LE
        Assert.Equal(0x00012345u, BitConverter.ToUInt32(result, 0)); // Door FormID
        Assert.Equal(100.0f, BitConverter.ToSingle(result, 4)); // X position
    }

    #endregion

    #region HNAM Context-Dependent

    [Fact]
    public void ConvertSubrecordData_HNAM_4Bytes_SwapsFormId()
    {
        var data = new byte[] { 0x00, 0x01, 0x23, 0x45 };

        var result = EsmSubrecordConverter.ConvertSubrecordData("HNAM", data, "NPC_");

        Assert.Equal(new byte[] { 0x45, 0x23, 0x01, 0x00 }, result);
    }

    [Fact]
    public void ConvertSubrecordData_HNAM_8Bytes_SwapsTwoFormIds()
    {
        // Two FormIDs in big-endian - RACE record HNAM supports variable-length FormID arrays
        var data = new byte[]
        {
            0x00, 0x01, 0x23, 0x45, // FormID1 (BE: 0x00012345)
            0x00, 0xAB, 0xCD, 0xEF  // FormID2 (BE: 0x00ABCDEF)
        };

        var result = EsmSubrecordConverter.ConvertSubrecordData("HNAM", data, "RACE");

        // After swap, BitConverter reads the correct LE values
        Assert.Equal(0x00012345u, BitConverter.ToUInt32(result, 0));
        Assert.Equal(0x00ABCDEFu, BitConverter.ToUInt32(result, 4));
    }

    #endregion

    #region Idempotency (Double Conversion Should Work)

    [Fact]
    public void ConvertSubrecordData_DoubleConversion_NAME()
    {
        // Converting twice should give original data (swap is its own inverse)
        var original = new byte[] { 0x00, 0x01, 0x23, 0x45 };
        var data = (byte[])original.Clone();

        var result1 = EsmSubrecordConverter.ConvertSubrecordData("NAME", data, "REFR");
        var result2 = EsmSubrecordConverter.ConvertSubrecordData("NAME", result1, "REFR");

        Assert.Equal(original, result2);
    }

    [Fact]
    public void ConvertSubrecordData_DoubleConversion_OBND()
    {
        var original = new byte[] { 0xFF, 0xF6, 0xFF, 0xEC, 0xFF, 0xE2, 0x00, 0x0A, 0x00, 0x14, 0x00, 0x1E };
        var data = (byte[])original.Clone();

        var result1 = EsmSubrecordConverter.ConvertSubrecordData("OBND", data, "WEAP");
        var result2 = EsmSubrecordConverter.ConvertSubrecordData("OBND", result1, "WEAP");

        Assert.Equal(original, result2);
    }

    #endregion
}

