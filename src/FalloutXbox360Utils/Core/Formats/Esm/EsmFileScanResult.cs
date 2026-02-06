using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Comprehensive ESM file scan result.
///     Used when scanning actual ESM files (not memory dumps).
/// </summary>
public record EsmFileScanResult
{
    /// <summary>ESM file header information.</summary>
    public EsmFileHeader? Header { get; init; }

    /// <summary>Count of records by type signature.</summary>
    public Dictionary<string, int> RecordTypeCounts { get; init; } = [];

    /// <summary>Total number of records.</summary>
    public int TotalRecords { get; init; }

    /// <summary>All parsed records (for small files or when requested).</summary>
    public List<ParsedMainRecord> Records { get; init; } = [];

    /// <summary>Record info list (signature, FormID, offset) for all records.</summary>
    public List<RecordInfo> RecordInfos { get; init; } = [];

    /// <summary>FormID to EditorID mapping.</summary>
    public Dictionary<uint, string> FormIdToEditorId { get; init; } = [];

    /// <summary>Records by category.</summary>
    public Dictionary<RecordCategory, int> RecordsByCategory { get; init; } = [];
}
