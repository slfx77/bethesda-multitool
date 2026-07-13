using NifAnalyzer.Models;
using CoreNifParser = BethesdaMultitool.Core.Formats.Nif.Parser.NifParser;

namespace NifAnalyzer.Parsers;

/// <summary>
///     Parses NIF headers by delegating to the MAIN APPLICATION's parser
///     (<c>BethesdaMultitool.Core.Formats.Nif.Parser.NifParser</c>) and projecting its authoritative
///     result into <see cref="NifInfo" />. NifAnalyzer is a debugging aid FOR the main app, so it must
///     decode NIFs with the app's own code — a forked header parser would read some formats differently
///     (its old hand-rolled reader assumed the modern block-size-array layout and threw on Morrowind
///     4.0.0.2, whose blocks are separated by inline type-name prefixes). The app parser handles every
///     supported version (NetImmerse/Morrowind, legacy Gamebryo/Oblivion, and modern Bethesda) and
///     hands back exact per-block offsets from its measure walk.
/// </summary>
internal static class NifParser
{
    public static NifInfo Parse(byte[] data)
    {
        var core = CoreNifParser.Parse(data)
                   ?? throw new InvalidDataException("Not a valid NIF file (the application parser returned null).");

        // Rebuild a dedup table from the authoritative per-block type names so
        // BlockTypes[BlockTypeIndices[i]] == the real type of block i for EVERY version — including
        // Morrowind, which ships no on-disk block-types table (the app parser leaves TypeIndex = 0 and
        // stores the name inline per block).
        var typeIndexByName = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var blockTypes = new List<string>();
        var blockTypeIndices = new ushort[core.Blocks.Count];
        var blockSizes = new uint[core.Blocks.Count];
        var blockOffsets = new int[core.Blocks.Count];

        for (var i = 0; i < core.Blocks.Count; i++)
        {
            var block = core.Blocks[i];
            if (!typeIndexByName.TryGetValue(block.TypeName, out var typeIndex))
            {
                typeIndex = (ushort)blockTypes.Count;
                typeIndexByName[block.TypeName] = typeIndex;
                blockTypes.Add(block.TypeName);
            }

            blockTypeIndices[i] = typeIndex;
            blockSizes[i] = (uint)block.Size;
            blockOffsets[i] = block.DataOffset;
        }

        return new NifInfo
        {
            VersionString = core.HeaderString,
            Version = core.BinaryVersion,
            IsBigEndian = core.IsBigEndian,
            UserVersion = core.UserVersion,
            NumBlocks = core.Blocks.Count,
            BsVersion = (int)core.BsVersion,
            BlockTypes = blockTypes,
            BlockTypeIndices = blockTypeIndices,
            BlockSizes = blockSizes,
            BlockOffsets = blockOffsets,
            Strings = core.Strings,
            NumStrings = core.Strings.Count,
            MaxStringLength = core.Strings.Count > 0 ? core.Strings.Max(s => s.Length) : 0,
            BlockDataOffset = core.Blocks.Count > 0 ? core.Blocks[0].DataOffset : 0,
        };
    }
}
