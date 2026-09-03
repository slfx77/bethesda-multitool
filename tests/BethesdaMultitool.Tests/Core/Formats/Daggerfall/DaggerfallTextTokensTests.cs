using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     The TEXT.RSC/BOOKS byte grammar: line controls, prefix arguments, subrecord splitting and
///     the code-page-437 reading of high bytes.
/// </summary>
public class DaggerfallTextTokensTests
{
    [Fact]
    public void RenderPlain_TextRscStyle_JustifyBytesTerminateLines()
    {
        // Retail record 0 opens exactly like this: the attribute name is centred (0xFD after it),
        // then left-justified lines each ending in 0xFC.
        byte[] bytes = [.. "STRENGTH"u8, 0xFD, .. " Strength governs encumbrance"u8, 0xFC, .. " and more."u8, 0xFC];

        Assert.Equal("STRENGTH\nStrength governs encumbrance\nand more.", DaggerfallTextTokens.RenderPlain(bytes));
    }

    [Fact]
    public void RenderPlain_BookStyle_NewlinesAndPrefixesCollapseToReadableText()
    {
        // BOK00000 page 0 opens with two blank lines, a font select, a centred title, three blank
        // lines, another font select and the body.
        byte[] bytes =
        [
            0x00, 0x00, 0xF9, 0x02, 0x00, 0x00, 0xFD, .. "The First Scroll"u8, 0x00, 0x00, 0x00,
            0xF9, 0x04, 0x00, 0x00, .. " What follows is a translation"u8, 0x00
        ];

        Assert.Equal("The First Scroll\n\nWhat follows is a translation", DaggerfallTextTokens.RenderPlain(bytes));
    }

    [Fact]
    public void RenderPlain_PrefixArgumentsAreNeverReadAsText()
    {
        // 0x41 is 'A'; as a position/font argument it must not print.
        byte[] positioned = [.. "left"u8, 0xFB, 0x41, .. "right"u8];
        byte[] font = [0xF9, 0x41, .. "x"u8];

        Assert.Equal("left right", DaggerfallTextTokens.RenderPlain(positioned));
        Assert.Equal("x", DaggerfallTextTokens.RenderPlain(font));
    }

    [Fact]
    public void RenderPlain_HighBytesMapThroughCodePage437()
    {
        // BOK10000 is written in German: 0x81/0x84/0x94/0xE1 are ü/ä/ö/ß in the DOS code page.
        byte[] bytes = [.. "verk"u8, 0x81, .. "ndet, da"u8, 0xE1, .. " die G"u8, 0x94, .. "tter"u8];

        Assert.Equal("verkündet, daß die Götter", DaggerfallTextTokens.RenderPlain(bytes));
        Assert.Equal(128, DaggerfallTextTokens.CodePage437High.Length);
    }

    [Fact]
    public void RenderPlain_StopsAtEndOfRecord_AndIgnoresSilentControls()
    {
        byte[] bytes = [0x01, .. "abc"u8, 0x02, 0xF8, .. "d"u8, 0xFE, .. "never"u8];

        Assert.Equal("abcd", DaggerfallTextTokens.RenderPlain(bytes));
    }

    [Fact]
    public void RenderPlain_EmptyInput_IsEmpty()
    {
        Assert.Equal(string.Empty, DaggerfallTextTokens.RenderPlain([]));
        Assert.Equal(string.Empty, DaggerfallTextTokens.RenderPlain([0x00, 0x00, 0xFD]));
    }

    [Fact]
    public void SplitSubrecords_SplitsAtSeparators_AndSkipsSeparatorLookingArguments()
    {
        byte[] bytes = [.. "a"u8, 0xFF, .. "b"u8, 0xFB, 0xFF, .. "c"u8, 0xFF];

        var parts = DaggerfallTextTokens.SplitSubrecords(bytes);

        Assert.Equal(3, parts.Count);
        Assert.Equal("a", DaggerfallTextTokens.RenderPlain(parts[0].Span));
        Assert.Equal("b c", DaggerfallTextTokens.RenderPlain(parts[1].Span));
        Assert.Equal(0, parts[2].Length);
    }

    [Fact]
    public void SplitSubrecords_WithoutSeparators_IsOneSubrecord_EvenWhenEmpty()
    {
        Assert.Single(DaggerfallTextTokens.SplitSubrecords("plain"u8.ToArray()));
        Assert.Single(DaggerfallTextTokens.SplitSubrecords(new byte[0]));

        // Nothing past an end-of-record byte belongs to the record.
        byte[] bytes = [.. "x"u8, 0xFE, 0xFF, .. "y"u8];
        var parts = DaggerfallTextTokens.SplitSubrecords(bytes);
        Assert.Single(parts);
        Assert.Equal(1, parts[0].Length);
    }
}
