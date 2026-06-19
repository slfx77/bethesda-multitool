namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;

/// <summary>A scanned combat-style (CSTY) record reduced to the weapon restriction it imposes.</summary>
internal sealed record CstyEntry(WeaponRestriction Restriction);
