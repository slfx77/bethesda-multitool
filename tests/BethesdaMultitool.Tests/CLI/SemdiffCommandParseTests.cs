using System.CommandLine;
using BethesdaMultitool.CLI.Commands.Esm;
using BethesdaMultitool.CLI.Formatters;
using Spectre.Console;
using Xunit;
using static BethesdaMultitool.Tests.CLI.SemdiffTestRecords;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>
///     Tests for the <c>esm semdiff</c> options: <c>--match</c>, <c>--map</c>, <c>-f</c> and <c>--format</c>. Invalid
///     option values fail before either file is read, so the missing file paths used here are never
///     opened; the error names the option, which is what separates it from a file-not-found exit.
/// </summary>
public sealed class SemdiffCommandParseTests
{
    [Theory]
    [InlineData("JSON", "EDITORID")]
    [InlineData("Table", "FormId")]
    public void SemdiffCommand_FormatAndMatch_AcceptCaseInsensitiveValues(string format, string match)
    {
        var root = new RootCommand("test");
        root.Subcommands.Add(EsmSemdiffCommand.CreateSemanticDiffCommand());
        Assert.Empty(root.Parse(["semdiff", "a.esm", "b.esm", "--format", format, "--match", match]).Errors);
    }

    [Theory]
    [InlineData("0x0101=zz")]
    [InlineData("0x1")]
    [InlineData("=0x2")]
    [InlineData("0x1=0x2=0x3")]
    public void SemdiffCommand_MapOption_RejectsMalformedPair(string value)
    {
        var (exitCode, stderr) = Invoke("missing-a.esm", "missing-b.esm", "--map", value);

        Assert.Equal(1, exitCode);
        Assert.Contains("--map", stderr);
        Assert.Contains(value, stderr);
    }

    [Fact]
    public void SemdiffCommand_MatchOption_RejectsUnknownValue()
    {
        var root = new RootCommand("test");
        root.Subcommands.Add(EsmSemdiffCommand.CreateSemanticDiffCommand());

        Assert.NotEmpty(root.Parse(["semdiff", "a.esm", "b.esm", "--match", "bogus"]).Errors);
        Assert.Empty(root.Parse(["semdiff", "a.esm", "b.esm", "--match", "editorid"]).Errors);
        Assert.Empty(root.Parse(["semdiff", "a.esm", "b.esm", "--match", "formid"]).Errors);
    }

    [Theory]
    [InlineData("-f", "0x010134AA")]
    [InlineData("--match", "editorid")]
    [InlineData("-t", "QUST")]
    [InlineData("--type", "QUST")]
    public void SemdiffCommand_MapOption_RefusesConflictingOptions(string option, string value)
    {
        // A type filter would drop B's REFR at a reused FormID and report it as missing instead of
        // refusing the signature mismatch, so -t is refused like -f.
        var (exitCode, stderr) = Invoke("missing-a.esm", "missing-b.esm",
            "--map", "0x010134AA=0x01011316", option, value);

        Assert.Equal(1, exitCode);
        Assert.Contains("cannot be combined", stderr);
    }

    /// <summary>
    ///     <c>--format</c> advertised <c>table|tree|json</c> and the handler never read it, so <c>tree</c> (never
    ///     implemented) silently printed the table. It is now refused at parse time; <c>table</c> and
    ///     <c>json</c> are the accepted values.
    /// </summary>
    [Fact]
    public void SemdiffCommand_FormatOption_RejectsUnimplementedTree()
    {
        var root = new RootCommand("test");
        root.Subcommands.Add(EsmSemdiffCommand.CreateSemanticDiffCommand());

        var tree = root.Parse(["semdiff", "a.esm", "b.esm", "--format", "tree"]);
        Assert.NotEmpty(tree.Errors);
        Assert.Contains(tree.Errors, error => error.Message.Contains("tree", StringComparison.Ordinal));
        Assert.Empty(root.Parse(["semdiff", "a.esm", "b.esm", "--format", "json"]).Errors);
        Assert.Empty(root.Parse(["semdiff", "a.esm", "b.esm", "--format", "table"]).Errors);
        Assert.Empty(root.Parse(["semdiff", "a.esm", "b.esm"]).Errors);

        var format = EsmSemdiffCommand.CreateSemanticDiffCommand().Options
            .Single(o => o.Name == "--format" || o.Aliases.Contains("--format"));
        Assert.DoesNotContain("tree", format.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSemanticDiffCore_UnknownFormat_FailsBeforeReadingEitherFile()
    {
        // The core is also called directly (tests, the unified diff command), so it validates the value the
        // parser would have refused. The files do not exist: the format error must come first.
        var request = new EsmSemdiffCommand.SemdiffRequest("missing-a.esm", "missing-b.esm") { Format = "tree" };
        using var stderr = new StringWriter();
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        using var stdout = new MemoryStream();

        var exitCode = EsmSemdiffCommand.RunSemanticDiffCore(request, console, stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("--format 'tree'", stderr.ToString());
        Assert.DoesNotContain("File not found", stderr.ToString() + output);
        Assert.Equal(0, stdout.Length);
    }

    [Fact]
    public void SemdiffCommand_MapOption_HelpNamesTheTypeFilterExclusion()
    {
        var map = EsmSemdiffCommand.CreateSemanticDiffCommand().Options
            .Single(o => o.Name == "--map" || o.Aliases.Contains("--map"));

        Assert.Contains("Not combinable with -f, -t or --match editorid", map.Description);
    }

    [Theory]
    [InlineData("0xZZ")]
    [InlineData("not-a-formid")]
    public void SemdiffCommand_FormIdOption_RejectsMalformedValueCleanly(string value)
    {
        var (exitCode, stderr) = Invoke("missing-a.esm", "missing-b.esm", "-f", value);

        Assert.Equal(1, exitCode);
        Assert.Contains("invalid FormID", stderr);
    }

    [Fact]
    public void TryParseMappings_ParsesRepeatedHexPairs()
    {
        Assert.True(EsmSemdiffCommand.TryParseMappings(["0x010134AA=0x01011316", "1E59=0x01011E59"],
            out var mappings, out var error));

        Assert.Null(error);
        Assert.Equal(
            [
                new SemdiffTypes.FormIdMapping(0x010134AA, 0x01011316),
                new SemdiffTypes.FormIdMapping(0x00001E59, 0x01011E59)
            ],
            mappings);
    }

    [Fact]
    public void RunSemanticDiffCore_UnprefixedFormId_IsHexAndHeaderOnlyChangeIsReported()
    {
        // The same placed actor in two builds: only header flag bit 11 (and the version-control
        // words) differ. "000E739E" is hex, as `show` reads it; the old parser read unprefixed
        // input as decimal and rejected it.
        var directory = Path.Combine(Path.GetTempPath(), "semdiff-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fileA = WritePlugin(directory, "a", 0x00000400, 0x00195609, 3);
            var fileB = WritePlugin(directory, "b", 0x00000C00, 0x0006060B, 4);
            var request = new EsmSemdiffCommand.SemdiffRequest(fileA, fileB) { FormIdText = "000E739E" };
            using var stderr = new StringWriter();
            var output = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(output),
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No
            });
            console.Profile.Width = 240;

            var exitCode = EsmSemdiffCommand.RunSemanticDiffCore(request, console, Stream.Null, stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal("", stderr.ToString());
            var text = output.ToString();
            Assert.Contains("Found 1 record(s) with differences", text);
            Assert.Contains("+ bit 11 (0x00000800) Initially Disabled", text);
            Assert.DoesNotContain("No differences found", text);
            Assert.DoesNotContain("Records are identical", text);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static string WritePlugin(string directory, string build, uint flags, uint versionControl1,
        ushort versionControl2)
    {
        // Named FalloutNV.esm so game detection names New Vegas header bits.
        var buildDirectory = Path.Combine(directory, build);
        Directory.CreateDirectory(buildDirectory);
        var path = Path.Combine(buildDirectory, "FalloutNV.esm");
        File.WriteAllBytes(path, RecordBytes(false, "ACHR", 0x000E739E, flags, versionControl1, 15,
            versionControl2, false, ("NAME", U32(0x00123456)), ("DATA", new byte[24])));
        return path;
    }

    private static (int ExitCode, string Stderr) Invoke(params string[] arguments)
    {
        var root = new RootCommand("test");
        root.Subcommands.Add(EsmSemdiffCommand.CreateSemanticDiffCommand());
        var parseResult = root.Parse(["semdiff", .. arguments]);
        Assert.Empty(parseResult.Errors);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = parseResult.Invoke(new InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
            EnableDefaultExceptionHandler = false
        });
        return (exitCode, stderr.ToString());
    }
}
