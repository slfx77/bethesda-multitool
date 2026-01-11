using Xbox360MemoryCarver.Core;
using Xbox360MemoryCarver.Core.Formats;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Formats;

/// <summary>
///     Tests for file type color coding.
///     Verifies that each format has the correct category and color assignment.
/// </summary>
public class FileColoringTests
{
    // Expected category colors (ARGB format)
    private const uint TextureColor = 0xFF2ECC71; // Green
    private const uint ImageColor = 0xFF1ABC9C; // Teal/Cyan
    private const uint AudioColor = 0xFFE74C3C; // Red
    private const uint ModelColor = 0xFFF1C40F; // Yellow
    private const uint ModuleColor = 0xFF9B59B6; // Purple
    private const uint ScriptColor = 0xFFE67E22; // Orange
    private const uint XboxColor = 0xFF3498DB; // Blue
    private const uint PluginColor = 0xFFFF6B9D; // Pink/Magenta
    private const uint HeaderColor = 0xFF607D8B; // Blue-gray

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
    [InlineData("esp", FileCategory.Plugin)]
    [InlineData("script", FileCategory.Script)]
    [InlineData("scda", FileCategory.Script)]
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
    [InlineData("esp", FileCategory.Plugin)]
    [InlineData("script_scn", FileCategory.Script)]
    [InlineData("script_scriptname", FileCategory.Script)]
    [InlineData("scda", FileCategory.Script)]
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
    [InlineData(FileCategory.Plugin, PluginColor)]
    [InlineData(FileCategory.Header, HeaderColor)]
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
    [InlineData(FileCategory.Plugin, PluginColor)]
    [InlineData(FileCategory.Header, HeaderColor)]
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
        // Arrange - Simulates how MemoryDumpAnalyzer creates module entries
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
        // Arrange - Simulates how MemoryDumpAnalyzer creates XUI entries
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
    [InlineData("esp", PluginColor)]
    [InlineData("script_scn", ScriptColor)]
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
        // Act - This is exactly what MemoryDumpAnalyzer does
        var format = FormatRegistry.GetBySignatureId(signatureId);

        // Assert
        Assert.NotNull(format);
        Assert.Equal(expectedCategory, format.Category);
    }

    #endregion
}