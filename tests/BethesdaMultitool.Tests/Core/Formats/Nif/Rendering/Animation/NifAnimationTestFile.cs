using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A synthetic NIF body for the animation-view tests: blocks written back to back through one
///     <see cref="NifAnimationByteWriter" />, with a <see cref="NifInfo" /> whose block table records each block's type,
///     offset and size exactly as NifParser would. No header bytes are written; the views read block bodies only.
/// </summary>
internal sealed class NifAnimationTestFile
{
    private readonly NifAnimationByteWriter _writer;

    /// <summary>Creates an empty body for the given byte order and versions (default 20.2.0.7, BS 34).</summary>
    public NifAnimationTestFile(bool bigEndian, uint binaryVersion = NifVersions.Gamebryo202007, uint bsVersion = 34)
    {
        _writer = new NifAnimationByteWriter(bigEndian);
        Nif = new NifInfo
        {
            BinaryVersion = binaryVersion,
            BsVersion = bsVersion,
            UserVersion = 11,
            IsBigEndian = bigEndian,
            HasInlineStrings = binaryVersion < 0x14010001
        };
    }

    /// <summary>The block table (and versions) the readers take.</summary>
    public NifInfo Nif { get; }

    /// <summary>Appends one block whose body <paramref name="write" /> produces, and records it in the block table.</summary>
    public BlockInfo AddBlock(string typeName, Action<NifAnimationByteWriter> write)
    {
        var start = _writer.Length;
        write(_writer);
        var block = new BlockInfo
        {
            Index = Nif.Blocks.Count,
            TypeName = typeName,
            DataOffset = start,
            Size = _writer.Length - start
        };
        Nif.Blocks.Add(block);
        Nif.BlockCount = Nif.Blocks.Count;
        return block;
    }

    /// <summary>The body bytes (call after the last block is added).</summary>
    public byte[] ToArray()
    {
        return _writer.ToArray();
    }
}
