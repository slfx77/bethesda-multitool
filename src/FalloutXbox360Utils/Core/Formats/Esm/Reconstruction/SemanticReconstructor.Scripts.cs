using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class SemanticReconstructor
{
    #region Scripts

    /// <summary>
    ///     Reconstruct all Script (SCPT) records from the scan result.
    ///     Parses SCHR header, SCTX source text, SCDA bytecode, SLSD+SCVR variables, and SCRO references.
    /// </summary>
    public List<ReconstructedScript> ReconstructScripts()
    {
        var scripts = new List<ReconstructedScript>();

        if (_accessor == null)
        {
            // Without accessor, create stub records from scan data
            foreach (var record in GetRecordsByType("SCPT"))
            {
                scripts.Add(new ReconstructedScript
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return scripts;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(65536); // Scripts can be large
        try
        {
            foreach (var record in GetRecordsByType("SCPT"))
            {
                var script = ReconstructScriptFromAccessor(record, buffer);
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
        if (_runtimeReader != null)
        {
            MergeRuntimeScriptData(scripts);
        }

        return scripts;
    }

    /// <summary>
    ///     Merge runtime Script struct data into existing scripts or create new entries.
    ///     Scripts found via runtime hash table walk (FormType 0x11) may have source text
    ///     and compiled bytecode that ESM fragments don't contain (game discards ESM records at load time).
    /// </summary>
    private void MergeRuntimeScriptData(List<ReconstructedScript> scripts)
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

    private ReconstructedScript EnrichScriptWithRuntimeData(
        ReconstructedScript existing, RuntimeScriptData runtime)
    {
        var needsUpdate = false;
        var sourceText = existing.SourceText;
        var compiledData = existing.CompiledData;
        var decompiledText = existing.DecompiledText;
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

        // Decompile newly-acquired bytecode (runtime data is always big-endian)
        if (compiledData is { Length: > 0 } && string.IsNullOrEmpty(decompiledText))
        {
            try
            {
                var decompiler = new ScriptDecompiler(variables, referencedObjects, ResolveFormName,
                    isBigEndian: true);
                decompiledText = decompiler.Decompile(compiledData);
            }
            catch (Exception ex)
            {
                decompiledText = $"; Decompilation failed: {ex.Message}";
            }
        }

        return existing with
        {
            SourceText = sourceText,
            CompiledData = compiledData,
            DecompiledText = decompiledText,
            Variables = variables,
            ReferencedObjects = referencedObjects,
            OwnerQuestFormId = runtime.OwnerQuestFormId ?? existing.OwnerQuestFormId,
            QuestScriptDelay = runtime.QuestScriptDelay
        };
    }

    private ReconstructedScript CreateScriptFromRuntimeData(RuntimeScriptData runtime)
    {
        var variables = runtime.Variables;
        var referencedObjects = runtime.ReferencedObjects.Select(r => r.FormId).ToList();

        // Decompile bytecode if available (runtime data is always big-endian)
        string? decompiledText = null;
        if (runtime.CompiledData is { Length: > 0 })
        {
            try
            {
                var decompiler = new ScriptDecompiler(variables, referencedObjects, ResolveFormName,
                    isBigEndian: true);
                decompiledText = decompiler.Decompile(runtime.CompiledData);
            }
            catch (Exception ex)
            {
                decompiledText = $"; Decompilation failed: {ex.Message}";
            }
        }

        return new ReconstructedScript
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
            DecompiledText = decompiledText,
            Variables = variables,
            ReferencedObjects = referencedObjects,
            OwnerQuestFormId = runtime.OwnerQuestFormId,
            QuestScriptDelay = runtime.QuestScriptDelay,
            Offset = runtime.DumpOffset,
            IsBigEndian = true,
            FromRuntime = true
        };
    }

    private ReconstructedScript? ReconstructScriptFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ReconstructedScript
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

        // Decompile bytecode if available
        string? decompiledText = null;
        if (compiledData is { Length: > 0 })
        {
            try
            {
                // SCDA bytecode is always little-endian — compiled by PC-based GECK, stored verbatim in ESM.
                // The game byte-swaps at runtime via ScriptRunner::Endian() on Xbox 360.
                var decompiler = new ScriptDecompiler(variables, referencedObjects, ResolveFormName,
                    isBigEndian: false);
                decompiledText = decompiler.Decompile(compiledData);
            }
            catch (Exception ex)
            {
                decompiledText = $"; Decompilation failed: {ex.Message}";
            }
        }

        return new ReconstructedScript
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
            DecompiledText = decompiledText,
            Variables = variables,
            ReferencedObjects = referencedObjects,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
