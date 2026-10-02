using BethesdaMultitool.Core.Media.Audio.Lip;

namespace BethesdaMultitool.Core.Formats.Lip;

/// <summary>
///     Bethesda LIP (lip-sync animation) format module.
/// </summary>
/// <remarks>
///     LIP files do NOT have a "LIPS" magic header. They start with:
///     - Version (uint32, typically 1)
///     - Declared size (uint32, not compressed file length)
///     - Flags (uint32)
///     - A byte-compressed sample body in the verified FO3/FNV variant
///     The "LIPS" string appears in memory dumps only as part of asset path strings
///     (e.g., "sound/voice/falloutnv.esm/maleadult01/lips_....lip"), not as file headers.
///     Across 50+ crash dumps analyzed, 0 valid LIP files were found - they are loaded
///     on-demand during dialogue playback and aren't resident in crash dumps.
///     This format is DISABLED for signature scanning since there's no reliable magic
///     to detect actual LIP files vs. path strings containing "lip".
/// </remarks>
public sealed class LipFormat : FileFormatBase
{
    public override string FormatId => "lip";
    public override string DisplayName => "LIP";
    public override string Extension => ".lip";
    public override FileCategory Category => FileCategory.Audio;
    public override string GroupLabel => "LIP Sync";
    public override string OutputFolder => "lipsync";
    public override int MinSize => 15;
    public override int MaxSize => 5 * 1024 * 1024;

    // DISABLED: LIP files have no magic header. The previous "LIPS" signature was matching
    // asset path strings, not actual lip-sync files. Real LIP files start with version bytes.
    public override bool EnableSignatureScanning => false;
    public override bool ShowInFilterUI => false;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        // No reliable signature - LIP files don't have magic bytes
    ];

    /// <summary>Validates a complete known file instead of estimating compressed length from the size field.</summary>
    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (offset < 0 || offset > data.Length)
        {
            return null;
        }
        try
        {
            var timeline = LipDecoder.Decode(data[offset..]);
            return new ParseResult
            {
                Format = "LIP",
                EstimatedSize = timeline.EncodedSize,
                Metadata = new Dictionary<string, object>
                {
                    ["version"] = timeline.Revision,
                    ["declaredSize"] = timeline.DeclaredSize,
                    ["flags"] = timeline.Flags,
                    ["frameCount"] = timeline.FrameCount,
                    ["startingFrame"] = timeline.StartingFrame,
                    ["framesPerSecond"] = timeline.FramesPerSecond,
                    ["trackCount"] = LipTimeline.Tracks.Count
                }
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }
}
