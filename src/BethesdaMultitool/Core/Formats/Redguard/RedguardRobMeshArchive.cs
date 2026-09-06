using System.Diagnostics.CodeAnalysis;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     A <c>.ROB</c> read as a mesh archive: the objects for one Redguard location. Every non-empty
///     segment is a plain uncompressed XnGine mesh, so this is <see cref="RedguardRobParser" /> plus
///     name lookup and per-entry parsing.
///     <para>
///         Meshes are read with <see cref="XnGineMeshLayout.Daggerfall" /> — Redguard's plane header
///         is 8 bytes like Daggerfall's, not Battlespire's 10, even though both games tag their
///         meshes <c>v2.7</c>. The tag follows the tech lineage; the layout follows the game.
///     </para>
///     <para>
///         Measured over all 41 retail archives (2026-09-04): 4,667 non-empty segments, every one a
///         mesh that reproduces its planes' stored normals from the header's <c>OffsetVertexCoors</c>
///         — including all 217 tagged <c>v2.6</c>. So a <c>v2.6</c> tag here does NOT mean the
///         unsolved loose-file <c>.3DC</c> layout.
///     </para>
/// </summary>
internal sealed class RedguardRobMeshArchive : IDisposable
{
    private readonly byte[] _bytes;
    private readonly IReadOnlyList<RedguardRobEntry> _entries;
    private readonly Dictionary<string, int> _indexByName;

    private RedguardRobMeshArchive(string name, byte[] bytes, IReadOnlyList<RedguardRobEntry> entries)
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

    /// <summary>Segments in the archive, empty placeholders included.</summary>
    public int Count => _entries.Count;

    /// <summary>Opens a ROB archive.</summary>
    public static RedguardRobMeshArchive Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var archive = RedguardRobParser.Parse(path);
        return new RedguardRobMeshArchive(Path.GetFileName(path), File.ReadAllBytes(path), archive.Entries);
    }

    /// <summary>The segment name at an index, without the <c>.3D</c> the archive views append.</summary>
    public string EntryName(int index)
    {
        return _entries[index].Name;
    }

    /// <summary>True when a segment is one of the empty placeholders and holds no mesh.</summary>
    public bool IsEmpty(int index)
    {
        return _entries[index].Size == 0;
    }

    /// <summary>The index of a segment name (case-insensitive, with or without <c>.3D</c>), or -1.</summary>
    public int IndexOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_indexByName.TryGetValue(name, out var index))
        {
            return index;
        }

        return name.EndsWith(".3D", StringComparison.OrdinalIgnoreCase)
            ? _indexByName.GetValueOrDefault(name[..^3], -1)
            : -1;
    }

    /// <summary>The bytes of one segment. Nothing in a ROB is compressed.</summary>
    public byte[] RecordBytes(int index)
    {
        var entry = _entries[index];
        if (entry.Offset < 0 || entry.Size < 0 || entry.Offset + entry.Size > _bytes.Length)
        {
            throw new InvalidDataException(
                $"{Name} segment '{entry.Name}' ({entry.Offset}+{entry.Size}) lies outside the {_bytes.Length}-byte archive.");
        }

        return new ReadOnlySpan<byte>(_bytes, (int)entry.Offset, entry.Size).ToArray();
    }

    /// <summary>Parses one segment as a mesh.</summary>
    public XnGineMesh Parse(int index)
    {
        if (IsEmpty(index))
        {
            throw new InvalidDataException($"{Name} segment '{EntryName(index)}' is empty and holds no mesh.");
        }

        return XnGineMesh.Parse(RecordBytes(index), (uint)index, XnGineMeshLayout.Daggerfall);
    }

    /// <summary>Parses one segment, reporting a malformed or empty record instead of throwing.</summary>
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

    public void Dispose()
    {
        // Bytes are owned outright; the type is IDisposable so callers read like the sibling
        // archive readers and keep working if a mapped-file backing ever replaces the byte array.
    }
}
