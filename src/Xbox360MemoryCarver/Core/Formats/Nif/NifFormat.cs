using System.Text;
using System.Text.RegularExpressions;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     NetImmerse/Gamebryo (NIF) 3D model format module.
/// </summary>
public sealed partial class NifFormat : FileFormatBase
{
    public override string FormatId => "nif";
    public override string DisplayName => "NIF";
    public override string Extension => ".nif";
    public override FileCategory Category => FileCategory.Model;
    public override string OutputFolder => "models";
    public override int MinSize => 100;
    public override int MaxSize => 20 * 1024 * 1024;
    public override int DisplayPriority => 2;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "nif",
            MagicBytes = "Gamebryo File Format"u8.ToArray(),
            Description = "NetImmerse/Gamebryo 3D model"
        }
    ];

    [GeneratedRegex(@"^\d{1,2}\.\d{1,2}\.\d{1,2}\.\d{1,2}$")]
    private static partial Regex VersionPattern();

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 64) return null;

        var headerMagic = data.Slice(offset, 20);
        if (!headerMagic.SequenceEqual("Gamebryo File Format"u8)) return null;

        try
        {
            if (data.Length < offset + 50) return null;

            // Check for ", Version " after the magic
            var versionPrefixSpan = data.Slice(offset + 20, 10);
            if (!versionPrefixSpan.SequenceEqual(", Version "u8)) return null;

            var versionOffset = offset + 30;

            // Find the newline that terminates the version string
            var newlinePos = -1;
            for (var i = versionOffset; i < Math.Min(versionOffset + 20, data.Length); i++)
                if (data[i] == 0x0A)
                {
                    newlinePos = i;
                    break;
                }

            if (newlinePos == -1 || newlinePos <= versionOffset) return null;

            // Ensure we don't go out of bounds when slicing
            if (newlinePos > data.Length) return null;

            var versionString = Encoding.ASCII.GetString(data[versionOffset..newlinePos]).TrimEnd('\r');

            if (!VersionPattern().IsMatch(versionString)) return null;

            var majorVersion = 0;
            var dotIdx = versionString.IndexOf('.');
            if (dotIdx > 0 && int.TryParse(versionString[..dotIdx], out var major)) majorVersion = major;

            if (majorVersion < 2 || majorVersion > 30) return null;

            // Note: Binary version validation removed as it was causing false negatives
            // The text-based version validation is sufficient for NIF identification

            var estimatedSize = EstimateNifSize(data, offset);

            return new ParseResult
            {
                Format = "NIF",
                EstimatedSize = estimatedSize,
                Metadata = new Dictionary<string, object>
                {
                    ["version"] = versionString,
                    ["majorVersion"] = majorVersion
                }
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            // Invalid offset or data bounds - not a valid NIF
            return null;
        }
    }

    private static int EstimateNifSize(ReadOnlySpan<byte> data, int offset)
    {
        const int minSize = 100;
        const int maxScan = 5 * 1024 * 1024;
        const int defaultSize = 256 * 1024;

        return SignatureBoundaryScanner.FindBoundary(
            data, offset, minSize, maxScan, defaultSize,
            "Gamebryo File Format"u8);
    }
}
