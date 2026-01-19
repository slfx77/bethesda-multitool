using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Lip;

/// <summary>
///     Bethesda LIP (lip-sync animation) format module.
/// </summary>
/// <remarks>
///     LIP files do NOT have a "LIPS" magic header. They start with:
///     - Version (uint32, typically 1)
///     - DataSize (uint32)
///     - Unknown (uint32)
///     - Phoneme count/data
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
    public override string OutputFolder => "lipsync";
    public override int MinSize => 20;
    public override int MaxSize => 5 * 1024 * 1024;

    // DISABLED: LIP files have no magic header. The previous "LIPS" signature was matching
    // asset path strings, not actual lip-sync files. Real LIP files start with version bytes.
    public override bool EnableSignatureScanning => false;
    public override bool ShowInFilterUI => false;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        // No reliable signature - LIP files don't have magic bytes
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        // LIP files have no magic header, so we can't reliably parse them from memory dumps.
        // This parser exists only for potential future use with known file offsets.
        const int minHeaderSize = 12;
        if (data.Length < offset + minHeaderSize)
        {
            return null;
        }

        // LIP format (based on actual files):
        // 0x00: Version (uint32, typically 1)
        // 0x04: DataSize (uint32)
        // 0x08: Unknown (uint32)
        // 0x0C: Phoneme data...

        var version = BinaryUtils.ReadUInt32LE(data, offset);
        if (version == 0 || version > 10)
        {
            return null;
        }

        var dataSize = BinaryUtils.ReadUInt32LE(data, offset + 4);
        if (dataSize == 0 || dataSize > 1 * 1024 * 1024)
        {
            return null;
        }

        // Estimate size as header + reported data size
        var estimatedSize = 12 + (int)dataSize;
        if (estimatedSize > MaxSize)
        {
            return null;
        }

        return new ParseResult
        {
            Format = "LIP",
            EstimatedSize = estimatedSize,
            Metadata = new Dictionary<string, object>
            {
                ["version"] = version,
                ["dataSize"] = dataSize
            }
        };
    }
}
