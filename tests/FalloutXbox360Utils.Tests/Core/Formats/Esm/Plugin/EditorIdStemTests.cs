using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Phase C: pin the conservative EditorID stem-normalizer behavior. Wider regex
///     patterns are intentionally NOT covered (new/old/alt/temp/test/v\d+) — they should
///     fall through unchanged until census evidence justifies widening.
/// </summary>
public class EditorIdStemTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_NullOrEmpty_ReturnsNull(string? input)
    {
        Assert.Null(EditorIdStem.Normalize(input));
    }

    [Fact]
    public void Normalize_NoSuffix_LowercasesOnly()
    {
        Assert.Equal("mystat", EditorIdStem.Normalize("MyStat"));
        Assert.Equal("lucky38base", EditorIdStem.Normalize("Lucky38Base"));
    }

    // Disambiguation-letter rename suffix — the case that motivated this phase.
    // Prototype `SCOLParkingLotChunk05` should produce the SAME stem as master
    // `SCOLParkingLotChunk05b`. The chunk number is preserved so that 01/02/03/04/05
    // don't collapse onto each other.
    [Theory]
    [InlineData("SCOLParkingLotChunk03", "scolparkinglotchunk03")]
    [InlineData("SCOLParkingLotChunk03b", "scolparkinglotchunk03")]
    [InlineData("SCOLParkingLotChunk05", "scolparkinglotchunk05")]
    [InlineData("SCOLParkingLotChunk05b", "scolparkinglotchunk05")]
    [InlineData("FXGlow02", "fxglow02")]
    [InlineData("Mystat3", "mystat3")]
    [InlineData("Mystat3a", "mystat3")]
    public void Normalize_StripsDisambiguationLetterAfterDigit(string input, string expected)
    {
        Assert.Equal(expected, EditorIdStem.Normalize(input));
    }

    // Letters that don't follow a digit are part of the stem; never strip them.
    [Theory]
    [InlineData("Lucky38Base", "lucky38base")]
    [InlineData("FooBar", "foobar")]
    public void Normalize_DoesNotStripTrailingLetterWithoutPrecedingDigit(string input, string expected)
    {
        Assert.Equal(expected, EditorIdStem.Normalize(input));
    }

    // FNV-vs-FO3 rename suffix `nv` / `_nv` — common pattern when prototypes carry FO3
    // names and FNV master records have the `_nv` rename.
    [Theory]
    [InlineData("MonorailPlatform_NV", "monorailplatform")]
    [InlineData("MonorailPlatformNV", "monorailplatform")]
    [InlineData("Lucky38Base_NV", "lucky38base")]
    public void Normalize_StripsNvRenameSuffix(string input, string expected)
    {
        Assert.Equal(expected, EditorIdStem.Normalize(input));
    }

    // Patterns we explicitly left OUT of the conservative regex — these must NOT change.
    [Theory]
    [InlineData("MyNew", "mynew")]
    [InlineData("MyOld", "myold")]
    [InlineData("MyAlt", "myalt")]
    [InlineData("MyTemp", "mytemp")]
    [InlineData("MyTest", "mytest")]
    [InlineData("MyVersionV2", "myversionv2")] // no trailing letter after digit → unchanged
    public void Normalize_ConservativeMode_DoesNotStripWiderSuffixes(string input, string expected)
    {
        Assert.Equal(expected, EditorIdStem.Normalize(input));
    }

    [Fact]
    public void Normalize_SuffixOnlyInput_ReturnsNull()
    {
        // Strings that are entirely a strippable suffix have no meaningful stem.
        Assert.Null(EditorIdStem.Normalize("NV"));
        Assert.Null(EditorIdStem.Normalize("_NV"));
    }

    [Fact]
    public void Normalize_SingleChar_PassesThrough()
    {
        Assert.Equal("a", EditorIdStem.Normalize("A"));
    }

    [Fact]
    public void Normalize_IsIdempotentForAlreadyStemmedInputs()
    {
        var first = EditorIdStem.Normalize("SCOLParkingLotChunk03");
        var second = EditorIdStem.Normalize(first);

        Assert.Equal(first, second);
    }
}
