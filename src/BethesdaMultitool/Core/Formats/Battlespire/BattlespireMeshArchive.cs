using System.Diagnostics.CodeAnalysis;
using BethesdaMultitool.Core.Compression;
using BethesdaMultitool.Core.Formats.Xngine.Bsa;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     Battlespire's mesh archives — <c>3D.BSA</c> (2,400 records) and <c>3D.BS6</c> (2,115) — are
///     name-record XnGine BSAs whose entries are per-entry LZSS-compressed <c>.3D</c> meshes. The
///     meshes label themselves <c>v2.7</c> like Daggerfall's, but their plane headers are 10 bytes
///     rather than 8, so they are read with <see cref="XnGineMeshLayout.Battlespire" />.
///     <para>
///         The same layout covers the 245 loose <c>.3D</c> files beside the archives, which is why
///         this class also opens a single file.
///     </para>
/// </summary>
internal sealed class BattlespireMeshArchive : IDisposable
{
    private readonly byte[] _bytes;
    private readonly IReadOnlyList<XnGineBsaEntry> _entries;
    private readonly Dictionary<string, int> _indexByName;

    private BattlespireMeshArchive(string name, byte[] bytes, IReadOnlyList<XnGineBsaEntry> entries)
    {
        Name = name;
        _bytes = bytes;
        _entries = entries;
        _indexByName = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            _indexByName.TryAdd(entries[i].Name, i);
        }
    }

    /// <summary>Archive file name.</summary>
    public string Name { get; }

    /// <summary>Records in the archive.</summary>
    public int Count => _entries.Count;

    public void Dispose()
    {
        // The archive is read fully into memory at open, so there is nothing to release; the type
        // stays disposable so callers can treat every archive handle the same way.
    }

    /// <summary>Opens a Battlespire mesh archive.</summary>
    public static BattlespireMeshArchive Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var archive = XnGineBsaParser.Parse(path);
        if (archive.IsNumbered)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is a number-record BSA; Battlespire's mesh archives are name-record.");
        }

        return new BattlespireMeshArchive(Path.GetFileName(path), File.ReadAllBytes(path), archive.Entries);
    }

    /// <summary>
    ///     Parses a loose XnGine mesh file. The layout defaults to Battlespire's because this class
    ///     is its archive reader, but the caller must pass Daggerfall's for a Redguard <c>.3D</c> or
    ///     <c>.3DC</c> — those tile only with the 8-byte plane header despite carrying the same
    ///     <c>v2.7</c> tag Battlespire's 10-byte records use.
    /// </summary>
    public static XnGineMesh ParseLoose(byte[] bytes, string name,
        XnGineMeshLayout layout = XnGineMeshLayout.Battlespire)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        return XnGineMesh.Parse(bytes, 0, layout);
    }

    /// <summary>The entry name at an index.</summary>
    public string EntryName(int index)
    {
        return _entries[index].Name;
    }

    /// <summary>The index of an entry name (case-insensitive), or -1.</summary>
    public int IndexOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _indexByName.GetValueOrDefault(name, -1);
    }

    /// <summary>The decompressed bytes of one entry.</summary>
    public byte[] RecordBytes(int index)
    {
        var entry = _entries[index];
        if (entry.Offset < 0 || entry.Size < 0 || entry.Offset + entry.Size > _bytes.Length)
        {
            throw new InvalidDataException(
                $"{Name} record {index} ({entry.Offset}+{entry.Size}) lies outside the {_bytes.Length}-byte archive.");
        }

        var raw = new ReadOnlySpan<byte>(_bytes, (int)entry.Offset, entry.Size);
        return entry.Compressed ? LzssCodec.DecompressBattlespire(raw) : raw.ToArray();
    }

    /// <summary>Parses one entry.</summary>
    public XnGineMesh Parse(int index)
    {
        return XnGineMesh.Parse(RecordBytes(index), (uint)index, XnGineMeshLayout.Battlespire);
    }

    /// <summary>Parses one entry, reporting a malformed record instead of throwing.</summary>
    public bool TryParse(int index, [NotNullWhen(true)] out XnGineMesh? mesh, out string? error)
    {
        try
        {
            mesh = Parse(index);
            error = null;
            return true;
        }
        catch (InvalidDataException e)
        {
            mesh = null;
            error = e.Message;
            return false;
        }
    }
}
