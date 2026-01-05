using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Xui;

/// <summary>
///     Xbox XUI (Xbox User Interface) format module.
///     Handles both XUIS (scene) and XUIB (binary) formats.
/// </summary>
public sealed class XuiFormat : FileFormatBase
{
    public override string FormatId => "xui";
    public override string DisplayName => "XUI";
    public override string Extension => ".xui";
    public override FileCategory Category => FileCategory.Xbox;
    public override string OutputFolder => "xbox";
    public override int MinSize => 24;
    public override int MaxSize => 5 * 1024 * 1024;
    public override int DisplayPriority => 3;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new() { Id = "xui_scene", MagicBytes = "XUIS"u8.ToArray(), Description = "XUI Scene" },
        new() { Id = "xui_binary", MagicBytes = "XUIB"u8.ToArray(), Description = "XUI Binary" }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 16;
        if (data.Length < offset + minHeaderSize) return null;

        var magic = data.Slice(offset, 4);
        var isScene = magic.SequenceEqual("XUIS"u8);
        var isBinary = magic.SequenceEqual("XUIB"u8);

        if (!isScene && !isBinary) return null;

        try
        {
            var version = BinaryUtils.ReadUInt32BE(data, offset + 4);
            var fileSize = BinaryUtils.ReadUInt32BE(data, offset + 8);

            if (fileSize < minHeaderSize || fileSize > 10 * 1024 * 1024)
            {
                fileSize = BinaryUtils.ReadUInt32LE(data, offset + 8);
                if (fileSize < minHeaderSize || fileSize > 10 * 1024 * 1024)
                {
                    const int minSize = 1024;
                    const int maxScan = 5 * 1024 * 1024;
                    const int defaultSize = 256 * 1024;

                    var excludeSig = isScene ? "XUIS"u8 : "XUIB"u8;
                    fileSize = (uint)SignatureBoundaryScanner.FindBoundary(
                        data, offset, minSize, maxScan, defaultSize,
                        excludeSig, false);
                }
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
