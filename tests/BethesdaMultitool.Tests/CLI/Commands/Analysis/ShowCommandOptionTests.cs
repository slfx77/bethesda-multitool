using System.CommandLine;
using System.Text;
using BethesdaMultitool.CLI.Commands.Analysis;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Analysis;

/// <summary>
///     <c>show --full</c> and its alias <c>--no-truncate</c>. Before, <c>show</c> declared no options at all, so
///     either spelling was an "Unrecognized command or argument" parse error and there was no way to print a
///     script, message or book body past its 2,000-character cap.
/// </summary>
public sealed class ShowCommandOptionTests
{
    private const uint SmokeMessageFormId = 0x00AB0400;
    private const string SmokeMessageEditorId = "FullTextSmokeMessage";
    private const string TextSentinel = "SMOKE_TAIL_SENTINEL";
    private const int SmokeTextLength = 3000;

    [Theory]
    [InlineData("--full")]
    [InlineData("--no-truncate")]
    public void Show_ParsesFullAndNoTruncateAlias(string spelling)
    {
        var show = ShowCommand.Create();
        var root = new RootCommand("test");
        root.Subcommands.Add(show);

        var parsed = root.Parse(["show", "x.esm", "BooneSCRIPT", spelling]);

        Assert.Empty(parsed.Errors);
        Assert.True(parsed.GetValue(FullOption(show)));
    }

    [Fact]
    public void Show_WithoutFull_KeepsTheDefault()
    {
        var show = ShowCommand.Create();
        var root = new RootCommand("test");
        root.Subcommands.Add(show);

        var parsed = root.Parse(["show", "x.esm", "BooneSCRIPT"]);

        Assert.Empty(parsed.Errors);
        Assert.False(parsed.GetValue(FullOption(show)));
    }

    [Fact]
    public void Show_FullOption_DeclaresItsAliasAndDescription()
    {
        var full = FullOption(ShowCommand.Create());

        Assert.Contains("--no-truncate", full.Aliases);
        Assert.Contains("verbatim", full.Description, StringComparison.Ordinal);
    }

    /// <summary>
    ///     End to end through the SHIPPED exe (see <see cref="CliExeRunner" />) on a synthetic FalloutNV.esm
    ///     whose one MESG carries 3,000 characters of CRLF-broken, TAB-indented text. The exe's stdout is
    ///     redirected, so its console is 80 columns wide under <c>--plain</c>: the default run must cut the text
    ///     with a marker naming the total and <c>--full</c>, and the <c>--full</c> run must carry the text
    ///     byte for byte in a BEGIN/END block. <c>--no-truncate</c> must print exactly what <c>--full</c> prints
    ///     from the panel on (the lines before it carry timings).
    /// </summary>
    [Fact]
    public async Task Show_FullAndNoTruncate_ThroughShippedExe_PrintTheWholeTextVerbatim()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var text = BuildSmokeText();
        var plugin = CliExeRunner.WriteFalloutNvEsm(directory, BuildSmokePlugin(text));
        var cancellationToken = TestContext.Current.CancellationToken;

        var byDefault = await CliExeRunner.RunAsync(
            ["--plain", "show", plugin, SmokeMessageEditorId], cancellationToken, workingDirectory: directory.Path);
        var full = await CliExeRunner.RunAsync(
            ["--plain", "show", plugin, SmokeMessageEditorId, "--full"], cancellationToken,
            workingDirectory: directory.Path);
        var noTruncate = await CliExeRunner.RunAsync(
            ["--plain", "show", plugin, SmokeMessageEditorId, "--no-truncate"], cancellationToken,
            workingDirectory: directory.Path);

        Assert.True(byDefault.ExitCode == 0, byDefault.Describe());
        Assert.True(full.ExitCode == 0, full.Describe());
        Assert.True(noTruncate.ExitCode == 0, noTruncate.Describe());

        Assert.DoesNotContain(TextSentinel, byDefault.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("... (truncated: 2,000 of 3,000 characters shown; rerun with --full)",
            byDefault.StandardOutput, StringComparison.Ordinal);

        Assert.Contains(text, full.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("----- BEGIN Message text (3,000 chars, ", full.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("----- END Message text -----", full.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("(truncated", full.StandardOutput, StringComparison.Ordinal);

        Assert.Equal(FromPanel(full.StandardOutput), FromPanel(noTruncate.StandardOutput));
    }

    private static Option<bool> FullOption(Command show)
    {
        return Assert.IsType<Option<bool>>(Assert.Single(show.Options, option => option.Name == "--full"));
    }

    /// <summary>The output from the panel's FormID line on.</summary>
    private static string FromPanel(string stdout)
    {
        var start = stdout.IndexOf("FormID:", StringComparison.Ordinal);
        Assert.True(start >= 0, "The MESG panel was not printed.");
        return stdout[start..];
    }

    /// <summary>
    ///     Exactly <see cref="SmokeTextLength" /> ASCII characters (the MESG text decodes as Windows-1252, and
    ///     ASCII keeps the check about truncation and layout, not code pages), with <see cref="TextSentinel" />
    ///     past the cap.
    /// </summary>
    private static string BuildSmokeText()
    {
        var text = new StringBuilder();
        for (var i = 0; text.Length < 2450; i++)
        {
            text.Append($"Line {i}:\tThe caravan waits at the gate until nightfall.\r\n");
        }

        text.Append(TextSentinel).Append("\r\n");
        const string tail = "\r\nEnd of message.";
        var fill = SmokeTextLength - text.Length - tail.Length;
        Assert.True(fill >= 1, "The fixture body overran its target length.");
        text.Append('=', fill).Append(tail);
        Assert.Equal(SmokeTextLength, text.Length);
        return text.ToString();
    }

    /// <summary>A little-endian FalloutNV plugin with one MESG: EDID, FULL and the long DESC.</summary>
    private static EsmTestFileBuilder BuildSmokePlugin(string text)
    {
        return new EsmTestFileBuilder()
            .AddTopLevelGrup("MESG",
                EsmTestFileBuilder.BuildRecord("MESG", SmokeMessageFormId, 0,
                    ("EDID", NullTerminated(SmokeMessageEditorId)),
                    ("DESC", NullTerminated(text)),
                    ("FULL", NullTerminated("Smoke Title"))));
    }

    private static byte[] NullTerminated(string value)
    {
        return Encoding.ASCII.GetBytes(value + "\0");
    }
}
