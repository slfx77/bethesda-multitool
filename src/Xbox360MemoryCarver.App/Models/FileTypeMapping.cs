namespace Xbox360MemoryCarver.App;

/// <summary>
///     Maps display names to file signature keys for extraction filtering.
/// </summary>
public static class FileTypeMapping
{
    /// <summary>
    ///     Maps display name to signature key(s) used in file detection.
    /// </summary>
    public static readonly Dictionary<string, string[]> Mapping = new()
    {
        ["DDS"] = ["dds"],
        ["DDX (3XDO)"] = ["ddx_3xdo"],
        ["DDX (3XDR)"] = ["ddx_3xdr"],
        ["PNG"] = ["png"],
        ["XMA"] = ["xma"],
        ["NIF"] = ["nif"],
        ["Module"] = ["xex"], // Module maps to XEX executables
        ["XDBF"] = ["xdbf"],
        ["XUI"] = ["xui_scene", "xui_binary"], // XUI has two variants
        ["ESP"] = ["esp"],
        ["LIP"] = ["lip"],
        ["ObScript"] = ["script_scn"]
    };

    /// <summary>
    ///     Display names for UI checkboxes.
    /// </summary>
    public static readonly string[] DisplayNames = [.. Mapping.Keys];

    /// <summary>
    ///     Get signature keys for the given display names.
    /// </summary>
    public static IEnumerable<string> GetSignatureKeys(IEnumerable<string> displayNames)
    {
        return displayNames
            .SelectMany(name => Mapping.TryGetValue(name, out var sigKeys) ? sigKeys : []);
    }
}
