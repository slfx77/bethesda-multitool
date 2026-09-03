using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class CliLaunchModeTests
{
    [Theory]
    [InlineData("archive")]
    [InlineData("ARCHIVE")]
    [InlineData("render")]
    [InlineData("esm")]
    [InlineData("build-shader-bytecode-pack")]
    public void Registered_root_command_selects_cli_without_legacy_flag(string command)
    {
        Assert.True(Program.ShouldRunCli([command, "--help"]));
    }

    [Theory]
    [InlineData("--plain")]
    [InlineData("--no-ansi")]
    [InlineData("--resource-stats")]
    [InlineData("--verbose")]
    [InlineData("-v")]
    public void Cli_presentation_switch_may_precede_registered_command(string option)
    {
        Assert.True(Program.ShouldRunCli([option, "archive", "list", "game.ba2"]));
    }

    [Fact]
    public void Explicit_no_gui_remains_cli_regardless_of_position()
    {
        Assert.True(Program.ShouldRunCli(["input.dmp", "--no-gui"]));
        Assert.True(Program.ShouldRunCli(["input.dmp", "-n"]));
    }

    [Fact]
    public void Gui_options_and_positional_files_do_not_become_cli_commands()
    {
        Assert.False(Program.ShouldRunCli([]));
        Assert.False(Program.ShouldRunCli(["capture.dmp"]));
        Assert.False(Program.ShouldRunCli(["--file", "capture.esm", "--actor", "archive"]));
    }
}
