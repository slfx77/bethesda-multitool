using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.Tests.Helpers;
using Spectre.Console;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Shared;

[Collection(ProcessEnvironmentGroup.Name)]
public sealed class CliHelpersCaptureTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void Capture_preserves_plain_text_with_or_without_GitHub_Actions(string? githubActions)
    {
        var previous = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", githubActions);
            const string payload = "\t[red]literal[/]\r\nline 2\n";
            var output = CliHelpers.CaptureSpectreOutput(console =>
            {
                Assert.False(console.Profile.Capabilities.Ansi);
                console.MarkupLine("[bold]Show:[/] {0}", Markup.Escape("a[1].esm"));
                console.Profile.Out.Writer.Write(payload);
            });

            Assert.Equal($"Show: a[1].esm{Environment.NewLine}{payload}", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", previous);
        }
    }
}
