using Xunit;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>
///     A JSON request must switch the CLI to plain mode before anything is written, so neither the FIGlet
///     banner nor the resource-stats table can land on stdout ahead of or after the document.
/// </summary>
public sealed class CliJsonOutputModeTests
{
    [Theory]
    [InlineData("esm", "semdiff", "a.esm", "b.esm", "--format", "json")]
    [InlineData("esm", "semdiff", "a.esm", "b.esm", "--format", "JSON")]
    [InlineData("esm", "semdiff", "a.esm", "b.esm", "--format=json")]
    [InlineData("esm", "packages", "a.esm", "-f", "json")]
    [InlineData("esm", "a.esm", "-f=json")]
    [InlineData("esm", "semdiff", "a.esm", "b.esm", "--format:json")]
    [InlineData("esm", "packages", "a.esm", "-f:JSON")]
    [InlineData("lip", "inspect", "a.lip", "--json")]
    [InlineData("lip", "inspect", "a.lip", "--samples")]
    [InlineData("esm", "actor-details", "a.esm", "0x00000100")]
    [InlineData("esm", "terminal-graph", "a.esm", "0x00000100")]
    [InlineData("esm", "terminal-graph", "a.esm", "0x00000100", "--format", "dot")]
    [InlineData("refs", "a.esm", "0x00000100", "--format:json")]
    public void Json_format_requests_are_recognised(params string[] args)
    {
        Assert.True(Program.RequestsJsonOutput(args));
    }

    [Theory]
    [InlineData("esm", "semdiff", "a.esm", "b.esm", "--format", "table")]
    [InlineData("esm", "semdiff", "a.esm", "b.esm", "-f", "0x000E739E")]
    [InlineData("show", "a.esm", "json")]
    [InlineData("esm", "packages", "a.esm", "-F", "json")]
    [InlineData("esm", "packages", "a.esm", "-f")]
    [InlineData("esm", "semdiff", "a.esm", "b.esm", "-f", "json")]
    [InlineData("dmp", "probe-shifts", "a.dmp", "--json", "report.json")]
    [InlineData("lip", "inspect", "a.lip", "--json:false")]
    public void Other_arguments_do_not_request_json(params string[] args)
    {
        Assert.False(Program.RequestsJsonOutput(args));
    }

    [Fact]
    public void JsonRequestAloneDisablesInteractiveBanner()
    {
        Assert.False(Program.IsPlainMode([], outputRedirected: false, noColor: false, jsonOutput: false));
        Assert.True(Program.IsPlainMode([], outputRedirected: false, noColor: false, jsonOutput: true));
    }
}
