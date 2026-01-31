namespace FalloutXbox360Utils.Repack;

/// <summary>
///     Information about source folder contents.
/// </summary>
public sealed record SourceInfo
{
    public int VideoFiles { get; init; }
    public int MusicFiles { get; init; }
    public int BsaFiles { get; init; }
    public int EsmFiles { get; init; }
    public int EspFiles { get; init; }

    public int TotalFiles => VideoFiles + MusicFiles + BsaFiles + EsmFiles + EspFiles;
}
