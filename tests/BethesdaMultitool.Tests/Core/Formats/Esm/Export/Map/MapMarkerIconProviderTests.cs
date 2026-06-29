using BethesdaMultitool.Core.Formats.Esm.Export.Map;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export.Map;

/// <summary>
///     Verifies every catalog icon key actually resolves to an embedded PNG in the assembly — a headless
///     gate against a missing/renamed asset or a broken csproj <c>EmbeddedResource</c> glob (the GPU blit
///     itself can't be tested without a device).
/// </summary>
public class MapMarkerIconProviderTests
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void EveryCatalogIconKey_ResolvesToEmbeddedPng(BethesdaGame game)
    {
        var withIcons = MapMarkerCatalog.For(game)
            .Where(e => !string.IsNullOrEmpty(e.IconKey))
            .ToList();
        Assert.NotEmpty(withIcons);

        foreach (var entry in withIcons)
        {
            var png = MapMarkerIconProvider.GetIconPng(entry.IconKey);
            Assert.True(png is not null, $"{game} '{entry.DisplayName}' icon key '{entry.IconKey}' is not embedded");
            Assert.True(png!.Length > PngMagic.Length && png.AsSpan(0, 4).SequenceEqual(PngMagic),
                $"'{entry.IconKey}' is not a PNG");
        }
    }

    [Theory]
    [InlineData(BethesdaGame.Skyrim, 52)]
    [InlineData(BethesdaGame.Fallout4, 81)]
    [InlineData(BethesdaGame.Fallout76, 96)]
    public void EmbeddedGame_HasExpectedIconCount(BethesdaGame game, int expected)
    {
        var count = MapMarkerCatalog.For(game)
            .Count(e => !string.IsNullOrEmpty(e.IconKey) && MapMarkerIconProvider.GetIconPng(e.IconKey) is not null);
        Assert.Equal(expected, count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("does_not_exist")]
    public void GetIconPng_UnknownStem_ReturnsNull(string key)
    {
        Assert.Null(MapMarkerIconProvider.GetIconPng(key));
    }
}
