using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Redguard;
using BethesdaMultitool.Core.Modeling.Units;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Redguard;

/// <summary>
///     The Redguard <c>.3DC</c> reader (cut-1c plan section 8, slice 6; section 4) on synthetic narrow, wide and 2-frame
///     stacks whose every expected value is worked out by hand here: the keyframe mapped as a static <c>.3D</c>, the
///     later frames as morph targets (narrow deltas exactly as stored, wide poses as absolute positions), Flat normals,
///     the Step clip with its derived final key and no authored duration, the pose-union corner rule, the coverage
///     census, the native rows, the game option and the declines. Each check that could pass vacuously is paired with
///     the plan's control, which must fail.
/// </summary>
public sealed class Redguard3DcModelReaderTests : IDisposable
{
    /// <summary>Frame 1's narrow deltas, per point p0 to p4.</summary>
    private static readonly (int X, int Y, int Z)[] DeltasOne = [(1, 2, 3), (0, -4, 0), (5, 0, -6), (0, 0, 0), (7, 8, 9)];

    /// <summary>Frame 2's narrow deltas, per point p0 to p4 (relative to the KEYFRAME, like frame 1's).</summary>
    private static readonly (int X, int Y, int Z)[] DeltasTwo = [(10, 20, 30), (1, 1, 1), (0, 0, 0), (-2, -2, -2), (3, 0, 0)];

    /// <summary>The stray point p5 <see cref="Stack" /> can add: named by no plane.</summary>
    private static readonly (int X, int Y, int Z) Stray = (7, 8, 9);

    /// <summary>The stray point's narrow deltas in frames 1 and 2.</summary>
    private static readonly (int X, int Y, int Z)[] StrayDeltas = [(11, -12, 13), (-14, 15, -16)];

    private readonly string _directory = Directory.CreateTempSubdirectory("bmt-cut1c-3dc-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    ///     The stack every test starts from: points p0 (0,0,0), p1 (256,0,0), p2 (256,0,256), p3 (0,0,256), p4
    ///     (128,256,0); plane 0 key 0x0182 the quad (p0, p1, p2, p3) with stored UVs (0,0), (64,0), (0,64), (-64,0);
    ///     plane 1 key 0x0183 the triangle (p0, p1, p4) whose c0 stores u = 16384, a value in the packed-UV fold range;
    ///     frames 1 and 2 are <see cref="DeltasOne" /> and <see cref="DeltasTwo" /> from the keyframe (a narrow stack
    ///     stores them; a wide one stores keyframe + delta as int32 poses, so both widths describe the same poses); a
    ///     40-byte unaccounted region at the end. With <paramref name="strayPoint" />, a sixth point p5
    ///     (<see cref="Stray" />, moving by <see cref="StrayDeltas" />) that no plane names. <paramref name="tag" /> is the
    ///     record's tag (a <c>v2.5</c> stack stores its corners as point index x 4).
    /// </summary>
    private static Redguard3DcTestStackBuilder Stack(bool wide = false, int frames = 3, string tag = "v2.6",
        bool strayPoint = false)
    {
        var builder = new Redguard3DcTestStackBuilder(tag, wide) { UnaccountedLength = 40 };
        List<(int X, int Y, int Z)> keyframe = [(0, 0, 0), (256, 0, 0), (256, 0, 256), (0, 0, 256), (128, 256, 0)];
        List<(int X, int Y, int Z)[]> frameDeltas = [DeltasOne, DeltasTwo];
        if (strayPoint)
        {
            keyframe.Add(Stray);
            frameDeltas = [[.. DeltasOne, StrayDeltas[0]], [.. DeltasTwo, StrayDeltas[1]]];
        }

        foreach (var (x, y, z) in keyframe)
        {
            builder.AddPoint(x, y, z);
        }

        builder.AddPlane(0x0182, [(0, 0, 0), (1, 64, 0), (2, 0, 64), (3, -64, 0)]);
        builder.AddPlane(0x0183, [(0, 16384, 0), (1, 64, 0), (4, 0, 64)]);
        foreach (var deltas in frameDeltas.Take(frames - 1))
        {
            builder.AddFrame(wide
                ? keyframe.Zip(deltas, (k, d) => (k.X + d.X, k.Y + d.Y, k.Z + d.Z)).ToArray()
                : deltas);
        }

        return builder;
    }

    /// <summary>A point's delta or pose as the document carries it: <c>(x, -y, z)</c>.</summary>
    private static Vector3 Flip((int X, int Y, int Z) value)
    {
        return new Vector3(value.X, -(float)value.Y, value.Z);
    }

    // ---- Document shape, geometry and poses.

    [Fact]
    public void Narrow_DocumentShape_UnitsBasisProvenanceAndDiagnostics()
    {
        var bytes = Stack().Build();

        var document = Redguard3DcModelTestSupport.Read(bytes).Document;

        Assert.Equal("bmt.redguard.3dc", document.SourceFormat);
        Assert.Equal("ACTOR", document.Name);
        var node = Assert.Single(document.Nodes);
        Assert.Equal(0, node.MeshIndex);
        Assert.Equal(SceneNodeRole.Transform, node.Role);
        var mesh = Assert.Single(document.Meshes);
        Assert.Equal(2, mesh.Primitives.Count);
        Assert.Equal(new[] { "TEXTURE.003#2", "TEXTURE.003#3" }, document.Materials.Select(m => m.Name).ToArray());
        Assert.All(document.Materials, m => Assert.Null(m.Texture));

        Assert.Equal(ClassicModelUnits.RedguardMetersPerUnit, document.Units!.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Assumed, document.Units.Provenance);
        Assert.StartsWith(Redguard3DcModelReader.FormatEvidence + ": ", document.Units.Evidence, StringComparison.Ordinal);
        Assert.EndsWith(ClassicModelUnits.RedguardActorEvidence, document.Units.Evidence, StringComparison.Ordinal);
        Assert.Same(XnGineModelBasis.Basis, document.SourceBasis);
        Assert.Equal(Redguard3DcModelTestSupport.DefaultPath, document.SourceProvenance!.RelativePath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), document.SourceProvenance.Sha256);

        var codes = document.Diagnostics.Select(d => d.Code).ToList();
        Assert.Contains(ClassicModelUnits.ActorScaleDiagnostic, codes);
        Assert.Contains(Redguard3DcModelDiagnostics.UvUndecoded, codes);
        Assert.Contains(Redguard3DcModelDiagnostics.PoseRateAssumed, codes);
        Assert.Contains(XnGineModelDiagnostics.TextureSizeAssumed, codes);

        // Control: the actor-scale pin discriminates, because a static .3D document does not carry it.
        var staticDocument = XnGineModelTestSupport.Read(XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714)).Document;
        Assert.DoesNotContain(staticDocument.Diagnostics, d => d.Code == ClassicModelUnits.ActorScaleDiagnostic);
    }

    /// <summary>
    ///     The keyframe is mapped exactly as a static <c>.3D</c>: primitive 0 (plane 0, the quad) has face (c0, c3, c2, c1)
    ///     = points (0, 3, 2, 1) and, all four corners kept in every pose, the fan (1, 2, 3), (1, 3, 0) from c1 written
    ///     reversed as face-local (3, 1, 2), (3, 0, 1); primitive 1 (plane 1) has face (c0, c2, c1) = points (0, 4, 1) and
    ///     the triangle (0, 1, 2). Positions are (x, -y, z) of the keyframe, and every primitive shares one point domain
    ///     named by frame 0's point block (never by header +48, which holds frame 1's offset).
    /// </summary>
    [Fact]
    public void Keyframe_FacesTrianglesPointsAndDomain_AreTheStaticMapping()
    {
        var builder = Stack();
        var bytes = builder.Build();

        var primitives = Redguard3DcModelTestSupport.Read(bytes).Document.Meshes[0].Primitives;

        var quad = primitives[0];
        Assert.Equal(new[] { 0, 3, 2, 1 }, quad.PointIndices!.Values.ToArray());
        Assert.Equal(5, quad.PointIndices.PointCount);
        Assert.Equal(new[] { 4 }, quad.Faces!.FaceSizes.ToArray());
        Assert.Equal(new[] { 3, 1, 2, 3, 0, 1 }, quad.Indices.ToArray());
        Assert.Equal(new[] { new Vector3(0, -0f, 0), new Vector3(0, -0f, 256), new Vector3(256, -0f, 256), new Vector3(256, -0f, 0) },
            quad.Vertices.Select(v => v.Position).ToArray());
        Assert.Equal(new[] { 0 }, XnGineModelTestSupport.PlaneOrdinals(quad));

        var triangle = primitives[1];
        Assert.Equal(new[] { 0, 4, 1 }, triangle.PointIndices!.Values.ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, triangle.Indices.ToArray());
        Assert.Equal(new[] { 1 }, XnGineModelTestSupport.PlaneOrdinals(triangle));

        var domain = $"xngine.points:memory:{Redguard3DcModelTestSupport.DefaultPath}@{builder.Layout.Points[0]}";
        Assert.All(primitives, p => Assert.Equal(domain, p.PointIndices!.SourceDomainId));
        Assert.NotEqual(builder.Layout.Points[0], builder.Layout.Points[1]);
    }

    /// <summary>
    ///     A narrow stack's targets are the stored int16 deltas, (dx, -dy, dz) per vertex through its source point:
    ///     target 1 (frame 2) is <see cref="DeltasTwo" /> exactly. Control: accumulating the deltas frame to frame (frame
    ///     1's plus frame 2's) fails that pin on every vertex whose point moves in both frames.
    /// </summary>
    [Fact]
    public void NarrowTargets_AreTheStoredDeltas_AndAccumulatingThemFailsThePin()
    {
        var primitives = Redguard3DcModelTestSupport.Read(Stack().Build()).Document.Meshes[0].Primitives;

        foreach (var primitive in primitives)
        {
            Assert.Equal(new[] { "pose 001", "pose 002" }, primitive.MorphTargets.Select(t => t.Name).ToArray());
            var points = primitive.PointIndices!.Values;
            var one = primitive.MorphTargets[0];
            var two = primitive.MorphTargets[1];
            Assert.Null(one.AbsolutePositions);
            Assert.Null(two.AbsolutePositions);
            Assert.Null(one.NormalDeltas);
            Assert.Equal(points.Select(p => Flip(DeltasOne[p])).ToArray(), one.PositionDeltas.ToArray());
            Assert.Equal(points.Select(p => Flip(DeltasTwo[p])).ToArray(), two.PositionDeltas.ToArray());

            var accumulated = points.Select(p => Flip((DeltasOne[p].X + DeltasTwo[p].X, DeltasOne[p].Y + DeltasTwo[p].Y,
                DeltasOne[p].Z + DeltasTwo[p].Z))).ToArray();
            Assert.NotEqual(accumulated, two.PositionDeltas.ToArray());
        }

        // Pinned by hand for p0: (10, -20, 30), not the accumulated (11, -22, 33).
        Assert.Equal(new Vector3(10, -20, 30), primitives[0].MorphTargets[1].PositionDeltas[0]);
    }

    /// <summary>
    ///     A wide stack's targets are the stored int32 poses as absolute positions (x, -y, z), with no position deltas; the
    ///     poses equal the narrow stack's keyframe-plus-delta, so both widths describe the same shapes.
    /// </summary>
    [Fact]
    public void WideTargets_AreTheStoredPoses_AsAbsolutePositions()
    {
        var wide = Redguard3DcModelTestSupport.Read(Stack(wide: true).Build()).Document.Meshes[0].Primitives;
        var narrow = Redguard3DcModelTestSupport.Read(Stack().Build()).Document.Meshes[0].Primitives;

        for (var p = 0; p < wide.Count; p++)
        {
            for (var t = 0; t < 2; t++)
            {
                var target = wide[p].MorphTargets[t];
                Assert.Empty(target.PositionDeltas);
                var expected = narrow[p].Vertices.Zip(narrow[p].MorphTargets[t].PositionDeltas,
                    (v, d) => v.Position + d).ToArray();
                Assert.Equal(expected, target.AbsolutePositions!.ToArray());
            }
        }

        Assert.Equal(new Vector3(257, -1, 1), wide[0].MorphTargets[1].AbsolutePositions![3]);
    }

    /// <summary>
    ///     Both widths are Flat (plan decision D4): Flat mode, Flat provenance, zero vertex normals, no normal deltas.
    ///     Control: the same narrow primitive marked Authored fails Shared's Flat agreement.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Normals_AreFlat_AndAuthoredProvenanceFailsTheFlatAgreement(bool wide)
    {
        var document = Redguard3DcModelTestSupport.Read(Stack(wide).Build()).Document;

        Assert.All(document.Meshes[0].Primitives, primitive =>
        {
            Assert.Equal(SceneNormalMode.Flat, primitive.NormalMode);
            Assert.Equal(SceneNormalProvenanceKind.Flat, primitive.NormalProvenance!.Kind);
            Assert.All(primitive.Vertices, v => Assert.Equal(Vector3.Zero, v.Normal));
            Assert.All(primitive.MorphTargets, t => Assert.Null(t.NormalDeltas));
        });

        var authored = Redguard3DcModelTestSupport.WithPrimitive(document, 0,
            Redguard3DcModelTestSupport.WithNormalProvenance(document.Meshes[0].Primitives[0],
                SceneNormalProvenanceKind.Authored));
        var failure = Assert.Throws<InvalidDataException>(() => SceneValidation.ValidateStructure(authored));
        Assert.Contains("Normal provenance and the primitive normal mode must agree", failure.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>xngine.uv16</c> keeps the stored values (16384 at plane 1's c0, which the reference unfolds), and the portable
    ///     UVs are the <c>.3D</c> rule without the unfold: the triangle accumulates c1 and c2, so in face order (c0, c2,
    ///     c1) they are 16384/16/64 = 16, (16448/16/64, 64/16/64) = (16.0625, 0.0625) and (16.0625, 0). Control: the
    ///     legacy keyframe parse unfolds that value.
    /// </summary>
    [Fact]
    public void Uvs_KeepTheStoredValues_AndThePortableRuleSkipsTheUnfold()
    {
        var bytes = Stack().Build();

        var triangle = Redguard3DcModelTestSupport.Read(bytes).Document.Meshes[0].Primitives[1];

        Assert.Equal(new (short, short)[] { (16384, 0), (0, 64), (64, 0) }, XnGineModelTestSupport.StoredUv(triangle));
        Assert.Equal(new[] { new Vector2(16, 0), new Vector2(16.0625f, 0.0625f), new Vector2(16.0625f, 0) },
            triangle.Vertices.Select(v => v.TexCoord).ToArray());

        var legacy = Redguard3DcFile.Parse(bytes, "ACTOR.3DC").KeyframeMesh;
        Assert.NotEqual(16384, legacy.Planes[1].Points[0].U);
        Assert.Equal(XnGineMesh.UnpackUv(16384), legacy.Planes[1].Points[0].U);
    }

    /// <summary>
    ///     A corner collinear only in the keyframe is kept because another pose keeps it (plan section 4): the quad (p0,
    ///     p1, p5, p2) has p5 on the edge p1-p2 in the keyframe and moved off it in frame 1, so all four corners are kept
    ///     and fanned from c1 as (1, 2, 3), (1, 3, 0), written face-local (3, 1, 2), (3, 0, 1). Control: the keyframe-only
    ///     corner test keeps c1, c3, c0 and gives the one triangle (3, 0, 1).
    /// </summary>
    [Fact]
    public void KeyframeOnlyCornerTest_FailsOnACornerCollinearOnlyInTheKeyframe()
    {
        var builder = new Redguard3DcTestStackBuilder();
        builder.AddPoint(0, 0, 0);
        builder.AddPoint(256, 0, 0);
        builder.AddPoint(256, 0, 128);
        builder.AddPoint(256, 0, 256);
        builder.AddPlane(0x0182, [(0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0)]);
        builder.AddFrame([(0, 0, 0), (0, 0, 0), (64, 0, 0), (0, 0, 0)]);
        var bytes = builder.Build();

        var document = Redguard3DcModelTestSupport.Read(bytes).Document;

        var quad = Assert.Single(document.Meshes[0].Primitives);
        Assert.Equal(new[] { 3, 1, 2, 3, 0, 1 }, quad.Indices.ToArray());
        var rule = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.UvRuleKind);
        Assert.Equal(XnGineTriangulation.PoseUnionRuleId, rule["triangulationRule"]!.GetValue<string>());
        Assert.Equal(1, rule["poseDependentNgons"]!.GetValue<int>());
        Assert.Contains(document.Diagnostics, d => d.Code == Redguard3DcModelDiagnostics.PoseDependentCorners);

        var keyframe = Redguard3DcFile.Parse(bytes, "P.3DC").Keyframe;
        var keyframeOnly = XnGineTriangulation.Triangulate(0, keyframe.ToList(), new XnGineMeshPoint(0, -256, 0));
        Assert.Equal(new[] { 1, 3, 0 }, keyframeOnly.KeptCorners.ToArray());
        var keyframeOnlyIndices = keyframeOnly.Triangles.SelectMany(t => new[]
        {
            XnGineModelGeometry.CornerOfVertex(4, t.A), XnGineModelGeometry.CornerOfVertex(4, t.C),
            XnGineModelGeometry.CornerOfVertex(4, t.B)
        }).ToArray();
        Assert.Equal(new[] { 3, 0, 1 }, keyframeOnlyIndices);
        Assert.NotEqual(keyframeOnlyIndices, quad.Indices.ToArray());

        // The stack without the pose-dependent corner reports none.
        var plain = Redguard3DcModelTestSupport.Read(Stack().Build()).Document;
        Assert.Equal(0, XnGineModelTestSupport.Payload(plain, XnGineModelNativeState.UvRuleKind)
            ["poseDependentNgons"]!.GetValue<int>());
        Assert.DoesNotContain(plain.Diagnostics, d => d.Code == Redguard3DcModelDiagnostics.PoseDependentCorners);
    }

    // ---- The clip.

    /// <summary>
    ///     One clip 'poses', one Step track on node 0 over the 2 targets: keys at 0, 1/15, 2/15 and the derived final key
    ///     3/15 s, weights none, pose 1, pose 2, pose 2 again; no authored duration; 15 frames per second Assumed. At every
    ///     mid-frame time frame i shows exactly (one-hot) and the clip ends at N/15. Controls: the same keys with Linear
    ///     blend two poses at mid-frame; without the derived key the clip ends at (N-1)/15, failing the end pin.
    /// </summary>
    [Fact]
    public void Clip_IsStepOneHotAt15FramesPerSecond_WithTheDerivedFinalKey()
    {
        const int frames = 3;
        var document = Redguard3DcModelTestSupport.Read(Stack().Build()).Document;

        var clip = Assert.Single(document.Animations);
        Assert.Equal("poses", clip.Name);
        Assert.Null(clip.DurationSeconds);
        Assert.Equal(15.0, clip.Timing!.FramesPerSecond);
        Assert.Equal(SceneValueProvenance.Assumed, clip.Timing.Provenance);
        var track = Assert.Single(clip.MorphTracks);
        Assert.Equal(0, track.NodeIndex);
        Assert.Equal(frames - 1, track.TargetCount);
        Assert.Equal(SceneInterpolation.Step, track.Interpolation);
        Assert.Equal(new[] { 0f, 1 / 15f, 2 / 15f, 3 / 15f }, track.Times.ToArray());
        Assert.Equal(new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 1f }, track.Weights.ToArray());

        for (var frame = 0; frame < frames; frame++)
        {
            Assert.Equal(Redguard3DcModelTestSupport.OneHot(frames - 1, frame),
                Redguard3DcModelTestSupport.Sample(track, (frame + 0.5f) / 15f));
        }

        Assert.Equal(frames / 15f, track.Times[^1]);

        var linear = new SceneMorphTrack(track.NodeIndex, track.TargetCount, track.Times, track.Weights,
            SceneInterpolation.Linear);
        Assert.NotEqual(Redguard3DcModelTestSupport.OneHot(frames - 1, 1), Redguard3DcModelTestSupport.Sample(linear, 1.5f / 15f));
        Assert.NotEqual(frames / 15f, track.Times.Take(frames).Last());

        var row = XnGineModelTestSupport.Payload(document, Redguard3DcModelNativeState.ClipKind);
        Assert.Equal(frames, row["derivedFinalKey"]!["key"]!.GetValue<int>());
        Assert.Equal(frames - 1, row["derivedFinalKey"]!["repeatsFrame"]!.GetValue<int>());
        Assert.Equal(new SceneElementRef(SceneElementKind.Animation, 0),
            Assert.Single(XnGineModelTestSupport.Rows(document, Redguard3DcModelNativeState.ClipKind)).Target);
    }

    /// <summary>
    ///     The 2-frame stack (the BLOBA001 shape): one target, keys 0, 1/15 and 2/15, weights 0, 1, 1. A single-frame stack
    ///     has no target and no clip, and says so.
    /// </summary>
    [Fact]
    public void TwoFrameStack_HasOneTarget_AndASingleFrameStackHasNoClip()
    {
        var two = Redguard3DcModelTestSupport.Read(Stack(frames: 2).Build()).Document;

        var track = Assert.Single(Assert.Single(two.Animations).MorphTracks);
        Assert.Equal(1, track.TargetCount);
        Assert.Equal(new[] { 0f, 1 / 15f, 2 / 15f }, track.Times.ToArray());
        Assert.Equal(new[] { 0f, 1f, 1f }, track.Weights.ToArray());
        Assert.All(two.Meshes[0].Primitives, p => Assert.Equal("pose 001", Assert.Single(p.MorphTargets).Name));

        var one = Redguard3DcModelTestSupport.Read(Stack(frames: 1).Build()).Document;
        Assert.Empty(one.Animations);
        Assert.All(one.Meshes[0].Primitives, p => Assert.Empty(p.MorphTargets));
        Assert.Contains(one.Diagnostics, d => d.Code == Redguard3DcModelDiagnostics.SingleFrame);
        Assert.Equal(new SceneElementRef(SceneElementKind.Document),
            Assert.Single(XnGineModelTestSupport.Rows(one, Redguard3DcModelNativeState.ClipKind)).Target);
    }

    /// <summary>
    ///     The GLB writer converts both widths (the wide absolute poses lowered to relative targets). Control: the draft's
    ///     clip (no derived key, an authored duration of N/15 against a last key at (N-1)/15) is refused as NotSupported
    ///     with the <c>animation.duration-unsupported</c> row.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Glb_ConvertsTheClip_AndAnAuthoredDurationDifferingFromTheLastKeyIsRefused(bool wide)
    {
        const int frames = 3;
        var document = Redguard3DcModelTestSupport.Read(Stack(wide).Build()).Document;

        var (rows, written, refusal) = await Redguard3DcModelTestSupport.WriteGlbAsync(document, _directory,
            wide ? "wide" : "narrow");
        Assert.Null(refusal);
        Assert.Equal(ModelItemOutcome.Converted, written!.Outcome);
        Assert.DoesNotContain(rows, r => r.ReasonCode == "animation.duration-unsupported");
        if (wide)
        {
            Assert.Contains(rows, r => r.ReasonCode == "geometry.absolute-morph-relative");
        }

        var track = document.Animations[0].MorphTracks[0];
        var draft = new SceneAnimation("poses",
            [new SceneMorphTrack(0, frames - 1, track.Times.Take(frames), track.Weights.Take(frames * (frames - 1)),
                SceneInterpolation.Step)],
            durationSeconds: frames / 15f, timing: document.Animations[0].Timing);
        var (draftRows, draftWritten, draftRefusal) = await Redguard3DcModelTestSupport.WriteGlbAsync(
            Redguard3DcModelTestSupport.WithAnimations(document, [draft]), _directory, wide ? "wide-draft" : "narrow-draft");
        Assert.Null(draftWritten);
        Assert.NotNull(draftRefusal);
        Assert.Contains(draftRows, r => r.ReasonCode == "animation.duration-unsupported" &&
                                        r.Outcome == ModelFidelityOutcome.Degraded);
    }

    /// <summary>
    ///     Blender carries the clip natively (<c>animation-native-curves</c>, Converted; Shared SA5). Control: a clip the
    ///     admission refuses (events only, no driven channel) reports <c>later-cut(1b)</c>, Dropped.
    /// </summary>
    [Fact]
    public void Blender_CarriesTheClipNatively_AndARefusedClipReportsLaterCut()
    {
        var document = Redguard3DcModelTestSupport.Read(Stack().Build()).Document;

        var clip = Assert.Single(XnGineModelTestSupport.BlenderRows(document),
            r => r.Target.Kind == SceneElementKind.Animation && r.FeatureId == "clip");
        Assert.Equal("animation-native-curves", clip.ReasonCode);
        Assert.Equal(ModelFidelityOutcome.Converted, clip.Outcome);

        var refused = Redguard3DcModelTestSupport.WithAnimations(document,
            [new SceneAnimation("poses", [], events: [new SceneAnimationEvent(0f, "start")])]);
        SceneValidation.ValidateStructure(refused);
        var refusedRow = Assert.Single(XnGineModelTestSupport.BlenderRows(refused),
            r => r.Target.Kind == SceneElementKind.Animation && r.FeatureId == "clip");
        Assert.Equal("later-cut(1b)", refusedRow.ReasonCode);
        Assert.Equal(ModelFidelityOutcome.Dropped, refusedRow.Outcome);
    }

    // ---- Coverage and native state.

    /// <summary>
    ///     The census is the header counts plus the tiling: header, frame-block, frame-table (plus its fourth dwords on a
    ///     wide stack), two planes, three blocks per frame and the unaccounted region. Typed: header, table, planes and
    ///     every point block. Control: dropping one plane's classification fails the independent count.
    /// </summary>
    [Theory]
    [InlineData(false, 15)]
    [InlineData(true, 16)]
    public void Coverage_IsTheIndependentCensus(bool wide, int elements)
    {
        var bytes = Stack(wide).Build();

        var coverage = Redguard3DcModelTestSupport.Read(bytes).Coverage;

        Assert.Equal(elements, coverage.TotalCount);
        var kinds = coverage.Classifications.ToDictionary(c => c.ElementIdentity, c => c.Kind);
        foreach (var typed in new[] { "header", "frame-table", "plane:0", "plane:1", "frame:0:points", "frame:1:points", "frame:2:points" })
        {
            Assert.Equal(ModelSourceCoverageKind.Typed, kinds[typed]);
        }

        foreach (var native in new[] { "frame-block", "frame:0:normals", "frame:2:plane-data", "unaccounted" })
        {
            Assert.Equal(ModelSourceCoverageKind.NativeOnly, kinds[native]);
        }

        Assert.Equal(wide, kinds.ContainsKey(Redguard3DcModelCoverage.FourthDwordsElement));
        var normals = coverage.Classifications.Single(c => c.ElementIdentity == "frame:1:normals");
        Assert.Equal(wide ? Redguard3DcModelCoverage.WideNormalsReason : Redguard3DcModelCoverage.NarrowNormalsReason,
            normals.Reason);

        var file = Redguard3DcFile.Parse(bytes, "ACTOR.3DC");
        var census = Redguard3DcModelCoverage.Census(bytes, file.Tiling());
        var rows = coverage.Classifications.Where(c => c.ElementIdentity != "plane:1").ToList();
        Assert.Throws<ArgumentException>(() =>
            new ModelSourceCoverage(coverage.Source, coverage.CensusEvidence, census, rows));
    }

    /// <summary>
    ///     The native rows: the header with the frame layout and the frame-1 offsets note, the six preamble dwords, the
    ///     frame table with every block's digest, the unaccounted region, the planes and the rule row; with full detail
    ///     one raw row per frame block.
    /// </summary>
    [Fact]
    public void NativeRows_CarryTheFrameStack()
    {
        var builder = Stack(wide: true);
        var bytes = builder.Build();
        var layout = builder.Layout;

        var document = Redguard3DcModelTestSupport.Read(bytes).Document;

        var header = XnGineModelTestSupport.Payload(document, Redguard3DcModelNativeState.HeaderKind);
        Assert.Equal("wide", header["width"]!.GetValue<string>());
        Assert.Equal(4, header["frameRecordDwords"]!.GetValue<int>());
        Assert.Equal(3, header["frameCount"]!.GetValue<int>());
        Assert.True(header["headerOffsetsAreFrameOne"]!.GetValue<bool>());
        Assert.Equal(layout.Points[1], header["plus48"]!.GetValue<int>());

        var preamble = XnGineModelTestSupport.Payload(document, Redguard3DcModelNativeState.PreambleKind);
        Assert.Equal(new[] { 88, 11, 40, 8209, 44, 55 },
            preamble["dwords"]!.AsArray().Select(d => d!.GetValue<int>()).ToArray());

        var frames = XnGineModelTestSupport.Payload(document, Redguard3DcModelNativeState.FramesKind)["frames"]!.AsArray();
        Assert.Equal(3, frames.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(layout.Points[i], frames[i]!["pointOffset"]!.GetValue<int>());
            Assert.Equal(layout.Normals[i], frames[i]!["normalOffset"]!.GetValue<int>());
            Assert.Equal(0x1000 + i, frames[i]!["fourthDword"]!.GetValue<int>());
            var expected = Enumerable.Repeat((byte)(0xA0 + i), 2 * 12).ToArray();
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), frames[i]!["normalsSha256"]!.GetValue<string>());
        }

        var unaccounted = XnGineModelTestSupport.Payload(document, Redguard3DcModelNativeState.UnaccountedKind);
        Assert.Equal(layout.UnaccountedStart, unaccounted["start"]!.GetValue<int>());
        Assert.Equal(40, unaccounted["length"]!.GetValue<int>());
        Assert.Equal(2, XnGineModelTestSupport.Payload(document, XnGineModelNativeState.PlanesKind)["count"]!.GetValue<int>());
        var rule = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.UvRuleKind);
        Assert.False(rule["unfoldApplied"]!.GetValue<bool>());
        Assert.False(rule["uvDecoded"]!.GetValue<bool>());
        Assert.Empty(XnGineModelTestSupport.Rows(document, Redguard3DcModelNativeState.FrameNormalsKind));

        var full = Redguard3DcModelTestSupport.Read(bytes, detail: ModelNativeDetail.Full).Document;
        var raw = XnGineModelTestSupport.Rows(full, Redguard3DcModelNativeState.FrameNormalsKind);
        Assert.Equal(3, raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            Assert.True(raw[i].HasRawContent);
            Assert.Equal(Enumerable.Repeat((byte)(0xA0 + i), 2 * 12).ToArray(), raw[i].CopyRawContent());
        }

        Assert.Equal(3, XnGineModelTestSupport.Rows(full, Redguard3DcModelNativeState.FramePlaneDataKind).Count);
    }

    // ---- The game and the declines.

    [Fact]
    public void GameOption_MayNameRedguard_AndAnotherGameThrows()
    {
        var bytes = Stack().Build();

        var redguard = Redguard3DcModelTestSupport.Read(bytes, XnGineModelTestSupport.Game("redguard")).Document;
        Assert.StartsWith("Redguard per bmt.game=redguard", redguard.Units!.Evidence, StringComparison.Ordinal);
        var auto = Redguard3DcModelTestSupport.Read(bytes, XnGineModelTestSupport.Game("auto")).Document;
        Assert.StartsWith(Redguard3DcModelReader.FormatEvidence, auto.Units!.Evidence, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => Redguard3DcModelTestSupport.Read(bytes, XnGineModelTestSupport.Game("battlespire")));
        Assert.Throws<ArgumentException>(() => Redguard3DcModelTestSupport.Read(bytes, XnGineModelTestSupport.Game("fnv")));

        var install = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BethesdaModelRegistration.ClassicGameOption] = "Daggerfall"
        };
        var refuted = Redguard3DcModelTestSupport.Read(bytes, install).Document;
        Assert.Contains(refuted.Diagnostics, d => d.Code == XnGineGameIdentity.StepRefutedDiagnostic);
        install[BethesdaModelRegistration.ClassicGameOption] = "Redguard";
        Assert.DoesNotContain(Redguard3DcModelTestSupport.Read(bytes, install).Document.Diagnostics,
            d => d.Code == XnGineGameIdentity.StepRefutedDiagnostic);
    }

    /// <summary>
    ///     A stack that does not tile is rejected as invalid data (one stray byte after the declared region); a 3dfx tag, a
    ///     v2.5 tag and a static mesh are declined as not supported. Control: the intact stack reads.
    /// </summary>
    [Fact]
    public void Declines_NonTilingStacks_OtherTags_AndStaticMeshes()
    {
        var bytes = Stack().Build();
        Assert.NotNull(Redguard3DcModelTestSupport.Read(bytes).Document);

        var stray = bytes.Append((byte)0).ToArray();
        Assert.Throws<InvalidDataException>(() => Redguard3DcModelTestSupport.Read(stray));

        var fxart = Retagged(bytes, "v4.0");
        Assert.Equal(XnGineModelFormatMetadata.FxartUnsupportedReason,
            Assert.Throws<NotSupportedException>(() => Redguard3DcModelTestSupport.Read(fxart)).Message);
        Assert.Equal(Redguard3DcModelFormatMetadata.V25UnsupportedReason,
            Assert.Throws<NotSupportedException>(() => Redguard3DcModelTestSupport.Read(Stack(tag: "v2.5").Build()))
                .Message);
        Assert.Throws<NotSupportedException>(() =>
            Redguard3DcModelTestSupport.Read(XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714)));
    }

    /// <summary>
    ///     Slice-6 review finding 4: a header whose declared sizes do not fit the file is refused as invalid data with the
    ///     declared-size reason, before the parse. The review's point count goes through its non-tiling sibling (an
    ///     unaccounted length of 4): the tiling would name another reason, and a regressed check cannot allocate the 4 GiB
    ///     keyframe. The normal offset 2^31 - 4, whose block end wraps below zero, is a stack the parser tiles; its block
    ///     slices threw <see cref="ArgumentOutOfRangeException" /> in the read. Control: the consistent three-point record
    ///     reads, one primitive and no clip.
    /// </summary>
    [Fact]
    public void DeclaredSizesOutsideTheFile_AreInvalidData_AndTheConsistentRecordReads()
    {
        var overflow = Assert.Throws<InvalidDataException>(() => Redguard3DcModelTestSupport.Read(
            Redguard3DcModelTestSupport.Record(0x15555556, (132, 140, 144), 4, 156)));
        Assert.Equal("The header declares 357913942 points, a 4294967304-byte keyframe, more than the 156-byte file " +
                     "holds.", overflow.Message);

        const int wrapping = int.MaxValue - 3;
        var wrapped = Assert.Throws<InvalidDataException>(() => Redguard3DcModelTestSupport.Read(
            Redguard3DcModelTestSupport.Record(3, (132, wrapping, 168), wrapping - 180, 180, (0, 0, 0), (256, 0, 0),
                (0, 0, 256))));
        Assert.Equal("Frame 0's normal block offset 2147483644 lies outside the 180-byte file.", wrapped.Message);

        var document = Redguard3DcModelTestSupport.Read(Redguard3DcModelTestSupport.ConsistentRecord()).Document;
        Assert.Single(document.Meshes[0].Primitives);
        Assert.Empty(document.Animations);
    }

    /// <summary>
    ///     Slice-6 review finding 2: a point no plane names (p5 (7, 8, 9), moving by (11, -12, 13) in frame 1 and (-14, 15,
    ///     -16) in frame 2) reaches no vertex or morph target, so the <c>bmt.redguard.3dc.unreferenced-points</c> row keeps
    ///     its stored value in every frame: the keyframe triple, then the narrow deltas as stored, or the wide poses
    ///     (keyframe plus delta: (18, -4, 22) and (-7, 23, -7)) as stored. With full detail its raw content is the
    ///     canonical bytes, rebuilt here from the builder's own block offsets. Every point block's Typed classification
    ///     names the row. Control: the stack without p5 has no row and no reason.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreferencedPoints_KeepTheirStoredValues_AndThePointBlocksNameTheRow(bool wide)
    {
        var builder = Stack(wide, strayPoint: true);
        var bytes = builder.Build();

        var result = Redguard3DcModelTestSupport.Read(bytes, detail: ModelNativeDetail.Full);

        var document = result.Document;
        Assert.All(document.Meshes[0].Primitives, p => Assert.DoesNotContain(5, p.PointIndices!.Values));
        var row = Assert.Single(XnGineModelTestSupport.Rows(document, Redguard3DcModelNativeState.UnreferencedPointsKind));
        Assert.Equal(new SceneElementRef(SceneElementKind.Mesh, 0), row.Target);
        var payload = XnGineModelTestSupport.Payload(document, Redguard3DcModelNativeState.UnreferencedPointsKind);
        Assert.Equal(1, payload["count"]!.GetValue<int>());
        var point = Assert.Single(payload["points"]!.AsArray())!;
        Assert.Equal(5, point["point"]!.GetValue<int>());
        Assert.Equal(new[] { 7, 8, 9 }, Ints(point["keyframe"]));
        var later = point["later"]!.AsArray().Select(Ints).ToArray();
        Assert.Equal(2, later.Length);
        Assert.Equal(wide ? new[] { 18, -4, 22 } : new[] { 11, -12, 13 }, later[0]);
        Assert.Equal(wide ? new[] { -7, 23, -7 } : new[] { -14, 15, -16 }, later[1]);

        var stride = wide ? 12 : 6;
        var layout = builder.Layout;
        var canonical = new byte[4 + 12 + 2 * stride];
        BinaryPrimitives.WriteInt32LittleEndian(canonical, 5);
        bytes.AsSpan(layout.Points[0] + 5 * 12, 12).CopyTo(canonical.AsSpan(4));
        bytes.AsSpan(layout.Points[1] + 5 * stride, stride).CopyTo(canonical.AsSpan(16));
        bytes.AsSpan(layout.Points[2] + 5 * stride, stride).CopyTo(canonical.AsSpan(16 + stride));
        Assert.Equal(canonical, row.CopyRawContent());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(canonical)), payload["sha256"]!.GetValue<string>());

        var reason = Redguard3DcModelCoverage.UnreferencedPointsReason(1);
        Assert.Contains(Redguard3DcModelNativeState.UnreferencedPointsKind, reason, StringComparison.Ordinal);
        for (var frame = 0; frame < 3; frame++)
        {
            var classification = result.Coverage.GetClassification(Redguard3DcModelCoverage.FramePointsElement(frame));
            Assert.Equal(ModelSourceCoverageKind.Typed, classification.Kind);
            Assert.Equal(reason, classification.Reason);
        }

        var plain = Redguard3DcModelTestSupport.Read(Stack(wide).Build(), detail: ModelNativeDetail.Full);
        Assert.Empty(XnGineModelTestSupport.Rows(plain.Document, Redguard3DcModelNativeState.UnreferencedPointsKind));
        Assert.Null(plain.Coverage.GetClassification("frame:1:points").Reason);
    }

    /// <summary>
    ///     Slice-6 review finding 5: the one-hot clip holds (N + 1) x (N - 1) weights, so the reader refuses, from the
    ///     header and before the parse, a stack past 1,024 frames (1,025 frames: 1,050,624 weights over the 1,048,576
    ///     budget) although its census, 1 plane + 3,075 frame blocks + 5 elements, is far inside the element budget.
    ///     Control: a 228-frame stack, the longest retail length, reads with its 229 x 227 = 51,983 weights; the bound
    ///     sits exactly between 1,024 frames (1,048,575 weights) and 1,025.
    /// </summary>
    [Fact]
    public void ClipBudget_RefusesMoreThan1024Frames_AndA228FrameStackReads()
    {
        var refused = Assert.Throws<NotSupportedException>(() => Redguard3DcModelTestSupport.Read(LongStack(1025)));
        Assert.Contains("1050624 weights", refused.Message, StringComparison.Ordinal);

        var clip = Assert.Single(Redguard3DcModelTestSupport.Read(LongStack(228)).Document.Animations);
        Assert.Equal(229 * 227, Assert.Single(clip.MorphTracks).Weights.Count);

        Redguard3DcModelReader.CheckElementBudget(1, 1024);
        Assert.Throws<NotSupportedException>(() => Redguard3DcModelReader.CheckElementBudget(1, 1025));
    }

    /// <summary>
    ///     Slice-6 review finding 5, the morph targets: N - 1 targets of one position per plane corner. A 229-frame stack
    ///     of 73 planes, each naming all 255 points of a convex 255-gon, has 18,615 corners and needs 228 x 18,615 =
    ///     4,244,220 positions, past the 4,194,304 budget: refused after the parse and before the geometry. Control: the
    ///     same planes over 3 frames read (18,615 vertices, 2 x 18,615 positions); the bound sits exactly at 4,194,304.
    /// </summary>
    [Fact]
    public void MorphBudget_RefusesMoreThan4194304Positions_AndTheSamePlanesOverThreeFramesRead()
    {
        var refused = Assert.Throws<NotSupportedException>(() => Redguard3DcModelTestSupport.Read(PolygonStack(229)));
        Assert.Contains("4244220 morph positions", refused.Message, StringComparison.Ordinal);

        var document = Redguard3DcModelTestSupport.Read(PolygonStack(3)).Document;
        var primitive = Assert.Single(document.Meshes[0].Primitives);
        Assert.Equal(18_615, primitive.Vertices.Count);
        Assert.Equal(2, primitive.MorphTargets.Count);

        Redguard3DcModelReader.CheckMorphBudget(4096, 1025);
        Assert.Throws<NotSupportedException>(() => Redguard3DcModelReader.CheckMorphBudget(4097, 1025));
    }

    /// <summary>
    ///     Plan row 6's package-version control (slice-6 review finding 7), headless: Shared's Blender writer plans and
    ///     writes the package zip without locating or running Blender, and its manifest declares version 4 for a
    ///     <c>.3DC</c> document that carries the pose clip. Controls: the slice-5 static <c>.3D</c> fixture and a
    ///     single-frame stack (no clip) both write version 2, so a <c>.3DC</c> package written at the static version, or
    ///     one that lost its clip, fails the pin.
    /// </summary>
    [Fact]
    public async Task BlenderPackage_IsVersion4WithTheClip_And2WithoutOne()
    {
        var stack = Redguard3DcModelTestSupport.Read(Stack().Build()).Document;
        Assert.Equal(4, await Redguard3DcModelTestSupport.PackageVersionAsync(stack, _directory, "stack"));

        var staticMesh = XnGineModelTestSupport.Read(XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714)).Document;
        Assert.Equal(2, await Redguard3DcModelTestSupport.PackageVersionAsync(staticMesh, _directory, "static"));

        var single = Redguard3DcModelTestSupport.Read(Stack(frames: 1).Build()).Document;
        Assert.Empty(single.Animations);
        Assert.Equal(2, await Redguard3DcModelTestSupport.PackageVersionAsync(single, _directory, "single"));
    }

    /// <summary>A narrow stack of three points, one triangle and <paramref name="frames" /> frames, every later delta zero.</summary>
    private static byte[] LongStack(int frames)
    {
        var builder = new Redguard3DcTestStackBuilder();
        builder.AddPoint(0, 0, 0);
        builder.AddPoint(256, 0, 0);
        builder.AddPoint(0, 0, 256);
        builder.AddPlane(0x0182, [(0, 0, 0), (1, 64, 0), (2, 0, 64)]);
        for (var frame = 1; frame < frames; frame++)
        {
            builder.AddFrame([(0, 0, 0), (0, 0, 0), (0, 0, 0)]);
        }

        return builder.Build();
    }

    /// <summary>
    ///     A narrow stack of <paramref name="frames" /> frames (every later delta zero) whose 255 points lie on a circle of
    ///     radius 10,000 in the XZ plane and whose 73 planes each name all 255 in order: a convex 255-gon, so every corner
    ///     passes the reference corner test and every plane is drawn.
    /// </summary>
    private static byte[] PolygonStack(int frames)
    {
        const int corners = 255;
        var builder = new Redguard3DcTestStackBuilder();
        for (var k = 0; k < corners; k++)
        {
            var angle = 2 * Math.PI * k / corners;
            builder.AddPoint((int)Math.Round(10_000 * Math.Cos(angle)), 0, (int)Math.Round(10_000 * Math.Sin(angle)));
        }

        var polygon = Enumerable.Range(0, corners).Select(static k => (k, (short)0, (short)0)).ToArray();
        for (var plane = 0; plane < 73; plane++)
        {
            builder.AddPlane(0x0182, polygon);
        }

        var still = new (int X, int Y, int Z)[corners];
        for (var frame = 1; frame < frames; frame++)
        {
            builder.AddFrame(still);
        }

        return builder.Build();
    }

    /// <summary>A JSON array of integers as an array.</summary>
    private static int[] Ints(JsonNode? array)
    {
        return array!.AsArray().Select(static value => value!.GetValue<int>()).ToArray();
    }

    /// <summary>The same bytes under another four-character tag.</summary>
    internal static byte[] Retagged(byte[] bytes, string tag)
    {
        var copy = bytes.ToArray();
        System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(copy, 0);
        return copy;
    }
}
