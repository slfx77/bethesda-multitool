using System.Text;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Hand-laid 20.2.0.7 NIF writer for the block-decoder tests. It writes a Bethesda header (little- or big-endian
///     body order, BS 14, 21, 26, 32 or 34, user version 11), the block-type table, per-block type indices and sizes,
///     a string table whose entries are raw bytes, zero groups, the blocks exactly as the test lays them out, and the
///     footer. It deliberately knows nothing about NifSchema: every block body is written field by field by the test
///     (or by <see cref="NifTestBlockLayouts" />) from a reading of nif.xml, so the decoder is never compared with
///     itself.
/// </summary>
/// <remarks>
///     Header byte order follows retail Xbox files and the existing <see cref="BigEndianNifBuilder" />: the header
///     line, version, endian byte, user version, block count and BS version are little-endian in every file;
///     everything from Num Block Types on, the block bodies and the footer use the body order.
/// </remarks>
internal sealed class NifTestFileBuilder
{
    /// <summary>The BS stream versions of the cut-1a key.</summary>
    public static readonly uint[] CutOneABsVersions = [14, 21, 26, 32, 34];

    private readonly List<(string Type, byte[] Body)> _blocks = [];
    private readonly List<int> _roots = [];
    private readonly List<byte[]> _strings = [];
    private byte[] _trailingBytes = [];
    private int[]? _footerRootsOverride;

    /// <summary>Starts a file.</summary>
    /// <param name="bigEndian">True for an Xbox-style big-endian body (endian byte 0).</param>
    /// <param name="bsVersion">The BSStreamHeader BS version.</param>
    /// <param name="userVersion">The header user version.</param>
    public NifTestFileBuilder(bool bigEndian, uint bsVersion, uint userVersion = 11)
    {
        BigEndian = bigEndian;
        BsVersion = bsVersion;
        UserVersion = userVersion;
    }

    /// <summary>True when the body order is big-endian.</summary>
    public bool BigEndian { get; }

    /// <summary>The BS version written to the header.</summary>
    public uint BsVersion { get; }

    /// <summary>The user version written to the header.</summary>
    public uint UserVersion { get; }

    /// <summary>The number of blocks added so far (the index the next block gets).</summary>
    public int BlockCount => _blocks.Count;

    /// <summary>Adds an ASCII header string and returns its index.</summary>
    public int AddString(string ascii)
    {
        return AddRawString(Encoding.ASCII.GetBytes(ascii));
    }

    /// <summary>Adds a header string from raw bytes (any byte value) and returns its index.</summary>
    public int AddRawString(byte[] bytes)
    {
        _strings.Add([.. bytes]);
        return _strings.Count - 1;
    }

    /// <summary>Adds a block laid out by <paramref name="write" /> and returns its index.</summary>
    public int AddBlock(string typeName, Action<NifTestBlockWriter> write)
    {
        var writer = new NifTestBlockWriter(BigEndian);
        write(writer);
        _blocks.Add((typeName, writer.ToArray()));
        return _blocks.Count - 1;
    }

    /// <summary>Declares a footer root (defaults to block 0 when none is declared).</summary>
    public NifTestFileBuilder WithRoot(int blockIndex)
    {
        _roots.Add(blockIndex);
        return this;
    }

    /// <summary>Writes these footer roots verbatim, even when they are out of range.</summary>
    public NifTestFileBuilder WithFooterRootsOverride(params int[] roots)
    {
        _footerRootsOverride = roots;
        return this;
    }

    /// <summary>Appends bytes after the footer (to test the EOF check).</summary>
    public NifTestFileBuilder WithTrailingBytes(params byte[] bytes)
    {
        _trailingBytes = bytes;
        return this;
    }

    /// <summary>Builds the file.</summary>
    public byte[] Build()
    {
        var w = new NifTestBlockWriter(BigEndian);

        // Header, little-endian segment.
        w.RawAscii("Gamebryo File Format, Version 20.2.0.7");
        w.RawU8(0x0A);
        w.RawU32Le(0x14020007);
        w.RawU8(BigEndian ? (byte)0 : (byte)1);
        w.RawU32Le(UserVersion);
        w.RawU32Le((uint)_blocks.Count);
        w.RawU32Le(BsVersion);
        w.RawExportString(); // Author
        w.RawExportString(); // Process Script (BS below 131)
        w.RawExportString(); // Export Script (no Max Filepath below BS 103)

        // Header, body-order segment.
        var typeNames = _blocks.Select(b => b.Type).Distinct(StringComparer.Ordinal).ToList();
        w.U16((ushort)typeNames.Count);
        foreach (var name in typeNames)
        {
            w.SizedString(name);
        }

        foreach (var (type, _) in _blocks)
        {
            w.U16((ushort)typeNames.IndexOf(type));
        }

        foreach (var (_, body) in _blocks)
        {
            w.U32((uint)body.Length);
        }

        w.U32((uint)_strings.Count);
        w.U32(_strings.Count == 0 ? 0u : (uint)_strings.Max(s => s.Length));
        foreach (var bytes in _strings)
        {
            w.U32((uint)bytes.Length);
            w.Bytes(bytes);
        }

        w.U32(0); // Num Groups

        foreach (var (_, body) in _blocks)
        {
            w.Bytes(body);
        }

        int[] roots;
        if (_footerRootsOverride is not null)
        {
            roots = _footerRootsOverride;
        }
        else if (_roots.Count > 0)
        {
            roots = [.. _roots];
        }
        else if (_blocks.Count > 0)
        {
            roots = [0];
        }
        else
        {
            roots = [];
        }

        w.U32((uint)roots.Length);
        foreach (var root in roots)
        {
            w.I32(root);
        }

        w.Bytes(_trailingBytes);
        return w.ToArray();
    }
}
