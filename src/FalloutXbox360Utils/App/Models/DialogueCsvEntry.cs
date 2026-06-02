namespace FalloutXbox360Utils;

/// <summary>
///     One row in the DMP→ESP converter's dialogue-audio CSV list. Mirrors the CLI's
///     repeatable <c>--dialogue-audio-csv</c> option: each path points at a Fallout
///     Audio Transcriber CSV export that supplies voice audio/lip requests for INFO
///     records present in the ESP or DMP. Used in tandem with asset packing and a
///     secondary data folder containing the audio.
/// </summary>
public sealed class DialogueCsvEntry
{
    public required string Path { get; init; }
}
