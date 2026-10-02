using System.CommandLine;
using BethesdaMultitool.CLI.Commands.Mesh;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Inspection;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Mesh;

/// <summary>
///     The <c>--skeleton</c> option of the <c>mesh</c> commands (cut-1b slice 10, plan section 1.7): every subcommand that
///     reads a model accepts one file, which the workflow serves through the companion resolver as the
///     <see cref="BethesdaModelRegistration.SkeletonOption" /> path, so a loose <c>.kf</c> reads against it; the option
///     names a file and switches no behavior.
/// </summary>
public sealed class MeshSkeletonOptionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bmt-mesh-skeleton-").FullName;

    public static TheoryData<string[]> Commands => new()
    {
        new[] { "mesh", "info", "a.kf" },
        new[] { "mesh", "convert", "a.kf", "out" },
        new[] { "mesh", "validate", "a.kf" },
        new[] { "mesh", "fidelity", "a.kf", "--format", "glb" },
        new[] { "mesh", "dump", "a.kf" },
        new[] { "mesh", "package", "a.kf", "out.zip" }
    };

    /// <summary>Removes the temporary directory.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is harmless; the test result stands.
        }
    }

    /// <summary>Control: <c>--skeleton</c> without a file is a parse error mapped to the usage exit code.</summary>
    [Theory]
    [MemberData(nameof(Commands))]
    public void Skeleton_ParsesOnEveryReadCommand_AndNeedsAFile(string[] command)
    {
        var root = new RootCommand();
        root.Subcommands.Add(MeshCommand.Create());

        Assert.Empty(root.Parse(command).Errors);
        Assert.Empty(root.Parse([.. command, "--skeleton", "rig/skeleton.nif"]).Errors);

        var missing = root.Parse([.. command, "--skeleton"]);
        Assert.NotEmpty(missing.Errors);
        Assert.Equal(MeshCommand.UsageExitCode, MeshCommand.MapUsageExitCode(missing, 1));
    }

    /// <summary>The option is named and says what it does.</summary>
    [Fact]
    public void SkeletonOption_IsNamedAndDescribed()
    {
        var option = MeshCommand.CreateSkeletonOption();

        Assert.EndsWith("skeleton", option.Name, StringComparison.Ordinal);
        Assert.Contains(".kf", option.Description, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A loose <c>.kf</c> whose folder holds no <c>skeleton.nif</c> reads with <c>--skeleton</c> naming a file elsewhere
    ///     (one clip); control: the same inspection without it is Unsupported with D4's reason.
    /// </summary>
    [Fact]
    public async Task Info_ReadsAKfAgainstTheNamedSkeleton_AndIsUnsupportedWithoutIt()
    {
        var token = TestContext.Current.CancellationToken;
        var kf = Path.Combine(_directory, "idle.kf");
        var rig = Directory.CreateDirectory(Path.Combine(_directory, "rig")).FullName;
        var skeleton = Path.Combine(rig, "custom.nif");
        await File.WriteAllBytesAsync(kf,
            NifModelKfFixtures.Kf(false, 34, NifModelKfFixtures.PelvisName, NifModelKfFixtures.SpineName), token);
        await File.WriteAllBytesAsync(skeleton, NifModelKfFixtures.Skeleton(false), token);

        var with = await BethesdaModelWorkflow.InfoAsync(kf, null, "fnv", null, null, AmpleMemory(), token, skeleton);
        var without = await BethesdaModelWorkflow.InfoAsync(kf, null, "fnv", null, null, AmpleMemory(), token);

        Assert.Equal(ModelInfoStatus.Completed, with.Status);
        Assert.Equal(1, with.Document!.NumAnimations);
        Assert.Equal(ModelInfoStatus.Unsupported, without.Status);
        Assert.Contains(NifModelSkeletonResolver.NoSkeletonReason, without.Reason, StringComparison.Ordinal);
    }

    /// <summary>A <c>--skeleton</c> naming no file fails the command with exit 1 and the message, before any read.</summary>
    [Fact]
    public async Task Info_WithAMissingSkeletonFile_ExitsOne()
    {
        var token = TestContext.Current.CancellationToken;
        var kf = Path.Combine(_directory, "idle.kf");
        await File.WriteAllBytesAsync(kf, NifModelKfFixtures.Kf(false, 34, NifModelKfFixtures.PelvisName), token);
        var error = new StringWriter();

        var exit = await MeshCommand.ExecuteInfoAsync(kf, null, "fnv", false, false, new StringWriter(),
            new MemoryStream(), error, token, AmpleMemory(), skeleton: Path.Combine(_directory, "absent.nif"));

        Assert.Equal(1, exit);
        Assert.Contains("--skeleton file does not exist", error.ToString(), StringComparison.Ordinal);
    }

    private static ModelMemoryGate AmpleMemory()
    {
        return new ModelMemoryGate(() => new ModelMemorySample(64L * 1024 * 1024 * 1024));
    }
}
