using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Xngine;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Redguard's animated mesh, <c>.3DC</c>. Original RE (2026-09-06) — the MIT exporter
///     (RGUnity/redguard-file-exporter) carries a reader that its own comments admit does not work
///     ("Some old 3DC files have a different vertex start offset for some unknown reason"), so
///     nothing here is ported.
///     <para>
///         A <c>.3DC</c> is an ordinary <see cref="XnGineMesh" /> whose
///         <b>
///             geometry is a stack of
///             poses
///         </b>
///         . The 64-byte header and the plane (face) list are byte-for-byte Daggerfall's,
///         which is why a <c>.3DC</c> parses as a <c>.3D</c> and yields wrong points rather than an
///         error: the header's point/normal/plane-data offsets at +48/+52/+24 are <b>frame 1's</b>,
///         not the mesh's. The real geometry is named by a FRAME TABLE:
///     </para>
///     <list type="bullet">
///         <item>
///             Header +20 gives the frame-block offset (64 on every retail file — the block starts
///             immediately after the header). Its first dword is the frame table's own offset (88 on
///             147/147) and its <b>third dword is the length of one unaccounted region</b>.
///         </item>
///         <item>
///             The table holds <see cref="FrameCount" /> records of three or four dwords —
///             <c>(vertexOffset, normalOffset, planeDataOffset[, unknown])</c>. Retail splits 110 three-
///             dword / 37 four-dword.
///         </item>
///         <item>
///             Frame 0 is a KEYFRAME of <c>PointCount</c> int32 triples. Later frames are either
///             int32 triples again (37 "wide" files) or <b>int16 deltas added to the keyframe</b>
///             (110 "narrow" files) — never accumulated frame-to-frame.
///         </item>
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
///     <para>
///         ⚠ "The header offsets are frame 1's" holds on the 37 WIDE files only (measured 2026-09-28:
///         +24/+48/+52 equal frame 1's plane-data, point and normal block offsets on 37 of 37 wide files
///         and on 0 of 110 narrow files, where they name no frame and on 22 files +24 lies past the end
///         of the file). <see cref="KeyframeMesh" />'s <c>PlaneData</c> slices are therefore taken from
///         wherever +24 points; a cut-1c reader takes plane data from <see cref="FrameBlocks" /> instead.
///     </para>
/// </summary>
internal sealed class Redguard3DcFile
{
    /// <summary>Bytes per point in a keyframe, and in every frame of a "wide" file.</summary>
    public const int WidePointLength = 12;

    /// <summary>Bytes per point in a delta frame of a "narrow" file.</summary>
    public const int NarrowPointLength = 6;

    /// <summary>Dwords of frame-block preamble before the frame table.</summary>
    public const int PreambleDwords = 6;

    /// <summary>Bytes per plane in a wide file's per-frame normal block (int32 triples).</summary>
    public const int WideNormalLength = 12;

    /// <summary>Bytes per plane in a narrow file's per-frame normal block (undecoded).</summary>
    public const int NarrowNormalLength = 4;

    /// <summary>Bytes per plane in a wide file's per-frame plane-data block.</summary>
    public const int WidePlaneDataLength = 24;

    /// <summary>Bytes per plane in a narrow file's per-frame plane-data block.</summary>
    public const int NarrowPlaneDataLength = 12;

    private readonly XnGineMeshPoint[] _keyframeNormals;

    private Redguard3DcFile(
        string name,
        ReadOnlyMemory<byte> bytes,
        XnGineMesh keyframeMesh,
        XnGineMeshPoint[] keyframeNormals,
        IReadOnlyList<Redguard3DcFrame> frames,
        IReadOnlyList<Redguard3DcFrameTableRecord> frameTable,
        IReadOnlyList<int> preamble,
        Geometry geometry,
        int headerUnknown44,
        int planeListEnd)
    {
        Name = name;
        Bytes = bytes;
        KeyframeMesh = keyframeMesh;
        _keyframeNormals = keyframeNormals;
        Frames = frames;
        FrameTable = frameTable;
        Preamble = preamble;
        WideFrames = geometry.Wide;
        FrameRecordDwords = geometry.RecordDwords;
        UnaccountedLength = geometry.UnaccountedLength;
        FrameBlockOffset = geometry.FrameBlockOffset;
        FrameTableOffset = geometry.TableOffset;
        PlaneListOffset = geometry.PlaneListOffset;
        PlaneListEnd = planeListEnd;
        PointCount = geometry.PointCount;
        PlaneCount = geometry.PlaneCount;
        HeaderUnknown44 = headerUnknown44;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The bytes the file was parsed from; every raw-span accessor slices into them.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>The mesh in its keyframe pose — points, planes and computed normals.</summary>
    public XnGineMesh KeyframeMesh { get; }

    /// <summary>Every pose, keyframe first.</summary>
    public IReadOnlyList<Redguard3DcFrame> Frames { get; }

    /// <summary>True when later frames store int32 triples rather than int16 deltas.</summary>
    public bool WideFrames { get; }

    /// <summary>Dwords per frame-table record: three or four.</summary>
    public int FrameRecordDwords { get; }

    /// <summary>Length of the one region the tiling leaves unaccounted, as the file declares it.</summary>
    public int UnaccountedLength { get; }

    /// <summary>Number of poses.</summary>
    public int FrameCount => Frames.Count;

    /// <summary>Header +4: points per pose.</summary>
    public int PointCount { get; }

    /// <summary>Header +8: planes.</summary>
    public int PlaneCount { get; }

    /// <summary>Header +20: where the frame block starts (64 on 147 of 147 retail files).</summary>
    public int FrameBlockOffset { get; }

    /// <summary>Frame-block dword 0: where the frame table starts (88 on 147 of 147 retail files).</summary>
    public int FrameTableOffset { get; }

    /// <summary>Header +60: where the plane list starts.</summary>
    public int PlaneListOffset { get; }

    /// <summary>One past the last byte of the plane list, found by the walk.</summary>
    public int PlaneListEnd { get; }

    /// <summary>
    ///     Header +44, surfaced raw. Non-zero on the 147 <c>.3DC</c> files and on no static mesh (plan
    ///     section 0.2); measured 2026-09-28 it reads exactly 1 on 147 of 147.
    /// </summary>
    public int HeaderUnknown44 { get; }

    /// <summary>
    ///     Frame-block dwords 0 to 5, raw. Dword 0 is <see cref="FrameTableOffset" /> and dword 2 is
    ///     <see cref="UnaccountedLength" />; 1, 3, 4 and 5 are undecoded (measured 2026-09-28: dword 3 is
    ///     8209 on all 110 narrow files and 16402 on 19 of the 37 wide ones, the other 18 wide values and
    ///     dwords 1, 4 and 5 vary per file).
    /// </summary>
    public IReadOnlyList<int> Preamble { get; }

    /// <summary>
    ///     The frame table, one record per pose: where its point, normal and plane-data blocks start, and
    ///     on a four-dword table the fourth dword, surfaced raw (null on a three-dword table). Measured
    ///     2026-09-28 over the 2,218 records of the 37 wide files: 1,512 distinct values, 141 zero, equal
    ///     to the next frame's point offset on 0 and to the record's own plane-data block end on 0, so
    ///     it is not an offset the tiling can use.
    /// </summary>
    public IReadOnlyList<Redguard3DcFrameTableRecord> FrameTable { get; }

    /// <summary>Frame 0's absolute int32 points: the base every narrow delta is added to.</summary>
    public IReadOnlyList<XnGineMeshPoint> Keyframe => Frames[0].Points;

    /// <summary>Content probe: a <c>.3DC</c> is exactly the file whose blocks tile under this reader.</summary>
    public static bool Is3dcFile(ReadOnlyMemory<byte> bytes)
    {
        return TryParse(bytes, "probe", out _, out _);
    }

    /// <summary>Parses the mesh, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static Redguard3DcFile Parse(ReadOnlyMemory<byte> bytes, string name)
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
        out Redguard3DcFile file,
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
        var headerUnknown44 = BinaryPrimitives.ReadInt32LittleEndian(span[44..]);
        var planeListOffset = BinaryPrimitives.ReadInt32LittleEndian(span[60..]);

        if (pointCount <= 0 || planeCount <= 0 || frameCount <= 0)
        {
            error = $"{name}: {pointCount} points, {planeCount} planes and {frameCount} frames are not all positive.";
            return false;
        }

        if (frameBlockOffset < 0 || frameBlockOffset + PreambleDwords * 4 > span.Length)
        {
            error = $"{name}: the frame block at {frameBlockOffset} does not fit the file.";
            return false;
        }

        var preamble = new int[PreambleDwords];
        for (var i = 0; i < PreambleDwords; i++)
        {
            preamble[i] = BinaryPrimitives.ReadInt32LittleEndian(span[(frameBlockOffset + i * 4)..]);
        }

        var tableOffset = preamble[0];
        var unaccounted = preamble[2];
        if (tableOffset < 0 || tableOffset > planeListOffset || planeListOffset > span.Length)
        {
            error = $"{name}: the frame table at {tableOffset} does not precede the plane list at {planeListOffset}.";
            return false;
        }

        // The acceptance walk tiles the frame block and the frame table as ONE block, so a table that
        // started inside the six-dword preamble would still tile; requiring it to start where the
        // preamble ends keeps that block the union of the two areas DeclaredAreas() reports. Retail is
        // 88 = 64 + 24 on 147 of 147 files.
        var preambleEnd = frameBlockOffset + PreambleDwords * 4;
        if (tableOffset < preambleEnd)
        {
            error = $"{name}: the frame table at {tableOffset} starts inside the frame block's preamble " +
                    $"({frameBlockOffset} to {preambleEnd}).";
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

        var table = new Redguard3DcFrameTableRecord[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            var at = tableOffset + i * recordDwords * 4;
            table[i] = new Redguard3DcFrameTableRecord(
                BinaryPrimitives.ReadInt32LittleEndian(span[at..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[(at + 4)..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[(at + 8)..]),
                recordDwords == 4 ? BinaryPrimitives.ReadInt32LittleEndian(span[(at + 12)..]) : null);
        }

        // No field declares the frame width, but the frame RECORD width predicts it on all 147
        // retail files — four dwords always means full int32 poses. That is a correlation, not a
        // definition, so it only picks which reading to try first; the tiling is what accepts one.
        string? predictedFailure = null;
        foreach (var wide in recordDwords == 4 ? new[] { true, false } : new[] { false, true })
        {
            if (TryBuild(bytes, name, new Geometry(pointCount, planeCount, frameCount, planeListOffset,
                    frameBlockOffset, tableOffset, recordDwords, unaccounted, wide), table, preamble,
                    headerUnknown44, out file, out var attempt))
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

    /// <summary>
    ///     The pose of one frame as absolute points: the keyframe for frame 0, an int32 pose on a wide
    ///     file, the keyframe plus that frame's own int16 deltas on a narrow file (the same list
    ///     <see cref="Frames" /> holds).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame" /> is not a frame of this file.</exception>
    public IReadOnlyList<XnGineMeshPoint> Pose(int frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frame, FrameCount);
        return Frames[frame].Points;
    }

    /// <summary>
    ///     The int16 deltas a narrow file stores for one later frame, exactly as stored: each point's
    ///     (dx, dy, dz) relative to the KEYFRAME, never to the previous frame.
    ///     <see cref="Pose" /> of the same frame is <see cref="Keyframe" /> plus these, point for point.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is wide; its later frames store poses, not deltas.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame" /> is 0 (the keyframe has no deltas) or not a frame.</exception>
    public IReadOnlyList<XnGineMeshPoint> NarrowDeltas(int frame)
    {
        if (WideFrames)
        {
            throw new InvalidOperationException($"{Name}: a wide file stores int32 poses, not int16 deltas.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(frame, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frame, FrameCount);

        var span = Bytes.Span;
        var offset = FrameTable[frame].PointOffset;
        var deltas = new XnGineMeshPoint[PointCount];
        for (var i = 0; i < deltas.Length; i++)
        {
            var at = offset + i * NarrowPointLength;
            deltas[i] = new XnGineMeshPoint(
                BinaryPrimitives.ReadInt16LittleEndian(span[at..]),
                BinaryPrimitives.ReadInt16LittleEndian(span[(at + 2)..]),
                BinaryPrimitives.ReadInt16LittleEndian(span[(at + 4)..]));
        }

        return deltas;
    }

    /// <summary>
    ///     One frame's three blocks as the frame table places them, with their raw bytes: the points
    ///     (12 bytes per point for frame 0 and on a wide file, 6 on a narrow file's later frames), the
    ///     per-plane normals (12 bytes per plane wide, 4 narrow) and the per-plane plane data (24 wide,
    ///     12 narrow). The normal and plane-data bytes are handed back undecoded: the wide normal blocks
    ///     are authored per-pose plane normals (plan section 0.2) and the narrow ones match no decoding
    ///     tried, so a cut-1c reader carries both as native state.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame" /> is not a frame of this file.</exception>
    public Redguard3DcFrameBlocks FrameBlocks(int frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frame, FrameCount);

        var record = FrameTable[frame];
        var points = new ByteArea($"frame:{frame}:points", record.PointOffset,
            record.PointOffset + PointCount * (frame == 0 || WideFrames ? WidePointLength : NarrowPointLength));
        var normals = new ByteArea($"frame:{frame}:normals", record.NormalOffset,
            record.NormalOffset + PlaneCount * (WideFrames ? WideNormalLength : NarrowNormalLength));
        var planeData = new ByteArea($"frame:{frame}:plane-data", record.PlaneDataOffset,
            record.PlaneDataOffset + PlaneCount * (WideFrames ? WidePlaneDataLength : NarrowPlaneDataLength));
        return new Redguard3DcFrameBlocks(frame, points, normals, planeData,
            Bytes.Slice(points.Start, points.Length),
            Bytes.Slice(normals.Start, normals.Length),
            Bytes.Slice(planeData.Start, planeData.Length));
    }

    /// <summary>
    ///     Every byte area the file declares, named as the cut-1c coverage elements are (plan section 4):
    ///     <c>header</c>, <c>frame-block</c> (the preamble), <c>frame-table</c>, <c>planes</c>, then per
    ///     frame <c>frame:{i}:points</c>, <c>frame:{i}:normals</c> and <c>frame:{i}:plane-data</c>. Tiling
    ///     the file with them leaves exactly the one <c>unaccounted</c> region of
    ///     <see cref="UnaccountedLength" /> bytes (or nothing when it is 0). <see cref="TryParse" /> accepted
    ///     the file on the same header, plane and frame blocks plus ONE block from
    ///     <see cref="FrameBlockOffset" /> to <see cref="PlaneListOffset" />; the split reported here
    ///     (<c>frame-block</c> up to the table, <c>frame-table</c> from it on) is that block's exact union
    ///     because <see cref="TryParse" /> refuses a table that starts before the end of the six-dword
    ///     preamble it read (88 = 64 + 24 on 147 of 147 retail files), so the two block sets cannot diverge.
    /// </summary>
    public IReadOnlyList<ByteArea> DeclaredAreas()
    {
        var areas = new List<ByteArea>(4 + 3 * FrameCount)
        {
            new("header", 0, XnGineMesh.HeaderLength),
            new("frame-block", FrameBlockOffset, FrameTableOffset),
            new("frame-table", FrameTableOffset, PlaneListOffset),
            new("planes", PlaneListOffset, PlaneListEnd)
        };

        for (var i = 0; i < FrameCount; i++)
        {
            var blocks = FrameBlocks(i);
            areas.Add(blocks.Points);
            areas.Add(blocks.Normals);
            areas.Add(blocks.PlaneData);
        }

        return areas;
    }

    /// <summary>Tiles the file with <see cref="DeclaredAreas" />; the one gap, if any, is the unaccounted region.</summary>
    public ByteAreaTiling Tiling()
    {
        return ByteAreaTiling.Compute(Bytes.Length, DeclaredAreas());
    }

    /// <summary>
    ///     The keyframe mesh parsed again under the given UV handling, over the same keyframe points and
    ///     the same computed normals. <see cref="XnGineUvHandling.Reference" /> reproduces
    ///     <see cref="KeyframeMesh" />; <see cref="XnGineUvHandling.Stored" /> keeps the stored UV values,
    ///     which the reference reading unfolds on every <c>.3DC</c> because the keyframe is parsed with
    ///     object id 0 (113,017 first-three-corner values across the 147 retail files, measured
    ///     2026-09-28; the <c>.3DC</c> UV encoding itself is undecoded, plan D3).
    /// </summary>
    public XnGineMesh ParseKeyframeMesh(XnGineUvHandling uvHandling)
    {
        return XnGineMesh.Parse(Bytes, 0, XnGineMeshLayout.Daggerfall, Keyframe, _keyframeNormals, uvHandling);
    }

    private static bool TryBuild(
        ReadOnlyMemory<byte> bytes,
        string name,
        Geometry geometry,
        Redguard3DcFrameTableRecord[] table,
        int[] preamble,
        int headerUnknown44,
        out Redguard3DcFile file,
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
            var normalLength = geometry.Wide ? WideNormalLength : NarrowNormalLength;
            var planeDataLength = geometry.Wide ? WidePlaneDataLength : NarrowPlaneDataLength;
            blocks.Add((table[i].PointOffset, table[i].PointOffset + geometry.PointCount * pointLength));
            blocks.Add((table[i].NormalOffset, table[i].NormalOffset + geometry.PlaneCount * normalLength));
            blocks.Add((table[i].PlaneDataOffset, table[i].PlaneDataOffset + geometry.PlaneCount * planeDataLength));
        }

        if (!Tiles(blocks, span.Length, geometry.UnaccountedLength, name, out error))
        {
            return false;
        }

        var frames = new Redguard3DcFrame[geometry.FrameCount];
        var keyframe = ReadKeyframe(span, geometry, table[0].PointOffset);
        frames[0] = new Redguard3DcFrame(0, table[0].PointOffset, keyframe);
        for (var i = 1; i < geometry.FrameCount; i++)
        {
            frames[i] = new Redguard3DcFrame(i, table[i].PointOffset,
                geometry.Wide
                    ? ReadKeyframe(span, geometry, table[i].PointOffset)
                    : ReadDeltaFrame(span, geometry, table[i].PointOffset, keyframe));
        }

        var normals = ComputeNormals(keyframe, bytes, geometry);
        var mesh = XnGineMesh.Parse(bytes, 0, XnGineMeshLayout.Daggerfall, keyframe, normals);
        file = new Redguard3DcFile(name, bytes, mesh, normals, frames, table, preamble, geometry, headerUnknown44,
            planeListEnd);
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

            end += planeHeaderLength + span[end] * planePointLength;
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
            var at = offset + i * WidePointLength;
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
            var at = offset + i * NarrowPointLength;
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
                var a = points[PointIndex(span, first + q * 8, points.Length)];
                var b = points[PointIndex(span, first + (q + 1) % planePoints * 8, points.Length)];
                nx += (a.Y - (double)b.Y) * (a.Z + (double)b.Z);
                ny += (a.Z - (double)b.Z) * (a.X + (double)b.X);
                nz += (a.X - (double)b.X) * (a.Y + (double)b.Y);
            }

            var scale = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            normals[i] = scale <= 0
                ? new XnGineMeshPoint(0, 0, 0)
                : new XnGineMeshPoint(
                    (int)Math.Round(nx / scale * XnGineMesh.PointDivisor),
                    (int)Math.Round(ny / scale * XnGineMesh.PointDivisor),
                    (int)Math.Round(nz / scale * XnGineMesh.PointDivisor));
            position = first + planePoints * 8;
        }

        return normals;
    }

    private static int PointIndex(ReadOnlySpan<byte> span, int at, int pointCount)
    {
        var offset = BinaryPrimitives.ReadInt32LittleEndian(span[at..]) / WidePointLength;
        return offset >= 0 && offset < pointCount ? offset : 0;
    }

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
}

/// <summary>One pose of a <see cref="Redguard3DcFile" />, already resolved to absolute points.</summary>
/// <param name="Index">Position in the frame table.</param>
/// <param name="Offset">Where the frame's own bytes start, for diagnostics.</param>
/// <param name="Points">The pose, in native units — deltas already applied.</param>
internal readonly record struct Redguard3DcFrame(int Index, int Offset, IReadOnlyList<XnGineMeshPoint> Points);

/// <summary>
///     One frame-table record of a <see cref="Redguard3DcFile" />: where the pose's three blocks start,
///     plus the fourth dword a four-dword table carries.
/// </summary>
/// <param name="PointOffset">Where the frame's point block starts.</param>
/// <param name="NormalOffset">Where the frame's per-plane normal block starts.</param>
/// <param name="PlaneDataOffset">Where the frame's per-plane plane-data block starts.</param>
/// <param name="FourthDword">The fourth dword, raw and undecoded; null on a three-dword table.</param>
internal readonly record struct Redguard3DcFrameTableRecord(
    int PointOffset,
    int NormalOffset,
    int PlaneDataOffset,
    int? FourthDword);

/// <summary>One frame's three blocks of a <see cref="Redguard3DcFile" />, as areas and as raw bytes.</summary>
/// <param name="Frame">Position in the frame table.</param>
/// <param name="Points">The point block: <c>frame:{i}:points</c>.</param>
/// <param name="Normals">The per-plane normal block: <c>frame:{i}:normals</c>.</param>
/// <param name="PlaneData">The per-plane plane-data block: <c>frame:{i}:plane-data</c>.</param>
/// <param name="PointBytes">The point block's bytes.</param>
/// <param name="NormalBytes">The normal block's bytes, undecoded.</param>
/// <param name="PlaneDataBytes">The plane-data block's bytes, undecoded.</param>
internal sealed record Redguard3DcFrameBlocks(
    int Frame,
    ByteArea Points,
    ByteArea Normals,
    ByteArea PlaneData,
    ReadOnlyMemory<byte> PointBytes,
    ReadOnlyMemory<byte> NormalBytes,
    ReadOnlyMemory<byte> PlaneDataBytes);
