using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>One payload the index names.</summary>
/// <param name="Group">The <c>.grp</c> stem, e.g. <c>Maps</c>.</param>
/// <param name="Index">The payload's position in that archive's directory.</param>
/// <param name="TypeId">The resource type, in hundreds (see the <see cref="VanBurenResourceIndex" /> constants).</param>
/// <param name="Name">The authored name, with no extension.</param>
internal readonly record struct VanBurenResourceEntry(string Group, int Index, uint TypeId, string Name);

/// <summary>
///     <c>resource.rht</c> — the prototype's resource index, which NAMES every payload in every
///     <c>.grp</c>. Original RE 2026-09-08. The archives themselves store only offsets and sizes,
///     so this is where the identity the board called missing actually lives.
///     <para>
///         LE. Header: <c>1</c>, <c>1</c>, the ENTRY COUNT, the offset of the group-name table, the
///         offset of the name table. Then <c>count</c> 20-byte records:
///         <c>(1, index, typeId, nameOffset, groupNameOffset)</c> — the name offset is relative to
///         the name table, and ⚑
///         <b>
///             the last field is the OFFSET OF THE GROUP'S NAME in the
///             group-name table
///         </b>
///         (<c>Tiles</c> 0, <c>Interface</c> 6, <c>Critters</c> 16 …), not an
///         id: read as a group, its per-value counts equal every archive's entry count exactly,
///         where reading the type field as the group matched nothing.
///     </para>
///     <para>
///         ⚑ Everything tiles: the records end exactly where the group table begins, the group
///         table ends exactly where the name table begins, every name offset lands on a string
///         start, the last name ends at EOF, and the 7,044 <c>(group, index)</c> keys are unique
///         and cover the 7,044 archive entries with none left over.
///     </para>
///     <para>
///         Type ids measured against the payload tags: 100 <c>B3D</c> meshes, 200 textures (TGA
///         and BMP), 400 <c>8TRE</c> scenes, 600 Granny skeletons, 700 Granny animations,
///         800 INI/text, 1100 <c>RIFF</c> sounds, 1300 <c>EMAP</c> maps, 1500 <c>VEG</c>,
///         1600 <c>GUI</c>, 1700 walk grids, and one id per entity group for <c>EEN2</c>.
///     </para>
/// </summary>
internal sealed class VanBurenResourceIndex
{
    /// <summary>The conventional file name beside <c>data/</c>.</summary>
    public const string FileName = "resource.rht";

    /// <summary>Five dwords.</summary>
    public const int HeaderLength = 20;

    /// <summary>Five dwords per record.</summary>
    public const int RecordLength = 20;

    /// <summary><c>B3D</c> meshes.</summary>
    public const uint MeshType = 100;

    /// <summary>TGA and BMP textures.</summary>
    public const uint TextureType = 200;

    /// <summary><c>8TRE</c> octree scenes.</summary>
    public const uint SceneType = 400;

    /// <summary><c>EMAP</c> maps.</summary>
    public const uint MapType = 1300;

    /// <summary>Run-length walk grids.</summary>
    public const uint WalkGridType = 1700;

    private readonly Dictionary<(string Group, int Index), VanBurenResourceEntry> _byKey;

    private VanBurenResourceIndex(IReadOnlyList<string> groups, IReadOnlyList<VanBurenResourceEntry> entries)
    {
        Groups = groups;
        Entries = entries;
        _byKey = new Dictionary<(string, int), VanBurenResourceEntry>(entries.Count);
        foreach (var entry in entries)
        {
            _byKey[(entry.Group.ToUpperInvariant(), entry.Index)] = entry;
        }
    }

    /// <summary>The group-name table, in file order.</summary>
    public IReadOnlyList<string> Groups { get; }

    /// <summary>Every record, in file order.</summary>
    public IReadOnlyList<VanBurenResourceEntry> Entries { get; }

    /// <summary>Content probe: the two constant dwords and a record area that ends on the group table.</summary>
    public static bool IsResourceIndex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var groupTable = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 1
               && BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == 1
               && count <= (uint)int.MaxValue / RecordLength
               && HeaderLength + (long)count * RecordLength == groupTable
               && groupTable <= bytes.Length;
    }

    /// <summary>Loads the index that sits beside a build's <c>data/</c> directory, or null when absent.</summary>
    public static VanBurenResourceIndex? TryLoadBeside(string archivePath)
    {
        ArgumentNullException.ThrowIfNull(archivePath);
        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(archivePath));
        var buildRoot = dataDirectory is null ? null : Path.GetDirectoryName(dataDirectory);
        foreach (var directory in new[] { buildRoot, dataDirectory })
        {
            if (directory is null)
            {
                continue;
            }

            var candidate = Path.Combine(directory, FileName);
            if (!File.Exists(candidate))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(candidate);
            if (TryParse(bytes, candidate, out var index, out _))
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>Parses the index, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static VanBurenResourceIndex Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var index, out var error))
        {
            throw new InvalidDataException(error);
        }

        return index;
    }

    /// <summary>Parses the index, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out VanBurenResourceIndex index,
        out string error)
    {
        index = null!;
        if (!IsResourceIndex(bytes))
        {
            error = $"{name}: does not open with 1, 1 and a record count that ends on the group table.";
            return false;
        }

        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var groupTable = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var nameTable = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        if (nameTable < groupTable || nameTable > bytes.Length || bytes.Length == 0 || bytes[^1] != 0)
        {
            error =
                $"{name}: the name table at {nameTable} does not sit after the group table and end on a terminator.";
            return false;
        }

        // The group table: NUL-terminated names tiling [groupTable, nameTable) exactly.
        var groupStarts = new Dictionary<int, string>();
        var groups = new List<string>();
        var at = groupTable;
        while (at < nameTable)
        {
            var end = bytes[at..nameTable].IndexOf((byte)0);
            if (end < 0)
            {
                error = $"{name}: the group table does not end on a terminator.";
                return false;
            }

            var groupName = Encoding.ASCII.GetString(bytes.Slice(at, end));
            groupStarts[at - groupTable] = groupName;
            groups.Add(groupName);
            at += end + 1;
        }

        var names = bytes[nameTable..];
        var entries = new List<VanBurenResourceEntry>(count);
        var seen = new HashSet<(string, int)>();
        for (var i = 0; i < count; i++)
        {
            var record = bytes.Slice(HeaderLength + i * RecordLength, RecordLength);
            var one = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var entryIndex = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            var typeId = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            var groupOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            if (one != 1)
            {
                error = $"{name}: record {i} does not open with the constant 1.";
                return false;
            }

            if (!groupStarts.TryGetValue((int)groupOffset, out var group))
            {
                error = $"{name}: record {i} names a group at offset {groupOffset}, which is not a group-name start.";
                return false;
            }

            // ⚑ A name offset must land on a string START — the byte before it is a terminator (or
            // it is the table's first byte). That is what makes the table an index rather than a
            // blob it happens to point into.
            if (nameOffset >= (uint)names.Length || (nameOffset > 0 && names[(int)nameOffset - 1] != 0))
            {
                error = $"{name}: record {i} names offset {nameOffset}, which is not a string start.";
                return false;
            }

            var length = names[(int)nameOffset..].IndexOf((byte)0);
            var entryName = Encoding.Latin1.GetString(names.Slice((int)nameOffset, length));
            if (!seen.Add((group.ToUpperInvariant(), (int)entryIndex)))
            {
                error = $"{name}: {group} entry {entryIndex} is indexed twice.";
                return false;
            }

            entries.Add(new VanBurenResourceEntry(group, (int)entryIndex, typeId, entryName));
        }

        index = new VanBurenResourceIndex(groups, entries);
        error = string.Empty;
        return true;
    }

    /// <summary>The entry for one archive position, or null when the index has none.</summary>
    public VanBurenResourceEntry? Lookup(string group, int index)
    {
        ArgumentNullException.ThrowIfNull(group);
        return _byKey.TryGetValue((group.ToUpperInvariant(), index), out var entry) ? entry : null;
    }

    /// <summary>The entries in one group carrying a name and type, in index order.</summary>
    public IEnumerable<VanBurenResourceEntry> Find(string group, string entryName, uint typeId)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(entryName);
        return Entries
            .Where(e => e.TypeId == typeId
                        && e.Group.Equals(group, StringComparison.OrdinalIgnoreCase)
                        && e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Index);
    }
}
