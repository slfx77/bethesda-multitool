using System.Buffers;
using System.Buffers.Binary;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

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
    internal static List<RuntimeScriptData> MergeRuntimeScriptData(
        RecordParserContext context,
        List<ScriptRecord> scripts)
    {
        var capturedRuntimeScripts = new List<RuntimeScriptData>();
        var scriptsByFormId = scripts
            .GroupBy(s => s.FormId)
            .ToDictionary(g => g.Key, g => g.First());
        var runtimeEntries = context.ScanResult.RuntimeEditorIds
            .Where(e => e.FormType == 0x11 && e.TesFormOffset != null)
            .ToList();

        var runtimeCount = 0;
        var enrichedCount = 0;
        var runtimeDataByFormId = new Dictionary<uint, List<RuntimeScriptData>>();

        foreach (var entry in runtimeEntries)
        {
            var runtimeData = context.RuntimeReader!.ReadRuntimeScript(entry);
            if (runtimeData == null)
            {
                continue;
            }

            capturedRuntimeScripts.Add(runtimeData);
            if (!runtimeDataByFormId.TryGetValue(runtimeData.FormId, out var copies))
            {
                copies = [];
                runtimeDataByFormId.Add(runtimeData.FormId, copies);
            }

            copies.Add(runtimeData);
        }

        foreach (var (formId, copies) in runtimeDataByFormId)
        {
            var runtimeData = SelectConsistentRuntimeData(copies);
            if (runtimeData is null)
            {
                Logger.Instance.Warn(
                    $"  [Semantic] SCPT 0x{formId:X8}: conflicting runtime Script objects; " +
                    "left the fragment/master emission candidate unchanged.");
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
                if (newScript == null)
                {
                    continue;
                }

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

        return capturedRuntimeScripts;
    }

    internal static RuntimeScriptData? SelectConsistentRuntimeData(
        IReadOnlyList<RuntimeScriptData> copies)
    {
        ArgumentNullException.ThrowIfNull(copies);
        if (copies.Count == 0)
        {
            throw new ArgumentException("At least one runtime Script object is required.", nameof(copies));
        }

        var first = copies[0];
        return copies.Skip(1).All(copy => RuntimeDataEquivalent(first, copy))
            ? first
            : null;
    }

    /// <summary>
    ///     Carries the standalone SCPT SCTX decision back to the raw RuntimeScripts ledger.
    ///     The ledger intentionally retains rejected text for diagnostics, so downstream
    ///     declaration consumers must use this explicit status rather than SourceText alone.
    /// </summary>
    internal static List<RuntimeScriptData> ApplySourceCorrespondenceStatuses(
        IReadOnlyList<RuntimeScriptData> runtimeScripts,
        IReadOnlyList<ScriptRecord> standaloneScripts)
    {
        ArgumentNullException.ThrowIfNull(runtimeScripts);
        ArgumentNullException.ThrowIfNull(standaloneScripts);

        var scriptsByFormId = standaloneScripts
            .Where(static script => script.FormId != 0)
            .GroupBy(static script => script.FormId)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var consistencyByFormId = runtimeScripts
            .GroupBy(static runtime => runtime.FormId)
            .ToDictionary(
                static group => group.Key,
                static group => SelectConsistentRuntimeData(group.ToArray()) is not null);
        return runtimeScripts.Select(runtime => runtime with
        {
            SourceTextCorrespondenceStatus = consistencyByFormId[runtime.FormId]
                ? ResolveSourceCorrespondenceStatus(
                    runtime,
                    scriptsByFormId.GetValueOrDefault(runtime.FormId))
                : ScriptSourceCorrespondenceStatus.Rejected
        }).ToList();
    }

    private static ScriptSourceCorrespondenceStatus ResolveSourceCorrespondenceStatus(
        RuntimeScriptData runtime,
        IReadOnlyList<ScriptRecord>? standaloneScripts)
    {
        if (string.IsNullOrEmpty(runtime.SourceText))
        {
            return ScriptSourceCorrespondenceStatus.Unverified;
        }

        var correspondingScript = standaloneScripts?.FirstOrDefault(script =>
            script.SourceTextOrigin == ScriptSourceTextOrigin.RuntimeSameObject
            && string.Equals(script.SourceText, runtime.SourceText, StringComparison.Ordinal));
        if (correspondingScript is null)
        {
            return ScriptSourceCorrespondenceStatus.Rejected;
        }

        return correspondingScript.SourceTextCorrespondenceStatus switch
        {
            ScriptSourceCorrespondenceStatus.Accepted => ScriptSourceCorrespondenceStatus.Accepted,
            ScriptSourceCorrespondenceStatus.AcceptedSourceOnly =>
                ScriptSourceCorrespondenceStatus.AcceptedSourceOnly,
            ScriptSourceCorrespondenceStatus.Rejected => ScriptSourceCorrespondenceStatus.Rejected,
            _ => ScriptSourceCorrespondenceStatus.Unverified
        };
    }

    internal static bool RuntimeDataEquivalent(RuntimeScriptData left, RuntimeScriptData right)
    {
        return left.FormId == right.FormId
               && string.Equals(left.EditorId, right.EditorId, StringComparison.Ordinal)
               && left.HeaderVariableCount == right.HeaderVariableCount
               && left.VariableCount == right.VariableCount
               && left.RefObjectCount == right.RefObjectCount
               && left.DataSize == right.DataSize
               && left.LastVariableId == right.LastVariableId
               && left.IsQuestScript == right.IsQuestScript
               && left.IsMagicEffectScript == right.IsMagicEffectScript
               && left.IsCompiled == right.IsCompiled
               && left.SourceTextCorrespondenceStatus == right.SourceTextCorrespondenceStatus
               && string.Equals(left.SourceText, right.SourceText, StringComparison.Ordinal)
               && ByteArraysEqual(left.CompiledData, right.CompiledData)
               && left.OwnerQuestFormId == right.OwnerQuestFormId
               && left.QuestScriptDelay.Equals(right.QuestScriptDelay)
               && left.ReferencedObjectsComplete == right.ReferencedObjectsComplete
               && left.VariablesComplete == right.VariablesComplete
               && left.VariableMetadataComplete == right.VariableMetadataComplete
               && left.ReferencedObjects.SequenceEqual(right.ReferencedObjects)
               && left.Variables.SequenceEqual(right.Variables);
    }

    private static bool ByteArraysEqual(byte[]? left, byte[]? right)
    {
        return ReferenceEquals(left, right)
               || (left is not null && right is not null && left.AsSpan().SequenceEqual(right));
    }

    internal static ScriptRecord EnrichScriptWithRuntimeData(
        ScriptRecord existing, RuntimeScriptData runtime)
    {
        var needsUpdate = false;
        var sourceText = existing.SourceText;
        var sourceTextOrigin = existing.SourceTextOrigin;
        var compiledData = existing.CompiledData;
        var compiledSize = existing.CompiledSize;
        var variables = existing.Variables;
        var referencedObjects = existing.ReferencedObjects;
        var variableCount = existing.VariableCount;
        var lastVariableId = existing.LastVariableId;
        var refObjectCount = existing.RefObjectCount;
        var compiledDataIsBigEndian = existing.IsBigEndian;

        // SCDA's 1-based reference slots and local-variable operands make bytecode, SCRO/SCRV,
        // and SLSD one atomic unit. A nonempty prefix from a broken list walk must never be
        // combined with otherwise complete fragment bytecode (or vice versa).
        var runtimeCompiledData = runtime.CompiledData is { Length: > 0 } candidate
                                  && (uint)candidate.Length == runtime.DataSize
            ? candidate
            : null;
        var runtimeVariableTableComplete = runtime.VariablesComplete
                                           && runtime.Variables.Count == runtime.VariableCount;
        var runtimeReferenceTableComplete = runtime.ReferencedObjectsComplete
                                            && runtime.ReferencedObjects.Count == runtime.RefObjectCount;
        var runtimeReferencedObjects = runtime.ReferencedObjects
            .Select(static value => value.FormId)
            .ToList();
        var runtimeLocalBindingsComplete = runtimeCompiledData == null
                                           || ScriptBytecodeAnalyzer.HasCompleteLocalVariableBindings(
                                               runtimeCompiledData,
                                               true,
                                               runtime.Variables,
                                               runtimeReferencedObjects);
        var runtimeBundleComplete = runtimeCompiledData != null
                                    && runtimeVariableTableComplete
                                    && runtimeReferenceTableComplete
                                    && runtimeLocalBindingsComplete;
        var existingBytecodeMatchesRuntime = runtimeCompiledData != null
                                             && existing.CompiledData is { Length: > 0 } originalCompiledData
                                             && existing.IsBigEndian
                                             && originalCompiledData.AsSpan().SequenceEqual(runtimeCompiledData);
        var adoptedRuntimeBundle = false;
        var executableBundleChanged = false;
        if (runtimeBundleComplete)
        {
            adoptedRuntimeBundle = true;
            var acceptedRuntimeCompiledData = runtimeCompiledData!;
            var runtimeVariables = PreserveCapturedVariableNames(runtime.Variables, variables);

            if (compiledData == null || !compiledData.AsSpan().SequenceEqual(acceptedRuntimeCompiledData))
            {
                compiledData = acceptedRuntimeCompiledData;
                needsUpdate = true;
                executableBundleChanged = true;
            }

            var runtimeCompiledSize = (uint)acceptedRuntimeCompiledData.Length;
            if (compiledSize != runtimeCompiledSize)
            {
                compiledSize = runtimeCompiledSize;
                needsUpdate = true;
            }

            if (!variables.SequenceEqual(runtimeVariables))
            {
                variables = runtimeVariables;
                needsUpdate = true;
                executableBundleChanged = true;
            }

            if (variableCount != runtime.VariableCount || lastVariableId != runtime.LastVariableId)
            {
                variableCount = runtime.VariableCount;
                lastVariableId = runtime.LastVariableId;
                needsUpdate = true;
                executableBundleChanged = true;
            }

            if (!referencedObjects.SequenceEqual(runtimeReferencedObjects))
            {
                referencedObjects = runtimeReferencedObjects;
                needsUpdate = true;
                executableBundleChanged = true;
            }

            if (refObjectCount != runtime.RefObjectCount)
            {
                refObjectCount = runtime.RefObjectCount;
                needsUpdate = true;
                executableBundleChanged = true;
            }

            if (!compiledDataIsBigEndian)
            {
                compiledDataIsBigEndian = true;
                needsUpdate = true;
                executableBundleChanged = true;
            }
        }

        // RuntimeScriptReader returns source only after observing its terminating NUL. Tie a
        // replacement source to the same accepted runtime bundle. Byte-identical SCDA alone
        // is insufficient: local/reference operands derive their meaning from the ordered
        // SLSD/SCRO/SCRV tables, so an incomplete runtime table cannot prove SCTX identity.
        var runtimeBytecodeMatchesExisting = existingBytecodeMatchesRuntime;
        var sourceCanStandAlone = existing.CompiledData is not { Length: > 0 }
                                  && !existing.IsCompiled
                                  && IsProvenUncompiledSourceOnlyStub(runtime);
        if (!string.IsNullOrEmpty(runtime.SourceText)
            && (adoptedRuntimeBundle || sourceCanStandAlone)
            && (!string.Equals(sourceText, runtime.SourceText, StringComparison.Ordinal)
                || sourceTextOrigin != ScriptSourceTextOrigin.RuntimeSameObject))
        {
            sourceText = runtime.SourceText;
            sourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject;
            needsUpdate = true;
        }
        else if (adoptedRuntimeBundle
                 && executableBundleChanged
                 && string.IsNullOrEmpty(runtime.SourceText)
                 && !string.IsNullOrEmpty(sourceText))
        {
            // The executable bundle changed and the same runtime Script object supplied no
            // terminated source for it. Keeping an older fragment's SCTX would falsely claim
            // that text describes the emitted SCDA, so omit SCTX instead of guessing.
            sourceText = null;
            sourceTextOrigin = ScriptSourceTextOrigin.None;
            needsUpdate = true;
        }

        if (runtimeBytecodeMatchesExisting
            && compiledSize != (uint)existing.CompiledData!.Length)
        {
            compiledSize = (uint)existing.CompiledData.Length;
            needsUpdate = true;
        }

        var runtimeEditorId = ScriptRecordEmissionPolicy.ResolveEditorId(
            runtime.EditorId,
            sourceTextOrigin == ScriptSourceTextOrigin.RuntimeSameObject
                ? sourceText
                : null);
        var editorId = !string.IsNullOrEmpty(existing.EditorId)
            ? existing.EditorId
            : runtimeEditorId;
        var ownerQuestFormId = runtime.OwnerQuestFormId ?? existing.OwnerQuestFormId;
        var questScriptDelay = runtime.OwnerQuestFormId.HasValue
            ? runtime.QuestScriptDelay
            : existing.QuestScriptDelay;
        var isQuestScript = existing.IsQuestScript || runtime.IsQuestScript;
        var isMagicEffectScript = existing.IsMagicEffectScript || runtime.IsMagicEffectScript;
        var isCompiled = existing.IsCompiled || (adoptedRuntimeBundle && runtime.IsCompiled);
        needsUpdate |= !string.Equals(editorId, existing.EditorId, StringComparison.Ordinal)
                       || ownerQuestFormId != existing.OwnerQuestFormId
                       || !questScriptDelay.Equals(existing.QuestScriptDelay)
                       || isQuestScript != existing.IsQuestScript
                       || isMagicEffectScript != existing.IsMagicEffectScript
                       || isCompiled != existing.IsCompiled;

        if (!needsUpdate)
        {
            return existing;
        }

        // Decompilation is deferred to pass 2 in ParseScripts()
        return existing with
        {
            EditorId = editorId,
            VariableCount = variableCount,
            RefObjectCount = refObjectCount,
            CompiledSize = compiledSize,
            LastVariableId = lastVariableId,
            IsQuestScript = isQuestScript,
            IsMagicEffectScript = isMagicEffectScript,
            IsCompiled = isCompiled,
            SourceText = sourceText,
            SourceTextOrigin = sourceTextOrigin,
            CompiledData = compiledData,
            ExecutableBundleFromRuntime = existing.ExecutableBundleFromRuntime || adoptedRuntimeBundle,
            HasMalformedSerializedHeader = !adoptedRuntimeBundle && existing.HasMalformedSerializedHeader,
            HasMalformedSerializedTable = !adoptedRuntimeBundle && existing.HasMalformedSerializedTable,
            IsIncompleteExecutableBundle = !adoptedRuntimeBundle && existing.IsIncompleteExecutableBundle,
            Variables = variables,
            ReferencedObjects = referencedObjects,
            OwnerQuestFormId = ownerQuestFormId,
            QuestScriptDelay = questScriptDelay,
            IsBigEndian = compiledDataIsBigEndian,
            FromRuntime = true
        };
    }

    private static List<ScriptVariableInfo> PreserveCapturedVariableNames(
        IReadOnlyList<ScriptVariableInfo> runtimeVariables,
        IReadOnlyList<ScriptVariableInfo> existingVariables)
    {
        var existingNames = existingVariables
            .Where(static variable => !string.IsNullOrEmpty(variable.Name))
            .GroupBy(static variable => (variable.Index, variable.Type))
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Name);

        return runtimeVariables
            .Select(variable => string.IsNullOrEmpty(variable.Name)
                                && existingNames.TryGetValue((variable.Index, variable.Type), out var name)
                ? variable with { Name = name }
                : variable)
            .ToList();
    }

    internal static ScriptRecord? CreateScriptFromRuntimeData(RuntimeScriptData runtime)
    {
        var variablesComplete = runtime.VariablesComplete
                                && runtime.Variables.Count == runtime.VariableCount;
        var referencedObjectsComplete = runtime.ReferencedObjectsComplete
                                        && runtime.ReferencedObjects.Count == runtime.RefObjectCount;
        var referencedObjects = runtime.ReferencedObjects
            .Select(static value => value.FormId)
            .ToList();
        var completeCompiledBundle = runtime.DataSize > 0
                                     && runtime.CompiledData is { Length: > 0 } compiledData
                                     && (uint)compiledData.Length == runtime.DataSize
                                     && variablesComplete
                                     && referencedObjectsComplete
                                     && ScriptBytecodeAnalyzer.HasCompleteLocalVariableBindings(
                                         compiledData,
                                         true,
                                         runtime.Variables,
                                         referencedObjects);

        // bIsCompiled=false with a complete SCDA is a legitimate disabled-script state
        // (present in retail/debug runtime captures), so retain both the bytecode and the
        // disabled flag. The contradictory state we suppress is the reverse: a compiled/
        // enabled flag without a complete executable bundle.

        // SCDA, SLSD, and SCRO/SCRV share operand slots and must come from one complete
        // runtime object. A genuine source-only stub is the sole exception: the runtime
        // header has no bytecode or tables, both empty walks are proven complete, and the
        // same object's terminated m_text or stable editor ID supplies its identity.
        var sourceOnlyStub = IsProvenUncompiledSourceOnlyStub(runtime)
                             && (!string.IsNullOrWhiteSpace(runtime.SourceText)
                                 || !string.IsNullOrWhiteSpace(runtime.EditorId));
        if (!completeCompiledBundle && !sourceOnlyStub)
        {
            return null;
        }

        var variables = completeCompiledBundle ? runtime.Variables : [];
        referencedObjects = completeCompiledBundle ? referencedObjects : [];

        // Decompilation is deferred to pass 2 in ParseScripts()
        return new ScriptRecord
        {
            FormId = runtime.FormId,
            EditorId = ScriptRecordEmissionPolicy.ResolveEditorId(
                runtime.EditorId,
                runtime.SourceText),
            VariableCount = completeCompiledBundle ? runtime.VariableCount : 0,
            RefObjectCount = completeCompiledBundle ? runtime.RefObjectCount : 0,
            CompiledSize = completeCompiledBundle ? runtime.DataSize : 0,
            LastVariableId = completeCompiledBundle ? runtime.LastVariableId : 0,
            IsQuestScript = runtime.IsQuestScript,
            IsMagicEffectScript = runtime.IsMagicEffectScript,
            IsCompiled = runtime.IsCompiled,
            SourceText = runtime.SourceText,
            SourceTextOrigin = string.IsNullOrEmpty(runtime.SourceText)
                ? ScriptSourceTextOrigin.None
                : ScriptSourceTextOrigin.RuntimeSameObject,
            CompiledData = completeCompiledBundle ? runtime.CompiledData : null,
            ExecutableBundleFromRuntime = completeCompiledBundle,
            IsIncompleteExecutableBundle = false,
            Variables = variables,
            ReferencedObjects = referencedObjects,
            OwnerQuestFormId = runtime.OwnerQuestFormId,
            QuestScriptDelay = runtime.QuestScriptDelay,
            Offset = runtime.DumpOffset,
            IsBigEndian = true,
            FromRuntime = true
        };
    }

    private static bool IsProvenUncompiledSourceOnlyStub(RuntimeScriptData runtime)
    {
        return runtime.DataSize == 0
               && runtime.CompiledData is not { Length: > 0 }
               && !runtime.IsCompiled
               && runtime.VariableCount == 0
               && runtime.RefObjectCount == 0
               && runtime.Variables.Count == 0
               && runtime.ReferencedObjects.Count == 0
               && runtime.VariablesComplete
               && runtime.ReferencedObjectsComplete;
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
