using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Redguard;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     Synthetic vectors for Redguard's animated <c>.3DC</c> mesh, shaped after the retail set
///     measured by independent Python walks 2026-09-06: 147 files, all tiling exactly, 110 with
///     three-dword frame records and 37 with four — and the record width predicts the frame width,
///     so the same 37 store full int32 poses while 110 store int16 deltas.
///     <para>
///         The trap these pin is that a <c>.3DC</c> is a VALID <c>.3D</c> byte-for-byte in its header
///         and plane list, so a <c>.3D</c> reader parses one without complaint and returns the wrong
///         geometry — the header's point offset is frame 1's, not the mesh's. Only the frame table
///         gives the real poses.
///     </para>
/// </summary>
public sealed class Redguard3dcFileTests
{
    private const int PointCount = 3;
    private const int PlaneCount = 1;

    /// <summary>
    ///     Builds a minimal two-frame mesh that tiles exactly: header, frame block, frame table,
    ///     plane list, then each frame's point/normal/plane-data blocks back to back.
    /// </summary>
    private static byte[] Mesh(bool wide, int trailingRegion = 0, int[]? keyframe = null, short[]? deltas = null)
    {
        keyframe ??= [0, 0, 0, 256, 0, 0, 0, 256, 0];
        deltas ??= [1, 2, 3, 4, 5, 6, 7, 8, 9];

        const int frameCount = 2;
        const int frameBlockOffset = 64;
        var tableOffset = frameBlockOffset + Redguard3DcFile.PreambleDwords * 4;
        var recordDwords = wide ? 4 : 3;
        var planeListOffset = tableOffset + frameCount * recordDwords * 4;
        var planeListEnd = planeListOffset + 8 + PointCount * 8;

        var laterPoints = PointCount * (wide ? Redguard3DcFile.WidePointLength : Redguard3DcFile.NarrowPointLength);
        var normalLength = PlaneCount * (wide ? 12 : 4);
        var planeDataLength = PlaneCount * (wide ? 24 : 12);

        var f0Points = planeListEnd;
        var f0Normals = f0Points + PointCount * Redguard3DcFile.WidePointLength;
        var f0PlaneData = f0Normals + normalLength;
        var f1Points = f0PlaneData + planeDataLength;
        var f1Normals = f1Points + laterPoints;
        var f1PlaneData = f1Normals + normalLength;
        var size = f1PlaneData + planeDataLength + trailingRegion;

        var b = new byte[size];
        Encoding.ASCII.GetBytes("v2.6").CopyTo(b, 0);
        Write(b, 4, PointCount);
        Write(b, 8, PlaneCount);
        Write(b, 12, 256); // radius — deliberately NOT the acceptance gate
        Write(b, 16, frameCount);
        Write(b, 20, frameBlockOffset);
        Write(b, 24, f1PlaneData); // the header carries FRAME 1's offsets, not the mesh's
        Write(b, 48, f1Points);
        Write(b, 52, f1Normals);
        Write(b, 60, planeListOffset);

        Write(b, frameBlockOffset, tableOffset); // preamble[0]: where the table starts
        Write(b, frameBlockOffset + 8, trailingRegion); // preamble[2]: the one unaccounted region

        Write(b, tableOffset, f0Points);
        Write(b, tableOffset + 4, f0Normals);
        Write(b, tableOffset + 8, f0PlaneData);
        Write(b, tableOffset + recordDwords * 4, f1Points);
        Write(b, tableOffset + recordDwords * 4 + 4, f1Normals);
        Write(b, tableOffset + recordDwords * 4 + 8, f1PlaneData);

        b[planeListOffset] = PointCount; // one plane over all three points
        for (var q = 0; q < PointCount; q++)
        {
            Write(b, planeListOffset + 8 + q * 8, q * Redguard3DcFile.WidePointLength);
        }

        for (var i = 0; i < PointCount * 3; i++)
        {
            Write(b, f0Points + i * 4, keyframe[i]);
        }

        for (var i = 0; i < PointCount * 3; i++)
        {
            if (wide)
            {
                Write(b, f1Points + i * 4, keyframe[i] + deltas[i]);
            }
            else
            {
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(f1Points + i * 2), deltas[i]);
            }
        }

        return b;
    }

    private static void Write(byte[] bytes, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    }

    [Fact]
    public void Parse_ReadsBothFramesFromTheFrameTable()
    {
        var file = Redguard3DcFile.Parse(Mesh(false), "T.3DC");

        Assert.Equal(2, file.FrameCount);
        Assert.Equal(3, file.FrameRecordDwords);
        Assert.False(file.WideFrames);
        Assert.Equal(new XnGineMeshPointTriple(0, 0, 0), Triple(file, 0, 0));
        Assert.Equal(new XnGineMeshPointTriple(256, 0, 0), Triple(file, 0, 1));
    }

    [Fact]
    public void Parse_AddsANarrowFramesDeltasToTheKeyframe_NotToItsPredecessor()
    {
        // ⚠ THE animation trap. Measured on all 91 retail files with ten or more frames:
        // accumulating frame-to-frame makes the mesh drift, every time. Frame 1 here is the
        // keyframe plus its own deltas and nothing else.
        var file = Redguard3DcFile.Parse(Mesh(false), "T.3DC");

        Assert.Equal(new XnGineMeshPointTriple(0 + 1, 0 + 2, 0 + 3), Triple(file, 1, 0));
        Assert.Equal(new XnGineMeshPointTriple(256 + 4, 0 + 5, 0 + 6), Triple(file, 1, 1));
        Assert.Equal(new XnGineMeshPointTriple(0 + 7, 256 + 8, 0 + 9), Triple(file, 1, 2));
    }

    [Fact]
    public void Parse_ReadsAWideFilesLaterFramesAsFullPoints()
    {
        var file = Redguard3DcFile.Parse(Mesh(true), "W.3DC");

        Assert.True(file.WideFrames);
        Assert.Equal(4, file.FrameRecordDwords);
        Assert.Equal(new XnGineMeshPointTriple(256 + 4, 0 + 5, 0 + 6), Triple(file, 1, 1));
    }

    [Fact]
    public void Parse_AcceptsTheOneUnaccountedRegionTheFrameBlockDeclares()
    {
        // The reference reads that dword as its vertex base ("endFaceData + u3"), which is why it
        // works only when the region happens to sit before the frames. Here it is simply declared.
        var file = Redguard3DcFile.Parse(Mesh(false, 40), "R.3DC");

        Assert.Equal(40, file.UnaccountedLength);
    }

    [Fact]
    public void Parse_RejectsAFileWhoseBlocksDoNotAccountForIt()
    {
        // An undeclared trailing region is the failure mode a looser reader would swallow.
        var bytes = Mesh(false, 40);
        Write(bytes, 64 + 8, 0);

        var error = Assert.Throws<InvalidDataException>(() => Redguard3DcFile.Parse(bytes, "BAD.3DC"));
        Assert.Contains("one region of 0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAFrameTableThatDoesNotDivide()
    {
        var bytes = Mesh(false);
        Write(bytes, 60, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(60)) + 4);

        Assert.Throws<InvalidDataException>(() => Redguard3DcFile.Parse(bytes, "BAD.3DC"));
    }

    [Fact]
    public void KeyframeMesh_CarriesTheKeyframePointsAndNormalsComputedFromThem()
    {
        var file = Redguard3DcFile.Parse(Mesh(false), "T.3DC");
        var mesh = file.KeyframeMesh;

        Assert.Equal(PointCount, mesh.Points.Count);
        Assert.Single(mesh.Planes);
        Assert.Equal(256, mesh.Points[1].X);

        // The three points lie in the z = 0 plane wound counter-clockwise, so Newell's method gives
        // +Z — scaled to the format's fixed point where 256 is 1.0. A .3DC stores no normal list a
        // .3D reader could use, so this is computed, and a wrong winding would show as -256.
        var normal = mesh.Planes[0].Normal;
        Assert.Equal((0, 0, 256), (normal.X, normal.Y, normal.Z));
    }

    [Fact]
    public void Is3dcFile_AcceptsOnlyWhatTiles()
    {
        Assert.True(Redguard3DcFile.Is3dcFile(Mesh(false)));
        Assert.False(Redguard3DcFile.Is3dcFile("v2.6 but nothing else"u8.ToArray()));
    }

    private static XnGineMeshPointTriple Triple(Redguard3DcFile file, int frame, int point)
    {
        var p = file.Frames[frame].Points[point];
        return new XnGineMeshPointTriple(p.X, p.Y, p.Z);
    }

    /// <summary>A comparable stand-in so failures print the coordinates.</summary>
    private readonly record struct XnGineMeshPointTriple(int X, int Y, int Z);
}
