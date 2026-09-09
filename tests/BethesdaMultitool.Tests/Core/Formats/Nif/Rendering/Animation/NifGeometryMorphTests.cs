using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

public sealed class NifGeometryMorphTests
{
    [Fact]
    public void QuadraticScalar_UsesAuthoredOutgoingIncomingTangentsWithoutDurationMultiplier()
    {
        var curve = new NifMorphScalarCurve(NifKeyInterpolation.Quadratic, 0f,
        [
            new NifMorphScalarKey(1.9333334f, 0.06185294f, 99f, 0.32407466f),
            new NifMorphScalarKey(2.2666667f, 0.6070215f, 0f, 99f)
        ]);
        Assert.Equal(0.5370299208443612f, NifGeometryMorphEvaluator.Sample(curve, 2.1833333373069763f), 5);
        Assert.Equal(curve.Keys[0].Value, NifGeometryMorphEvaluator.Sample(curve, curve.Keys[0].Time));
        Assert.Equal(curve.Keys[1].Value, NifGeometryMorphEvaluator.Sample(curve, curve.Keys[1].Time));
    }

    [Theory]
    [InlineData(-2f, -4f)]
    [InlineData(-0.001f, -0.002f)]
    [InlineData(-0.0009f, 0f)]
    [InlineData(0.0009f, 0f)]
    [InlineData(0.001f, 0.002f)]
    [InlineData(2f, 4f)]
    public void RelativeComposition_PreservesSignedThresholdEndpointsAndForcesBase(float weight, float expectedDelta)
    {
        var morph = Read(new Fixture());
        morph.Targets[0] = morph.Targets[0] with
        {
            Curve = new NifMorphScalarCurve(NifKeyInterpolation.Linear, 0f, [new NifMorphScalarKey(0f, -100f)])
        };
        morph.Targets[1] = morph.Targets[1] with
        {
            Curve = new NifMorphScalarCurve(NifKeyInterpolation.Linear, weight, [])
        };
        var weights = new float[2];
        NifGeometryMorphEvaluator.SampleWeights(morph, 0f, weights);
        Assert.Equal(1f, weights[0]);
        Assert.Equal(weight, weights[1]);
        var position = NifGeometryMorphEvaluator.EvaluatePosition(morph, weights, 0);
        Assert.Equal(10f + expectedDelta, position.X, 5);
        Assert.Equal(20f, position.Y);
    }

    [Fact]
    public void Reader_RetainsIndependentTangentsAndReplacesStoredZeroWeight()
    {
        var morph = Read(new Fixture());
        Assert.Equal(2, morph.Targets.Length);
        Assert.Equal(3, morph.VertexCount);
        Assert.Equal(NifKeyInterpolation.Quadratic, morph.Targets[1].Curve.Interpolation);
        Assert.Equal(2f, morph.Targets[1].Curve.Keys[0].OutTangent);
        Assert.Equal(0f, morph.Targets[1].Curve.FallbackWeight);
        Assert.Equal(0.75f, NifGeometryMorphEvaluator.Sample(morph.Targets[1].Curve, 1f));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("normalFlags")]
    [InlineData("controllerFlags")]
    [InlineData("skin")]
    [InlineData("sourceOrder")]
    [InlineData("relative")]
    [InlineData("truncated")]
    [InlineData("tcb")]
    [InlineData("tangent")]
    [InlineData("repeatedTime")]
    public void Reader_RejectsUnsupportedOrMalformedGeometryWithoutThrowing(string defect)
    {
        var fixture = new Fixture();
        switch (defect)
        {
            case "count": fixture.U32(1, 33, uint.MaxValue); break;
            case "normalFlags": fixture.U16(1, 26, 1); break;
            case "controllerFlags": fixture.U16(1, 4, 104); break;
            case "skin": fixture.I32(0, 80, 5); break;
            case "sourceOrder": fixture.Float(5, 9, 11f); break;
            case "relative": fixture.Data[fixture.Nif.Blocks[2].DataOffset + 8] = 0; break;
            case "truncated": fixture.Nif.Blocks[2].Size--; break;
            case "tcb": fixture.U32(4, 4, 3); break;
            case "tangent": fixture.Float(4, 20, float.NaN); break;
            case "repeatedTime": fixture.Float(4, 24, 0f); break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        Assert.False(NifGeometryMorphReader.TryRead(fixture.Data, fixture.Nif, fixture.Nif.Blocks[1], out _));
    }

    [Theory]
    [InlineData(-1, 0f)]
    [InlineData(3, 0.6f)]
    public void MissingKeyData_UsesStoredWeightOrValidInterpolatorDefault(int interpolator, float expected)
    {
        var fixture = new Fixture();
        fixture.I32(1, 45, interpolator);
        fixture.Float(3, 0, 0.6f);
        fixture.I32(3, 4, -1);
        var morph = Read(fixture);
        Assert.Equal(expected, NifGeometryMorphEvaluator.Sample(morph.Targets[1].Curve, 1f));
    }

    [Fact]
    public void NativePose_ResetsLocalPositionsOnSeekAndPreservesAuthoredAttributes()
    {
        var morph = Read(new Fixture());
        var vertices = Vertices(morph);
        var original = (GpuMeshUploader.GpuVertex[])vertices.Clone();
        var weights = new float[2];
        BethesdaViewerGeometryMorphPolicy.Pose(morph, 1f, vertices, weights);
        Assert.Equal(11.5f, vertices[0].Position.X);
        Assert.Equal(original[0].Normal, vertices[0].Normal);
        Assert.Equal(original[0].TexCoord, vertices[0].TexCoord);
        Assert.Equal(original[0].VertexColorRgba, vertices[0].VertexColorRgba);
        BethesdaViewerGeometryMorphPolicy.Pose(morph, 0f, vertices, weights);
        Assert.Equal(original, vertices);
        BethesdaViewerGeometryMorphPolicy.Pose(morph, 1f, vertices, weights);
        Assert.Equal(11.5f, vertices[0].Position.X);
    }

    [Fact]
    public void Decoder_SnapshotsMorphDataAndMaterializerAppliesWorldEnvelopeExactlyOnce()
    {
        var fixture = new Fixture();
        var morph = Read(fixture);
        var scene = Scene(morph);
        Assert.True(
            BethesdaViewerGeometryMorphPolicy.TryCreateClip(fixture.Data, fixture.Nif, scene, out var clip,
                out var error), error);
        Assert.NotNull(clip);
        Assert.Empty(clip.NodeTracks);
        scene.AnimationClips.Add(clip);
        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);
        var snapshot = Assert.Single(Assert.Single(decoded.AnimationClips).GeometryMorphTracks!);
        var authoredTarget = clip.GeometryMorphTracks![0].Morph.Targets[1];
        authoredTarget.Positions[0] = new Vector3(10000f);
        authoredTarget.Curve.Keys[0] = new NifMorphScalarKey(0f, 10000f);
        Assert.Equal(new Vector3(2f, 0f, 0f), snapshot.Morph.Targets[1].Positions[0]);
        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(decoded);
        Assert.Empty(posed.UnsupportedMeshParts);
        Assert.Equal(new Vector3(110f, 20f, 30f), posed.Mesh.Submeshes[0].Vertices[0].Position);
        Assert.NotNull(posed.Bounds);
        Assert.InRange(posed.Bounds.Value.Minimum.X, 109.9f, 110f);
        Assert.InRange(posed.Bounds.Value.Maximum.X, 113f, 113.1f);
        Assert.InRange(posed.Mesh.Submeshes[0].LocalBoundsCenter.X, 111.4f, 111.6f);
    }

    [Fact]
    public void Decoder_RejectsReorderedDecodedBaseWithoutLosingStaticMesh()
    {
        var morph = Read(new Fixture());
        var scene = Scene(morph);
        scene.AnimationClips.Add(Clip(morph));
        scene.MeshParts[0].Submesh.Positions[0] += 5f;
        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);
        Assert.Empty(decoded.AnimationClips);
        Assert.NotEmpty(decoded.MeshParts[0].Submesh.Vertices);
    }

    [Theory]
    [InlineData(2f, 1f, -0.5f, 1f)]
    [InlineData(0f, 1f, 0f, 0f)]
    public void MorphOnlyClock_UsesAuthoredRateAndDormantZeroFrequency(float frequency, float phase, float origin,
        float duration)
    {
        var clip = Clip(Read(new Fixture()) with { Frequency = frequency, Phase = phase });
        var window = BethesdaViewerAnimationClockPolicy.Resolve(clip);
        Assert.Equal(origin, window.RawOriginSeconds);
        Assert.Equal(duration, window.PresentationDurationSeconds);
    }

    [Fact]
    public void GeometryClip_RejectsMixedNodeTracksAndKeepsHigherOrderOvershootWithinBounds()
    {
        var morph = Read(new Fixture());
        var curve = new NifMorphScalarCurve(NifKeyInterpolation.Quadratic, 0f,
            [new NifMorphScalarKey(0f, 0f, 0f, 8f), new NifMorphScalarKey(2f, 0f, -8f)]);
        morph.Targets[1] = morph.Targets[1] with { Curve = curve };
        var bounds = NifGeometryMorphEvaluator.GetConservativeBounds(morph);
        var weights = new float[2];
        NifGeometryMorphEvaluator.SampleWeights(morph, 1f, weights);
        Assert.Equal(2f, weights[1]);
        Assert.True(bounds.Maximum.X >= 15f);
        var clip = Clip(morph) with
        {
            NodeTracks =
            [
                new BethesdaViewerNodeAnimationTrack(0, 1f, 0f,
                    BethesdaViewerKeyInterpolation.Linear, [], BethesdaViewerKeyInterpolation.Linear,
                    [new BethesdaViewerVector3Key(0f, Vector3.Zero)], BethesdaViewerKeyInterpolation.Linear, [])
            ]
        };
        Assert.False(BethesdaViewerAnimationValidator.TryValidate(clip, 2, 1, out _));
    }

    private static NifGeometryMorphData Read(Fixture fixture)
    {
        Assert.True(NifGeometryMorphReader.TryRead(fixture.Data, fixture.Nif, fixture.Nif.Blocks[1], out var morph));
        return morph;
    }

    private static BethesdaViewerAnimationClip Clip(NifGeometryMorphData morph)
    {
        return new BethesdaViewerAnimationClip("Morph", morph.StartTime, morph.StopTime, morph.Loops, [], [], [],
            GeometryMorphTracks: [new BethesdaViewerGeometryMorphTrack(0, morph)]);
    }

    private static GpuMeshUploader.GpuVertex[] Vertices(NifGeometryMorphData morph)
    {
        return morph.Targets[0].Positions.Select(static position => new GpuMeshUploader.GpuVertex
        {
            Position = position, Normal = Vector3.UnitZ, TexCoord = new Vector2(0.2f, 0.4f),
            VertexColorRgba = 0x12345678u, Tangent = Vector3.UnitX, Bitangent = Vector3.UnitY
        }).ToArray();
    }

    private static BethesdaViewerScene Scene(NifGeometryMorphData morph)
    {
        var scene = new BethesdaViewerScene("morph-fixture", BethesdaViewerScenePurpose.RawNif);
        var world = Matrix4x4.CreateTranslation(100f, 0f, 0f);
        var node = scene.AddNode("Flag", 0, world, world, BethesdaViewerNodeRole.Attachment);
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = "Flag", NodeIndex = node,
            Submesh = new RenderableSubmesh
            {
                SourceBlockIndex = 0,
                Positions = morph.Targets[0].Positions.SelectMany(static p => new[] { p.X, p.Y, p.Z }).ToArray(),
                Triangles = [0, 1, 2]
            }
        });
        return scene;
    }

    private sealed class Fixture
    {
        internal Fixture()
        {
            var kinds = new[]
            {
                "NiTriStrips", "NiGeomMorpherController", "NiMorphData", "NiFloatInterpolator", "NiFloatData",
                "NiTriStripsData"
            };
            var sizes = new[] { 88, 53, 89, 8, 40, 45 };
            var offset = 0;
            for (var index = 0; index < sizes.Length; index++)
            {
                Nif.Blocks.Add(new BlockInfo
                    { Index = index, TypeName = kinds[index], DataOffset = offset, Size = sizes[index] });
                offset += sizes[index];
            }

            Nif.Strings.AddRange(["Base", "Delta"]);
            Nif.BlockCount = Nif.Blocks.Count;
            Data = new byte[offset];
            I32(0, 8, 1);
            I32(0, 72, -1);
            I32(0, 76, 5);
            I32(0, 80, -1);
            I32(1, 0, -1);
            U16(1, 4, 72);
            Float(1, 6, 1f);
            Float(1, 18, 2f);
            I32(1, 22, 0);
            I32(1, 28, 2);
            U32(1, 33, 2);
            I32(1, 37, 3);
            I32(1, 45, 3);
            U32(2, 0, 2);
            U32(2, 4, 3);
            Data[Nif.Blocks[2].DataOffset + 8] = 1;
            I32(2, 49, 1);
            var positions = new[]
                { new Vector3(10f, 20f, 30f), new Vector3(11f, 20f, 30f), new Vector3(10f, 21f, 30f) };
            U16(5, 4, 3);
            Data[Nif.Blocks[5].DataOffset + 8] = 1;
            for (var vertex = 0; vertex < positions.Length; vertex++)
            {
                var position = positions[vertex];
                Float(2, 13 + vertex * 12, position.X);
                Float(2, 17 + vertex * 12, position.Y);
                Float(2, 21 + vertex * 12, position.Z);
                Float(2, 53 + vertex * 12, 2f);
                Float(5, 9 + vertex * 12, position.X);
                Float(5, 13 + vertex * 12, position.Y);
                Float(5, 17 + vertex * 12, position.Z);
            }

            Float(3, 0, float.MinValue);
            I32(3, 4, 4);
            U32(4, 0, 2);
            U32(4, 4, 2);
            Float(4, 20, 2f);
            Float(4, 24, 2f);
            Float(4, 28, 1f);
        }

        internal NifInfo Nif { get; } = new() { BinaryVersion = 0x14020007, BsVersion = 34, UserVersion = 11 };
        internal byte[] Data { get; }

        internal void U32(int block, int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(Nif.Blocks[block].DataOffset + offset), value);
        }

        internal void I32(int block, int offset, int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(Data.AsSpan(Nif.Blocks[block].DataOffset + offset), value);
        }

        internal void U16(int block, int offset, ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(Data.AsSpan(Nif.Blocks[block].DataOffset + offset), value);
        }

        internal void Float(int block, int offset, float value)
        {
            I32(block, offset, BitConverter.SingleToInt32Bits(value));
        }
    }
}