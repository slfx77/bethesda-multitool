using Xbox360MemoryCarver.Core.Minidump;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Minidump;

/// <summary>
///     Tests for MinidumpParser.
/// </summary>
public class MinidumpParserTests
{
    #region Stream Count Tests

    [Fact]
    public void Parse_WithMultipleStreams_SetsNumberOfStreams()
    {
        // Arrange
        var data = CreateMinimalMinidump(5);
        using var stream = new MemoryStream(data);

        // Act
        var result = MinidumpParser.Parse(stream);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal((uint)5, result.NumberOfStreams);
    }

    #endregion

    #region Header Validation Tests

    [Fact]
    public void Parse_InvalidSignature_ReturnsInvalid()
    {
        // Arrange - Missing MDMP signature
        var data = new byte[100];
        "XXXX"u8.ToArray().CopyTo(data, 0);

        using var stream = new MemoryStream(data);

        // Act
        var result = MinidumpParser.Parse(stream);

        // Assert
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_ValidSignature_ReturnsValid()
    {
        // Arrange - Valid MDMP signature with minimal header
        var data = CreateMinimalMinidump();
        using var stream = new MemoryStream(data);

        // Act
        var result = MinidumpParser.Parse(stream);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_TruncatedHeader_ReturnsInvalid()
    {
        // Arrange - Only 10 bytes when 32 required
        var data = new byte[10];
        "MDMP"u8.ToArray().CopyTo(data, 0);

        using var stream = new MemoryStream(data);

        // Act
        var result = MinidumpParser.Parse(stream);

        // Assert
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_ZeroStreams_ReturnsInvalid()
    {
        // Arrange - Valid signature but 0 streams
        var data = CreateMinidumpHeader(0, 32);
        using var stream = new MemoryStream(data);

        // Act
        var result = MinidumpParser.Parse(stream);

        // Assert
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_TooManyStreams_ReturnsInvalid()
    {
        // Arrange - Unreasonably large number of streams (>100)
        var data = CreateMinidumpHeader(500, 32);
        using var stream = new MemoryStream(data);

        // Act
        var result = MinidumpParser.Parse(stream);

        // Assert
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_ZeroStreamDirectoryRva_ReturnsInvalid()
    {
        // Arrange - Valid stream count but zero RVA
        var data = CreateMinidumpHeader(1, 0);
        using var stream = new MemoryStream(data);

        // Act
        var result = MinidumpParser.Parse(stream);

        // Assert
        Assert.False(result.IsValid);
    }

    #endregion

    #region SystemInfo Stream Tests

    [Fact]
    public void Parse_WithSystemInfoStream_ExtractsArchitecture()
    {
        // Arrange - Minidump with SystemInfo stream indicating PowerPC (Xbox 360)
        var data = CreateMinidumpWithSystemInfo(0x03);
        using var stream = new MemoryStream(data);

        // Act
        var result = MinidumpParser.Parse(stream);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(0x03, result.ProcessorArchitecture);
        Assert.True(result.IsXbox360);
    }

    [Fact]
    public void Parse_WithX86Architecture_NotXbox360()
    {
        // Arrange - SystemInfo stream with x86 (0x00)
        var data = CreateMinidumpWithSystemInfo(0x00);
        using var stream = new MemoryStream(data);

        // Act
        var result = MinidumpParser.Parse(stream);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(0x00, result.ProcessorArchitecture);
        Assert.False(result.IsXbox360);
    }

    [Fact]
    public void Parse_WithAmd64Architecture_NotXbox360()
    {
        // Arrange - SystemInfo stream with AMD64 (0x09)
        var data = CreateMinidumpWithSystemInfo(0x09);
        using var stream = new MemoryStream(data);

        // Act
        var result = MinidumpParser.Parse(stream);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(0x09, result.ProcessorArchitecture);
        Assert.False(result.IsXbox360);
    }

    #endregion

    #region MinidumpInfo Helper Tests

    [Fact]
    public void MinidumpInfo_FindModuleByVirtualAddress_FindsModule()
    {
        // Arrange
        var info = new MinidumpInfo
        {
            IsValid = true,
            Modules =
            [
                new MinidumpModule { Name = "module1.exe", BaseAddress = 0x10000, Size = 0x5000 },
                new MinidumpModule { Name = "module2.dll", BaseAddress = 0x20000, Size = 0x3000 }
            ]
        };

        // Act
        var found = info.FindModuleByVirtualAddress(0x12000);

        // Assert
        Assert.NotNull(found);
        Assert.Equal("module1.exe", found.Name);
    }

    [Fact]
    public void MinidumpInfo_FindModuleByVirtualAddress_NotFound()
    {
        // Arrange
        var info = new MinidumpInfo
        {
            IsValid = true,
            Modules =
            [
                new MinidumpModule { Name = "module1.exe", BaseAddress = 0x10000, Size = 0x5000 }
            ]
        };

        // Act
        var found = info.FindModuleByVirtualAddress(0x50000); // Outside any module

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public void MinidumpInfo_FileOffsetToVirtualAddress_ConvertsCorrectly()
    {
        // Arrange
        var info = new MinidumpInfo
        {
            IsValid = true,
            MemoryRegions =
            [
                new MinidumpMemoryRegion { VirtualAddress = 0x80000000, Size = 0x10000, FileOffset = 0x1000 }
            ]
        };

        // Act - File offset 0x1500 should map to VA 0x80000500
        var va = info.FileOffsetToVirtualAddress(0x1500);

        // Assert
        Assert.NotNull(va);
        Assert.Equal(0x80000500, va.Value);
    }

    [Fact]
    public void MinidumpInfo_FileOffsetToVirtualAddress_OutOfRange_ReturnsNull()
    {
        // Arrange
        var info = new MinidumpInfo
        {
            IsValid = true,
            MemoryRegions =
            [
                new MinidumpMemoryRegion { VirtualAddress = 0x80000000, Size = 0x10000, FileOffset = 0x1000 }
            ]
        };

        // Act
        var va = info.FileOffsetToVirtualAddress(0x50000); // Outside region

        // Assert
        Assert.Null(va);
    }

    [Fact]
    public void MinidumpInfo_VirtualAddressToFileOffset_ConvertsCorrectly()
    {
        // Arrange
        var info = new MinidumpInfo
        {
            IsValid = true,
            MemoryRegions =
            [
                new MinidumpMemoryRegion { VirtualAddress = 0x80000000, Size = 0x10000, FileOffset = 0x1000 }
            ]
        };

        // Act - VA 0x80000500 should map to file offset 0x1500
        var offset = info.VirtualAddressToFileOffset(0x80000500);

        // Assert
        Assert.NotNull(offset);
        Assert.Equal(0x1500, offset.Value);
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateMinidumpHeader(uint numberOfStreams, uint streamDirectoryRva)
    {
        var data = new byte[64];

        // Signature: "MDMP"
        data[0] = 0x4D;
        data[1] = 0x44;
        data[2] = 0x4D;
        data[3] = 0x50;

        // Version (unused in these tests)
        data[4] = 0x93;
        data[5] = 0xA7;
        data[6] = 0x00;
        data[7] = 0x00;

        // NumberOfStreams (little-endian)
        WriteUInt32Le(data, 8, numberOfStreams);

        // StreamDirectoryRva (little-endian)
        WriteUInt32Le(data, 12, streamDirectoryRva);

        return data;
    }

    private static byte[] CreateMinimalMinidump(int streamCount = 1)
    {
        // Create a minimal valid minidump with empty stream directory
        const int headerSize = 32;
        const int directoryOffset = 32;
        var directorySize = streamCount * 12;
        var totalSize = headerSize + directorySize + 64; // Extra space for data

        var data = new byte[totalSize];

        // Header
        data[0] = 0x4D;
        data[1] = 0x44;
        data[2] = 0x4D;
        data[3] = 0x50; // "MDMP"
        data[4] = 0x93;
        data[5] = 0xA7; // Version
        WriteUInt32Le(data, 8, (uint)streamCount);
        WriteUInt32Le(data, 12, (uint)directoryOffset);

        // Empty stream directory entries (type 0 = unused)
        // Just leave as zeros

        return data;
    }

    private static byte[] CreateMinidumpWithSystemInfo(ushort processorArchitecture)
    {
        const int directoryOffset = 32;
        var systemInfoOffset = directoryOffset + 12; // After 1 directory entry
        var totalSize = systemInfoOffset + 64;

        var data = new byte[totalSize];

        // Header
        data[0] = 0x4D;
        data[1] = 0x44;
        data[2] = 0x4D;
        data[3] = 0x50;
        data[4] = 0x93;
        data[5] = 0xA7;
        WriteUInt32Le(data, 8, 1); // 1 stream
        WriteUInt32Le(data, 12, directoryOffset);

        // Stream directory entry for SystemInfoStream (type 7)
        WriteUInt32Le(data, directoryOffset, 7); // StreamType = SystemInfoStream
        WriteUInt32Le(data, directoryOffset + 4, 64); // DataSize
        WriteUInt32Le(data, directoryOffset + 8, (uint)systemInfoOffset); // RVA

        // SystemInfo structure (processor architecture at offset 0)
        WriteUInt16Le(data, systemInfoOffset, processorArchitecture);

        return data;
    }

    private static void WriteUInt32Le(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
        data[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteUInt16Le(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    #endregion
}