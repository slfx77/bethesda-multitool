using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     TheStripWorld street-light compatibility policy for NEW REFR verdicts.
///     v123 force-enabled 119 disabled Strip REFRs to restore content that v122 hid.
///     Artifact tracing split that set into 98 normally-gated street-light refs, three
///     captured starter markers, and 18 genuinely parentless compatibility refs. The
///     first 101 are not undriven: retail's surviving VStreetLighting quest can drive
///     them through the parent aliases selected below, so their captured disabled state
///     is required for daytime-off behavior. Only the 18 parentless refs retain the
///     v123 force-enable fallback.
///
///     Keep this compatibility rule deliberately narrow: NEW REFRs in TheStripWorld
///     only. Opposite-state XESP children, the staged light network, actors, creatures,
///     and every other worldspace retain the capture verbatim.
/// </summary>
internal static class StripStreetLightPolicy
{
    internal const uint TheStripWorldFormId = 0x0010B96F;
    private const uint ProtoStreetLightSouthParent = 0x00127E9F;
    private const uint ProtoStreetLightFirstParent = 0x00127EA0;
    private const uint ProtoStreetLightNorthParent = 0x00127EA1;
    private const uint RetailStreetLightOneParent = 0x0013BAE3;
    private const uint RetailStreetLightTwoParent = 0x0013BAE2;
    private const uint RetailStreetLightThreeParent = 0x0013BAE1;

    public static bool SelectNewInitiallyDisabled(
        RecordPlan child,
        PlacedReference placed,
        CellPlan cellPlan)
    {
        var isStripUndrivenScenery = child.Type == "REFR"
            && cellPlan.Context.WorldspaceFormId == TheStripWorldFormId
            && placed.IsInitiallyDisabled
            && !IsProtoStreetLightParent(placed.FormId)
            && !TryMapProtoStreetLightParent(placed.EnableParentFormId, out _)
            && (placed.EnableParentFlags.GetValueOrDefault() & 0x01) == 0;

        return !isStripUndrivenScenery && placed.IsInitiallyDisabled;
    }

    /// <summary>
    ///     The prototype and retail masters share the VStreetLighting quest/script FormIDs,
    ///     but the retail script targets its renamed One/Two/Three starter refs. Point only
    ///     the prototype Strip's staged XESP children at those live retail parents. The
    ///     semantic mapping follows the scripted activation sequence:
    ///     First/South/North becomes One/Two/Three.
    /// </summary>
    public static uint? SelectNewEnableParent(PlacedReference placed, CellPlan cellPlan)
    {
        if (cellPlan.Context.WorldspaceFormId != TheStripWorldFormId)
        {
            return null;
        }

        return TryMapProtoStreetLightParent(placed.EnableParentFormId, out var retailParent)
            ? retailParent
            : null;
    }

    private static bool IsProtoStreetLightParent(uint formId) =>
        formId is ProtoStreetLightSouthParent
            or ProtoStreetLightFirstParent
            or ProtoStreetLightNorthParent;

    private static bool TryMapProtoStreetLightParent(uint? sourceParent, out uint retailParent)
    {
        retailParent = sourceParent switch
        {
            ProtoStreetLightFirstParent => RetailStreetLightOneParent,
            ProtoStreetLightSouthParent => RetailStreetLightTwoParent,
            ProtoStreetLightNorthParent => RetailStreetLightThreeParent,
            _ => 0,
        };
        return retailParent != 0;
    }
}
