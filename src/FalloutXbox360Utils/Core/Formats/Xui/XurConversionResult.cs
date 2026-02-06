namespace FalloutXbox360Utils.Core.Formats.Xui;

/// <summary>
///     Result of XUR to XUI conversion.
/// </summary>
public class XurConversionResult
{
    public bool Success { get; init; }
    public byte[]? XuiData { get; init; }
    public int XurVersion { get; init; }
    public string? Notes { get; init; }
    public string? ConsoleOutput { get; init; }

    public static XurConversionResult Failure(string message)
    {
        return new XurConversionResult { Success = false, Notes = message };
    }
}
