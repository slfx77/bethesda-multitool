// Chunk grammar and the group/leaf tag list follow ariscop/battlespire-tools
//   (https://github.com/ariscop/battlespire-tools, Unlicense/public domain) — bs6tool/bs6tool.py.
//   License texts are collected centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>A three-component vector as BS6 stores it: three little-endian i32s in block units.</summary>
internal readonly record struct Bs6Vector(int X, int Y, int Z);

/// <summary>One placed mesh: which entry of the level's mesh list it uses, and where it sits.</summary>
internal readonly record struct Bs6Object(int Id, int MeshIndex, Bs6Vector Position, Bs6Vector Angles, int Selected);

/// <summary>One placed light. Only its identity, position, brightness and radius are understood.</summary>
internal readonly record struct Bs6Light(int Id, Bs6Vector Position, int Brightness, int Radius);

/// <summary>One placed flat (sprite): the file it names, where it sits and its scale.</summary>
internal readonly record struct Bs6Flat(int Id, string FileName, Bs6Vector Position, int Scale);

/// <summary>One node of the chunk tree: its tag, its payload, and any children it groups.</summary>
internal sealed class Bs6Chunk
{
    public required string Tag { get; init; }

    /// <summary>The chunk's payload, children included.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>Child chunks, empty for a leaf.</summary>
    public required IReadOnlyList<Bs6Chunk> Children { get; init; }

    /// <summary>Every chunk in the subtree, this one first.</summary>
    public IEnumerable<Bs6Chunk> Descend()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.Descend())
            {
                yield return node;
            }
        }
    }

    /// <summary>The first descendant carrying a tag, or null.</summary>
    public Bs6Chunk? Find(string tag)
    {
        return Descend().FirstOrDefault(c => c.Tag == tag);
    }
}

/// <summary>
///     A Battlespire level, <c>*.BS6</c>: a tree of chunks, each a 4-byte ASCII tag and a
///     little-endian u32 length, rooted at <c>GNRL</c>. The level's geometry is by reference —
///     <c>OBJS/LFIL</c> lists 260-byte mesh file names and each <c>OBJS/OBJD</c> places one of them
///     by index (<c>IDFI</c>) with a position and Euler angles.
///     <para>
///         Measured on the retail <c>BS6.BSA</c> (2026-09-03): 47 entries, of which 45 parse as
///         levels holding 3,500 mesh-list names (3,456 of them resolving to a real <c>.3D</c> in
///         3D.BSA or beside it) and 7,428 object placements, 7,426 of whose indices land inside
///         their own level's list. The other two entries are not levels: <c>ADR.TXT</c> is a text
///         file, and the entry named <c>C</c> is truncated and has a second level's <c>GNRL</c>
///         spliced inside its light list.
///     </para>
/// </summary>
internal sealed class Bs6File
{
    /// <summary>Bytes in each name of a mesh list.</summary>
    public const int NameLength = 260;

    /// <summary>Tags whose payload is a further chunk sequence rather than data.</summary>
    private static readonly HashSet<string> GroupTags =
    [
        "GNRL", "TEXI", "STRU", "SNAP", "VIEW", "CTRL", "LINK", "OBJS", "OBJD", "LITS", "LITD", "FLAS", "FLAD"
    ];

    private const int ChunkHeaderLength = 8;

    private Bs6File(string name, Bs6Chunk root)
    {
        Name = name;
        Root = root;
    }

    /// <summary>Logical file name.</summary>
    public string Name { get; }

    /// <summary>The <c>GNRL</c> root.</summary>
    public Bs6Chunk Root { get; }

    /// <summary>The level's mesh list: the names <c>OBJD</c> entries index into.</summary>
    public IReadOnlyList<string> MeshNames { get; private set; } = [];

    /// <summary>Placed meshes.</summary>
    public IReadOnlyList<Bs6Object> Objects { get; private set; } = [];

    /// <summary>Placed lights.</summary>
    public IReadOnlyList<Bs6Light> Lights { get; private set; } = [];

    /// <summary>Placed flats.</summary>
    public IReadOnlyList<Bs6Flat> Flats { get; private set; } = [];

    /// <summary>The authoring directory the level was built from, when it carries one.</summary>
    public string? TextureDirectory { get; private set; }

    /// <summary>The level's bounding box, as two corners in block units.</summary>
    public (Bs6Vector Min, Bs6Vector Max)? BoundingBox { get; private set; }

    /// <summary>The level's bounding radius.</summary>
    public int Radius { get; private set; }

    /// <summary>The level's centre.</summary>
    public Bs6Vector Center { get; private set; }

    /// <summary>The <c>WATR</c> word: non-zero on levels the engine floods.</summary>
    public int Water { get; private set; }

    /// <summary>The <c>BITS</c> word (flags, meaning unknown).</summary>
    public int Bits { get; private set; }

    /// <summary>Camera/view records.</summary>
    public int ViewCount { get; private set; }

    /// <summary>Snap-grid records.</summary>
    public int SnapCount { get; private set; }

    /// <summary>True for a <c>.BS6</c> name.</summary>
    public static bool IsBs6FileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.EndsWith(".BS6", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses a complete level file.</summary>
    public static Bs6File Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        var top = ReadChunks(new ReadOnlyMemory<byte>(bytes), name, 0);
        if (top.Count == 0 || top[0].Tag != "GNRL")
        {
            throw new InvalidDataException($"{name} does not open with a GNRL chunk.");
        }

        var file = new Bs6File(name, top[0]);
        file.Interpret();
        return file;
    }

    private static List<Bs6Chunk> ReadChunks(ReadOnlyMemory<byte> bytes, string name, int depth)
    {
        if (depth > 8)
        {
            throw new InvalidDataException($"{name}: chunk nesting deeper than 8 levels.");
        }

        var chunks = new List<Bs6Chunk>();
        var span = bytes.Span;
        var position = 0;
        while (position + ChunkHeaderLength <= span.Length)
        {
            var tag = Encoding.ASCII.GetString(span.Slice(position, 4));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(span[(position + 4)..]);
            if (position + ChunkHeaderLength + length > (uint)span.Length)
            {
                throw new InvalidDataException(
                    $"{name}: chunk '{Printable(tag)}' at {position} declares {length} bytes, past the {span.Length} that remain.");
            }

            var payload = bytes.Slice(position + ChunkHeaderLength, (int)length);
            chunks.Add(new Bs6Chunk
            {
                Tag = tag,
                Payload = payload,
                Children = GroupTags.Contains(tag) ? ReadChunks(payload, name, depth + 1) : []
            });

            position += ChunkHeaderLength + (int)length;
        }

        return chunks;
    }

    /// <summary>Reads the parts of the tree whose meaning is established.</summary>
    private void Interpret()
    {
        var objectGroup = Root.Children.FirstOrDefault(c => c.Tag == "OBJS");
        if (objectGroup is not null)
        {
            var list = objectGroup.Children.FirstOrDefault(c => c.Tag == "LFIL");
            if (list is not null)
            {
                MeshNames = ReadNameList(list.Payload.Span);
            }

            Objects = [.. objectGroup.Children.Where(c => c.Tag == "OBJD").Select(ReadObject)];
        }

        var lightGroup = Root.Children.FirstOrDefault(c => c.Tag == "LITS");
        if (lightGroup is not null)
        {
            Lights = [.. lightGroup.Children.Where(c => c.Tag == "LITD").Select(ReadLight)];
        }

        var flatGroup = Root.Children.FirstOrDefault(c => c.Tag == "FLAS");
        if (flatGroup is not null)
        {
            Flats = [.. flatGroup.Children.Where(c => c.Tag == "FLAD").Select(ReadFlat)];
        }

        TextureDirectory = Root.Children.FirstOrDefault(c => c.Tag == "TEXI")?.Find("DIRN") is { } directory
            ? ReadCString(directory.Payload.Span)
            : null;

        if (Root.Children.FirstOrDefault(c => c.Tag == "BBOX") is { } box && box.Payload.Length >= 24)
        {
            BoundingBox = (ReadVector(box.Payload.Span), ReadVector(box.Payload.Span[12..]));
        }

        Radius = ReadInt(Root, "RADI");
        Water = ReadInt(Root, "WATR");
        Bits = ReadInt(Root, "BITS");
        if (Root.Children.FirstOrDefault(c => c.Tag == "CENT") is { } centre && centre.Payload.Length >= 12)
        {
            Center = ReadVector(centre.Payload.Span);
        }

        ViewCount = Root.Children.Count(c => c.Tag == "VIEW");
        SnapCount = Root.Children.Count(c => c.Tag == "SNAP");
    }

    private static Bs6Object ReadObject(Bs6Chunk chunk)
    {
        return new Bs6Object(
            ReadInt(chunk, "IDNB"),
            ReadInt(chunk, "IDFI"),
            ReadVectorChunk(chunk, "POSI"),
            ReadVectorChunk(chunk, "ANGS"),
            ReadInt(chunk, "SELE"));
    }

    private static Bs6Light ReadLight(Bs6Chunk chunk)
    {
        return new Bs6Light(
            ReadInt(chunk, "IDNB"),
            ReadVectorChunk(chunk, "POSI"),
            ReadInt(chunk, "BRIT"),
            ReadInt(chunk, "RADI"));
    }

    private static Bs6Flat ReadFlat(Bs6Chunk chunk)
    {
        var name = chunk.Children.FirstOrDefault(c => c.Tag == "FILN");
        return new Bs6Flat(
            ReadInt(chunk, "IDNB"),
            name is null ? string.Empty : ReadCString(name.Payload.Span),
            ReadVectorChunk(chunk, "POSI"),
            ReadInt(chunk, "SCAL"));
    }

    /// <summary>The names of a <c>LFIL</c> list, each in a fixed 260-byte slot.</summary>
    private static string[] ReadNameList(ReadOnlySpan<byte> payload)
    {
        var count = payload.Length / NameLength;
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = ReadCString(payload.Slice(i * NameLength, NameLength));
        }

        return names;
    }

    private static int ReadInt(Bs6Chunk chunk, string tag)
    {
        var child = chunk.Children.FirstOrDefault(c => c.Tag == tag);
        return child is { Payload.Length: >= 4 } ? BinaryPrimitives.ReadInt32LittleEndian(child.Payload.Span) : 0;
    }

    private static Bs6Vector ReadVectorChunk(Bs6Chunk chunk, string tag)
    {
        var child = chunk.Children.FirstOrDefault(c => c.Tag == tag);
        return child is { Payload.Length: >= 12 } ? ReadVector(child.Payload.Span) : default;
    }

    private static Bs6Vector ReadVector(ReadOnlySpan<byte> payload)
    {
        return new Bs6Vector(
            BinaryPrimitives.ReadInt32LittleEndian(payload),
            BinaryPrimitives.ReadInt32LittleEndian(payload[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[8..]));
    }

    private static string ReadCString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private static string Printable(string tag)
    {
        var builder = new StringBuilder(tag.Length);
        foreach (var character in tag)
        {
            builder.Append(char.IsControl(character) ? '.' : character);
        }

        return builder.ToString();
    }
}
