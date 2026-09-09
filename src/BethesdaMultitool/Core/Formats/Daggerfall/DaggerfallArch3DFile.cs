using System.Diagnostics.CodeAnalysis;
using BethesdaMultitool.Core.Formats.Xngine.Bsa;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     Daggerfall's mesh archive, <c>ARCH3D.BSA</c>: a number-record XnGine BSA whose ids are the
///     game's model ids. Ten ids appear twice on retail, so records are addressed by INDEX here
///     and <see cref="IndexOf" /> returns the first match, as the reference's lookup does.
/// </summary>
internal sealed class DaggerfallArch3DFile
{
    /// <summary>The archive's file name.</summary>
    public const string FileName = "ARCH3D.BSA";

    private readonly byte[] _bytes;
    private readonly IReadOnlyList<XnGineBsaEntry> _entries;
    private readonly Dictionary<uint, int> _firstIndexById;

    private DaggerfallArch3DFile(byte[] bytes, IReadOnlyList<XnGineBsaEntry> entries)
    {
        _bytes = bytes;
        _entries = entries;
        _firstIndexById = new Dictionary<uint, int>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Id is { } id)
            {
                _firstIndexById.TryAdd(id, i);
            }
        }
    }

    /// <summary>Records in the archive.</summary>
    public int Count => _entries.Count;

    /// <summary>Distinct object ids.</summary>
    public int DistinctIdCount => _firstIndexById.Count;

    /// <summary>Opens an ARCH3D.BSA from disk.</summary>
    public static DaggerfallArch3DFile Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var archive = XnGineBsaParser.Parse(path);
        if (!archive.IsNumbered)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is a name-record BSA; ARCH3D.BSA is number-record.");
        }

        return new DaggerfallArch3DFile(File.ReadAllBytes(path), archive.Entries);
    }

    /// <summary>The object id at an index.</summary>
    public uint RecordId(int index)
    {
        return _entries[index].Id ?? throw new InvalidDataException($"ARCH3D record {index} has no id.");
    }

    /// <summary>The first index carrying an object id, or -1.</summary>
    public int IndexOf(uint objectId)
    {
        return _firstIndexById.GetValueOrDefault(objectId, -1);
    }

    /// <summary>The raw bytes of one record.</summary>
    public ReadOnlyMemory<byte> RecordBytes(int index)
    {
        var entry = _entries[index];
        if (entry.Offset < 0 || entry.Size < 0 || entry.Offset + entry.Size > _bytes.Length)
        {
            throw new InvalidDataException(
                $"ARCH3D record {index} ({entry.Offset}+{entry.Size}) lies outside the {_bytes.Length}-byte archive.");
        }

        return new ReadOnlyMemory<byte>(_bytes, (int)entry.Offset, entry.Size);
    }

    /// <summary>Parses one record.</summary>
    public XnGineMesh Parse(int index)
    {
        return XnGineMesh.Parse(RecordBytes(index), RecordId(index));
    }

    /// <summary>Parses one record, reporting a malformed one instead of throwing.</summary>
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
