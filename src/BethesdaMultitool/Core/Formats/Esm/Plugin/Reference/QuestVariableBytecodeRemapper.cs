using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Script;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

internal sealed record QuestVariableBytecodeRemapDiagnostic(
    string RecordType,
    uint RecordFormId,
    string ScriptPath,
    int ReadOperandsPatched,
    int WriteOperandsPatched,
    bool SourceTextOmitted,
    string Message);

internal sealed record QuestVariableBytecodeRemapResult(
    int ScriptsPatched,
    int ReadOperandsPatched,
    int WriteOperandsPatched,
    IReadOnlyList<QuestVariableBytecodeRemapDiagnostic> Diagnostics);

/// <summary>
///     One writer-side reason a candidate producer bundle failed (or would fail) the
///     structural write proof. Diagnostic-only: production evidence collection is unchanged.
/// </summary>
internal sealed record ProducerScanRejection(
    string RecordType,
    uint FormId,
    string ScriptPath,
    string Reason,
    string Detail);

/// <summary>
///     Applies the same exact source-to-target quest-local decisions used for CTDA to every
///     emission-eligible DMP script bundle. SCDA, its ordered SCRO/SCRV table, and its local table
///     are treated atomically; retained master scripts and shared master INFO overlays are
///     never changed.
/// </summary>
internal static class QuestVariableBytecodeRemapper
{
    internal enum ProducerOperandIdentity
    {
        Source,
        Target,
    }

    /// <summary>
    ///     Finds direct quest-local writes whose owning record is both planner-routed and
    ///     otherwise emission-eligible. This is deliberately analysis-only: the caller
    ///     gates unsupported fresh locals before <see cref="Apply" /> mutates any SCDA.
    ///     INFO result scripts are handled separately by <see cref="DialogueProducerLedger" />
    ///     because their nested dialogue section is reconstructed outside EmitPlan.
    /// </summary>
    public static IReadOnlyList<QuestVariableProducerEvidence> FindEmissionEligibleProducerWrites(
        RecordCollection records,
        IReadOnlyList<QuestVariableRecoveryMapping> freshMappings,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? formIdAliases,
        IReadOnlySet<string> skippedRecordTypes)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(freshMappings);
        ArgumentNullException.ThrowIfNull(masterRecordsByFormId);
        ArgumentNullException.ThrowIfNull(skippedRecordTypes);

        if (freshMappings.Count == 0)
        {
            return [];
        }

        var index = BuildMappingIndex(freshMappings, formIdAliases, ProducerOperandIdentity.Source);
        var evidence = new HashSet<QuestVariableProducerEvidence>();

        void ScanBundle(
            string recordType,
            uint recordFormId,
            string scriptPath,
            byte[]? compiledData,
            IReadOnlyList<ScriptVariableInfo> variables,
            IReadOnlyList<uint> referencedObjects,
            bool isBigEndian)
        {
            foreach (var mapping in FindBundleProducerWrites(
                         recordType,
                         recordFormId,
                         scriptPath,
                         compiledData,
                         variables,
                         referencedObjects,
                         isBigEndian,
                         index,
                         formIdAliases,
                         ProducerOperandIdentity.Source))
            {
                evidence.Add(new QuestVariableProducerEvidence(
                    mapping,
                    new QuestVariableProducerOwner(recordType, recordFormId, scriptPath)));
            }
        }

        // Every record type is planner-owned since the 2026-08-11 legacy retirement, so
        // eligibility reduces to "not explicitly skipped by --skip-record-type".
        bool IsEligible(string recordType) => !skippedRecordTypes.Contains(recordType);

        if (IsEligible("SCPT"))
        {
            foreach (var script in records.Scripts)
            {
                if (IsMasterAnchored("SCPT", script.FormId, masterRecordsByFormId, formIdAliases))
                {
                    continue;
                }

                ScanBundle(
                    "SCPT",
                    script.FormId,
                    script.EditorId ?? "SCPT",
                    script.CompiledData,
                    script.Variables,
                    script.ReferencedObjects,
                    script.IsBigEndian);
            }
        }

        if (IsEligible("PACK"))
        {
            foreach (var package in records.Packages)
            {
                if (IsMasterAnchored("PACK", package.FormId, masterRecordsByFormId, formIdAliases))
                {
                    continue;
                }

                // Match PackEncoder's atomic owner gate. Null validity keeps this early
                // pass from guessing future FormID liveness while still rejecting known-
                // incomplete SCDA/SLSD/SCRO/SCRV bundles.
                if (InlineScriptReferenceValidator.FindFirstIssue(
                        package, null, formIdAliases) is not null)
                {
                    continue;
                }

                ScanAction(package, package.OnBegin);
                ScanAction(package, package.OnEnd);
                ScanAction(package, package.OnChange);
            }
        }

        if (IsEligible("TERM"))
        {
            foreach (var terminal in records.Terminals)
            {
                if (IsMasterAnchored("TERM", terminal.FormId, masterRecordsByFormId, formIdAliases))
                {
                    continue;
                }

                if (InlineScriptReferenceValidator.FindFirstIssue(
                        terminal, null, formIdAliases) is not null)
                {
                    continue;
                }

                for (var itemIndex = 0; itemIndex < terminal.MenuItems.Count; itemIndex++)
                {
                    var item = terminal.MenuItems[itemIndex];
                    ScanBundle(
                        "TERM",
                        terminal.FormId,
                        $"{terminal.EditorId ?? $"TERM 0x{terminal.FormId:X8}"}/menu[{itemIndex}]",
                        item.CompiledData,
                        item.Variables,
                        item.ReferencedObjects,
                        item.IsBigEndianBytecode);
                }
            }
        }

        return evidence
            .OrderBy(static item => item.Mapping.TargetScriptFormId)
            .ThenBy(static item => item.Mapping.TargetVariable.Index)
            .ThenBy(static item => item.Mapping.SourceQuestFormId)
            .ThenBy(static item => item.Mapping.SourceVariable.Index)
            .ThenBy(static item => item.Owner.RecordType, StringComparer.Ordinal)
            .ThenBy(static item => item.Owner.SourceFormId)
            .ThenBy(static item => item.Owner.ScriptPath, StringComparer.Ordinal)
            .ToArray();

        void ScanAction(PackageRecord package, PackageEventAction? action)
        {
            if (action is null)
            {
                return;
            }

            var path = $"{package.EditorId ?? $"PACK 0x{package.FormId:X8}"}/{action.Kind}";
            for (var scriptIndex = 0; scriptIndex < action.Scripts.Count; scriptIndex++)
            {
                var script = action.Scripts[scriptIndex];
                ScanBundle(
                    "PACK",
                    package.FormId,
                    $"{path}/script[{scriptIndex}]",
                    script.CompiledData,
                    script.Variables,
                    script.ReferencedObjects,
                    script.IsBigEndianBytecode);
            }
        }
    }

    /// <summary>
    ///     Finds exact result-script producers on the combined dialogue output. An unsafe
    ///     sibling block invalidates the complete INFO owner, matching the atomic gate in
    ///     <see cref="DialogGrupBuilder" />. Source-mode is used before remapping; target-mode
    ///     records the bundles that actually reached the emitted dialogue byte stream.
    /// </summary>
    internal static ImmutableArray<QuestVariableProducerEvidence> FindInfoProducerWrites(
        IReadOnlyList<DialogueRecord> infos,
        IReadOnlyList<QuestVariableRecoveryMapping> mappings,
        IReadOnlyDictionary<uint, uint>? formIdAliases,
        ProducerOperandIdentity operandIdentity)
    {
        ArgumentNullException.ThrowIfNull(infos);
        ArgumentNullException.ThrowIfNull(mappings);

        var result = ImmutableArray.CreateBuilder<QuestVariableProducerEvidence>();
        foreach (var info in infos)
        {
            result.AddRange(FindInfoProducerWrites(
                info,
                info.FormId,
                info.EditorId,
                mappings,
                formIdAliases,
                operandIdentity,
                validFormIds: null,
                emissionRemapTable: null));
        }

        return result
            .Distinct()
            .OrderBy(static item => item.Mapping.TargetScriptFormId)
            .ThenBy(static item => item.Mapping.TargetVariable.Index)
            .ThenBy(static item => item.Owner.SourceFormId)
            .ThenBy(static item => item.Owner.ScriptPath, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    internal static ImmutableArray<QuestVariableProducerEvidence> FindInfoProducerWrites(
        DialogueRecord info,
        uint ownerSourceFormId,
        string? ownerEditorId,
        IReadOnlyList<QuestVariableRecoveryMapping> mappings,
        IReadOnlyDictionary<uint, uint>? formIdAliases,
        ProducerOperandIdentity operandIdentity,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? emissionRemapTable)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(mappings);
        if (ownerSourceFormId == 0 || mappings.Count == 0)
        {
            return [];
        }

        // INFO is one atomic owner. If either begin/end result block is incomplete or has
        // an unresolvable fixed-slot reference, no block on this INFO can qualify.
        if (InlineScriptReferenceValidator.FindFirstIssue(
                info,
                validFormIds,
                emissionRemapTable) is not null)
        {
            return [];
        }

        var index = BuildMappingIndex(mappings, formIdAliases, operandIdentity);
        var result = ImmutableArray.CreateBuilder<QuestVariableProducerEvidence>();
        var ownerLabel = ownerEditorId ?? $"INFO 0x{ownerSourceFormId:X8}";
        // InfoEncoder serializes exactly two canonical slots: begin and end. Any surplus
        // capture is validation input for the atomic owner gate, but can never be producer
        // evidence because no bytes from that slot reach the emitted INFO.
        var emittedScriptCount = Math.Min(info.ResultScripts.Count, 2);
        for (var scriptIndex = 0; scriptIndex < emittedScriptCount; scriptIndex++)
        {
            var script = info.ResultScripts[scriptIndex];
            var scriptPath = $"{ownerLabel}/ResultScripts[{scriptIndex}]";
            foreach (var mapping in FindBundleProducerWrites(
                         "INFO",
                         ownerSourceFormId,
                         scriptPath,
                         script.CompiledData,
                         script.Variables,
                         script.ReferencedObjects,
                         script.IsBigEndianBytecode,
                         index,
                         formIdAliases,
                         operandIdentity))
            {
                result.Add(new QuestVariableProducerEvidence(
                    mapping,
                    new QuestVariableProducerOwner("INFO", ownerSourceFormId, scriptPath)));
            }
        }

        return result.Distinct().ToImmutableArray();
    }

    /// <summary>
    ///     Diagnostic-only mirror of the per-INFO producer scan. Unlike
    ///     <see cref="FindInfoProducerWrites(DialogueRecord,uint,string?,IReadOnlyList{QuestVariableRecoveryMapping},IReadOnlyDictionary{uint,uint}?,ProducerOperandIdentity,IReadOnlySet{uint}?,IReadOnlyDictionary{uint,uint}?)" />,
    ///     it keeps scanning past the atomic-INFO gate and inspects EVERY result-script slot,
    ///     recording one <see cref="ProducerScanRejection" /> per fail-closed condition. The
    ///     returned evidence is what a scan of this INFO's bytecode WOULD prove — callers use
    ///     "evidence exists here but production collected none" to attribute the gap (e.g., a
    ///     writer INFO excluded from the combined NewInfos model). Never feed this evidence to
    ///     the producer gate.
    /// </summary>
    internal static ImmutableArray<QuestVariableProducerEvidence> CollectInfoProducerDiagnostics(
        DialogueRecord info,
        IReadOnlyList<QuestVariableRecoveryMapping> mappings,
        IReadOnlyDictionary<uint, uint>? formIdAliases,
        ICollection<ProducerScanRejection> rejections)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(rejections);
        if (info.FormId == 0 || mappings.Count == 0)
        {
            return [];
        }

        var ownerLabel = info.EditorId ?? $"INFO 0x{info.FormId:X8}";
        if (InlineScriptReferenceValidator.FindFirstIssue(info, null, null) is { } issue)
        {
            rejections.Add(new ProducerScanRejection(
                "INFO", info.FormId, issue.ScriptPath, "atomic-info-validator",
                $"{issue.Message} (field {issue.FieldPath}; any bad sibling slot disqualifies the whole INFO)"));
        }

        var index = BuildMappingIndex(mappings, formIdAliases, ProducerOperandIdentity.Source);
        var result = ImmutableArray.CreateBuilder<QuestVariableProducerEvidence>();
        for (var scriptIndex = 0; scriptIndex < info.ResultScripts.Count; scriptIndex++)
        {
            var script = info.ResultScripts[scriptIndex];
            var scriptPath = $"{ownerLabel}/ResultScripts[{scriptIndex}]";
            if (script.IsIncompleteExecutableBundle)
            {
                rejections.Add(new ProducerScanRejection(
                    "INFO", info.FormId, scriptPath, "duplicate-merge-incomplete-sentinel",
                    "The slot is marked incomplete-executable (raw partial capture, or two " +
                    "complete duplicate captures conflicted during result-script merge)."));
            }

            var found = FindBundleProducerWrites(
                "INFO",
                info.FormId,
                scriptPath,
                script.CompiledData,
                script.Variables,
                script.ReferencedObjects,
                script.IsBigEndianBytecode,
                index,
                formIdAliases,
                ProducerOperandIdentity.Source,
                rejections);
            if (found.Count == 0)
            {
                continue;
            }

            if (scriptIndex >= 2)
            {
                rejections.Add(new ProducerScanRejection(
                    "INFO", info.FormId, scriptPath, "surplus-slot-write-never-emitted",
                    $"Slot {scriptIndex} structurally writes {found.Count} mapped channel(s), but " +
                    "InfoEncoder serializes only slots 0-1 — the write never reaches the output."));
                continue;
            }

            foreach (var mapping in found)
            {
                result.Add(new QuestVariableProducerEvidence(
                    mapping,
                    new QuestVariableProducerOwner("INFO", info.FormId, scriptPath)));
            }
        }

        return result.Distinct().ToImmutableArray();
    }

    public static QuestVariableBytecodeRemapResult Apply(
        RecordCollection records,
        IReadOnlyList<QuestVariableRecoveryMapping> mappings,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? formIdAliases,
        IReadOnlySet<string> skippedRecordTypes)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(masterRecordsByFormId);
        ArgumentNullException.ThrowIfNull(skippedRecordTypes);

        if (mappings.Count == 0)
        {
            return new QuestVariableBytecodeRemapResult(0, 0, 0, []);
        }

        var index = BuildMappingIndex(mappings, formIdAliases, ProducerOperandIdentity.Source);
        var diagnostics = new List<QuestVariableBytecodeRemapDiagnostic>();

        if (!skippedRecordTypes.Contains("SCPT"))
        {
            for (var i = 0; i < records.Scripts.Count; i++)
            {
                var script = records.Scripts[i];
                if (IsMasterAnchored("SCPT", script.FormId, masterRecordsByFormId, formIdAliases))
                {
                    continue;
                }

                var rewrite = RewriteBundle(
                    "SCPT",
                    script.FormId,
                    script.EditorId ?? "SCPT",
                    script.CompiledData,
                    script.SourceText,
                    script.Variables,
                    script.ReferencedObjects,
                    script.IsBigEndian,
                    index,
                    formIdAliases);
                if (rewrite.CompiledData is not null)
                {
                    records.Scripts[i] = script with
                    {
                        CompiledData = rewrite.CompiledData,
                        SourceText = rewrite.DropSourceText ? null : script.SourceText,
                    };
                    diagnostics.Add(rewrite.Diagnostic!);
                }
            }
        }

        if (!skippedRecordTypes.Contains("DIAL") && !skippedRecordTypes.Contains("INFO"))
        {
            for (var i = 0; i < records.Dialogues.Count; i++)
            {
                var info = records.Dialogues[i];
                if (IsMasterAnchored("INFO", info.FormId, masterRecordsByFormId, formIdAliases))
                {
                    continue;
                }

                var rewritten = RewriteResultScripts(
                    "INFO",
                    info.FormId,
                    info.EditorId ?? $"INFO 0x{info.FormId:X8}",
                    info.ResultScripts,
                    index,
                    formIdAliases,
                    diagnostics);
                if (rewritten is not null)
                {
                    records.Dialogues[i] = info with { ResultScripts = rewritten };
                }
            }
        }

        if (!skippedRecordTypes.Contains("PACK"))
        {
            for (var i = 0; i < records.Packages.Count; i++)
            {
                var package = records.Packages[i];
                if (IsMasterAnchored("PACK", package.FormId, masterRecordsByFormId, formIdAliases))
                {
                    continue;
                }

                var onBegin = RewritePackageAction(package, package.OnBegin, index, formIdAliases, diagnostics);
                var onEnd = RewritePackageAction(package, package.OnEnd, index, formIdAliases, diagnostics);
                var onChange = RewritePackageAction(package, package.OnChange, index, formIdAliases, diagnostics);
                if (!ReferenceEquals(onBegin, package.OnBegin)
                    || !ReferenceEquals(onEnd, package.OnEnd)
                    || !ReferenceEquals(onChange, package.OnChange))
                {
                    records.Packages[i] = package with
                    {
                        OnBegin = onBegin,
                        OnEnd = onEnd,
                        OnChange = onChange
                    };
                }
            }
        }

        if (!skippedRecordTypes.Contains("TERM"))
        {
            for (var i = 0; i < records.Terminals.Count; i++)
            {
                var terminal = records.Terminals[i];
                if (IsMasterAnchored("TERM", terminal.FormId, masterRecordsByFormId, formIdAliases))
                {
                    continue;
                }

                List<TerminalMenuItem>? rewrittenItems = null;
                for (var itemIndex = 0; itemIndex < terminal.MenuItems.Count; itemIndex++)
                {
                    var item = terminal.MenuItems[itemIndex];
                    var rewrite = RewriteBundle(
                        "TERM",
                        terminal.FormId,
                        $"{terminal.EditorId ?? $"TERM 0x{terminal.FormId:X8}"}/menu[{itemIndex}]",
                        item.CompiledData,
                        item.SourceText,
                        item.Variables,
                        item.ReferencedObjects,
                        item.IsBigEndianBytecode,
                        index,
                        formIdAliases);
                    if (rewrite.CompiledData is null)
                    {
                        continue;
                    }

                    rewrittenItems ??= [.. terminal.MenuItems];
                    rewrittenItems[itemIndex] = item with
                    {
                        CompiledData = rewrite.CompiledData,
                        SourceText = rewrite.DropSourceText ? null : item.SourceText,
                    };
                    diagnostics.Add(rewrite.Diagnostic!);
                }

                if (rewrittenItems is not null)
                {
                    records.Terminals[i] = terminal with { MenuItems = rewrittenItems };
                }
            }
        }

        return new QuestVariableBytecodeRemapResult(
            diagnostics.Count,
            diagnostics.Sum(static diagnostic => diagnostic.ReadOperandsPatched),
            diagnostics.Sum(static diagnostic => diagnostic.WriteOperandsPatched),
            diagnostics);
    }

    internal static byte[] RewriteCompiledData(
        byte[] compiledData,
        bool isBigEndian,
        IReadOnlyList<ScriptVariableInfo> variables,
        IReadOnlyList<uint> referencedObjects,
        IReadOnlyList<QuestVariableRecoveryMapping> mappings,
        IReadOnlyDictionary<uint, uint>? formIdAliases = null)
    {
        var index = BuildMappingIndex(mappings, formIdAliases);
        var rewrite = RewriteBundle(
            "SCPT",
            0,
            "test-script",
            compiledData,
            null,
            variables,
            referencedObjects,
            isBigEndian,
            index,
            formIdAliases);
        return rewrite.CompiledData ?? (byte[])compiledData.Clone();
    }

    private static PackageEventAction? RewritePackageAction(
        PackageRecord package,
        PackageEventAction? action,
        MappingIndex index,
        IReadOnlyDictionary<uint, uint>? aliases,
        List<QuestVariableBytecodeRemapDiagnostic> diagnostics)
    {
        if (action is null)
        {
            return null;
        }

        var scripts = RewriteResultScripts(
            "PACK",
            package.FormId,
            $"{package.EditorId ?? $"PACK 0x{package.FormId:X8}"}/{action.Kind}",
            action.Scripts,
            index,
            aliases,
            diagnostics);
        return scripts is null ? action : action with { Scripts = scripts };
    }

    private static List<DialogueResultScript>? RewriteResultScripts(
        string recordType,
        uint recordFormId,
        string path,
        List<DialogueResultScript> scripts,
        MappingIndex index,
        IReadOnlyDictionary<uint, uint>? aliases,
        List<QuestVariableBytecodeRemapDiagnostic> diagnostics)
    {
        List<DialogueResultScript>? rewritten = null;
        for (var scriptIndex = 0; scriptIndex < scripts.Count; scriptIndex++)
        {
            var script = scripts[scriptIndex];
            var rewrite = RewriteBundle(
                recordType,
                recordFormId,
                $"{path}/script[{scriptIndex}]",
                script.CompiledData,
                script.SourceText,
                script.Variables,
                script.ReferencedObjects,
                script.IsBigEndianBytecode,
                index,
                aliases);
            if (rewrite.CompiledData is null)
            {
                continue;
            }

            rewritten ??= [.. scripts];
            rewritten[scriptIndex] = script with
            {
                CompiledData = rewrite.CompiledData,
                SourceText = rewrite.DropSourceText ? null : script.SourceText,
            };
            diagnostics.Add(rewrite.Diagnostic!);
        }

        return rewritten;
    }

    private static BundleRewrite RewriteBundle(
        string recordType,
        uint recordFormId,
        string scriptPath,
        byte[]? compiledData,
        string? sourceText,
        IReadOnlyList<ScriptVariableInfo> variables,
        IReadOnlyList<uint> referencedObjects,
        bool isBigEndian,
        MappingIndex index,
        IReadOnlyDictionary<uint, uint>? aliases)
    {
        if (compiledData is not { Length: > 0 })
        {
            return default;
        }

        var relevantQuests = referencedObjects
            .Where(static formId => (formId & 0x80000000) == 0)
            .Select(formId => ResolveAlias(formId, aliases))
            .Where(index.QuestFormIds.Contains)
            .ToHashSet();
        if (relevantQuests.Count == 0)
        {
            return default;
        }

        var walk = ScriptBytecodeAnalyzer.Walk(
            compiledData,
            isBigEndian,
            variables,
            referencedObjects,
            scriptPath);
        if (!IsCompleteWalk(walk, compiledData.Length))
        {
            throw new InvalidDataException(
                $"Cannot safely remap quest locals in {recordType} 0x{recordFormId:X8} " +
                $"({scriptPath}): its SCDA structural walk is incomplete or contains unknown data.");
        }

        // The direct ref.variable grammar is proven and tracked structurally.  The separate
        // function-parameter grammar for GetQuestVariable/GetScriptVariable is not yet proven;
        // fail closed rather than guessing where a variable operand begins.
        if (walk.DecompiledText.Contains("GetQuestVariable", StringComparison.Ordinal)
            || walk.DecompiledText.Contains("GetScriptVariable", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Cannot safely remap quest locals in {recordType} 0x{recordFormId:X8} " +
                $"({scriptPath}): SCDA uses an unverified script-variable function-parameter grammar.");
        }

        byte[]? output = null;
        var readCount = 0;
        var writeCount = 0;
        var renamedSourceIdentity = false;
        foreach (var access in walk.ExternalVariableReads)
        {
            var questFormId = ResolveAlias(access.ReferencedFormId, aliases);
            if (!index.BySource.TryGetValue(
                    new MappingKey(questFormId, access.VariableIndex),
                    out var mapping))
            {
                continue;
            }

            ValidateOperandIdentity(recordType, recordFormId, scriptPath, access, mapping);
            var targetIndex = checked((ushort)mapping.TargetVariable.Index);
            var renamedIdentity = !string.Equals(
                mapping.SourceVariable.Name,
                mapping.TargetVariable.Name,
                StringComparison.OrdinalIgnoreCase);
            if (targetIndex == access.VariableIndex)
            {
                if (renamedIdentity)
                {
                    renamedSourceIdentity = true;
                }

                continue;
            }

            if (access.OperandOffset < 0 || access.OperandOffset + sizeof(ushort) > compiledData.Length)
            {
                throw new InvalidDataException(
                    $"Tracked quest-local operand at 0x{access.OperandOffset:X} is outside " +
                    $"{recordType} 0x{recordFormId:X8} SCDA.");
            }

            output ??= (byte[])compiledData.Clone();
            if (isBigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(
                    output.AsSpan(access.OperandOffset, sizeof(ushort)),
                    targetIndex);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    output.AsSpan(access.OperandOffset, sizeof(ushort)),
                    targetIndex);
            }

            if (access.IsWrite)
            {
                writeCount++;
            }
            else
            {
                readCount++;
            }

            renamedSourceIdentity |= renamedIdentity;
        }

        if (output is null && !renamedSourceIdentity)
        {
            return default;
        }

        var omitStaleSource = renamedSourceIdentity && !string.IsNullOrEmpty(sourceText);
        string rewriteReason;
        if (!omitStaleSource)
        {
            rewriteReason = "Patched SCDA quest-local operands to the same exact target IDs used by emitted conditions.";
        }
        else if (output is null)
        {
            rewriteReason = "Retained the numerically identical SCDA local operand and omitted stale SCTX because the emitted local required a collision-safe name.";
        }
        else
        {
            rewriteReason = "Patched SCDA to the exact appended local and omitted stale SCTX because the emitted local required a collision-safe name.";
        }

        return new BundleRewrite(
            output ?? compiledData,
            omitStaleSource,
            new QuestVariableBytecodeRemapDiagnostic(
                recordType,
                recordFormId,
                scriptPath,
                readCount,
                writeCount,
                omitStaleSource,
                rewriteReason));
    }

    private static HashSet<QuestVariableRecoveryMapping> FindBundleProducerWrites(
        string recordType,
        uint recordFormId,
        string scriptPath,
        byte[]? compiledData,
        IReadOnlyList<ScriptVariableInfo> variables,
        IReadOnlyList<uint> referencedObjects,
        bool isBigEndian,
        MappingIndex index,
        IReadOnlyDictionary<uint, uint>? aliases,
        ProducerOperandIdentity operandIdentity,
        ICollection<ProducerScanRejection>? rejections = null)
    {
        void Reject(string reason, string detail) => rejections?.Add(
            new ProducerScanRejection(recordType, recordFormId, scriptPath, reason, detail));

        if (compiledData is not { Length: > 0 })
        {
            Reject("no-bytecode", "The captured bundle carries no compiled SCDA.");
            return new HashSet<QuestVariableRecoveryMapping>();
        }

        var relevantQuests = referencedObjects
            .Where(static formId => (formId & 0x80000000) == 0)
            .Select(formId => ResolveAlias(formId, aliases))
            .Where(index.QuestFormIds.Contains)
            .ToHashSet();
        if (relevantQuests.Count == 0)
        {
            Reject("no-mapping-quest-reference",
                "SCRO references no quest that owns an unsupported append-only local.");
            return new HashSet<QuestVariableRecoveryMapping>();
        }

        ScriptBytecodeWalk walk;
        try
        {
            walk = ScriptBytecodeAnalyzer.Walk(
                compiledData,
                isBigEndian,
                variables,
                referencedObjects,
                scriptPath);
        }
        catch (InvalidDataException ex)
        {
            // An undecodable bundle is not producer evidence. If another proven producer
            // keeps this mapping live, Apply will inspect this bundle again and fail closed.
            Reject("walk-exception", ex.Message);
            return new HashSet<QuestVariableRecoveryMapping>();
        }

        if (!IsCompleteWalk(walk, compiledData.Length))
        {
            Reject("incomplete-walk",
                "The bytecode walk did not fully decode (truncated/unknown-opcode/error marker).");
            return new HashSet<QuestVariableRecoveryMapping>();
        }

        if (walk.DecompiledText.Contains("GetQuestVariable", StringComparison.Ordinal)
            || walk.DecompiledText.Contains("GetScriptVariable", StringComparison.Ordinal))
        {
            Reject("unverified-variable-function-grammar",
                "The walk is complete but contains GetQuestVariable/GetScriptVariable, whose " +
                "function-parameter grammar the analyzer cannot yet structurally prove.");
            return new HashSet<QuestVariableRecoveryMapping>();
        }

        var result = new HashSet<QuestVariableRecoveryMapping>();
        var unrelatedWrites = rejections is null ? null : new List<string>();
        var rejectedBefore = 0;
        foreach (var access in walk.ExternalVariableReads)
        {
            if (!access.IsWrite)
            {
                continue;
            }

            var questFormId = ResolveAlias(access.ReferencedFormId, aliases);
            if (!index.BySource.TryGetValue(
                    new MappingKey(questFormId, access.VariableIndex),
                    out var mapping))
            {
                unrelatedWrites?.Add($"0x{questFormId:X8}[{access.VariableIndex}]");
                continue;
            }

            try
            {
                ValidateOperandIdentity(
                    recordType,
                    recordFormId,
                    scriptPath,
                    access,
                    mapping,
                    operandIdentity);
            }
            catch (InvalidDataException ex)
            {
                // A marker/type mismatch cannot qualify as an exact producer. As above,
                // Apply still rejects it if some other exact producer keeps the channel.
                Reject("operand-identity", ex.Message);
                rejectedBefore++;
                continue;
            }

            result.Add(mapping);
        }

        if (result.Count == 0 && rejectedBefore == 0)
        {
            Reject("no-matching-write",
                unrelatedWrites is { Count: > 0 }
                    ? $"The bundle writes only unmapped variables: {string.Join(", ", unrelatedWrites)}."
                    : "The bundle references a mapping quest but performs no external variable write.");
        }

        return result;
    }

    private static bool IsCompleteWalk(ScriptBytecodeWalk walk, int byteLength)
    {
        if (walk.FinalPosition < byteLength)
        {
            return false;
        }

        var text = walk.DecompiledText;
        return !text.Contains("; Truncated", StringComparison.Ordinal)
               && !text.Contains("; Error", StringComparison.Ordinal)
               && !text.Contains("; Unknown opcode", StringComparison.Ordinal)
               && !text.Contains("; Decompilation error", StringComparison.Ordinal)
               && !text.Contains("UnknownFunc_", StringComparison.Ordinal)
               && !text.Contains("<unknown", StringComparison.Ordinal)
               && !text.Contains("<truncated", StringComparison.Ordinal)
               && !text.Contains("<push:", StringComparison.Ordinal);
    }

    private static void ValidateOperandIdentity(
        string recordType,
        uint recordFormId,
        string scriptPath,
        ScriptExternalVariableRead access,
        QuestVariableRecoveryMapping mapping,
        ProducerOperandIdentity operandIdentity = ProducerOperandIdentity.Source)
    {
        if (mapping.SourceVariable.Index > ushort.MaxValue
            || mapping.TargetVariable.Index > ushort.MaxValue)
        {
            throw new InvalidDataException(
                $"Quest-local mapping {mapping.SourceVariable.Index}->{mapping.TargetVariable.Index} " +
                $"cannot be represented by UInt16 SCDA operands.");
        }

        var operandVariable = operandIdentity == ProducerOperandIdentity.Source
            ? mapping.SourceVariable
            : mapping.TargetVariable;
        var expectedMarker = operandVariable.Type == 0
            ? ScriptOpcodes.MarkerFloatLocal
            : ScriptOpcodes.MarkerIntLocal;
        var kindMatchesSerializedType = ScriptVariableDeclarationIdentity.IsStorageCompatible(
            mapping.DeclarationKind,
            operandVariable.Type);
        if (access.Marker != expectedMarker
            || mapping.TargetVariable.Type != mapping.SourceVariable.Type
            || !kindMatchesSerializedType)
        {
            throw new InvalidDataException(
                $"Refused non-exact serialized quest-local rewrite in {recordType} 0x{recordFormId:X8} " +
                $"({scriptPath}): SCDA marker 0x{access.Marker:X2}, source type " +
                $"{mapping.SourceVariable.Type}, target type {mapping.TargetVariable.Type}.");
        }
    }

    private static MappingIndex BuildMappingIndex(
        IReadOnlyList<QuestVariableRecoveryMapping> mappings,
        IReadOnlyDictionary<uint, uint>? aliases) =>
        BuildMappingIndex(mappings, aliases, ProducerOperandIdentity.Source);

    private static MappingIndex BuildMappingIndex(
        IReadOnlyList<QuestVariableRecoveryMapping> mappings,
        IReadOnlyDictionary<uint, uint>? aliases,
        ProducerOperandIdentity operandIdentity)
    {
        var bySource = new Dictionary<MappingKey, QuestVariableRecoveryMapping>();
        var questFormIds = new HashSet<uint>();
        foreach (var mapping in mappings)
        {
            if (mapping.SourceVariable.Index > ushort.MaxValue
                || mapping.TargetVariable.Index > ushort.MaxValue)
            {
                throw new InvalidDataException(
                    $"Quest-local mapping {mapping.SourceVariable.Index}->{mapping.TargetVariable.Index} " +
                    $"cannot be represented by UInt16 SCDA operands.");
            }

            var sourceQuest = ResolveAlias(mapping.SourceQuestFormId, aliases);
            var targetQuest = ResolveAlias(mapping.TargetQuestFormId, aliases);
            if (sourceQuest != targetQuest)
            {
                throw new InvalidDataException(
                    $"Quest-local mapping 0x{mapping.SourceQuestFormId:X8}->" +
                    $"0x{mapping.TargetQuestFormId:X8} does not resolve to one emitted quest identity.");
            }

            var operandQuest = operandIdentity == ProducerOperandIdentity.Source
                ? sourceQuest
                : targetQuest;
            var operandVariable = operandIdentity == ProducerOperandIdentity.Source
                ? mapping.SourceVariable
                : mapping.TargetVariable;
            var key = new MappingKey(operandQuest, checked((ushort)operandVariable.Index));
            if (bySource.TryGetValue(key, out var existing)
                && (existing.TargetVariable.Index != mapping.TargetVariable.Index
                    || existing.TargetVariable.Type != mapping.TargetVariable.Type
                    || existing.DeclarationKind != mapping.DeclarationKind
                    || !string.Equals(
                        existing.TargetVariable.Name,
                        mapping.TargetVariable.Name,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(
                    $"Ambiguous exact quest-local mapping for quest 0x{sourceQuest:X8}, " +
                    $"source ID {mapping.SourceVariable.Index}.");
            }

            bySource[key] = mapping;
            questFormIds.Add(sourceQuest);
        }

        return new MappingIndex(bySource, questFormIds);
    }

    private static bool IsMasterAnchored(
        string signature,
        uint sourceFormId,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? aliases)
    {
        var resolved = ResolveAlias(sourceFormId, aliases);
        return masterRecordsByFormId.TryGetValue(resolved, out var master)
               && master.Header.Signature == signature;
    }

    private static uint ResolveAlias(uint formId, IReadOnlyDictionary<uint, uint>? aliases)
    {
        if (formId == 0 || aliases is null)
        {
            return formId;
        }

        var current = formId;
        var visited = new HashSet<uint>();
        while (visited.Add(current)
               && aliases.TryGetValue(current, out var mapped)
               && mapped != 0
               && mapped != current)
        {
            current = mapped;
        }

        return current;
    }

    private readonly record struct MappingKey(uint QuestFormId, ushort SourceVariableIndex);

    private sealed record MappingIndex(
        IReadOnlyDictionary<MappingKey, QuestVariableRecoveryMapping> BySource,
        IReadOnlySet<uint> QuestFormIds);

    private readonly record struct BundleRewrite(
        byte[]? CompiledData,
        bool DropSourceText,
        QuestVariableBytecodeRemapDiagnostic? Diagnostic);
}
