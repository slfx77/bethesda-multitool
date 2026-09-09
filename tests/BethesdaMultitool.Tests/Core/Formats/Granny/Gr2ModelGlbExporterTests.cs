using System.Numerics;
using BethesdaMultitool.Core.Formats.Granny;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Granny;

/// <summary>
///     The exporter's basis conversion on a hand-built file: one triangle whose algebraic cross
///     product <c>(B-A)×(C-A)</c> equals its stored normal — the relation the Van Buren deathclaw
///     carries on 1,448 of 1,448 triangles. A reflection (the left-handed LightWave basis, Back
///     <c>-Z</c>) negates that cross product relative to the transformed normal, so the exporter
///     must reverse the winding there and ONLY there: a right-handed basis is the control that
///     catches an unconditional reversal, the left-handed case catches a missing one.
/// </summary>
public sealed class Gr2ModelGlbExporterTests
{
    private static Gr2File OneTriangle(Vector3 back)
    {
        var mesh = new Gr2Mesh
        {
            Name = "tri",
            VertexLayout = "Position:Real32[3]@0,Normal:Real32[3]@12",
            VertexStride = 24,
            Positions = [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1)],
            // (1,0,0) × (0,0,1) = (0,-1,0): the stored normal IS the algebraic cross product.
            Normals = [new Vector3(0, -1, 0), new Vector3(0, -1, 0), new Vector3(0, -1, 0)],
            TextureCoordinates = null,
            Influences = null,
            Indices = [0, 1, 2],
            SixteenBitIndices = false,
            TopologyDefect = null,
            Groups = [new Gr2TriangleGroup(0, 0, 1)],
            MaterialNames = [],
            BoneBindings = []
        };
        return new Gr2File
        {
            Name = "synthetic",
            ArtToolInfo = new Gr2ArtToolInfo("Synthetic", 1, 0, 1f, Vector3.Zero, Vector3.UnitX, Vector3.UnitY, back),
            ExporterName = null,
            FromFileName = null,
            TextureFileNames = [],
            MaterialNames = [],
            Skeletons = [],
            Models =
            [
                new Gr2Model { Name = "model", Skeleton = null, InitialPlacement = Matrix4x4.Identity, Meshes = [mesh] }
            ],
            Meshes = [mesh],
            Animations = []
        };
    }

    private static (Vector3[] Positions, Vector3[] Normals, (int A, int B, int C)[] Triangles) ReadBack(byte[] glb)
    {
        var model = ModelRoot.ParseGLB(glb);
        var primitive = Assert.Single(Assert.Single(model.LogicalMeshes).Primitives);
        return (primitive.GetVertexAccessor("POSITION").AsVector3Array().ToArray(),
            primitive.GetVertexAccessor("NORMAL").AsVector3Array().ToArray(),
            primitive.GetTriangleIndices().ToArray());
    }

    private static Vector3 WindingNormal(
        (Vector3[] Positions, Vector3[] Normals, (int A, int B, int C)[] Triangles) read)
    {
        var (a, b, c) = Assert.Single(read.Triangles);
        return Vector3.Cross(read.Positions[b] - read.Positions[a], read.Positions[c] - read.Positions[a]);
    }

    [Fact]
    public void LeftHandedSource_FlipsZAndReversesTheWinding()
    {
        var read = ReadBack(Gr2ModelGlbExporter.WriteToBytes(OneTriangle(new Vector3(0, 0, -1))));

        // The third file vertex (0,0,1) lands at (0,0,-1); the normal is unchanged by this basis.
        Assert.Contains(new Vector3(0, 0, -1), read.Positions);
        Assert.DoesNotContain(new Vector3(0, 0, 1), read.Positions);
        Assert.All(read.Normals, normal => Assert.Equal(new Vector3(0, -1, 0), normal));
        // Reflection alone would give (0,+1,0) here; the reversed winding restores agreement.
        Assert.Equal(new Vector3(0, -1, 0), WindingNormal(read));
    }

    [Fact]
    public void RightHandedSource_KeepsPositionsAndWinding()
    {
        var read = ReadBack(Gr2ModelGlbExporter.WriteToBytes(OneTriangle(new Vector3(0, 0, 1))));

        Assert.Contains(new Vector3(0, 0, 1), read.Positions);
        Assert.Equal(new Vector3(0, -1, 0), WindingNormal(read));
    }

    [Fact]
    public void BasisMatrix_IsIdentityForANonOrthonormalToolBasis()
    {
        var skewed = new Gr2ArtToolInfo("Bad", 0, 0, 1f, Vector3.Zero, Vector3.UnitX, Vector3.UnitX, Vector3.UnitZ);

        Assert.Equal(Matrix4x4.Identity, Gr2ModelGlbExporter.BasisMatrix(skewed));
        Assert.Equal(Matrix4x4.Identity, Gr2ModelGlbExporter.BasisMatrix(null));
        Assert.Equal(-1f,
            Gr2ModelGlbExporter.BasisMatrix(new Gr2ArtToolInfo("LW", 7, 5, 1f, Vector3.Zero, Vector3.UnitX,
                Vector3.UnitY, new Vector3(0, 0, -1))).GetDeterminant());
    }
}
