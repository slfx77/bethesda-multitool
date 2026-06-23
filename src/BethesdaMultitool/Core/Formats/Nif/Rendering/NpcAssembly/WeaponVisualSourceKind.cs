namespace BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

/// <summary>Where an NPC's displayed weapon was resolved from (AI package, best-weapon heuristic, live DMP state), or why none is shown.</summary>
internal enum WeaponVisualSourceKind
{
    EsmPackage,
    EsmBestWeapon,
    DmpRuntimeCurrent,
    OmittedUnequipped,
    OmittedUnresolved
}
