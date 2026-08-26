using BethesdaMultitool.CLI.Commands.Dmp;
using BethesdaMultitool.CLI.Shared;
using Spectre.Console;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Dmp;

/// <summary>
///     Pins the fix for the `dmp rtti` usage-string crash: the literal "[&lt;va2&gt; ...]" used
///     to be parsed by Spectre as a style tag and threw InvalidOperationException
///     ("Could not find color or style '&lt;va2&gt;'") instead of printing usage.
/// </summary>
public sealed class RttiCommandUsageTests
{
    [Fact]
    public void UsageTextNoInput_RendersThroughSpectreWithLiteralBrackets()
    {
        var output = CliHelpers.CaptureSpectreOutput(
            console => console.MarkupLine(RttiCommand.UsageTextNoInput));

        // Assert on short fragments: the capture console wraps at its default width,
        // so the full line may span multiple output lines.
        Assert.Contains("[<va2> ...]", output);
        Assert.Contains("--census-all", output);
    }

    [Fact]
    public void UsageTextNoAction_RendersThroughSpectreWithLiteralBrackets()
    {
        var output = CliHelpers.CaptureSpectreOutput(
            console => console.MarkupLine(RttiCommand.UsageTextNoAction));

        Assert.Contains("[<va2> ...]", output);
        Assert.Contains("--scan 0xSTART-0xEND", output);
    }
}
