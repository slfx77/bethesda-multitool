using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Xui;

/// <summary>
///     Xbox XUI (Xbox User Interface) format module.
///     Handles both XUIS (scene) and XUIB (binary) formats.
///     Signature scanning is disabled — XUI files are identified but not carved.
/// </summary>
public sealed class XuiFormat : FileFormatBase
{
    public override string FormatId => "xui";
    public override string DisplayName => "XUI";
    public override string Extension => ".xur";
    public override FileCategory Category => FileCategory.Xbox;
    public override string OutputFolder => "xur";
    public override int MinSize => 24;
    public override int MaxSize => 5 * 1024 * 1024;
    public override bool EnableSignatureScanning => false;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new() { Id = "xui_scene", MagicBytes = "XUIS"u8.ToArray(), Description = "XUI Scene" },
        new() { Id = "xui_binary", MagicBytes = "XUIB"u8.ToArray(), Description = "XUR Binary" }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        // XUR header structure (big-endian):
        // Offset 0: Magic (4 bytes) - "XUIB" or "XUIS"
        // Offset 4: Version (4 bytes) - 5 for XUR5, 8 for XUR8
        // Offset 8: Flags (4 bytes)
        // Offset 12: ToolVersion (2 bytes)
        // Offset 14: FileSize (4 bytes) - total file size
        // Offset 18: SectionsCount (2 bytes)
        const int minHeaderSize = 20;
        if (data.Length < offset + minHeaderSize)
        {
            return null;
        }

        var magic = data.Slice(offset, 4);
        var isScene = magic.SequenceEqual("XUIS"u8);
        var isBinary = magic.SequenceEqual("XUIB"u8);

        if (!isScene && !isBinary)
        {
            return null;
        }

        try
        {
            var version = BinaryUtils.ReadUInt32BE(data, offset + 4);

            // FileSize is at offset 14 in XUR header (big-endian)
            var fileSize = BinaryUtils.ReadUInt32BE(data, offset + 14);

            // Validate file size is reasonable
            if (fileSize < minHeaderSize || fileSize > 10 * 1024 * 1024)
            {
                // Fall back to boundary scanning if header size seems wrong
                const int minSize = 128;
                const int maxScan = 5 * 1024 * 1024;
                const int defaultSize = 64 * 1024;

                var excludeSig = isScene ? "XUIS"u8 : "XUIB"u8;
                fileSize = (uint)SignatureBoundaryScanner.FindBoundary(
                    data, offset, minSize, maxScan, defaultSize,
                    excludeSig, false);
            }

            return new ParseResult
            {
                Format = isScene ? "XUI Scene" : "XUI Binary",
                EstimatedSize = (int)fileSize,
                Metadata = new Dictionary<string, object>
                {
                    ["version"] = version,
                    ["isScene"] = isScene
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XuiFormat] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public override string GetDisplayDescription(string signatureId,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        return signatureId == "xui_binary" ? "XUI Binary" : "XUI Scene";
    }
}
