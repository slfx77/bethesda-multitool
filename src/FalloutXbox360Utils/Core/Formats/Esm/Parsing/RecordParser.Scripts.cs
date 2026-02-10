using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class RecordParser
{
    #region Scripts

    /// <summary>
    ///     Reconstruct all Script (SCPT) records from the scan result.
    ///     Uses a two-pass approach: first parses all scripts to build a cross-script variable
    ///     database, then decompiles with full context for proper name resolution.
    /// </summary>
    public List<ScriptRecord> ReconstructScripts()
    {
        var scripts = new List<ScriptRecord>();

        if (_accessor == null)
        {
            // Without accessor, create stub records from scan data
            foreach (var record in GetRecordsByType("SCPT"))
            {
                scripts.Add(new ScriptRecord
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return scripts;
        }

        // PASS 1: Parse all scripts — collect variables, refs, compiled data (no decompilation)
        var buffer = ArrayPool<byte>.Shared.Rent(65536); // Scripts can be large
        try
        {
            foreach (var record in GetRecordsByType("SCPT"))
            {
                var script = ParseScriptFromAccessor(record, buffer);
                if (script != null)
                {
                    scripts.Add(script);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Merge runtime struct data (Script C++ objects from hash table walk)
        // Runtime merging also skips decompilation — that happens in pass 2
        if (_runtimeReader != null)
        {
            MergeRuntimeScriptData(scripts);
        }

        // Build cross-script variable database: FormID → variable list
        // This enables resolving ref.varN to ref.actualVarName during decompilation
        var variableDb = new Dictionary<uint, List<ScriptVariableInfo>>();
        foreach (var script in scripts)
        {
            if (script.Variables.Count > 0)
            {
                variableDb.TryAdd(script.FormId, script.Variables);
            }

            // Also map quest FormIDs to their script's variable lists.
            // When a script references vSomeQuest.fTimer, the SCRO points to the quest FormID,
            // not the quest's script FormID. OwnerQuestFormId links scripts to their owning quest.
            if (script.OwnerQuestFormId.HasValue && script.Variables.Count > 0)
            {
                variableDb.TryAdd(script.OwnerQuestFormId.Value, script.Variables);
            }
        }

        // PASS 2: Decompile all scripts with the full cross-script variable database
        var resolvedCount = 0;
        for (var i = 0; i < scripts.Count; i++)
        {
            if (scripts[i].CompiledData is not { Length: > 0 })
            {
                continue;
            }

            var (decompiled, crossRefResolved) = DecompileScript(scripts[i], variableDb);
            scripts[i] = decompiled;
            resolvedCount += crossRefResolved;
        }

        if (resolvedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Scripts: resolved {resolvedCount} cross-script variable references to names");
        }

        return scripts;
    }

    /// <summary>
    ///     Decompile a single script using the full cross-script variable database.
    ///     Returns the updated script and the count of cross-script variable references resolved.
    /// </summary>
    private (ScriptRecord Script, int CrossRefsResolved) DecompileScript(
        ScriptRecord script,
        Dictionary<uint, List<ScriptVariableInfo>> variableDb)
    {
        if (script.CompiledData is not { Length: > 0 })
        {
            return (script, 0);
        }

        var crossRefsResolved = 0;

        string? ResolveExternalVariable(uint formId, ushort varIndex)
        {
            if (!variableDb.TryGetValue(formId, out var vars))
            {
                return null;
            }

            var variable = vars.FirstOrDefault(v => v.Index == varIndex);
            if (variable?.Name != null)
            {
                crossRefsResolved++;
                return variable.Name;
            }

            return null;
        }

        string? decompiledText;
        try
        {
            // ESM SCDA bytecode is always little-endian (compiled by PC GECK).
            // Runtime bytecode (from memory dumps) is big-endian (byte-swapped at load time).
            var isBigEndian = script.FromRuntime;
            var decompiler = new ScriptDecompiler(
                script.Variables, script.ReferencedObjects, ResolveFormName,
                isBigEndian: isBigEndian,
                scriptName: script.EditorId,
                resolveExternalVariable: ResolveExternalVariable);
            decompiledText = decompiler.Decompile(script.CompiledData);
        }
        catch (Exception ex)
        {
            decompiledText = $"; Decompilation failed: {ex.Message}";
        }

        return (script with { DecompiledText = decompiledText }, crossRefsResolved);
    }

    /// <summary>
    ///     Merge runtime Script struct data into existing scripts or create new entries.
    ///     Scripts found via runtime hash table walk (FormType 0x11) may have source text
    ///     and compiled bytecode that ESM fragments don't contain (game discards ESM records at load time).
    ///     Decompilation is deferred to pass 2.
    /// </summary>
    private void MergeRuntimeScriptData(List<ScriptRecord> scripts)
    {
        var scriptsByFormId = scripts.ToDictionary(s => s.FormId, s => s);
        var runtimeEntries = _scanResult.RuntimeEditorIds
            .Where(e => e.FormType == 0x11 && e.TesFormOffset != null)
            .ToList();

        var runtimeCount = 0;
        var enrichedCount = 0;

        foreach (var entry in runtimeEntries)
        {
            var runtimeData = _runtimeReader!.ReadRuntimeScript(entry);
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

    private static ScriptRecord EnrichScriptWithRuntimeData(
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

        // Decompilation is deferred to pass 2 in ReconstructScripts()
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

    private static ScriptRecord CreateScriptFromRuntimeData(RuntimeScriptData runtime)
    {
        var variables = runtime.Variables;
        var referencedObjects = runtime.ReferencedObjects.Select(r => r.FormId).ToList();

        // Decompilation is deferred to pass 2 in ReconstructScripts()
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

    private ScriptRecord? ParseScriptFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ScriptRecord
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;

        // SCHR header fields (PDB: SCRIPT_HEADER, 20 bytes)
        uint variableCount = 0, refObjectCount = 0, compiledSize = 0, lastVariableId = 0;
        bool isQuestScript = false, isMagicEffectScript = false, isCompiled = false;

        string? sourceText = null;
        byte[]? compiledData = null;

        var variables = new List<ScriptVariableInfo>();
        var referencedObjects = new List<uint>();

        // Track pending SLSD data for pairing with SCVR
        uint? pendingSlsdIndex = null;
        byte pendingSlsdType = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        _formIdToEditorId[record.FormId] = editorId;
                    }

                    break;

                case "SCHR" when sub.DataLength >= 20:
                    // PDB SCRIPT_HEADER: variableCount(4), refObjectCount(4), dataSize(4),
                    // m_uiLastID(4), bIsQuestScript(1), bIsMagicEffectScript(1), bIsCompiled(1), pad(1)
                    if (record.IsBigEndian)
                    {
                        variableCount = BinaryPrimitives.ReadUInt32BigEndian(subData);
                        refObjectCount = BinaryPrimitives.ReadUInt32BigEndian(subData[4..]);
                        compiledSize = BinaryPrimitives.ReadUInt32BigEndian(subData[8..]);
                        lastVariableId = BinaryPrimitives.ReadUInt32BigEndian(subData[12..]);
                    }
                    else
                    {
                        variableCount = BinaryPrimitives.ReadUInt32LittleEndian(subData);
                        refObjectCount = BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]);
                        compiledSize = BinaryPrimitives.ReadUInt32LittleEndian(subData[8..]);
                        lastVariableId = BinaryPrimitives.ReadUInt32LittleEndian(subData[12..]);
                    }

                    isQuestScript = subData[16] != 0;
                    isMagicEffectScript = subData[17] != 0;
                    isCompiled = subData[18] != 0;
                    break;

                case "SCTX":
                    sourceText = EsmStringUtils.ReadNullTermString(subData);
                    break;

                case "SCDA":
                    // Raw bytecode — no endian conversion (platform-native)
                    compiledData = subData.ToArray();
                    break;

                case "SLSD" when sub.DataLength >= 16:
                    // PDB SCRIPT_LOCAL: uiID(4) + fValue(8) + bIsInteger(4) + padding(8)
                    pendingSlsdIndex = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    // bIsInteger at offset 12
                    var isIntegerRaw = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[12..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[12..]);
                    pendingSlsdType = isIntegerRaw != 0 ? (byte)1 : (byte)0;
                    break;

                case "SCVR":
                    {
                        var varName = EsmStringUtils.ReadNullTermString(subData);
                        if (pendingSlsdIndex.HasValue)
                        {
                            variables.Add(new ScriptVariableInfo(pendingSlsdIndex.Value, varName, pendingSlsdType));
                            pendingSlsdIndex = null;
                        }

                        break;
                    }

                case "SCRO" when sub.DataLength >= 4:
                    var formId = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    referencedObjects.Add(formId);
                    break;
            }
        }

        // Add any unpaired SLSD (variable without name)
        if (pendingSlsdIndex.HasValue)
        {
            variables.Add(new ScriptVariableInfo(pendingSlsdIndex.Value, null, pendingSlsdType));
        }

        // Decompilation is deferred to pass 2 in ReconstructScripts()
        return new ScriptRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            VariableCount = variableCount,
            RefObjectCount = refObjectCount,
            CompiledSize = compiledSize,
            LastVariableId = lastVariableId,
            IsQuestScript = isQuestScript,
            IsMagicEffectScript = isMagicEffectScript,
            IsCompiled = isCompiled,
            SourceText = sourceText,
            CompiledData = compiledData,
            Variables = variables,
            ReferencedObjects = referencedObjects,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
