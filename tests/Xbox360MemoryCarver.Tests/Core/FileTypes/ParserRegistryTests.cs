using Xunit;
using Xbox360MemoryCarver.Core.FileTypes;
using Xbox360MemoryCarver.Core.Parsers;

namespace Xbox360MemoryCarver.Tests.Core.FileTypes;

/// <summary>
/// Tests for ParserRegistry.
/// </summary>
public class ParserRegistryTests
{
    #region GetParserForSignature Tests

    [Theory]
    [InlineData("ddx_3xdo", typeof(DdxParser))]
    [InlineData("ddx_3xdr", typeof(DdxParser))]
    [InlineData("dds", typeof(DdsParser))]
    [InlineData("png", typeof(PngParser))]
    [InlineData("xma", typeof(XmaParser))]
    [InlineData("nif", typeof(NifParser))]
    [InlineData("xex", typeof(XexParser))]
    [InlineData("xdbf", typeof(XdbfParser))]
    [InlineData("xui_scene", typeof(XuiParser))]
    [InlineData("xui_binary", typeof(XuiParser))]
    [InlineData("esp", typeof(EspParser))]
    [InlineData("lip", typeof(LipParser))]
    [InlineData("script_scn", typeof(ScriptParser))]
    public void GetParserForSignature_KnownSignatures_ReturnsCorrectParser(string signatureId, Type expectedType)
    {
        // Act
        var parser = ParserRegistry.GetParserForSignature(signatureId);

        // Assert
        Assert.NotNull(parser);
        Assert.IsType(expectedType, parser);
    }

    [Fact]
    public void GetParserForSignature_UnknownSignature_ReturnsNull()
    {
        // Act
        var parser = ParserRegistry.GetParserForSignature("unknown_signature");

        // Assert
        Assert.Null(parser);
    }

    [Fact]
    public void GetParserForSignature_ReturnsCachedInstance()
    {
        // Act
        var parser1 = ParserRegistry.GetParserForSignature("dds");
        var parser2 = ParserRegistry.GetParserForSignature("dds");

        // Assert
        Assert.NotNull(parser1);
        Assert.NotNull(parser2);
        Assert.Same(parser1, parser2); // Should be the same cached instance
    }

    #endregion

    #region GetParser Tests

    [Fact]
    public void GetParser_WithTypeDef_ReturnsParser()
    {
        // Arrange
        var typeDef = FileTypeRegistry.GetByTypeId("dds");

        // Act
        var parser = ParserRegistry.GetParser(typeDef);

        // Assert
        Assert.NotNull(parser);
        Assert.IsType<DdsParser>(parser);
    }

    [Fact]
    public void GetParser_NullTypeDef_ReturnsNull()
    {
        // Act
        var parser = ParserRegistry.GetParser(null);

        // Assert
        Assert.Null(parser);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void AllFileTypes_HaveParsers()
    {
        // Act & Assert
        foreach (var typeDef in FileTypeRegistry.AllTypes)
        {
            var parser = ParserRegistry.GetParser(typeDef);
            Assert.NotNull(parser);
        }
    }

    [Fact]
    public void AllParsers_ImplementIFileParser()
    {
        // Act & Assert
        foreach (var typeDef in FileTypeRegistry.AllTypes)
        {
            var parser = ParserRegistry.GetParser(typeDef);
            Assert.NotNull(parser);
            Assert.IsAssignableFrom<IFileParser>(parser);
        }
    }

    #endregion
}
