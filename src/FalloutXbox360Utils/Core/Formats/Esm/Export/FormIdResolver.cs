namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Unified FormID resolution: wraps EditorID + DisplayName + Ref→Base dictionaries
///     into a single object for consistent name resolution across reports and GUI.
/// </summary>
public sealed class FormIdResolver
{
    /// <summary>An empty resolver for contexts where no data is available.</summary>
    public static FormIdResolver Empty { get; } = new([], [], []);

    /// <summary>Backward-compatible constructor (no Ref→Base data).</summary>
    public FormIdResolver(
        Dictionary<uint, string> editorIds,
        Dictionary<uint, string> displayNames)
        : this(editorIds, displayNames, [])
    {
    }

    public FormIdResolver(
        Dictionary<uint, string> editorIds,
        Dictionary<uint, string> displayNames,
        Dictionary<uint, uint> refToBase)
    {
        EditorIds = editorIds;
        DisplayNames = displayNames;
        RefToBase = refToBase;
    }

    /// <summary>The underlying EditorID dictionary.</summary>
    public Dictionary<uint, string> EditorIds { get; }

    /// <summary>The underlying DisplayName dictionary.</summary>
    public Dictionary<uint, string> DisplayNames { get; }

    /// <summary>Reference FormID → Base object FormID mapping (REFR/ACHR/ACRE → base record).</summary>
    public Dictionary<uint, uint> RefToBase { get; }

    #region Core Lookups

    /// <summary>Returns the EditorID for a FormID, or null.</summary>
    public string? GetEditorId(uint formId)
    {
        return EditorIds.GetValueOrDefault(formId);
    }

    /// <summary>Returns the display name for a FormID, or null.</summary>
    public string? GetDisplayName(uint formId)
    {
        return DisplayNames.GetValueOrDefault(formId);
    }

    /// <summary>Returns the best available name: DisplayName > EditorID > null.</summary>
    public string? GetBestName(uint formId)
    {
        return DisplayNames.GetValueOrDefault(formId) ?? EditorIds.GetValueOrDefault(formId);
    }

    #endregion

    #region Reference Resolution

    /// <summary>Returns the base object FormID for a placed reference, or null if not a reference.</summary>
    public uint? GetBaseFormId(uint refFormId)
    {
        return RefToBase.TryGetValue(refFormId, out var baseId) ? baseId : null;
    }

    /// <summary>Returns true if the given FormID is a placed reference (REFR/ACHR/ACRE).</summary>
    public bool IsReference(uint formId)
    {
        return RefToBase.ContainsKey(formId);
    }

    /// <summary>
    ///     Returns the best name, chaining through Ref→Base if direct lookup fails.
    ///     For a reference with no direct name, resolves the base object's name instead.
    /// </summary>
    public string? GetBestNameWithRefChain(uint formId)
    {
        var directName = GetBestName(formId);
        if (directName != null)
        {
            return directName;
        }

        if (RefToBase.TryGetValue(formId, out var baseFormId))
        {
            return GetBestName(baseFormId);
        }

        return null;
    }

    #endregion

    #region Formatting

    /// <summary>Formats "EditorId (0x00123456)" or just "0x00123456".</summary>
    public string FormatWithEditorId(uint formId)
    {
        return Fmt.FIdWithName(formId, EditorIds);
    }

    /// <summary>
    ///     Formats "DisplayName - EditorId (0xFormID)" with all available info.
    ///     Falls back to fewer fields when data is missing.
    /// </summary>
    public string FormatFull(uint formId)
    {
        var editorId = EditorIds.GetValueOrDefault(formId);
        var displayName = DisplayNames.GetValueOrDefault(formId);

        if (editorId != null && displayName != null)
        {
            return $"{displayName} - {editorId} ({Fmt.FIdAlways(formId)})";
        }

        if (editorId != null)
        {
            return $"{editorId} ({Fmt.FIdAlways(formId)})";
        }

        if (displayName != null)
        {
            return $"{displayName} ({Fmt.FIdAlways(formId)})";
        }

        return Fmt.FIdAlways(formId);
    }

    /// <summary>Resolves EditorID or falls back to formatted FormID hex.</summary>
    public string ResolveEditorId(uint formId)
    {
        return EditorIds.TryGetValue(formId, out var name) ? name : Fmt.FIdAlways(formId);
    }

    /// <summary>Resolves display name or falls back to "(none)".</summary>
    public string ResolveDisplayName(uint formId)
    {
        return DisplayNames.TryGetValue(formId, out var name) ? name : "(none)";
    }

    /// <summary>Resolves EditorID for CSV: EditorID or empty string.</summary>
    public string ResolveCsv(uint formId)
    {
        return Fmt.Resolve(formId, EditorIds);
    }

    /// <summary>Resolves display name for CSV: display name or empty string.</summary>
    public string ResolveDisplayNameCsv(uint formId)
    {
        return formId != 0 && DisplayNames.TryGetValue(formId, out var name) ? name : "";
    }

    #endregion
}
