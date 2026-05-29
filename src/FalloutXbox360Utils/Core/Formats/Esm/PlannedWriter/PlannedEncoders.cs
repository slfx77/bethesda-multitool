using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.SimpleRef;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.Trivial;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter;

/// <summary>
///     Central factory listing every <see cref="IPlannedRecordEncoder" /> the planner-side
///     pipeline currently supports. Each tier adds rows here as encoders ship.
/// </summary>
/// <remarks>
///     <c>PluginBuildOptions.PlannerEnabledRecordTypes</c> selects which subset of these
///     encoders are actually exercised per build. An entry here doesn't enable the planner
///     for that type — the build options do. This factory is the single source of truth for
///     "what types CAN the planner emit," used by <c>PluginBuilder</c> to detect mis-configured
///     option sets (planner-enabled type with no registered encoder).
/// </remarks>
public static class PlannedEncoders
{
    /// <summary>
    ///     Build a fresh <see cref="PlannedEncoderRegistry" />. Cheap — encoders are stateless.
    /// </summary>
    public static PlannedEncoderRegistry BuildRegistry()
    {
        return new PlannedEncoderRegistry(BuildAll());
    }

    /// <summary>
    ///     Enumerate every planned encoder. Tier 1+ extends this list.
    /// </summary>
    public static IEnumerable<IPlannedRecordEncoder> BuildAll()
    {
        // Tier 1 — trivial static-data encoders. No outgoing FormID resolution (or only
        // verbatim FormID pass-through, matching legacy behavior byte-for-byte).
        yield return new PlannedStatEncoder();
        yield return new PlannedGlobEncoder();
        yield return new PlannedGmstEncoder();
        yield return new PlannedArmoEncoder();
        yield return new PlannedAmmoEncoder();
        yield return new PlannedBookEncoder();
        yield return new PlannedAlchEncoder();

        // Tier 2 — simple FormID-ref encoders. Most emit FormIDs verbatim without
        // validation; WEAP threads the plan's emit set + remap table through to
        // its legacy EncodeNew(weap, validFormIds, remapTable) overload.
        yield return new PlannedWeapEncoder();
        yield return new PlannedDoorEncoder();
        yield return new PlannedMiscEncoder();
        yield return new PlannedKeymEncoder();
        yield return new PlannedNoteEncoder();
        yield return new PlannedRcpeEncoder();
        yield return new PlannedCobjEncoder();
        yield return new PlannedArmaEncoder();
        yield return new PlannedImodEncoder();
        yield return new PlannedEnchEncoder();
        yield return new PlannedSpelEncoder();
        yield return new PlannedExplEncoder();
        yield return new PlannedMgefEncoder();
        yield return new PlannedProjEncoder();
    }
}
