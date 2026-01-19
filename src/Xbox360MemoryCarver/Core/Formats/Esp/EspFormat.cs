using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Esp;

/// <summary>
///     Bethesda ESP/ESM (Elder Scrolls Plugin) format module.
/// </summary>
public sealed class EspFormat : FileFormatBase
{
    public override string FormatId => "esp";
    public override string DisplayName => "ESP";
    public override string Extension => ".esp";
    public override FileCategory Category => FileCategory.Plugin;
    public override string OutputFolder => "plugins";
    public override int MinSize => 24;
    public override int MaxSize => 500 * 1024 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "esp",
            MagicBytes = "TES4"u8.ToArray(),
            Description = "Elder Scrolls Plugin"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 24;
        const int hedrSubrecordSize = 18; // HEDR(4) + size(2) + data(12)
        if (data.Length < offset + minHeaderSize + hedrSubrecordSize)
        {
            return null;
        }

        var magic = data.Slice(offset, 4);
        if (!magic.SequenceEqual("TES4"u8))
        {
            return null;
        }

        try
        {
            var dataSize = BinaryUtils.ReadUInt32LE(data, offset + 4);
            var flags = BinaryUtils.ReadUInt32LE(data, offset + 8);
            var formId = BinaryUtils.ReadUInt32LE(data, offset + 12);

            // Reject if dataSize is 0 (false positive from debug strings like "TES4@I...")
            // Valid TES4 headers always have at least HEDR subrecord
            if (dataSize == 0 || dataSize > 500 * 1024 * 1024)
            {
                return null;
            }

            // Validate flags - must be reasonable (not floating point garbage)
            // Valid flags: 0x01=ESM, 0x80=Localized, 0x200=Compressed
            // High bits should not be set for valid plugin files
            if ((flags & 0xFFFFF800) != 0)
            {
                return null;
            }

            // Verify HEDR subrecord exists immediately after TES4 header
            // TES4 header: magic(4) + dataSize(4) + flags(4) + formId(4) + revision(4) + version(4) = 24 bytes
            var hedrOffset = offset + 24;
            if (!data.Slice(hedrOffset, 4).SequenceEqual("HEDR"u8))
            {
                return null;
            }

            // Validate HEDR size (should be 12 bytes for version + numRecords + nextObjectId)
            var hedrSize = BinaryUtils.ReadUInt16LE(data, hedrOffset + 4);
            if (hedrSize != 12)
            {
                return null;
            }

            var isMaster = (flags & 0x01) != 0;
            var headerDataSize = (int)dataSize + 24;
            var estimatedSize = FindEspBoundary(data, offset, headerDataSize);

            return new ParseResult
            {
                Format = isMaster ? "ESM" : "ESP",
                EstimatedSize = estimatedSize,
                Metadata = new Dictionary<string, object>
                {
                    ["dataSize"] = dataSize,
                    ["flags"] = flags,
                    ["formId"] = formId,
                    ["isMaster"] = isMaster
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EspFormat] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public override string GetDisplayDescription(string signatureId,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        if (metadata?.TryGetValue("isMaster", out var isMaster) == true && isMaster is true)
        {
            return "Elder Scrolls Master";
        }

        return "Elder Scrolls Plugin";
    }

    private static int FindEspBoundary(ReadOnlySpan<byte> data, int offset, int headerDataSize)
    {
        // ESP files can contain many internal record types, so we need to be careful
        // Start scanning from after the TES4 header data
        var scanStart = Math.Max(offset + headerDataSize, offset + 100);
        var maxScan = Math.Min(data.Length - offset, 50 * 1024 * 1024);

        // Look for another TES4 (new plugin file) or other file signatures
        for (var i = scanStart; i < offset + maxScan - 4; i++)
        {
            var slice = data.Slice(i, 4);

            // Another TES4 would indicate a new plugin file
            if (slice.SequenceEqual("TES4"u8) && i + 24 <= data.Length)
            {
                // Validate it looks like a real TES4 header
                var nextDataSize = BinaryUtils.ReadUInt32LE(data, i + 4);
                if (nextDataSize is > 0 and < 500 * 1024 * 1024)
                {
                    return i - offset;
                }
            }

            // Check for other known file signatures
            if (slice.SequenceEqual("3XDO"u8) ||
                slice.SequenceEqual("3XDR"u8) ||
                slice.SequenceEqual("RIFF"u8) ||
                slice.SequenceEqual("XEX2"u8) ||
                slice.SequenceEqual("XUIS"u8) ||
                slice.SequenceEqual("XUIB"u8) ||
                slice.SequenceEqual("XDBF"u8) ||
                slice.SequenceEqual("LIPS"u8) ||
                slice.SequenceEqual("DDS "u8) ||
                SignatureBoundaryScanner.IsPngSignature(data, i))
            {
                return i - offset;
            }

            // Check for NIF
            if (i + 20 <= data.Length && data.Slice(i, 20).SequenceEqual("Gamebryo File Format"u8))
            {
                return i - offset;
            }
        }

        // No boundary found, use header data size or cap at available data
        return Math.Min(headerDataSize, maxScan);
    }
}
