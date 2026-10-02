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
    private List<ScriptOwnerLink>? _runtimeObjectToScript;

    internal List<RuntimeScriptData> RuntimeScripts { get; private set; } = [];

    /// <summary>
    ///     Provide pre-built object→script mappings from runtime struct data (NPC_, CREA, CONT, ACTI).
    ///     Used for DMP files where ESM records are not available for BuildCrossReferenceChains.
    /// </summary>
    internal void SetRuntimeObjectScriptMappings(List<ScriptOwnerLink> objectToScript)
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

            Context.ExternalScriptVariables = new ExternalScriptVariableResolver(scripts, []);
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

        var links = new List<ScriptOwnerLink>();
        ScriptRuntimeMerger.BuildObjectToScriptLinks(Context, links);
        if (_runtimeObjectToScript is not null) links.AddRange(_runtimeObjectToScript);
        var variableResolver = new ExternalScriptVariableResolver(scripts, links);
        Context.ExternalScriptVariables = variableResolver;

        // PASS 2: Decompile all scripts with the full cross-script variable database
        var resolvedCount = 0;
        for (var i = 0; i < scripts.Count; i++)
        {
            var script = scripts[i];
            if (script.CompiledData is { Length: > 0 })
            {
                var (decompiled, crossRefResolved) = DecompileScript(script, variableResolver);
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
        ExternalScriptVariableResolver variableResolver)
    {
        if (script.CompiledData is not { Length: > 0 })
        {
            return (script, 0);
        }

        var bindings = new List<ScriptExternalVariableBinding>();

        string? decompiledText;
        try
        {
            // Decode in the BYTECODE's order, never the container's: an Xbox 360 ESM is a
            // big-endian container around little-endian SCDA. ParseScriptFromAccessor records the
            // order ScriptBytecodeByteOrderSelector chose; a script enriched with runtime
            // source/owner metadata keeps it, and ScriptRuntimeMerger replaces it only when it
            // atomically adopts a runtime Script object's (already swapped) bytecode together with
            // its ordered metadata tables.
            var isBigEndian = script.IsBigEndianBytecode;
            var decompiler = new ScriptDecompiler(
                script.Variables, script.ReferencedObjects, Context.ResolveFormName,
                isBigEndian,
                script.EditorId,
                variableResolver.Track(bindings),
                ScriptFunctionTables.For(Context.Game));
            decompiledText = decompiler.Decompile(script.CompiledData);
        }
        catch (Exception ex)
        {
            decompiledText = $"; Decompilation failed: {ex.Message}";
        }

        return (script with { DecompiledText = decompiledText, ExternalVariableBindings = bindings },
            bindings.Count(b => b.Status == "resolved"));
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
            true,
            script.SourceTextOrigin,
            script.CompiledData,
            script.SourceText,
            script.DecompiledText,
            script.Variables,
            script.ReferencedObjects,
            script.IsBigEndianBytecode,
            ScriptRecordEmissionPolicy.ResolveEditorId(script));
        if (decision.BundleIssue is not null)
        {
            var safety = script.CompiledData is { Length: > 0 }
                ? ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(
                    script.CompiledData,
                    script.IsBigEndianBytecode,
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
            SourceTextOrigin = decision.ResolveSourceTextOrigin(script.SourceTextOrigin),
            IsIncompleteExecutableBundle = !decision.ExecutableBundleSafe
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
        // This status describes the CAPTURED text's correspondence with the bytecode, which is
        // what provenance reports are asking about. A decompiled substitution is emitted source
        // but never an accepted capture — since the 2026-09-03 ruling a rejected capture is
        // replaced rather than dropped, so "has text" alone no longer implies "was verified".
        var sourceIsDecompiled =
            evaluated.SourceTextOrigin == ScriptSourceTextOrigin.DecompiledFromBytecode;
        var hasAcceptedSource = !string.IsNullOrEmpty(evaluated.SourceText)
                                && evaluated.SourceTextOrigin != ScriptSourceTextOrigin.None
                                && !sourceIsDecompiled
                                && !evaluated.IsIncompleteExecutableBundle;
        ScriptSourceCorrespondenceStatus status;
        if (hasAcceptedSource)
        {
            status = evaluated.CompiledData is { Length: > 0 }
                ? ScriptSourceCorrespondenceStatus.Accepted
                : ScriptSourceCorrespondenceStatus.AcceptedSourceOnly;
        }
        else
        {
            status = !string.IsNullOrEmpty(captured.SourceText)
                     && (string.IsNullOrEmpty(evaluated.SourceText) || sourceIsDecompiled)
                ? ScriptSourceCorrespondenceStatus.Rejected
                : ScriptSourceCorrespondenceStatus.Unverified;
        }

        return evaluated with { SourceTextCorrespondenceStatus = status };
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
                    //   offset 4..7:   RefCount (uint32, container byte order)
                    //   offset 8..11:  CompiledSize (uint32, container byte order)
                    //   offset 12..15: VariableCount (uint32, container byte order)
                    //   offset 16..17: Type (uint16, little-endian on every platform;
                    //                  0=Object, 1=Quest, 0x100=Effect)
                    //   offset 18..19: Flags (uint16, little-endian on every platform; 0x0001=Enabled)
                    // Bytes 16..19 are byte-sized flags the Xbox 360 never swaps: the engine's
                    // SCRIPT_HEADER holds bool bIsQuestScript@16, bIsMagicEffectScript@17 and
                    // bIsCompiled@18 (docs/PDB_Script_Bytecode_Format.md), which is fopdoc's
                    // little-endian u16 Type/Flags, and the converter schema passes them through
                    // unswapped (SubrecordDialogueSchemas SCHR: UInt16LittleEndian). Measured on the
                    // July 2010 X360 prototype: of the SCPT that PC retail types Quest or Effect, the
                    // little-endian reading agrees on 444 of 444 and a container-order reading on 0
                    // (it swaps Quest with Effect and turns Flags 0x0001 into 0x0100).
                    // The runtime SCRIPT_HEADER struct has VariableCount at offset 0 and
                    // uiLastID at offset 12 — different layout. This parser is for the
                    // serialized ESM/ESP record, not the runtime struct.
                    refObjectCount = ReadContainerUInt32(subData[4..], record.IsBigEndian);
                    compiledSize = ReadContainerUInt32(subData[8..], record.IsBigEndian);
                    variableCount = ReadContainerUInt32(subData[12..], record.IsBigEndian);
                    var scriptType = BinaryPrimitives.ReadUInt16LittleEndian(subData[16..]);
                    var scriptFlags = BinaryPrimitives.ReadUInt16LittleEndian(subData[18..]);

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
                    // Stored verbatim. Its byte order belongs to the payload, not the container
                    // (serialized SCDA is little-endian inside an Xbox 360 ESM too), and is
                    // selected once the whole record has been read.
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

        // Every measured on-disk SCDA is little-endian whatever the container (all 2,487 SCPT of
        // the July 2010 X360 prototype and all 2,546 of the X360 final open with 1D 00 00 00), so
        // an on-disk payload the selector cannot decide defaults to little-endian. A DMP fragment
        // keeps the container order instead: dump fragments were not measured, and that is what
        // this parser has always assumed for them.
        var isDmpDerived = Context.MinidumpInfo is not null;
        var bytecodeOrder = ScriptBytecodeByteOrderSelector.Select(
            compiledData,
            variables,
            referencedObjects,
            isDmpDerived && record.IsBigEndian,
            isDmpDerived
                ? ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback
                : ScriptBytecodeByteOrderEvidence.AmbiguousSerializedDefault,
            Context.Game);
        if (bytecodeOrder.Evidence is ScriptBytecodeByteOrderEvidence.AmbiguousSerializedDefault
            or ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback)
        {
            Logger.Instance.Debug(
                $"  [Semantic] SCPT 0x{record.FormId:X8}: SCDA byte order not decided by the payload; "
                + $"read it {(bytecodeOrder.IsBigEndian ? "big" : "little")}-endian "
                + $"({bytecodeOrder.Evidence}; {bytecodeOrder.Detail}).");
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
            IsBigEndian = record.IsBigEndian,
            IsBigEndianBytecode = bytecodeOrder.IsBigEndian,
            BytecodeByteOrderEvidence = bytecodeOrder.Evidence
        };
    }

    private static uint ReadContainerUInt32(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    private static void WarnRepeatedScriptBundleComponent(uint formId, string signature)
    {
        Logger.Instance.Warn(
            $"  [Semantic] SCPT 0x{formId:X8}: repeated {signature} makes the captured "
            + "script bundle ambiguous; rejected the record.");
    }
}
