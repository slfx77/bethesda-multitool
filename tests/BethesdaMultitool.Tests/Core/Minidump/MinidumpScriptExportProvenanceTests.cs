using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Export.Scripts;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Minidump;

/// <summary>
///     GUI Batch mode merges load-order records under a dump's parse for name enrichment, and
///     <see cref="RecordCollection.MergeWith" /> keeps every base record the dump lacks. The per-script export
///     labels every file as memory-dump content, so it must receive the dump's OWN scripts, captured before
///     the merge — never a retail-fallback script from the load order.
/// </summary>
public sealed class MinidumpScriptExportProvenanceTests : IDisposable
{
    private const uint LoadOrderOnlyFormId = 0x000A0001;
    private const uint DumpFormId = 0x000B0002;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bmt_dump_script_export_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public async Task Merged_load_order_scripts_are_not_exported_as_dump_output()
    {
        var loadOrderOnly = new ScriptRecord
        {
            FormId = LoadOrderOnlyFormId,
            EditorId = "LoadOrderOnlyScript",
            SourceText = "scn LoadOrderOnlyScript\r\nBegin GameMode\r\nEnd"
        };
        var dumpScript = new ScriptRecord
        {
            FormId = DumpFormId,
            EditorId = "DumpScript",
            SourceText = "scn DumpScript\r\nBegin GameMode\r\nEnd",
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
            FromRuntime = true
        };
        var dump = new RecordCollection { Scripts = [dumpScript], Game = BethesdaGame.FalloutNewVegas };
        var loadOrder = new RecordCollection { Scripts = [loadOrderOnly], Game = BethesdaGame.FalloutNewVegas };

        var (records, dumpScripts) = MinidumpExtractionReporter.SelectDumpScripts(dump, loadOrder);

        // Reports and name enrichment still see the merged set...
        Assert.Equal([LoadOrderOnlyFormId, DumpFormId], records.Scripts.Select(s => s.FormId).Order());
        // ...but only the dump's own script is selected for export.
        Assert.Equal(DumpFormId, Assert.Single(dumpScripts).FormId);

        var source = new ScriptExportSource
        {
            Path = "/synthetic/Fallout_Release_Beta.xex21.dmp",
            FileName = "Fallout_Release_Beta.xex21.dmp",
            Kind = ScriptExportSourceKind.MemoryDump,
            Game = BethesdaGame.FalloutNewVegas
        };
        var summary = await EsmRecordExporter.ExportParsedScriptsAsync(dumpScripts, null, _root, source);

        var scriptsDirectory = Path.Combine(_root, "scripts");
        Assert.NotNull(summary);
        Assert.Equal(1, summary.ScriptCount);
        Assert.True(File.Exists(Path.Combine(scriptsDirectory, "DumpScript.gek")));
        Assert.Empty(Directory.GetFiles(scriptsDirectory, "LoadOrderOnlyScript*"));

        using var manifest = JsonDocument.Parse(
            await File.ReadAllBytesAsync(
                Path.Combine(scriptsDirectory, "scripts.manifest.json"),
                TestContext.Current.CancellationToken));
        Assert.Equal("memory-dump", manifest.RootElement.GetProperty("source").GetProperty("kind").GetString());
        var entry = Assert.Single(manifest.RootElement.GetProperty("scripts").EnumerateArray());
        Assert.Equal("0x000B0002", entry.GetProperty("formId").GetString());
        var file = Assert.Single(entry.GetProperty("files").EnumerateArray());
        Assert.Equal("runtime-same-object", file.GetProperty("provenance").GetString());
        Assert.Equal("accepted", file.GetProperty("correspondence").GetString());
    }

    [Fact]
    public void Without_load_order_records_the_dump_collection_is_used_as_is()
    {
        var dumpScript = new ScriptRecord { FormId = DumpFormId, EditorId = "DumpScript", SourceText = "scn DumpScript" };
        var dump = new RecordCollection { Scripts = [dumpScript] };

        var (records, dumpScripts) = MinidumpExtractionReporter.SelectDumpScripts(dump, null);

        Assert.Same(dump, records);
        Assert.Same(dumpScript, Assert.Single(dumpScripts));
    }
}
