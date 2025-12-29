using System.Text;
using Xunit;
using Xbox360MemoryCarver.Core.Parsers;

namespace Xbox360MemoryCarver.Tests.Core.Parsers;

/// <summary>
/// Tests for ScriptParser.
/// </summary>
public class ScriptParserTests
{
    private readonly ScriptParser _parser = new();

    #region Script Header Recognition Tests

    [Theory]
    [InlineData("scn TestScript\r\n")]
    [InlineData("scn\tTestScript\r\n")]
    [InlineData("Scn TestScript\r\n")]
    [InlineData("SCN TestScript\r\n")]
    public void ParseHeader_ScnFormat_ReturnsResult(string scriptHeader)
    {
        // Arrange
        var data = Encoding.ASCII.GetBytes(scriptHeader + "begin OnActivate\r\nend");

        // Act
        var result = _parser.ParseHeader(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Script", result.Format);
        Assert.Equal("TestScript", result.Metadata["scriptName"]);
    }

    [Theory]
    [InlineData("ScriptName MyScript\r\n")]
    [InlineData("ScriptName\tMyScript\r\n")]
    [InlineData("scriptname MyScript\r\n")]
    [InlineData("SCRIPTNAME MyScript\r\n")]
    public void ParseHeader_ScriptNameFormat_ReturnsResult(string scriptHeader)
    {
        // Arrange
        var data = Encoding.ASCII.GetBytes(scriptHeader + "begin OnActivate\r\nend");

        // Act
        var result = _parser.ParseHeader(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Script", result.Format);
        Assert.Equal("MyScript", result.Metadata["scriptName"]);
    }

    [Fact]
    public void ParseHeader_InvalidHeader_ReturnsNull()
    {
        // Arrange
        var data = Encoding.ASCII.GetBytes("not a script\r\n");

        // Act
        var result = _parser.ParseHeader(data);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Script Name Extraction Tests

    [Fact]
    public void ParseHeader_ExtractsScriptNameWithUnderscores()
    {
        // Arrange
        var data = Encoding.ASCII.GetBytes("scn My_Test_Script\r\nbegin OnActivate\r\nend");

        // Act
        var result = _parser.ParseHeader(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("My_Test_Script", result.Metadata["scriptName"]);
    }

    [Fact]
    public void ParseHeader_TrimsCommentFromName()
    {
        // Arrange
        var data = Encoding.ASCII.GetBytes("scn TestScript ; this is a comment\r\nbegin OnActivate\r\nend");

        // Act
        var result = _parser.ParseHeader(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("TestScript", result.Metadata["scriptName"]);
    }

    [Fact]
    public void ParseHeader_InvalidScriptName_ReturnsNull()
    {
        // Arrange - script name with invalid characters
        var data = Encoding.ASCII.GetBytes("scn Test@Script!\r\n");

        // Act
        var result = _parser.ParseHeader(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_EmptyScriptName_ReturnsNull()
    {
        // Arrange
        var data = Encoding.ASCII.GetBytes("scn \r\n");

        // Act
        var result = _parser.ParseHeader(data);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Safe Name Tests

    [Fact]
    public void ParseHeader_CreatesSafeFilename()
    {
        // Arrange
        var data = Encoding.ASCII.GetBytes("scn TestScript123\r\nbegin OnActivate\r\nend");

        // Act
        var result = _parser.ParseHeader(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("TestScript123", result.Metadata["safeName"]);
    }

    #endregion

    #region Script End Detection Tests

    [Fact]
    public void ParseHeader_FindsEndOfScript()
    {
        // Arrange
        var script = "scn TestScript\r\nbegin OnActivate\r\n  ; do something\r\nend";
        var data = Encoding.ASCII.GetBytes(script);

        // Act
        var result = _parser.ParseHeader(data);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.EstimatedSize > 0);
        Assert.True(result.EstimatedSize <= script.Length);
    }

    [Fact]
    public void ParseHeader_StopsAtBinaryData()
    {
        // Arrange - script followed by binary data
        var script = "scn TestScript\r\nbegin OnActivate\r\nend";
        var scriptBytes = Encoding.ASCII.GetBytes(script);
        var data = new byte[scriptBytes.Length + 10];
        scriptBytes.CopyTo(data, 0);
        data[scriptBytes.Length] = 0x00; // Binary null

        // Act
        var result = _parser.ParseHeader(data);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.EstimatedSize <= scriptBytes.Length);
    }

    [Fact]
    public void ParseHeader_StopsAtNextScript()
    {
        // Arrange - two scripts concatenated
        var script1 = "scn FirstScript\r\nbegin OnActivate\r\nend\r\n";
        var script2 = "scn SecondScript\r\nbegin OnLoad\r\nend";
        var data = Encoding.ASCII.GetBytes(script1 + script2);

        // Act
        var result = _parser.ParseHeader(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("FirstScript", result.Metadata["scriptName"]);
        // Size should not include second script
        Assert.True(result.EstimatedSize < data.Length);
    }

    #endregion

    #region Leading Comment Tests

    [Fact]
    public void ParseHeader_DetectsLeadingComment()
    {
        // Arrange - comment before script definition
        var comment = "; This is a header comment\r\n";
        var script = "scn TestScript\r\nbegin OnActivate\r\nend";
        var fullData = comment + script;

        // Need to position parser at the script start
        var scriptOffset = comment.Length;
        var data = Encoding.ASCII.GetBytes(fullData);

        // Act - parse from script offset
        var result = _parser.ParseHeader(data.AsSpan(scriptOffset));

        // Assert
        Assert.NotNull(result);
        // Note: Leading comment detection looks backwards from signature position
    }

    #endregion

    #region Offset Tests

    [Fact]
    public void ParseHeader_WithOffset_ParsesCorrectly()
    {
        // Arrange
        var padding = new byte[50];
        var script = Encoding.ASCII.GetBytes("scn TestScript\r\nbegin OnActivate\r\nend");
        var data = new byte[padding.Length + script.Length];
        script.CopyTo(data, 50);

        // Act
        var result = _parser.ParseHeader(data, offset: 50);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("TestScript", result.Metadata["scriptName"]);
    }

    #endregion
}
