using BethesdaMultitool.Core.Formats.Xngine.Bsa;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>Kinds of BLOCKS.BSA entries, by extension.</summary>
internal enum DaggerfallBlockType
{
    Unknown,
    Rmb,
    Rdb,
    Rdi
}

/// <summary>
///     Daggerfall's block archive, <c>BLOCKS.BSA</c>: a name-record XnGine BSA of exterior (RMB),
///     dungeon (RDB) and 512-byte dungeon-info (RDI) records. Retail holds 920 + 187 + 187 of them
///     plus one stray entry, "FOO", which is a DOS directory listing packed by mistake and reads as
///     <see cref="DaggerfallBlockType.Unknown" />.
/// </summary>
internal sealed class DaggerfallBlocksFile
{
    /// <summary>The archive's file name.</summary>
    public const string FileName = "BLOCKS.BSA";

    /// <summary>Bytes in an RDI record.</summary>
    public const int RdiLength = 512;

    private readonly byte[] _bytes;
    private readonly IReadOnlyList<XnGineBsaEntry> _entries;
    private readonly Dictionary<string, int> _indexByName;

    private DaggerfallBlocksFile(byte[] bytes, IReadOnlyList<XnGineBsaEntry> entries)
    {
        _bytes = bytes;
        _entries = entries;
        _indexByName = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            _indexByName.TryAdd(entries[i].Name, i);
        }
    }

    public int Count => _entries.Count;

    /// <summary>Opens a BLOCKS.BSA from disk.</summary>
    public static DaggerfallBlocksFile Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var archive = XnGineBsaParser.Parse(path);
        if (archive.IsNumbered)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is a number-record BSA; BLOCKS.BSA is name-record.");
        }

        return new DaggerfallBlocksFile(File.ReadAllBytes(path), archive.Entries);
    }

    /// <summary>The kind of block a name denotes.</summary>
    public static DaggerfallBlockType TypeOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.EndsWith(".RMB", StringComparison.OrdinalIgnoreCase))
        {
            return DaggerfallBlockType.Rmb;
        }

        if (name.EndsWith(".RDB", StringComparison.OrdinalIgnoreCase))
        {
            return DaggerfallBlockType.Rdb;
        }

        return name.EndsWith(".RDI", StringComparison.OrdinalIgnoreCase) ? DaggerfallBlockType.Rdi : DaggerfallBlockType.Unknown;
    }

    public string Name(int index)
    {
        return _entries[index].Name;
    }

    public DaggerfallBlockType TypeAt(int index)
    {
        return TypeOf(_entries[index].Name);
    }

    /// <summary>The index of a block name (case-insensitive), or -1.</summary>
    public int IndexOf(string name)
    {
        return _indexByName.GetValueOrDefault(name, -1);
    }

    public ReadOnlyMemory<byte> RecordBytes(int index)
    {
        var entry = _entries[index];
        if (entry.Offset < 0 || entry.Size < 0 || entry.Offset + entry.Size > _bytes.Length)
        {
            throw new InvalidDataException($"BLOCKS record {index} ({entry.Offset}+{entry.Size}) lies outside the {_bytes.Length}-byte archive.");
        }

        return new ReadOnlyMemory<byte>(_bytes, (int)entry.Offset, entry.Size);
    }

    public DaggerfallRmbBlock ParseRmb(int index)
    {
        RequireType(index, DaggerfallBlockType.Rmb);
        return DaggerfallRmbBlock.Parse(RecordBytes(index), Name(index));
    }

    public DaggerfallRdbBlock ParseRdb(int index)
    {
        RequireType(index, DaggerfallBlockType.Rdb);
        return DaggerfallRdbBlock.Parse(RecordBytes(index), Name(index));
    }

    /// <summary>The raw 512 bytes of an RDI record.</summary>
    public ReadOnlyMemory<byte> RdiBytes(int index)
    {
        RequireType(index, DaggerfallBlockType.Rdi);
        var bytes = RecordBytes(index);
        if (bytes.Length != RdiLength)
        {
            throw new InvalidDataException($"{Name(index)} is {bytes.Length} bytes; an RDI record is {RdiLength}.");
        }

        return bytes;
    }

    private void RequireType(int index, DaggerfallBlockType expected)
    {
        var actual = TypeAt(index);
        if (actual != expected)
        {
            throw new InvalidOperationException($"{Name(index)} is a {actual} record, not {expected}.");
        }
    }
}
