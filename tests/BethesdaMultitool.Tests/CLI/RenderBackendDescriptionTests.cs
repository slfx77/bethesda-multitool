using System.CommandLine;
using BethesdaMultitool.CLI.Commands.Render;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>
///     The <c>--gpu</c> flag must advertise the backend the renderer actually constructs.
///     <para>
///         Replaces <c>RenderBackendDescriptionSourceContractTests</c>, which grepped the command
///         sources for the literal <c>"Force GPU rendering (D3D12)"</c>. Nothing in that check
///         reached the option a user actually sees: renaming the variable, moving the string to a
///         constant, or building the option a different way would all have kept the test green
///         while changing the CLI. These commands are plain `net10.0` code, so the real
///         <see cref="Command" /> can just be built and inspected.
///     </para>
/// </summary>
public class RenderBackendDescriptionTests
{
    /// <summary>The backend identifier the selector prints when it succeeds.</summary>
    private const string ExpectedBackend = "Direct3D12";

    public static TheoryData<string, Command> RenderCommands => new()
    {
        { "render", RenderCommand.Create() },
        { "render npc", RenderNpcCommand.Create() }
    };

    [Theory]
    [MemberData(nameof(RenderCommands))]
    public void GpuOption_DescribesDirect3D12(string commandName, Command command)
    {
        _ = commandName; // Names the case in the test display name.

        var gpu = FindOption(command, "--gpu");

        Assert.Equal("Force GPU rendering (D3D12)", gpu.Description);
    }

    /// <summary>
    ///     The description must not name a backend this build cannot produce. The selector only ever
    ///     constructs a D3D12 device, so advertising Vulkan or D3D11 would be a false promise.
    /// </summary>
    [Theory]
    [MemberData(nameof(RenderCommands))]
    public void GpuOption_DoesNotAdvertiseABackendTheSelectorNeverBuilds(string commandName, Command command)
    {
        _ = commandName;

        var description = FindOption(command, "--gpu").Description ?? string.Empty;

        Assert.DoesNotContain("Vulkan", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("D3D11", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(RenderCommands))]
    public void CpuOption_IsOfferedAlongsideGpu(string commandName, Command command)
    {
        _ = commandName;

        // The pair is what makes the flags meaningful — "force GPU" only reads as a choice when
        // the opposite force exists.
        Assert.NotNull(FindOption(command, "--cpu").Description);
    }

    /// <summary>
    ///     Ties the advertised text to the value the selector actually reports at runtime, which is
    ///     the coupling the old source pin was reaching for.
    /// </summary>
    [Fact]
    public void GpuOptionDescription_MatchesTheBackendIdentifierTheSelectorReports()
    {
        Assert.Equal(ExpectedBackend, GpuDevice12.Backend);

        var description = FindOption(RenderCommand.Create(), "--gpu").Description!;

        // "D3D12" in the description and "Direct3D12" from the device are the same backend; assert
        // the description names the generation the device reports rather than a stale one.
        Assert.Contains("D3D12", description, StringComparison.Ordinal);
        Assert.EndsWith("12", ExpectedBackend, StringComparison.Ordinal);
    }

    private static Option FindOption(Command command, string name)
    {
        var option = command.Options.FirstOrDefault(o => o.Name == name || o.Aliases.Contains(name));

        Assert.True(option is not null,
            $"`{command.Name}` exposes no `{name}` option; found: "
            + string.Join(", ", command.Options.Select(o => o.Name)));
        return option!;
    }
}
