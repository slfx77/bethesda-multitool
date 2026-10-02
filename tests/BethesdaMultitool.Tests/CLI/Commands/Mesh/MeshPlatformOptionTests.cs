using System.CommandLine;
using BethesdaMultitool.CLI.Commands.Mesh;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Mesh;

/// <summary>
///     The <c>--platform</c> option of the <c>mesh</c> commands (slice 10): every subcommand that reads a model accepts
///     <c>x360</c> and <c>ps3</c>, refuses any other value as a usage error (exit 2), and parses without it. Only the
///     parse is exercised here; the reader's use of the value is tested on synthetic packed files.
/// </summary>
public sealed class MeshPlatformOptionTests
{
    public static TheoryData<string[]> Commands => new()
    {
        new[] { "mesh", "info", "a.nif" },
        new[] { "mesh", "convert", "a.nif", "out" },
        new[] { "mesh", "validate", "a.nif" },
        new[] { "mesh", "fidelity", "a.nif", "--format", "glb" },
        new[] { "mesh", "dump", "a.nif" },
        new[] { "mesh", "package", "a.nif", "out.zip" }
    };

    /// <summary>Control: a value outside x360 and ps3 is a parse error mapped to the usage exit code.</summary>
    [Theory]
    [MemberData(nameof(Commands))]
    public void Platform_AcceptsTheTwoConsoles_AndRefusesOthers(string[] command)
    {
        var root = new RootCommand();
        root.Subcommands.Add(MeshCommand.Create());

        Assert.Empty(root.Parse(command).Errors);
        Assert.Empty(root.Parse([.. command, "--platform", "x360"]).Errors);
        Assert.Empty(root.Parse([.. command, "--platform", "ps3"]).Errors);

        var wrong = root.Parse([.. command, "--platform", "wii"]);
        Assert.NotEmpty(wrong.Errors);
        Assert.Equal(MeshCommand.UsageExitCode, MeshCommand.MapUsageExitCode(wrong, 1));
    }

    [Fact]
    public void PlatformOption_IsNamedAndBounded()
    {
        var option = MeshCommand.CreatePlatformOption();

        Assert.EndsWith("platform", option.Name, StringComparison.Ordinal);
        Assert.Contains("x360", option.Description, StringComparison.Ordinal);
        Assert.Contains("ps3", option.Description, StringComparison.Ordinal);
    }
}
