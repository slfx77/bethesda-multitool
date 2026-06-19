namespace BethesdaMultitool.Core.Formats.Esm.Localization;

/// <summary>
///     Which of a localized plugin's three string tables a given lstring subrecord resolves
///     against. Bethesda splits localized text across three files by field role.
/// </summary>
public enum LStringKind
{
    /// <summary><c>.STRINGS</c> — short null-terminated text: display names (FULL) and the like.</summary>
    Strings,

    /// <summary><c>.DLSTRINGS</c> — length-prefixed descriptions (DESC, book text, …).</summary>
    DlStrings,

    /// <summary><c>.ILSTRINGS</c> — length-prefixed dialogue response lines (INFO NAM1).</summary>
    IlStrings
}
