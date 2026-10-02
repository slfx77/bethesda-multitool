using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     An independent writer of Redguard <c>.3DC</c> frame stacks for the cut-1c slice-6 tests (plan section 2,
///     <c>T/Helpers</c>): the 64-byte header, the frame block at +20 = 64 (six preamble dwords, then the frame table of
///     three dwords per frame on a NARROW stack or four on a WIDE one), the plane list with 8-byte plane headers, and per
///     frame a point block (int32 triples for the keyframe and every wide frame, int16 deltas from the keyframe for a
///     narrow frame), a per-plane normal block (12 bytes wide, 4 narrow) and a per-plane plane-data block (24 bytes wide,
///     12 narrow), optionally with the one unaccounted region the preamble declares. Written from the plan's layout
///     (sections 0.2 and 4), not from <c>Redguard3DcFile</c>, so the reader is never compared with its own writer.
/// </summary>
/// <remarks>
///     Layout: header, frame block and table, plane list, the unaccounted region when
///     <see cref="UnaccountedBeforeFrames" /> is set, then every frame's three blocks back to back, then the region
///     otherwise. The header's +24/+48/+52 carry frame 1's plane-data, point and normal offsets (the retail wide shape;
///     0 for a single-frame stack) and +44 is 1, as on 147 of 147 retail files. Normal blocks are filled with
///     <c>0xA0 + frame</c>, plane-data blocks with <c>0xB0 + frame</c> (both modulo 256, for the long stacks of the
///     budget tests) and the unaccounted region with <c>0xCC</c>, so a test can tell every block of a short stack apart.
/// </remarks>
internal sealed class Redguard3DcTestStackBuilder
{
    /// <summary>Bytes in the fixed header.</summary>
    public const int HeaderLength = 64;

    /// <summary>Where the frame block starts (64 on every retail file).</summary>
    public const int FrameBlockOffset = 64;

    /// <summary>Where the frame table starts: after the six preamble dwords (88 on every retail file).</summary>
    public const int FrameTableOffset = FrameBlockOffset + 24;

    private readonly List<(int X, int Y, int Z)> _keyframe = [];
    private readonly List<byte[]> _planes = [];
    private readonly List<IReadOnlyList<(int X, int Y, int Z)>> _laterFrames = [];
    private readonly string _tag;

    /// <summary>Creates a builder for one tag and frame width.</summary>
    /// <exception cref="ArgumentException">The tag is not four ASCII characters.</exception>
    public Redguard3DcTestStackBuilder(string tag = "v2.6", bool wide = false)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length != 4 || tag.Any(c => c > 0x7E))
        {
            throw new ArgumentException("The tag is four ASCII characters.", nameof(tag));
        }

        _tag = tag;
        Wide = wide;
    }

    /// <summary>True for int32 poses and four-dword records, false for int16 deltas and three-dword records.</summary>
    public bool Wide { get; }

    /// <summary>Header +12, the bounding radius.</summary>
    public uint Radius { get; set; } = 400;

    /// <summary>Preamble dwords 1, 3, 4 and 5 (dword 0 is the table offset, dword 2 the unaccounted length).</summary>
    public (int One, int Three, int Four, int Five) PreambleWords { get; set; } = (11, 8209, 44, 55);

    /// <summary>The length of the one unaccounted region the preamble declares (0 for none).</summary>
    public int UnaccountedLength { get; set; }

    /// <summary>When true the unaccounted region sits between the plane list and the first frame, else at the end.</summary>
    public bool UnaccountedBeforeFrames { get; set; }

    /// <summary>A wide record's fourth dword is this plus the frame ordinal.</summary>
    public int FourthDwordBase { get; set; } = 0x1000;

    /// <summary>The number of keyframe points so far.</summary>
    public int PointCount => _keyframe.Count;

    /// <summary>The number of frames so far, the keyframe included.</summary>
    public int FrameCount => 1 + _laterFrames.Count;

    /// <summary>Where every block went in the last <see cref="Build" />.</summary>
    public StackLayout Layout { get; private set; } = new(0, 0, [], [], [], -1);

    /// <summary>Adds a keyframe point (int32, native units) and returns its index.</summary>
    public int AddPoint(int x, int y, int z)
    {
        _keyframe.Add((x, y, z));
        return _keyframe.Count - 1;
    }

    /// <summary>
    ///     Adds a plane: its u16 texture key and its corners as (point index, stored u, stored v); each corner's point is
    ///     written as its byte offset (index x 12), or on a <c>v2.5</c> stack untripled (index x 4), the encoding a v2.5
    ///     record stores and the gate-1c oracle's <c>walk_planes</c> reads (slice-6 review finding 1).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A corner names a point that was not added.</exception>
    public Redguard3DcTestStackBuilder AddPlane(ushort textureKey, IReadOnlyList<(int Point, short U, short V)> corners,
        byte unknown1 = 0, byte[]? headerTail = null)
    {
        ArgumentNullException.ThrowIfNull(corners);
        var bytes = new byte[8 + corners.Count * 8];
        bytes[0] = checked((byte)corners.Count);
        bytes[1] = unknown1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), textureKey);
        headerTail?.AsSpan(0, 4).CopyTo(bytes.AsSpan(4));
        for (var q = 0; q < corners.Count; q++)
        {
            var (point, u, v) = corners[q];
            if (point < 0 || point >= _keyframe.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(corners), point, "The corner names no added point.");
            }

            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8 + q * 8),
                string.Equals(_tag, "v2.5", StringComparison.Ordinal) ? point * 4 : point * 12);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12 + q * 8), u);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(14 + q * 8), v);
        }

        _planes.Add(bytes);
        return this;
    }

    /// <summary>
    ///     Adds a later frame: on a narrow stack each point's int16 DELTA from the keyframe, on a wide stack each point's
    ///     absolute int32 pose.
    /// </summary>
    /// <exception cref="ArgumentException">The frame does not hold one value per keyframe point, or a delta overflows int16.</exception>
    public Redguard3DcTestStackBuilder AddFrame(IReadOnlyList<(int X, int Y, int Z)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != _keyframe.Count)
        {
            throw new ArgumentException("A frame holds one value per keyframe point.", nameof(values));
        }

        if (!Wide && values.Any(v => v.X is < short.MinValue or > short.MaxValue ||
                                     v.Y is < short.MinValue or > short.MaxValue ||
                                     v.Z is < short.MinValue or > short.MaxValue))
        {
            throw new ArgumentException("A narrow delta is an int16.", nameof(values));
        }

        _laterFrames.Add(values.ToArray());
        return this;
    }

    /// <summary>Writes the stack and records its <see cref="Layout" />.</summary>
    public byte[] Build()
    {
        var frames = FrameCount;
        var recordDwords = Wide ? 4 : 3;
        var planeListOffset = FrameTableOffset + frames * recordDwords * 4;
        var planeListEnd = planeListOffset + _planes.Sum(p => p.Length);
        var normalLength = _planes.Count * (Wide ? 12 : 4);
        var planeDataLength = _planes.Count * (Wide ? 24 : 12);
        var cursor = planeListEnd;
        var unaccountedStart = -1;
        if (UnaccountedLength > 0 && UnaccountedBeforeFrames)
        {
            unaccountedStart = cursor;
            cursor += UnaccountedLength;
        }

        var points = new int[frames];
        var normals = new int[frames];
        var planeData = new int[frames];
        for (var i = 0; i < frames; i++)
        {
            points[i] = cursor;
            normals[i] = points[i] + _keyframe.Count * (i == 0 || Wide ? 12 : 6);
            planeData[i] = normals[i] + normalLength;
            cursor = planeData[i] + planeDataLength;
        }

        if (UnaccountedLength > 0 && !UnaccountedBeforeFrames)
        {
            unaccountedStart = cursor;
            cursor += UnaccountedLength;
        }

        var b = new byte[cursor];
        var span = b.AsSpan();
        Encoding.ASCII.GetBytes(_tag, span[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], _keyframe.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], _planes.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], Radius);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], frames);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], FrameBlockOffset);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], frames > 1 ? planeData[1] : 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[44..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[48..], frames > 1 ? points[1] : 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[52..], frames > 1 ? normals[1] : 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[60..], planeListOffset);

        var (one, three, four, five) = PreambleWords;
        int[] preamble = [FrameTableOffset, one, UnaccountedLength, three, four, five];
        for (var i = 0; i < preamble.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[(FrameBlockOffset + i * 4)..], preamble[i]);
        }

        for (var i = 0; i < frames; i++)
        {
            var record = FrameTableOffset + i * recordDwords * 4;
            BinaryPrimitives.WriteInt32LittleEndian(span[record..], points[i]);
            BinaryPrimitives.WriteInt32LittleEndian(span[(record + 4)..], normals[i]);
            BinaryPrimitives.WriteInt32LittleEndian(span[(record + 8)..], planeData[i]);
            if (Wide)
            {
                BinaryPrimitives.WriteInt32LittleEndian(span[(record + 12)..], FourthDwordBase + i);
            }
        }

        var position = planeListOffset;
        foreach (var plane in _planes)
        {
            plane.CopyTo(span[position..]);
            position += plane.Length;
        }

        for (var j = 0; j < _keyframe.Count; j++)
        {
            WritePoint(span[(points[0] + j * 12)..], _keyframe[j]);
        }

        for (var i = 1; i < frames; i++)
        {
            var values = _laterFrames[i - 1];
            for (var j = 0; j < values.Count; j++)
            {
                if (Wide)
                {
                    WritePoint(span[(points[i] + j * 12)..], values[j]);
                }
                else
                {
                    BinaryPrimitives.WriteInt16LittleEndian(span[(points[i] + j * 6)..], (short)values[j].X);
                    BinaryPrimitives.WriteInt16LittleEndian(span[(points[i] + j * 6 + 2)..], (short)values[j].Y);
                    BinaryPrimitives.WriteInt16LittleEndian(span[(points[i] + j * 6 + 4)..], (short)values[j].Z);
                }
            }
        }

        for (var i = 0; i < frames; i++)
        {
            span.Slice(normals[i], normalLength).Fill(unchecked((byte)(0xA0 + i)));
            span.Slice(planeData[i], planeDataLength).Fill(unchecked((byte)(0xB0 + i)));
        }

        if (unaccountedStart >= 0)
        {
            span.Slice(unaccountedStart, UnaccountedLength).Fill(0xCC);
        }

        Layout = new StackLayout(planeListOffset, planeListEnd, points, normals, planeData, unaccountedStart);
        return b;
    }

    private static void WritePoint(Span<byte> target, (int X, int Y, int Z) point)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target, point.X);
        BinaryPrimitives.WriteInt32LittleEndian(target[4..], point.Y);
        BinaryPrimitives.WriteInt32LittleEndian(target[8..], point.Z);
    }

    /// <summary>Where <see cref="Build" /> put every block.</summary>
    /// <param name="PlaneListOffset">The plane list's start (header +60).</param>
    /// <param name="PlaneListEnd">One past the plane list's last byte.</param>
    /// <param name="Points">Each frame's point block offset.</param>
    /// <param name="Normals">Each frame's normal block offset.</param>
    /// <param name="PlaneData">Each frame's plane-data block offset.</param>
    /// <param name="UnaccountedStart">The unaccounted region's start, or -1 when there is none.</param>
    internal sealed record StackLayout(
        int PlaneListOffset,
        int PlaneListEnd,
        int[] Points,
        int[] Normals,
        int[] PlaneData,
        int UnaccountedStart);
}
