using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Xngine;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     The cut-1c accessors added to <see cref="Redguard3DcFile" /> (plan section 8, slice 4): the
///     frame table with its fourth dword surfaced (control: a three-dword table reports none), the
///     frame-block preamble, header +44, the keyframe, the narrow deltas as stored, each frame's raw
///     block spans, the declared areas whose tiling leaves exactly the unaccounted region, and the
///     keyframe mesh re-parsed in stored-UV mode. <see cref="Redguard3dcFileTests" /> is untouched:
///     the stack built here has the same shape, plus distinctive values in the fields the accessors
///     surface.
/// </summary>
public sealed class Redguard3dcFileAccessorTests
{
    private const int PointCount = 3;
    private const int PlaneCount = 1;
    private const int FrameCount = 2;
    private const int FrameBlockOffset = 64;
    private const int TableOffset = FrameBlockOffset + Redguard3DcFile.PreambleDwords * 4;

    private static readonly int[] Keyframe = [0, 0, 0, 256, 0, 0, 0, 256, 0];
    private static readonly short[] Deltas = [1, 2, 3, 4, 5, 6, 7, 8, 9];

    /// <summary>Where the builder puts each block, so a test can pin an offset without re-deriving it.</summary>
    private sealed record BuiltStack(byte[] Bytes, int PlaneListOffset, int PlaneListEnd, int[] Points, int[] Normals, int[] PlaneData);

    /// <summary>
    ///     A two-frame stack that tiles exactly: header, frame block (preamble + table), plane list,
    ///     then each frame's point, normal and plane-data blocks back to back, then an optional
    ///     trailing region the preamble declares. Fourth dwords (wide only) are <c>0x1000 + frame</c>,
    ///     preamble dwords 1, 3, 4 and 5 are 11, 33, 44 and 55, header +44 is 1, and the plane's first
    ///     corner stores u = 16384, a value the reference unfolds.
    /// </summary>
    private static BuiltStack Build(bool wide, int trailingRegion = 0)
    {
        var recordDwords = wide ? 4 : 3;
        var planeListOffset = TableOffset + FrameCount * recordDwords * 4;
        var planeListEnd = planeListOffset + 8 + PointCount * 8;

        var pointLengths = new[] { PointCount * Redguard3DcFile.WidePointLength, PointCount * (wide ? Redguard3DcFile.WidePointLength : Redguard3DcFile.NarrowPointLength) };
        var normalLength = PlaneCount * (wide ? Redguard3DcFile.WideNormalLength : Redguard3DcFile.NarrowNormalLength);
        var planeDataLength = PlaneCount * (wide ? Redguard3DcFile.WidePlaneDataLength : Redguard3DcFile.NarrowPlaneDataLength);

        var points = new int[FrameCount];
        var normals = new int[FrameCount];
        var planeData = new int[FrameCount];
        var cursor = planeListEnd;
        for (var i = 0; i < FrameCount; i++)
        {
            points[i] = cursor;
            normals[i] = points[i] + pointLengths[i];
            planeData[i] = normals[i] + normalLength;
            cursor = planeData[i] + planeDataLength;
        }

        var b = new byte[cursor + trailingRegion];
        Encoding.ASCII.GetBytes("v2.6").CopyTo(b, 0);
        Write(b, 4, PointCount);
        Write(b, 8, PlaneCount);
        Write(b, 12, 256);
        Write(b, 16, FrameCount);
        Write(b, 20, FrameBlockOffset);
        Write(b, 24, planeData[1]); // the header carries FRAME 1's offsets, not the mesh's
        Write(b, 44, 1);
        Write(b, 48, points[1]);
        Write(b, 52, normals[1]);
        Write(b, 60, planeListOffset);

        Write(b, FrameBlockOffset, TableOffset);
        Write(b, FrameBlockOffset + 4, 11);
        Write(b, FrameBlockOffset + 8, trailingRegion);
        Write(b, FrameBlockOffset + 12, 33);
        Write(b, FrameBlockOffset + 16, 44);
        Write(b, FrameBlockOffset + 20, 55);

        for (var i = 0; i < FrameCount; i++)
        {
            var record = TableOffset + i * recordDwords * 4;
            Write(b, record, points[i]);
            Write(b, record + 4, normals[i]);
            Write(b, record + 8, planeData[i]);
            if (wide)
            {
                Write(b, record + 12, 0x1000 + i);
            }
        }

        b[planeListOffset] = PointCount;
        for (var q = 0; q < PointCount; q++)
        {
            Write(b, planeListOffset + 8 + q * 8, q * Redguard3DcFile.WidePointLength);
        }

        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(planeListOffset + 8 + 4), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(planeListOffset + 8 + 6), 100);

        for (var i = 0; i < PointCount * 3; i++)
        {
            Write(b, points[0] + i * 4, Keyframe[i]);
            if (wide)
            {
                Write(b, points[1] + i * 4, Keyframe[i] + Deltas[i]);
            }
            else
            {
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(points[1] + i * 2), Deltas[i]);
            }
        }

        for (var i = 0; i < FrameCount; i++)
        {
            b.AsSpan(normals[i], normalLength).Fill((byte)(0xA0 + i));
            b.AsSpan(planeData[i], planeDataLength).Fill((byte)(0xB0 + i));
        }

        return new BuiltStack(b, planeListOffset, planeListEnd, points, normals, planeData);
    }

    private static void Write(byte[] bytes, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    }

    [Fact]
    public void FrameTable_SurfacesTheFourthDword_OnAFourDwordTableOnly()
    {
        var wide = Redguard3DcFile.Parse(Build(true).Bytes, "W.3DC");
        var narrow = Redguard3DcFile.Parse(Build(false).Bytes, "N.3DC");

        Assert.Equal(2, wide.FrameTable.Count);
        Assert.Equal([0x1000, 0x1001], wide.FrameTable.Select(r => r.FourthDword));

        // Control: a three-dword record has no fourth dword to report, and must say so rather than
        // read the next record's first dword.
        Assert.Equal(2, narrow.FrameTable.Count);
        Assert.All(narrow.FrameTable, r => Assert.Null(r.FourthDword));

        var stack = Build(false);
        Assert.Equal((stack.Points[1], stack.Normals[1], stack.PlaneData[1]),
            (narrow.FrameTable[1].PointOffset, narrow.FrameTable[1].NormalOffset, narrow.FrameTable[1].PlaneDataOffset));
    }

    [Fact]
    public void Preamble_HeaderWords_AndOffsets_AreSurfacedRaw()
    {
        var file = Redguard3DcFile.Parse(Build(false, 40).Bytes, "P.3DC");

        Assert.Equal([TableOffset, 11, 40, 33, 44, 55], file.Preamble);
        Assert.Equal(FrameBlockOffset, file.FrameBlockOffset);
        Assert.Equal(TableOffset, file.FrameTableOffset);
        Assert.Equal(1, file.HeaderUnknown44);
        Assert.Equal((PointCount, PlaneCount), (file.PointCount, file.PlaneCount));
        Assert.Equal(40, file.UnaccountedLength);
    }

    [Fact]
    public void NarrowDeltas_AreTheStoredInt16s_AndThePoseIsTheKeyframePlusThem()
    {
        var file = Redguard3DcFile.Parse(Build(false).Bytes, "N.3DC");

        var deltas = file.NarrowDeltas(1);
        Assert.Equal([new XnGineMeshPoint(1, 2, 3), new XnGineMeshPoint(4, 5, 6), new XnGineMeshPoint(7, 8, 9)], deltas);

        Assert.Equal([new XnGineMeshPoint(0, 0, 0), new XnGineMeshPoint(256, 0, 0), new XnGineMeshPoint(0, 256, 0)], file.Keyframe);
        for (var i = 0; i < PointCount; i++)
        {
            var expected = new XnGineMeshPoint(
                file.Keyframe[i].X + deltas[i].X, file.Keyframe[i].Y + deltas[i].Y, file.Keyframe[i].Z + deltas[i].Z);
            Assert.Equal(expected, file.Pose(1)[i]);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => file.NarrowDeltas(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.NarrowDeltas(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => file.Pose(2));
    }

    [Fact]
    public void NarrowDeltas_RefusesAWideFile_WhosePosesAreNotDeltas()
    {
        var file = Redguard3DcFile.Parse(Build(true).Bytes, "W.3DC");

        Assert.Throws<InvalidOperationException>(() => file.NarrowDeltas(1));
        Assert.Equal(new XnGineMeshPoint(256 + 4, 0 + 5, 0 + 6), file.Pose(1)[1]);
    }

    [Fact]
    public void FrameBlocks_HandBackEachBlocksAreaAndBytes()
    {
        var stack = Build(false);
        var file = Redguard3DcFile.Parse(stack.Bytes, "N.3DC");

        var first = file.FrameBlocks(0);
        Assert.Equal(new ByteArea("frame:0:points", stack.Points[0], stack.Points[0] + PointCount * 12), first.Points);
        Assert.Equal(new ByteArea("frame:0:normals", stack.Normals[0], stack.Normals[0] + 4), first.Normals);
        Assert.Equal(new ByteArea("frame:0:plane-data", stack.PlaneData[0], stack.PlaneData[0] + 12), first.PlaneData);
        Assert.Equal(256, BinaryPrimitives.ReadInt32LittleEndian(first.PointBytes.Span[12..]));
        Assert.All(first.NormalBytes.ToArray(), value => Assert.Equal(0xA0, value));
        Assert.All(first.PlaneDataBytes.ToArray(), value => Assert.Equal(0xB0, value));

        var second = file.FrameBlocks(1);
        Assert.Equal(PointCount * Redguard3DcFile.NarrowPointLength, second.Points.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(second.PointBytes.Span));
        Assert.All(second.NormalBytes.ToArray(), value => Assert.Equal(0xA1, value));

        var wide = Redguard3DcFile.Parse(Build(true).Bytes, "W.3DC");
        Assert.Equal((PointCount * 12, PlaneCount * 12, PlaneCount * 24),
            (wide.FrameBlocks(1).Points.Length, wide.FrameBlocks(1).Normals.Length, wide.FrameBlocks(1).PlaneData.Length));

        Assert.Throws<ArgumentOutOfRangeException>(() => file.FrameBlocks(2));
    }

    [Fact]
    public void DeclaredAreas_TileTheFile_LeavingExactlyTheDeclaredRegion()
    {
        var exact = Redguard3DcFile.Parse(Build(false).Bytes, "N.3DC");
        Assert.Equal(4 + 3 * FrameCount, exact.DeclaredAreas().Count);
        Assert.Equal(["header", "frame-block", "frame-table", "planes"], exact.DeclaredAreas().Take(4).Select(a => a.Name));
        Assert.True(exact.Tiling().TilesExactly);

        var stack = Build(true, 40);
        var declared = Redguard3DcFile.Parse(stack.Bytes, "W.3DC");
        var tiling = declared.Tiling();
        Assert.Empty(tiling.Overlaps);
        Assert.Empty(tiling.OutOfRange);
        var gap = Assert.Single(tiling.Gaps);
        Assert.Equal(new ByteArea($"unclaimed:{stack.Bytes.Length - 40}-{stack.Bytes.Length}", stack.Bytes.Length - 40, stack.Bytes.Length), gap);
        Assert.Equal(declared.UnaccountedLength, tiling.UnclaimedBytes);
        Assert.Equal(stack.PlaneListEnd, declared.PlaneListEnd);
    }

    /// <summary>
    ///     The rule that keeps <see cref="Redguard3DcFile.DeclaredAreas" /> and the acceptance walk on the
    ///     same blocks: the frame table must start at or after the end of the six-dword preamble. This
    ///     stack moves the table twelve bytes back, into preamble dwords 3 to 5, and shifts everything
    ///     after it up by the same twelve bytes, so the header, the table records, the plane list and
    ///     every frame block still tile the file with no gap and the record widths still divide; only
    ///     that rule refuses it, and the message names it. Retail is 88 = 64 + 24 on 147 of 147 files.
    /// </summary>
    [Fact]
    public void TryParse_RefusesAFrameTableStartingInsideThePreamble_EvenThoughTheMergedBlockTiles()
    {
        const int shift = 12;
        var stack = Build(false);
        var source = stack.Bytes;
        var bytes = new byte[source.Length - shift];
        source.AsSpan(0, TableOffset).CopyTo(bytes);
        source.AsSpan(stack.PlaneListOffset).CopyTo(bytes.AsSpan(stack.PlaneListOffset - shift));
        Write(bytes, 24, stack.PlaneData[1] - shift);
        Write(bytes, 48, stack.Points[1] - shift);
        Write(bytes, 52, stack.Normals[1] - shift);
        Write(bytes, 60, stack.PlaneListOffset - shift);
        Write(bytes, FrameBlockOffset, TableOffset - shift);
        for (var i = 0; i < FrameCount; i++)
        {
            var record = TableOffset - shift + i * 3 * 4;
            Write(bytes, record, stack.Points[i] - shift);
            Write(bytes, record + 4, stack.Normals[i] - shift);
            Write(bytes, record + 8, stack.PlaneData[i] - shift);
        }

        Assert.False(Redguard3DcFile.TryParse(bytes, "IN.3DC", out _, out var error));
        Assert.Contains("starts inside the frame block's preamble", error, StringComparison.Ordinal);
        Assert.False(Redguard3DcFile.Is3dcFile(bytes));

        // Control: the same stack with the table where the preamble ends is the accepted shape.
        Assert.True(Redguard3DcFile.TryParse(source, "N.3DC", out _, out _));
    }

    [Fact]
    public void ParseKeyframeMesh_StoredMode_KeepsTheValueTheKeyframeMeshUnfolds()
    {
        // The keyframe is parsed with object id 0, which is below the packed-UV gate, so the legacy
        // KeyframeMesh reads the stored 16384 as 0. The stored-UV re-parse keeps it.
        var file = Redguard3DcFile.Parse(Build(false).Bytes, "N.3DC");

        Assert.Equal((0, 100), (file.KeyframeMesh.Planes[0].Points[0].U, file.KeyframeMesh.Planes[0].Points[0].V));
        Assert.Equal(1, file.KeyframeMesh.UnfoldedUvValueCount);

        var stored = file.ParseKeyframeMesh(XnGineUvHandling.Stored);
        Assert.Equal((16384, 100), (stored.Planes[0].Points[0].U, stored.Planes[0].Points[0].V));
        Assert.Equal(0, stored.UnfoldedUvValueCount);
        Assert.Equal(file.KeyframeMesh.Points, stored.Points);
        Assert.Equal(file.KeyframeMesh.Planes[0].Normal, stored.Planes[0].Normal);

        var reference = file.ParseKeyframeMesh(XnGineUvHandling.Reference);
        Assert.Equal(file.KeyframeMesh.Planes[0].Points, reference.Planes[0].Points);
    }

    [Fact]
    public void LegacySurface_IsUnchangedByTheAccessors()
    {
        var file = Redguard3DcFile.Parse(Build(false, 40).Bytes, "N.3DC");

        Assert.Equal(2, file.FrameCount);
        Assert.Equal(3, file.FrameRecordDwords);
        Assert.False(file.WideFrames);
        Assert.Equal(40, file.UnaccountedLength);
        Assert.Equal(file.Frames[0].Points, file.Keyframe);
        Assert.Equal(file.Frames[1].Points, file.Pose(1));
        Assert.Same(file.Frames[1].Points, file.Pose(1));
    }
}
