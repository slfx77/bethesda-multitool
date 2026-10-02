using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.CLI.Commands.Esm;
using Spectre.Console;
using Xunit;
using static BethesdaMultitool.Tests.CLI.SemdiffTestRecords;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>
///     <c>esm semdiff --format json</c> end to end through <see cref="EsmSemdiffCommand.RunSemanticDiffCore" />,
///     with stdout, stderr and the console injected (no process-global <c>Console.SetOut</c>). The option
///     used to be parsed and discarded: the banner, the warnings and the table all went to stdout, so the
///     "JSON" could not be parsed. Each test writes two synthetic plugins named FalloutNV.esm (so the header
///     bits are named with New Vegas's table) into a fresh temp directory.
/// </summary>
public sealed class SemdiffCommandJsonTests
{
    [Fact]
    public void RunSemanticDiffCore_JsonFormat_WritesOnlyJsonToStdout()
    {
        using var directory = new PluginDirectory();
        var fileA = directory.Write("a", AchrBytes(0x00000400, 0x00195609, 3), CorruptCompressedWeapon());
        var fileB = directory.Write("b", AchrBytes(0x00000C00, 0x0006060B, 4));

        var run = Run(new EsmSemdiffCommand.SemdiffRequest(fileA, fileB) { Format = "json" });

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("", run.Console);
        Assert.EndsWith("\n", run.Stdout, StringComparison.Ordinal);

        // JsonDocument.Parse rejects leading text and a second top-level value, so this is the
        // "exactly one document on stdout" assertion.
        using var document = JsonDocument.Parse(run.Stdout);
        var root = document.RootElement;
        Assert.Equal("bethesda-multitool/esm-semdiff", root.GetProperty("schema").GetString());
        Assert.Equal("FalloutNewVegas", root.GetProperty("files").GetProperty("a").GetProperty("game").GetString());
        Assert.Equal("little", root.GetProperty("files").GetProperty("a").GetProperty("endianness").GetString());
        var described = root.GetProperty("files").GetProperty("a");
        Assert.Equal(Path.GetFullPath(fileA), described.GetProperty("path").GetString());
        Assert.Equal(new FileInfo(fileA).Length, described.GetProperty("sizeBytes").GetInt64());

        // The corrupt compressed record is reported on stderr AND in the document.
        Assert.Contains("File A: 1 compressed record(s) could not be decompressed", run.Stderr);
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal("compressed-skipped", warning.GetProperty("code").GetString());
        Assert.Equal("A", warning.GetProperty("side").GetString());
        Assert.Equal(1, warning.GetProperty("count").GetInt32());

        var record = Assert.Single(root.GetProperty("records").EnumerateArray());
        Assert.Equal("different", record.GetProperty("status").GetString());
        var added = Assert.Single(record.GetProperty("header").GetProperty("flagsAdded").EnumerateArray());
        Assert.Equal(11, added.GetProperty("bit").GetInt32());
        Assert.Equal("Initially Disabled", added.GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("withDifferences").GetInt32());
    }

    [Fact]
    public void RunSemanticDiffCore_TableFormat_Control_WritesTheTableAndNoDocument()
    {
        // Control for the JSON test: the same inputs in table mode DO write to the console and never to the
        // JSON stream, so the empty console above is JSON mode's doing.
        using var directory = new PluginDirectory();
        var fileA = directory.Write("a", AchrBytes(0x00000400, 0x00195609, 3), CorruptCompressedWeapon());
        var fileB = directory.Write("b", AchrBytes(0x00000C00, 0x0006060B, 4));

        var run = Run(new EsmSemdiffCommand.SemdiffRequest(fileA, fileB));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("", run.Stdout);
        Assert.Contains("Semantic ESM Diff", run.Console);
        Assert.Contains("+ bit 11 (0x00000800) Initially Disabled", run.Console);
    }

    [Fact]
    public void RunSemanticDiffCore_JsonFormat_MissingFile_ErrorsOnStderrOnly()
    {
        using var directory = new PluginDirectory();
        var missing = Path.Combine(directory.Path, "missing", "FalloutNV.esm");
        var fileB = directory.Write("b", AchrBytes(0x00000C00, 0x0006060B, 4));

        var run = Run(new EsmSemdiffCommand.SemdiffRequest(missing, fileB) { Format = "json" });

        Assert.Equal(1, run.ExitCode);
        Assert.Equal("", run.Stdout);
        Assert.Equal("", run.Console);
        Assert.Contains("File not found", run.Stderr);
        Assert.Contains(missing, run.Stderr);
    }

    [Fact]
    public void RunSemanticDiffCore_JsonFormat_LimitTruncatesAndDuplicateFormIdWarningsCollapse()
    {
        // 25 INFO FormIDs occur twice in A (the Xbox 360 split-INFO shape) and once, identical, in B: each
        // first occurrence pairs as identical and each second is only in A.
        using var directory = new PluginDirectory();
        var recordsA = new List<byte[]>();
        var recordsB = new List<byte[]>();
        for (uint i = 0; i < 25; i++)
        {
            var info = InfoBytes(0x00100000 + i);
            recordsA.Add(info);
            recordsA.Add(info);
            recordsB.Add(info);
        }

        var fileA = directory.Write("a", [.. recordsA]);
        var fileB = directory.Write("b", [.. recordsB]);

        var run = Run(new EsmSemdiffCommand.SemdiffRequest(fileA, fileB) { Format = "json", Limit = 5 });

        Assert.Equal(0, run.ExitCode);
        using var document = JsonDocument.Parse(run.Stdout);
        var root = document.RootElement;

        var summary = root.GetProperty("summary");
        Assert.Equal(25, summary.GetProperty("onlyInA").GetInt32());
        Assert.Equal(25, summary.GetProperty("identical").GetInt32());
        Assert.Equal(25, summary.GetProperty("listed").GetInt32());
        Assert.Equal(5, summary.GetProperty("emitted").GetInt32());
        Assert.True(summary.GetProperty("truncated").GetBoolean());
        Assert.Equal(5, root.GetProperty("records").GetArrayLength());
        Assert.Equal(1, root.GetProperty("records")[0].GetProperty("a").GetProperty("occurrence").GetInt32());

        // 25 per-FormID warnings become one entry, and one stderr line.
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal("duplicate-formid", warning.GetProperty("code").GetString());
        Assert.Equal("A", warning.GetProperty("side").GetString());
        Assert.Equal(25, warning.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, warning.GetProperty("formId").ValueKind);
        Assert.Equal(20, warning.GetProperty("examples").GetArrayLength());
        Assert.Equal("0x00100000", warning.GetProperty("examples")[0].GetProperty("formId").GetString());

        var stderrLines = run.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(stderrLines);
        Assert.StartsWith("Warning: 25 FormIDs occur more than once in File A", line, StringComparison.Ordinal);
    }

    private static RunResult Run(EsmSemdiffCommand.SemdiffRequest request)
    {
        var consoleOutput = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(consoleOutput),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        console.Profile.Width = 240;
        using var stdout = new MemoryStream();
        using var stderr = new StringWriter();

        var exitCode = EsmSemdiffCommand.RunSemanticDiffCore(request, console, stdout, stderr);
        return new RunResult(exitCode, Encoding.UTF8.GetString(stdout.ToArray()), stderr.ToString(),
            consoleOutput.ToString());
    }

    private static byte[] AchrBytes(uint flags, uint versionControl1, ushort versionControl2)
    {
        return RecordBytes(false, "ACHR", 0x000E739E, flags, versionControl1, 15, versionControl2, false,
            ("NAME", U32(0x00123456)), ("DATA", new byte[24]));
    }

    private static byte[] InfoBytes(uint formId)
    {
        return RecordBytes(false, "INFO", formId, 0, 0, 15, 0, false, ("DATA", new byte[] { 2, 0, 0, 0 }));
    }

    /// <summary>
    ///     A WEAP whose compressed flag is set but whose payload (a decompressed-size prefix and bytes that
    ///     fail the zlib header check) cannot be inflated, so the parser skips and counts it.
    /// </summary>
    private static byte[] CorruptCompressedWeapon()
    {
        byte[] payload = [0x10, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        var record = new byte[24 + payload.Length];
        "WEAP"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), 0x00040000); // compressed flag
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), 0x00030003);
        payload.CopyTo(record, 24);
        return record;
    }

    private sealed record RunResult(int ExitCode, string Stdout, string Stderr, string Console);

    /// <summary>A temp directory holding one <c>&lt;build&gt;/FalloutNV.esm</c> per written plugin.</summary>
    private sealed class PluginDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "semdiff-json-" + Guid.NewGuid().ToString("N"));

        public string Write(string build, params byte[][] records)
        {
            var buildDirectory = System.IO.Path.Combine(Path, build);
            Directory.CreateDirectory(buildDirectory);
            var path = System.IO.Path.Combine(buildDirectory, "FalloutNV.esm");
            File.WriteAllBytes(path, records.SelectMany(r => r).ToArray());
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
            catch (IOException)
            {
                // Best effort: a lingering handle only leaves a temp directory behind.
            }
        }
    }
}
