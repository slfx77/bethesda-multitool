using Xunit;
using Xbox360MemoryCarver.Core.FileTypes;

namespace Xbox360MemoryCarver.Tests.Core.FileTypes;

/// <summary>
/// Tests for FileTypeRegistry.
/// </summary>
public class FileTypeRegistryTests
{
    #region GetByTypeId Tests

    [Theory]
    [InlineData("dds")]
    [InlineData("ddx")]
    [InlineData("png")]
    [InlineData("xma")]
    [InlineData("nif")]
    [InlineData("xex")]
    [InlineData("xdbf")]
    [InlineData("xui")]
    [InlineData("esp")]
    [InlineData("lip")]
    [InlineData("script")]
    [InlineData("scda")]
    public void GetByTypeId_KnownTypes_ReturnsDefinition(string typeId)
    {
        // Act
        var result = FileTypeRegistry.GetByTypeId(typeId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(typeId, result.TypeId, ignoreCase: true);
    }

    [Fact]
    public void GetByTypeId_UnknownType_ReturnsNull()
    {
        // Act
        var result = FileTypeRegistry.GetByTypeId("unknown_type");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetByTypeId_CaseInsensitive()
    {
        // Act
        var lower = FileTypeRegistry.GetByTypeId("dds");
        var upper = FileTypeRegistry.GetByTypeId("DDS");
        var mixed = FileTypeRegistry.GetByTypeId("Dds");

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
    [InlineData("ddx_3xdo")]
    [InlineData("ddx_3xdr")]
    [InlineData("dds")]
    [InlineData("png")]
    [InlineData("xma")]
    [InlineData("nif")]
    [InlineData("xex")]
    [InlineData("xdbf")]
    [InlineData("xui_scene")]
    [InlineData("xui_binary")]
    [InlineData("esp")]
    [InlineData("lip")]
    [InlineData("script_scn")]
    [InlineData("scda")]
    public void GetBySignatureId_KnownSignatures_ReturnsDefinition(string signatureId)
    {
        // Act
        var result = FileTypeRegistry.GetBySignatureId(signatureId);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void GetBySignatureId_UnknownSignature_ReturnsNull()
    {
        // Act
        var result = FileTypeRegistry.GetBySignatureId("unknown_signature");

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region GetCategory Tests

    [Fact]
    public void GetCategory_TextureTypes_ReturnsTextureCategory()
    {
        // Act & Assert
        Assert.Equal(FileCategory.Texture, FileTypeRegistry.GetCategory("ddx_3xdo"));
        Assert.Equal(FileCategory.Texture, FileTypeRegistry.GetCategory("ddx_3xdr"));
        Assert.Equal(FileCategory.Texture, FileTypeRegistry.GetCategory("dds"));
    }

    [Fact]
    public void GetCategory_AudioTypes_ReturnsAudioCategory()
    {
        // Act & Assert
        Assert.Equal(FileCategory.Audio, FileTypeRegistry.GetCategory("xma"));
        Assert.Equal(FileCategory.Audio, FileTypeRegistry.GetCategory("lip"));
    }

    [Fact]
    public void GetCategory_ModelTypes_ReturnsModelCategory()
    {
        // Act & Assert
        Assert.Equal(FileCategory.Model, FileTypeRegistry.GetCategory("nif"));
    }

    [Fact]
    public void GetCategory_ImageTypes_ReturnsImageCategory()
    {
        // Act & Assert
        Assert.Equal(FileCategory.Image, FileTypeRegistry.GetCategory("png"));
    }

    #endregion

    #region GetColor Tests

    [Fact]
    public void GetColor_ReturnsNonZeroColor()
    {
        // Act
        var color = FileTypeRegistry.GetColor("ddx_3xdo");

        // Assert
        Assert.NotEqual(0u, color);
    }

    [Fact]
    public void GetColor_SameCategoryHasSameColor()
    {
        // Act
        var ddxColor = FileTypeRegistry.GetColor("ddx_3xdo");
        var ddsColor = FileTypeRegistry.GetColor("dds");

        // Assert - both are textures, should have same color
        Assert.Equal(ddxColor, ddsColor);
    }

    #endregion

    #region NormalizeToSignatureId Tests

    [Theory]
    [InlineData("3xdo", "ddx_3xdo")]
    [InlineData("3xdr", "ddx_3xdr")]
    [InlineData("Xbox 360 DDX texture (3XDO format)", "ddx_3xdo")]
    [InlineData("Xbox 360 DDX texture (3XDR engine-tiled format)", "ddx_3xdr")]
    [InlineData("DirectDraw Surface texture", "dds")]
    [InlineData("PNG image", "png")]
    [InlineData("Xbox Media Audio (RIFF/XMA)", "xma")]
    [InlineData("NetImmerse/Gamebryo 3D model", "nif")]
    public void NormalizeToSignatureId_VariousInputs_ReturnsCorrectId(string input, string expected)
    {
        // Act
        var result = FileTypeRegistry.NormalizeToSignatureId(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeToSignatureId_AlreadySignatureId_ReturnsSame()
    {
        // Act
        var result = FileTypeRegistry.NormalizeToSignatureId("ddx_3xdo");

        // Assert
        Assert.Equal("ddx_3xdo", result);
    }

    #endregion

    #region AllTypes Tests

    [Fact]
    public void AllTypes_ContainsExpectedTypes()
    {
        // Act
        var types = FileTypeRegistry.AllTypes;

        // Assert
        Assert.NotEmpty(types);
        Assert.Contains(types, t => t.TypeId == "dds");
        Assert.Contains(types, t => t.TypeId == "ddx");
        Assert.Contains(types, t => t.TypeId == "png");
        Assert.Contains(types, t => t.TypeId == "xma");
        Assert.Contains(types, t => t.TypeId == "nif");
        Assert.Contains(types, t => t.TypeId == "xex");
    }

    [Fact]
    public void AllTypes_EachHasRequiredProperties()
    {
        // Act & Assert
        foreach (var typeDef in FileTypeRegistry.AllTypes)
        {
            Assert.NotNull(typeDef.TypeId);
            Assert.NotNull(typeDef.DisplayName);
            Assert.NotNull(typeDef.Extension);
            Assert.NotEmpty(typeDef.Signatures);
            Assert.True(typeDef.MinSize >= 0);
            Assert.True(typeDef.MaxSize > typeDef.MinSize);
        }
    }

    #endregion

    #region DisplayNames Tests

    [Fact]
    public void DisplayNames_ContainsExpectedNames()
    {
        // Act
        var names = FileTypeRegistry.DisplayNames;

        // Assert
        Assert.NotEmpty(names);
        Assert.Contains("DDS", names);
        Assert.Contains("DDX", names);
        Assert.Contains("PNG", names);
        Assert.Contains("XMA", names);
    }

    #endregion

    #region CategoryColors Tests

    [Fact]
    public void CategoryColors_HasAllCategories()
    {
        // Act
        var colors = FileTypeRegistry.CategoryColors;

        // Assert
        Assert.True(colors.ContainsKey(FileCategory.Texture));
        Assert.True(colors.ContainsKey(FileCategory.Image));
        Assert.True(colors.ContainsKey(FileCategory.Audio));
        Assert.True(colors.ContainsKey(FileCategory.Model));
        Assert.True(colors.ContainsKey(FileCategory.Module));
        Assert.True(colors.ContainsKey(FileCategory.Script));
        Assert.True(colors.ContainsKey(FileCategory.Xbox));
        Assert.True(colors.ContainsKey(FileCategory.Plugin));
    }

    #endregion

    #region GetSignatureIdsForDisplayNames Tests

    [Fact]
    public void GetSignatureIdsForDisplayNames_ReturnsCorrectSignatures()
    {
        // Arrange
        var displayNames = new[] { "DDX", "DDS" };

        // Act
        var signatureIds = FileTypeRegistry.GetSignatureIdsForDisplayNames(displayNames).ToList();

        // Assert
        Assert.Contains("ddx_3xdo", signatureIds);
        Assert.Contains("ddx_3xdr", signatureIds);
        Assert.Contains("dds", signatureIds);
    }

    #endregion
}
