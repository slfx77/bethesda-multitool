using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Tests for RuntimeStructReader using synthetic memory-mapped data that simulates
///     an Xbox 360 memory dump with heap memory at VA 0x40000000.
/// </summary>
public class RuntimeStructReaderTests(ITestOutputHelper output) : IDisposable
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>
    ///     Size of the synthetic dump file. Large enough for all test scenarios.
    /// </summary>
    private const int DataSize = 8192;

    /// <summary>
    ///     Xbox 360 heap base VA. VaToLong(0x40000000) = 0x40000000 (positive, no sign extension).
    /// </summary>
    private const uint HeapBaseVa = 0x40000000;

    // Struct constants mirrored from RuntimeStructReader for test clarity
    private const int AmmoStructSize = 236;
    private const int AmmoValueOffset = 140;
    private const byte AmmoFormType = 0x29;

    private const int MiscStructSize = 188;
    private const int MiscValueOffset = 136;
    private const int MiscWeightOffset = 144;
    private const byte MiscFormType = 0x1F;

    private const int ModelPathBSStringTOffset = 80;

    private string? _tempFilePath;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;

    #region Test Fixture Helpers

    /// <summary>
    ///     Creates a RuntimeStructReader backed by a temporary file containing the given byte array.
    ///     The MinidumpInfo maps VA 0x40000000 to file offset 0, so file offset == (VA - 0x40000000).
    /// </summary>
    private RuntimeStructReader CreateReader(byte[] data)
    {
        _tempFilePath = Path.GetTempFileName();
        File.WriteAllBytes(_tempFilePath, data);

        _mmf = MemoryMappedFile.CreateFromFile(_tempFilePath, FileMode.Open, null, data.Length,
            MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, data.Length, MemoryMappedFileAccess.Read);

        var minidumpInfo = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03, // PowerPC
            NumberOfStreams = 1,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(HeapBaseVa),
                    Size = data.Length,
                    FileOffset = 0
                }
            ]
        };

        return new RuntimeStructReader(_accessor, data.Length, minidumpInfo);
    }

    /// <summary>
    ///     Write a big-endian uint32 into a byte array at the specified offset.
    /// </summary>
    private static void WriteUInt32BE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    /// <summary>
    ///     Write a big-endian int32 into a byte array at the specified offset.
    /// </summary>
    private static void WriteInt32BE(byte[] data, int offset, int value)
    {
        WriteUInt32BE(data, offset, (uint)value);
    }

    /// <summary>
    ///     Write a big-endian uint16 into a byte array at the specified offset.
    /// </summary>
    private static void WriteUInt16BE(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    /// <summary>
    ///     Write a big-endian float into a byte array at the specified offset.
    /// </summary>
    private static void WriteFloatBE(byte[] data, int offset, float value)
    {
        var bits = BitConverter.SingleToUInt32Bits(value);
        WriteUInt32BE(data, offset, bits);
    }

    /// <summary>
    ///     Write a TESForm header at the given file offset.
    ///     Layout: byte[0-3] = vtable pointer, byte[4] = formType, byte[12-15] = formId (BE).
    /// </summary>
    private static void WriteTesFormHeader(byte[] data, int fileOffset, uint vtable, byte formType, uint formId)
    {
        WriteUInt32BE(data, fileOffset, vtable);
        data[fileOffset + 4] = formType;
        WriteUInt32BE(data, fileOffset + 12, formId);
    }

    /// <summary>
    ///     Write a BSStringT at the given file offset within a struct.
    ///     BSStringT layout: [4 bytes pointer BE][2 bytes length BE][2 bytes unused].
    ///     The string data is placed at stringDataFileOffset in the file.
    /// </summary>
    private static void WriteBSStringT(byte[] data, int bstFileOffset, uint stringVa, ushort stringLength,
        int stringDataFileOffset, string text)
    {
        WriteUInt32BE(data, bstFileOffset, stringVa);
        WriteUInt16BE(data, bstFileOffset + 4, stringLength);

        var textBytes = Encoding.ASCII.GetBytes(text);
        Array.Copy(textBytes, 0, data, stringDataFileOffset, Math.Min(textBytes.Length, stringLength));
    }

    /// <summary>
    ///     Convert a file offset to the corresponding Xbox 360 VA for our test memory region.
    /// </summary>
    private static uint FileOffsetToVa(int fileOffset)
    {
        return HeapBaseVa + (uint)fileOffset;
    }

    /// <summary>
    ///     Build a RuntimeEditorIdEntry with standard test values.
    /// </summary>
    private static RuntimeEditorIdEntry MakeEntry(string editorId, uint formId, byte formType, long? tesFormOffset,
        string? displayName = null)
    {
        return new RuntimeEditorIdEntry
        {
            EditorId = editorId,
            FormId = formId,
            FormType = formType,
            TesFormOffset = tesFormOffset,
            DisplayName = displayName
        };
    }

    #endregion

    #region ReadRuntimeAmmo Tests

    [Fact]
    public void ReadRuntimeAmmo_ValidEntry_ReturnsAmmoWithCorrectValue()
    {
        // Arrange: place an AMMO struct at file offset 0
        var data = new byte[DataSize];
        const uint formId = 0x00012345;
        const int expectedValue = 3;
        const int structOffset = 0;

        WriteTesFormHeader(data, structOffset, 0x82010000, AmmoFormType, formId);
        WriteInt32BE(data, structOffset + AmmoValueOffset, expectedValue);

        var reader = CreateReader(data);
        var entry = MakeEntry("Ammo10mm", formId, AmmoFormType, structOffset, "10mm Round");

        // Act
        var result = reader.ReadRuntimeAmmo(entry);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(formId, result.FormId);
        Assert.Equal("Ammo10mm", result.EditorId);
        Assert.Equal("10mm Round", result.FullName);
        Assert.Equal((uint)expectedValue, result.Value);
        Assert.True(result.IsBigEndian);
        Assert.Equal(structOffset, result.Offset);

        _output.WriteLine($"Ammo value={result.Value}, formId=0x{result.FormId:X8}");
    }

    [Fact]
    public void ReadRuntimeAmmo_ValueOutOfRange_ClampsToZero()
    {
        // Arrange: value > 1,000,000 should be clamped to 0
        var data = new byte[DataSize];
        const uint formId = 0x000ABCDE;
        const int structOffset = 0;

        WriteTesFormHeader(data, structOffset, 0x82010000, AmmoFormType, formId);
        WriteInt32BE(data, structOffset + AmmoValueOffset, 2_000_000);

        var reader = CreateReader(data);
        var entry = MakeEntry("AmmoOverprice", formId, AmmoFormType, structOffset);

        // Act
        var result = reader.ReadRuntimeAmmo(entry);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(0u, result.Value);

        _output.WriteLine("Out-of-range value clamped to 0 as expected");
    }

    [Fact]
    public void ReadRuntimeAmmo_FormIdMismatch_ReturnsNull()
    {
        // Arrange: entry says formId=0x111, but struct has formId=0x222
        var data = new byte[DataSize];
        const int structOffset = 0;

        WriteTesFormHeader(data, structOffset, 0x82010000, AmmoFormType, 0x00000222);

        var reader = CreateReader(data);
        var entry = MakeEntry("AmmoMismatch", 0x00000111, AmmoFormType, structOffset);

        // Act
        var result = reader.ReadRuntimeAmmo(entry);

        // Assert
        Assert.Null(result);

        _output.WriteLine("FormID mismatch correctly returns null");
    }

    [Fact]
    public void ReadRuntimeAmmo_WrongFormType_ReturnsNull()
    {
        // Arrange: form type 0x28 (WEAP) instead of 0x29 (AMMO)
        var data = new byte[DataSize];
        const uint formId = 0x00012345;
        const int structOffset = 0;

        WriteTesFormHeader(data, structOffset, 0x82010000, AmmoFormType, formId);

        var reader = CreateReader(data);
        var entry = MakeEntry("NotAmmo", formId, 0x28, structOffset); // wrong type

        // Act
        var result = reader.ReadRuntimeAmmo(entry);

        // Assert
        Assert.Null(result);

        _output.WriteLine("Wrong form type correctly returns null");
    }

    [Fact]
    public void ReadRuntimeAmmo_WithModelPath_ReturnsModelPath()
    {
        // Arrange: AMMO struct with BSStringT model path at offset 80
        var data = new byte[DataSize];
        const uint formId = 0x000AAAAA;
        const int structOffset = 0;
        const string modelPath = "meshes\\weapons\\10mmPistol\\10mmAmmo.nif";

        WriteTesFormHeader(data, structOffset, 0x82010000, AmmoFormType, formId);
        WriteInt32BE(data, structOffset + AmmoValueOffset, 5);

        // Place string data at file offset 1024 (well past the struct)
        const int stringDataOffset = 1024;
        var stringVa = FileOffsetToVa(stringDataOffset);

        WriteBSStringT(data, structOffset + ModelPathBSStringTOffset, stringVa,
            (ushort)modelPath.Length, stringDataOffset, modelPath);

        var reader = CreateReader(data);
        var entry = MakeEntry("Ammo10mm", formId, AmmoFormType, structOffset);

        // Act
        var result = reader.ReadRuntimeAmmo(entry);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(modelPath, result.ModelPath);

        _output.WriteLine($"Model path: {result.ModelPath}");
    }

    #endregion

    #region ReadRuntimeMiscItem Tests

    [Fact]
    public void ReadRuntimeMiscItem_ValidEntry_ReturnsValueAndWeight()
    {
        // Arrange
        var data = new byte[DataSize];
        const uint formId = 0x000BBBBB;
        const int structOffset = 0;
        const int expectedValue = 100;
        const float expectedWeight = 2.5f;

        WriteTesFormHeader(data, structOffset, 0x82010000, MiscFormType, formId);
        WriteInt32BE(data, structOffset + MiscValueOffset, expectedValue);
        WriteFloatBE(data, structOffset + MiscWeightOffset, expectedWeight);

        var reader = CreateReader(data);
        var entry = MakeEntry("MiscSensorModule", formId, MiscFormType, structOffset, "Sensor Module");

        // Act
        var result = reader.ReadRuntimeMiscItem(entry);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(formId, result.FormId);
        Assert.Equal("MiscSensorModule", result.EditorId);
        Assert.Equal("Sensor Module", result.FullName);
        Assert.Equal(expectedValue, result.Value);
        Assert.Equal(expectedWeight, result.Weight, precision: 3);
        Assert.True(result.IsBigEndian);

        _output.WriteLine($"MiscItem value={result.Value}, weight={result.Weight:F2}");
    }

    [Fact]
    public void ReadRuntimeMiscItem_WeightOutOfRange_ClampsToZero()
    {
        // Arrange: weight > 500 should be clamped to 0 by ReadValidatedFloat
        var data = new byte[DataSize];
        const uint formId = 0x000CCCCC;
        const int structOffset = 0;

        WriteTesFormHeader(data, structOffset, 0x82010000, MiscFormType, formId);
        WriteInt32BE(data, structOffset + MiscValueOffset, 10);
        WriteFloatBE(data, structOffset + MiscWeightOffset, 999.0f); // out of range (max 500)

        var reader = CreateReader(data);
        var entry = MakeEntry("MiscHeavy", formId, MiscFormType, structOffset);

        // Act
        var result = reader.ReadRuntimeMiscItem(entry);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(0f, result.Weight);

        _output.WriteLine("Out-of-range weight clamped to 0 as expected");
    }

    [Fact]
    public void ReadRuntimeMiscItem_NaNWeight_ClampsToZero()
    {
        // Arrange: NaN float should be caught by ReadValidatedFloat
        var data = new byte[DataSize];
        const uint formId = 0x000DDDDD;
        const int structOffset = 0;

        WriteTesFormHeader(data, structOffset, 0x82010000, MiscFormType, formId);
        WriteInt32BE(data, structOffset + MiscValueOffset, 5);

        // Write NaN: 0x7FC00000 in IEEE 754
        WriteUInt32BE(data, structOffset + MiscWeightOffset, 0x7FC00000);

        var reader = CreateReader(data);
        var entry = MakeEntry("MiscNaN", formId, MiscFormType, structOffset);

        // Act
        var result = reader.ReadRuntimeMiscItem(entry);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(0f, result.Weight);

        _output.WriteLine("NaN weight clamped to 0 as expected");
    }

    #endregion

    #region FollowPointerToFormId Tests (via ReadRuntimeAmmo/Weapon model pointer paths)

    [Fact]
    public void ReadRuntimeAmmo_PointerChain_NullPointerInBSStringT_ReturnsNullModelPath()
    {
        // Arrange: BSStringT with zero pointer should yield null model path
        var data = new byte[DataSize];
        const uint formId = 0x000EEEEE;
        const int structOffset = 0;

        WriteTesFormHeader(data, structOffset, 0x82010000, AmmoFormType, formId);
        WriteInt32BE(data, structOffset + AmmoValueOffset, 1);

        // BSStringT at offset 80: pointer = 0, length = 0
        WriteUInt32BE(data, structOffset + ModelPathBSStringTOffset, 0);
        WriteUInt16BE(data, structOffset + ModelPathBSStringTOffset + 4, 0);

        var reader = CreateReader(data);
        var entry = MakeEntry("AmmoNoModel", formId, AmmoFormType, structOffset);

        // Act
        var result = reader.ReadRuntimeAmmo(entry);

        // Assert
        Assert.NotNull(result);
        Assert.Null(result.ModelPath);

        _output.WriteLine("Null BSStringT pointer returns null model path as expected");
    }

    [Fact]
    public void ReadRuntimeAmmo_BSStringTPointerOutsideMemory_ReturnsNullModelPath()
    {
        // Arrange: BSStringT pointer points to VA outside our captured region
        var data = new byte[DataSize];
        const uint formId = 0x000FFFFF;
        const int structOffset = 0;

        WriteTesFormHeader(data, structOffset, 0x82010000, AmmoFormType, formId);
        WriteInt32BE(data, structOffset + AmmoValueOffset, 2);

        // BSStringT pointer to VA 0x50000000 (outside heap region 0x40000000-0x40002000)
        WriteUInt32BE(data, structOffset + ModelPathBSStringTOffset, 0x50000000);
        WriteUInt16BE(data, structOffset + ModelPathBSStringTOffset + 4, 10);

        var reader = CreateReader(data);
        var entry = MakeEntry("AmmoBadPtr", formId, AmmoFormType, structOffset);

        // Act
        var result = reader.ReadRuntimeAmmo(entry);

        // Assert
        Assert.NotNull(result);
        Assert.Null(result.ModelPath);

        _output.WriteLine("Out-of-region BSStringT pointer returns null model path");
    }

    #endregion

    #region Edge Cases: Null/Invalid TesFormOffset

    [Fact]
    public void ReadRuntimeAmmo_NullTesFormOffset_ReturnsNull()
    {
        var data = new byte[DataSize];
        var reader = CreateReader(data);
        var entry = MakeEntry("AmmoNull", 0x00011111, AmmoFormType, tesFormOffset: null);

        var result = reader.ReadRuntimeAmmo(entry);

        Assert.Null(result);

        _output.WriteLine("Null TesFormOffset correctly returns null");
    }

    [Fact]
    public void ReadRuntimeMiscItem_NullTesFormOffset_ReturnsNull()
    {
        var data = new byte[DataSize];
        var reader = CreateReader(data);
        var entry = MakeEntry("MiscNull", 0x00022222, MiscFormType, tesFormOffset: null);

        var result = reader.ReadRuntimeMiscItem(entry);

        Assert.Null(result);

        _output.WriteLine("Null TesFormOffset correctly returns null for MiscItem");
    }

    [Fact]
    public void ReadRuntimeAmmo_TesFormOffsetNearEndOfFile_ReturnsNull()
    {
        // Arrange: offset is valid but there's not enough room for the full struct
        var data = new byte[DataSize];
        var reader = CreateReader(data);

        // Place the struct offset so that offset + AmmoStructSize > fileSize
        long offsetNearEnd = DataSize - 10;
        var entry = MakeEntry("AmmoTruncated", 0x00033333, AmmoFormType, offsetNearEnd);

        var result = reader.ReadRuntimeAmmo(entry);

        Assert.Null(result);

        _output.WriteLine("Struct extending past end of file correctly returns null");
    }

    [Fact]
    public void ReadRuntimeMiscItem_TesFormOffsetNearEndOfFile_ReturnsNull()
    {
        var data = new byte[DataSize];
        var reader = CreateReader(data);

        long offsetNearEnd = DataSize - 10;
        var entry = MakeEntry("MiscTruncated", 0x00044444, MiscFormType, offsetNearEnd);

        var result = reader.ReadRuntimeMiscItem(entry);

        Assert.Null(result);

        _output.WriteLine("MiscItem struct past end of file correctly returns null");
    }

    [Fact]
    public void ReadRuntimeAmmo_ZeroLengthRemaining_ReturnsNull()
    {
        // Arrange: offset exactly at end of file
        var data = new byte[DataSize];
        var reader = CreateReader(data);

        var entry = MakeEntry("AmmoAtEnd", 0x00055555, AmmoFormType, tesFormOffset: DataSize);

        var result = reader.ReadRuntimeAmmo(entry);

        Assert.Null(result);

        _output.WriteLine("Struct at exact end of file correctly returns null");
    }

    #endregion

    #region ReadBSStringT Tests (via ReadRuntimeAmmo model path)

    [Fact]
    public void ReadBSStringT_ValidString_ReturnsCorrectText()
    {
        // Arrange: place a valid BSStringT and string data
        var data = new byte[DataSize];
        const uint formId = 0x000A1111;
        const int structOffset = 0;
        const string expectedPath = "meshes\\armor\\helmet.nif";

        WriteTesFormHeader(data, structOffset, 0x82010000, AmmoFormType, formId);
        WriteInt32BE(data, structOffset + AmmoValueOffset, 1);

        // String data at file offset 2048
        const int stringFileOffset = 2048;
        var stringVa = FileOffsetToVa(stringFileOffset);

        WriteBSStringT(data, structOffset + ModelPathBSStringTOffset,
            stringVa, (ushort)expectedPath.Length, stringFileOffset, expectedPath);

        var reader = CreateReader(data);

        // Test ReadBSStringT directly (it's public)
        var result = reader.ReadBSStringT(structOffset, ModelPathBSStringTOffset);

        Assert.Equal(expectedPath, result);

        _output.WriteLine($"ReadBSStringT returned: {result}");
    }

    [Fact]
    public void ReadBSStringT_ZeroLength_ReturnsNull()
    {
        var data = new byte[DataSize];
        const int structOffset = 0;

        // BSStringT with valid pointer but zero length
        WriteUInt32BE(data, structOffset + ModelPathBSStringTOffset, FileOffsetToVa(1024));
        WriteUInt16BE(data, structOffset + ModelPathBSStringTOffset + 4, 0);

        var reader = CreateReader(data);
        var result = reader.ReadBSStringT(structOffset, ModelPathBSStringTOffset);

        Assert.Null(result);

        _output.WriteLine("Zero-length BSStringT correctly returns null");
    }

    [Fact]
    public void ReadBSStringT_OffsetPastEndOfFile_ReturnsNull()
    {
        var data = new byte[DataSize];
        var reader = CreateReader(data);

        // Try to read BSStringT from beyond file boundary
        var result = reader.ReadBSStringT(DataSize - 2, ModelPathBSStringTOffset);

        Assert.Null(result);

        _output.WriteLine("BSStringT past end of file correctly returns null");
    }

    #endregion

    #region Multiple Structs at Different Offsets

    [Fact]
    public void ReadRuntimeAmmo_TwoStructsAtDifferentOffsets_BothReadCorrectly()
    {
        // Arrange: place two AMMO structs at different offsets in the same file
        var data = new byte[DataSize];

        const uint formId1 = 0x000A0001;
        const uint formId2 = 0x000A0002;
        const int structOffset1 = 0;
        const int structOffset2 = 512; // well past first struct (AmmoStructSize=236)
        const int value1 = 10;
        const int value2 = 25;

        WriteTesFormHeader(data, structOffset1, 0x82010000, AmmoFormType, formId1);
        WriteInt32BE(data, structOffset1 + AmmoValueOffset, value1);

        WriteTesFormHeader(data, structOffset2, 0x82010000, AmmoFormType, formId2);
        WriteInt32BE(data, structOffset2 + AmmoValueOffset, value2);

        var reader = CreateReader(data);

        var entry1 = MakeEntry("Ammo1", formId1, AmmoFormType, structOffset1);
        var entry2 = MakeEntry("Ammo2", formId2, AmmoFormType, structOffset2);

        // Act
        var result1 = reader.ReadRuntimeAmmo(entry1);
        var result2 = reader.ReadRuntimeAmmo(entry2);

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal((uint)value1, result1.Value);
        Assert.Equal((uint)value2, result2.Value);
        Assert.Equal(formId1, result1.FormId);
        Assert.Equal(formId2, result2.FormId);

        _output.WriteLine($"Struct1: formId=0x{result1.FormId:X8} value={result1.Value}");
        _output.WriteLine($"Struct2: formId=0x{result2.FormId:X8} value={result2.Value}");
    }

    #endregion

    #region Negative Value Tests

    [Fact]
    public void ReadRuntimeAmmo_NegativeValue_ClampsToZero()
    {
        // Arrange: negative value (signed int32 < 0) should be clamped to 0
        var data = new byte[DataSize];
        const uint formId = 0x000B1111;
        const int structOffset = 0;

        WriteTesFormHeader(data, structOffset, 0x82010000, AmmoFormType, formId);
        WriteInt32BE(data, structOffset + AmmoValueOffset, -500);

        var reader = CreateReader(data);
        var entry = MakeEntry("AmmoNegative", formId, AmmoFormType, structOffset);

        // Act
        var result = reader.ReadRuntimeAmmo(entry);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(0u, result.Value);

        _output.WriteLine("Negative value clamped to 0 as expected");
    }

    [Fact]
    public void ReadRuntimeMiscItem_NegativeValue_ClampsToZero()
    {
        var data = new byte[DataSize];
        const uint formId = 0x000B2222;
        const int structOffset = 0;

        WriteTesFormHeader(data, structOffset, 0x82010000, MiscFormType, formId);
        WriteInt32BE(data, structOffset + MiscValueOffset, -100);
        WriteFloatBE(data, structOffset + MiscWeightOffset, 1.0f);

        var reader = CreateReader(data);
        var entry = MakeEntry("MiscNegVal", formId, MiscFormType, structOffset);

        var result = reader.ReadRuntimeMiscItem(entry);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value);
        Assert.Equal(1.0f, result.Weight, precision: 3);

        _output.WriteLine("Negative misc value clamped to 0, weight preserved");
    }

    #endregion

    #region VaToLong Verification

    [Fact]
    public void VaToLong_HeapAddress_NoSignExtension()
    {
        // Xbox 360 heap addresses (0x40000000-0x50000000) have bit 31 clear,
        // so sign extension via unchecked((int)address) keeps them positive.
        var result = Xbox360MemoryUtils.VaToLong(0x40000000);
        Assert.Equal(0x40000000L, result);

        var resultEnd = Xbox360MemoryUtils.VaToLong(0x4FFFFFFF);
        Assert.Equal(0x4FFFFFFFL, resultEnd);

        _output.WriteLine($"VaToLong(0x40000000) = 0x{result:X16}");
        _output.WriteLine($"VaToLong(0x4FFFFFFF) = 0x{resultEnd:X16}");
    }

    [Fact]
    public void VaToLong_ModuleAddress_SignExtends()
    {
        // Xbox 360 module addresses (0x82000000+) have bit 31 set,
        // so sign extension produces a negative 64-bit value.
        var result = Xbox360MemoryUtils.VaToLong(0x82000000);
        Assert.True(result < 0, "Module address should sign-extend to negative");
        Assert.Equal(unchecked((long)(int)0x82000000), result);

        _output.WriteLine($"VaToLong(0x82000000) = 0x{result:X16} ({result})");
    }

    #endregion

    public void Dispose()
    {
        _accessor?.Dispose();
        _mmf?.Dispose();

        if (_tempFilePath != null && File.Exists(_tempFilePath))
        {
            try
            {
                File.Delete(_tempFilePath);
            }
            catch
            {
                // Best-effort cleanup; temp files are cleaned by OS eventually.
            }
        }
    }
}
