using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Xui;

/// <summary>
///     Xbox XUI (Xbox User Interface) format module.
///     Handles both XUIS (scene) and XUIB (binary) formats.
///     Supports conversion to readable XML via XUIHelper.
/// </summary>
public sealed class XuiFormat : FileFormatBase, IFileConverter
{
    private int _convertedCount;
    private XurSubprocessConverter? _converter;
    private int _failedCount;

    public override string FormatId => "xui";
    public override string DisplayName => "XUI";
    public override string Extension => ".xur"; // Output as .xur (binary format)
    public override FileCategory Category => FileCategory.Xbox;
    public override string OutputFolder => "xur"; // Changed to xur folder
    public override int MinSize => 24;
    public override int MaxSize => 5 * 1024 * 1024;

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
        if (data.Length < offset + minHeaderSize) return null;

        var magic = data.Slice(offset, 4);
        var isScene = magic.SequenceEqual("XUIS"u8);
        var isBinary = magic.SequenceEqual("XUIB"u8);

        if (!isScene && !isBinary) return null;

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

    #region IFileConverter

    public string TargetExtension => ".xui"; // Converted output is readable XML
    public string TargetFolder => "xui";
    public bool IsInitialized => _converter != null;
    public int ConvertedCount => _convertedCount;
    public int FailedCount => _failedCount;

    public bool Initialize(bool verbose = false, Dictionary<string, object>? options = null)
    {
        try
        {
            _converter = new XurSubprocessConverter();
            return true;
        }
        catch (FileNotFoundException ex)
        {
            Logger.Instance.Debug($"Warning: {ex.Message}");

            return false;
        }
    }

    public bool CanConvert(string signatureId, IReadOnlyDictionary<string, object>? metadata)
    {
        // XUIHelper only supports XUIB format (XUR v5/v8), not XUIS (Scene format)
        // XUIS appears to be a different scene format not supported by XUIHelper
        return signatureId == "xui_binary";
    }

    public async Task<DdxConversionResult> ConvertAsync(byte[] data,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        if (_converter == null) return new DdxConversionResult { Success = false, Notes = "Converter not initialized" };

        var result = await _converter.ConvertFromMemoryWithResultAsync(data);

        if (result.Success)
        {
            Interlocked.Increment(ref _convertedCount);
            return new DdxConversionResult
            {
                Success = true,
                DdsData = result.XuiData, // Using DdsData to hold XUI XML data
                Notes = $"XUR v{result.XurVersion} → XUI v12"
            };
        }

        Interlocked.Increment(ref _failedCount);
        return new DdxConversionResult
        {
            Success = false,
            Notes = result.Notes,
            ConsoleOutput = result.ConsoleOutput
        };
    }

    #endregion
}
