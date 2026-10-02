using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     An independent writer of XnGine <c>.3D</c> records for the cut-1c tests (plan section 2, <c>T/Helpers</c>):
///     the 64-byte header with its named fields (including +16, +20 and +44, which the game identity and the <c>.3DC</c>
///     shape test read), a point list, a normal list, and a plane list with 8-byte (Daggerfall, Redguard) or 10-byte
///     (Battlespire) plane headers, followed by any trailing bytes the test wants after the plane list. Written from the
///     plan's layout tables, not from <c>XnGineMesh</c>, so a reader is never compared with its own writer.
/// </summary>
/// <remarks>
///     Slice 3 needed only what the content facts read: the tag, the counts, the three header words and a plane walk.
///     Slice 5 adds what the <c>.3D</c> reader reads besides: the undecoded header words +36, +40 and +56, a plane's
///     unknown byte and header tail, the 24-byte plane-data area and the object-data area (v2.5 tripled offsets were
///     already honored; since slice 6 the <c>.3DC</c> frame stacks have their own writer,
///     <see cref="Redguard3DcTestStackBuilder" />). Layout: header, points, normals, planes, plane
///     data, object data, trailing bytes; every added area is absent (its header fields 0) unless a test sets it, so
///     the records the slice-3 tests build are unchanged byte for byte.
/// </remarks>
internal sealed class XnGineTestMeshBuilder
{
    /// <summary>Bytes in the fixed header.</summary>
    public const int HeaderLength = 64;

    /// <summary>Bytes per point and per normal.</summary>
    public const int PointLength = 12;

    private readonly List<(int X, int Y, int Z)> _points = [];
    private readonly List<byte[]> _planes = [];
    private readonly List<(int X, int Y, int Z)> _normals = [];
    private readonly List<byte[]> _planeData = [];
    private readonly string _tag;
    private readonly int _planeHeaderLength;

    /// <summary>Creates a builder for one tag (<c>v2.5</c>, <c>v2.6</c>, <c>v2.7</c>, or any 4-character tag for a control) and plane-header size (8 or 10).</summary>
    /// <exception cref="ArgumentException">The tag is not 4 ASCII characters or the header size is neither 8 nor 10.</exception>
    public XnGineTestMeshBuilder(string tag = "v2.7", int planeHeaderLength = 8)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length != 4 || tag.Any(c => c > 0x7E))
        {
            throw new ArgumentException("The tag is four ASCII characters.", nameof(tag));
        }

        if (planeHeaderLength is not (8 or 10))
        {
            throw new ArgumentException("The plane header is 8 bytes (Daggerfall, Redguard) or 10 (Battlespire).",
                nameof(planeHeaderLength));
        }

        _tag = tag;
        _planeHeaderLength = planeHeaderLength;
    }

    /// <summary>Header +12, the bounding radius in native units.</summary>
    public uint Radius { get; set; }

    /// <summary>Header +16: a <c>.3DC</c>'s frame count, 0 on a static mesh.</summary>
    public int HeaderPlus16 { get; set; }

    /// <summary>Header +20: a <c>.3DC</c>'s frame block offset; the Daggerfall discriminator on static meshes.</summary>
    public int HeaderPlus20 { get; set; }

    /// <summary>Header +44: non-zero only on <c>.3DC</c> files.</summary>
    public int HeaderPlus44 { get; set; }

    /// <summary>Header +36, an undecoded dword the reader keeps in native state.</summary>
    public uint HeaderPlus36 { get; set; }

    /// <summary>Header +40, an undecoded dword the reader keeps in native state.</summary>
    public uint HeaderPlus40 { get; set; }

    /// <summary>Header +56, an undecoded dword the reader keeps in native state.</summary>
    public uint HeaderPlus56 { get; set; }

    /// <summary>
    ///     When true the record carries the 24-byte-per-plane plane-data area after the plane list (+24 points at it);
    ///     each plane's 24 bytes are the ones <see cref="AddPlane" /> was given, zeros by default.
    /// </summary>
    public bool WritePlaneData { get; set; }

    /// <summary>Header +32, the object-data count; with a positive count <see cref="ObjectData" /> follows the plane data (+28 points at it).</summary>
    public int ObjectDataCount { get; set; }

    /// <summary>The object-data bytes (written only when <see cref="ObjectDataCount" /> is positive).</summary>
    public byte[] ObjectData { get; set; } = [];

    /// <summary>Bytes appended after every area (a test control for walks that run past the list).</summary>
    public byte[] TrailingBytes { get; set; } = [];

    /// <summary>The number of points added so far.</summary>
    public int PointCount => _points.Count;

    /// <summary>The number of planes added so far.</summary>
    public int PlaneCount => _planes.Count;

    /// <summary>Adds a point in native units and returns its index.</summary>
    public int AddPoint(int x, int y, int z)
    {
        _points.Add((x, y, z));
        return _points.Count - 1;
    }

    /// <summary>
    ///     Adds a plane: its texture key (u16 under 8-byte headers, u32 under 10-byte headers), its corners as
    ///     (point index, stored u, stored v), and its authored normal (default +Y at unit length 256). The corner's
    ///     point index is written as the point's BYTE offset, divided by three on a <c>v2.5</c> record.
    /// </summary>
    /// <param name="textureKey">The key: u16 under 8-byte headers, u32 under 10-byte headers.</param>
    /// <param name="corners">The corners as (point index, stored u, stored v).</param>
    /// <param name="normal">The authored plane normal; default (0, 256, 0).</param>
    /// <param name="unknown1">The byte after the corner count.</param>
    /// <param name="headerTail">The four bytes after the key (the last four header bytes in both layouts); zeros by default.</param>
    /// <param name="planeData">The plane's 24 plane-data bytes, written when <see cref="WritePlaneData" /> is set; zeros by default.</param>
    /// <exception cref="ArgumentOutOfRangeException">A corner names a point that was not added.</exception>
    /// <exception cref="ArgumentException">The header tail or plane data has the wrong length.</exception>
    public XnGineTestMeshBuilder AddPlane(uint textureKey, IReadOnlyList<(int Point, short U, short V)> corners,
        (int X, int Y, int Z)? normal = null, byte unknown1 = 0, byte[]? headerTail = null, byte[]? planeData = null)
    {
        ArgumentNullException.ThrowIfNull(corners);
        if (headerTail is not null && headerTail.Length != 4)
        {
            throw new ArgumentException("The header tail after the key is four bytes.", nameof(headerTail));
        }

        if (planeData is not null && planeData.Length != 24)
        {
            throw new ArgumentException("A plane-data record is 24 bytes.", nameof(planeData));
        }

        var bytes = new byte[_planeHeaderLength + corners.Count * 8];
        bytes[0] = checked((byte)corners.Count);
        bytes[1] = unknown1;
        headerTail?.CopyTo(bytes.AsSpan(_planeHeaderLength - 4));
        if (_planeHeaderLength == 10)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), textureKey);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), checked((ushort)textureKey));
        }

        var position = _planeHeaderLength;
        foreach (var (point, u, v) in corners)
        {
            if (point < 0 || point >= _points.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(corners), point, "The corner names no added point.");
            }

            var byteOffset = point * PointLength;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(position),
                string.Equals(_tag, "v2.5", StringComparison.Ordinal) ? byteOffset / 3 : byteOffset);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(position + 4), u);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(position + 6), v);
            position += 8;
        }

        _planes.Add(bytes);
        _normals.Add(normal ?? (0, 256, 0));
        _planeData.Add(planeData ?? new byte[24]);
        return this;
    }

    /// <summary>
    ///     Adds a plane whose corners all name the same point with zero UVs. With exactly one such plane and two zero
    ///     trailing bytes the record walks under BOTH header sizes (the 10-byte reading takes the corners two bytes
    ///     later, where the zero bytes still address point 0), the shape of the 20 ARCH3D records that walk both ways.
    /// </summary>
    public XnGineTestMeshBuilder AddDegeneratePlane(int point, int cornerCount = 3)
    {
        var corners = new (int, short, short)[cornerCount];
        for (var i = 0; i < cornerCount; i++)
        {
            corners[i] = (point, 0, 0);
        }

        return AddPlane(0, corners);
    }

    /// <summary>Writes the record.</summary>
    public byte[] Build()
    {
        var pointListOffset = HeaderLength;
        var normalListOffset = pointListOffset + _points.Count * PointLength;
        var planeListOffset = normalListOffset + _normals.Count * PointLength;
        var planeBytes = _planes.Sum(p => p.Length);
        var planeDataOffset = WritePlaneData ? planeListOffset + planeBytes : 0;
        var planeDataBytes = WritePlaneData ? _planeData.Count * 24 : 0;
        var objectDataOffset = ObjectDataCount > 0 ? planeListOffset + planeBytes + planeDataBytes : 0;
        var objectDataBytes = ObjectDataCount > 0 ? ObjectData.Length : 0;
        var record = new byte[planeListOffset + planeBytes + planeDataBytes + objectDataBytes + TrailingBytes.Length];
        var span = record.AsSpan();

        Encoding.ASCII.GetBytes(_tag, span[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], _points.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], _planes.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], Radius);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], HeaderPlus16);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], HeaderPlus20);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], planeDataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], objectDataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(span[32..], ObjectDataCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..], HeaderPlus36);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], HeaderPlus40);
        BinaryPrimitives.WriteInt32LittleEndian(span[44..], HeaderPlus44);
        BinaryPrimitives.WriteInt32LittleEndian(span[48..], pointListOffset);
        BinaryPrimitives.WriteInt32LittleEndian(span[52..], normalListOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[56..], HeaderPlus56);
        BinaryPrimitives.WriteInt32LittleEndian(span[60..], planeListOffset);

        var position = pointListOffset;
        foreach (var (x, y, z) in _points)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[position..], x);
            BinaryPrimitives.WriteInt32LittleEndian(span[(position + 4)..], y);
            BinaryPrimitives.WriteInt32LittleEndian(span[(position + 8)..], z);
            position += PointLength;
        }

        foreach (var (x, y, z) in _normals)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[position..], x);
            BinaryPrimitives.WriteInt32LittleEndian(span[(position + 4)..], y);
            BinaryPrimitives.WriteInt32LittleEndian(span[(position + 8)..], z);
            position += PointLength;
        }

        foreach (var plane in _planes)
        {
            plane.CopyTo(span[position..]);
            position += plane.Length;
        }

        if (WritePlaneData)
        {
            foreach (var data in _planeData)
            {
                data.CopyTo(span[position..]);
                position += data.Length;
            }
        }

        if (ObjectDataCount > 0)
        {
            ObjectData.CopyTo(span[position..]);
            position += ObjectData.Length;
        }

        TrailingBytes.CopyTo(span[position..]);
        return record;
    }

    /// <summary>A four-point, one-plane <c>v2.7</c> record with 8-byte plane headers and the given header +20 (Daggerfall or Redguard shape).</summary>
    public static byte[] EightByteRecord(int headerPlus20 = 0, string tag = "v2.7")
    {
        return Square(tag, 8, headerPlus20).Build();
    }

    /// <summary>A four-point, one-plane <c>v2.7</c> record with 10-byte plane headers (the Battlespire shape).</summary>
    public static byte[] TenByteRecord(int headerPlus20 = 0, string tag = "v2.7")
    {
        return Square(tag, 10, headerPlus20).Build();
    }

    /// <summary>A record that walks under both header sizes (see <see cref="AddDegeneratePlane" />).</summary>
    public static byte[] BothWaysRecord(int headerPlus20 = 0)
    {
        var builder = new XnGineTestMeshBuilder("v2.7", 8) { HeaderPlus20 = headerPlus20, TrailingBytes = [0, 0] };
        builder.AddPoint(0, 0, 0);
        builder.AddPoint(256, 0, 0);
        return builder.AddDegeneratePlane(0).Build();
    }

    /// <summary>A unit square in the XZ plane at native scale with one 4-corner plane keyed 0x0182 (archive 3, record 2).</summary>
    private static XnGineTestMeshBuilder Square(string tag, int planeHeaderLength, int headerPlus20)
    {
        var builder = new XnGineTestMeshBuilder(tag, planeHeaderLength) { HeaderPlus20 = headerPlus20, Radius = 362 };
        builder.AddPoint(0, 0, 0);
        builder.AddPoint(256, 0, 0);
        builder.AddPoint(256, 0, 256);
        builder.AddPoint(0, 0, 256);
        return builder.AddPlane(0x0182, [(0, 0, 0), (1, 1024, 0), (2, 0, 1024), (3, 0, 0)]);
    }
}
