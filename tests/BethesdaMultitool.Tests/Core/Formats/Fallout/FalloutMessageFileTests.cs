using System.Text;
using BethesdaMultitool.Core.Formats.Fallout;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Fallout;

/// <summary>
///     Vectors for Fallout's <c>.MSG</c> text files, shaped after the 27 retail
///     <c>TEXT\ENGLISH\GAME</c> files (13,189 entries) measured 2026-09-06: no entry's text contains
///     a brace, none spans a line, and 14 ids are duplicated with DIFFERENT text.
/// </summary>
public sealed class FalloutMessageFileTests
{
    private static FalloutMessageFile Parse(string text)
    {
        return FalloutMessageFile.Parse(Encoding.Latin1.GetBytes(text), "TEST.MSG");
    }

    [Fact]
    public void Parse_ReadsTheThreeBraceFields()
    {
        var msg = Parse("{100}{}{Leather Armor}\n{101}{}{Your basic all leather apparel.}\n");

        Assert.Equal(2, msg.Count);
        Assert.Equal("Leather Armor", msg.Find(100));
        Assert.Equal("Your basic all leather apparel.", msg.Find(101));
        Assert.Null(msg.Find(102));
    }

    [Fact]
    public void Parse_SkipsCommentsAndBlankLines()
    {
        var msg = Parse("#\n# Messages for Prototypes\n#\n\n{10}{}{<None>}\n");

        Assert.Equal(1, msg.Count);
        Assert.Equal("<None>", msg.Find(10));
    }

    [Fact]
    public void Parse_ReadsAnEntryWhoseTextSpansLines()
    {
        // ⚠⚠ 3,962 of the 23,126 dialogue entries wrap across lines, while NONE of the 13,189
        // game-text entries do. A line-oriented reader looks perfect on the game text and silently
        // mangles a sixth of the dialogue — which is exactly what the first version of this reader
        // did, because it was written against the game files alone.
        var msg = Parse("{100}{}{This line wraps\nand continues here.}\n{101}{}{Next}\n");

        Assert.Equal("This line wraps\nand continues here.", msg.Find(100));
        Assert.Equal("Next", msg.Find(101));
    }

    [Fact]
    public void Parse_ReadsAnEntryWhoseFieldsAreSeparatedByNewlines()
    {
        var msg = Parse("{200}\n{VOICE01}\n{Spoken line.}\n");

        Assert.Equal("Spoken line.", msg.Find(200));
    }

    [Fact]
    public void Parse_KeepsTheLastOfADuplicatedId()
    {
        // ⚠ Not a tie-break of convenience. PRO_SCEN.MSG holds 85400 = "Sign" and 85401 twice —
        // "Maltese Falcon" then "This is a neon sign for the Maltese Falcon." A prototype's
        // description is its name's id plus one, so 85401 is Sign's description and only the
        // SECOND entry reads as one. Taking the first puts a stray name where prose belongs.
        var msg = Parse("{85400}{}{Sign}\n{85401}{}{Maltese Falcon}\n{85401}{}{This is a neon sign.}\n");

        Assert.Equal("This is a neon sign.", msg.Find(85401));
        Assert.Equal(1, msg.DuplicateIds);
        Assert.Equal(2, msg.Count);
    }

    [Fact]
    public void Parse_CarriesTheAudioFieldWithoutNeedingIt()
    {
        var msg = Parse("{500}{VAULT01}{Hello.}\n");

        Assert.Equal("Hello.", msg.Find(500));
    }

    [Fact]
    public void Parse_SurvivesMalformedContentWithoutThrowing()
    {
        // A damaged file in a mod must not take the whole read down with it.
        // ⚠ Note what "malformed" can mean once entries may span lines: a truncated `{101}{}` is
        // NOT skippable in isolation, because the very next `{...}` legitimately completes it as
        // that entry's text. That is inherent to the format, not a reader defect — so this asserts
        // the surrounding entries survive rather than pretending the middle one is discarded.
        var msg = Parse("{100}{}{Good}\n{101}{}\n{oops}\n{102}{}{Also good}\n{103}{}{unterminated");

        Assert.Equal("Good", msg.Find(100));
        Assert.Equal("Also good", msg.Find(102));
        Assert.Null(msg.Find(103));   // no closing brace, so it is not an entry
    }

    [Fact]
    public void Parse_ReadsHighByteCharacters()
    {
        // DOS code page 437 text round-trips through Latin-1 byte-for-byte.
        var msg = FalloutMessageFile.Parse([.. "{1}{}{caf"u8, 0xE9, .. "}"u8], "T.MSG");

        Assert.Equal("café", msg.Find(1));
    }
}
