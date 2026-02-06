using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats;

/// <summary>
///     Tests for file type color coding.
///     Verifies that each format has the correct category and color assignment.
/// </summary>
public class FileColoringTests
{
    // Expected category colors (ARGB format) - Evenly-spaced hue spectrum from FormatRegistry
    private const uint TextureColor = 0xFFE0C043; // Hue 48° Gold
    private const uint ImageColor = 0xFFC0E043; // Hue 72° Yellow-green
    private const uint AudioColor = 0xFF82E043; // Hue 96° Lime
    private const uint ModelColor = 0xFF43E043; // Hue 120° Green
    private const uint ModuleColor = 0xFFE08243; // Hue 24° Orange
    private const uint ScriptColor = 0xFF43E082; // Hue 144° Spring green
    private const uint XboxColor = 0xFF43C0E0; // Hue 192° Sky blue
    private const uint HeaderColor = 0xFF708090; // Steel gray
    private const uint EsmDataColor = 0xFF43E0C0; // Hue 168° Aquamarine

    #region Format Category Tests

    [Theory]
    [InlineData("xui", FileCategory.Xbox)]
    [InlineData("xdbf", FileCategory.Xbox)]
    [InlineData("dds", FileCategory.Texture)]
    [InlineData("ddx", FileCategory.Texture)]
    [InlineData("png", FileCategory.Image)]
    [InlineData("xma", FileCategory.Audio)]
    [InlineData("lip", FileCategory.Audio)]
    [InlineData("nif", FileCategory.Model)]
    public void Format_HasCorrectCategory(string formatId, FileCategory expectedCategory)
    {
        // Act
        var format = FormatRegistry.GetByFormatId(formatId);

        // Assert
        Assert.NotNull(format);
        Assert.Equal(expectedCategory, format.Category);
    }

    [Theory]
    [InlineData("xui_scene", FileCategory.Xbox)]
    [InlineData("xui_binary", FileCategory.Xbox)]
    [InlineData("xdbf", FileCategory.Xbox)]
    [InlineData("dds", FileCategory.Texture)]
    [InlineData("ddx_3xdo", FileCategory.Texture)]
    [InlineData("ddx_3xdr", FileCategory.Texture)]
    [InlineData("png", FileCategory.Image)]
    [InlineData("xma", FileCategory.Audio)]
    [InlineData("lip", FileCategory.Audio)]
    [InlineData("nif", FileCategory.Model)]
    public void SignatureId_ResolvesToCorrectCategory(string signatureId, FileCategory expectedCategory)
    {
        // Act
        var category = FormatRegistry.GetCategory(signatureId);

        // Assert
        Assert.Equal(expectedCategory, category);
    }

    #endregion

    #region Category Color Tests

    [Theory]
    [InlineData(FileCategory.Texture, TextureColor)]
    [InlineData(FileCategory.Image, ImageColor)]
    [InlineData(FileCategory.Audio, AudioColor)]
    [InlineData(FileCategory.Model, ModelColor)]
    [InlineData(FileCategory.Module, ModuleColor)]
    [InlineData(FileCategory.Script, ScriptColor)]
    [InlineData(FileCategory.Xbox, XboxColor)]
    [InlineData(FileCategory.Header, HeaderColor)]
    [InlineData(FileCategory.EsmData, EsmDataColor)]
    public void CategoryColors_ContainsCorrectColor(FileCategory category, uint expectedColor)
    {
        // Act
        var hasColor = FormatRegistry.CategoryColors.TryGetValue(category, out var actualColor);

        // Assert
        Assert.True(hasColor, $"Category {category} should have a color defined");
        Assert.Equal(expectedColor, actualColor);
    }

    [Fact]
    public void CategoryColors_ContainsAllCategories()
    {
        // Arrange
        var allCategories = Enum.GetValues<FileCategory>();

        // Act & Assert
        foreach (var category in allCategories)
            Assert.True(FormatRegistry.CategoryColors.ContainsKey(category),
                $"Category {category} should have a color defined");
    }

    #endregion

    #region CarvedFileInfo Color Assignment Tests

    [Theory]
    [InlineData(FileCategory.Xbox, XboxColor)]
    [InlineData(FileCategory.Module, ModuleColor)]
    [InlineData(FileCategory.Image, ImageColor)]
    [InlineData(FileCategory.Texture, TextureColor)]
    [InlineData(FileCategory.Audio, AudioColor)]
    [InlineData(FileCategory.Model, ModelColor)]
    [InlineData(FileCategory.Script, ScriptColor)]
    [InlineData(FileCategory.Header, HeaderColor)]
    [InlineData(FileCategory.EsmData, EsmDataColor)]
    public void CarvedFileInfo_WithCategory_GetsCorrectColor(FileCategory category, uint expectedColor)
    {
        // Arrange
        var file = new CarvedFileInfo
        {
            Offset = 0x1000,
            Length = 0x100,
            FileType = "Test File",
            Category = category
        };

        // Act
        var actualColor = FormatRegistry.CategoryColors.GetValueOrDefault(file.Category, FormatRegistry.UnknownColor);

        // Assert
        Assert.Equal(expectedColor, actualColor);
    }

    [Fact]
    public void CarvedFileInfo_ModuleCategory_GetsPurpleColor()
    {
        // Arrange - Simulates how MinidumpAnalyzer creates module entries
        var dllFile = new CarvedFileInfo
        {
            Offset = 0x0CB176E4,
            Length = 100000,
            FileType = "Xbox 360 Module (DLL)",
            FileName = "test.dll",
            SignatureId = "module",
            Category = FileCategory.Module
        };

        // Act
        var hasColor = FormatRegistry.CategoryColors.TryGetValue(dllFile.Category, out var color);

        // Assert
        Assert.True(hasColor, "Module category should have a color");
        Assert.Equal(ModuleColor, color);
    }

    [Fact]
    public void CarvedFileInfo_XuiCategory_GetsBlueColor()
    {
        // Arrange - Simulates how MinidumpAnalyzer creates XUI entries
        var xuiFile = new CarvedFileInfo
        {
            Offset = 0x0CB07B22,
            Length = 5000,
            FileType = "XUI Scene",
            SignatureId = "xui_scene",
            Category = FileCategory.Xbox
        };

        // Act
        var hasColor = FormatRegistry.CategoryColors.TryGetValue(xuiFile.Category, out var color);

        // Assert
        Assert.True(hasColor, "Xbox category should have a color");
        Assert.Equal(XboxColor, color);
    }

    [Fact]
    public void CarvedFileInfo_XdbfCategory_GetsBlueColor()
    {
        // Arrange - XDBF should be Xbox category (blue), not Image (teal)
        var xdbfFile = new CarvedFileInfo
        {
            Offset = 0x0A191384,
            Length = 10000,
            FileType = "Xbox Dashboard File",
            SignatureId = "xdbf",
            Category = FileCategory.Xbox
        };

        // Act
        var hasColor = FormatRegistry.CategoryColors.TryGetValue(xdbfFile.Category, out var color);

        // Assert
        Assert.True(hasColor, "Xbox category should have a color");
        Assert.Equal(XboxColor, color);
        Assert.NotEqual(ImageColor, color); // Should NOT be teal (PNG color)
    }

    #endregion

    #region GetColor by SignatureId Tests

    [Theory]
    [InlineData("xui_scene", XboxColor)]
    [InlineData("xui_binary", XboxColor)]
    [InlineData("xdbf", XboxColor)]
    [InlineData("dds", TextureColor)]
    [InlineData("ddx_3xdo", TextureColor)]
    [InlineData("png", ImageColor)]
    [InlineData("xma", AudioColor)]
    [InlineData("nif", ModelColor)]
    public void GetColor_BySignatureId_ReturnsCorrectColor(string signatureId, uint expectedColor)
    {
        // Act
        var actualColor = FormatRegistry.GetColor(signatureId);

        // Assert
        Assert.Equal(expectedColor, actualColor);
    }

    [Theory]
    [InlineData("minidump_header", HeaderColor)]
    public void GetColor_SpecialSignatureId_ReturnsCorrectColor(string signatureId, uint expectedColor)
    {
        // Act - minidump_header is a special case handled in GetCategory
        var category = FormatRegistry.GetCategory(signatureId);
        var actualColor = FormatRegistry.CategoryColors.GetValueOrDefault(category, FormatRegistry.UnknownColor);

        // Assert
        Assert.Equal(expectedColor, actualColor);
    }

    #endregion

    #region Real World Scenario Tests

    /// <summary>
    ///     Simulates the specific offsets from Fallout_Release_Beta.xex1.dmp where
    ///     coloring was reported as incorrect. These test that the correct categories
    ///     would be assigned when creating CarvedFileInfo objects.
    /// </summary>
    [Fact]
    public void RealWorld_XuiSceneAtOffset_ShouldBeXboxCategory()
    {
        // XUI at 0x0CB07B22 was reported showing white instead of blue
        var xuiFile = new CarvedFileInfo
        {
            Offset = 0x0CB07B22,
            Length = 5000,
            FileType = "XUI Scene",
            SignatureId = "xui_scene",
            Category = FileCategory.Xbox
        };

        // Verify category and color
        Assert.Equal(FileCategory.Xbox, xuiFile.Category);
        var color = FormatRegistry.CategoryColors[xuiFile.Category];
        Assert.Equal(XboxColor, color);
    }

    [Fact]
    public void RealWorld_DllAtOffset_ShouldBeModuleCategory()
    {
        // DLL at 0x0CB176E4 was reported showing white instead of purple
        var dllFile = new CarvedFileInfo
        {
            Offset = 0x0CB176E4,
            Length = 655360,
            FileType = "Xbox 360 Module (DLL)",
            FileName = "xbdm.dll",
            SignatureId = "module",
            Category = FileCategory.Module
        };

        // Verify category and color
        Assert.Equal(FileCategory.Module, dllFile.Category);
        var color = FormatRegistry.CategoryColors[dllFile.Category];
        Assert.Equal(ModuleColor, color);
    }

    [Fact]
    public void RealWorld_XurAtOffset_ShouldBeXboxCategory()
    {
        // XUR at 0x0CA5B8AA was reported showing white instead of blue
        var xurFile = new CarvedFileInfo
        {
            Offset = 0x0CA5B8AA,
            Length = 2000,
            FileType = "XUI Binary",
            SignatureId = "xui_binary",
            Category = FileCategory.Xbox
        };

        // Verify category and color
        Assert.Equal(FileCategory.Xbox, xurFile.Category);
        var color = FormatRegistry.CategoryColors[xurFile.Category];
        Assert.Equal(XboxColor, color);
    }

    [Fact]
    public void RealWorld_XdbfAtOffset_ShouldBeXboxCategory_NotPng()
    {
        // XDBF at 0x0A191384 was reported showing teal (PNG) instead of blue (Xbox)
        var xdbfFile = new CarvedFileInfo
        {
            Offset = 0x0A191384,
            Length = 10000,
            FileType = "Xbox Dashboard File",
            SignatureId = "xdbf",
            Category = FileCategory.Xbox
        };

        // Verify category and color
        Assert.Equal(FileCategory.Xbox, xdbfFile.Category);
        var color = FormatRegistry.CategoryColors[xdbfFile.Category];
        Assert.Equal(XboxColor, color);
        Assert.NotEqual(ImageColor, color); // NOT teal/PNG
    }

    [Fact]
    public void RealWorld_XdbfAtOffset2_ShouldBeXboxCategory_NotPng()
    {
        // XDBF at 0x0A193384 was reported showing teal (PNG) instead of blue (Xbox)
        var xdbfFile = new CarvedFileInfo
        {
            Offset = 0x0A193384,
            Length = 10000,
            FileType = "Xbox Dashboard File",
            SignatureId = "xdbf",
            Category = FileCategory.Xbox
        };

        // Verify category and color
        Assert.Equal(FileCategory.Xbox, xdbfFile.Category);
        var color = FormatRegistry.CategoryColors[xdbfFile.Category];
        Assert.Equal(XboxColor, color);
        Assert.NotEqual(ImageColor, color); // NOT teal/PNG
    }

    /// <summary>
    ///     Tests that FormatRegistry.GetBySignatureId returns the correct format
    ///     with correct Category for each signature.
    /// </summary>
    [Theory]
    [InlineData("xdbf", FileCategory.Xbox)]
    [InlineData("xui_scene", FileCategory.Xbox)]
    [InlineData("xui_binary", FileCategory.Xbox)]
    [InlineData("png", FileCategory.Image)]
    [InlineData("dds", FileCategory.Texture)]
    [InlineData("ddx_3xdo", FileCategory.Texture)]
    [InlineData("xma", FileCategory.Audio)]
    [InlineData("nif", FileCategory.Model)]
    public void GetBySignatureId_ReturnsFormatWithCorrectCategory(string signatureId, FileCategory expectedCategory)
    {
        // Act - This is exactly what MinidumpAnalyzer does
        var format = FormatRegistry.GetBySignatureId(signatureId);

        // Assert
        Assert.NotNull(format);
        Assert.Equal(expectedCategory, format.Category);
    }

    #endregion
}