using FalloutXbox360Utils.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Parsers;

/// <summary>
///     Tests for FileTypeDetector — magic byte detection and extension checking.
/// </summary>
public class FileTypeDetectorTests
{
    #region AnalysisFileType Enum

    [Fact]
    public void AnalysisFileType_HasExpectedValues()
    {
        Assert.Equal(0, (int)AnalysisFileType.Unknown);
        Assert.Equal(1, (int)AnalysisFileType.Minidump);
        Assert.Equal(2, (int)AnalysisFileType.EsmFile);
    }

    #endregion

    #region DetectFromMagic

    [Fact]
    public void DetectFromMagic_PcEsm_ReturnsEsmFile()
    {
        // "TES4" (0x54 0x45 0x53 0x34)
        byte[] header = [(byte)'T', (byte)'E', (byte)'S', (byte)'4'];
        Assert.Equal(AnalysisFileType.EsmFile, FileTypeDetector.DetectFromMagic(header));
    }

    [Fact]
    public void DetectFromMagic_Xbox360Esm_ReturnsEsmFile()
    {
        // "4SET" (0x34 0x53 0x45 0x54)
        byte[] header = [(byte)'4', (byte)'S', (byte)'E', (byte)'T'];
        Assert.Equal(AnalysisFileType.EsmFile, FileTypeDetector.DetectFromMagic(header));
    }

    [Fact]
    public void DetectFromMagic_Minidump_ReturnsMinidump()
    {
        // "MDMP" (0x4D 0x44 0x4D 0x50)
        byte[] header = [(byte)'M', (byte)'D', (byte)'M', (byte)'P'];
        Assert.Equal(AnalysisFileType.Minidump, FileTypeDetector.DetectFromMagic(header));
    }

    [Fact]
    public void DetectFromMagic_Unknown_ReturnsUnknown()
    {
        byte[] header = [0xDE, 0xAD, 0xBE, 0xEF];
        Assert.Equal(AnalysisFileType.Unknown, FileTypeDetector.DetectFromMagic(header));
    }

    [Fact]
    public void DetectFromMagic_TooShort_ReturnsUnknown()
    {
        byte[] header = [(byte)'T', (byte)'E', (byte)'S'];
        Assert.Equal(AnalysisFileType.Unknown, FileTypeDetector.DetectFromMagic(header));
    }

    [Fact]
    public void DetectFromMagic_Empty_ReturnsUnknown()
    {
        Assert.Equal(AnalysisFileType.Unknown, FileTypeDetector.DetectFromMagic([]));
    }

    [Fact]
    public void DetectFromMagic_SingleByte_ReturnsUnknown()
    {
        byte[] header = [0x54];
        Assert.Equal(AnalysisFileType.Unknown, FileTypeDetector.DetectFromMagic(header));
    }

    [Fact]
    public void DetectFromMagic_ZeroBytes_ReturnsUnknown()
    {
        byte[] header = [0x00, 0x00, 0x00, 0x00];
        Assert.Equal(AnalysisFileType.Unknown, FileTypeDetector.DetectFromMagic(header));
    }

    [Fact]
    public void DetectFromMagic_ExtraBytes_StillDetects()
    {
        // More than 4 bytes should still detect correctly from first 4
        byte[] header = [(byte)'T', (byte)'E', (byte)'S', (byte)'4', 0x00, 0x00, 0x00, 0x00];
        Assert.Equal(AnalysisFileType.EsmFile, FileTypeDetector.DetectFromMagic(header));
    }

    #endregion

    #region IsSupportedExtension

    [Theory]
    [InlineData("file.dmp")]
    [InlineData("file.DMP")]
    [InlineData("file.esm")]
    [InlineData("file.ESM")]
    [InlineData("file.esp")]
    [InlineData("file.ESP")]
    [InlineData("path/to/file.dmp")]
    [InlineData("C:\\Users\\test\\file.esm")]
    public void IsSupportedExtension_ValidExtension_ReturnsTrue(string path)
    {
        Assert.True(FileTypeDetector.IsSupportedExtension(path));
    }

    [Theory]
    [InlineData("file.txt")]
    [InlineData("file.exe")]
    [InlineData("file.nif")]
    [InlineData("file.dds")]
    [InlineData("file")]
    [InlineData("")]
    public void IsSupportedExtension_InvalidExtension_ReturnsFalse(string path)
    {
        Assert.False(FileTypeDetector.IsSupportedExtension(path));
    }

    #endregion

    #region Detect (File-based)

    [Fact]
    public void Detect_NonexistentFile_ReturnsUnknown()
    {
        Assert.Equal(AnalysisFileType.Unknown, FileTypeDetector.Detect("nonexistent_file.esm"));
    }

    [Fact]
    public void Detect_ValidTempEsmFile_ReturnsEsmFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(tempFile, [(byte)'T', (byte)'E', (byte)'S', (byte)'4', 0x00, 0x00, 0x00, 0x00]);
            Assert.Equal(AnalysisFileType.EsmFile, FileTypeDetector.Detect(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Detect_TooSmallFile_ReturnsUnknown()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(tempFile, [0x54, 0x45]); // Only 2 bytes
            Assert.Equal(AnalysisFileType.Unknown, FileTypeDetector.Detect(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    #endregion
}