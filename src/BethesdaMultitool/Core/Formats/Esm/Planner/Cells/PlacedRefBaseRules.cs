using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Placed-ref base-resolution rules shared by the plan-time verdict pass
///     (<c>CellChildVerdictPlanner</c>) and the writer's transitional fallback
///     (<c>PlannedPlacedRefEncoder</c>). Single source of truth — extracted from the
///     writer so the two paths cannot drift while the migration is staged.
/// </summary>
public static class PlacedRefBaseRules
{
    /// <summary>Engine-only render-culling marker bases: MultiBound, Occlusion, Room, Portal. Collision (0x21) is kept.</summary>
    public static readonly IReadOnlySet<uint> RenderCullingEngineBases =
        new HashSet<uint> { 0x15u, 0x17u, 0x1Fu, 0x20u };

    /// <summary>
    ///     A placed ref's base must resolve at load: a master record of a compatible base
    ///     type, a FormID this plugin emits, or an engine-hardcoded form (reserved range
    ///     below 0x800 plus the runtime-state singletons). Everything else — 0xFF-range
    ///     runtime clones, proto FormIDs the final master cut — dangles.
    /// </summary>
    public static bool IsValidPlacedBase(
        string placedRecordType,
        uint baseFormId,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        IReadOnlySet<uint> emittedFormIds,
        out string? knownBaseRecordType)
    {
        knownBaseRecordType = null;

        if (baseFormId is 0 or 0xFFFFFFFFu)
        {
            return true; // Null/sentinel bases pass through, same as legacy validation.
        }

        if (masterByFormId.TryGetValue(baseFormId, out var masterRecord))
        {
            knownBaseRecordType = masterRecord.Header.Signature;
            return ReferenceBaseRemapper.CanPlacedRecordUseBaseType(
                placedRecordType, masterRecord.Header.Signature);
        }

        if (emittedFormIds.Contains(baseFormId))
        {
            return true;
        }

        return baseFormId < 0x800u
               || RuntimeStateRecordPolicy.EngineFormIds.Contains(baseFormId);
    }

    /// <summary>
    ///     Runtime-dynamic (0xFF) leveled-spawn recovery: the actor's captured
    ///     <see cref="PlacedReference.LeveledCreatureOriginalBaseFormId" /> (preferred), else
    ///     <see cref="PlacedReference.LeveledCreatureTemplateFormId" />, names the concrete base
    ///     the engine spawned it from. Accept the first candidate that resolves (through
    ///     <paramref name="sourceToEmitted" />) to a type-compatible NPC_/CREA base.
    /// </summary>
    public static bool TryRecoverLeveledSpawnBase(
        PlacedReference placed,
        string placedRecordType,
        IReadOnlyDictionary<uint, uint> sourceToEmitted,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        IReadOnlySet<uint> emittedFormIds,
        IReadOnlyDictionary<uint, string>? dmpBaseTypes,
        out uint recoveredFormId)
    {
        return TryLeveledCandidate(
                   placed.LeveledCreatureOriginalBaseFormId, placedRecordType,
                   sourceToEmitted, masterByFormId, emittedFormIds, dmpBaseTypes, out recoveredFormId)
               || TryLeveledCandidate(
                   placed.LeveledCreatureTemplateFormId, placedRecordType,
                   sourceToEmitted, masterByFormId, emittedFormIds, dmpBaseTypes, out recoveredFormId);
    }

    private static bool TryLeveledCandidate(
        uint? candidate,
        string placedRecordType,
        IReadOnlyDictionary<uint, uint> sourceToEmitted,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        IReadOnlySet<uint> emittedFormIds,
        IReadOnlyDictionary<uint, string>? dmpBaseTypes,
        out uint recoveredFormId)
    {
        recoveredFormId = 0;
        if (candidate is not { } source || source == 0 || source >= 0xFF000000u)
        {
            return false;
        }

        var resolved = sourceToEmitted.TryGetValue(source, out var emitted) ? emitted : source;

        // IsValidPlacedBase enforces CanPlacedRecordUseBaseType for master bases, so a master
        // LVLN/LVLC candidate is rejected here and we fall through to the other pointer.
        if (!IsValidPlacedBase(placedRecordType, resolved, masterByFormId, emittedFormIds,
                out var knownBaseRecordType))
        {
            return false;
        }

        // Emitted (captured-proto) bases pass IsValidPlacedBase without a type check — verify the
        // captured base type is actor-compatible so we never re-point an actor at a non-actor base.
        if (knownBaseRecordType is null
            && !(dmpBaseTypes is { } dmpTypes
                 && dmpTypes.TryGetValue(source, out var dmpType)
                 && ReferenceBaseRemapper.CanPlacedRecordUseBaseType(placedRecordType, dmpType)))
        {
            return false;
        }

        recoveredFormId = resolved;
        return true;
    }
}
