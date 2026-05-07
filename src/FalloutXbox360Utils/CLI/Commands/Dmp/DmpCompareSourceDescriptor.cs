using FalloutXbox360Utils.Core;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     CLI-facing descriptor for one source accepted by dmp compare.
/// </summary>
internal sealed record DmpCompareSourceDescriptor(
    string FilePath,
    AnalysisFileType FileType,
    DateTime LastWriteTimeUtc)
{
    public bool IsDmp => FileType == AnalysisFileType.Minidump;
    public bool IsEsm => FileType == AnalysisFileType.EsmFile;
}
