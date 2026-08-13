using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     The closing cell phases, which all depend on the per-ref emit verdicts being settled
///     and on each other in a fixed order:
///     <list type="number">
///         <item>
///             door links — which placed refs are live doors, needed before anything can
///             judge a teleport target;
///         </item>
///         <item>
///             per-subrecord link resolution, which consumes that door set (XTEL) and the
///             verdicts (everything else);
///         </item>
///         <item>
///             NVCI connectivity, which needs both the written-navmesh set and the door plan.
///         </item>
///     </list>
///     Extracted from <see cref="EsmPlanner" /> so the coupling is stated in one place rather
///     than implied by statement order inside a long build method.
/// </summary>
internal static class CellPlanFinalizer
{
    public static EmitPlan Apply(
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId)
    {
        plan = plan with { NavmDoorLinks = NavmDoorLinkPlanner.Build(plan, masterByFormId) };

        plan = plan with
        {
            CellsByFormId = PlacedRefLinkPlanner.Apply(
                plan.CellsByFormId, masterByFormId, plan.SourceToEmittedFormId,
                plan.EmittedFormIds, plan.NavmDoorLinks.ValidDoorRefFormIds),
        };

        return plan with
        {
            NavmConnectivityByFormId = PlanNavmConnectivity.Compute(
                plan.CellsByFormId, plan.EmittedNavmFormIds, masterByFormId,
                plan.SourceToEmittedFormId, plan.NavmDoorLinks, plan.EmittedFormIds),
        };
    }
}
