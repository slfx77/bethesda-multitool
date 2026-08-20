using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.References;

/// <summary>Runs top-level and embedded script fixed-table safety as one planner phase.</summary>
internal static class ScriptReferenceSafetyPipeline
{
    internal static Result Apply(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> decisions,
        ImmutableDictionary<uint, uint> sourceToEmitted,
        ImmutableHashSet<uint> emittedFormIds,
        ImmutableDictionary<int, ImmutableArray<ResolvedRef>> references,
        ReferenceResolver resolver)
    {
        var topLevel = ScriptReferenceSafetyPlanner.Apply(
            decisions, sourceToEmitted, emittedFormIds, references, resolver);
        var inline = InlineScriptReferenceSafetyPlanner.Apply(
            topLevel.Decisions,
            topLevel.SourceToEmitted,
            topLevel.EmittedFormIds,
            topLevel.References,
            resolver);
        return new Result(
            inline.Decisions,
            inline.SourceToEmitted,
            inline.EmittedFormIds,
            inline.References,
            topLevel.Diagnostics.AddRange(inline.Diagnostics));
    }

    internal sealed record Result(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> Decisions,
        ImmutableDictionary<uint, uint> SourceToEmitted,
        ImmutableHashSet<uint> EmittedFormIds,
        ImmutableDictionary<int, ImmutableArray<ResolvedRef>> References,
        ImmutableArray<PlanDiagnostic> Diagnostics);
}
