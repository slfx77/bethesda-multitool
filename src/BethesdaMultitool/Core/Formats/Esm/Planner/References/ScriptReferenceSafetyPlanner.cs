using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.References;

/// <summary>
///     Removes new SCPT records whose fixed-size bytecode reference table cannot be emitted
///     intact. FNV disables an entire script when any SCRO is null/dangling or an SCRV names
///     no SLSD local, so dropping one slot is not a safe degradation.
/// </summary>
internal static class ScriptReferenceSafetyPlanner
{
    internal sealed record Result(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> Decisions,
        ImmutableDictionary<uint, uint> SourceToEmitted,
        ImmutableHashSet<uint> EmittedFormIds,
        ImmutableDictionary<int, ImmutableArray<ResolvedRef>> References,
        ImmutableArray<PlanDiagnostic> Diagnostics);

    public static Result Apply(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> decisions,
        ImmutableDictionary<uint, uint> sourceToEmitted,
        ImmutableHashSet<uint> emittedFormIds,
        ImmutableDictionary<int, ImmutableArray<ResolvedRef>> references,
        ReferenceResolver resolver)
    {
        var unsafeIndices = new Dictionary<int, string>();
        var unsafeFormIds = new HashSet<uint>();

        for (var i = 0; i < decisions.Count; i++)
        {
            var (entry, decision) = decisions[i];
            if (entry.Type != "SCPT"
                || decision.Disposition != RecordDisposition.New
                || entry.Model is not ScriptRecord script)
            {
                continue;
            }

            var issues = FindIssues(script, references.GetValueOrDefault(i));
            if (issues.Count == 0)
            {
                continue;
            }

            unsafeIndices[i] = string.Join(", ", issues);
            var emitted = entry.DmpFormId is { } source
                          && sourceToEmitted.TryGetValue(source, out var allocated)
                ? allocated
                : entry.DmpFormId ?? 0u;
            if (emitted != 0)
            {
                unsafeFormIds.Add(emitted);
            }
        }

        if (unsafeIndices.Count == 0)
        {
            return new Result(
                decisions, sourceToEmitted, emittedFormIds, references,
                ImmutableArray<PlanDiagnostic>.Empty);
        }

        var rewritten = decisions.ToArray();
        var diagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>(unsafeIndices.Count);
        foreach (var (index, issue) in unsafeIndices)
        {
            var (entry, decision) = rewritten[index];
            rewritten[index] = (entry, decision with
            {
                Disposition = RecordDisposition.Skip,
                Provenance = new PlanProvenance
                {
                    PolicyId = "ScriptReferenceSafetyPlanner.UnsafeReferenceTable",
                    Reason = $"New SCPT reference table is not loadable: {issue}.",
                },
            });
            diagnostics.Add(new PlanDiagnostic
            {
                Kind = PlanDiagnosticKind.Warning,
                Phase = "References",
                Code = "script.suppress-unsafe-reference-table",
                RecordType = "SCPT",
                FormId = entry.DmpFormId,
                Message = $"Suppressed new SCPT 0x{entry.DmpFormId ?? 0:X8}: {issue}.",
            });
        }

        var prunedAliases = sourceToEmitted
            .Where(pair => !unsafeFormIds.Contains(pair.Value))
            .ToImmutableDictionary();
        var prunedEmitSet = emittedFormIds.Except(unsafeFormIds);
        var resolvedAgain = resolver.ResolveAll(rewritten, prunedEmitSet, prunedAliases);
        return new Result(
            rewritten, prunedAliases, prunedEmitSet, resolvedAgain, diagnostics.ToImmutable());
    }

    public static ImmutableHashSet<uint> BuildValidScriptFormIds(
        IReadOnlyList<ParsedMainRecord> masterRecords,
        ImmutableArray<RecordPlan> plannedRecords)
    {
        return masterRecords
            .Where(record => record.Header.Signature == "SCPT")
            .Select(record => record.Header.FormId)
            .Concat(plannedRecords.Where(record => record.Type == "SCPT").Select(record => record.FormId))
            .ToImmutableHashSet();
    }

    private static List<string> FindIssues(
        ScriptRecord script,
        ImmutableArray<ResolvedRef> references)
    {
        var issues = references
            .Where(reference => reference.FieldPath.StartsWith("SCRO[", StringComparison.Ordinal)
                                && (reference.Action != ResolvedRefAction.Resolved
                                    || reference.FinalFormId is null or 0u))
            .Select(reference => $"{reference.FieldPath}=0x{reference.OriginalFormId ?? 0:X8} unresolved")
            .ToList();
        var variableIds = script.Variables.Select(variable => variable.Index).ToHashSet();
        for (var i = 0; i < script.ReferencedObjects.Count; i++)
        {
            var raw = script.ReferencedObjects[i];
            if ((raw & 0x80000000u) == 0)
            {
                continue;
            }

            var variableId = raw & 0x7FFFFFFFu;
            if (variableId == 0 || !variableIds.Contains(variableId))
            {
                issues.Add($"SCRV[{i}]={variableId} has no matching SLSD");
            }
        }

        return issues;
    }
}
