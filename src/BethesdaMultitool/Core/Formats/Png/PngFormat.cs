using BethesdaMultitool.Core.Carving;

namespace BethesdaMultitool.Core.Formats.Png;

/// <summary>
///     PNG image format module.
/// </summary>
public sealed class PngFormat : FileFormatBase, IGapAssessor
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IendMagic = "IEND"u8.ToArray();

    /// <summary>8-byte signature + the 25-byte IHDR chunk.</summary>
    private const int SignatureAndIhdrSize = 33;

    /// <inheritdoc />
    /// <remarks>
    ///     PNG image data is one zlib stream spread across IDAT chunks, so a hole anywhere in it
    ///     desynchronises inflate for the remainder of the image — there is no resynchronisation
    ///     point. Only the trailing IEND is expendable.
    /// </remarks>
    public string? AssessGaps(
        ReadOnlySpan<byte> data,
        IReadOnlyList<CarveHole> holes,
        IReadOnlyDictionary<string, object>? metadata)
    {
        if (GapAssessment.Overlaps(holes, 0, Math.Min(SignatureAndIhdrSize, data.Length)))
        {
            return "the PNG signature/IHDR (dimensions, bit depth and colour type are unreadable)";
        }

        var iendStart = Math.Max(0, data.Length - IendMagic.Length - 4);
        return GapAssessment.Overlaps(holes, SignatureAndIhdrSize, iendStart - SignatureAndIhdrSize)
            ? "the PNG image stream (one zlib stream; inflate cannot resynchronise past a hole)"
            : null;
    }

    public override string FormatId => "png";
    public override string DisplayName => "PNG";
    public override string Extension => ".png";
    public override FileCategory Category => FileCategory.Image;
    public override string OutputFolder => "images";
    public override int MinSize => 67; // Min valid PNG
    public override int MaxSize => 50 * 1024 * 1024;

    /// <summary>
    ///     Wide window so the IEND scan can find the true size of large PNGs
    ///     (the carver clamps at the containing memory region / EOF).
    /// </summary>
    public override int ParseWindowSize => 8 * 1024 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "png",
            MagicBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            Description = "PNG image"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 33)
        {
            return null;
        }

        if (!data.Slice(offset, 8).SequenceEqual(PngMagic))
        {
            return null;
        }

        try
        {
            // Try to extract dimensions from IHDR chunk (at offset 8)
            var width = 0;
            var height = 0;
            if (data.Length >= offset + 24)
            {
                // IHDR chunk: 4 bytes length, 4 bytes "IHDR", then width/height as big-endian uint32
                var ihdrData = data.Slice(offset + 16, 8);
                width = (ihdrData[0] << 24) | (ihdrData[1] << 16) | (ihdrData[2] << 8) | ihdrData[3];
                height = (ihdrData[4] << 24) | (ihdrData[5] << 16) | (ihdrData[6] << 8) | ihdrData[7];
            }

            var metadata = new Dictionary<string, object>();
            if (width > 0 && height > 0)
            {
                metadata["width"] = width;
                metadata["height"] = height;
                metadata["dimensions"] = $"{width}x{height}";
            }

            // Find IEND chunk to determine file size
            var size = FindIendChunk(data, offset);
            if (size <= 0)
            {
                // IEND may sit beyond the parse window. A valid IHDR still identifies a real
                // PNG, so estimate from the available data instead of dropping the file.
                if (!data.Slice(offset + 12, 4).SequenceEqual("IHDR"u8) || width <= 0 || height <= 0)
                {
                    return null;
                }

                size = Math.Min(data.Length - offset, MaxSize);
                metadata["boundaryFallback"] = true;
                metadata["boundaryFallbackReason"] = "IEND not found in parse window";
            }

            return new ParseResult
            {
                Format = "PNG",
                EstimatedSize = size,
                Metadata = metadata
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PngFormat] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public override string GetDisplayDescription(string signatureId,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        if (metadata?.TryGetValue("dimensions", out var dims) == true)
        {
            return $"PNG ({dims})";
        }

        return "PNG image";
    }

    private static int FindIendChunk(ReadOnlySpan<byte> data, int offset)
    {
        // Scan for IEND chunk - need 4 bytes for IEND + 4 bytes for CRC after the match position
        var maxScan = Math.Min(data.Length - offset, 50 * 1024 * 1024);

        for (var i = offset + 8; i <= offset + maxScan - 8; i++)
        {
            if (data.Slice(i, 4).SequenceEqual(IendMagic))
                // IEND chunk includes 4 byte length (before), 4 byte type, and 4 byte CRC (after)
                // The position i is at "IEND", so total size is i - offset + 4 (type) + 4 (CRC)
            {
                return i - offset + 8;
            }
        }

        return -1;
    }
}
