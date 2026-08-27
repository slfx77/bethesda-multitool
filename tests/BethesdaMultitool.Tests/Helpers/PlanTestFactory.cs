using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Planner;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Shared fixtures for planner tests.
///     <para>
///         The empty <see cref="EmitPlan" /> literal is twelve lines of "set every immutable
///         collection to Empty" that says nothing about any individual test, and it had been
///         copy-pasted into six planner test files under three different names
///         (<c>MakeEmptyPlan()</c>, <c>EmptyPlan()</c>, and inline). Every copy was byte-identical,
///         so each was one more place to update when <c>EmitPlan</c> gains a member — and a
///         divergence between them would be invisible.
///     </para>
/// </summary>
internal static class PlanTestFactory
{
    /// <summary>
    ///     First FormID the planner may allocate. Arbitrary but non-zero, so a test that
    ///     accidentally emits at the default 0 is distinguishable from one that allocated properly.
    /// </summary>
    public const uint DefaultNextObjectId = 0x800;

    /// <summary>
    ///     A plan with no records, no mappings and no diagnostics — the starting point for tests
    ///     that add exactly the one record they care about via <c>with</c>.
    /// </summary>
    public static EmitPlan EmptyPlan()
    {
        return new EmitPlan
        {
            Records = ImmutableArray<RecordPlan>.Empty,
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet<uint>.Empty,
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty,
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = DefaultNextObjectId,
                PlannerCoverage = ImmutableHashSet<string>.Empty
            }
        };
    }
}
