using BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins the schema-path DIAL/INFO extractor selection for every game. FNV/FO3 and Morrowind
///     never route here (typed-handler pipeline / TES3 parser fork respectively), but their mapping
///     is still pinned so an accidental schema-path activation for them fails loudly here instead
///     of silently mis-parsing dialogue with Oblivion's layout. Starfield lands on the Oblivion arm
///     (historical switch behavior; unreachable — no generated schema).
/// </summary>
public sealed class DialogueExtractorsTests
{
    [Theory]
    [InlineData(BethesdaGame.Skyrim, typeof(SkyrimDialogueExtractor))]
    [InlineData(BethesdaGame.Fallout4, typeof(Fallout4DialogueExtractor))]
    [InlineData(BethesdaGame.Fallout76, typeof(Fallout4DialogueExtractor))]
    [InlineData(BethesdaGame.Oblivion, typeof(OblivionDialogueExtractor))]
    [InlineData(BethesdaGame.Morrowind, typeof(OblivionDialogueExtractor))]
    [InlineData(BethesdaGame.Fallout3, typeof(OblivionDialogueExtractor))]
    [InlineData(BethesdaGame.FalloutNewVegas, typeof(OblivionDialogueExtractor))]
    [InlineData(BethesdaGame.Starfield, typeof(OblivionDialogueExtractor))]
    [InlineData(BethesdaGame.Unknown, typeof(OblivionDialogueExtractor))]
    public void For_PinsExtractorPerGame(BethesdaGame game, Type expected)
    {
        Assert.IsType(expected, DialogueExtractors.For(game));
    }
}