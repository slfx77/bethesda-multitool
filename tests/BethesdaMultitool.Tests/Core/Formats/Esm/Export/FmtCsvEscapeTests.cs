using BethesdaMultitool.Core.Formats.Esm.Export;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export;

/// <summary>
///     Characterization tests for <see cref="Fmt.CsvEscape" /> after replacing four sequential
///     <c>string.Contains</c> scans with a cached <c>SearchValues</c> + single <c>IndexOfAny</c>.
/// </summary>
public class FmtCsvEscapeTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("no special chars here", "no special chars here")]
    public void CsvEscape_NoSpecialChars_ReturnsUnquoted(string? input, string expected)
    {
        Assert.Equal(expected, Fmt.CsvEscape(input));
    }

    [Theory]
    [InlineData("a,b", "\"a,b\"")]                 // comma
    [InlineData("line1\nline2", "\"line1\nline2\"")] // newline
    [InlineData("cr\rhere", "\"cr\rhere\"")]        // carriage return
    public void CsvEscape_DelimiterChars_AreQuoted(string input, string expected)
    {
        Assert.Equal(expected, Fmt.CsvEscape(input));
    }

    [Theory]
    [InlineData("a\"b", "\"a\"\"b\"")]   // embedded quote is doubled and the whole field quoted
    [InlineData("\"", "\"\"\"\"")]        // a lone quote -> "" wrapped in quotes => four quotes
    public void CsvEscape_EmbeddedQuotes_AreDoubledAndQuoted(string input, string expected)
    {
        Assert.Equal(expected, Fmt.CsvEscape(input));
    }
}
