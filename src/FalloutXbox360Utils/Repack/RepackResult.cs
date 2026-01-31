namespace FalloutXbox360Utils.Repack;

/// <summary>
///     Result of the repacking process.
/// </summary>
public sealed record RepackResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int VideoFilesProcessed { get; set; }
    public int MusicFilesProcessed { get; set; }
    public int BsaFilesProcessed { get; set; }
    public int EsmFilesProcessed { get; set; }
    public int EspFilesProcessed { get; set; }
    public int IniFilesProcessed { get; set; }

    public int TotalFilesProcessed =>
        VideoFilesProcessed + MusicFilesProcessed + BsaFilesProcessed + EsmFilesProcessed + EspFilesProcessed +
        IniFilesProcessed;
}
