using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Redguard's animated mesh, <c>.3DC</c>. Original RE (2026-09-06) — the MIT exporter
///     (RGUnity/redguard-file-exporter) carries a reader that its own comments admit does not work
///     ("Some old 3DC files have a different vertex start offset for some unknown reason"), so
///     nothing here is ported.
///     <para>
///         A <c>.3DC</c> is an ordinary <see cref="XnGineMesh" /> whose <b>geometry is a stack of
///         poses</b>. The 64-byte header and the plane (face) list are byte-for-byte Daggerfall's,
///         which is why a <c>.3DC</c> parses as a <c>.3D</c> and yields wrong points rather than an
///         error: the header's point/normal/plane-data offsets at +48/+52/+24 are <b>frame 1's</b>,
///         not the mesh's. The real geometry is named by a FRAME TABLE:
///     </para>
///     <list type="bullet">
///         <item>Header +20 gives the frame-block offset (64 on every retail file — the block starts
///         immediately after the header). Its first dword is the frame table's own offset (88 on
///         147/147) and its <b>third dword is the length of one unaccounted region</b>.</item>
///         <item>The table holds <see cref="FrameCount" /> records of three or four dwords —
///         <c>(vertexOffset, normalOffset, planeDataOffset[, unknown])</c>. Retail splits 110 three-
///         dword / 37 four-dword.</item>
///         <item>Frame 0 is a KEYFRAME of <c>PointCount</c> int32 triples. Later frames are either
///         int32 triples again (37 "wide" files) or <b>int16 deltas added to the keyframe</b>
///         (110 "narrow" files) — never accumulated frame-to-frame.</item>
///     </list>
///     <para>
///         ⚠ That third dword is what the reference reads as its vertex base (<c>endFaceData + u3</c>).
///         It works only when the unaccounted region happens to sit between the face list and the
///         first frame; when the region sits at the end of the file instead, the rule lands mid-pose.
///         That is the whole of the "unknown reason", and the frame table makes the heuristic
///         unnecessary.
///     </para>
///     <para>
///         ⛔ Do NOT gate acceptance on the header's Radius. It reproduces as the maximum distance
///         from the origin on all 52 static <c>.3D</c> controls, but a <c>.3DC</c> holds up to 228
///         poses, so "some frame matches within 2%" happens by chance — the control cannot
///         discriminate. Acceptance here is EXACT TILING instead: header, frame block, face list and
///         every frame's three blocks must cover the file with no overlap and exactly the one
///         declared unaccounted region. All 147 retail files satisfy that; a file that does not is
///         reported unparsed rather than decoded into plausible-looking rubbish.
///     </para>
/// </summary>
internal sealed class Redguard3dcFile
{
    /// <summary>Bytes per point in a keyframe, and in every frame of a "wide" file.</summary>
    public const int WidePointLength = 12;

    /// <summary>Bytes per point in a delta frame of a "narrow" file.</summary>
    public const int NarrowPointLength = 6;

    /// <summary>Dwords of frame-block preamble before the frame table.</summary>
    public const int PreambleDwords = 6;

    private Redguard3dcFile(
        string name,
        XnGineMesh keyframeMesh,
        IReadOnlyList<Redguard3dcFrame> frames,
        bool wideFrames,
        int frameRecordDwords,
        int unaccountedLength)
    {
        Name = name;
        KeyframeMesh = keyframeMesh;
        Frames = frames;
        WideFrames = wideFrames;
        FrameRecordDwords = frameRecordDwords;
        UnaccountedLength = unaccountedLength;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The mesh in its keyframe pose — points, planes and computed normals.</summary>
    public XnGineMesh KeyframeMesh { get; }

    /// <summary>Every pose, keyframe first.</summary>
    public IReadOnlyList<Redguard3dcFrame> Frames { get; }

    /// <summary>True when later frames store int32 triples rather than int16 deltas.</summary>
    public bool WideFrames { get; }

    /// <summary>Dwords per frame-table record: three or four.</summary>
    public int FrameRecordDwords { get; }

    /// <summary>Length of the one region the tiling leaves unaccounted, as the file declares it.</summary>
    public int UnaccountedLength { get; }

    /// <summary>Number of poses.</summary>
    public int FrameCount => Frames.Count;

    /// <summary>Content probe: a <c>.3DC</c> is exactly the file whose blocks tile under this reader.</summary>
    public static bool Is3dcFile(ReadOnlyMemory<byte> bytes)
    {
        return TryParse(bytes, "probe", out _, out _);
    }

    /// <summary>Parses the mesh, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static Redguard3dcFile Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var file, out var error))
        {
            throw new InvalidDataException(error);
        }

        return file;
    }

    /// <summary>
    ///     Parses the mesh, reporting why rather than throwing. No field declares the frame width,
    ///     so the reading that TILES is the answer — the frame record width only says which to try
    ///     first. That matters for the single retail file with fewer than three frames, where no
    ///     stride exists to compare and the tiling is the only evidence there is.
    /// </summary>
    public static bool TryParse(
        ReadOnlyMemory<byte> bytes,
        string name,
        out Redguard3dcFile file,
        out string error)
    {
        file = null!;
        var span = bytes.Span;
        if (span.Length < XnGineMesh.HeaderLength)
        {
            error = $"{name}: {span.Length} bytes is shorter than the {XnGineMesh.HeaderLength}-byte header.";
            return false;
        }

        var pointCount = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        var planeCount = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        var frameCount = BinaryPrimitives.ReadInt32LittleEndian(span[16..]);
        var frameBlockOffset = BinaryPrimitives.ReadInt32LittleEndian(span[20..]);
        var planeListOffset = BinaryPrimitives.ReadInt32LittleEndian(span[60..]);

        if (pointCount <= 0 || planeCount <= 0 || frameCount <= 0)
        {
            error = $"{name}: {pointCount} points, {planeCount} planes and {frameCount} frames are not all positive.";
            return false;
        }

        if (frameBlockOffset < 0 || frameBlockOffset + (PreambleDwords * 4) > span.Length)
        {
            error = $"{name}: the frame block at {frameBlockOffset} does not fit the file.";
            return false;
        }

        var tableOffset = BinaryPrimitives.ReadInt32LittleEndian(span[frameBlockOffset..]);
        var unaccounted = BinaryPrimitives.ReadInt32LittleEndian(span[(frameBlockOffset + 8)..]);
        if (tableOffset < 0 || tableOffset > planeListOffset || planeListOffset > span.Length)
        {
            error = $"{name}: the frame table at {tableOffset} does not precede the plane list at {planeListOffset}.";
            return false;
        }

        var tableBytes = planeListOffset - tableOffset;
        if (tableBytes % (4 * frameCount) != 0)
        {
            error = $"{name}: {tableBytes} bytes of frame table do not divide into {frameCount} records.";
            return false;
        }

        var recordDwords = tableBytes / (4 * frameCount);
        if (recordDwords is < 3 or > 4)
        {
            error = $"{name}: a frame record of {recordDwords} dwords is neither of the two known shapes.";
            return false;
        }

        var table = new FrameTableEntry[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            var at = tableOffset + (i * recordDwords * 4);
            table[i] = new FrameTableEntry(
                BinaryPrimitives.ReadInt32LittleEndian(span[at..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[(at + 4)..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[(at + 8)..]));
        }

        // No field declares the frame width, but the frame RECORD width predicts it on all 147
        // retail files — four dwords always means full int32 poses. That is a correlation, not a
        // definition, so it only picks which reading to try first; the tiling is what accepts one.
        string? predictedFailure = null;
        foreach (var wide in recordDwords == 4 ? new[] { true, false } : new[] { false, true })
        {
            if (TryBuild(bytes, name, new Geometry(pointCount, planeCount, frameCount, planeListOffset,
                    frameBlockOffset, tableOffset, recordDwords, unaccounted, wide), table, out file, out var attempt))
            {
                error = string.Empty;
                return true;
            }

            // Report why the PREDICTED reading failed. The fallback's complaint is about a width the
            // file was never going to have, so letting it overwrite this buries the real reason.
            predictedFailure ??= attempt;
        }

        error = predictedFailure ?? $"{name}: neither frame width tiles the file.";
        return false;
    }

    /// <summary>One frame table record: where this pose's three blocks start.</summary>
    private readonly record struct FrameTableEntry(int PointOffset, int NormalOffset, int PlaneDataOffset);

    /// <summary>Everything the tiling and the readers need that the header settles.</summary>
    private readonly record struct Geometry(
        int PointCount,
        int PlaneCount,
        int FrameCount,
        int PlaneListOffset,
        int FrameBlockOffset,
        int TableOffset,
        int RecordDwords,
        int UnaccountedLength,
        bool Wide);

    private static bool TryBuild(
        ReadOnlyMemory<byte> bytes,
        string name,
        Geometry geometry,
        FrameTableEntry[] table,
        out Redguard3dcFile file,
        out string error)
    {
        file = null!;
        var span = bytes.Span;

        // The plane walk is Daggerfall's, and its end is one of the tiling's blocks.
        if (!TryWalkPlaneList(span, geometry, out var planeListEnd, out error))
        {
            return false;
        }

        var blocks = new List<(int Start, int End)>
        {
            (0, XnGineMesh.HeaderLength),
            (geometry.FrameBlockOffset, geometry.PlaneListOffset),
            (geometry.PlaneListOffset, planeListEnd)
        };

        for (var i = 0; i < geometry.FrameCount; i++)
        {
            var pointLength = i == 0 || geometry.Wide ? WidePointLength : NarrowPointLength;
            var normalLength = geometry.Wide ? 12 : 4;
            var planeDataLength = geometry.Wide ? 24 : 12;
            blocks.Add((table[i].PointOffset, table[i].PointOffset + (geometry.PointCount * pointLength)));
            blocks.Add((table[i].NormalOffset, table[i].NormalOffset + (geometry.PlaneCount * normalLength)));
            blocks.Add((table[i].PlaneDataOffset, table[i].PlaneDataOffset + (geometry.PlaneCount * planeDataLength)));
        }

        if (!Tiles(blocks, span.Length, geometry.UnaccountedLength, name, out error))
        {
            return false;
        }

        var frames = new Redguard3dcFrame[geometry.FrameCount];
        var keyframe = ReadKeyframe(span, geometry, table[0].PointOffset);
        frames[0] = new Redguard3dcFrame(0, table[0].PointOffset, keyframe);
        for (var i = 1; i < geometry.FrameCount; i++)
        {
            frames[i] = new Redguard3dcFrame(i, table[i].PointOffset,
                geometry.Wide
                    ? ReadKeyframe(span, geometry, table[i].PointOffset)
                    : ReadDeltaFrame(span, geometry, table[i].PointOffset, keyframe));
        }

        var mesh = XnGineMesh.Parse(bytes, 0, XnGineMeshLayout.Daggerfall, keyframe,
            ComputeNormals(keyframe, bytes, geometry));
        file = new Redguard3dcFile(name, mesh, frames, geometry.Wide, geometry.RecordDwords,
            geometry.UnaccountedLength);
        error = string.Empty;
        return true;
    }

    /// <summary>Walks the plane list purely to find where it ends — the reader itself is shared.</summary>
    private static bool TryWalkPlaneList(ReadOnlySpan<byte> span, Geometry geometry, out int end, out string error)
    {
        const int planeHeaderLength = 8;
        const int planePointLength = 8;
        end = geometry.PlaneListOffset;
        for (var i = 0; i < geometry.PlaneCount; i++)
        {
            if (end + planeHeaderLength > span.Length)
            {
                error = $"the plane list ends inside plane {i}'s header.";
                return false;
            }

            end += planeHeaderLength + (span[end] * planePointLength);
            if (end > span.Length)
            {
                error = $"plane {i} runs past the end of the file.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     The acceptance gate: every block must tile the file, leaving only the one region the
    ///     frame block declares. Overlap is rejected outright — it is what a wrong frame width
    ///     produces, and it is exactly how the narrow layout was found.
    /// </summary>
    private static bool Tiles(
        List<(int Start, int End)> blocks,
        int length,
        int unaccounted,
        string name,
        out string error)
    {
        foreach (var (start, blockEnd) in blocks)
        {
            if (start < 0 || blockEnd > length)
            {
                error = $"{name}: a block at {start}..{blockEnd} lies outside the {length}-byte file.";
                return false;
            }
        }

        blocks.Sort((a, b) => a.Start.CompareTo(b.Start));
        var cursor = 0;
        var gaps = 0;
        var uncovered = 0;
        foreach (var (start, blockEnd) in blocks)
        {
            if (start > cursor)
            {
                gaps++;
                uncovered += start - cursor;
            }
            else if (start < cursor)
            {
                error = $"{name}: blocks overlap at {start} (covered to {cursor}).";
                return false;
            }

            cursor = Math.Max(cursor, blockEnd);
        }

        if (cursor < length)
        {
            gaps++;
            uncovered += length - cursor;
        }

        if (gaps > 1 || uncovered != unaccounted)
        {
            error = $"{name}: {uncovered} bytes in {gaps} gaps, but the file declares one region of {unaccounted}.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static XnGineMeshPoint[] ReadKeyframe(ReadOnlySpan<byte> span, Geometry geometry, int offset)
    {
        var points = new XnGineMeshPoint[geometry.PointCount];
        for (var i = 0; i < points.Length; i++)
        {
            var at = offset + (i * WidePointLength);
            points[i] = new XnGineMeshPoint(
                BinaryPrimitives.ReadInt32LittleEndian(span[at..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[(at + 4)..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[(at + 8)..]));
        }

        return points;
    }

    /// <summary>
    ///     A narrow frame: int16 offsets from the KEYFRAME. Measured on all 91 retail files with ten
    ///     or more frames — accumulating them frame-to-frame instead makes the mesh drift, in every
    ///     one of the 91.
    /// </summary>
    private static XnGineMeshPoint[] ReadDeltaFrame(
        ReadOnlySpan<byte> span,
        Geometry geometry,
        int offset,
        XnGineMeshPoint[] keyframe)
    {
        var points = new XnGineMeshPoint[geometry.PointCount];
        for (var i = 0; i < points.Length; i++)
        {
            var at = offset + (i * NarrowPointLength);
            points[i] = new XnGineMeshPoint(
                keyframe[i].X + BinaryPrimitives.ReadInt16LittleEndian(span[at..]),
                keyframe[i].Y + BinaryPrimitives.ReadInt16LittleEndian(span[(at + 2)..]),
                keyframe[i].Z + BinaryPrimitives.ReadInt16LittleEndian(span[(at + 4)..]));
        }

        return points;
    }

    /// <summary>
    ///     Plane normals computed from the geometry (Newell's method, scaled to the format's
    ///     fixed point where 256 is 1.0). A <c>.3DC</c> stores none that can be read as a <c>.3D</c>
    ///     normal list: the narrow files give each plane FOUR bytes per frame, not twelve.
    /// </summary>
    private static XnGineMeshPoint[] ComputeNormals(
        XnGineMeshPoint[] points,
        ReadOnlyMemory<byte> bytes,
        Geometry geometry)
    {
        var span = bytes.Span;
        var normals = new XnGineMeshPoint[geometry.PlaneCount];
        var position = geometry.PlaneListOffset;
        for (var i = 0; i < geometry.PlaneCount; i++)
        {
            var planePoints = span[position];
            var first = position + 8;
            double nx = 0, ny = 0, nz = 0;
            for (var q = 0; q < planePoints; q++)
            {
                var a = points[PointIndex(span, first + (q * 8), points.Length)];
                var b = points[PointIndex(span, first + (((q + 1) % planePoints) * 8), points.Length)];
                nx += (a.Y - (double)b.Y) * (a.Z + (double)b.Z);
                ny += (a.Z - (double)b.Z) * (a.X + (double)b.X);
                nz += (a.X - (double)b.X) * (a.Y + (double)b.Y);
            }

            var scale = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            normals[i] = scale <= 0
                ? new XnGineMeshPoint(0, 0, 0)
                : new XnGineMeshPoint(
                    (int)Math.Round(nx / scale * XnGineMesh.PointDivisor),
                    (int)Math.Round(ny / scale * XnGineMesh.PointDivisor),
                    (int)Math.Round(nz / scale * XnGineMesh.PointDivisor));
            position = first + (planePoints * 8);
        }

        return normals;
    }

    private static int PointIndex(ReadOnlySpan<byte> span, int at, int pointCount)
    {
        var offset = BinaryPrimitives.ReadInt32LittleEndian(span[at..]) / WidePointLength;
        return offset >= 0 && offset < pointCount ? offset : 0;
    }
}

/// <summary>One pose of a <see cref="Redguard3dcFile" />, already resolved to absolute points.</summary>
/// <param name="Index">Position in the frame table.</param>
/// <param name="Offset">Where the frame's own bytes start, for diagnostics.</param>
/// <param name="Points">The pose, in native units — deltas already applied.</param>
internal readonly record struct Redguard3dcFrame(int Index, int Offset, IReadOnlyList<XnGineMeshPoint> Points);
