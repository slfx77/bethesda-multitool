using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Scripts;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export.Scripts;

/// <summary>
///     Pins the per-script export: authored SCTX written byte for byte as Windows-1252 with no wrapper, a
///     reconstruction written only as a separately named, banner-headed <c>.decompiled</c> file rendered from
///     <see cref="ScriptRecord.DecompiledText" /> (never from a decompiled substitute in
///     <see cref="ScriptRecord.SourceText" />), the provenance each file carries, the manifest and its hashes,
///     and the refusal to overwrite. The discriminating byte is 0x92 (RIGHT SINGLE QUOTATION MARK in
///     Windows-1252, as in retail Lucky38MrHouseTerminalCodeSCRIPT's "Caesar's"): a UTF-8 writer turns it
///     into E2 80 99. Expected values are independent literals or hashes computed here from the bytes on disk.
/// </summary>
public sealed class ScriptExportWriterTests : IDisposable
{
    private static readonly DateTimeOffset CreatedUtc = new(2026, 9, 28, 12, 34, 56, TimeSpan.Zero);

    private const string AbsenceWording =
        "absence from a partial memory dump is not evidence of absence from the build";

    private const string ExportBannerFirstLine =
        "; Reconstruction from SCDA — BethesdaMultitool";

    private const string DecompiledStatements = "ScriptName FooSCRIPT\nBegin GameMode\n  Set fTimer to 1\nEnd";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bmt_script_export_{Guid.NewGuid():N}");

    [Fact]
    public void Reconstruction_uses_scrv_for_refs_and_preserves_stored_source_and_raw_types()
    {
        const string stored = "scn Types\r\nref rTarget\r\nfloat fTimer\r\n; original spacing";
        var script = new ScriptRecord
        {
            FormId = 0x2000, EditorId = "Types", SourceText = stored,
            // Neither a name prefix nor dot-use decides the declarations.
            DecompiledText = "Begin GameMode\n; fTimer.MisleadingText\nEnd",
            CompiledData = [0x1D, 0, 0, 0],
            Variables = [new(1, "rTarget", 0), new(2, "fTimer", 0)],
            ReferencedObjects = [0x80000001],
            ExternalVariableBindings = [new(0x1000, 9, "resolved", 0x2001, "Flag", [0x2001], [0x1000, 0x2001])]
        };
        var directory = Path.Combine(_root, "types");

        ScriptExportWriter.Write([script], PluginSource(),
            Options(".gek") with { Decompiled = ScriptDecompiledPolicy.All }, directory, null, CreatedUtc);

        Assert.Equal(Encoding.ASCII.GetBytes(stored), File.ReadAllBytes(Path.Combine(directory, "Types.gek")));
        var reconstructed = EsmStringUtils.DecodeGameText(File.ReadAllBytes(Path.Combine(directory, "Types.decompiled.gek")));
        Assert.Contains("ref rTarget\r\nfloat fTimer\r\n", reconstructed, StringComparison.Ordinal);
        Assert.All(script.Variables, variable => Assert.Equal((byte)0, variable.Type));
        using var manifest = ReadManifest(directory);
        var binding = Assert.Single(manifest.RootElement.GetProperty("scripts")[0].GetProperty("externalVariables").EnumerateArray());
        Assert.Equal("0x00001000", binding.GetProperty("ownerFormId").GetString());
        Assert.Equal(9, binding.GetProperty("variableIndex").GetInt32());
        Assert.Equal("resolved", binding.GetProperty("status").GetString());
        Assert.Equal("0x00002001", binding.GetProperty("scriptFormId").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void Authored_source_is_written_byte_for_byte_windows1252()
    {
        var sctx = CaesarSctx("scn Foo\r\n; Caesar_s Legion\r\nBegin GameMode\r\nEnd");
        var script = new ScriptRecord
        {
            FormId = 0x001746C1,
            EditorId = "Foo",
            SourceText = EsmStringUtils.DecodeGameText(sctx)
        };
        var directory = Path.Combine(_root, "out");

        var summary = ScriptExportWriter.Write([script], PluginSource(), Options(".gek"), directory, null, CreatedUtc);

        var written = File.ReadAllBytes(Path.Combine(directory, "Foo.gek"));
        Assert.Equal(sctx, written);
        Assert.Single(written, b => b == 0x92);
        Assert.DoesNotContain(written, b => b == 0xE2);
        Assert.Equal((byte)'s', written[0]);
        Assert.Equal((byte)'d', written[^1]);
        Assert.Equal(1, summary.StoredSourceFiles);
        Assert.Equal(0, summary.DecompiledFiles);
    }

    [Fact]
    public async Task Existing_carve_entry_point_writes_gek_without_report_wrapper()
    {
        var sctx = CaesarSctx("scn Foo\r\n; Caesar_s Legion\r\nBegin GameMode\r\nEnd");
        var script = new ScriptRecord
        {
            FormId = 0x10,
            EditorId = "Foo",
            SourceText = EsmStringUtils.DecodeGameText(sctx),
            DecompiledText = DecompiledStatements,
            CompiledData = [0x1D, 0x00, 0x00, 0x00],
            Variables = [new ScriptVariableInfo(1, "fTimer", 0)]
        };

        var summary = await EsmRecordExporter.ExportParsedScriptsAsync(
            [script], new Dictionary<uint, string>(), _root, PluginSource());

        var scriptsDirectory = Path.Combine(_root, "scripts");
        Assert.NotNull(summary);
        Assert.Equal(sctx, File.ReadAllBytes(Path.Combine(scriptsDirectory, "Foo.gek")));
        Assert.True(File.Exists(Path.Combine(scriptsDirectory, "scripts.manifest.json")));
        Assert.False(File.Exists(Path.Combine(scriptsDirectory, "Foo.txt")));
        // The carve path's policy is "missing": a script with source gets no reconstruction beside it.
        Assert.False(File.Exists(Path.Combine(scriptsDirectory, "Foo.decompiled.gek")));
        var exported = EsmStringUtils.DecodeGameText(File.ReadAllBytes(Path.Combine(scriptsDirectory, "Foo.gek")));
        Assert.StartsWith("scn Foo\r\n", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("; Script:", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("=== Source Text (SCTX) ===", exported, StringComparison.Ordinal);

        // Re-extraction replaces what is there, as the carve pipeline always has.
        await EsmRecordExporter.ExportParsedScriptsAsync([script], null, _root, PluginSource());
        Assert.Equal(sctx, File.ReadAllBytes(Path.Combine(scriptsDirectory, "Foo.gek")));

        // The extension follows the game: only FNV and FO3 default to .gek.
        var oblivionRoot = Path.Combine(_root, "oblivion");
        await EsmRecordExporter.ExportParsedScriptsAsync(
            [script], null, oblivionRoot, PluginSource() with { Game = BethesdaGame.Oblivion });
        Assert.True(File.Exists(Path.Combine(oblivionRoot, "scripts", "Foo.txt")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Decompiled_substitute_is_never_exported_as_stored_source(bool dumpInput)
    {
        IReadOnlyList<ScriptVariableInfo> variables = [new ScriptVariableInfo(1, "fTimer", 0)];
        var substitute = CapturedScriptEmissionContract.BuildDecompiledSource(DecompiledStatements, variables, "Foo");
        Assert.NotNull(substitute);
        var script = new ScriptRecord
        {
            FormId = 0x0001ABCD,
            EditorId = "Foo",
            SourceText = substitute,
            SourceTextOrigin = dumpInput ? ScriptSourceTextOrigin.DecompiledFromBytecode : ScriptSourceTextOrigin.None,
            DecompiledText = DecompiledStatements,
            CompiledData = [0x1D, 0x00, 0x00, 0x00],
            Variables = [.. variables],
            FromRuntime = dumpInput
        };
        var directory = Path.Combine(_root, "dump");

        var exportSource = dumpInput ? DumpSource() : PluginSource();
        var summary = ScriptExportWriter.Write([script], exportSource, Options(".gek"), directory, null, CreatedUtc);

        Assert.False(File.Exists(Path.Combine(directory, "Foo.gek")));
        var text = EsmStringUtils.DecodeGameText(File.ReadAllBytes(Path.Combine(directory, "Foo.decompiled.gek")));
        Assert.StartsWith(ExportBannerFirstLine + "\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Decompiled from captured SCDA", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no proven source text in the dump", text, StringComparison.Ordinal);
        Assert.Contains(dumpInput
                ? "; Memory dump: Fallout_Release_Beta.xex21.dmp (partial capture)\r\n"
                : $"; Plugin: {exportSource.FileName}\r\n", text,
            StringComparison.Ordinal);
        Assert.Contains("; FormID: 0x0001ABCD\r\n", text, StringComparison.Ordinal);
        Assert.Contains("; Build: (no build label)\r\n", text, StringComparison.Ordinal);
        Assert.Contains("; Bytecode byte order: little-endian", text, StringComparison.Ordinal);
        Assert.Contains("ScriptName Foo\r\n", text, StringComparison.Ordinal);
        Assert.Contains("float fTimer\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', text.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain('\r', text.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        Assert.Equal(1, summary.DecompiledFiles);
        Assert.Equal(0, summary.StoredSourceFiles + summary.CapturedSourceFiles);

        using var manifest = ReadManifest(directory);
        var entry = manifest.RootElement.GetProperty("scripts")[0];
        Assert.Equal("decompiled-from-bytecode", entry.GetProperty("sourceText").GetProperty("kind").GetString());
        var file = Assert.Single(entry.GetProperty("files").EnumerateArray());
        Assert.Equal("Foo.decompiled.gek", file.GetProperty("path").GetString());
        Assert.Equal("decompiled-from-scda", file.GetProperty("content").GetString());
        Assert.Equal("decompiled-from-bytecode", file.GetProperty("provenance").GetString());
        Assert.Equal("crlf", file.GetProperty("lineEndings").GetString());
    }

    [Fact]
    public void Captured_dump_source_keeps_origin_and_correspondence()
    {
        var runtime = new ScriptRecord
        {
            FormId = 0x10,
            EditorId = "RuntimeFoo",
            SourceText = "scn RuntimeFoo\r\nBegin GameMode\r\nEnd",
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
            FromRuntime = true
        };
        var fragment = new ScriptRecord
        {
            FormId = 0x20,
            EditorId = "FragmentFoo",
            SourceText = "scn FragmentFoo",
            SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment
        };
        var unattributed = new ScriptRecord
        {
            FormId = 0x30,
            EditorId = "LooseFoo",
            SourceText = "scn LooseFoo"
        };
        var dumpDirectory = Path.Combine(_root, "dump");

        var dumpSummary = ScriptExportWriter.Write(
            [runtime, fragment, unattributed], DumpSource(), Options(".gek"), dumpDirectory, null, CreatedUtc);

        Assert.Equal(3, dumpSummary.CapturedSourceFiles);
        Assert.Equal(0, dumpSummary.StoredSourceFiles);
        Assert.Equal("scn RuntimeFoo\r\nBegin GameMode\r\nEnd",
            Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(dumpDirectory, "RuntimeFoo.gek"))));
        using (var manifest = ReadManifest(dumpDirectory))
        {
            Assert.Equal(0, manifest.RootElement.GetProperty("summary").GetProperty("storedSourceFiles").GetInt32());
            Assert.False(manifest.RootElement.GetProperty("summary").TryGetProperty("authoredSourceFiles", out _));
            var scripts = manifest.RootElement.GetProperty("scripts");
            AssertFile(scripts[0], "RuntimeFoo.gek", "captured-source", "runtime-same-object", "accepted");
            AssertFile(scripts[1], "FragmentFoo.gek", "captured-source", "dmp-fragment", "unverified");
            AssertFile(scripts[2], "LooseFoo.gek", "captured-source", "unattributed-same-dump", "unverified");
        }

        var pluginDirectory = Path.Combine(_root, "plugin");
        var pluginSummary = ScriptExportWriter.Write(
            [unattributed], PluginSource(), Options(".gek"), pluginDirectory, null, CreatedUtc);

        Assert.Equal(1, pluginSummary.StoredSourceFiles);
        Assert.Equal(0, pluginSummary.CapturedSourceFiles);
        using (var manifest = ReadManifest(pluginDirectory))
        {
            Assert.Equal(1, manifest.RootElement.GetProperty("summary").GetProperty("storedSourceFiles").GetInt32());
            AssertFile(
                manifest.RootElement.GetProperty("scripts")[0],
                "LooseFoo.gek",
                "stored-source",
                "plugin-record",
                "not-applicable");
        }
    }

    [Theory]
    [InlineData(ScriptDecompiledPolicy.Missing, "missing", false, true)]
    [InlineData(ScriptDecompiledPolicy.All, "all", true, true)]
    [InlineData(ScriptDecompiledPolicy.None, "none", false, false)]
    public void Decompiled_policy_controls_companion_files(
        ScriptDecompiledPolicy policy,
        string token,
        bool sourcedScriptGetsCompanion,
        bool sourceLessScriptGetsCompanion)
    {
        var sourced = new ScriptRecord
        {
            FormId = 0x10,
            EditorId = "WithSource",
            SourceText = "scn WithSource\r\nBegin GameMode\r\nEnd",
            DecompiledText = "ScriptName WithSource\nBegin GameMode\nEnd",
            CompiledData = [0x1D, 0x00, 0x00, 0x00]
        };
        var sourceLess = new ScriptRecord
        {
            FormId = 0x20,
            EditorId = "SourceLess",
            DecompiledText = "ScriptName SourceLess\nBegin GameMode\nEnd",
            CompiledData = [0x1D, 0x00, 0x00, 0x00]
        };
        var undecompiled = new ScriptRecord
        {
            FormId = 0x30,
            EditorId = "Undecompiled",
            CompiledData = [0x1D, 0x00, 0x00, 0x00]
        };
        var empty = new ScriptRecord { FormId = 0x40, EditorId = "Empty" };
        var directory = Path.Combine(_root, token);

        var summary = ScriptExportWriter.Write(
            [sourced, sourceLess, undecompiled, empty],
            PluginSource(),
            new ScriptExportOptions { Extension = "gek", Decompiled = policy },
            directory,
            null,
            CreatedUtc);

        Assert.True(File.Exists(Path.Combine(directory, "WithSource.gek")));
        Assert.Equal(sourcedScriptGetsCompanion, File.Exists(Path.Combine(directory, "WithSource.decompiled.gek")));
        Assert.False(File.Exists(Path.Combine(directory, "SourceLess.gek")));
        Assert.Equal(sourceLessScriptGetsCompanion,
            File.Exists(Path.Combine(directory, "SourceLess.decompiled.gek")));
        Assert.Empty(Directory.GetFiles(directory, "Undecompiled*"));
        Assert.Empty(Directory.GetFiles(directory, "Empty*"));

        using var manifest = ReadManifest(directory);
        Assert.Equal(token, manifest.RootElement.GetProperty("options").GetProperty("decompiled").GetString());
        var scripts = manifest.RootElement.GetProperty("scripts");
        Assert.Equal(JsonValueKind.Null, scripts[0].GetProperty("skipped").ValueKind);
        Assert.Equal(
            sourceLessScriptGetsCompanion ? null : "decompiled-not-requested",
            scripts[1].GetProperty("skipped").GetString());
        Assert.Equal("bytecode-not-decompiled", scripts[2].GetProperty("skipped").GetString());
        Assert.Equal("no-source-or-bytecode", scripts[3].GetProperty("skipped").GetString());
        Assert.Equal(sourceLessScriptGetsCompanion ? 2 : 3, summary.SkippedScripts);
    }

    [Fact]
    public void Manifest_is_valid_json_and_hashes_match_files()
    {
        var sctx = CaesarSctx("scn VERShadows01QuestScript\r\n; Caesar_s\r\nshort iStage\r\nref rTarget\r\nEnd");
        var script = new ScriptRecord
        {
            FormId = 0x00168CFC,
            EditorId = "VERShadows01QuestScript",
            SourceText = EsmStringUtils.DecodeGameText(sctx),
            IsQuestScript = true,
            IsCompiled = true,
            CompiledSize = 4,
            VariableCount = 2,
            RefObjectCount = 2,
            CompiledData = [0x1D, 0x00, 0x00, 0x00],
            Variables = [new ScriptVariableInfo(1, "iStage", 1), new ScriptVariableInfo(2, "rTarget", 0)],
            ReferencedObjects = [0x00000014, 0x80000002],
            Offset = 0x1234
        };
        var source = PluginSource() with { PluginVersion = 1.34f, PluginMasters = ["FalloutNV.esm"] };
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");

        ScriptExportWriter.Write([script], source, Options(".gek"), first, ResolvePlayerRef, CreatedUtc);
        ScriptExportWriter.Write([script], source, Options(".gek"), second, ResolvePlayerRef, CreatedUtc);

        var firstBytes = File.ReadAllBytes(Path.Combine(first, "scripts.manifest.json"));
        Assert.Equal(firstBytes, File.ReadAllBytes(Path.Combine(second, "scripts.manifest.json")));

        using var manifest = JsonDocument.Parse(firstBytes);
        var root = manifest.RootElement;
        Assert.Equal("bethesda-multitool/script-export", root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("toolVersion").GetString()));
        Assert.Equal("2026-09-28T12:34:56Z", root.GetProperty("createdUtc").GetString());

        var sourceJson = root.GetProperty("source");
        Assert.Equal("plugin", sourceJson.GetProperty("kind").GetString());
        Assert.Equal("FalloutNewVegas", sourceJson.GetProperty("game").GetString());
        Assert.Equal(JsonValueKind.Null, sourceJson.GetProperty("dump").ValueKind);
        Assert.Equal(1.34f, sourceJson.GetProperty("plugin").GetProperty("version").GetSingle());
        Assert.Equal("FalloutNV.esm", sourceJson.GetProperty("plugin").GetProperty("masters")[0].GetString());
        Assert.Equal(".gek", root.GetProperty("options").GetProperty("extension").GetString());

        var entry = Assert.Single(root.GetProperty("scripts").EnumerateArray());
        Assert.Equal("0x00168CFC", entry.GetProperty("formId").GetString());
        Assert.Equal("VERShadows01QuestScript", entry.GetProperty("stem").GetString());
        Assert.Equal("Quest", entry.GetProperty("scriptType").GetString());
        Assert.Equal(0x1234L, entry.GetProperty("recordOffset").GetInt64());
        Assert.Equal("plugin-record", entry.GetProperty("sourceText").GetProperty("kind").GetString());
        Assert.Equal(
            "ad497f997ead95db601f7d7ed72a7a624ba52ce6f4145a6dc7ec10d1f03876a9",
            entry.GetProperty("bytecode").GetProperty("sha256").GetString());
        Assert.Equal("little", entry.GetProperty("bytecode").GetProperty("endianness").GetString());

        // What the old per-script .txt wrapper printed as "; === Variables ===" / "; === Referenced Objects ===".
        var variables = entry.GetProperty("variables");
        Assert.Equal(2, variables.GetArrayLength());
        Assert.Equal(1u, variables[0].GetProperty("index").GetUInt32());
        Assert.Equal("iStage", variables[0].GetProperty("name").GetString());
        Assert.Equal("int", variables[0].GetProperty("type").GetString());
        Assert.Equal("ref", variables[1].GetProperty("type").GetString());
        Assert.Equal("float", variables[1].GetProperty("storageType").GetString());
        Assert.Equal(0, variables[1].GetProperty("typeByte").GetInt32());
        Assert.Equal("scrv-local-reference", variables[1].GetProperty("typeEvidence").GetString());
        var references = entry.GetProperty("referencedObjects");
        Assert.Equal("0x00000014", references[0].GetProperty("formId").GetString());
        Assert.Equal("PlayerRef", references[0].GetProperty("editorId").GetString());
        Assert.Equal(JsonValueKind.Null, references[0].GetProperty("scrvVariableIndex").ValueKind);
        Assert.Equal(JsonValueKind.Null, references[1].GetProperty("formId").ValueKind);
        Assert.Equal(2u, references[1].GetProperty("scrvVariableIndex").GetUInt32());

        foreach (var file in entry.GetProperty("files").EnumerateArray())
        {
            var bytes = File.ReadAllBytes(Path.Combine(first, file.GetProperty("path").GetString()!));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.GetProperty("sha256").GetString());
            Assert.Equal(bytes.Length, file.GetProperty("byteLength").GetInt32());
            Assert.Equal("windows-1252", file.GetProperty("encoding").GetString());
            Assert.Equal(0, file.GetProperty("unmappedCharacters").GetInt32());
        }

        Assert.Equal(1, root.GetProperty("summary").GetProperty("filesWritten").GetInt32());
    }

    [Fact]
    public void Memory_dump_manifest_states_partial_capture()
    {
        var dumpPath = Path.Combine(_root, "Fallout_Release_Beta.xex21.dmp");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(dumpPath, [0x4D, 0x44, 0x4D, 0x50]);
        var raw = new AnalysisResult
        {
            BuildType = "Release Beta",
            MinidumpInfo = new MinidumpInfo
            {
                ProcessorArchitecture = 0x03,
                Modules = [new MinidumpModule { Name = "Fallout_Release_Beta.exe", TimeDateStamp = 0x4B7C2A10 }]
            }
        };
        var source = ScriptExportSource.Describe(dumpPath, raw, BethesdaGame.FalloutNewVegas, true, null);
        var script = new ScriptRecord
        {
            FormId = 0x10,
            EditorId = "Foo",
            SourceText = "scn Foo",
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject
        };
        var directory = Path.Combine(_root, "out");

        ScriptExportWriter.Write([script], source, Options(".gek"), directory, null, CreatedUtc);

        using var manifest = ReadManifest(directory);
        var sourceJson = manifest.RootElement.GetProperty("source");
        Assert.Equal("memory-dump", sourceJson.GetProperty("kind").GetString());
        Assert.Equal("big", sourceJson.GetProperty("containerEndianness").GetString());
        Assert.Equal(JsonValueKind.Null, sourceJson.GetProperty("plugin").ValueKind);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dumpPath))),
            sourceJson.GetProperty("sha256").GetString());
        var dump = sourceJson.GetProperty("dump");
        Assert.Equal("partial-memory-dump", dump.GetProperty("capture").GetString());
        Assert.Contains(AbsenceWording, dump.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Equal("Release Beta", dump.GetProperty("buildType").GetString());
        Assert.Equal("Fallout_Release_Beta.exe", dump.GetProperty("gameModule").GetString());
        Assert.Equal("2010-02-17T17:40:32Z", dump.GetProperty("gameModuleTimeDateStampUtc").GetString());
    }

    [Fact]
    public void Existing_files_are_not_overwritten_and_nothing_is_partially_written()
    {
        var directory = Path.Combine(_root, "out");
        Directory.CreateDirectory(directory);
        var existing = Path.Combine(directory, "Second.gek");
        File.WriteAllText(existing, "keep");
        IReadOnlyList<ScriptRecord> scripts =
        [
            new ScriptRecord { FormId = 0x10, EditorId = "First", SourceText = "scn First" },
            new ScriptRecord { FormId = 0x20, EditorId = "Second", SourceText = "scn Second" },
            new ScriptRecord { FormId = 0x30, EditorId = "Third", SourceText = "scn Third" }
        ];

        var conflict = Assert.Throws<ScriptExportConflictException>(() =>
            ScriptExportWriter.Write(scripts, PluginSource(), Options(".gek"), directory, null, CreatedUtc));

        Assert.Equal("Second.gek", Path.GetFileName(Assert.Single(conflict.ConflictingPaths)));
        Assert.Equal(["Second.gek"], Directory.GetFiles(directory).Select(Path.GetFileName));
        Assert.Equal("keep", File.ReadAllText(existing));

        // An existing manifest alone also blocks the export.
        var manifestOnly = Path.Combine(_root, "manifest-only");
        Directory.CreateDirectory(manifestOnly);
        File.WriteAllText(Path.Combine(manifestOnly, "scripts.manifest.json"), "{}");
        Assert.Throws<ScriptExportConflictException>(() =>
            ScriptExportWriter.Write(scripts, PluginSource(), Options(".gek"), manifestOnly, null, CreatedUtc));
        Assert.Single(Directory.GetFiles(manifestOnly));

        var summary = ScriptExportWriter.Write(
            scripts,
            PluginSource(),
            new ScriptExportOptions { Extension = ".gek", Overwrite = true },
            directory,
            null,
            CreatedUtc);

        Assert.Equal(3, summary.FilesWritten);
        Assert.Equal("scn Second", File.ReadAllText(existing));
        Assert.True(File.Exists(Path.Combine(directory, "First.gek")));
        Assert.True(File.Exists(Path.Combine(directory, "Third.gek")));
        Assert.True(File.Exists(Path.Combine(directory, "scripts.manifest.json")));
    }

    [Fact]
    public void Emission_banner_is_concise_and_export_banner_replaces_it()
    {
        IReadOnlyList<ScriptVariableInfo> variables = [new ScriptVariableInfo(1, "fA", 0)];
        const string statements = "ScriptName X\nBegin GameMode\nEnd";
        var nl = Environment.NewLine;

        var emission = CapturedScriptEmissionContract.BuildDecompiledSource(statements, variables, "Foo");
        var exported = CapturedScriptEmissionContract.BuildDecompiledSource(
            statements, variables, "Foo", ["; A", "; B"]);

        // The new emission marker is concise; legacy markers are tested on re-import.
        Assert.Equal(
            "; Reconstruction from SCDA — BethesdaMultitool" + nl +
            "; Declarations: SLSD/SCVR and SCRV." + nl +
            "ScriptName Foo" + nl + nl + "float fA" + nl + nl + "Begin GameMode" + nl + "End",
            emission);
        Assert.Equal(
            "; A" + nl + "; B" + nl +
            "ScriptName Foo" + nl + nl + "float fA" + nl + nl + "Begin GameMode" + nl + "End",
            exported);
    }

    [Fact]
    public void Plugin_source_description_reads_header_and_corpus_build_label()
    {
        var hedr = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(hedr, 1.34f);
        BinaryPrimitives.WriteInt32LittleEndian(hedr.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hedr.AsSpan(8), 0x800);
        var plugin = EsmTestFileBuilder.BuildRecord(
            "TES4", 0, 0, ("HEDR", hedr), ("MAST", Encoding.ASCII.GetBytes("FalloutNV.esm\0")));
        var dataDirectory = Path.Combine(
            _root, "Sample", "Builds", "Fallout - New Vegas (2010-7-21, X360 - Prototype)", "FalloutNV", "Data");
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "Synthetic.esm");
        File.WriteAllBytes(path, plugin);

        var source = ScriptExportSource.Describe(path, null, BethesdaGame.FalloutNewVegas, false, null);

        Assert.Equal(ScriptExportSourceKind.Plugin, source.Kind);
        Assert.Equal("Synthetic.esm", source.FileName);
        Assert.Equal((long)plugin.Length, source.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(plugin)), source.Sha256);
        Assert.Equal("little", source.ContainerEndianness);
        Assert.Equal(1.34f, source.PluginVersion);
        Assert.Equal(["FalloutNV.esm"], source.PluginMasters);
        Assert.Equal("Fallout - New Vegas (2010-7-21, X360 - Prototype)", source.BuildLabel);
        Assert.Equal("corpus-path", source.BuildLabelSource);

        var labelled = ScriptExportSource.Describe(path, null, BethesdaGame.FalloutNewVegas, false, "  my build  ");
        Assert.Equal("my build", labelled.BuildLabel);
        Assert.Equal("user", labelled.BuildLabelSource);

        var outside = Path.Combine(_root, "Synthetic.esm");
        File.WriteAllBytes(outside, plugin);
        var unlabelled = ScriptExportSource.Describe(outside, null, BethesdaGame.FalloutNewVegas, false, null);
        Assert.Null(unlabelled.BuildLabel);
        Assert.Null(unlabelled.BuildLabelSource);
    }

    [Fact]
    public async Task Carved_sctx_fragments_are_written_verbatim_with_a_manifest()
    {
        var bytes = CaesarSctx("scn Fragment\r\n; Caesar_s Legion\r\nEnd");
        var fragment = new SctxRecord(EsmStringUtils.DecodeGameText(bytes), 0x1234, bytes.Length + 1);

        await EsmRecordExporter.ExportScriptSourcesAsync([fragment], _root);

        var directory = Path.Combine(_root, "script_sources");
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(directory, "sctx_0000_0x00001234.gek")));
        Assert.False(File.Exists(Path.Combine(directory, "sctx_0000_0x00001234.txt")));

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal("bethesda-multitool/script-sources", root.GetProperty("schema").GetString());
        Assert.Equal("carved-sctx-fragment", root.GetProperty("provenance").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("source").ValueKind);
        Assert.Contains(AbsenceWording, root.GetProperty("note").GetString(), StringComparison.Ordinal);
        var entry = Assert.Single(root.GetProperty("fragments").EnumerateArray());
        Assert.Equal("sctx_0000_0x00001234.gek", entry.GetProperty("path").GetString());
        Assert.Equal(0x1234L, entry.GetProperty("offset").GetInt64());
        Assert.Equal(bytes.Length + 1, entry.GetProperty("subrecordLength").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), entry.GetProperty("sha256").GetString());
    }

    /// <summary>ASCII text with its single '_' replaced by 0x92, the Windows-1252 right single quote.</summary>
    private static byte[] CaesarSctx(string asciiWithUnderscore)
    {
        var bytes = Encoding.ASCII.GetBytes(asciiWithUnderscore);
        bytes[Array.IndexOf(bytes, (byte)'_')] = 0x92;
        return bytes;
    }

    private static ScriptExportOptions Options(string extension)
    {
        return new ScriptExportOptions { Extension = extension };
    }

    private static ScriptExportSource PluginSource()
    {
        return new ScriptExportSource
        {
            Path = "/synthetic/FalloutNV.esm",
            FileName = "FalloutNV.esm",
            Kind = ScriptExportSourceKind.Plugin,
            Game = BethesdaGame.FalloutNewVegas,
            ContainerEndianness = "little"
        };
    }

    private static ScriptExportSource DumpSource()
    {
        return new ScriptExportSource
        {
            Path = "/synthetic/Fallout_Release_Beta.xex21.dmp",
            FileName = "Fallout_Release_Beta.xex21.dmp",
            Kind = ScriptExportSourceKind.MemoryDump,
            Game = BethesdaGame.FalloutNewVegas,
            ContainerEndianness = "big"
        };
    }

    private static string? ResolvePlayerRef(uint formId)
    {
        return formId == 0x00000014 ? "PlayerRef" : null;
    }

    private static JsonDocument ReadManifest(string directory)
    {
        return JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "scripts.manifest.json")));
    }

    private static void AssertFile(
        JsonElement script,
        string path,
        string content,
        string provenance,
        string correspondence)
    {
        var file = Assert.Single(script.GetProperty("files").EnumerateArray());
        Assert.Equal(path, file.GetProperty("path").GetString());
        Assert.Equal(content, file.GetProperty("content").GetString());
        Assert.Equal(provenance, file.GetProperty("provenance").GetString());
        Assert.Equal(correspondence, file.GetProperty("correspondence").GetString());
    }
}
