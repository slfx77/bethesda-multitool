using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Xngine.Mesh;

/// <summary>XnGine mesh parsing on synthetic records: header, versions, plane walk, UV unpack, rejection.</summary>
public class XnGineMeshTests
{
    [Fact]
    public void Parse_ReadsHeaderPointsPlanesAndTextures()
    {
        var bytes = XnGineMeshFixture.Build("v2.7",
            [(0, 0, 0), (256, 0, 0), (256, -512, 0), (0, -512, 0)],
            [
                new XnGineMeshFixture.Plane(XnGineMeshFixture.Texture(24, 3), [(0, 16, 32), (1, 8, 0), (2, 0, 8)],
                    (0, 0, -256)),
                new XnGineMeshFixture.Plane(XnGineMeshFixture.Texture(321, 4),
                    [(2, 0, 0), (3, 0, 0), (0, 0, 0), (1, 0, 0)], (0, 0, 256))
            ],
            37094);

        var mesh = XnGineMesh.Parse(bytes, 44005);

        Assert.Equal(44005u, mesh.ObjectId);
        Assert.Equal(XnGineMeshVersion.V27, mesh.Version);
        Assert.Equal("v2.7", mesh.VersionTag);
        Assert.Equal(37094u, mesh.Radius);
        Assert.Equal(37094f / 256f, mesh.RadiusUnits);
        Assert.Equal(3, mesh.ObjectDataCount);
        Assert.Equal(24832u, mesh.Unknown2);
        Assert.Equal(4, mesh.Points.Count);
        Assert.Equal(new XnGineMeshPoint(256, -512, 0), mesh.Points[2]);
        Assert.Equal(new XnGineMeshPoint(0, -512, 0), mesh.Min);
        Assert.Equal(new XnGineMeshPoint(256, 0, 0), mesh.Max);
        Assert.Equal((1f, 2f, 0f), mesh.Size);

        Assert.Equal(2, mesh.Planes.Count);
        var first = mesh.Planes[0];
        Assert.Equal(24, first.TextureArchive);
        Assert.Equal(3, first.TextureRecord);
        Assert.Equal(0x11, first.Unknown1);
        Assert.Equal(0xCAFEBABEu, first.Unknown2);
        Assert.Equal(new XnGineMeshPoint(0, 0, -256), first.Normal);
        Assert.Equal([0, 1, 2], first.Points.Select(p => p.PointIndex));
        Assert.Equal(new XnGinePlanePoint(0, 16, 32), first.Points[0]);
        // The fixture fills plane data with its byte index, so plane k's block starts at 24k.
        Assert.Equal(24, first.PlaneData.Length);
        Assert.Equal(0, first.PlaneData.Span[0]);
        Assert.Equal(24, mesh.Planes[1].PlaneData.Span[0]);

        Assert.Equal([(24, 3), (321, 4)], mesh.UniqueTextures);
        Assert.Equal(321, mesh.Planes[1].TextureArchive);
        Assert.Equal(4, mesh.Planes[1].TextureRecord);
    }

    [Fact]
    public void Parse_Version25_ScalesPointOffsetsByThree()
    {
        var bytes = XnGineMeshFixture.Quad("v2.5");

        var mesh = XnGineMesh.Parse(bytes, 5000);

        Assert.Equal(XnGineMeshVersion.V25, mesh.Version);
        Assert.Equal([0, 1, 2, 3], mesh.Planes[0].Points.Select(p => p.PointIndex));

        // The same bytes read as v2.7 address points 0, 4, 8 — past the list — and are rejected.
        bytes[3] = (byte)'7';
        Assert.Throws<InvalidDataException>(() => XnGineMesh.Parse(bytes, 5000));
    }

    [Fact]
    public void Parse_UnpacksPackedUvs_OnlyForLowObjectIds_AndOnlyOnTheFirstThreePoints()
    {
        var plane = new XnGineMeshFixture.Plane(XnGineMeshFixture.Texture(1, 1),
            [(0, 16384, 14336), (1, -7168, 100), (2, 16384, 0), (3, 16384, 16384)], (0, -256, 0));
        var bytes = XnGineMeshFixture.Build("v2.7", [(0, 0, 0), (256, 0, 0), (256, 0, 256), (0, 0, 256)], [plane]);

        var low = XnGineMesh.Parse(bytes, 904);
        Assert.Equal((0, -2048), (low.Planes[0].Points[0].U, low.Planes[0].Points[0].V));
        // -7168 is the one in-range value the reference still folds (by -8192, giving 1024).
        Assert.Equal((1024, 100), (low.Planes[0].Points[1].U, low.Planes[0].Points[1].V));
        Assert.Equal((0, 0), (low.Planes[0].Points[2].U, low.Planes[0].Points[2].V));
        Assert.Equal((16384, 16384), (low.Planes[0].Points[3].U, low.Planes[0].Points[3].V));

        var high = XnGineMesh.Parse(bytes, 905);
        Assert.Equal((16384, 14336), (high.Planes[0].Points[0].U, high.Planes[0].Points[0].V));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(14335, 14335)]
    [InlineData(-14335, -14335)]
    [InlineData(16384, 0)]
    [InlineData(14336, -2048)]
    [InlineData(-7168, 1024)]
    [InlineData(-16384, 0)]
    public void UnpackUv_FoldsByTheNearestMultipleOf8192(int packed, int expected)
    {
        Assert.Equal(expected, XnGineMesh.UnpackUv(packed));
    }

    [Fact]
    public void Parse_RejectsMalformedRecords()
    {
        var good = XnGineMeshFixture.Quad();

        Assert.Throws<InvalidDataException>(() => XnGineMesh.Parse(good.AsMemory(0, 40), 1));

        var badTag = (byte[])good.Clone();
        badTag[1] = (byte)'3';
        Assert.Throws<InvalidDataException>(() => XnGineMesh.Parse(badTag, 1));

        var tooManyPoints = (byte[])good.Clone();
        tooManyPoints[4] = 0xFF;
        tooManyPoints[5] = 0x7F;
        Assert.Throws<InvalidDataException>(() => XnGineMesh.Parse(tooManyPoints, 1));

        var planeListPastEnd = (byte[])good.Clone();
        planeListPastEnd[60] = 0xFF;
        planeListPastEnd[61] = 0x7F;
        Assert.Throws<InvalidDataException>(() => XnGineMesh.Parse(planeListPastEnd, 1));

        // A point offset that is not a whole point.
        var misaligned = (byte[])good.Clone();
        var planeListOffset = BitConverter.ToInt32(good, 60);
        misaligned[planeListOffset + 8 + 8] = 5;
        Assert.Throws<InvalidDataException>(() => XnGineMesh.Parse(misaligned, 1));

        // A plane list that ends inside a point.
        Assert.Throws<InvalidDataException>(() => XnGineMesh.Parse(good.AsMemory(0, planeListOffset + 8 + 4), 1));
    }
}