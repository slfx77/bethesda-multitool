// Parser for NiDefaultAVObjectPalette and NiControllerSequence blocks
// Extracts block index → name mappings for node name restoration

using System.Text;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Result from parsing palette and controller sequence blocks.
/// </summary>
internal sealed class NifNameMappings
{
    /// <summary>
    ///     Block index → name mappings from NiDefaultAVObjectPalette.
    /// </summary>
    public Dictionary<int, string> BlockNames { get; } = [];

    /// <summary>
    ///     The Accum Root Name from NiControllerSequence (root node name).
    ///     This is the BSFadeNode/NiNode root name for the animation system.
    /// </summary>
    public string? AccumRootName { get; set; }
}

/// <summary>
///     Parses NiDefaultAVObjectPalette blocks to extract block→name mappings.
///     Used to restore node names that are NULL in Xbox 360 NIFs.
/// </summary>
internal static class NifPaletteParser
{
    /// <summary>
    ///     Parse all name-related blocks and return combined mappings.
    /// </summary>
    public static NifNameMappings ParseAll(byte[] data, NifInfo info, bool verbose = false)
    {
        var result = new NifNameMappings();

        // Parse NiDefaultAVObjectPalette for block→name mappings
        var blockNames = Parse(data, info, verbose);
        if (blockNames != null)
            foreach (var kvp in blockNames)
                result.BlockNames[kvp.Key] = kvp.Value;

        // Parse NiControllerSequence for Accum Root Name
        var accumRootName = ParseAccumRootName(data, info, verbose);
        if (accumRootName != null)
        {
            result.AccumRootName = accumRootName;
            if (verbose)
                Console.WriteLine($"  Accum Root Name: '{accumRootName}'");
        }

        return result;
    }

    /// <summary>
    ///     Parse NiDefaultAVObjectPalette and return a dictionary mapping block index to name.
    ///     Only returns entries with valid block references (not -1).
    /// </summary>
    public static Dictionary<int, string>? Parse(byte[] data, NifInfo info, bool verbose = false)
    {
        // Find NiDefaultAVObjectPalette block
        BlockInfo? paletteBlock = null;
        foreach (var block in info.Blocks)
            if (block.TypeName == "NiDefaultAVObjectPalette")
            {
                paletteBlock = block;
                break;
            }

        if (paletteBlock == null)
        {
            if (verbose)
                Console.WriteLine("  No NiDefaultAVObjectPalette found");
            return null;
        }

        return ParseBlock(data, paletteBlock.DataOffset, info.IsBigEndian, verbose);
    }

    /// <summary>
    ///     Parse NiDefaultAVObjectPalette at the given offset.
    /// </summary>
    private static Dictionary<int, string> ParseBlock(byte[] data, int offset, bool bigEndian, bool verbose)
    {
        var result = new Dictionary<int, string>();
        var pos = offset;

        // Scene reference (Ptr to NiAVObject) - 4 bytes
        var sceneRef = ReadInt32(data, ref pos, bigEndian);

        // Num objects - 4 bytes  
        var numObjs = ReadInt32(data, ref pos, bigEndian);

        if (verbose)
            Console.WriteLine($"  Parsing NiDefaultAVObjectPalette: {numObjs} entries, scene ref {sceneRef}");

        // Parse AVObject array - each entry is: SizedString (uint length + chars) + Ptr (int)
        for (var i = 0; i < numObjs; i++)
        {
            // Read SizedString: uint length + chars
            var strLen = ReadInt32(data, ref pos, bigEndian);
            if (strLen < 0 || strLen > 256 || pos + strLen > data.Length)
            {
                if (verbose)
                    Console.WriteLine($"    Invalid string length {strLen} at entry {i}");
                break;
            }

            var name = Encoding.ASCII.GetString(data, pos, strLen);
            pos += strLen;

            // Read Ptr (block reference)
            var blockRef = ReadInt32(data, ref pos, bigEndian);

            if (verbose)
                Console.WriteLine($"    [{i}] Name='{name}' -> Block {blockRef}");

            // Only add entries with valid block references
            if (blockRef >= 0)
            {
                // Strip suffix like ":0" from names (animation controller format vs node name)
                var baseName = StripAnimationSuffix(name);

                // Don't overwrite if we already have a simpler name for this block
                if (!result.ContainsKey(blockRef) || result[blockRef].Length > baseName.Length)
                    result[blockRef] = baseName;
            }
        }

        if (verbose)
            Console.WriteLine($"  Found {result.Count} block→name mappings");

        return result;
    }

    /// <summary>
    ///     Parse NiControllerSequence blocks to find Accum Root Name.
    ///     The Accum Root Name is the name of the root node for animation accumulation.
    /// </summary>
    public static string? ParseAccumRootName(byte[] data, NifInfo info, bool verbose = false)
    {
        // Find first NiControllerSequence block
        BlockInfo? seqBlock = null;
        foreach (var block in info.Blocks)
            if (block.TypeName == "NiControllerSequence")
            {
                seqBlock = block;
                break;
            }

        if (seqBlock == null)
        {
            if (verbose)
                Console.WriteLine("  No NiControllerSequence found");
            return null;
        }

        return ParseControllerSequence(data, seqBlock.DataOffset, info, verbose);
    }

    /// <summary>
    ///     Parse NiControllerSequence block to extract Accum Root Name.
    ///     Structure (for version 20.2.0.7, BS Version 34):
    ///     - NiSequence base:
    ///     - Name (string index)
    ///     - Num Controlled Blocks (uint)
    ///     - Array Grow By (uint)
    ///     - Controlled Blocks (array of ControlledBlock)
    ///     - NiControllerSequence fields:
    ///     - Weight (float)
    ///     - Text Keys (Ref)
    ///     - Cycle Type (uint)
    ///     - Frequency (float)
    ///     - Start Time (float)
    ///     - Stop Time (float)
    ///     - Manager (Ptr)
    ///     - Accum Root Name (string index) <- This is what we want!
    /// </summary>
    private static string? ParseControllerSequence(byte[] data, int offset, NifInfo info, bool verbose)
    {
        var pos = offset;
        var bigEndian = info.IsBigEndian;

        // NiSequence base fields:
        // Name (string index)
        var nameIdx = ReadInt32(data, ref pos, bigEndian);

        // Num Controlled Blocks
        var numControlledBlocks = ReadInt32(data, ref pos, bigEndian);

        // Array Grow By
        _ = ReadInt32(data, ref pos, bigEndian); // Not used

        if (verbose)
            Console.WriteLine($"  NiControllerSequence: nameIdx={nameIdx}, numControlled={numControlledBlocks}");

        // Skip Controlled Blocks array
        // Each ControlledBlock for version 20.2.0.7, BS Version 34 (Bethesda) is:
        // - Interpolator (Ref, 4 bytes)
        // - Controller (Ref, 4 bytes)
        // - Priority (byte, 1 byte) - BSSTREAM conditional
        // - Node Name (string index, 4 bytes) - since 20.1.0.1
        // - Property Type (string index, 4 bytes)
        // - Controller Type (string index, 4 bytes)
        // - Controller ID (string index, 4 bytes)
        // - Interpolator ID (string index, 4 bytes)
        // Total: 29 bytes per ControlledBlock
        const int controlledBlockSize = 29;
        pos += numControlledBlocks * controlledBlockSize;

        // NiControllerSequence specific fields:
        // Weight (float)
        pos += 4;

        // Text Keys (Ref)
        pos += 4;

        // Cycle Type (uint)
        pos += 4;

        // Frequency (float)
        pos += 4;

        // Start Time (float)
        pos += 4;

        // Stop Time (float)
        pos += 4;

        // Manager (Ptr)
        pos += 4;

        // Accum Root Name (string index) - THIS IS WHAT WE WANT!
        var accumRootNameIdx = ReadInt32(data, ref pos, bigEndian);

        if (verbose)
            Console.WriteLine($"  Accum Root Name Index: {accumRootNameIdx}");

        // Look up the string
        if (accumRootNameIdx >= 0 && accumRootNameIdx < info.Strings.Count) return info.Strings[accumRootNameIdx];

        return null;
    }

    /// <summary>
    ///     Strip animation controller suffix (":0", ":1" etc.) from name.
    ///     Animation controllers use "NodeName:0" format, but we want just "NodeName".
    /// </summary>
    private static string StripAnimationSuffix(string name)
    {
        var colonIdx = name.LastIndexOf(':');
        if (colonIdx > 0 && colonIdx < name.Length - 1)
        {
            // Check if everything after colon is digits
            var suffix = name.AsSpan(colonIdx + 1);
            var isNumeric = true;
            foreach (var c in suffix)
                if (c < '0' || c > '9')
                {
                    isNumeric = false;
                    break;
                }

            if (isNumeric) return name[..colonIdx];
        }

        return name;
    }

    private static int ReadInt32(byte[] data, ref int pos, bool bigEndian)
    {
        int value;
        if (bigEndian)
            value = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
        else
            value = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24);
        pos += 4;
        return value;
    }
}
