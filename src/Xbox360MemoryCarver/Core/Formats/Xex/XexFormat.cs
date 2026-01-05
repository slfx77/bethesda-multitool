using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Xex;

/// <summary>
///     Xbox 360 Executable (XEX) format module.
///     Note: Module extraction from minidump metadata is preferred over signature scanning.
///     Signature scanning is disabled by default since minidump provides accurate names and sizes.
/// </summary>
public sealed class XexFormat : FileFormatBase
{
    public override string FormatId => "xex";
    public override string DisplayName => "Module";
    public override string Extension => ".xex";
    public override FileCategory Category => FileCategory.Module;
    public override string OutputFolder => "executables";
    public override int MinSize => 24;
    public override int MaxSize => 100 * 1024 * 1024;
    public override int DisplayPriority => 4;

    /// <summary>
    ///     XEX signature scanning is disabled since minidump module extraction
    ///     provides more accurate results with proper filenames and sizes.
    /// </summary>
    public override bool EnableSignatureScanning => false;

    /// <summary>
    ///     Hide from filter UI since modules are extracted from minidump metadata.
    /// </summary>
    public override bool ShowInFilterUI => false;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "xex",
            MagicBytes = "XEX2"u8.ToArray(),
            Description = "Xbox 360 Executable"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 24) return null;

        if (!data.Slice(offset, 4).SequenceEqual("XEX2"u8)) return null;

        try
        {
            var moduleFlags = BinaryUtils.ReadUInt32BE(data, offset + 0x04);
            var dataOffset = BinaryUtils.ReadUInt32BE(data, offset + 0x08);
            var securityInfoOffset = BinaryUtils.ReadUInt32BE(data, offset + 0x10);
            var optionalHeaderCount = BinaryUtils.ReadUInt32BE(data, offset + 0x14);

            if (dataOffset == 0 || dataOffset > 50 * 1024 * 1024) return null;
            if (optionalHeaderCount > 100) return null;

            var imageSize = FindImageSize(data, offset, optionalHeaderCount);
            var estimatedSize = imageSize > 0
                ? imageSize
                : (int)Math.Min(dataOffset * 4, 10 * 1024 * 1024);

            return new ParseResult
            {
                Format = "XEX2",
                EstimatedSize = estimatedSize,
                Metadata = new Dictionary<string, object>
                {
                    ["moduleFlags"] = moduleFlags,
                    ["dataOffset"] = dataOffset,
                    ["securityInfoOffset"] = securityInfoOffset,
                    ["optionalHeaderCount"] = optionalHeaderCount,
                    ["isXbox360"] = true
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XexFormat] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public override string GetDisplayDescription(string signatureId,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        if (metadata?.TryGetValue("fileName", out var fn) == true && fn is string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext == ".exe" ? "Xbox 360 Module (EXE)" : "Xbox 360 Module (DLL)";
        }

        return "Xbox 360 Executable";
    }

    private static int FindImageSize(ReadOnlySpan<byte> data, int offset, uint optionalHeaderCount)
    {
        var headerOffset = offset + 0x18;

        for (var i = 0; i < optionalHeaderCount && headerOffset + 8 <= data.Length; i++)
        {
            var headerId = BinaryUtils.ReadUInt32BE(data, headerOffset);
            var headerData = BinaryUtils.ReadUInt32BE(data, headerOffset + 4);

            // Image size header has ID 0x00010100
            if ((headerId & 0xFFFF) == 0x0100)
            {
                var imageSize = (int)headerData;
                if (imageSize > 0 && imageSize < 100 * 1024 * 1024) return imageSize;
            }

            headerOffset += 8;
        }

        return 0;
    }
}
