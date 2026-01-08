using Xbox360MemoryCarver.Core.Formats;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Formats;

/// <summary>
///     Tests for FormatRegistry (auto-discovery and lookup).
/// </summary>
public class FormatRegistryTests
{
    #region Format Discovery Tests

    [Fact]
    public void All_ContainsFormats()
    {
        // Act
        var formats = FormatRegistry.All;

        // Assert
        Assert.NotNull(formats);
        Assert.NotEmpty(formats);
    }

    [Fact]
    public void All_ContainsExpectedCoreFormats()
    {
        // Act
        var formats = FormatRegistry.All;
        var formatIds = formats.Select(f => f.FormatId).ToList();

        // Assert - Core formats should be present
        Assert.Contains("dds", formatIds);
        Assert.Contains("ddx", formatIds);
        Assert.Contains("png", formatIds);
        Assert.Contains("nif", formatIds);
        Assert.Contains("xma", formatIds);
        Assert.Contains("xex", formatIds);
    }

    [Fact]
    public void All_FormatsHaveValidProperties()
    {
        // Act & Assert
        foreach (var format in FormatRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(format.FormatId), $"FormatId is empty");
            Assert.False(string.IsNullOrWhiteSpace(format.DisplayName), $"DisplayName is empty for {format.FormatId}");
            Assert.False(string.IsNullOrWhiteSpace(format.Extension), $"Extension is empty for {format.FormatId}");
            // Note: Some formats (e.g., EsmRecordFormat) are dump scanners without signatures
            // They implement IDumpScanner instead of using signature-based detection
            Assert.True(format.MinSize >= 0, $"MinSize is negative for {format.FormatId}");
            Assert.True(format.MaxSize >= format.MinSize, $"MaxSize < MinSize for {format.FormatId}");
        }
    }

    [Fact]
    public void All_SignaturesHaveValidMagicBytes()
    {
        // Act & Assert
        foreach (var format in FormatRegistry.All)
        {
            // Skip formats that don't use signature-based detection (e.g., dump scanners)
            if (format.Signatures.Count == 0) continue;

            foreach (var sig in format.Signatures)
            {
                Assert.False(string.IsNullOrWhiteSpace(sig.Id), $"Signature Id is empty in {format.FormatId}");
                Assert.NotEmpty(sig.MagicBytes);
            }
        }
    }

    #endregion

    #region GetByFormatId Tests

    [Theory]
    [InlineData("dds")]
    [InlineData("ddx")]
    [InlineData("png")]
    [InlineData("nif")]
    [InlineData("xma")]
    [InlineData("xex")]
    public void GetByFormatId_KnownFormat_ReturnsFormat(string formatId)
    {
        // Act
        var format = FormatRegistry.GetByFormatId(formatId);

        // Assert
        Assert.NotNull(format);
        Assert.Equal(formatId, format.FormatId);
    }

    [Fact]
    public void GetByFormatId_UnknownFormat_ReturnsNull()
    {
        // Act
        var format = FormatRegistry.GetByFormatId("nonexistent_format");

        // Assert
        Assert.Null(format);
    }

    [Fact]
    public void GetByFormatId_CaseInsensitive()
    {
        // Act
        var lower = FormatRegistry.GetByFormatId("dds");
        var upper = FormatRegistry.GetByFormatId("DDS");
        var mixed = FormatRegistry.GetByFormatId("Dds");

        // Assert
        Assert.NotNull(lower);
        Assert.NotNull(upper);
        Assert.NotNull(mixed);
        Assert.Same(lower, upper);
        Assert.Same(lower, mixed);
    }

    #endregion

    #region GetBySignatureId Tests

    [Theory]
    [InlineData("dds")]
    [InlineData("ddx_3xdo")]
    [InlineData("ddx_3xdr")]
    [InlineData("png")]
    [InlineData("nif")]
    [InlineData("xma")]
    [InlineData("xex")]
    public void GetBySignatureId_KnownSignature_ReturnsFormat(string signatureId)
    {
        // Act
        var format = FormatRegistry.GetBySignatureId(signatureId);

        // Assert
        Assert.NotNull(format);
        Assert.Contains(format.Signatures, s => s.Id == signatureId);
    }

    [Fact]
    public void GetBySignatureId_UnknownSignature_ReturnsNull()
    {
        // Act
        var format = FormatRegistry.GetBySignatureId("nonexistent_signature");

        // Assert
        Assert.Null(format);
    }

    #endregion

    #region GetCategory Tests

    [Theory]
    [InlineData("dds", FileCategory.Texture)]
    [InlineData("ddx_3xdo", FileCategory.Texture)]
    [InlineData("png", FileCategory.Image)]
    [InlineData("xma", FileCategory.Audio)]
    [InlineData("nif", FileCategory.Model)]
    [InlineData("xex", FileCategory.Module)]
    public void GetCategory_KnownSignature_ReturnsCorrectCategory(string signatureId, FileCategory expectedCategory)
    {
        // Act
        var category = FormatRegistry.GetCategory(signatureId);

        // Assert
        Assert.Equal(expectedCategory, category);
    }

    #endregion

    #region GetColor Tests

    [Fact]
    public void GetColor_KnownSignature_ReturnsNonZeroColor()
    {
        // Act
        var color = FormatRegistry.GetColor("dds");

        // Assert
        Assert.NotEqual(0u, color);
        Assert.True((color & 0xFF000000) != 0, "Alpha should be non-zero");
    }

    [Fact]
    public void GetColor_DifferentCategories_ReturnDifferentColors()
    {
        // Act
        var textureColor = FormatRegistry.GetColor("dds");
        var audioColor = FormatRegistry.GetColor("xma");
        var modelColor = FormatRegistry.GetColor("nif");

        // Assert - Different categories should have different colors
        Assert.NotEqual(textureColor, audioColor);
        Assert.NotEqual(textureColor, modelColor);
        Assert.NotEqual(audioColor, modelColor);
    }

    #endregion

    #region CategoryColors Tests

    [Fact]
    public void CategoryColors_ContainsAllCategories()
    {
        // Act
        var colors = FormatRegistry.CategoryColors;

        // Assert
        Assert.Contains(FileCategory.Texture, colors.Keys);
        Assert.Contains(FileCategory.Image, colors.Keys);
        Assert.Contains(FileCategory.Audio, colors.Keys);
        Assert.Contains(FileCategory.Model, colors.Keys);
        Assert.Contains(FileCategory.Module, colors.Keys);
        Assert.Contains(FileCategory.Script, colors.Keys);
        Assert.Contains(FileCategory.Xbox, colors.Keys);
        Assert.Contains(FileCategory.Plugin, colors.Keys);
    }

    [Fact]
    public void UnknownColor_IsDarkGray()
    {
        // Assert - Unknown color should be a dark gray (#3D3D3D)
        Assert.Equal(0xFF3D3D3D, FormatRegistry.UnknownColor);
    }

    #endregion

    #region NormalizeToSignatureId Tests

    [Theory]
    [InlineData("dds", "dds")]
    [InlineData("DDS", "dds")]
    [InlineData("ddx_3xdo", "ddx_3xdo")]
    [InlineData("3xdo", "ddx_3xdo")]
    [InlineData("3XDO", "ddx_3xdo")]
    [InlineData("3xdr", "ddx_3xdr")]
    [InlineData("something with texture", "dds")]  // Keyword fallback
    [InlineData("some audio file", "xma")]         // Keyword fallback
    [InlineData("a model file", "nif")]            // Keyword fallback
    [InlineData("an executable", "xex")]           // Keyword fallback
    public void NormalizeToSignatureId_KnownInputs_ReturnsNormalized(string input, string expected)
    {
        // Act
        var result = FormatRegistry.NormalizeToSignatureId(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Xbox 360 DDX Texture (3XDO format)", "ddx_3xdo")]
    [InlineData("DirectDraw Surface Texture", "dds")]
    [InlineData("PNG Image", "png")]
    [InlineData("Xbox Media Audio (RIFF/XMA)", "xma")]
    [InlineData("NetImmerse/Gamebryo 3D Model", "nif")]
    public void NormalizeToSignatureId_Descriptions_ReturnsNormalized(string description, string expected)
    {
        // Act
        var result = FormatRegistry.NormalizeToSignatureId(description);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region GetDisplayPriority Tests

    [Fact]
    public void GetDisplayPriority_KnownSignature_ReturnsValue()
    {
        // Act
        var priority = FormatRegistry.GetDisplayPriority("dds");

        // Assert - Should return the format's priority
        var format = FormatRegistry.GetBySignatureId("dds");
        Assert.NotNull(format);
        Assert.Equal(format.DisplayPriority, priority);
    }

    [Fact]
    public void GetDisplayPriority_UnknownSignature_ReturnsDefault()
    {
        // Act
        var priority = FormatRegistry.GetDisplayPriority("nonexistent");

        // Assert - Default is 5
        Assert.Equal(5, priority);
    }

    #endregion

    #region DisplayNames Tests

    [Fact]
    public void DisplayNames_ContainsUniqueNames()
    {
        // Act
        var names = FormatRegistry.DisplayNames;

        // Assert
        Assert.NotEmpty(names);
        Assert.Equal(names.Count, names.Distinct().Count()); // All unique
    }

    [Fact]
    public void DisplayNames_MatchFormatsWithShowInFilterUI()
    {
        // Act
        var names = FormatRegistry.DisplayNames;
        var expectedNames = FormatRegistry.All
            .Where(f => f.ShowInFilterUI)
            .Select(f => f.DisplayName)
            .ToList();

        // Assert
        Assert.Equal(expectedNames.Count, names.Count);
        foreach (var name in expectedNames)
        {
            Assert.Contains(name, names);
        }
    }

    #endregion

    #region GetSignatureIdsForDisplayNames Tests

    [Fact]
    public void GetSignatureIdsForDisplayNames_ValidNames_ReturnsSignatureIds()
    {
        // Arrange
        var displayNames = new[] { "DDS", "PNG" };

        // Act
        var signatureIds = FormatRegistry.GetSignatureIdsForDisplayNames(displayNames).ToList();

        // Assert
        Assert.NotEmpty(signatureIds);
        Assert.Contains("dds", signatureIds);
        Assert.Contains("png", signatureIds);
    }

    [Fact]
    public void GetSignatureIdsForDisplayNames_InvalidNames_ReturnsEmpty()
    {
        // Arrange
        var displayNames = new[] { "NonExistent1", "NonExistent2" };

        // Act
        var signatureIds = FormatRegistry.GetSignatureIdsForDisplayNames(displayNames).ToList();

        // Assert
        Assert.Empty(signatureIds);
    }

    #endregion
}
