using Xbox360MemoryCarver.Core.Utils;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Parsers;

/// <summary>
///     Tests for TexturePathExtractor utility methods.
/// </summary>
public class TexturePathExtractorTests
{
    #region IsValidPathChar Tests

    [Theory]
    [InlineData('a', true)]
    [InlineData('z', true)]
    [InlineData('A', true)]
    [InlineData('Z', true)]
    [InlineData('0', true)]
    [InlineData('9', true)]
    [InlineData('_', true)]
    [InlineData('-', true)]
    [InlineData('.', true)]
    [InlineData('\\', true)]
    [InlineData('/', true)]
    [InlineData(' ', true)]
    [InlineData('*', false)]
    [InlineData('?', false)]
    [InlineData('<', false)]
    [InlineData('>', false)]
    [InlineData('|', false)]
    [InlineData('"', false)]
    public void IsValidPathChar_ReturnsExpectedResult(char c, bool expected)
    {
        // Act
        var result = TexturePathExtractor.IsValidPathChar(c);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region FindPrecedingDdxPath Tests

    [Fact]
    public void FindPrecedingDdxPath_CallsFindPrecedingPathWithDdxExtension()
    {
        // Arrange
        var pathBytes = "textures\\test.ddx"u8.ToArray();
        var data = new byte[pathBytes.Length + 100];
        pathBytes.CopyTo(data, 0);

        var headerOffset = pathBytes.Length + 50;

        // Act
        var result = TexturePathExtractor.FindPrecedingDdxPath(data, headerOffset);

        // Assert
        Assert.NotNull(result);
        Assert.EndsWith(".ddx", result, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region FindPrecedingDdsPath Tests

    [Fact]
    public void FindPrecedingDdsPath_CallsFindPrecedingPathWithDdsExtension()
    {
        // Arrange
        var pathBytes = "textures\\test.dds"u8.ToArray();
        var data = new byte[pathBytes.Length + 100];
        pathBytes.CopyTo(data, 0);

        var headerOffset = pathBytes.Length + 50;

        // Act
        var result = TexturePathExtractor.FindPrecedingDdsPath(data, headerOffset);

        // Assert
        Assert.NotNull(result);
        Assert.EndsWith(".dds", result, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region SanitizeFilename Tests

    [Fact]
    public void SanitizeFilename_ValidFilename_ReturnsUnchanged()
    {
        // Arrange
        var filename = "texture_normal_01";

        // Act
        var result = TexturePathExtractor.SanitizeFilename(filename);

        // Assert
        Assert.Equal("texture_normal_01", result);
    }

    [Fact]
    public void SanitizeFilename_WithSpaces_RemovesSpaces()
    {
        // Arrange
        var filename = "texture normal 01";

        // Act
        var result = TexturePathExtractor.SanitizeFilename(filename);

        // Assert
        Assert.Equal("texturenormal01", result);
    }

    [Fact]
    public void SanitizeFilename_WithSpecialChars_RemovesThem()
    {
        // Arrange
        var filename = "texture/normal\\01.test";

        // Act
        var result = TexturePathExtractor.SanitizeFilename(filename);

        // Assert
        Assert.Equal("texturenormal01test", result);
    }

    [Fact]
    public void SanitizeFilename_AllowsHyphens()
    {
        // Arrange
        var filename = "texture-normal-01";

        // Act
        var result = TexturePathExtractor.SanitizeFilename(filename);

        // Assert
        Assert.Equal("texture-normal-01", result);
    }

    #endregion

    #region FindPrecedingPath Tests

    [Fact]
    public void FindPrecedingPath_FindsDdxPath()
    {
        // Arrange - path ending in .ddx followed by some data, then DDX header position
        var pathBytes = "textures\\characters\\test.ddx"u8.ToArray();
        var data = new byte[pathBytes.Length + 100];
        pathBytes.CopyTo(data, 0);

        // Header would be at offset pathBytes.Length + some gap
        var headerOffset = pathBytes.Length + 50;

        // Act
        var result = TexturePathExtractor.FindPrecedingPath(data, headerOffset, ".ddx");

        // Assert
        Assert.NotNull(result);
        Assert.EndsWith(".ddx", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindPrecedingPath_FindsDdsPath()
    {
        // Arrange
        var pathBytes = "textures\\landscape\\grass.dds"u8.ToArray();
        var data = new byte[pathBytes.Length + 100];
        pathBytes.CopyTo(data, 0);

        var headerOffset = pathBytes.Length + 50;

        // Act
        var result = TexturePathExtractor.FindPrecedingPath(data, headerOffset, ".dds");

        // Assert
        Assert.NotNull(result);
        Assert.EndsWith(".dds", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindPrecedingPath_NoPathFound_ReturnsNull()
    {
        // Arrange - no valid path in data
        var data = new byte[200];

        // Act
        var result = TexturePathExtractor.FindPrecedingPath(data, 100, ".ddx");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindPrecedingPath_PathTooFarBack_ReturnsNull()
    {
        // Arrange - path is beyond maxSearchDistance (default 512)
        var data = new byte[1000];
        var pathBytes = "textures\\test.ddx"u8.ToArray();
        pathBytes.CopyTo(data, 0);

        // Header is 600 bytes after the path (beyond 512 byte search limit)
        var headerOffset = 600;

        // Act
        var result = TexturePathExtractor.FindPrecedingPath(data, headerOffset, ".ddx");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindPrecedingPath_CaseInsensitiveExtension()
    {
        // Arrange - uppercase extension
        var pathBytes = "textures\\test.DDX"u8.ToArray();
        var data = new byte[pathBytes.Length + 100];
        pathBytes.CopyTo(data, 0);

        var headerOffset = pathBytes.Length + 50;

        // Act
        var result = TexturePathExtractor.FindPrecedingPath(data, headerOffset, ".ddx");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void FindPrecedingPath_TrimsLeadingGarbage()
    {
        // Arrange - path with leading garbage characters
        var pathBytes = "\x00\x00textures\\test.ddx"u8.ToArray();
        var data = new byte[pathBytes.Length + 100];
        pathBytes.CopyTo(data, 0);

        var headerOffset = pathBytes.Length + 50;

        // Act
        var result = TexturePathExtractor.FindPrecedingPath(data, headerOffset, ".ddx");

        // Assert
        Assert.NotNull(result);
        Assert.StartsWith("textures", result);
    }

    [Fact]
    public void FindPrecedingPath_FindsTexturesRoot()
    {
        // Arrange - path with data\ prefix before textures\
        var pathBytes = "data\\textures\\characters\\test.ddx"u8.ToArray();
        var data = new byte[pathBytes.Length + 100];
        pathBytes.CopyTo(data, 0);

        var headerOffset = pathBytes.Length + 50;

        // Act
        var result = TexturePathExtractor.FindPrecedingPath(data, headerOffset, ".ddx");

        // Assert
        Assert.NotNull(result);
        Assert.StartsWith("textures", result);
    }

    #endregion
}