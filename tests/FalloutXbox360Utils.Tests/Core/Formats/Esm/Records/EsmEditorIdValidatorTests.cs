using FalloutXbox360Utils.Core.Formats.Esm;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Records;

/// <summary>
///     Tests for <see cref="EsmEditorIdValidator" /> covering valid/invalid editor IDs
///     and repeated-pattern detection.
/// </summary>
public class EsmEditorIdValidatorTests
{
    #region IsValidEditorId — Valid Cases

    [Theory]
    [InlineData("WeapPlasmaPistol")]
    [InlineData("NPCDocMitchell")]
    [InlineData("MS01")]
    [InlineData("DialogueTopic")]
    [InlineData("NVDLC01SomeQuest")]
    [InlineData("ab")] // Minimum length (2 chars)
    [InlineData("Test_Name")] // Underscore is valid
    [InlineData("x_y")] // Short with underscore
    [InlineData("A1B2C3")] // Mixed alphanumeric
    [InlineData("9mmPistol")] // Starts with digit
    public void IsValidEditorId_ValidNames_ReturnsTrue(string name)
    {
        Assert.True(EsmEditorIdValidator.IsValidEditorId(name));
    }

    #endregion

    #region IsValidEditorId — Invalid Cases

    [Fact]
    public void IsValidEditorId_EmptyString_ReturnsFalse()
    {
        Assert.False(EsmEditorIdValidator.IsValidEditorId(""));
    }

    [Fact]
    public void IsValidEditorId_SingleChar_ReturnsFalse()
    {
        // Length < 2 is rejected
        Assert.False(EsmEditorIdValidator.IsValidEditorId("A"));
    }

    [Fact]
    public void IsValidEditorId_TooLong_ReturnsFalse()
    {
        // 201 chars exceeds the 200 limit
        var longName = new string('A', 201);
        Assert.False(EsmEditorIdValidator.IsValidEditorId(longName));
    }

    [Fact]
    public void IsValidEditorId_ExactlyMaxLength_ReturnsTrue()
    {
        // 200 chars should be accepted (use non-repeating pattern to avoid HasRepeatedPattern)
        var maxName = string.Concat(Enumerable.Range(0, 20).Select(i => "AbCdEfGhIj"));
        Assert.Equal(200, maxName.Length);
        Assert.True(EsmEditorIdValidator.IsValidEditorId(maxName));
    }

    [Theory]
    [InlineData("_Leading")] // Starts with underscore
    [InlineData(".dotstart")] // Starts with dot
    [InlineData(" space")] // Starts with space
    public void IsValidEditorId_InvalidStartChar_ReturnsFalse(string name)
    {
        Assert.False(EsmEditorIdValidator.IsValidEditorId(name));
    }

    [Theory]
    [InlineData("Name With Spaces")]
    [InlineData("Path\\Slash")]
    [InlineData("Has.Dot")]
    [InlineData("Has-Dash")]
    [InlineData("Tab\there")]
    [InlineData("New\nLine")]
    [InlineData("Control\x01Char")]
    public void IsValidEditorId_InvalidChars_ReturnsFalse(string name)
    {
        Assert.False(EsmEditorIdValidator.IsValidEditorId(name));
    }

    [Fact]
    public void IsValidEditorId_RepeatedPattern_LongString_ReturnsFalse()
    {
        // "ABABABABABAB" is 12 chars (>= 8) with pattern "AB" repeating 6 times (>= 3)
        Assert.False(EsmEditorIdValidator.IsValidEditorId("ABABABABABAB"));
    }

    [Fact]
    public void IsValidEditorId_RepeatedPattern_ShortString_ReturnsTrue()
    {
        // "ABABAB" is only 6 chars (< 8), so repeated pattern check is skipped
        Assert.True(EsmEditorIdValidator.IsValidEditorId("ABABAB"));
    }

    #endregion

    #region HasRepeatedPattern

    [Theory]
    [InlineData("AAAAAAAAA")] // Pattern "AA" repeats 4+ times
    [InlineData("ABABABABAB")] // Pattern "AB" repeats 5 times
    [InlineData("katSkatSkatS")] // Pattern "katS" repeats 3 times
    [InlineData("xyzxyzxyz")] // Pattern "xyz" repeats 3 times
    [InlineData("abcdefabcdefabcdef")] // Pattern "abcdef" repeats 3 times
    public void HasRepeatedPattern_RepeatingStrings_ReturnsTrue(string s)
    {
        Assert.True(EsmEditorIdValidator.HasRepeatedPattern(s));
    }

    [Theory]
    [InlineData("Normal")]
    [InlineData("WeapPistol")]
    [InlineData("DialogueTopic01")]
    [InlineData("ABCDEFGHIJ")] // All different chars
    [InlineData("TestName")] // Real-looking editor ID
    public void HasRepeatedPattern_NonRepeatingStrings_ReturnsFalse(string s)
    {
        Assert.False(EsmEditorIdValidator.HasRepeatedPattern(s));
    }

    [Fact]
    public void HasRepeatedPattern_PatternRepeatsTwice_ReturnsFalse()
    {
        // "ABAB" only repeats 2 times (needs >= 3)
        Assert.False(EsmEditorIdValidator.HasRepeatedPattern("ABAB"));
    }

    [Fact]
    public void HasRepeatedPattern_PatternRepeatsExactlyThree_ReturnsTrue()
    {
        // "ABABAB" has pattern "AB" repeating exactly 3 times
        Assert.True(EsmEditorIdValidator.HasRepeatedPattern("ABABAB"));
    }

    [Fact]
    public void HasRepeatedPattern_SingleCharPattern_ReturnsFalse()
    {
        // Pattern length starts at 2, so single-char repeats are not detected
        // "AAA" has no 2-char pattern that repeats 3 times (only 1 full fit of "AA" + partial)
        // Actually "AA" fits once at offset 0, but offset 2 is just "A" — only 1 repeat
        Assert.False(EsmEditorIdValidator.HasRepeatedPattern("AAA"));
    }

    [Fact]
    public void HasRepeatedPattern_EmptyString_ReturnsFalse()
    {
        Assert.False(EsmEditorIdValidator.HasRepeatedPattern(""));
    }

    #endregion
}