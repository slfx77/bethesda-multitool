using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

internal sealed class ScriptRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    private Dictionary<uint, uint>? _runtimeObjectToScript;

    /// <summary>
    ///     Provide pre-built object→script mappings from runtime struct data (NPC_, CREA, CONT, ACTI).
    ///     Used for DMP files where ESM records are not available for BuildCrossReferenceChains.
    /// </summary>
    internal void SetRuntimeObjectScriptMappings(Dictionary<uint, uint> objectToScript)
    {
        _runtimeObjectToScript = objectToScript;
    }

    /// <summary>
    ///     Parse all Script (SCPT) records from the scan result.
    ///     Uses a two-pass approach: first parses all scripts to build a cross-script variable
    ///     database, then decompiles with full context for proper name resolution.
    /// </summary>
    internal List<ScriptRecord> ParseScripts()
    {
        var scripts = new List<ScriptRecord>();

        if (Context.Accessor == null)
        {
            // Without accessor, create stub records from scan data
            foreach (var record in Context.GetRecordsByType("SCPT"))
            {
                scripts.Add(new ScriptRecord
                {
                    FormId = record.FormId,
                    EditorId = Context.GetEditorId(record.FormId),
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
            foreach (var record in Context.GetRecordsByType("SCPT"))
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
        if (Context.RuntimeReader != null)
        {
            MergeRuntimeScriptData(scripts);
        }

        // Build cross-script variable database: FormID -> variable list
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

        // Quest fallback: for quest scripts with no OwnerQuestFormId, scan SCRO list
        // for QUST FormIDs and map those to the script's variables.
        // RuntimeEditorIds is only populated for DMP files (runtime hash table walk).
        var questFormIds = Context.ScanResult.RuntimeEditorIds
            .Where(e => e.FormType is 0x47)
            .Select(e => e.FormId)
            .ToHashSet();
        var questFallbackCount = 0;

        if (questFormIds.Count > 0)
        {
            foreach (var script in scripts)
            {
                if (!script.IsQuestScript || script.OwnerQuestFormId.HasValue
                                          || script.Variables.Count == 0)
                {
                    continue;
                }

                foreach (var scroFormId in script.ReferencedObjects)
                {
                    if ((scroFormId & 0x80000000) != 0)
                    {
                        continue; // skip SCRV entries
                    }

                    if (variableDb.ContainsKey(scroFormId))
                    {
                        continue;
                    }

                    if (!questFormIds.Contains(scroFormId))
                    {
                        continue;
                    }

                    variableDb.TryAdd(scroFormId, script.Variables);
                    questFallbackCount++;
                }
            }
        }

        // Build object→script mappings for multi-level variable resolution.
        // When resolving ref.varN, the SCRO FormID may point to a placed reference (REFR/ACHR)
        // or a base object (NPC_/CREA) rather than a script. These mappings enable the chain:
        // placed ref → base object → script → variables
        var objectToScript = new Dictionary<uint, uint>();
        if (Context.Accessor != null)
        {
            BuildObjectToScriptMap(objectToScript);
        }

        // Merge runtime object→script mappings (from NPC_/CREA/CONT/ACTI runtime struct reads).
        // For DMP files, ESM records are freed at load time so BuildCrossReferenceChains finds nothing.
        // Runtime struct readers extract Script FormIDs from C++ object pointers instead.
        if (_runtimeObjectToScript != null)
        {
            foreach (var (objectFormId, scriptFormId) in _runtimeObjectToScript)
            {
                objectToScript.TryAdd(objectFormId, scriptFormId);
            }
        }

        var dbSizeBefore = variableDb.Count;

        // Extend variableDb with indirect object→script→variables mappings
        foreach (var (objectFormId, scriptFormId) in objectToScript)
        {
            if (variableDb.TryGetValue(scriptFormId, out var vars))
            {
                variableDb.TryAdd(objectFormId, vars);
            }
        }

        // Extend variableDb with ref→base→variables mappings
        foreach (var (refFormId, baseFormId) in Context.RefToBase)
        {
            if (variableDb.TryGetValue(baseFormId, out var vars))
            {
                variableDb.TryAdd(refFormId, vars);
            }
        }

        // EditorID-based REF→base heuristic for placed references.
        // Many placed refs have EditorIDs like "CraigBooneREF" — strip "REF" to find
        // the base form "CraigBoone" and chain to its script's variables.
        // Note: Build a fresh reverse lookup from FormIdToEditorId (which is mutable and includes
        // parse-added entries) rather than using the stale EditorIdToFormId dictionary.
        var editorIdToFormId = Context.FormIdToEditorId
            .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
        var refHeuristicCount = 0;

        foreach (var script in scripts)
        {
            foreach (var refFormId in script.ReferencedObjects)
            {
                if (variableDb.ContainsKey(refFormId))
                {
                    continue;
                }

                var editorId = Context.ResolveFormName(refFormId);
                if (editorId == null || !editorId.EndsWith("REF", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var baseName = editorId[..^3];
                if (editorIdToFormId.TryGetValue(baseName, out var baseFormId))
                {
                    if (variableDb.TryGetValue(baseFormId, out var vars))
                    {
                        variableDb.TryAdd(refFormId, vars);
                        refHeuristicCount++;
                    }
                    else if (objectToScript.TryGetValue(baseFormId, out var scriptFid)
                             && variableDb.TryGetValue(scriptFid, out var scriptVars))
                    {
                        variableDb.TryAdd(refFormId, scriptVars);
                        refHeuristicCount++;
                    }
                }
            }
        }

        Logger.Instance.Debug(
            $"  [Semantic] Cross-ref chains: {objectToScript.Count} obj→script, " +
            $"{Context.RefToBase.Count} ref→base, {refHeuristicCount} REF→base, " +
            $"{questFallbackCount} quest→script, variableDb {dbSizeBefore}→{variableDb.Count}");

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
                script.Variables, script.ReferencedObjects, Context.ResolveFormName,
                isBigEndian,
                script.EditorId,
                ResolveExternalVariable);
            decompiledText = decompiler.Decompile(script.CompiledData);
        }
        catch (Exception ex)
        {
            decompiledText = $"; Decompilation failed: {ex.Message}";
        }

        return (script with { DecompiledText = decompiledText }, crossRefsResolved);
    }

    /// <summary>
    ///     Delegates to <see cref="ScriptRuntimeMerger.BuildObjectToScriptMap" />.
    /// </summary>
    private void BuildObjectToScriptMap(Dictionary<uint, uint> objectToScript)
    {
        ScriptRuntimeMerger.BuildObjectToScriptMap(Context, objectToScript);
    }

    /// <summary>
    ///     Delegates runtime script merging to <see cref="ScriptRuntimeMerger" />.
    /// </summary>
    private void MergeRuntimeScriptData(List<ScriptRecord> scripts)
    {
        ScriptRuntimeMerger.MergeRuntimeScriptData(Context, scripts);
    }

    internal ScriptRecord? ParseScriptFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ScriptRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
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
                        Context.FormIdToEditorId[record.FormId] = editorId;
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

                // SCRV entries occupy slots in the reference list alongside SCRO.
                // The bytecode uses 1-based indices into the combined SCRO+SCRV list.
                // Store with high bit set so the decompiler can distinguish them.
                case "SCRV" when sub.DataLength >= 4:
                    var varIdx = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    referencedObjects.Add(0x80000000 | varIdx);
                    break;
            }
        }

        // Add any unpaired SLSD (variable without name)
        if (pendingSlsdIndex.HasValue)
        {
            variables.Add(new ScriptVariableInfo(pendingSlsdIndex.Value, null, pendingSlsdType));
        }

        // Decompilation is deferred to pass 2 in ParseScripts()
        return new ScriptRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
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
}
