using System.Collections.Frozen;

namespace EsmAnalyzer.Core;

/// <summary>
///     Known worldspace FormIDs for Fallout: New Vegas.
/// </summary>
public static class FalloutWorldspaces
{
    /// <summary>
    ///     Known worldspace names mapped to their FormIDs.
    /// </summary>
    public static readonly FrozenDictionary<string, uint> KnownWorldspaces = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        // Main game worldspaces
        ["WastelandNV"] = 0x000DA726,
        ["Wasteland"] = 0x000DA726, // Alias
        ["FreesideWorld"] = 0x00108E2D,
        ["Freeside"] = 0x00108E2D, // Alias
        ["Strip01"] = 0x00108E2E,
        ["Strip02"] = 0x00108E2F,

        // DLC worldspaces
        ["DeadMoneyWorld"] = 0x01000DA3,
        ["HonestHeartsWorld"] = 0x02000800,
        ["OWBWorld"] = 0x03000DED,
        ["LonesomeRoadWorld"] = 0x04000A1E
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Default worldspace for Fallout: New Vegas (WastelandNV).
    /// </summary>
    public const string DefaultWorldspace = "WastelandNV";

    /// <summary>
    ///     FormID of the default worldspace (WastelandNV).
    /// </summary>
    public const uint DefaultWorldspaceFormId = 0x000DA726;

    /// <summary>
    ///     Attempts to resolve a worldspace name or FormID string to a FormID.
    /// </summary>
    /// <param name="nameOrFormId">Worldspace name or FormID (hex string like "0x000DA726").</param>
    /// <param name="formId">The resolved FormID.</param>
    /// <returns>True if resolved successfully.</returns>
    public static bool TryResolveWorldspace(string? nameOrFormId, out uint formId)
    {
        if (string.IsNullOrEmpty(nameOrFormId))
        {
            formId = DefaultWorldspaceFormId;
            return true;
        }

        // Try known worldspace names first
        if (KnownWorldspaces.TryGetValue(nameOrFormId, out formId))
            return true;

        // Try parsing as hex FormID
        if (nameOrFormId.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(nameOrFormId.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out formId))
                return true;
        }
        else if (uint.TryParse(nameOrFormId, System.Globalization.NumberStyles.HexNumber, null, out formId))
        {
            return true;
        }

        formId = 0;
        return false;
    }
}
