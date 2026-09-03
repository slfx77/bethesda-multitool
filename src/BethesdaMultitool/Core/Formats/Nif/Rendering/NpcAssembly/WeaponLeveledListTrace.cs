namespace BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

/// <summary>
///     Provenance for a weapon reached through a leveled item list. This is retained on
///     both successful and fail-closed preview decisions so captures can state exactly
///     which LVLI, preview level, and authored tier were used.
/// </summary>
internal sealed record WeaponLeveledListTrace(
    uint ListFormId,
    string? ListEditorId,
    byte ChanceNone,
    byte Flags,
    ushort? PreviewPlayerLevel,
    ushort? SelectedEntryLevel,
    uint? SelectedEntryFormId,
    ushort? SelectedEntryCount);
