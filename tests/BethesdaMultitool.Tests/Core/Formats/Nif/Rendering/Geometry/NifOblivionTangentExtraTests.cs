using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

public sealed class NifOblivionTangentExtraTests
{
    [Theory]
    [InlineData(0x14000004u, "Gamebryo File Format, Version 20.0.0.4")]
    [InlineData(0x14000005u, "Gamebryo File Format, Version 20.0.0.5")]
    public void Read_PlanarArraysPreserveAsymmetricAxesMagnitudeAndMirroredHandedness(uint version, string header)
    {
        var fixture = new NifOblivionTangentTestData();
        fixture.Info.BinaryVersion = version;
        fixture.Info.HeaderString = header;
        var basis = Read(fixture) ?? throw new InvalidOperationException("The supported authored basis was rejected.");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(NifOblivionTangentTestData.Tangents[i], VectorAt(basis.Tangents, i));
            Assert.Equal(NifOblivionTangentTestData.Bitangents[i], VectorAt(basis.Bitangents, i));
        }

        Assert.True(Orientation(basis.Tangents, basis.Bitangents, 0) > 0);
        Assert.True(Orientation(basis.Tangents, basis.Bitangents, 1) < 0);
        Assert.True(Orientation(basis.Tangents, basis.Bitangents, 2) > 0);
    }

    [Theory]
    [InlineData("endian")]
    [InlineData("strings")]
    [InlineData("user")]
    [InlineData("stream")]
    [InlineData("version")]
    [InlineData("header")]
    [InlineData("owner-type")]
    [InlineData("extra-type")]
    [InlineData("name")]
    public void Read_UnsupportedOrMismatchedIdentityReturnsNoBasis(string mismatch)
    {
        var fixture = new NifOblivionTangentTestData();
        switch (mismatch)
        {
            case "endian": fixture.Info.IsBigEndian = true; break;
            case "strings": fixture.Info.HasInlineStrings = false; break;
            case "user": fixture.Info.UserVersion = 12; break;
            case "stream": fixture.Info.BsVersion = 34; break;
            case "version": fixture.Info.BinaryVersion = 0x14020007; break;
            case "header": fixture.Info.HeaderString = "Gamebryo File Format, Version 20.0.0.4"; break;
            case "owner-type": fixture.Info.Blocks[0].TypeName = "NiNode"; break;
            case "extra-type": fixture.Info.Blocks[1].TypeName = "NiStringExtraData"; break;
            case "name": fixture.Data[fixture.Info.Blocks[1].DataOffset + 4] = (byte)'t'; break;
            default: throw new ArgumentOutOfRangeException(nameof(mismatch));
        }

        Assert.Null(Read(fixture));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void Read_OnlyTheOwningShapeMaySupplyAttachments(int shapeIndex)
    {
        var fixture = new NifOblivionTangentTestData();
        Assert.Null(NifOblivionTangentExtraReader.Read(
            fixture.Data, fixture.Info, shapeIndex, 3, NifOblivionTangentTestData.Normals));
    }

    [Fact]
    public void Read_DetachedAndAmbiguousExtraBlocksAreNotUsed()
    {
        Assert.Null(Read(new NifOblivionTangentTestData(links: [])));
        Assert.Null(Read(new NifOblivionTangentTestData(links: [1, 1])));
        Assert.Null(Read(new NifOblivionTangentTestData(links: [-1, 1])));
        Assert.Null(Read(new NifOblivionTangentTestData(links: [int.MaxValue])));
        // A valid unrelated attached geometry block does not become a tangent source.
        Assert.Null(Read(new NifOblivionTangentTestData(links: [2])));
        Assert.NotNull(Read(new NifOblivionTangentTestData(links: [2, 1])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void Read_VertexCountMustExactlyMatchPayloadAndNormals(int count)
    {
        var fixture = new NifOblivionTangentTestData();
        var normals = count is > 0 and <= 4 ? new float[count * 3] : NifOblivionTangentTestData.Normals;
        Assert.Null(NifOblivionTangentExtraReader.Read(
            fixture.Data, fixture.Info, 0, count, normals));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(48u)]
    [InlineData(71u)]
    [InlineData(73u)]
    [InlineData(uint.MaxValue)]
    public void Read_DeclaredPayloadLengthMustMatchBothContainerAndVertexCount(uint length)
    {
        var fixture = new NifOblivionTangentTestData();
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Data.AsSpan(fixture.ExtraPayloadOffset - 4), length);
        Assert.Null(Read(fixture));
    }

    [Theory]
    [InlineData(0, -1, 4)]
    [InlineData(0, 0, 3)]
    [InlineData(0, int.MaxValue, 8)]
    [InlineData(1, -1, 72)]
    [InlineData(1, 0, -1)]
    [InlineData(1, 0, int.MaxValue)]
    public void Read_InvalidBlockSpansReturnNoBasis(int index, int offset, int size)
    {
        var fixture = new NifOblivionTangentTestData();
        fixture.Info.Blocks[index].DataOffset = offset;
        fixture.Info.Blocks[index].Size = size;
        Assert.Null(Read(fixture));
    }

    [Theory]
    [InlineData("shape-name")]
    [InlineData("extra-count")]
    [InlineData("extra-name")]
    [InlineData("truncated-payload")]
    [InlineData("trailing-payload")]
    public void Read_MalformedContainersFailClosed(string defect)
    {
        var fixture = new NifOblivionTangentTestData();
        switch (defect)
        {
            case "shape-name": BinaryPrimitives.WriteUInt32LittleEndian(fixture.Data, uint.MaxValue); break;
            case "extra-count":
                BinaryPrimitives.WriteUInt32LittleEndian(
                    fixture.Data.AsSpan(NifOblivionTangentTestData.ShapeCountOffset), uint.MaxValue);
                break;
            case "extra-name":
                BinaryPrimitives.WriteUInt32LittleEndian(
                    fixture.Data.AsSpan(fixture.Info.Blocks[1].DataOffset), uint.MaxValue);
                break;
            case "truncated-payload": fixture.Info.Blocks[1].Size--; break;
            case "trailing-payload": fixture.Info.Blocks[1].Size++; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        Assert.Null(Read(fixture));
    }

    [Theory]
    [InlineData(0, float.NaN)]
    [InlineData(4, float.PositiveInfinity)]
    [InlineData(36, float.NegativeInfinity)]
    [InlineData(64, float.MaxValue)]
    public void Read_NonfiniteOrOverflowingBasisFailsClosed(int byteOffset, float value)
    {
        var fixture = new NifOblivionTangentTestData();
        BinaryPrimitives.WriteSingleLittleEndian(fixture.Data.AsSpan(fixture.ExtraPayloadOffset + byteOffset), value);
        Assert.Null(Read(fixture));
    }

    [Fact]
    public void Read_MissingInvalidNormalsAndZeroVectorsAreNotAccepted()
    {
        var fixture = new NifOblivionTangentTestData();
        Assert.Null(NifOblivionTangentExtraReader.Read(fixture.Data, null, 0, 3, NifOblivionTangentTestData.Normals));
        Assert.Null(NifOblivionTangentExtraReader.Read(fixture.Data, fixture.Info, 0, 3, null));
        Assert.Null(NifOblivionTangentExtraReader.Read(fixture.Data, fixture.Info, 0, 3, new float[9]));
        var normals = (float[])NifOblivionTangentTestData.Normals.Clone();
        normals[8] = float.NaN;
        Assert.Null(NifOblivionTangentExtraReader.Read(fixture.Data, fixture.Info, 0, 3, normals));
        // A zero first B is unusable. A finite nonzero collinear pair remains authored data;
        // it must not cause the whole shape to lose its basis or be silently reconstructed.
        BinaryPrimitives.WriteSingleLittleEndian(fixture.Data.AsSpan(fixture.ExtraPayloadOffset + 4), 0f);
        Assert.Null(Read(fixture));
        BinaryPrimitives.WriteSingleLittleEndian(fixture.Data.AsSpan(fixture.ExtraPayloadOffset), 4f);
        var basis = Read(fixture) ?? throw new InvalidOperationException("Authored collinear pair was rejected.");
        Assert.Equal(new Vector3(2, 0, 0), VectorAt(basis.Tangents, 0));
        Assert.Equal(new Vector3(4, 0, 0), VectorAt(basis.Bitangents, 0));
    }

    [Fact]
    public void Read_RetailNearCollinearPairIsPreservedWithoutAnInventedDeterminantGate()
    {
        // Stock greaves shape18 vertex144, independently read by the retained NifAnalyzer hex probe.
        var normal = new Vector3(-0.8094918727874756f, -0.4327249228954315f, -0.3968275189399719f);
        var tangent = new Vector3(0.514984130859375f, -0.1986926943063736f, -0.8338539600372314f);
        var bitangent = new Vector3(-0.5149840712547302f, 0.1986927092075348f, 0.8338539004325867f);
        var fixture = new NifOblivionTangentTestData();
        var normals = (float[])NifOblivionTangentTestData.Normals.Clone();
        for (var axis = 0; axis < 3; axis++)
        {
            normals[axis] = normal[axis];
            BinaryPrimitives.WriteSingleLittleEndian(
                fixture.Data.AsSpan(fixture.ExtraPayloadOffset + axis * 4), bitangent[axis]);
            BinaryPrimitives.WriteSingleLittleEndian(
                fixture.Data.AsSpan(fixture.ExtraPayloadOffset + 36 + axis * 4), tangent[axis]);
        }

        var basis = NifOblivionTangentExtraReader.Read(fixture.Data, fixture.Info, 0, 3, normals)
                    ?? throw new InvalidOperationException("Finite authored retail pair was rejected.");
        Assert.Equal(tangent, VectorAt(basis.Tangents, 0));
        Assert.Equal(bitangent, VectorAt(basis.Bitangents, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extract_PreservesExistingInlineBasis(bool strips)
    {
        var fixture = new NifOblivionTangentTestData(strips, true);
        var mesh = Extract(fixture, Matrix4x4.Identity);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(Vector3.UnitY, VectorAt(mesh.Tangents!, i));
            Assert.Equal(-Vector3.UnitX, VectorAt(mesh.Bitangents!, i));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extract_MalformedInlineBasisIsNotSilentlyReplaced(bool strips)
    {
        var fixture = new NifOblivionTangentTestData(strips, true);
        var firstInlineTangent = fixture.Info.Blocks[2].DataOffset + 9 + 36 + 3 + 36;
        BinaryPrimitives.WriteSingleLittleEndian(fixture.Data.AsSpan(firstInlineTangent), float.NaN);
        var mesh = Extract(fixture, Matrix4x4.Identity);
        Assert.NotNull(mesh.Tangents);
        Assert.True(float.IsNaN(mesh.Tangents[0]));
        Assert.Equal(-Vector3.UnitX, VectorAt(mesh.Bitangents!, 0));
    }

    [Theory]
    [InlineData(0x14020007u, 34u)]
    [InlineData(0x14000005u, 6u)]
    public void Extract_UnsupportedGameOrStreamDoesNotConsumeTheExtra(uint version, uint stream)
    {
        var fixture = new NifOblivionTangentTestData();
        fixture.Info.BinaryVersion = version;
        fixture.Info.BsVersion = stream;
        var mesh = Extract(fixture, Matrix4x4.Identity);
        Assert.Null(mesh.Tangents);
        Assert.Null(mesh.Bitangents);
        Assert.Equal(3, mesh.VertexCount);
    }

    [Fact]
    public void Read_ReturnsIndependentArraysWithoutMutatingSource()
    {
        var fixture = new NifOblivionTangentTestData();
        var originalBytes = (byte[])fixture.Data.Clone();
        var first = Read(fixture) ?? throw new InvalidOperationException("First basis was rejected.");
        first.Tangents[0] = 1234f;
        first.Bitangents[1] = -1234f;
        var second = Read(fixture) ?? throw new InvalidOperationException("Second basis was rejected.");
        Assert.Equal(originalBytes, fixture.Data);
        Assert.Equal(NifOblivionTangentTestData.Tangents[0], VectorAt(second.Tangents, 0));
        Assert.Equal(NifOblivionTangentTestData.Bitangents[0], VectorAt(second.Bitangents, 0));
        Assert.NotSame(first.Tangents, second.Tangents);
        Assert.NotSame(first.Bitangents, second.Bitangents);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Extract_RecoversBeforeNonidentityTransformAndSkinning(bool strips, bool skinned, bool dualQuaternion)
    {
        var fixture = new NifOblivionTangentTestData(strips);
        var world = Matrix4x4.CreateRotationZ(0.6f) * Matrix4x4.CreateTranslation(10, 20, 30);
        var bone = Matrix4x4.CreateRotationX(-0.4f) * Matrix4x4.CreateTranslation(-2, 3, 4);
        ((int BoneIdx, float Weight)[][], Matrix4x4[])? skin = skinned
            ? ([[(0, 1f)], [(0, 1f)], [(0, 1f)]], [bone])
            : null;
        var mesh = Extract(fixture, world, skin, dualQuaternion);
        Assert.NotNull(mesh.Tangents);
        Assert.NotNull(mesh.Bitangents);
        Assert.Null(mesh.NormalMapTexturePath); // Tangent recovery does not invent a normal-map binding.
        var effective = skinned ? bone : world;
        for (var i = 0; i < 3; i++)
        {
            var expectedT = Vector3.TransformNormal(NifOblivionTangentTestData.Tangents[i], effective);
            var expectedB = Vector3.TransformNormal(NifOblivionTangentTestData.Bitangents[i], effective);
            if (skinned)
            {
                expectedT = Vector3.Normalize(expectedT);
                expectedB = Vector3.Normalize(expectedB);
            }

            AssertVectorClose(expectedT, VectorAt(mesh.Tangents, i));
            AssertVectorClose(expectedB, VectorAt(mesh.Bitangents, i));
            AssertVectorClose(Vector3.Transform(NifOblivionTangentTestData.Positions[i], effective),
                VectorAt(mesh.Positions, i));
        }
    }

    private static (float[] Tangents, float[] Bitangents)? Read(NifOblivionTangentTestData fixture)
    {
        return NifOblivionTangentExtraReader.Read(fixture.Data, fixture.Info, 0, 3, NifOblivionTangentTestData.Normals);
    }

    private static RenderableSubmesh Extract(
        NifOblivionTangentTestData fixture,
        Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][], Matrix4x4[])? skin = null,
        bool dualQuaternion = false)
    {
        return Assert.IsType<RenderableSubmesh>(NifSubmeshExtractor.ExtractSubmesh(
            fixture.Data, fixture.Info, 0, 2, new Dictionary<int, Matrix4x4> { [0] = transform },
            skinning: skin, useDualQuaternionSkinning: dualQuaternion));
    }

    private static float Orientation(float[] tangents, float[] bitangents, int i)
    {
        return Vector3.Dot(Vector3.Cross(Vector3.UnitZ, VectorAt(tangents, i)), VectorAt(bitangents, i));
    }

    private static Vector3 VectorAt(float[] values, int vertex)
    {
        return NifOblivionTangentTestData.VectorAt(values, vertex);
    }

    private static void AssertVectorClose(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }
}