using System.Text;
using System.Text.RegularExpressions;
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

    public override string FormatId => "nif";
    public override string DisplayName => "NIF";
    public override string Extension => ".nif";
    public override FileCategory Category => FileCategory.Model;
    public override string OutputFolder => "models";
    public override int MinSize => 100;
    public override int MaxSize => 20 * 1024 * 1024;

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
            var versionInfo = ValidateAndExtractVersion(data, offset);
            if (versionInfo == null) return null;

            var (versionString, majorVersion, newlinePos) = versionInfo.Value;

            // Try to parse full header structure for accurate size calculation and content type detection
            var headerInfo = ParseNifHeader(data, offset, newlinePos);

            // If ParseNifHeader returns -1, this is not a valid NIF file (e.g., false positive)
            if (headerInfo.Size < 0) return null;

            // Determine if this is animation or geometry based on block types
            var (contentType, outputFolder, extension) = ClassifyNifContent(headerInfo.BlockTypes);

            return new ParseResult
            {
                Format = "NIF",
                EstimatedSize = headerInfo.Size,
                OutputFolderOverride = outputFolder,
                ExtensionOverride = extension,
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

    private static (string versionString, int majorVersion, int newlinePos)? ValidateAndExtractVersion(
        ReadOnlySpan<byte> data, int offset)
    {
        if (data.Length < offset + 50) return null;

        // Check for ", Version " after the magic
        var versionPrefixSpan = data.Slice(offset + 20, 10);
        if (!versionPrefixSpan.SequenceEqual(", Version "u8)) return null;

        var versionOffset = offset + 30;
        var newlinePos = FindNewlinePosition(data, versionOffset);
        if (newlinePos == -1 || newlinePos <= versionOffset || newlinePos > data.Length) return null;

        var versionString = Encoding.ASCII.GetString(data[versionOffset..newlinePos]).TrimEnd('\r');
        if (!VersionPattern().IsMatch(versionString)) return null;

        var majorVersion = ExtractMajorVersion(versionString);
        if (majorVersion < 2 || majorVersion > 30) return null;

        return (versionString, majorVersion, newlinePos);
    }

    private static int FindNewlinePosition(ReadOnlySpan<byte> data, int start)
    {
        for (var i = start; i < Math.Min(start + 20, data.Length); i++)
            if (data[i] == 0x0A)
                return i;
        return -1;
    }

    private static int ExtractMajorVersion(string versionString)
    {
        var dotIdx = versionString.IndexOf('.');
        if (dotIdx > 0 && int.TryParse(versionString[..dotIdx], out var major)) return major;
        return 0;
    }

    /// <summary>
    ///     Classify NIF content as animation or geometry based on block types.
    ///     Returns the content type, output folder, and file extension.
    /// </summary>
    private static (string contentType, string outputFolder, string? extension) ClassifyNifContent(
        List<string> blockTypes)
    {
        var hasGeometry = blockTypes.Any(bt => GeometryBlockTypes.Contains(bt));
        var hasAnimation = blockTypes.Any(bt => AnimationBlockTypes.Contains(bt));

        if (hasGeometry && !hasAnimation)
            return ("geometry", "meshes", null); // Use default .nif extension

        if (hasAnimation && !hasGeometry)
            return ("animation", "anims", ".kf"); // Animation files use .kf extension

        if (hasGeometry) // At this point, hasAnimation must also be true (mixed content)
            return ("mixed", "meshes", null); // Prefer meshes for mixed content

        return ("unknown", "models", null); // Fallback to generic models folder
    }

    /// <summary>
    ///     Parse the NIF header to calculate accurate file size and extract block type names.
    ///     Returns NifHeaderInfo with size=-1 if the file is not a valid NIF.
    /// </summary>
    private static NifHeaderInfo ParseNifHeader(ReadOnlySpan<byte> data, int fileOffset, int newlinePos)
    {
        const int maxSize = 10 * 1024 * 1024;

        try
        {
            var pos = newlinePos + 1;
            var blockTypes = new List<string>();

            // Parse initial header fields
            if (!TryParseInitialHeader(data, ref pos, out var binaryVersion, out var isBigEndian, out var userVersion))
                return new NifHeaderInfo(-1, false, []);

            // Parse number of blocks
            if (!TryReadNumBlocks(data, ref pos, out var numBlocks))
                return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);

            // Skip Bethesda header if present
            if (IsBethesdaVersion(binaryVersion, userVersion) && !TrySkipBethesdaHeader(data, ref pos))
                return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);

            // Handle optional alignment padding
            TrySkipAlignmentPadding(data, ref pos, isBigEndian);

            // Read block types
            if (!TryReadBlockTypes(data, ref pos, isBigEndian, blockTypes))
                return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);

            // Skip block type indices
            pos += (int)numBlocks * 2;
            if (pos > data.Length) return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);

            // Read block sizes and calculate total
            if (!TryReadBlockSizes(data, ref pos, isBigEndian, numBlocks, out var totalBlockSize))
                return new NifHeaderInfo(FallbackSize(data, fileOffset), isBigEndian, blockTypes);

            // Skip strings section
            TrySkipStringsSection(data, ref pos, isBigEndian);

            // Skip groups section
            TrySkipGroupsSection(data, ref pos, isBigEndian);

            // Calculate final size
            var headerSize = pos - fileOffset;
            const int footerEstimate = 8;
            var calculatedSize = headerSize + (int)totalBlockSize + footerEstimate;
            var totalSize = Math.Max(100, Math.Min(calculatedSize, maxSize));

            return new NifHeaderInfo(totalSize, isBigEndian, blockTypes);
        }
        catch
        {
            return new NifHeaderInfo(FallbackSize(data, fileOffset), false, []);
        }
    }

    /// <summary>
    ///     Parse initial header: binary version, endian byte, user version.
    /// </summary>
    private static bool TryParseInitialHeader(
        ReadOnlySpan<byte> data,
        ref int pos,
        out uint binaryVersion,
        out bool isBigEndian,
        out uint userVersion)
    {
        binaryVersion = 0;
        isBigEndian = false;
        userVersion = 0;

        if (pos + 9 > data.Length) return false;

        binaryVersion = BinaryUtils.ReadUInt32LE(data, pos);
        pos += 4;

        // Validate binary version
        if (binaryVersion < 0x04000000 || binaryVersion > 0x20000000) return false;

        var endianByte = data[pos];
        if (endianByte > 1) return false;

        pos += 1;
        isBigEndian = endianByte == 0;

        userVersion = BinaryUtils.ReadUInt32LE(data, pos);
        pos += 4;

        return true;
    }

    /// <summary>
    ///     Read number of blocks.
    /// </summary>
    private static bool TryReadNumBlocks(ReadOnlySpan<byte> data, ref int pos, out uint numBlocks)
    {
        numBlocks = 0;
        if (pos + 4 > data.Length) return false;

        numBlocks = BinaryUtils.ReadUInt32LE(data, pos);
        pos += 4;

        return numBlocks > 0 && numBlocks <= 100000;
    }

    /// <summary>
    ///     Skip Bethesda-specific header (BSStreamHeader).
    /// </summary>
    private static bool TrySkipBethesdaHeader(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos + 4 > data.Length) return false;

        var bsVersion = BinaryUtils.ReadUInt32LE(data, pos);
        pos += 4;

        // Skip Author string
        if (!TrySkipExportString(data, ref pos)) return false;

        // Unknown int if bsVersion > 130
        if (bsVersion > 130) pos += 4;

        // Skip Process Script if bsVersion < 131
        if (bsVersion < 131 && !TrySkipExportString(data, ref pos)) return false;

        // Skip Export Script
        if (!TrySkipExportString(data, ref pos)) return false;

        // Skip Max Filepath if bsVersion >= 103
        if (bsVersion >= 103 && !TrySkipExportString(data, ref pos)) return false;

        return true;
    }

    /// <summary>
    ///     Skip an ExportString (1 byte length + chars).
    /// </summary>
    private static bool TrySkipExportString(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos + 1 > data.Length) return false;

        var len = data[pos];
        pos += 1 + len;
        return true;
    }

    /// <summary>
    ///     Try to skip alignment padding byte if present.
    /// </summary>
    private static void TrySkipAlignmentPadding(ReadOnlySpan<byte> data, ref int pos, bool isBigEndian)
    {
        if (pos + 4 > data.Length) return;

        var testNumBlockTypes = isBigEndian
            ? BinaryUtils.ReadUInt16BE(data, pos)
            : BinaryUtils.ReadUInt16LE(data, pos);

        if (testNumBlockTypes == 0 || testNumBlockTypes > 500)
        {
            var testNumBlockTypes2 = isBigEndian
                ? BinaryUtils.ReadUInt16BE(data, pos + 1)
                : BinaryUtils.ReadUInt16LE(data, pos + 1);

            if (testNumBlockTypes2 > 0 && testNumBlockTypes2 < 500) pos += 1;
        }
    }

    /// <summary>
    ///     Read block types section.
    /// </summary>
    private static bool TryReadBlockTypes(
        ReadOnlySpan<byte> data,
        ref int pos,
        bool isBigEndian,
        List<string> blockTypes)
    {
        if (pos + 2 > data.Length) return false;

        var numBlockTypes = isBigEndian
            ? BinaryUtils.ReadUInt16BE(data, pos)
            : BinaryUtils.ReadUInt16LE(data, pos);
        pos += 2;

        if (numBlockTypes == 0 || numBlockTypes > 1000) return false;

        for (var i = 0; i < numBlockTypes; i++)
        {
            if (!TryReadSizedString(data, ref pos, isBigEndian, out var blockTypeName)) return false;

            if (!string.IsNullOrEmpty(blockTypeName)) blockTypes.Add(blockTypeName);
        }

        return true;
    }

    /// <summary>
    ///     Read a SizedString (uint32 length + chars).
    /// </summary>
    private static bool TryReadSizedString(
        ReadOnlySpan<byte> data,
        ref int pos,
        bool isBigEndian,
        out string result)
    {
        result = string.Empty;
        if (pos + 4 > data.Length) return false;

        var strLen = isBigEndian
            ? BinaryUtils.ReadUInt32BE(data, pos)
            : BinaryUtils.ReadUInt32LE(data, pos);
        pos += 4;

        if (strLen > 256) return false;

        if (pos + strLen <= data.Length && strLen > 0) result = Encoding.ASCII.GetString(data.Slice(pos, (int)strLen));

        pos += (int)strLen;
        return true;
    }

    /// <summary>
    ///     Read block sizes and calculate total.
    /// </summary>
    private static bool TryReadBlockSizes(
        ReadOnlySpan<byte> data,
        ref int pos,
        bool isBigEndian,
        uint numBlocks,
        out long totalBlockSize)
    {
        totalBlockSize = 0;
        if (pos + numBlocks * 4 > data.Length) return false;

        for (var i = 0; i < numBlocks; i++)
        {
            var blockSize = isBigEndian
                ? BinaryUtils.ReadUInt32BE(data, pos)
                : BinaryUtils.ReadUInt32LE(data, pos);
            pos += 4;

            if (blockSize > 50 * 1024 * 1024) return false;

            totalBlockSize += blockSize;
        }

        return true;
    }

    /// <summary>
    ///     Skip strings section.
    /// </summary>
    private static void TrySkipStringsSection(ReadOnlySpan<byte> data, ref int pos, bool isBigEndian)
    {
        if (pos + 8 > data.Length) return;

        var numStrings = isBigEndian
            ? BinaryUtils.ReadUInt32BE(data, pos)
            : BinaryUtils.ReadUInt32LE(data, pos);
        pos += 4;

        // Skip Max String Length
        pos += 4;

        // Skip Strings
        for (var i = 0; i < numStrings && pos + 4 <= data.Length; i++)
        {
            var strLen = isBigEndian
                ? BinaryUtils.ReadUInt32BE(data, pos)
                : BinaryUtils.ReadUInt32LE(data, pos);
            pos += 4;

            if (strLen > 1024) break;

            pos += (int)strLen;
        }
    }

    /// <summary>
    ///     Skip groups section.
    /// </summary>
    private static void TrySkipGroupsSection(ReadOnlySpan<byte> data, ref int pos, bool isBigEndian)
    {
        if (pos + 4 > data.Length) return;

        var numGroups = isBigEndian
            ? BinaryUtils.ReadUInt32BE(data, pos)
            : BinaryUtils.ReadUInt32LE(data, pos);
        pos += 4;
        pos += (int)numGroups * 4;
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

    /// <summary>
    ///     Result from parsing NIF header.
    /// </summary>
    private readonly record struct NifHeaderInfo(int Size, bool IsBigEndian, List<string> BlockTypes);
}
