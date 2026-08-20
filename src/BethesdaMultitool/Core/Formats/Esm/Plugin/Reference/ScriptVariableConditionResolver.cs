using BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Planner;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

/// <summary>
///     Resolves GetScriptVariable's object-reference owner through its placed base and
///     attached SCPT. The captured and effective emitted chains are kept separate: actor
///     bases retain the master's SCRI, while genuinely-new bases/scripts retain their DMP
///     binding. Any chain whose effective binding depends on an unproven override decision
///     fails closed so its containing PACK can be suppressed.
/// </summary>
internal sealed class ScriptVariableConditionResolver
{
    private static readonly HashSet<string> PlacedReferenceTypes =
        new(StringComparer.Ordinal) { "REFR", "ACHR", "ACRE" };

    private static readonly HashSet<string> ActorBaseTypes =
        new(StringComparer.Ordinal) { "NPC_", "CREA" };

    private readonly Dictionary<uint, List<BaseScriptBinding>> _dmpBasesByFormId = [];
    private readonly Dictionary<uint, List<BaseScriptBinding>> _dmpBasesByResolvedFormId = [];
    private readonly Dictionary<uint, List<PlacedReference>> _dmpOwnersByFormId = [];
    private readonly Dictionary<uint, List<PlacedReference>> _dmpOwnersByResolvedFormId = [];
    private readonly Dictionary<uint, List<ScriptRecord>> _dmpScriptsByFormId = [];
    private readonly Dictionary<uint, List<ScriptRecord>> _dmpScriptsByResolvedFormId = [];

    private readonly IReadOnlyDictionary<uint, ParsedMainRecord> _masterRecords;
    private readonly Dictionary<uint, string?> _masterSourceTextByScript;
    private readonly Dictionary<uint, IReadOnlyList<ScriptVariableInfo>> _masterVariablesByScript;
    private readonly IReadOnlyDictionary<uint, uint>? _remapTable;
    private readonly Dictionary<uint, List<RuntimeScriptData>> _runtimeScriptMetadataByFormId = [];
    private readonly Dictionary<uint, List<RuntimeScriptData>> _runtimeScriptMetadataByResolvedFormId = [];

    public ScriptVariableConditionResolver(
        RecordCollection dmpRecords,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecords,
        IReadOnlyDictionary<uint, uint>? remapTable)
    {
        ArgumentNullException.ThrowIfNull(dmpRecords);
        ArgumentNullException.ThrowIfNull(masterRecords);

        _masterRecords = masterRecords;
        _remapTable = remapTable;
        _masterVariablesByScript = masterRecords.Values
            .Where(static record => record.Header.Signature == "SCPT")
            .ToDictionary(
                static record => record.Header.FormId,
                static record => (IReadOnlyList<ScriptVariableInfo>)EsmScriptBlockReader.ReadScriptVariables(
                    record.Subrecords,
                    0,
                    record.Subrecords.Count));
        _masterSourceTextByScript = masterRecords.Values
            .Where(static record => record.Header.Signature == "SCPT")
            .ToDictionary(
                static record => record.Header.FormId,
                static record => ReadSingleSourceText(record));

        foreach (var placed in dmpRecords.Cells.SelectMany(static cell => cell.PlacedObjects)
                     .Concat(dmpRecords.MapMarkers))
        {
            if (placed.FormId == 0 || !PlacedReferenceTypes.Contains(placed.RecordType))
            {
                continue;
            }

            Add(_dmpOwnersByFormId, placed.FormId, placed);
            Add(_dmpOwnersByResolvedFormId, ResolveAlias(placed.FormId), placed);
        }

        AddBaseBindings(dmpRecords);
        foreach (var script in dmpRecords.Scripts.Where(static script => script.FormId != 0))
        {
            Add(_dmpScriptsByFormId, script.FormId, script);
            Add(_dmpScriptsByResolvedFormId, ResolveAlias(script.FormId), script);
        }

        foreach (var script in dmpRecords.RuntimeScripts.Where(static script => script.FormId != 0))
        {
            Add(_runtimeScriptMetadataByFormId, script.FormId, script);
            Add(_runtimeScriptMetadataByResolvedFormId, ResolveAlias(script.FormId), script);
        }
    }

    public ScriptVariableConditionResolution Resolve(uint ownerReferenceFormId, uint variableIndex)
    {
        var owner = ResolveOwner(ownerReferenceFormId);
        if (!owner.IsResolved)
        {
            return Failure(owner.Code, owner.Message, ownerReferenceFormId, variableIndex, owner.Metadata);
        }

        var source = ResolveSourceBinding(owner.SourceBaseFormId!.Value);
        if (!source.IsResolved)
        {
            return Failure(
                source.Code,
                source.Message,
                ownerReferenceFormId,
                variableIndex,
                MergeMetadata(owner.Metadata, source.Metadata));
        }

        var target = ResolveTargetBinding(owner);
        if (!target.IsResolved)
        {
            return Failure(
                target.Code,
                target.Message,
                ownerReferenceFormId,
                variableIndex,
                MergeMetadata(owner.Metadata, source.Metadata, target.Metadata),
                source.VariableTableFormId,
                target.VariableTableFormId);
        }

        var sourceVariables = source.Variables!;
        var sourceAtIndex = sourceVariables
            .Where(variable => variable.Index == variableIndex)
            .ToList();
        if (sourceAtIndex.Count != 1 || string.IsNullOrWhiteSpace(sourceAtIndex[0].Name))
        {
            var detail = sourceAtIndex.Count > 1
                ? "the captured source SCPT has multiple locals with that numeric ID"
                : "the captured source SCPT has no uniquely named local at that numeric ID";
            return Failure(
                "script-variable.source-metadata-missing",
                $"Cannot prove GetScriptVariable owner 0x{ownerReferenceFormId:X8}, variable ID " +
                $"{variableIndex}: {detail}.",
                ownerReferenceFormId,
                variableIndex,
                MergeMetadata(owner.Metadata, source.Metadata, target.Metadata),
                source.VariableTableFormId,
                target.VariableTableFormId);
        }

        var sourceVariable = sourceAtIndex[0];
        var targetVariables = target.Variables!;
        var targetAtSameIndex = targetVariables
            .Where(variable => variable.Index == variableIndex)
            .ToList();
        var hasConcreteSourceKind = TryGetDeclarationKind(
            sourceVariable,
            source.SourceText,
            out var sourceKind);
        if (!hasConcreteSourceKind
            && targetAtSameIndex.Count == 1
            && SameSerializedIdentity(sourceVariable, targetAtSameIndex[0]))
        {
            // No lexical keyword is inferred. The condition already addresses the same
            // numeric slot, and the same-dump source table exactly agrees with the emitted
            // table on ID, name, and SLSD storage, so retaining it cannot alias another
            // local or disturb retail indices.
            return Success(
                ScriptVariableConditionResolutionKind.Valid,
                ownerReferenceFormId,
                variableIndex,
                sourceVariable,
                targetAtSameIndex[0],
                source.VariableTableFormId!.Value,
                target.VariableTableFormId!.Value,
                MergeMetadata(owner.Metadata, source.Metadata, target.Metadata));
        }

        if (!hasConcreteSourceKind)
        {
            return Failure(
                "script-variable.source-declaration-unresolved",
                $"Cannot prove GetScriptVariable owner 0x{ownerReferenceFormId:X8}, variable ID " +
                $"{variableIndex} ('{sourceVariable.Name}'): its exact short/long/int/float/reference " +
                "declaration is not available in this dump's SCTX.",
                ownerReferenceFormId,
                variableIndex,
                MergeMetadata(owner.Metadata, source.Metadata, target.Metadata),
                source.VariableTableFormId,
                target.VariableTableFormId,
                sourceVariable);
        }

        if (targetAtSameIndex.Count == 1
            && VariablesMatch(sourceVariable, sourceKind, targetAtSameIndex[0], target.SourceText))
        {
            return Success(
                ScriptVariableConditionResolutionKind.Valid,
                ownerReferenceFormId,
                variableIndex,
                sourceVariable,
                targetAtSameIndex[0],
                source.VariableTableFormId!.Value,
                target.VariableTableFormId!.Value,
                MergeMetadata(owner.Metadata, source.Metadata, target.Metadata));
        }

        var exactMatches = targetVariables.Where(targetVariable =>
                VariablesMatch(sourceVariable, sourceKind, targetVariable, target.SourceText))
            .ToList();
        if (exactMatches.Count == 1)
        {
            return Success(
                ScriptVariableConditionResolutionKind.Remap,
                ownerReferenceFormId,
                variableIndex,
                sourceVariable,
                exactMatches[0],
                source.VariableTableFormId!.Value,
                target.VariableTableFormId!.Value,
                MergeMetadata(owner.Metadata, source.Metadata, target.Metadata));
        }

        var reason = exactMatches.Count == 0
            ? "the effective target SCPT has no local with the exact captured name and declaration kind"
            : "the effective target SCPT has multiple locals with the exact captured name and declaration kind";
        return Failure(
            exactMatches.Count == 0
                ? "script-variable.exact-target-missing"
                : "script-variable.exact-target-ambiguous",
            $"Cannot safely retain GetScriptVariable owner 0x{ownerReferenceFormId:X8}, variable ID " +
            $"{variableIndex} ('{sourceVariable.Name}'): {reason}.",
            ownerReferenceFormId,
            variableIndex,
            MergeMetadata(owner.Metadata, source.Metadata, target.Metadata),
            source.VariableTableFormId,
            target.VariableTableFormId,
            sourceVariable);
    }

    private OwnerResolution ResolveOwner(uint sourceOwnerFormId)
    {
        var resolvedOwnerFormId = ResolveAlias(sourceOwnerFormId);
        var dmpOwner = FindUniqueOwner(sourceOwnerFormId, resolvedOwnerFormId, out var ownerAmbiguous);
        MasterPlacedReference? masterOwner = TryGetMasterPlacedReference(resolvedOwnerFormId, out var parsedOwner)
            ? parsedOwner
            : null;

        var ownerSource = (dmpOwner, masterOwner) switch
        {
            (not null, not null) => "dmp+master",
            (not null, null) => "dmp",
            (null, not null) => "master",
            _ => null
        };
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["script-variable-source-owner-form-id"] = FormatFormId(sourceOwnerFormId),
            ["script-variable-target-owner-form-id"] = FormatFormId(resolvedOwnerFormId),
            ["script-variable-owner-source"] = ownerSource
        };

        if (ownerAmbiguous)
        {
            return OwnerResolution.Failed(
                "script-variable.owner-ambiguous",
                $"GetScriptVariable owner 0x{sourceOwnerFormId:X8} has conflicting captured " +
                "REFR/ACHR/ACRE base bindings.",
                metadata);
        }

        if (dmpOwner is null && masterOwner is null)
        {
            return OwnerResolution.Failed(
                "script-variable.owner-unresolved",
                $"GetScriptVariable owner 0x{sourceOwnerFormId:X8} is not an available " +
                "DMP/master REFR, ACHR, or ACRE.",
                metadata);
        }

        if (dmpOwner is not null
            && masterOwner is not null
            && !string.Equals(dmpOwner.RecordType, masterOwner.Value.RecordType, StringComparison.Ordinal))
        {
            metadata["script-variable-dmp-owner-type"] = dmpOwner.RecordType;
            metadata["script-variable-master-owner-type"] = masterOwner.Value.RecordType;
            return OwnerResolution.Failed(
                "script-variable.owner-type-ambiguous",
                $"GetScriptVariable owner 0x{sourceOwnerFormId:X8} is captured as " +
                $"{dmpOwner.RecordType}, but the retained master record is {masterOwner.Value.RecordType}.",
                metadata);
        }

        var sourceBaseFormId = dmpOwner?.BaseFormId ?? masterOwner!.Value.BaseFormId;
        if (sourceBaseFormId == 0)
        {
            return OwnerResolution.Failed(
                "script-variable.base-unresolved",
                $"GetScriptVariable owner 0x{sourceOwnerFormId:X8} has no captured/master NAME base.",
                metadata);
        }

        var resolvedDmpBase = dmpOwner is null ? (uint?)null : ResolveAlias(dmpOwner.BaseFormId);
        if (masterOwner is not null
            && resolvedDmpBase.HasValue
            && resolvedDmpBase.Value != masterOwner.Value.BaseFormId)
        {
            metadata["script-variable-source-base-form-id"] = FormatFormId(dmpOwner!.BaseFormId);
            metadata["script-variable-master-base-form-id"] = FormatFormId(masterOwner.Value.BaseFormId);
            return OwnerResolution.Failed(
                "script-variable.owner-base-ambiguous",
                $"GetScriptVariable owner 0x{sourceOwnerFormId:X8} has captured base " +
                $"0x{dmpOwner.BaseFormId:X8}, but retained master owner 0x{resolvedOwnerFormId:X8} " +
                $"has base 0x{masterOwner.Value.BaseFormId:X8}; the emitted NAME choice is not proven.",
                metadata);
        }

        var targetBaseFormId = masterOwner?.BaseFormId ?? resolvedDmpBase!.Value;
        metadata["script-variable-source-base-form-id"] = FormatFormId(sourceBaseFormId);
        metadata["script-variable-target-base-form-id"] = FormatFormId(targetBaseFormId);
        return OwnerResolution.Resolved(sourceBaseFormId, targetBaseFormId, metadata);
    }

    private ScriptBindingResolution ResolveSourceBinding(uint sourceBaseFormId)
    {
        var resolvedBaseFormId = ResolveAlias(sourceBaseFormId);
        var dmpBase = FindUniqueBase(sourceBaseFormId, resolvedBaseFormId, out var baseAmbiguous);
        var masterBase = _masterRecords.GetValueOrDefault(resolvedBaseFormId);
        var metadata = BuildBindingMetadata("source", sourceBaseFormId, resolvedBaseFormId, dmpBase, masterBase);
        if (baseAmbiguous)
        {
            return ScriptBindingResolution.Failed(
                "script-variable.source-base-ambiguous",
                $"Captured base 0x{sourceBaseFormId:X8} has conflicting SCRI bindings.",
                metadata);
        }

        if (dmpBase is not null
            && masterBase is not null
            && !string.Equals(dmpBase.RecordType, masterBase.Header.Signature, StringComparison.Ordinal))
        {
            return ScriptBindingResolution.Failed(
                "script-variable.source-base-type-ambiguous",
                $"Captured base 0x{sourceBaseFormId:X8} is {dmpBase.RecordType}, but its resolved " +
                $"master base is {masterBase.Header.Signature}.",
                metadata);
        }

        var sourceScriptFormId = dmpBase?.ScriptFormId;
        if (sourceScriptFormId is not > 0)
        {
            sourceScriptFormId = ReadScri(masterBase);
        }

        if (sourceScriptFormId is not > 0)
        {
            return ScriptBindingResolution.Failed(
                "script-variable.source-script-unresolved",
                $"GetScriptVariable base 0x{sourceBaseFormId:X8} has no available captured/master SCRI.",
                metadata);
        }

        return ResolveVariableTable(
            sourceScriptFormId.Value,
            "source",
            "script-variable.source-script-unresolved",
            metadata);
    }

    private ScriptBindingResolution ResolveTargetBinding(OwnerResolution owner)
    {
        var targetBaseFormId = owner.TargetBaseFormId!.Value;
        var resolvedBaseFormId = ResolveAlias(targetBaseFormId);
        var dmpBase = FindUniqueBase(owner.SourceBaseFormId!.Value, resolvedBaseFormId, out var baseAmbiguous);
        var masterBase = _masterRecords.GetValueOrDefault(resolvedBaseFormId);
        var metadata = BuildBindingMetadata("target", targetBaseFormId, resolvedBaseFormId, dmpBase, masterBase);
        if (baseAmbiguous)
        {
            return ScriptBindingResolution.Failed(
                "script-variable.target-base-ambiguous",
                $"Effective base 0x{resolvedBaseFormId:X8} has conflicting captured SCRI bindings.",
                metadata);
        }

        if (dmpBase is not null
            && masterBase is not null
            && !string.Equals(dmpBase.RecordType, masterBase.Header.Signature, StringComparison.Ordinal))
        {
            return ScriptBindingResolution.Failed(
                "script-variable.target-base-type-ambiguous",
                $"Captured base 0x{owner.SourceBaseFormId.Value:X8} is {dmpBase.RecordType}, but its " +
                $"effective master base is {masterBase.Header.Signature}.",
                metadata);
        }

        uint? targetScriptFormId;
        if (masterBase is not null)
        {
            var masterScriptFormId = ReadScri(masterBase);
            var dmpScriptFormId = dmpBase?.ScriptFormId is > 0
                ? ResolveAlias(dmpBase.ScriptFormId.Value)
                : (uint?)null;

            // Actor SCRI is explicitly retained by the merge policy. For other master
            // bases, a differing captured SCRI could win only if that base override is
            // emitted. This pre-planning pass cannot prove the disposition, so require
            // both available bindings to agree rather than guessing.
            if (ActorBaseTypes.Contains(masterBase.Header.Signature))
            {
                targetScriptFormId = masterScriptFormId;
            }
            else if (dmpScriptFormId is > 0
                     && masterScriptFormId != dmpScriptFormId)
            {
                metadata["script-variable-master-script-form-id"] = masterScriptFormId.HasValue
                    ? FormatFormId(masterScriptFormId.Value)
                    : null;
                metadata["script-variable-dmp-script-form-id"] = FormatFormId(dmpScriptFormId.Value);
                return ScriptBindingResolution.Failed(
                    "script-variable.target-script-ambiguous",
                    $"Effective base 0x{resolvedBaseFormId:X8} has a captured SCRI that differs from " +
                    "the master (including master-without-SCRI), and its final override disposition " +
                    "is not proven.",
                    metadata);
            }
            else
            {
                targetScriptFormId = masterScriptFormId ?? dmpScriptFormId;
            }
        }
        else
        {
            targetScriptFormId = dmpBase?.ScriptFormId is > 0
                ? ResolveAlias(dmpBase.ScriptFormId.Value)
                : null;
        }

        if (targetScriptFormId is not > 0)
        {
            return ScriptBindingResolution.Failed(
                "script-variable.target-script-unresolved",
                $"Effective base 0x{resolvedBaseFormId:X8} has no proven emitted/master SCRI.",
                metadata);
        }

        return ResolveVariableTable(
            targetScriptFormId.Value,
            "target",
            "script-variable.target-script-unresolved",
            metadata);
    }

    private ScriptBindingResolution ResolveVariableTable(
        uint scriptFormId,
        string role,
        string failureCode,
        IReadOnlyDictionary<string, string?> metadata)
    {
        var resolvedScriptFormId = ResolveAlias(scriptFormId);
        var mutableMetadata = new Dictionary<string, string?>(metadata, StringComparer.Ordinal)
        {
            [$"script-variable-{role}-script-form-id"] = FormatFormId(scriptFormId),
            [$"script-variable-{role}-variable-table-form-id"] = FormatFormId(resolvedScriptFormId)
        };

        var hasRawDmpScript = _dmpScriptsByFormId.ContainsKey(scriptFormId);
        var dmpScript = FindUniqueScript(scriptFormId, resolvedScriptFormId, out var scriptAmbiguous);
        if (scriptAmbiguous)
        {
            return ScriptBindingResolution.Failed(
                failureCode,
                $"SCPT 0x{scriptFormId:X8} has conflicting captured variable tables.",
                mutableMetadata,
                resolvedScriptFormId);
        }

        var runtimeMetadata = FindUniqueRuntimeScriptMetadata(
            scriptFormId,
            resolvedScriptFormId,
            !hasRawDmpScript,
            out var runtimeMetadataInvalid);
        if (role == "source" && runtimeMetadataInvalid)
        {
            return ScriptBindingResolution.Failed(
                failureCode,
                $"SCPT 0x{scriptFormId:X8} has incomplete or conflicting same-dump runtime " +
                "variable metadata.",
                mutableMetadata,
                resolvedScriptFormId);
        }

        // A retained/master SCPT is authoritative for the emitted table. The captured
        // table remains authoritative only for source identity when its raw script FormID
        // differs from the resolved master identity.
        if (role == "target"
            && TryGetMasterScriptVariables(resolvedScriptFormId, out var masterVariables))
        {
            _masterSourceTextByScript.TryGetValue(resolvedScriptFormId, out var sourceText);
            return ScriptBindingResolution.Resolved(
                resolvedScriptFormId,
                masterVariables,
                sourceText,
                mutableMetadata);
        }

        // A clean same-object listVariables walk remains authoritative for the captured
        // numeric table even when the loaded engine zeroed the header count. Its raw SCTX
        // is declaration evidence only when standalone parsing explicitly accepted it;
        // target tables must still be proven emitted or retained from the master.
        if (role == "source" && runtimeMetadata is not null)
        {
            mutableMetadata["script-variable-source-table-source"] = "runtime-metadata";
            mutableMetadata["script-variable-source-sctx-status"] =
                runtimeMetadata.SourceTextCorrespondenceStatus.ToString();
            ScriptVariableDeclarationIdentity.TryGetAcceptedDeclarationSourceText(
                dmpScript,
                runtimeMetadata,
                out var acceptedRuntimeSourceText);
            return ScriptBindingResolution.Resolved(
                resolvedScriptFormId,
                runtimeMetadata.Variables,
                acceptedRuntimeSourceText,
                mutableMetadata);
        }

        if (dmpScript is not null)
        {
            ScriptVariableDeclarationIdentity.TryGetAcceptedDeclarationSourceText(
                dmpScript,
                null,
                out var acceptedDmpSourceText);
            return ScriptBindingResolution.Resolved(
                resolvedScriptFormId,
                dmpScript.Variables,
                acceptedDmpSourceText,
                mutableMetadata);
        }

        return ScriptBindingResolution.Failed(
            failureCode,
            role == "source"
                ? $"SCPT 0x{scriptFormId:X8} has no captured DMP variable table from which to " +
                  "prove the condition's source variable identity."
                : $"SCPT 0x{scriptFormId:X8} has no available emitted/master variable table.",
            mutableMetadata,
            resolvedScriptFormId);
    }

    private void AddBaseBindings(RecordCollection records)
    {
        foreach (var npc in records.Npcs)
        {
            AddBase(new BaseScriptBinding(npc.FormId, "NPC_", npc.Script));
        }

        foreach (var creature in records.Creatures)
        {
            AddBase(new BaseScriptBinding(creature.FormId, "CREA", creature.Script));
        }

        foreach (var activator in records.Activators)
        {
            AddBase(new BaseScriptBinding(activator.FormId, "ACTI", activator.Script));
        }

        foreach (var light in records.Lights)
        {
            AddBase(new BaseScriptBinding(light.FormId, "LIGH", light.Script));
        }

        foreach (var door in records.Doors)
        {
            AddBase(new BaseScriptBinding(door.FormId, "DOOR", door.Script));
        }

        foreach (var furniture in records.Furniture)
        {
            AddBase(new BaseScriptBinding(furniture.FormId, "FURN", furniture.Script));
        }

        foreach (var container in records.Containers)
        {
            AddBase(new BaseScriptBinding(container.FormId, "CONT", container.Script));
        }

        foreach (var terminal in records.Terminals)
        {
            AddBase(new BaseScriptBinding(terminal.FormId, "TERM", terminal.ScriptFormId));
        }
    }

    private void AddBase(BaseScriptBinding binding)
    {
        if (binding.FormId == 0)
        {
            return;
        }

        Add(_dmpBasesByFormId, binding.FormId, binding);
        Add(_dmpBasesByResolvedFormId, ResolveAlias(binding.FormId), binding);
    }

    private PlacedReference? FindUniqueOwner(uint sourceFormId, uint resolvedFormId, out bool ambiguous)
    {
        var candidates = _dmpOwnersByFormId.GetValueOrDefault(sourceFormId)
                         ?? _dmpOwnersByResolvedFormId.GetValueOrDefault(resolvedFormId);
        ambiguous = candidates is not null
                    && candidates.Select(static owner => (owner.RecordType, owner.BaseFormId)).Distinct().Count() > 1;
        return ambiguous ? null : candidates?.FirstOrDefault();
    }

    private BaseScriptBinding? FindUniqueBase(uint sourceFormId, uint resolvedFormId, out bool ambiguous)
    {
        var candidates = _dmpBasesByFormId.GetValueOrDefault(sourceFormId)
                         ?? _dmpBasesByResolvedFormId.GetValueOrDefault(resolvedFormId);
        ambiguous = candidates is not null
                    && candidates.Select(static binding => (binding.RecordType, binding.ScriptFormId)).Distinct()
                        .Count() > 1;
        return ambiguous ? null : candidates?.FirstOrDefault();
    }

    private ScriptRecord? FindUniqueScript(uint sourceFormId, uint resolvedFormId, out bool ambiguous)
    {
        var candidates = _dmpScriptsByFormId.GetValueOrDefault(sourceFormId)
                         ?? _dmpScriptsByResolvedFormId.GetValueOrDefault(resolvedFormId);
        ambiguous = candidates is not null
                    && candidates.Skip(1).Any(candidate =>
                        !ScriptVariableDeclarationIdentity.TablesEquivalent(
                            candidates[0].Variables,
                            candidates[0].SourceText,
                            candidate.Variables,
                            candidate.SourceText)
                        || candidates[0].SourceTextCorrespondenceStatus
                        != candidate.SourceTextCorrespondenceStatus
                        || candidates[0].IsIncompleteExecutableBundle
                        != candidate.IsIncompleteExecutableBundle);
        return ambiguous ? null : candidates?.FirstOrDefault();
    }

    private RuntimeScriptData? FindUniqueRuntimeScriptMetadata(
        uint sourceFormId,
        uint resolvedFormId,
        bool allowResolvedFallback,
        out bool invalid)
    {
        var candidates = _runtimeScriptMetadataByFormId.GetValueOrDefault(sourceFormId);
        if (candidates is null && allowResolvedFallback)
        {
            candidates = _runtimeScriptMetadataByResolvedFormId.GetValueOrDefault(resolvedFormId);
        }

        invalid = candidates is not null
                  && (candidates.Any(static candidate => !candidate.VariablesComplete)
                      || candidates.Skip(1).Any(candidate =>
                          !ScriptRuntimeMerger.RuntimeDataEquivalent(candidates[0], candidate)));
        return invalid ? null : candidates?.FirstOrDefault();
    }

    private bool TryGetMasterPlacedReference(uint formId, out MasterPlacedReference owner)
    {
        if (_masterRecords.TryGetValue(formId, out var record)
            && PlacedReferenceTypes.Contains(record.Header.Signature)
            && ReadName(record) is { } baseFormId)
        {
            owner = new MasterPlacedReference(record.Header.Signature, baseFormId);
            return true;
        }

        owner = default;
        return false;
    }

    private bool TryGetMasterScriptVariables(uint formId, out IReadOnlyList<ScriptVariableInfo> variables)
    {
        if (_masterRecords.TryGetValue(formId, out var record)
            && record.Header.Signature == "SCPT"
            && _masterVariablesByScript.TryGetValue(formId, out variables!))
        {
            return true;
        }

        variables = [];
        return false;
    }

    private uint ResolveAlias(uint formId)
    {
        if (_remapTable is null || formId == 0)
        {
            return formId;
        }

        var current = formId;
        var visited = new HashSet<uint>();
        while (visited.Add(current)
               && _remapTable.TryGetValue(current, out var mapped)
               && mapped != 0
               && mapped != current)
        {
            current = mapped;
        }

        return current;
    }

    private static uint? ReadName(ParsedMainRecord? record)
    {
        return ReadFormIdSubrecord(record, "NAME");
    }

    private static uint? ReadScri(ParsedMainRecord? record)
    {
        return ReadFormIdSubrecord(record, "SCRI");
    }

    private static uint? ReadFormIdSubrecord(ParsedMainRecord? record, string signature)
    {
        var subrecord = record?.Subrecords.FirstOrDefault(sub =>
            sub.Signature == signature && sub.Data.Length >= sizeof(uint));
        return subrecord is null || subrecord.DataAsFormId == 0 ? null : subrecord.DataAsFormId;
    }

    private static bool VariablesMatch(
        ScriptVariableInfo source,
        ScriptVariableDeclarationKind sourceKind,
        ScriptVariableInfo target,
        string? targetSourceText)
    {
        return source.Type == target.Type
               && string.Equals(source.Name, target.Name, StringComparison.OrdinalIgnoreCase)
               && TryGetDeclarationKind(target, targetSourceText, out var targetKind)
               && ScriptVariableDeclarationIdentity.KindsMatchExact(sourceKind, targetKind);
    }

    private static bool SameSerializedIdentity(
        ScriptVariableInfo source,
        ScriptVariableInfo target)
    {
        return source.Index == target.Index
               && source.Type == target.Type
               && string.Equals(source.Name, target.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetDeclarationKind(
        ScriptVariableInfo variable,
        string? sourceText,
        out ScriptVariableDeclarationKind kind)
    {
        if (variable.Type is not (0 or 1) || string.IsNullOrWhiteSpace(variable.Name))
        {
            kind = default;
            return false;
        }

        if (!ScriptVariableDeclarationParser.TryGetKind(sourceText, variable.Name, out kind))
        {
            return false;
        }

        return variable.Type == 1
            ? kind is ScriptVariableDeclarationKind.Short
                or ScriptVariableDeclarationKind.Long
                or ScriptVariableDeclarationKind.Int
            : kind is ScriptVariableDeclarationKind.Float or ScriptVariableDeclarationKind.Reference;
    }

    private static string? ReadSingleSourceText(ParsedMainRecord script)
    {
        var sources = script.Subrecords
            .Where(static subrecord => subrecord.Signature == "SCTX")
            .ToArray();
        return sources.Length == 1 ? sources[0].DataAsString : null;
    }

    private static Dictionary<string, string?> BuildBindingMetadata(
        string role,
        uint sourceBaseFormId,
        uint resolvedBaseFormId,
        BaseScriptBinding? dmpBase,
        ParsedMainRecord? masterBase)
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"script-variable-{role}-base-form-id"] = FormatFormId(sourceBaseFormId),
            [$"script-variable-{role}-resolved-base-form-id"] = FormatFormId(resolvedBaseFormId),
            [$"script-variable-{role}-dmp-base-type"] = dmpBase?.RecordType,
            [$"script-variable-{role}-master-base-type"] = masterBase?.Header.Signature
        };
    }

    private static Dictionary<string, string?> MergeMetadata(
        params IReadOnlyDictionary<string, string?>[] sources)
    {
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var (key, value) in source)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    private static ScriptVariableConditionResolution Success(
        ScriptVariableConditionResolutionKind kind,
        uint ownerReferenceFormId,
        uint sourceVariableIndex,
        ScriptVariableInfo sourceVariable,
        ScriptVariableInfo targetVariable,
        uint sourceScriptFormId,
        uint targetScriptFormId,
        IReadOnlyDictionary<string, string?> metadata)
    {
        return new ScriptVariableConditionResolution(
            kind,
            ownerReferenceFormId,
            sourceVariableIndex,
            sourceVariable,
            targetVariable,
            sourceScriptFormId,
            targetScriptFormId,
            kind == ScriptVariableConditionResolutionKind.Remap ? targetVariable.Index : null,
            kind == ScriptVariableConditionResolutionKind.Remap
                ? "script-variable.remapped"
                : "script-variable.valid",
            kind == ScriptVariableConditionResolutionKind.Remap
                ? $"Remapped GetScriptVariable ID {sourceVariableIndex} -> {targetVariable.Index} by " +
                  "the unique exact variable name/declaration match on the effective owner script."
                : "Retained GetScriptVariable after proving the owner/base/script chain and exact local identity.",
            metadata);
    }

    private static ScriptVariableConditionResolution Failure(
        string code,
        string message,
        uint ownerReferenceFormId,
        uint sourceVariableIndex,
        IReadOnlyDictionary<string, string?> metadata,
        uint? sourceScriptFormId = null,
        uint? targetScriptFormId = null,
        ScriptVariableInfo? sourceVariable = null)
    {
        return new ScriptVariableConditionResolution(
            ScriptVariableConditionResolutionKind.Invalid,
            ownerReferenceFormId,
            sourceVariableIndex,
            sourceVariable,
            null,
            sourceScriptFormId,
            targetScriptFormId,
            null,
            code,
            message,
            metadata);
    }

    private static void Add<T>(Dictionary<uint, List<T>> index, uint formId, T value)
    {
        if (!index.TryGetValue(formId, out var list))
        {
            list = [];
            index.Add(formId, list);
        }

        list.Add(value);
    }

    private static string FormatFormId(uint formId)
    {
        return $"0x{formId:X8}";
    }

    private sealed record BaseScriptBinding(uint FormId, string RecordType, uint? ScriptFormId);

    private readonly record struct MasterPlacedReference(string RecordType, uint BaseFormId);

    private sealed record OwnerResolution(
        bool IsResolved,
        string Code,
        string Message,
        uint? SourceBaseFormId,
        uint? TargetBaseFormId,
        IReadOnlyDictionary<string, string?> Metadata)
    {
        public static OwnerResolution Resolved(
            uint sourceBaseFormId,
            uint targetBaseFormId,
            IReadOnlyDictionary<string, string?> metadata)
        {
            return new OwnerResolution(true, string.Empty, string.Empty, sourceBaseFormId, targetBaseFormId, metadata);
        }

        public static OwnerResolution Failed(
            string code,
            string message,
            IReadOnlyDictionary<string, string?> metadata)
        {
            return new OwnerResolution(false, code, message, null, null, metadata);
        }
    }

    private sealed record ScriptBindingResolution(
        bool IsResolved,
        string Code,
        string Message,
        uint? VariableTableFormId,
        IReadOnlyList<ScriptVariableInfo>? Variables,
        string? SourceText,
        IReadOnlyDictionary<string, string?> Metadata)
    {
        public static ScriptBindingResolution Resolved(
            uint variableTableFormId,
            IReadOnlyList<ScriptVariableInfo> variables,
            string? sourceText,
            IReadOnlyDictionary<string, string?> metadata)
        {
            return new ScriptBindingResolution(true, string.Empty, string.Empty, variableTableFormId, variables,
                sourceText, metadata);
        }

        public static ScriptBindingResolution Failed(
            string code,
            string message,
            IReadOnlyDictionary<string, string?> metadata,
            uint? variableTableFormId = null)
        {
            return new ScriptBindingResolution(false, code, message, variableTableFormId, null, null, metadata);
        }
    }
}

internal enum ScriptVariableConditionResolutionKind
{
    Valid,
    Remap,
    Invalid
}

internal sealed record ScriptVariableConditionResolution(
    ScriptVariableConditionResolutionKind Kind,
    uint OwnerReferenceFormId,
    uint SourceVariableIndex,
    ScriptVariableInfo? SourceVariable,
    ScriptVariableInfo? TargetVariable,
    uint? SourceScriptFormId,
    uint? TargetScriptFormId,
    uint? RemappedIndex,
    string Code,
    string Message,
    IReadOnlyDictionary<string, string?> Metadata);
