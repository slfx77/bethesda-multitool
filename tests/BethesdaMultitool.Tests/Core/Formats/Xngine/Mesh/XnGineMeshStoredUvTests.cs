using BethesdaMultitool.Core.Formats.Xngine;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Xngine.Mesh;

/// <summary>
///     The cut-1c additions to <see cref="XnGineMesh" /> (plan section 8, slice 4): the stored-UV
///     mode that keeps a first-three-corner value of 14336 or more exactly as the record stores it
///     (control: the default Reference mode unfolds it, as every legacy caller expects), the count of
///     values the unfold changed, the plane-list end, and the declared byte areas that tile the record.
///     <see cref="XnGineMeshTests" /> is untouched; every legacy reading it pins still comes out of the
///     default mode.
/// </summary>
public sealed class XnGineMeshStoredUvTests
{
    /// <summary>A quad whose first three corners carry packed-range values and whose fourth carries one too.</summary>
    private static byte[] PackedQuad()
    {
        var plane = new XnGineMeshFixture.Plane(XnGineMeshFixture.Texture(1, 1),
            [(0, 16384, 14336), (1, -7168, 100), (2, 16384, 0), (3, 16384, 16384)], (0, -256, 0));
        return XnGineMeshFixture.Build("v2.7", [(0, 0, 0), (256, 0, 0), (256, 0, 256), (0, 0, 256)], [plane]);
    }

    [Fact]
    public void StoredMode_KeepsTheFoldRangeValues_TheReferenceModeUnfolds()
    {
        var bytes = PackedQuad();

        var stored = XnGineMesh.Parse(bytes, 904, uvHandling: XnGineUvHandling.Stored);
        var reference = XnGineMesh.Parse(bytes, 904);

        Assert.Equal(XnGineUvHandling.Stored, stored.UvHandling);
        Assert.Equal(XnGineUvHandling.Reference, reference.UvHandling);

        // Stored: 14336 and above survive on every corner, exactly as the record stores them.
        Assert.Equal([(16384, 14336), (-7168, 100), (16384, 0), (16384, 16384)],
            stored.Planes[0].Points.Select(p => (p.U, p.V)));

        // Control: the default mode folds the first three corners (the legacy reading XnGineMeshTests
        // pins), so a stored mode that quietly delegated to it would fail the pin above.
        Assert.Equal([(0, -2048), (1024, 100), (0, 0), (16384, 16384)],
            reference.Planes[0].Points.Select(p => (p.U, p.V)));
    }

    [Fact]
    public void UnfoldedUvValueCount_CountsTheValuesTheUnfoldChanged()
    {
        var bytes = PackedQuad();

        // Corner 0 u and v, corner 1 u (-7168 is the in-range value the reference still folds) and
        // corner 2 u change; corner 1 v (100), corner 2 v (0) and every fourth-corner value do not.
        Assert.Equal(4, XnGineMesh.Parse(bytes, 904).UnfoldedUvValueCount);
        Assert.Equal(0, XnGineMesh.Parse(bytes, 904, uvHandling: XnGineUvHandling.Stored).UnfoldedUvValueCount);

        // At id 905 the gate is closed: nothing changes and the two modes read the same values.
        var highReference = XnGineMesh.Parse(bytes, 905);
        var highStored = XnGineMesh.Parse(bytes, 905, uvHandling: XnGineUvHandling.Stored);
        Assert.Equal(0, highReference.UnfoldedUvValueCount);
        Assert.Equal(highStored.Planes[0].Points, highReference.Planes[0].Points);
    }

    [Fact]
    public void BattlespireLayout_ReadsIdenticallyUnderBothModes_EvenAtIdZero()
    {
        var plane = new XnGineMeshFixture.Plane(0, [(0, 16384, 14336), (1, -7168, 0), (2, 16384, 0)], (0, -256, 0),
            0x0012_3456);
        var bytes = XnGineMeshFixture.Build("v2.7", [(0, 0, 0), (256, 0, 0), (256, 0, 256)], [plane],
            layout: XnGineMeshLayout.Battlespire);

        var reference = XnGineMesh.Parse(bytes, 0, XnGineMeshLayout.Battlespire);
        var stored = XnGineMesh.Parse(bytes, 0, XnGineMeshLayout.Battlespire, uvHandling: XnGineUvHandling.Stored);

        Assert.Equal(0, reference.UnfoldedUvValueCount);
        Assert.Equal([(16384, 14336), (-7168, 0), (16384, 0)], reference.Planes[0].Points.Select(p => (p.U, p.V)));
        Assert.Equal(reference.Planes[0].Points, stored.Planes[0].Points);
    }

    [Fact]
    public void StoredMode_ChangesNothingButTheUvs()
    {
        var bytes = PackedQuad();

        var stored = XnGineMesh.Parse(bytes, 904, uvHandling: XnGineUvHandling.Stored);
        var reference = XnGineMesh.Parse(bytes, 904);

        Assert.Equal(reference.Points, stored.Points);
        Assert.Equal(reference.UniqueTextures, stored.UniqueTextures);
        Assert.Equal(reference.Planes[0].Normal, stored.Planes[0].Normal);
        Assert.Equal(reference.Planes[0].TextureKey, stored.Planes[0].TextureKey);
        Assert.Equal(reference.Planes[0].Points.Select(p => p.PointIndex), stored.Planes[0].Points.Select(p => p.PointIndex));
        Assert.Equal(reference.PlaneListEnd, stored.PlaneListEnd);
        Assert.Equal(reference.RecordLength, stored.RecordLength);
    }

    [Fact]
    public void PlaneListEnd_IsWhereTheWalkStopped()
    {
        // The fixture lays the 24-byte plane data straight after the plane list, so the header's
        // plane-data offset is an independent statement of where the list ends.
        var bytes = XnGineMeshFixture.Quad();

        var mesh = XnGineMesh.Parse(bytes, 5000);

        Assert.Equal(BitConverter.ToInt32(bytes, 24), mesh.PlaneListEnd);
        Assert.Equal(BitConverter.ToInt32(bytes, 60) + 8 + 4 * 8, mesh.PlaneListEnd);
        Assert.Equal(bytes.Length, mesh.RecordLength);
    }

    [Fact]
    public void DeclaredAreas_TileTheFixtureExactly_AndNameTheResidueOtherwise()
    {
        var bytes = XnGineMeshFixture.Quad();
        var mesh = XnGineMesh.Parse(bytes, 5000);

        Assert.Equal(["header", "points", "normals", "planes", "plane-data"], mesh.DeclaredAreas().Select(a => a.Name));
        Assert.True(mesh.Tiling().TilesExactly);

        // Five trailing bytes the header does not account for become one named gap.
        byte[] paddedBytes = [.. bytes, 1, 2, 3, 4, 5];
        var padded = XnGineMesh.Parse(paddedBytes, 5000);
        var gap = Assert.Single(padded.Tiling().Gaps);
        Assert.Equal(new ByteArea($"unclaimed:{bytes.Length}-{bytes.Length + 5}", bytes.Length, bytes.Length + 5), gap);
        Assert.Equal(5, padded.Tiling().UnclaimedBytes);

        // Control: with the plane-data offset zeroed the area is not declared, and the 24 bytes it
        // covered are residue. A helper that took the offset on trust would still tile exactly.
        var noPlaneData = (byte[])bytes.Clone();
        BitConverter.GetBytes(0).CopyTo(noPlaneData, 24);
        var withoutPlaneData = XnGineMesh.Parse(noPlaneData, 5000);
        Assert.DoesNotContain(withoutPlaneData.DeclaredAreas(), a => a.Name == "plane-data");
        var residue = Assert.Single(withoutPlaneData.Tiling().Gaps);
        Assert.Equal((bytes.Length - 24, bytes.Length), (residue.Start, residue.End));
    }
}
