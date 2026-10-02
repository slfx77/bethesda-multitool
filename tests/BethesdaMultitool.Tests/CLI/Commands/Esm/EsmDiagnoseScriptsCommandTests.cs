using System.Buffers.Binary;
using System.CommandLine;
using System.Text;
using BethesdaMultitool.CLI.Commands.Esm;
using BethesdaMultitool.Tests.Helpers;
using Spectre.Console;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Esm;

/// <summary>
///     <c>esm diagnose-scripts</c> target scoping. <c>--record</c> without <c>--actor</c> used to fall
///     back to the hard-coded Ulysses / Chomps Lewis targets and copy the explicit record under both,
///     which crashed the audit on the duplicate INFO. <c>--record</c> alone is now explicit-only; the
///     legacy targets remain only when neither option is given, and say so.
/// </summary>
public sealed class EsmDiagnoseScriptsCommandTests
{
    private const uint UlyssesId = 0x00100001;
    private const uint ChompsId = 0x00100002;
    private const uint HorowitzId = 0x00100003;
    private const uint InfoId = 0x00200001;

    [Theory]
    [InlineData(new string[0], new uint[] { InfoId }, new string[0], false)]
    [InlineData(new string[0], new uint[0], new[] { "Ulysses", "Chomps Lewis" }, true)]
    [InlineData(new[] { "A" }, new uint[] { InfoId }, new[] { "A" }, false)]
    [InlineData(new[] { " Horowitz " }, new uint[0], new[] { "Horowitz" }, false)]
    [InlineData(new[] { "  " }, new uint[] { InfoId }, new string[0], false)]
    [InlineData(new[] { "  " }, new uint[0], new string[0], false)]
    public void ResolveTargets_ExplicitRecordsOnly_HasNoImplicitActors(
        string[] actors,
        uint[] explicitRecords,
        string[] expectedTargets,
        bool expectedLegacyDefaults)
    {
        var targets = EsmDiagnoseScriptsCommand.ResolveTargets(
            actors,
            explicitRecords.ToHashSet(),
            out var usedLegacyDefaults);

        Assert.Equal(expectedTargets, targets);
        Assert.Equal(expectedLegacyDefaults, usedLegacyDefaults);
    }

    [Fact]
    public async Task DiagnoseScripts_EndToEnd_ExplicitInfoOnly_ExitsZeroWithOneExplicitRow()
    {
        var directory = CreateTempDirectory();
        try
        {
            var input = WritePlugin(directory);
            var output = Path.Combine(directory, "out");
            var root = new RootCommand("test");
            root.Subcommands.Add(EsmDiagnoseScriptsCommand.CreateDiagnoseScriptsCommand());
            var parseResult = root.Parse(
                ["diagnose-scripts", input, "--record", $"0x{InfoId:X8}", "--output", output]);
            Assert.Empty(parseResult.Errors);

            var exitCode = await parseResult.InvokeAsync(
                new InvocationConfiguration
                {
                    Output = TextWriter.Null,
                    Error = TextWriter.Null,
                    EnableDefaultExceptionHandler = false
                },
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            var records = ReadLines(Path.Combine(output, "target_records.csv"));
            Assert.Equal(2, records.Length);
            Assert.StartsWith("explicit,explicit-record,INFO,0x00200001,", records[1], StringComparison.Ordinal);
            var matches = ReadLines(Path.Combine(output, "target_matches.csv"));
            Assert.Single(matches);
            Assert.StartsWith("target,", matches[0], StringComparison.Ordinal);
            var dialogue = ReadLines(Path.Combine(output, "target_dialogue_audit.csv"));
            Assert.Equal(2, dialogue.Length);
            var summary = await File.ReadAllTextAsync(Path.Combine(output, "summary.md"),
                TestContext.Current.CancellationToken);
            Assert.Contains("- Targets: (none; explicit records only)", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("Ulysses", summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DiagnoseScripts_ShippedExe_ExplicitInfoOnly_ExitsZeroWithOneExplicitRow()
    {
        // The reported command, run through the trimmed exe the way it ships.
        using var directory = CliExeRunner.CreateTempDirectory();
        var plugin = CliExeRunner.WriteFalloutNvEsm(directory, BuildPlugin());
        var output = Path.Combine(directory.Path, "out");

        var result = await CliExeRunner.RunAsync(
            ["--plain", "esm", "diagnose-scripts", plugin, "--record", $"0x{InfoId:X8}", "--output", output],
            TestContext.Current.CancellationToken,
            workingDirectory: directory.Path);

        Assert.True(result.ExitCode == 0, result.Describe());
        // A redirected console wraps at 80 columns, so match single words rather than the phrase.
        Assert.Contains("0x00200001", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Ulysses", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", result.StandardError, StringComparison.Ordinal);
        var records = ReadLines(Path.Combine(output, "target_records.csv"));
        Assert.Equal(2, records.Length);
        Assert.StartsWith("explicit,explicit-record,INFO,0x00200001,", records[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ExplicitOnly_HeaderNamesTheRecordsAndNoLegacyActors()
    {
        var directory = CreateTempDirectory();
        try
        {
            var input = WritePlugin(directory);
            var (console, text) = RecordingConsole();

            var exitCode = await EsmDiagnoseScriptsCommand.RunAsync(
                input,
                [],
                [$"{InfoId:X8}", "0x00DEAD01"],
                null,
                null,
                Path.Combine(directory, "out"),
                console,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            var output = text.ToString();
            Assert.Contains("for explicit record(s) 0x00200001, 0x00DEAD01", output, StringComparison.Ordinal);
            Assert.Contains("Explicit record not found: 0x00DEAD01", output, StringComparison.Ordinal);
            Assert.Contains("1 explicit record(s)", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Ulysses", output, StringComparison.Ordinal);
            Assert.DoesNotContain("legacy default", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RunAsync_NoActorOrRecord_KeepsLegacyTargetsAndSaysSo()
    {
        var directory = CreateTempDirectory();
        try
        {
            var input = WritePlugin(directory);
            var output = Path.Combine(directory, "out");
            var (console, text) = RecordingConsole();

            var exitCode = await EsmDiagnoseScriptsCommand.RunAsync(
                input, [], [], null, null, output, console, TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains("using the legacy default targets Ulysses, Chomps Lewis", text.ToString(),
                StringComparison.Ordinal);
            var matches = ReadLines(Path.Combine(output, "target_matches.csv"));
            Assert.Contains(matches, line => line.StartsWith("Ulysses,NPC_,0x00100001,", StringComparison.Ordinal));
            Assert.Contains(matches, line => line.StartsWith("Chomps Lewis,NPC_,0x00100002,", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RunAsync_RecordTokensWithNoValidFormId_ExitsOneWithoutWritingReports()
    {
        var directory = CreateTempDirectory();
        try
        {
            var input = WritePlugin(directory);
            var output = Path.Combine(directory, "out");
            var (console, text) = RecordingConsole();

            var exitCode = await EsmDiagnoseScriptsCommand.RunAsync(
                input, [], ["not-a-formid"], null, null, output, console, TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.False(Directory.Exists(output));
            var printed = text.ToString();
            Assert.Contains("Skipping invalid FormID: not-a-formid", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("Ulysses", printed, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RunAsync_BlankActor_RejectsWithoutDiagnosingLegacyTargets()
    {
        var directory = CreateTempDirectory();
        try
        {
            var output = Path.Combine(directory, "out");
            var (console, text) = RecordingConsole();
            var exitCode = await EsmDiagnoseScriptsCommand.RunAsync(
                WritePlugin(directory), ["  "], [], null, null, output, console, TestContext.Current.CancellationToken);
            Assert.Equal(1, exitCode);
            Assert.False(Directory.Exists(output));
            Assert.Contains("every value is blank", text.ToString());
            Assert.DoesNotContain("Ulysses", text.ToString());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RunAsync_MissingInputWithMarkupLikePath_ReportsItLiterally()
    {
        var directory = CreateTempDirectory();
        try
        {
            // "[1]" is a Spectre colour tag; unescaped, the error line itself would throw.
            var input = Path.Combine(directory, "[1] missing.esm");
            var (console, text) = RecordingConsole();

            var exitCode = await EsmDiagnoseScriptsCommand.RunAsync(
                input, [], [], null, null, Path.Combine(directory, "out"), console,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Contains("File not found: " + input, text.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"diagnose_scripts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    ///     A New Vegas plugin (HEDR 1.34, named FalloutNV.esm) holding the two legacy default actors, a
    ///     third NPC_ and an INFO that only the third one speaks.
    /// </summary>
    private static string WritePlugin(string directory)
    {
        var path = Path.Combine(directory, "FalloutNV.esm");
        File.WriteAllBytes(path, BuildPlugin().Build());
        return path;
    }

    private static EsmTestFileBuilder BuildPlugin()
    {
        return new EsmTestFileBuilder()
            .AddTopLevelGrup("NPC_",
                EsmTestFileBuilder.BuildRecord("NPC_", UlyssesId, 0,
                    ("EDID", Z("Ulysses")), ("FULL", Z("Ulysses"))),
                EsmTestFileBuilder.BuildRecord("NPC_", ChompsId, 0,
                    ("EDID", Z("ChompsLewis")), ("FULL", Z("Chomps Lewis"))),
                EsmTestFileBuilder.BuildRecord("NPC_", HorowitzId, 0,
                    ("EDID", Z("VVault34Horowitz")), ("FULL", Z("Horowitz"))))
            .AddTopLevelGrup("INFO",
                EsmTestFileBuilder.BuildRecord("INFO", InfoId, 0,
                    ("CTDA", GetIsIdCondition(HorowitzId)),
                    ("TRDT", new byte[24]),
                    ("NAM1", Z("Of course, please, take this!")),
                    ("ANAM", U32(HorowitzId))));
    }

    private static (IAnsiConsole Console, StringWriter Text) RecordingConsole()
    {
        var text = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(text),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        console.Profile.Width = 400;
        return (console, text);
    }

    private static string[] ReadLines(string path)
    {
        return File.ReadAllLines(path)
            .Select(line => line.TrimStart('\uFEFF'))
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private static byte[] Z(string value)
    {
        var data = new byte[value.Length + 1];
        Encoding.Latin1.GetBytes(value, data);
        return data;
    }

    private static byte[] U32(uint value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        return data;
    }

    private static byte[] GetIsIdCondition(uint actorFormId)
    {
        var data = new byte[28];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1.0f);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 0x48);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), actorFormId);
        return data;
    }
}
