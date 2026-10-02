namespace BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

/// <summary>
///     Where an NPC's displayed weapon was resolved from (AI package, best-weapon heuristic, live DMP state), or why
///     none is shown.
/// </summary>
internal enum WeaponVisualSourceKind
{
    EsmPackage,
    EsmBestWeapon,
    DmpRuntimeCurrent,
    OmittedUnequipped,
    OmittedLeveledContextRequired,
    OmittedUnresolved,

    /// <summary>Selected only from the supplied seeded preview inventory, without an observed runtime equipment claim.</summary>
    GeneratedPreviewInventory
}
