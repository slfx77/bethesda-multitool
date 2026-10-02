using System.Buffers.Binary;
using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.CLI.Commands.Export;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Tests.Helpers;
using Spectre.Console;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>
///     <c>export scripts</c> in process: option parsing, <c>--id</c>/<c>-f</c> selection, and whole runs of
///     <see cref="ExportScriptsCommand.RunAsync" /> over a synthetic FalloutNV.esm (see
///     <see cref="ExportScriptsTestPlugin" />) with stdout, stderr and the log captured separately. Before this
///     command existed the only per-script export was the carve pipeline's <c>&lt;EditorID&gt;.txt</c> report
///     wrapper; nothing wrote an individual script, so every test here fails on that tree. Expected bytes are the
///     SCTX the plugin was built from and hashes computed here from the files on disk.
/// </summary>
public sealed class ExportScriptsCommandTests : IDisposable
{
    private const string BannerFirstLine =
        "; Reconstruction from SCDA — BethesdaMultitool";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bmt_export_scripts_cmd_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void Parses_options_and_rejects_bad_values()
    {
        var export = ExportCommand.Create();
        Assert.Contains(export.Subcommands, command => command.Name == "scripts");

        var full = export.Parse([
            "scripts", "x.esm", "-o", "out", "--ext", "gek", "--decompiled", "all", "--id", "0x00168CFC",
            "--id", "Foo", "-f", "Quest", "--overwrite", "--build-label", "July 2010"
        ]);
        Assert.Empty(full.Errors);
        Assert.Equal(["0x00168CFC", "Foo"], Assert.IsType<string[]>(full.GetValue<string[]>("--id")));
        Assert.Equal("gek", full.GetValue<string>("--ext"));
        Assert.True(full.GetValue<bool>("--overwrite"));

        // Defaults: --decompiled missing, no --ext (the game decides), no ids; any case of a policy is accepted.
        var minimal = export.Parse(["scripts", "x.esm", "--output", "out"]);
        Assert.Empty(minimal.Errors);
        Assert.Equal("missing", minimal.GetValue<string>("--decompiled"));
        Assert.Null(minimal.GetValue<string>("--ext"));
        Assert.Empty(export.Parse(["scripts", "x.esm", "-o", "out", "--decompiled", "NONE"]).Errors);

        AssertParseError(export, ["scripts", "x.esm", "-o", "out", "--decompiled", "bogus"], "bogus");
        AssertParseError(export, ["scripts", "x.esm"], null);
        AssertParseError(export, ["scripts", "-o", "out"], null);
        AssertParseError(export, ["scripts", "x.esm", "-o", "out", "--ext", "a/b"], "a/b");
        AssertParseError(export, ["scripts", "x.esm", "-o", "out", "--ext", ".g k"], ".g k");
        AssertParseError(export, ["scripts", "x.esm", "-o", "out", "--ext", "."], "'.'");
        AssertParseError(export, ["scripts", "x.esm", "-o", "out", "--id", "0xZZ"], "0xZZ");
        AssertParseError(export, ["scripts", "x.esm", "-o", "out", "--id", "0x123456789"], "0x123456789");
    }

    [Fact]
    public void Id_selectors_match_a_formid_or_an_exact_editor_id_and_filter_narrows()
    {
        Assert.True(ExportScriptsCommand.TryParseId(" 0x00168cfc ", out var formIdSelector, out _));
        Assert.Equal(0x00168CFCu, formIdSelector.FormId);
        Assert.Null(formIdSelector.EditorId);
        Assert.Equal("0x00168cfc", formIdSelector.Text);
        Assert.True(ExportScriptsCommand.TryParseId("0X1", out var shortFormId, out _));
        Assert.Equal(1u, shortFormId.FormId);
        Assert.True(ExportScriptsCommand.TryParseId("VERShadows01QuestScript", out var editorIdSelector, out _));
        Assert.Null(editorIdSelector.FormId);
        Assert.Equal("VERShadows01QuestScript", editorIdSelector.EditorId);
        foreach (var bad in new[] { "", "   ", "0x", "0xGG", "0x123456789" })
        {
            Assert.False(ExportScriptsCommand.TryParseId(bad, out _, out var problem));
            Assert.StartsWith("--id", problem, StringComparison.Ordinal);
        }

        var foo = new ScriptRecord { FormId = 0x10, EditorId = "FooScript", Offset = 100 };
        var fooAgain = new ScriptRecord { FormId = 0x10, EditorId = "FooScript", Offset = 900 };
        var fooExtra = new ScriptRecord { FormId = 0x20, EditorId = "FooScriptExtra", Offset = 200 };
        var unnamed = new ScriptRecord { FormId = 0x30, Offset = 300 };
        IReadOnlyList<ScriptRecord> all = [foo, fooAgain, fooExtra, unnamed];

        // An EditorID matches exactly (never FooScriptExtra), ignoring case, and every duplicate of it.
        var selected = ExportScriptsCommand.SelectScripts(all, [Id("fooscript"), Id("0x30")], null);
        Assert.Equal([100L, 900L, 300L], selected.Scripts.Select(script => script.Offset));
        Assert.Empty(selected.UnmatchedIds);

        var unmatched = ExportScriptsCommand.SelectScripts(all, [Id("FooScript"), Id("NoSuch"), Id("0x00000040")], null);
        Assert.Equal(["NoSuch", "0x00000040"], unmatched.UnmatchedIds.Select(selector => selector.Text));

        // -f is a case-insensitive EditorID substring; a script without an EditorID never matches it.
        Assert.Equal(
            [200L],
            ExportScriptsCommand.SelectScripts(all, [], "extra").Scripts.Select(script => script.Offset));

        // Both together narrow: FooScript matched the input, so it is not "unmatched", but -f removes it.
        var narrowed = ExportScriptsCommand.SelectScripts(all, [Id("FooScript")], "Extra");
        Assert.Empty(narrowed.Scripts);
        Assert.Empty(narrowed.UnmatchedIds);

        // Nothing given: everything, in input order.
        Assert.Equal(
            [100L, 900L, 200L, 300L],
            ExportScriptsCommand.SelectScripts(all, [], null).Scripts.Select(script => script.Offset));
    }

    [Fact]
    public async Task Default_export_writes_verbatim_source_and_decompiles_only_scripts_without_source()
    {
        var plugin = WritePlugin("default");
        var outDir = Path.Combine(_root, "default-out");

        var run = await RunAsync(new ExportScriptsCommand.Request { InputPath = plugin, OutputDirectory = outDir });

        Assert.True(run.ExitCode == 0, run.Describe());
        Assert.Equal(
            ["SmokeAuthoredSCRIPT.gek", "SmokeCompiledOnlySCRIPT.decompiled.gek", "scripts.manifest.json"],
            FileNames(outDir));

        // Authored SCTX: the subrecord's bytes exactly (0x92 kept, CRLF kept, no BOM, nothing appended).
        var authoredSctx = ExportScriptsTestPlugin.AuthoredSctx();
        var authored = File.ReadAllBytes(Path.Combine(outDir, "SmokeAuthoredSCRIPT.gek"));
        Assert.Equal(authoredSctx, authored);
        Assert.Single(authored, b => b == 0x92);

        // The script with bytecode and no SCTX gets only a banner-headed reconstruction, all CRLF.
        var decompiled = ReadWindows1252(Path.Combine(outDir, "SmokeCompiledOnlySCRIPT.decompiled.gek"));
        Assert.StartsWith(BannerFirstLine + "\r\n", decompiled, StringComparison.Ordinal);
        Assert.Contains("\r\n; Plugin: FalloutNV.esm\r\n", decompiled, StringComparison.Ordinal);
        Assert.Contains("\r\n; FormID: 0x00005002\r\n", decompiled, StringComparison.Ordinal);
        Assert.Contains("\r\n; Build: (no build label)\r\n", decompiled, StringComparison.Ordinal);
        Assert.Contains("\r\n; Bytecode byte order: little-endian (", decompiled, StringComparison.Ordinal);
        Assert.EndsWith(
            "\r\nScriptName SmokeCompiledOnlySCRIPT\r\n\r\nBegin OnAdd\r\nEnd",
            decompiled,
            StringComparison.Ordinal);
        Assert.DoesNotContain("no proven source text in the dump", decompiled, StringComparison.Ordinal);
        Assert.Equal(Occurrences(decompiled, "\n"), Occurrences(decompiled, "\r\n"));

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "scripts.manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal("bethesda-multitool/script-export", root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var source = root.GetProperty("source");
        Assert.Equal("plugin", source.GetProperty("kind").GetString());
        Assert.Equal("FalloutNV.esm", source.GetProperty("fileName").GetString());
        Assert.Equal("FalloutNewVegas", source.GetProperty("game").GetString());
        Assert.Equal("little", source.GetProperty("containerEndianness").GetString());
        Assert.Equal(Sha256(File.ReadAllBytes(plugin)), source.GetProperty("sha256").GetString());
        Assert.Equal(new FileInfo(plugin).Length, source.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("buildLabel").ValueKind);
        Assert.Equal(".gek", root.GetProperty("options").GetProperty("extension").GetString());
        Assert.Equal("missing", root.GetProperty("options").GetProperty("decompiled").GetString());
        Assert.False(root.GetProperty("options").GetProperty("overwrite").GetBoolean());

        var scripts = root.GetProperty("scripts").EnumerateArray()
            .ToDictionary(script => script.GetProperty("formId").GetString()!);
        Assert.Equal(2, scripts.Count);

        var authoredEntry = scripts["0x00005001"];
        Assert.Equal("SmokeAuthoredSCRIPT", authoredEntry.GetProperty("stem").GetString());
        Assert.Equal(0, authoredEntry.GetProperty("nameAdjustments").GetArrayLength());
        Assert.Equal("little", authoredEntry.GetProperty("bytecode").GetProperty("endianness").GetString());
        Assert.Equal(
            Sha256(ExportScriptsTestPlugin.Scda),
            authoredEntry.GetProperty("bytecode").GetProperty("sha256").GetString());
        var authoredFile = Assert.Single(authoredEntry.GetProperty("files").EnumerateArray());
        Assert.Equal("SmokeAuthoredSCRIPT.gek", authoredFile.GetProperty("path").GetString());
        Assert.Equal("stored-source", authoredFile.GetProperty("content").GetString());
        Assert.Equal("plugin-record", authoredFile.GetProperty("provenance").GetString());
        Assert.Equal("not-applicable", authoredFile.GetProperty("correspondence").GetString());
        Assert.Equal("windows-1252", authoredFile.GetProperty("encoding").GetString());
        Assert.Equal("crlf", authoredFile.GetProperty("lineEndings").GetString());
        Assert.Equal(authoredSctx.Length, authoredFile.GetProperty("byteLength").GetInt32());
        Assert.Equal(Sha256(authoredSctx), authoredFile.GetProperty("sha256").GetString());

        var compiledEntry = scripts["0x00005002"];
        Assert.Equal("none", compiledEntry.GetProperty("sourceText").GetProperty("kind").GetString());
        var compiledFile = Assert.Single(compiledEntry.GetProperty("files").EnumerateArray());
        Assert.Equal("SmokeCompiledOnlySCRIPT.decompiled.gek", compiledFile.GetProperty("path").GetString());
        Assert.Equal("decompiled-from-scda", compiledFile.GetProperty("content").GetString());
        Assert.Equal("decompiled-from-bytecode", compiledFile.GetProperty("provenance").GetString());
        Assert.Equal(
            Sha256(File.ReadAllBytes(Path.Combine(outDir, "SmokeCompiledOnlySCRIPT.decompiled.gek"))),
            compiledFile.GetProperty("sha256").GetString());

        var summary = root.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("scripts").GetInt32());
        Assert.Equal(1, summary.GetProperty("storedSourceFiles").GetInt32());
        Assert.Equal(0, summary.GetProperty("capturedSourceFiles").GetInt32());
        Assert.Equal(1, summary.GetProperty("decompiledFiles").GetInt32());
        Assert.Equal(0, summary.GetProperty("skippedScripts").GetInt32());

        // The summary is stdout's; the load status line is stderr's.
        Assert.Contains("Exported 2 script(s) from FalloutNV.esm", run.Output, StringComparison.Ordinal);
        Assert.Contains("Source stored as plugin SCTX (verbatim .gek): 1", run.Output, StringComparison.Ordinal);
        Assert.Contains(
            "Decompiled reconstructions (.decompiled.gek, --decompiled missing): 1",
            run.Output,
            StringComparison.Ordinal);
        Assert.Contains("Skipped (no file written): 0", run.Output, StringComparison.Ordinal);
        Assert.Contains("Loading FalloutNV.esm", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Loading", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Error", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Id_ext_decompiled_filter_and_build_label_shape_the_export()
    {
        var plugin = WritePlugin("options");
        var outDir = Path.Combine(_root, "options-out");

        var run = await RunAsync(new ExportScriptsCommand.Request
        {
            InputPath = plugin,
            OutputDirectory = outDir,
            Ids = ["smokeauthoredscript"],
            Extension = "txt",
            Decompiled = "all",
            BuildLabel = "Test Build 7"
        });

        Assert.True(run.ExitCode == 0, run.Describe());
        Assert.Equal(
            ["SmokeAuthoredSCRIPT.decompiled.txt", "SmokeAuthoredSCRIPT.txt", "scripts.manifest.json"],
            FileNames(outDir));
        Assert.Equal(ExportScriptsTestPlugin.AuthoredSctx(), File.ReadAllBytes(Path.Combine(outDir, "SmokeAuthoredSCRIPT.txt")));
        var decompiled = ReadWindows1252(Path.Combine(outDir, "SmokeAuthoredSCRIPT.decompiled.txt"));
        Assert.StartsWith(BannerFirstLine + "\r\n", decompiled, StringComparison.Ordinal);
        Assert.Contains("\r\n; FormID: 0x00005001\r\n", decompiled, StringComparison.Ordinal);
        Assert.Contains("\r\n; Build: Test Build 7\r\n", decompiled, StringComparison.Ordinal);
        Assert.EndsWith("\r\nScriptName SmokeAuthoredSCRIPT\r\n\r\nBegin OnAdd\r\nEnd", decompiled, StringComparison.Ordinal);

        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "scripts.manifest.json"))))
        {
            var root = manifest.RootElement;
            Assert.Equal(".txt", root.GetProperty("options").GetProperty("extension").GetString());
            Assert.Equal("all", root.GetProperty("options").GetProperty("decompiled").GetString());
            Assert.Equal("Test Build 7", root.GetProperty("source").GetProperty("buildLabel").GetString());
            Assert.Equal("user", root.GetProperty("source").GetProperty("buildLabelSource").GetString());
            var entry = Assert.Single(root.GetProperty("scripts").EnumerateArray());
            Assert.Equal("0x00005001", entry.GetProperty("formId").GetString());
            Assert.Equal(
                ["SmokeAuthoredSCRIPT.txt", "SmokeAuthoredSCRIPT.decompiled.txt"],
                entry.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("path").GetString()));
        }

        Assert.Contains("Source stored as plugin SCTX (verbatim .txt): 1", run.Output, StringComparison.Ordinal);
        Assert.Contains(
            "Decompiled reconstructions (.decompiled.txt, --decompiled all): 1",
            run.Output,
            StringComparison.Ordinal);
        Assert.Contains("build 'Test Build 7' (user)", run.Output, StringComparison.Ordinal);

        // A FormID id and a filter together; the filter is case-insensitive.
        var byFormId = Path.Combine(_root, "formid-out");
        var formIdRun = await RunAsync(new ExportScriptsCommand.Request
        {
            InputPath = plugin,
            OutputDirectory = byFormId,
            Ids = ["0x5002"],
            Filter = "compiled"
        });
        Assert.True(formIdRun.ExitCode == 0, formIdRun.Describe());
        Assert.Equal(["SmokeCompiledOnlySCRIPT.decompiled.gek", "scripts.manifest.json"], FileNames(byFormId));

        // --decompiled none: the source-less script is listed as skipped and gets no file.
        var none = Path.Combine(_root, "none-out");
        var noneRun = await RunAsync(new ExportScriptsCommand.Request
        {
            InputPath = plugin,
            OutputDirectory = none,
            Decompiled = "none"
        });
        Assert.True(noneRun.ExitCode == 0, noneRun.Describe());
        Assert.Equal(["SmokeAuthoredSCRIPT.gek", "scripts.manifest.json"], FileNames(none));
        Assert.Contains("Skipped (no file written): 1", noneRun.Output, StringComparison.Ordinal);
        Assert.Contains("decompiled-not-requested: 1", noneRun.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unmatched_ids_and_existing_targets_exit_1_without_writing()
    {
        var plugin = WritePlugin("conflict");

        var unmatchedOut = Path.Combine(_root, "unmatched-out");
        var unmatched = await RunAsync(new ExportScriptsCommand.Request
        {
            InputPath = plugin,
            OutputDirectory = unmatchedOut,
            Ids = ["SmokeAuthoredSCRIPT", "0x00009999", "NoSuchScript"]
        });
        Assert.True(unmatched.ExitCode == 1, unmatched.Describe());
        Assert.Contains("0x00009999", unmatched.Error, StringComparison.Ordinal);
        Assert.Contains("NoSuchScript", unmatched.Error, StringComparison.Ordinal);
        Assert.Contains("Nothing was written.", unmatched.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("SmokeAuthoredSCRIPT:", unmatched.Error, StringComparison.Ordinal);
        Assert.Equal("", unmatched.Output);
        Assert.False(Directory.Exists(unmatchedOut));

        var filterMiss = await RunAsync(new ExportScriptsCommand.Request
        {
            InputPath = plugin,
            OutputDirectory = unmatchedOut,
            Filter = "NoSuchText"
        });
        Assert.True(filterMiss.ExitCode == 1, filterMiss.Describe());
        Assert.Contains("NoSuchText", filterMiss.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(unmatchedOut));

        var outDir = Path.Combine(_root, "conflict-out");
        var request = new ExportScriptsCommand.Request { InputPath = plugin, OutputDirectory = outDir };
        var first = await RunAsync(request);
        Assert.True(first.ExitCode == 0, first.Describe());

        var authoredPath = Path.Combine(outDir, "SmokeAuthoredSCRIPT.gek");
        var manifestPath = Path.Combine(outDir, "scripts.manifest.json");
        byte[] sentinel = [0x73, 0x65, 0x6E, 0x74, 0x69, 0x6E, 0x65, 0x6C];
        File.WriteAllBytes(authoredPath, sentinel);
        var manifestBefore = File.ReadAllBytes(manifestPath);

        // A second run into the same directory: refused before the input is even loaded.
        var second = await RunAsync(request);
        Assert.True(second.ExitCode == 1, second.Describe());
        Assert.Contains("already exist", second.Error, StringComparison.Ordinal);
        Assert.Contains("--overwrite", second.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Loading", second.Error, StringComparison.Ordinal);
        Assert.Equal(sentinel, File.ReadAllBytes(authoredPath));
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));

        // Only script files in the way: the writer's own check refuses before writing anything.
        File.Delete(manifestPath);
        var third = await RunAsync(request);
        Assert.True(third.ExitCode == 1, third.Describe());
        Assert.Contains("SmokeAuthoredSCRIPT.gek", third.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(manifestPath));
        Assert.Equal(sentinel, File.ReadAllBytes(authoredPath));

        var overwrite = await RunAsync(request with { Overwrite = true });
        Assert.True(overwrite.ExitCode == 0, overwrite.Describe());
        Assert.Equal(ExportScriptsTestPlugin.AuthoredSctx(), File.ReadAllBytes(authoredPath));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.True(manifest.RootElement.GetProperty("options").GetProperty("overwrite").GetBoolean());
    }

    [Fact]
    public async Task Invalid_values_and_inputs_exit_1_before_anything_is_loaded()
    {
        var plugin = WritePlugin("invalid");
        var outDir = Path.Combine(_root, "invalid-out");
        var textFile = Path.Combine(_root, "notes.txt");
        File.WriteAllText(textFile, "not a plugin");

        (ExportScriptsCommand.Request Request, string Expected)[] cases =
        [
            (new ExportScriptsCommand.Request { InputPath = plugin, OutputDirectory = outDir, Extension = "a/b" }, "a/b"),
            (new ExportScriptsCommand.Request { InputPath = plugin, OutputDirectory = outDir, Decompiled = "bogus" },
                "bogus"),
            (new ExportScriptsCommand.Request { InputPath = plugin, OutputDirectory = outDir, Ids = ["0xZZ"] }, "0xZZ"),
            (new ExportScriptsCommand.Request { InputPath = plugin, OutputDirectory = " " }, "--output"),
            (new ExportScriptsCommand.Request
                { InputPath = Path.Combine(_root, "missing.esm"), OutputDirectory = outDir }, "missing.esm"),
            (new ExportScriptsCommand.Request { InputPath = _root, OutputDirectory = outDir }, "directory"),
            (new ExportScriptsCommand.Request { InputPath = textFile, OutputDirectory = outDir }, "notes.txt")
        ];

        foreach (var (request, expected) in cases)
        {
            var run = await RunAsync(request);
            Assert.True(run.ExitCode == 1, run.Describe());
            Assert.Contains(expected, run.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("Loading", run.Error, StringComparison.Ordinal);
            Assert.Equal("", run.Output);
            Assert.False(Directory.Exists(outDir));
        }
    }

    private static void AssertParseError(Command command, string[] args, string? expected)
    {
        var errors = command.Parse(args).Errors;
        Assert.NotEmpty(errors);
        if (expected is not null)
        {
            Assert.Contains(errors, error => error.Message.Contains(expected, StringComparison.Ordinal));
        }
    }

    private static ExportScriptsCommand.IdSelector Id(string text)
    {
        Assert.True(ExportScriptsCommand.TryParseId(text, out var selector, out var problem), problem);
        return selector;
    }

    private string WritePlugin(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "FalloutNV.esm");
        File.WriteAllBytes(path, ExportScriptsTestPlugin.Build().Build());
        return path;
    }

    private static async Task<CommandRun> RunAsync(ExportScriptsCommand.Request request)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var log = new StringWriter();
        var exitCode = await ExportScriptsCommand.RunAsync(
            request,
            CreateConsole(output),
            CreateConsole(error),
            log,
            TestContext.Current.CancellationToken);
        return new CommandRun(exitCode, output.ToString(), error.ToString(), log.ToString());
    }

    private static IAnsiConsole CreateConsole(StringWriter writer)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        console.Profile.Width = 400;
        return console;
    }

    private static string[] FileNames(string directory)
    {
        return Directory.GetFiles(directory)
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ReadWindows1252(string path)
    {
        return CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(File.ReadAllBytes(path));
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string Sha256(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private sealed record CommandRun(int ExitCode, string Output, string Error, string Log)
    {
        public string Describe()
        {
            return $"exit={ExitCode}\n--- stdout ---\n{Output}\n--- stderr ---\n{Error}\n--- log ---\n{Log}";
        }
    }
}

/// <summary>
///     A synthetic FalloutNV.esm (the name makes the loader treat it as Fallout: New Vegas, so the default
///     extension is <c>.gek</c>) with two SCPT records:
///     <list type="bullet">
///         <item>
///             <description>
///                 0x00005001 <c>SmokeAuthoredSCRIPT</c>: SCHR, SCDA and an SCTX of ASCII text with CRLF line
///                 breaks and one 0x92 byte (Windows-1252 RIGHT SINGLE QUOTATION MARK, as in retail
///                 Lucky38MrHouseTerminalCodeSCRIPT's "Caesar's"), no trailing NUL. A UTF-8 writer turns the 0x92
///                 into E2 80 99, so it is what tells a verbatim export from a transcoded one.
///             </description>
///         </item>
///         <item>
///             <description>0x00005002 <c>SmokeCompiledOnlySCRIPT</c>: SCHR and SCDA only, no SCTX.</description>
///         </item>
///     </list>
///     Both carry the 20-byte little-endian SCDA of the July 2010 Xbox 360 prototype's NVCCBunkerLogBookSCRIPT
///     (ScriptName / Begin OnAdd / End), which the parser decompiles to <c>ScriptName &lt;EditorID&gt;</c>,
///     <c>Begin OnAdd</c>, <c>End</c>.
/// </summary>
internal static class ExportScriptsTestPlugin
{
    internal const uint AuthoredFormId = 0x00005001;
    internal const uint CompiledOnlyFormId = 0x00005002;
    internal const string AuthoredEditorId = "SmokeAuthoredSCRIPT";
    internal const string CompiledOnlyEditorId = "SmokeCompiledOnlySCRIPT";

    /// <summary>ScriptName / Begin OnAdd (event 3) / End, little-endian.</summary>
    internal static readonly byte[] Scda = Convert.FromHexString("1D00000010000800030004000000000011000000");

    /// <summary>The authored SCTX bytes: the one apostrophe is replaced by 0x92.</summary>
    internal static byte[] AuthoredSctx()
    {
        var bytes = Encoding.ASCII.GetBytes("scn SmokeAuthoredSCRIPT\r\n; Caesar's Legion\r\nBegin OnAdd\r\nEnd");
        bytes[Array.IndexOf(bytes, (byte)'\'')] = 0x92;
        return bytes;
    }

    internal static EsmTestFileBuilder Build()
    {
        return new EsmTestFileBuilder().AddTopLevelGrup(
            "SCPT",
            EsmTestFileBuilder.BuildRecord(
                "SCPT",
                AuthoredFormId,
                0,
                ("EDID", NullTerminated(AuthoredEditorId)),
                ("SCHR", Schr()),
                ("SCDA", Scda),
                ("SCTX", AuthoredSctx())),
            EsmTestFileBuilder.BuildRecord(
                "SCPT",
                CompiledOnlyFormId,
                0,
                ("EDID", NullTerminated(CompiledOnlyEditorId)),
                ("SCHR", Schr()),
                ("SCDA", Scda)));
    }

    /// <summary>An object script, compiled, no references or locals, SCDA length in bytes 8..11.</summary>
    private static byte[] Schr()
    {
        var schr = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(schr.AsSpan(8), (uint)Scda.Length);
        schr[18] = 0x01;
        return schr;
    }

    private static byte[] NullTerminated(string value)
    {
        return Encoding.ASCII.GetBytes(value + "\0");
    }
}
