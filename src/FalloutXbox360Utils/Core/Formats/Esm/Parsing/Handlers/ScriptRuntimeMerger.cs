using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Merges runtime Script struct data from memory dumps into parsed script records.
///     Handles enriching existing ESM scripts with runtime data (source text, compiled bytecode,
///     variables) and creating new scripts from runtime-only entries.
/// </summary>
internal static class ScriptRuntimeMerger
{
    /// <summary>
    ///     Merge runtime Script struct data into existing scripts or create new entries.
    ///     Scripts found via runtime hash table walk (FormType 0x11) may have source text
    ///     and compiled bytecode that ESM fragments don't contain (game discards ESM records at load time).
    ///     Decompilation is deferred to pass 2.
    /// </summary>
    internal static void MergeRuntimeScriptData(
        RecordParserContext context,
        List<ScriptRecord> scripts)
    {
        var scriptsByFormId = scripts
            .GroupBy(s => s.FormId)
            .ToDictionary(g => g.Key, g => g.First());
        var runtimeEntries = context.ScanResult.RuntimeEditorIds
            .Where(e => e.FormType == 0x11 && e.TesFormOffset != null)
            .ToList();

        var runtimeCount = 0;
        var enrichedCount = 0;

        foreach (var entry in runtimeEntries)
        {
            var runtimeData = context.RuntimeReader!.ReadRuntimeScript(entry);
            if (runtimeData == null)
            {
                continue;
            }

            if (scriptsByFormId.TryGetValue(runtimeData.FormId, out var existing))
            {
                // Enrich existing ESM script with runtime data
                var enriched = EnrichScriptWithRuntimeData(existing, runtimeData);
                if (enriched != existing)
                {
                    var idx = scripts.IndexOf(existing);
                    scripts[idx] = enriched;
                    scriptsByFormId[enriched.FormId] = enriched;
                    enrichedCount++;
                }
            }
            else
            {
                // Create new script from runtime data only
                var newScript = CreateScriptFromRuntimeData(runtimeData);
                scripts.Add(newScript);
                scriptsByFormId[newScript.FormId] = newScript;
                runtimeCount++;
            }
        }

        if (runtimeCount > 0 || enrichedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Scripts: {runtimeCount} from runtime structs, {enrichedCount} enriched with runtime data");
        }
    }

    internal static ScriptRecord EnrichScriptWithRuntimeData(
        ScriptRecord existing, RuntimeScriptData runtime)
    {
        var needsUpdate = false;
        var sourceText = existing.SourceText;
        var compiledData = existing.CompiledData;
        var variables = existing.Variables;
        var referencedObjects = existing.ReferencedObjects;

        // Prefer runtime source text (may have comments preserved in debug builds)
        if (string.IsNullOrEmpty(sourceText) && !string.IsNullOrEmpty(runtime.SourceText))
        {
            sourceText = runtime.SourceText;
            needsUpdate = true;
        }

        // Add compiled data if ESM didn't have it
        if (compiledData == null && runtime.CompiledData is { Length: > 0 })
        {
            compiledData = runtime.CompiledData;
            needsUpdate = true;
        }

        // Add variables from runtime if ESM had none
        if (variables.Count == 0 && runtime.Variables.Count > 0)
        {
            variables = runtime.Variables;
            needsUpdate = true;
        }

        // Add referenced objects from runtime if ESM had none
        if (referencedObjects.Count == 0 && runtime.ReferencedObjects.Count > 0)
        {
            referencedObjects = runtime.ReferencedObjects.Select(r => r.FormId).ToList();
            needsUpdate = true;
        }

        if (!needsUpdate && runtime.OwnerQuestFormId == null)
        {
            return existing;
        }

        // Decompilation is deferred to pass 2 in ParseScripts()
        return existing with
        {
            EditorId = !string.IsNullOrEmpty(existing.EditorId) ? existing.EditorId : runtime.EditorId,
            VariableCount = existing.VariableCount > 0 ? existing.VariableCount : runtime.VariableCount,
            RefObjectCount = existing.RefObjectCount > 0 ? existing.RefObjectCount : runtime.RefObjectCount,
            CompiledSize = existing.CompiledSize > 0 ? existing.CompiledSize : runtime.DataSize,
            LastVariableId = existing.LastVariableId > 0 ? existing.LastVariableId : runtime.LastVariableId,
            IsQuestScript = existing.IsQuestScript || runtime.IsQuestScript,
            IsMagicEffectScript = existing.IsMagicEffectScript || runtime.IsMagicEffectScript,
            IsCompiled = existing.IsCompiled || runtime.IsCompiled,
            SourceText = sourceText,
            CompiledData = compiledData,
            Variables = variables,
            ReferencedObjects = referencedObjects,
            OwnerQuestFormId = runtime.OwnerQuestFormId ?? existing.OwnerQuestFormId,
            QuestScriptDelay = runtime.QuestScriptDelay,
            FromRuntime = true
        };
    }

    internal static ScriptRecord CreateScriptFromRuntimeData(RuntimeScriptData runtime)
    {
        var variables = runtime.Variables;
        var referencedObjects = runtime.ReferencedObjects.Select(r => r.FormId).ToList();

        // Decompilation is deferred to pass 2 in ParseScripts()
        return new ScriptRecord
        {
            FormId = runtime.FormId,
            EditorId = runtime.EditorId,
            VariableCount = runtime.VariableCount,
            RefObjectCount = runtime.RefObjectCount,
            CompiledSize = runtime.DataSize,
            LastVariableId = runtime.LastVariableId,
            IsQuestScript = runtime.IsQuestScript,
            IsMagicEffectScript = runtime.IsMagicEffectScript,
            IsCompiled = runtime.IsCompiled,
            SourceText = runtime.SourceText,
            CompiledData = runtime.CompiledData,
            Variables = variables,
            ReferencedObjects = referencedObjects,
            OwnerQuestFormId = runtime.OwnerQuestFormId,
            QuestScriptDelay = runtime.QuestScriptDelay,
            Offset = runtime.DumpOffset,
            IsBigEndian = true,
            FromRuntime = true
        };
    }

    /// <summary>
    ///     Build object-to-script (SCRI) mappings by scanning ESM records.
    ///     SCRI subrecords on NPC_/CREA/ACTI/etc. link objects to their scripts.
    ///     Also builds ref-to-base-to-script chains from placed references.
    /// </summary>
    internal static void BuildObjectToScriptMap(
        RecordParserContext context,
        Dictionary<uint, uint> objectToScript)
    {
        // Record types that can have SCRI subrecords (objects with attached scripts)
        HashSet<string> scriTypes =
        [
            "NPC_", "CREA", "ACTI", "CONT", "DOOR", "FURN", "WEAP", "ARMO", "MISC",
            "BOOK", "ALCH", "KEYM", "AMMO", "LIGH", "LVLC", "LVLN", "FACT", "QUST"
        ];

        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            foreach (var record in context.ScanResult.MainRecords)
            {
                if (!scriTypes.Contains(record.RecordType))
                {
                    continue;
                }

                TryExtractScriFormId(context, record, buffer, objectToScript);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Build ref-to-base-to-script chains: for each ref that has a base with a script,
        // add the ref to script's variables mapping
        foreach (var (refFormId, baseFormId) in context.RefToBase)
        {
            if (objectToScript.TryGetValue(baseFormId, out var scriptFormId))
            {
                objectToScript.TryAdd(refFormId, scriptFormId);
            }
        }
    }

    private static void TryExtractScriFormId(
        RecordParserContext context,
        DetectedMainRecord record,
        byte[] buffer,
        Dictionary<uint, uint> objectToScript)
    {
        var recordData = context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return;
        }

        var (data, dataSize) = recordData.Value;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            if (sub.Signature == "SCRI" && sub.DataLength >= 4)
            {
                var scriptFormId = record.IsBigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(sub.DataOffset, 4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sub.DataOffset, 4));
                objectToScript.TryAdd(record.FormId, scriptFormId);
                break; // Only one SCRI per record
            }
        }
    }
}
