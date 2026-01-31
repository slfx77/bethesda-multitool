namespace FalloutXbox360Utils.Core.Formats.Scda;

/// <summary>
///     Results from scanning a dump for SCDA records.
/// </summary>
public record ScdaScanResult
{
    public List<ScdaRecord> Records { get; init; } = [];
}
