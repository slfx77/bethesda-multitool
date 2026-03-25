namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Holds FormID -> EditorID maps for all three ESM files in a diff.
/// </summary>
public sealed class DiffFormIdResolver
{
    public required Dictionary<uint, string> XboxMap { get; init; }
    public required Dictionary<uint, string> ConvertedMap { get; init; }
    public required Dictionary<uint, string> PcMap { get; init; }

    public string? ResolveXbox(uint formId)
    {
        return XboxMap.GetValueOrDefault(formId);
    }

    public string? ResolveConverted(uint formId)
    {
        return ConvertedMap.GetValueOrDefault(formId);
    }

    public string? ResolvePc(uint formId)
    {
        return PcMap.GetValueOrDefault(formId);
    }
}
