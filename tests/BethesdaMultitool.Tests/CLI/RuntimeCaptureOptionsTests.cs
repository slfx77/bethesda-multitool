using System.CommandLine;
using BethesdaMultitool.CLI.Commands.Runtime;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

public sealed class RuntimeCaptureOptionsTests
{
    private static readonly string[] GuestManifestOptions = ["--guest-manifest", "guest.json"];
    private static readonly string[] ScenarioOptions = ["--scenario", "reads.json"];
    private static readonly string[] GuestScenarioOptions = ["--guest-manifest", "guest.json", "--scenario", "reads.json"];

    public static TheoryData<string[]> SupportedCaptureOptions => new()
    {
        Array.Empty<string>(),
        GuestManifestOptions,
        ScenarioOptions,
        GuestScenarioOptions
    };

    [Theory]
    [MemberData(nameof(SupportedCaptureOptions))]
    public void Capture_accepts_observation_options_without_a_profile(string[] options)
    {
        var parsed = CreateRoot().Parse(["runtime", "capture", "--pid", "123", "--output", "trace.ndjson",
            "--seconds", "20", .. options]);

        Assert.Empty(parsed.Errors);
    }

    [Theory]
    [InlineData("--profile", "--scenario")]
    [InlineData("--scenario", "--profile")]
    public void Run_requires_both_profile_and_scenario(string provided, string missing)
    {
        var parsed = CreateRoot().Parse(["runtime", "run", "--pid", "123", "--output", "trace.ndjson",
            provided, "input.json"]);

        Assert.Contains(parsed.Errors, error => error.Message.Contains(missing, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_accepts_a_profile_and_scenario_but_not_a_guest_manifest(bool withGuestManifest)
    {
        string[] arguments = ["runtime", "run", "--pid", "123", "--output", "trace.ndjson",
            "--profile", "profile.json", "--scenario", "actions.json"];
        if (withGuestManifest) arguments = [.. arguments, "--guest-manifest", "guest.json"];

        var parsed = CreateRoot().Parse(arguments);

        if (withGuestManifest)
            Assert.Contains(parsed.Errors, error => error.Message.Contains("--guest-manifest", StringComparison.Ordinal));
        else
            Assert.Empty(parsed.Errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("--guest-manifest")]
    [InlineData("--scenario")]
    public async Task Capture_rejects_profile_before_connection_or_input_reads(string? otherOption)
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var output = Path.Combine(directory.Path, "trace.ndjson");
        // PID zero cannot connect; the absent scenario would fail before connection if profile admission were late.
        var arguments = new List<string> { "runtime", "capture", "--pid", "0", "--output", output,
            "--profile", Path.Combine(directory.Path, "absent-profile.json") };
        if (otherOption is not null)
            arguments.AddRange([otherOption, Path.Combine(directory.Path, "absent-input.json")]);

        var result = await CliExeRunner.RunAsync(arguments, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("runtime capture does not support --profile", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--guest-manifest", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("runtime run --profile", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Capture_still_rejects_a_mutating_scenario_before_connection()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var token = TestContext.Current.CancellationToken;
        var scenario = Path.Combine(directory.Path, "actions.json");
        var output = Path.Combine(directory.Path, "trace.ndjson");
        const string contents = """
            {"actions":[{"kind":"set-global","target":"GameDaysPassed","value":1}],"observeMilliseconds":100}
            """;
        await File.WriteAllTextAsync(scenario, contents, token);

        var result = await CliExeRunner.RunAsync(["runtime", "capture", "--pid", "0", "--output", output,
            "--scenario", scenario], token);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Mutating actions require runtime run", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(result.StandardOutput);
        Assert.False(File.Exists(output));
        Assert.Equal(contents, await File.ReadAllTextAsync(scenario, token));
    }

    private static RootCommand CreateRoot()
    {
        var root = new RootCommand();
        root.Subcommands.Add(RuntimeCommand.Create());
        return root;
    }
}
