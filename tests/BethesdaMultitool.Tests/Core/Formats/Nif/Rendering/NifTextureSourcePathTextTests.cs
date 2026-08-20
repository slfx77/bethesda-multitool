using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifTextureSourcePathTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ; ; ")]
    public void ParseOverride_NoPathEntries_ReturnsNull(string? text)
    {
        Assert.Null(NifTextureSourcePathText.ParseOverride(text));
    }

    [Fact]
    public void ParseOverride_SplitsTrimsAndDeduplicatesSemicolonAndNewlineEntries()
    {
        const string text =
            " C:\\Game\\Textures.bsa ; ; D:\\Game\\Materials.ba2\r\n\r\n ; " +
            "c:\\game\\TEXTURES.bsa\n C:\\Loose\\textures ";

        var paths = Assert.IsType<string[]>(NifTextureSourcePathText.ParseOverride(text));

        Assert.Equal(
            [@"C:\Game\Textures.bsa", @"D:\Game\Materials.ba2", @"C:\Loose\textures"],
            paths);
    }

    [Fact]
    public void Format_UsesTheSameDelimiterThatParseOverrideConsumes()
    {
        string[] paths = [@"C:\Game\Textures.bsa", @"D:\Game\Materials.ba2"];

        var display = NifTextureSourcePathText.Format(paths);

        Assert.Equal(@"C:\Game\Textures.bsa; D:\Game\Materials.ba2", display);
        Assert.Equal(paths, Assert.IsType<string[]>(NifTextureSourcePathText.ParseOverride(display)));
    }
}