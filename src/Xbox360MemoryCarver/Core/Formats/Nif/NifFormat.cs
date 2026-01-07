using System.Text;
using System.Text.RegularExpressions;
using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     NetImmerse/Gamebryo (NIF) 3D model format module.
///     Parses NIF header structure to calculate accurate file size.
///     Attempts partial conversion of big-endian Xbox 360 NIFs.
/// </summary>
/// <remarks>
///     NIF header structure (version 20.2.0.7+, used by Fallout 3/NV):
///     - Header string: "Gamebryo File Format x.x.x.x\n"
///     - Version: uint32 (binary version, e.g., 0x14020007)
///     - Endian Type: byte (0 = big-endian/Xbox 360, 1 = little-endian)
///     - User Version: uint32
///     - Num Blocks: uint32
///     - BS Header (Bethesda): variable size
///     - Num Block Types: ushort
///     - Block Types: SizedString[]
///     - Block Type Index: ushort[Num Blocks]
///     - Block Size: uint[Num Blocks] (since 20.2.0.5)
///     - Num Strings: uint
///     - Max String Length: uint
///     - Strings: SizedString[]
///     - Num Groups: uint
///     - Groups: uint[]
///     - [Blocks data...]
///     - Footer
///     
///     XBOX 360 NIF LIMITATIONS:
///     - Xbox 360 NIFs use big-endian byte order for most data
///     - Header fields (version, userVersion, numBlocks, bsVersion) are always little-endian
///     - Post-header data (block types, sizes, block data) follows endian byte
///     - Xbox 360 NIFs may contain platform-specific blocks (e.g., BSPackedAdditionalGeometryData)
///     - Full BE to LE conversion requires per-block-type parsing (not implemented)
///     - Use tools like Noesis for better Xbox 360 NIF support
/// </remarks>
public sealed partial class NifFormat : FileFormatBase, IFileConverter
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

    // Block type names that indicate geometry meshes
    private static readonly HashSet<string> GeometryBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BSFadeNode", "NiNode", "NiTriStrips", "NiTriStripsData", "NiTriShape", "NiTriShapeData",
        "bhkRigidBody", "bhkCollisionObject", "bhkCompressedMeshShape", "bhkMoppBvTreeShape",
        "BSShaderPPLightingProperty", "BSShaderTextureSet", "NiMaterialProperty",
        "BSPackedAdditionalGeometryData", "NiSkinInstance", "NiSkinData", "NiSkinPartition"
    };

    // Block type names that indicate animation controllers
    private static readonly HashSet<string> AnimationBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "NiControllerSequence", "NiTransformInterpolator", "NiTransformData",
        "NiBSplineCompTransformInterpolator", "NiBSplineData", "NiBSplineBasisData",
        "NiTextKeyExtraData", "NiStringPalette", "NiControllerManager",
        "NiMultiTargetTransformController", "NiBlendTransformInterpolator"
    };

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
            {
                if (data[i] == 0x0A)
                {
                    newlinePos = i;
                    break;
                }
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

            // Try to parse full header structure for accurate size calculation and content type detection
            var headerInfo = ParseNifHeader(data, offset, newlinePos);

            // If ParseNifHeader returns -1, this is not a valid NIF file (e.g., false positive)
            if (headerInfo.Size < 0) return null;

            // Determine if this is animation or geometry based on block types
            var (contentType, outputFolder) = ClassifyNifContent(headerInfo.BlockTypes);

            return new ParseResult
            {
                Format = "NIF",
                EstimatedSize = headerInfo.Size,
                OutputFolderOverride = outputFolder,
                Metadata = new Dictionary<string, object>
                {
                    ["version"] = versionString,
                    ["majorVersion"] = majorVersion,
                    ["bigEndian"] = headerInfo.IsBigEndian,
                    ["contentType"] = contentType,
                    ["blockTypes"] = string.Join(", ", headerInfo.BlockTypes.Take(10))
                }
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            // Invalid offset or data bounds - not a valid NIF
            return null;
        }
    }

    /// <summary>
    ///     Classify NIF content as animation or geometry based on block types.
    /// </summary>
    private static (string contentType, string outputFolder) ClassifyNifContent(List<string> blockTypes)
    {
        var hasGeometry = blockTypes.Any(bt => GeometryBlockTypes.Contains(bt));
        var hasAnimation = blockTypes.Any(bt => AnimationBlockTypes.Contains(bt));

        if (hasGeometry && !hasAnimation)
            return ("geometry", "meshes");

        if (hasAnimation && !hasGeometry)
            return ("animation", "anims");

        if (hasGeometry && hasAnimation)
            return ("mixed", "meshes"); // Prefer meshes for mixed content

        return ("unknown", "models"); // Fallback to generic models folder
    }

    /// <summary>
    ///     Result from parsing NIF header.
    /// </summary>
    private readonly record struct NifHeaderInfo(int Size, bool IsBigEndian, List<string> BlockTypes);

    /// <summary>
    ///     Parse the NIF header to calculate accurate file size and extract block type names.
    ///     Returns NifHeaderInfo with size=-1 if the file is not a valid NIF.
    /// </summary>
    private static NifHeaderInfo ParseNifHeader(ReadOnlySpan<byte> data, int fileOffset, int newlinePos)
    {
        const int maxSize = 10 * 1024 * 1024;
        var blockTypes = new List<string>();

        try
        {
            // Position after the header string newline
            var pos = newlinePos + 1;

            if (pos + 9 > data.Length) return new NifHeaderInfo(FallbackSize(data, fileOffset), false, blockTypes);

            // Read binary version (4 bytes, always little-endian initially)
            var binaryVersion = BinaryUtils.ReadUInt32LE(data, pos);
            pos += 4;

            // Validate binary version - should be a known NIF version
            // Common versions: 0x14020007 (20.2.0.7), 0x14000005 (20.0.0.5), 0x14000004 (20.0.0.4)
            // Reject if it looks like garbage (e.g., UTF-16 text with alternating nulls)
            if (binaryVersion < 0x04000000 || binaryVersion > 0x20000000)
            {
                return new NifHeaderInfo(-1, false, []);
            }

            // Endian type (1 byte) - present in version >= 20.0.0.3
            // 0 = big-endian (Xbox 360), 1 = little-endian (PC)
            // NOTE: In Bethesda Xbox 360 NIFs, the header fields (userVersion, numBlocks, etc.)
            // are ALWAYS little-endian. Only the post-header data (NumBlockTypes, Block Types,
            // Block Sizes array) follows the endian byte.
            var endianByte = data[pos];

            // Validate endian byte - must be 0 or 1
            if (endianByte > 1)
            {
                return new NifHeaderInfo(-1, false, []);
            }
            pos += 1;

            // Endian byte determines byte order for post-header data only
            var isBigEndian = endianByte == 0;

            // User version is ALWAYS little-endian in Bethesda NIFs
            var userVersion = BinaryUtils.ReadUInt32LE(data, pos);
            pos += 4;

            // Num blocks (4 bytes) - ALWAYS little-endian in Bethesda NIFs
            if (pos + 4 > data.Length) return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);
            var numBlocks = BinaryUtils.ReadUInt32LE(data, pos);
            pos += 4;

            // Sanity check: numBlocks should be reasonable (< 100000)
            if (numBlocks == 0 || numBlocks > 100000)
            {
                return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);
            }

            // Check if this is a Bethesda file (user version indicates Bethesda stream header)
            // Bethesda versions: user version is typically 11, 12, or similar
            var hasBsHeader = IsBethesdaVersion(binaryVersion, userVersion);

            if (hasBsHeader)
            {
                // Skip BS Header (Bethesda-specific header)
                // BSStreamHeader structure:
                // - BS Version: ulittle32 (always little-endian!)
                // - Author: ExportString (1 byte length + chars including null)
                // - Unknown Int: uint (if BS Version > 130)
                // - Process Script: ExportString (if BS Version < 131)
                // - Export Script: ExportString
                // - Max Filepath: ExportString (if BS Version >= 103)

                if (pos + 4 > data.Length) return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);
                var bsVersion = BinaryUtils.ReadUInt32LE(data, pos); // Always little-endian!
                pos += 4;

                // Skip Author string (ExportString: 1 byte length + chars including null)
                if (pos + 1 > data.Length) return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);
                var authorLen = data[pos];
                pos += 1 + authorLen;

                // If bsVersion > 130, there's an unknown int
                if (bsVersion > 130)
                {
                    pos += 4;
                }

                // Skip Process Script (if BS Version < 131)
                if (bsVersion < 131)
                {
                    if (pos + 1 > data.Length) return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);
                    var processScriptLen = data[pos];
                    pos += 1 + processScriptLen;
                }

                // Skip Export Script
                if (pos + 1 > data.Length) return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);
                var exportScriptLen = data[pos];
                pos += 1 + exportScriptLen;

                // Skip Max Filepath (if BS Version >= 103)
                if (bsVersion >= 103)
                {
                    if (pos + 1 > data.Length) return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);
                    var maxFilepathLen = data[pos];
                    pos += 1 + maxFilepathLen;
                }
            }

            // Some NIF files have an extra padding byte here for alignment
            // Try to detect and skip it by checking if the next value makes sense
            if (pos + 4 <= data.Length)
            {
                var testNumBlockTypes = isBigEndian
                    ? BinaryUtils.ReadUInt16BE(data, pos)
                    : BinaryUtils.ReadUInt16LE(data, pos);

                // If the value is unreasonable, try skipping a byte (alignment padding)
                if (testNumBlockTypes == 0 || testNumBlockTypes > 500)
                {
                    // Check if skipping 1 byte gives a reasonable value
                    var testNumBlockTypes2 = isBigEndian
                        ? BinaryUtils.ReadUInt16BE(data, pos + 1)
                        : BinaryUtils.ReadUInt16LE(data, pos + 1);

                    if (testNumBlockTypes2 > 0 && testNumBlockTypes2 < 500)
                    {
                        pos += 1; // Skip the padding byte
                    }
                }
            }

            // Num Block Types (2 bytes)
            if (pos + 2 > data.Length) return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);
            var numBlockTypes = isBigEndian
                ? BinaryUtils.ReadUInt16BE(data, pos)
                : BinaryUtils.ReadUInt16LE(data, pos);
            pos += 2;

            // Sanity check
            if (numBlockTypes == 0 || numBlockTypes > 1000)
            {
                return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);
            }

            // Read Block Types (SizedString array) and collect their names
            // NIF SizedString: uint32 length + chars (no null terminator in string data)
            for (var i = 0; i < numBlockTypes; i++)
            {
                if (pos + 4 > data.Length) return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);
                var strLen = isBigEndian
                    ? BinaryUtils.ReadUInt32BE(data, pos)
                    : BinaryUtils.ReadUInt32LE(data, pos);
                pos += 4;

                // Sanity check string length
                if (strLen > 256) return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);

                // Read the block type name
                if (pos + strLen <= data.Length && strLen > 0)
                {
                    var blockTypeName = Encoding.ASCII.GetString(data.Slice(pos, (int)strLen));
                    blockTypes.Add(blockTypeName);
                }

                pos += (int)strLen;
            }

            // Skip Block Type Index (ushort[numBlocks])
            pos += (int)numBlocks * 2;

            if (pos > data.Length) return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);

            // Block Size array (uint[numBlocks]) - this is what we need!
            // Present in version 20.2.0.5+
            if (pos + numBlocks * 4 > data.Length) return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);

            long totalBlockSize = 0;
            for (var i = 0; i < numBlocks; i++)
            {
                var blockSize = isBigEndian
                    ? BinaryUtils.ReadUInt32BE(data, pos)
                    : BinaryUtils.ReadUInt32LE(data, pos);
                pos += 4;

                // Sanity check individual block size
                if (blockSize > 50 * 1024 * 1024) // 50 MB max per block
                {
                    return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);
                }

                totalBlockSize += blockSize;
            }

            // Skip Num Strings, Max String Length, Strings array
            if (pos + 8 > data.Length) return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);

            var numStrings = isBigEndian
                ? BinaryUtils.ReadUInt32BE(data, pos)
                : BinaryUtils.ReadUInt32LE(data, pos);
            pos += 4;

            // Skip Max String Length
            pos += 4;

            // Skip Strings (SizedString array)
            for (var i = 0; i < numStrings && pos + 4 <= data.Length; i++)
            {
                var strLen = isBigEndian
                    ? BinaryUtils.ReadUInt32BE(data, pos)
                    : BinaryUtils.ReadUInt32LE(data, pos);
                pos += 4;

                if (strLen > 1024) break; // Sanity check
                pos += (int)strLen;
            }

            // Skip Num Groups and Groups array
            if (pos + 4 <= data.Length)
            {
                var numGroups = isBigEndian
                    ? BinaryUtils.ReadUInt32BE(data, pos)
                    : BinaryUtils.ReadUInt32LE(data, pos);
                pos += 4;
                pos += (int)numGroups * 4;
            }

            // Calculate total size: header position + all block data + footer
            // Footer is: Num Roots (uint) + Root refs (int[Num Roots])
            // Most NIFs have 1-2 roots, so footer is typically 8-12 bytes
            var headerSize = pos - fileOffset;
            var footerEstimate = 8; // Minimum: 4 bytes NumRoots + 4 bytes for 1 root index

            var calculatedSize = headerSize + (int)totalBlockSize + footerEstimate;

            // Trust the header-calculated size when parsing succeeds.
            // NIF files can contain embedded "Gamebryo File Format" strings in debug data,
            // so boundary scanning would give false positives.
            // Only use boundary scanning as a fallback when header parsing fails.
            var totalSize = calculatedSize;

            // Clamp to reasonable range
            totalSize = Math.Max(100, Math.Min(totalSize, maxSize));

            return new NifHeaderInfo(totalSize, isBigEndian, blockTypes);
        }
        catch
        {
            return new NifHeaderInfo(FallbackSize(data, fileOffset), false, []);
        }
    }

    /// <summary>
    ///     Determine if this is a Bethesda NIF (has BSStreamHeader).
    /// </summary>
    private static bool IsBethesdaVersion(uint binaryVersion, uint userVersion)
    {
        // Bethesda NIFs typically have:
        // Version 20.2.0.7 (0x14020007) with user version 11, 12, or similar
        // Version 20.0.0.5 (0x14000005) with user version 11
        // Or other combinations with non-zero user version
        return binaryVersion is 0x14020007 or 0x14000005 or 0x14000004
               && userVersion is > 0 and < 100;
    }

    /// <summary>
    ///     Fallback size estimation using boundary scanning.
    /// </summary>
    private static int FallbackSize(ReadOnlySpan<byte> data, int offset)
    {
        const int minSize = 100;
        const int maxScan = 5 * 1024 * 1024;
        const int defaultSize = 64 * 1024;

        return SignatureBoundaryScanner.FindBoundary(
            data, offset, minSize, maxScan, defaultSize,
            "Gamebryo File Format"u8);
    }

    #region IFileConverter Implementation

    /// <inheritdoc />
    public string TargetExtension => ".nif";

    /// <inheritdoc />
    public string TargetFolder => "models_converted";

    /// <inheritdoc />
    public bool IsInitialized => true;

    /// <inheritdoc />
    public int ConvertedCount { get; private set; }

    /// <inheritdoc />
    public int FailedCount { get; private set; }

    /// <inheritdoc />
    public bool Initialize(bool verbose = false, Dictionary<string, object>? options = null)
    {
        // No external dependencies needed for NIF conversion
        return true;
    }

    /// <inheritdoc />
    public bool CanConvert(string signatureId, IReadOnlyDictionary<string, object>? metadata)
    {
        // Only convert big-endian NIF files
        if (metadata?.TryGetValue("bigEndian", out var beValue) == true && beValue is bool isBigEndian)
        {
            return isBigEndian;
        }

        return false;
    }

    /// <inheritdoc />
    public Task<DdxConversionResult> ConvertAsync(byte[] data, IReadOnlyDictionary<string, object>? metadata = null)
    {
        try
        {
            var verbose = metadata?.TryGetValue("verbose", out var v) == true && v is true;
            var converter = new NifEndianConverter(verbose);
            var converted = converter.ConvertToLittleEndian(data);
            if (converted != null)
            {
                ConvertedCount++;
                return Task.FromResult(new DdxConversionResult
                {
                    Success = true,
                    DdsData = converted,
                    Notes = "Partial conversion: header converted to LE, block data remains BE. " +
                            "Xbox 360 NIFs may have platform-specific blocks. " +
                            "Use Noesis for better compatibility."
                });
            }

            FailedCount++;
            return Task.FromResult(new DdxConversionResult
            {
                Success = false,
                Notes = "Failed to convert NIF - file may already be little-endian or invalid"
            });
        }
        catch (Exception ex)
        {
            FailedCount++;
            return Task.FromResult(new DdxConversionResult
            {
                Success = false,
                Notes = $"NIF conversion error: {ex.Message}"
            });
        }
    }

    #endregion
}
