using BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins the schema-path DIAL/INFO extractor selection. FNV/FO3 and Morrowind never route here
///     (typed-handler pipeline / TES3 parser fork respectively), while Starfield has no generated
///     schema. Those unsupported games must still be rejected so accidentally activating this path
///     cannot reinterpret their dialogue as Oblivion records.
/// </summary>
public sealed class DialogueExtractorsTests
{
    [Theory]
    [InlineData(BethesdaGame.Oblivion, typeof(OblivionDialogueExtractor))]
    [InlineData(BethesdaGame.Skyrim, typeof(SkyrimDialogueExtractor))]
    [InlineData(BethesdaGame.Fallout4, typeof(Fallout4DialogueExtractor))]
    [InlineData(BethesdaGame.Fallout76, typeof(Fallout4DialogueExtractor))]
    public void For_SupportedSchemaPrimaryGame_ReturnsItsExtractor(BethesdaGame game, Type expected)
    {
        Assert.IsType(expected, DialogueExtractors.For(game));
    }

    [Theory]
    [InlineData(BethesdaGame.Unknown)]
    [InlineData(BethesdaGame.Morrowind)]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Starfield)]
    public void For_GameWithoutSchemaPrimaryDialogueLayout_Throws(BethesdaGame game)
    {
        var exception = Assert.Throws<NotSupportedException>(() => DialogueExtractors.For(game));

        Assert.Contains(game.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void For_UndefinedGame_ThrowsArgumentOutOfRange()
    {
        var game = (BethesdaGame)int.MaxValue;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => DialogueExtractors.For(game));

        Assert.Equal("game", exception.ParamName);
        Assert.Equal(game, exception.ActualValue);
    }
}
