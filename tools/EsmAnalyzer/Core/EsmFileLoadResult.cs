using FalloutXbox360Utils.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Core;

/// <summary>
///     Result of loading and validating an ESM file.
/// </summary>
public sealed class EsmFileLoadResult
{
    public required byte[] Data { get; init; }
    public required EsmFileHeader Header { get; init; }
    public required MainRecordHeader Tes4Header { get; init; }
    public required int FirstGrupOffset { get; init; }
    public required string FilePath { get; init; }
    public bool IsBigEndian => Header.IsBigEndian;
}