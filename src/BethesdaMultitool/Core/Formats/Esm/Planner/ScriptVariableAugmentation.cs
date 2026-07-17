using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Planner;

/// <summary>
///     One append-only local-variable addition required on a retained master SCPT.
///     The variable metadata comes from the DMP currently being converted; the planner
///     never looks at another dump (or recompiles the retained script) to satisfy it.
/// </summary>
internal sealed record ScriptVariableAugmentation(
    uint TargetScriptFormId,
    ScriptVariableInfo Variable,
    ScriptVariableDeclarationKind DeclarationKind);

/// <summary>
///     Promotes retained master SCPTs to narrowly-scoped overrides carrying immutable
///     append-only variable directives. Existing master bytecode, references, and local
///     indices remain writer inputs; this pass neither parses nor rewrites SCDA.
/// </summary>
internal static class ScriptVariableAugmentationPlanner
{
    internal static EmitPlan Apply(
        EmitPlan plan,
        IEnumerable<ScriptVariableAugmentation> augmentations)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(augmentations);

        var requested = augmentations
            .Distinct()
            .GroupBy(static augmentation => augmentation.TargetScriptFormId)
            .OrderBy(static group => group.Key)
            .ToArray();
        if (requested.Length == 0)
        {
            return plan;
        }

        if (!plan.Meta.PlannerCoverage.Contains("SCPT"))
        {
            throw new InvalidOperationException(
                "Script-variable augmentation requires SCPT planner coverage.");
        }

        var records = plan.Records.ToBuilder();
        var diagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>();

        foreach (var group in requested)
        {
            var targetFormId = group.Key;
            var matchingIndices = records
                .Select(static (record, index) => (record, index))
                .Where(candidate => candidate.record.FormId == targetFormId
                                    && candidate.record.Type == "SCPT")
                .Select(static candidate => candidate.index)
                .ToArray();
            if (matchingIndices.Length != 1)
            {
                throw new InvalidOperationException(
                    matchingIndices.Length == 0
                        ? $"Cannot augment SCPT 0x{targetFormId:X8}: it is absent from the emit plan."
                        : $"Cannot augment SCPT 0x{targetFormId:X8}: the emit plan contains duplicate targets.");
            }

            var index = matchingIndices[0];
            var record = records[index];
            if (record.Master is null || record.Master.Header.Signature != "SCPT")
            {
                throw new InvalidOperationException(
                    $"Cannot augment SCPT 0x{targetFormId:X8}: no master SCPT is attached to the plan.");
            }

            if (record.Disposition is RecordDisposition.New or RecordDisposition.Skip)
            {
                throw new InvalidOperationException(
                    $"Cannot augment SCPT 0x{targetFormId:X8} with disposition {record.Disposition}; "
                    + "append-only augmentation is only valid for retained master scripts.");
            }

            var combined = record.ScriptVariableAugmentations
                .AddRange(group)
                .Distinct()
                .OrderBy(static augmentation => augmentation.Variable.Index)
                .ThenBy(static augmentation => augmentation.Variable.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static augmentation => augmentation.Variable.Name, StringComparer.Ordinal)
                .ThenBy(static augmentation => augmentation.Variable.Type)
                .ToImmutableArray();

            Validate(record.Master, targetFormId, combined);

            records[index] = record with
            {
                Disposition = RecordDisposition.Override,
                ScriptVariableAugmentations = combined,
                Provenance = new PlanProvenance
                {
                    PolicyId = "ScriptVariableAugmentationPlanner.AppendOnlyMasterLocal",
                    Reason = $"Retained master SCPT receives {combined.Length} fresh local variable(s) "
                             + "needed by recovered INFO/PACK conditions; SCDA and existing indices stay unchanged.",
                },
            };
            diagnostics.Add(new PlanDiagnostic
            {
                Kind = PlanDiagnosticKind.Decision,
                Phase = "References",
                Code = "script-variable.append-master-local",
                RecordType = "SCPT",
                FormId = targetFormId,
                Message = $"Added {combined.Length} append-only local variable(s) to retained master SCPT "
                          + $"0x{targetFormId:X8}; preserved master bytecode and existing local indices.",
                Metadata = new Dictionary<string, string?>
                {
                    ["variableCount"] = combined.Length.ToString(),
                    ["variableIds"] = string.Join(",", combined.Select(static value => value.Variable.Index)),
                    ["variableNames"] = string.Join(",", combined.Select(static value => value.Variable.Name)),
                    ["sctxPolicy"] = record.Master.Subrecords.All(static subrecord =>
                        subrecord.Signature != "SCTX")
                        ? "absent-no-master-source"
                        : "master-plus-fresh-local-declarations",
                },
            });
        }

        return plan with
        {
            Records = records.ToImmutable(),
            Diagnostics = plan.Diagnostics.AddRange(diagnostics),
        };
    }

    /// <summary>
    ///     Validate every condition settled by the planner so the writer only executes a
    ///     byte-level directive. Kept internal for a defensive re-check in the encoder.
    /// </summary>
    internal static void Validate(
        Parsing.ParsedMainRecord master,
        uint targetFormId,
        IReadOnlyList<ScriptVariableAugmentation> augmentations)
    {
        if (augmentations.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot augment SCPT 0x{targetFormId:X8} without any local variables.");
        }

        var schr = master.Subrecords.Where(static subrecord => subrecord.Signature == "SCHR").ToArray();
        if (schr.Length != 1 || schr[0].Data.Length < 16)
        {
            throw new InvalidOperationException(
                $"Cannot augment SCPT 0x{targetFormId:X8}: expected one canonical SCHR of at least 16 bytes.");
        }

        var sctxCount = master.Subrecords.Count(static subrecord => subrecord.Signature == "SCTX");
        if (sctxCount > 1)
        {
            throw new InvalidOperationException(
                $"Cannot augment SCPT 0x{targetFormId:X8}: expected at most one master SCTX, found "
                + $"{sctxCount}. Source is not borrowed from another dump.");
        }

        var existingIds = new HashSet<uint>();
        var seenReference = false;
        foreach (var subrecord in master.Subrecords)
        {
            if (subrecord.Signature is "SCRO" or "SCRV")
            {
                seenReference = true;
                continue;
            }

            if (subrecord.Signature != "SLSD")
            {
                continue;
            }

            if (seenReference)
            {
                throw new InvalidOperationException(
                    $"Cannot augment SCPT 0x{targetFormId:X8}: master SLSD occurs after SCRO/SCRV.");
            }

            if (subrecord.Data.Length < 4)
            {
                throw new InvalidOperationException(
                    $"Cannot augment SCPT 0x{targetFormId:X8}: master SLSD is shorter than four bytes.");
            }

            existingIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data));
        }

        var highestExistingId = existingIds.Count == 0 ? 0u : existingIds.Max();
        var requestedIds = new HashSet<uint>();
        foreach (var augmentation in augmentations)
        {
            var variable = augmentation.Variable;
            if (augmentation.TargetScriptFormId != targetFormId)
            {
                throw new InvalidOperationException(
                    $"SCPT 0x{targetFormId:X8} received a directive for "
                    + $"0x{augmentation.TargetScriptFormId:X8}.");
            }

            if (variable.Index == 0 || variable.Index <= highestExistingId || existingIds.Contains(variable.Index))
            {
                throw new InvalidOperationException(
                    $"Cannot augment SCPT 0x{targetFormId:X8}: variable {variable.Name ?? "<unnamed>"} "
                    + $"uses non-fresh index {variable.Index}; highest master index is {highestExistingId}.");
            }

            if (!requestedIds.Add(variable.Index))
            {
                throw new InvalidOperationException(
                    $"Cannot augment SCPT 0x{targetFormId:X8}: duplicate fresh variable index {variable.Index}.");
            }

            if (string.IsNullOrWhiteSpace(variable.Name)
                || variable.Name.IndexOfAny(['\0', '\r', '\n']) >= 0
                || variable.Name.Any(static character => character > byte.MaxValue))
            {
                throw new InvalidOperationException(
                    $"Cannot augment SCPT 0x{targetFormId:X8}: variable index {variable.Index} "
                    + "has an invalid source identifier.");
            }

            if (variable.Type is not (0 or 1))
            {
                throw new InvalidOperationException(
                    $"Cannot augment SCPT 0x{targetFormId:X8}: variable {variable.Name} has unsupported "
                    + $"SLSD integer flag {variable.Type}.");
            }

            if (!ScriptVariableDeclarationIdentity.IsStorageCompatible(
                    augmentation.DeclarationKind,
                    variable.Type))
            {
                throw new InvalidOperationException(
                    $"Cannot augment SCPT 0x{targetFormId:X8}: variable {variable.Name} has "
                    + $"lexical kind {augmentation.DeclarationKind} incompatible with SLSD integer flag "
                    + $"{variable.Type}.");
            }
        }

        var originalCount = BinaryPrimitives.ReadUInt32LittleEndian(schr[0].Data.AsSpan(12, 4));
        _ = checked(originalCount + (uint)augmentations.Count);
    }
}
