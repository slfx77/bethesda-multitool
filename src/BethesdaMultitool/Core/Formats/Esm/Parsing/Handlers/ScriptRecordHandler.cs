using System.Buffers;
using System.Buffers.Binary;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

internal sealed class ScriptRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    private Dictionary<uint, uint>? _runtimeObjectToScript;

    internal List<RuntimeScriptData> RuntimeScripts { get; private set; } = [];

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
            RuntimeScripts = MergeRuntimeScriptData(scripts);
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
            var script = scripts[i];
            if (script.CompiledData is { Length: > 0 })
            {
                var (decompiled, crossRefResolved) = DecompileScript(script, variableDb);
                script = decompiled;
                resolvedCount += crossRefResolved;
            }

            scripts[i] = Context.MinidumpInfo is not null
                ? EnforceCapturedEmissionContract(script)
                : script;
        }

        if (resolvedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Scripts: resolved {resolvedCount} cross-script variable references to names");
        }

        RuntimeScripts = ScriptRuntimeMerger.ApplySourceCorrespondenceStatuses(
            RuntimeScripts,
            scripts);

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
            // Endianness belongs to the accepted SCDA payload, not to the record as a whole.
            // A script can be enriched with runtime source/owner metadata while retaining a
            // little-endian ESM fragment. ScriptRuntimeMerger updates IsBigEndian only when
            // it atomically adopts runtime bytecode and its ordered metadata tables.
            var isBigEndian = script.IsBigEndian;
            var decompiler = new ScriptDecompiler(
                script.Variables, script.ReferencedObjects, Context.ResolveFormName,
                isBigEndian,
                script.EditorId,
                ResolveExternalVariable,
                ScriptFunctionTables.For(Context.Game));
            decompiledText = decompiler.Decompile(script.CompiledData);
        }
        catch (Exception ex)
        {
            decompiledText = $"; Decompilation failed: {ex.Message}";
        }

        return (script with { DecompiledText = decompiledText }, crossRefsResolved);
    }

    /// <summary>
    ///     A captured SCTX is safe to serialize beside SCDA only when the full-context
    ///     decompilation agrees with it. Source-only runtime scripts have no SCDA to compare
    ///     and remain valid recovery candidates; this gate applies only to compiled bundles.
    /// </summary>
    internal static ScriptRecord EnforceCapturedSourceCorrespondence(ScriptRecord script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var decision = CapturedScriptEmissionContract.EvaluateInline(
            isDmpDerived: true,
            script.SourceTextOrigin,
            script.CompiledData,
            script.SourceText,
            script.DecompiledText,
            script.Variables,
            script.ReferencedObjects,
            script.IsBigEndian);
        if (decision.BundleIssue is not null)
        {
            var safety = script.CompiledData is { Length: > 0 }
                ? ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(
                    script.CompiledData,
                    script.IsBigEndian,
                    script.Variables,
                    script.ReferencedObjects)
                : null;
            Logger.Instance.Warn(
                $"  [Semantic] SCPT 0x{script.FormId:X8}: rejected captured SCTX before "
                + "SCDA comparison proof; rejection-categories=[UnsafeBytecode=1], "
                + $"bytecode-diagnostic-count={safety?.Diagnostics.Count ?? 0} "
                + $"[{string.Join(" | ", safety?.Diagnostics ?? [])}], {decision.BundleIssue}, "
                + $"source-origin={script.SourceTextOrigin}.");
        }
        else if (decision.SourceIssue is not null)
        {
            Logger.Instance.Warn(
                $"  [Semantic] SCPT 0x{script.FormId:X8}: rejected captured SCTX after SCDA "
                + $"comparison; {decision.SourceIssue}, source-origin={script.SourceTextOrigin}.");
        }

        return ApplySourceCorrespondenceStatus(script, script with
        {
            SourceText = decision.SourceText,
            SourceTextOrigin = decision.SourceText is null
                ? ScriptSourceTextOrigin.None
                : script.SourceTextOrigin,
            IsIncompleteExecutableBundle = !decision.ExecutableBundleSafe,
        });
    }

    private static ScriptRecord EnforceCapturedEmissionContract(ScriptRecord script)
    {
        var decision = CapturedScriptEmissionContract.EvaluateStandalone(script);
        if (decision.BundleIssue is not null)
        {
            Logger.Instance.Warn(
                $"  [Semantic] SCPT 0x{script.FormId:X8}: rejected captured executable "
                + $"bundle; {decision.BundleIssue}.");
        }

        if (decision.SourceIssue is not null)
        {
            Logger.Instance.Warn(
                $"  [Semantic] SCPT 0x{script.FormId:X8}: omitted captured SCTX; "
                + $"{decision.SourceIssue}, source-origin={script.SourceTextOrigin}.");
        }

        return ApplySourceCorrespondenceStatus(script, decision.Script);
    }

    private static ScriptRecord ApplySourceCorrespondenceStatus(
        ScriptRecord captured,
        ScriptRecord evaluated)
    {
        var hasAcceptedSource = !string.IsNullOrEmpty(evaluated.SourceText)
                                && evaluated.SourceTextOrigin != ScriptSourceTextOrigin.None
                                && !evaluated.IsIncompleteExecutableBundle;
        var status = hasAcceptedSource && evaluated.CompiledData is { Length: > 0 }
            ? ScriptSourceCorrespondenceStatus.Accepted
            : hasAcceptedSource
                ? ScriptSourceCorrespondenceStatus.AcceptedSourceOnly
                : !string.IsNullOrEmpty(captured.SourceText)
                  && string.IsNullOrEmpty(evaluated.SourceText)
                    ? ScriptSourceCorrespondenceStatus.Rejected
                    : ScriptSourceCorrespondenceStatus.Unverified;
        return evaluated with { SourceTextCorrespondenceStatus = status };
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
    private List<RuntimeScriptData> MergeRuntimeScriptData(List<ScriptRecord> scripts)
    {
        return ScriptRuntimeMerger.MergeRuntimeScriptData(Context, scripts);
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
        var serializedLocals = new SerializedScriptLocalTableParser(variables);
        var seenSchr = false;
        var hasMalformedSerializedHeader = false;
        var hasMalformedSerializedTable = false;
        var seenSctx = false;
        var seenScda = false;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);
            serializedLocals.ObserveSubrecord(sub.Signature, subData, record.IsBigEndian);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;

                case "SCHR":
                    if (seenSchr)
                    {
                        WarnRepeatedScriptBundleComponent(record.FormId, "SCHR");
                        return null;
                    }

                    seenSchr = true;
                    if (sub.DataLength < 20)
                    {
                        hasMalformedSerializedHeader = true;
                        break;
                    }

                    // Canonical ESM SCHR layout per fopdoc (Records/Subrecords/SCHR.md):
                    //   offset 0..3:   Unused (4 bytes)
                    //   offset 4..7:   RefCount (uint32)
                    //   offset 8..11:  CompiledSize (uint32)
                    //   offset 12..15: VariableCount (uint32)
                    //   offset 16..17: Type (uint16; 0=Object, 1=Quest, 0x100=Effect)
                    //   offset 18..19: Flags (uint16; 0x0001=Enabled)
                    // The runtime SCRIPT_HEADER struct has VariableCount at offset 0 and
                    // uiLastID at offset 12 — different layout. This parser is for the
                    // serialized ESM/ESP record, not the runtime struct.
                    ushort scriptType;
                    ushort scriptFlags;
                    if (record.IsBigEndian)
                    {
                        refObjectCount = BinaryPrimitives.ReadUInt32BigEndian(subData[4..]);
                        compiledSize = BinaryPrimitives.ReadUInt32BigEndian(subData[8..]);
                        variableCount = BinaryPrimitives.ReadUInt32BigEndian(subData[12..]);
                        scriptType = BinaryPrimitives.ReadUInt16BigEndian(subData[16..]);
                        scriptFlags = BinaryPrimitives.ReadUInt16BigEndian(subData[18..]);
                    }
                    else
                    {
                        refObjectCount = BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]);
                        compiledSize = BinaryPrimitives.ReadUInt32LittleEndian(subData[8..]);
                        variableCount = BinaryPrimitives.ReadUInt32LittleEndian(subData[12..]);
                        scriptType = BinaryPrimitives.ReadUInt16LittleEndian(subData[16..]);
                        scriptFlags = BinaryPrimitives.ReadUInt16LittleEndian(subData[18..]);
                    }

                    isQuestScript = scriptType == 1;
                    isMagicEffectScript = scriptType == 0x100;
                    isCompiled = (scriptFlags & 0x0001) != 0;
                    // lastVariableId is no longer carried by ESM SCHR — runtime-only field.
                    // Leave it at its declaration default (0); runtime readers populate it
                    // from the PDB struct layout if needed for diagnostics.
                    break;

                case "SCTX":
                    if (seenSctx)
                    {
                        WarnRepeatedScriptBundleComponent(record.FormId, "SCTX");
                        return null;
                    }

                    seenSctx = true;
                    sourceText = EsmStringUtils.ReadNullTermString(subData);
                    break;

                case "SCDA":
                    if (seenScda)
                    {
                        WarnRepeatedScriptBundleComponent(record.FormId, "SCDA");
                        return null;
                    }

                    seenScda = true;
                    // Raw bytecode — no endian conversion (platform-native)
                    compiledData = subData.ToArray();
                    break;

                case "SCRO":
                    if (sub.DataLength < 4)
                    {
                        hasMalformedSerializedTable = true;
                    }
                    else
                    {
                        var formId = record.IsBigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                            : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                        referencedObjects.Add(formId);
                    }

                    break;

                // SCRV entries occupy slots in the reference list alongside SCRO.
                // The bytecode uses 1-based indices into the combined SCRO+SCRV list.
                // Store with high bit set so the decompiler can distinguish them.
                case "SCRV":
                    if (sub.DataLength < 4)
                    {
                        hasMalformedSerializedTable = true;
                    }
                    else
                    {
                        var varIdx = record.IsBigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                            : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                        referencedObjects.Add(0x80000000 | varIdx);
                    }

                    break;
            }
        }

        serializedLocals.Complete();
        hasMalformedSerializedTable |= serializedLocals.IsMalformed;

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
            HasSerializedHeader = seenSchr && !hasMalformedSerializedHeader,
            HasMalformedSerializedHeader = hasMalformedSerializedHeader,
            HasMalformedSerializedTable = hasMalformedSerializedTable,
            IsIncompleteExecutableBundle = hasMalformedSerializedHeader
                                           || hasMalformedSerializedTable,
            SourceText = sourceText,
            SourceTextOrigin = string.IsNullOrEmpty(sourceText) || Context.MinidumpInfo is null
                ? ScriptSourceTextOrigin.None
                : ScriptSourceTextOrigin.DmpFragment,
            CompiledData = compiledData,
            Variables = variables,
            ReferencedObjects = referencedObjects,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static void WarnRepeatedScriptBundleComponent(uint formId, string signature)
    {
        Logger.Instance.Warn(
            $"  [Semantic] SCPT 0x{formId:X8}: repeated {signature} makes the captured "
            + "script bundle ambiguous; rejected the record.");
    }
}
