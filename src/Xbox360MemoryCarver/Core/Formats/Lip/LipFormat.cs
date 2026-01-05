using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Lip;

/// <summary>
///     Bethesda LIP (lip-sync animation) format module.
/// </summary>
public sealed class LipFormat : FileFormatBase
{
    public override string FormatId => "lip";
    public override string DisplayName => "LIP";
    public override string Extension => ".lip";
    public override FileCategory Category => FileCategory.Audio;
    public override string OutputFolder => "lipsync";
    public override int MinSize => 20;
    public override int MaxSize => 5 * 1024 * 1024;
    public override int DisplayPriority => 1;

    // Disabled: LIP files are loaded on-demand during dialogue playback and aren't resident in crash dumps.
    // The "LIPS" magic appears in dumps only as asset path strings (e.g., "sound/voice/actor.lip"),
    // not actual lip-sync animation data. Across 50 dumps, 0 valid LIP files were found.
    public override bool ShowInFilterUI => false;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "lip",
            MagicBytes = "LIPS"u8.ToArray(),
            Description = "Lip-sync animation"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 12;
        if (data.Length < offset + minHeaderSize) return null;

        var magic = data.Slice(offset, 4);
        if (!magic.SequenceEqual("LIPS"u8)) return null;

        try
        {
            var version = BinaryUtils.ReadUInt32LE(data, offset + 4);
            if (version == 0 || version > 10) return null;

            var reportedSize = BinaryUtils.ReadUInt32LE(data, offset + 8);

            int estimatedSize;
            if (reportedSize > minHeaderSize && reportedSize < 5 * 1024 * 1024)
            {
                estimatedSize = (int)reportedSize;
            }
            else
            {
                const int minSize = 20;
                const int maxScan = 1 * 1024 * 1024;
                const int defaultSize = 64 * 1024;

                estimatedSize = SignatureBoundaryScanner.FindBoundary(
                    data, offset, minSize, maxScan, defaultSize,
                    "LIPS"u8, false);
            }

            return new ParseResult
            {
                Format = "LIP",
                EstimatedSize = estimatedSize,
                Metadata = new Dictionary<string, object>
                {
                    ["version"] = version
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LipFormat] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
