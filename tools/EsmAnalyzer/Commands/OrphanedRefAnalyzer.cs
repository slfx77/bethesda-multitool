using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Core analysis logic for finding orphaned FormID references in ESM files
///     and memory dumps. Extracts scripts, scans records, and cross-references
///     FormIDs against the known universe.
/// </summary>
internal static class OrphanedRefAnalyzer
{
    // ═══════════════════════════════════════════════════════════════════════
    // ESM Script Extraction
    // ═══════════════════════════════════════════════════════════════════════

    public static List<ParsedScript> ExtractScriptsFromEsm(
        List<AnalyzerRecordInfo> records, byte[] data, bool bigEndian)
    {
        var scripts = new List<ParsedScript>();

        foreach (var record in records.Where(r => r.Signature == "SCPT"))
        {
            var recordDataStart = (int)record.Offset + EsmParser.MainRecordHeaderSize;
            var recordDataEnd = recordDataStart + (int)record.DataSize;
            if (recordDataEnd > data.Length)
            {
                continue;
            }

            var recordData = data.AsSpan(recordDataStart, (int)record.DataSize).ToArray();

            // Handle compressed records
            if (record.IsCompressed && record.DataSize >= 4)
            {
                var decompressedSize = BinaryUtils.ReadUInt32(recordData, 0, bigEndian);
                if (decompressedSize > 0 && decompressedSize < 100_000_000)
                {
                    try
                    {
                        recordData = EsmHelpers.DecompressZlib(recordData[4..], (int)decompressedSize);
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

            string? editorId = null;
            var referencedObjects = new List<uint>();
            var variables = new List<ScriptVariableInfo>();
            byte[]? compiledData = null;
            uint pendingSlsdIndex = 0;
            byte pendingSlsdType = 0;
            bool havePendingSlsd = false;

            foreach (var sub in subrecords)
            {
                switch (sub.Signature)
                {
                    case "EDID":
                        editorId = EsmRecordParser.GetSubrecordString(sub);
                        break;
                    case "SCDA":
                        compiledData = sub.Data;
                        break;
                    case "SCRO":
                        if (sub.Data.Length >= 4)
                        {
                            referencedObjects.Add(BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian));
                        }
                        break;
                    case "SCRV":
                        // Local variable ref — store with high bit marker
                        if (sub.Data.Length >= 4)
                        {
                            referencedObjects.Add(BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian) | 0x80000000);
                        }
                        break;
                    case "SLSD":
                        if (sub.Data.Length >= 16)
                        {
                            pendingSlsdIndex = BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                            // Type byte at offset 12
                            pendingSlsdType = sub.Data.Length > 12 ? sub.Data[12] : (byte)0;
                            havePendingSlsd = true;
                        }
                        break;
                    case "SCVR":
                        if (havePendingSlsd)
                        {
                            var varName = EsmRecordParser.GetSubrecordString(sub);
                            variables.Add(new ScriptVariableInfo(pendingSlsdIndex, varName, pendingSlsdType));
                            havePendingSlsd = false;
                        }
                        break;
                }
            }

            scripts.Add(new ParsedScript
            {
                FormId = record.FormId,
                EditorId = editorId,
                ReferencedObjects = referencedObjects,
                Variables = variables,
                CompiledData = compiledData,
                IsBigEndian = bigEndian
            });
        }

        return scripts;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dump Loading
    // ═══════════════════════════════════════════════════════════════════════

    public static async Task LoadDumpScriptsAsync(
        string dumpPath,
        HashSet<uint> knownFormIds,
        Dictionary<uint, string> edidMap,
        List<(string Source, ScriptRecord Script)> dumpScripts)
    {
        var dumpFiles = new List<string>();

        if (Directory.Exists(dumpPath))
        {
            dumpFiles.AddRange(Directory.GetFiles(dumpPath, "*.dmp"));
            AnsiConsole.MarkupLine($"[grey]Found {dumpFiles.Count} dump files in directory[/]");
        }
        else if (File.Exists(dumpPath))
        {
            dumpFiles.Add(dumpPath);
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]WARN: Dump path not found: {dumpPath}[/]");
            return;
        }

        foreach (var dumpFile in dumpFiles)
        {
            var fileName = Path.GetFileName(dumpFile);
            AnsiConsole.MarkupLine($"[grey]Loading dump: {fileName}...[/]");

            try
            {
                var analyzer = new FalloutXbox360Utils.Core.Minidump.MinidumpAnalyzer();
                var analysisResult = await analyzer.AnalyzeAsync(dumpFile, includeMetadata: true);

                if (analysisResult.EsmRecords == null)
                {
                    AnsiConsole.MarkupLine($"[yellow]  No ESM records in {fileName}[/]");
                    continue;
                }

                // Merge FormIDs from dump's FormIdMap
                if (analysisResult.FormIdMap != null)
                {
                    foreach (var (formId, name) in analysisResult.FormIdMap)
                    {
                        knownFormIds.Add(formId);
                        edidMap.TryAdd(formId, name);
                    }
                }

                var fileInfo = new FileInfo(dumpFile);
                using var mmf = MemoryMappedFile.CreateFromFile(
                    dumpFile, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

                var reconstructor = new FalloutXbox360Utils.Core.Formats.Esm.Parsing.RecordParser(
                    analysisResult.EsmRecords,
                    analysisResult.FormIdMap,
                    accessor,
                    fileInfo.Length,
                    analysisResult.MinidumpInfo);

                var collection = reconstructor.ParseAll();

                // Merge dump FormIDs into known set
                foreach (var (formId, name) in collection.FormIdToEditorId)
                {
                    knownFormIds.Add(formId);
                    edidMap.TryAdd(formId, name);
                }

                foreach (var script in collection.Scripts)
                {
                    dumpScripts.Add((fileName, script));
                }

                AnsiConsole.MarkupLine(
                    $"[grey]  {fileName}: {collection.Scripts.Count} scripts, {collection.FormIdToEditorId.Count} FormIDs[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]  Error loading {fileName}: {ex.Message}[/]");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // All-Records Scanning
    // ═══════════════════════════════════════════════════════════════════════

    public static void ScanAllRecordsForOrphans(
        List<AnalyzerRecordInfo> records, byte[] data, bool bigEndian,
        HashSet<uint> knownFormIds, Dictionary<uint, string> edidMap,
        List<AllRecordOrphanedReference> orphans, OrphanStats stats)
    {
        foreach (var record in records)
        {
            if (record.Signature is "GRUP" or "TES4" or "SCPT")
            {
                continue; // SCPT already handled by script scanning
            }

            var recordDataStart = (int)record.Offset + EsmParser.MainRecordHeaderSize;
            var recordDataEnd = recordDataStart + (int)record.DataSize;
            if (recordDataEnd > data.Length)
            {
                continue;
            }

            var recordData = data.AsSpan(recordDataStart, (int)record.DataSize).ToArray();

            if (record.IsCompressed && record.DataSize >= 4)
            {
                var decompressedSize = BinaryUtils.ReadUInt32(recordData, 0, bigEndian);
                if (decompressedSize > 0 && decompressedSize < 100_000_000)
                {
                    try
                    {
                        recordData = EsmHelpers.DecompressZlib(recordData[4..], (int)decompressedSize);
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

            foreach (var sub in subrecords)
            {
                var schema = SubrecordSchemaRegistry.GetSchema(sub.Signature, record.Signature, sub.Data.Length);
                if (schema == null)
                {
                    continue;
                }

                var offset = 0;
                foreach (var field in schema.Fields)
                {
                    if (offset >= sub.Data.Length)
                    {
                        break;
                    }

                    var fieldSize = field.EffectiveSize;
                    if (fieldSize <= 0)
                    {
                        fieldSize = sub.Data.Length - offset;
                    }

                    if (field.Type is SubrecordFieldType.FormId or SubrecordFieldType.FormIdLittleEndian)
                    {
                        if (offset + 4 <= sub.Data.Length)
                        {
                            stats.AllRecordFormIdFieldsChecked++;

                            var formId = field.Type == SubrecordFieldType.FormIdLittleEndian
                                ? BitConverter.ToUInt32(sub.Data, offset)
                                : BinaryUtils.ReadUInt32(sub.Data, offset, bigEndian);

                            if (formId != 0 && (formId >> 24) == 0 && !knownFormIds.Contains(formId))
                            {
                                orphans.Add(new AllRecordOrphanedReference
                                {
                                    RecordType = record.Signature,
                                    RecordFormId = record.FormId,
                                    SubrecordType = sub.Signature,
                                    FieldName = field.Name,
                                    OrphanedFormId = formId
                                });
                            }
                        }
                    }

                    offset += fieldSize;
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Decompilation for Context
    // ═══════════════════════════════════════════════════════════════════════

    public static void DecompileContextForOrphans(
        List<ParsedScript> scripts,
        HashSet<uint> orphanedFormIds,
        Dictionary<uint, string> edidMap,
        bool bigEndian,
        List<OrphanedReference> orphans)
    {
        // Build set of script FormIDs that have orphans
        var scriptsWithOrphans = new HashSet<uint>(
            orphans.Where(o => o.Source == "ESM").Select(o => o.ScriptFormId));

        foreach (var script in scripts)
        {
            if (!scriptsWithOrphans.Contains(script.FormId))
            {
                continue;
            }

            if (script.CompiledData is not { Length: > 0 })
            {
                continue;
            }

            // Build resolve callback that marks orphans
            string? ResolveFormName(uint formId)
            {
                if (orphanedFormIds.Contains(formId))
                {
                    return $"__ORPHAN_0x{formId:X8}";
                }

                return edidMap.GetValueOrDefault(formId);
            }

            try
            {
                var decompiler = new ScriptDecompiler(
                    script.Variables,
                    script.ReferencedObjects,
                    ResolveFormName,
                    bigEndian,
                    script.EditorId);

                var decompiled = decompiler.Decompile(script.CompiledData);

                // Find context lines for each orphan in this script
                foreach (var orphan in orphans.Where(o =>
                             o.Source == "ESM" && o.ScriptFormId == script.FormId))
                {
                    orphan.DecompiledContext = FindOrphanInText(
                        decompiled, orphan.OrphanedFormId);
                }
            }
            catch
            {
                // Decompilation failure — leave context as null
            }
        }
    }

    public static string? FindOrphanInText(string text, uint orphanedFormId)
    {
        var marker = $"__ORPHAN_0x{orphanedFormId:X8}";
        var hexMarker = $"0x{orphanedFormId:X8}";

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains(hexMarker, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Length > 120 ? trimmed[..117] + "..." : trimmed;
            }
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Data Structures
    // ═══════════════════════════════════════════════════════════════════════

    internal sealed class ParsedScript
    {
        public uint FormId { get; init; }
        public string? EditorId { get; init; }
        public List<uint> ReferencedObjects { get; init; } = [];
        public List<ScriptVariableInfo> Variables { get; init; } = [];
        public byte[]? CompiledData { get; init; }
        public bool IsBigEndian { get; init; }
    }

    internal sealed class OrphanedReference
    {
        public required string Source { get; init; }
        public required string ScriptEditorId { get; init; }
        public uint ScriptFormId { get; init; }
        public uint OrphanedFormId { get; init; }
        public bool IsExternalPlugin { get; init; }
        public byte PluginIndex { get; init; }
        public string? DecompiledContext { get; set; }
        public bool ExistsInCompareFile { get; set; }
        public string? CompareEdid { get; set; }
        public string? CompareRecordType { get; set; }
    }

    internal sealed class AllRecordOrphanedReference
    {
        public required string RecordType { get; init; }
        public uint RecordFormId { get; init; }
        public required string SubrecordType { get; init; }
        public required string FieldName { get; init; }
        public uint OrphanedFormId { get; init; }
    }

    internal sealed class OrphanStats
    {
        public int ScriptsScanned { get; set; }
        public int DumpScriptsScanned { get; set; }
        public int TotalScroRefs { get; set; }
        public int OrphanedRefs { get; set; }
        public int ExternalRefs { get; set; }
        public int UniqueOrphanedFormIds { get; set; }
        public int ExistInCompareFile { get; set; }
        public int CompareFormIdCount { get; set; }
        public int AllRecordFormIdFieldsChecked { get; set; }
    }
}
