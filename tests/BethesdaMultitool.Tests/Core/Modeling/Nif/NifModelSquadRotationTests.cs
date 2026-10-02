using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationReaderTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTargetTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 16b, the quaternion TBC and QUADRATIC rotations (<see cref="NifModelCurveMapping" /> onto Shared's
///     Squad form; RE-24 of docs/formats/nif-animation-engine-behavior-20260925.md). The inner points are pinned bit for
///     bit against the RE-24 receipt (TestOutput/cut1b-re-20260925/RE-24/re24_oracle.json, part2_retail.testVectors:
///     the literals of the Core test SceneGamebryoSquadTests.ReceiptTrack for sneak2hhattackspin.kf block 85 and the
///     complete three-key group of h2haim.kf block 17), the sampled pose against the engine's sequence output through
///     Shared's public <see cref="ScenePoseEvaluator" />, and the platform policy through the reader. Every test carries
///     a control that fails.
/// </summary>
public sealed class NifModelSquadRotationTests
{
    private const uint Linear = 1;
    private const uint Quadratic = 2;
    private const uint Tbc = 3;
    private const int Stride = NifModelCurveMapping.SquadStride;
    private const int Incoming = 0;
    private const int Key = 4;
    private const int Outgoing = 8;

    /// <summary>The bound on a pose component against the engine's exactly normalized sequence value (Shared normalizes in double).</summary>
    private const float SampleTolerance = 5e-7f;

    /// <summary>
    ///     The per-component quaternion image of Shared's Xbox 360 hardware-estimate term (H at most 0.028 degrees,
    ///     4.9e-4 rad, for outer blend angles up to 90 degrees): a rotation of angle theta moves a unit quaternion's
    ///     components by at most theta / 2.
    /// </summary>
    private const float X360EstimateTolerance = 2.5e-4f;

    /// <summary>
    ///     Fallout - Meshes.bsa meshes/characters/_male/sneak2hhattackspin.kf (SHA-256 57af6890...241c) block 85, keys 0
    ///     to 2 of 41 as stored: time, W, X, Y, Z, tension, continuity, bias. A_0 and B_1 depend on these three keys only.
    /// </summary>
    private static readonly uint[][] SneakKeys =
    [
        [0x00000000, 0xBF800000, 0x80000000, 0x80000000, 0x80000000, 0x00000000, 0xBF800000, 0x00000000],
        [0x3E088889, 0xBF3504F3, 0x3F3504F3, 0x80000000, 0x80000000, 0x00000000, 0xBF800000, 0x00000000],
        [0x3E888889, 0xB3B504F4, 0x3F800000, 0x80000000, 0x80000000, 0x00000000, 0xBF800000, 0x00000000]
    ];

    /// <summary>
    ///     Fallout - Meshes.bsa meshes/creatures/nvgiantrat/h2haim.kf (SHA-256 f0e9f2bf...a767) block 17, the whole
    ///     three-key group as stored (key 0 bias 0x3F7FFFFF, key 2 bias -1).
    /// </summary>
    private static readonly uint[][] RatKeys =
    [
        [0x00000000, 0x3F7455C9, 0xBB595853, 0xBA5818CB, 0x3E98C837, 0x00000000, 0x00000000, 0x3F7FFFFF],
        [0x3F99999A, 0x3F79A326, 0xBA0F6FF8, 0xBAFFE134, 0x3E62DF62, 0x00000000, 0x00000000, 0x00000000],
        [0x40000000, 0x3F7455C9, 0xBB595850, 0xBA5818D6, 0x3E98C838, 0x00000000, 0x00000000, 0xBF800000]
    ];

    /// <summary>
    ///     The inner points are bit-equal to the receipt: sneak A_0 (the outgoing point of key 0) and B_1 (the incoming
    ///     point of key 1), rat A_0, B_1 and B_last, in Shared's X, Y, Z, W layout at their triple offsets. Control: the
    ///     same formula with double intermediates differs in bits on A_0 of both groups.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InnerPoints_MatchTheRe24Receipt_BitForBit(bool bigEndian)
    {
        var sneak = Keyed(NifModelCurveMapping.MapRotation(Rotation(bigEndian, Tbc, SneakKeys), NifModelSquadPolicy.Pc));
        var rat = Keyed(NifModelCurveMapping.MapRotation(Rotation(bigEndian, Tbc, RatKeys), NifModelSquadPolicy.Pc));

        Assert.Equal(SceneInterpolation.GamebryoSquad, sneak.Interpolation);
        Assert.Equal(3 * Stride, sneak.Values.Length);
        AssertBits(sneak.Values, 0 * Stride + Outgoing, 0xBF6C835E, 0xBEC3EF16, 0x00000000, 0x00000000);
        AssertBits(sneak.Values, 1 * Stride + Incoming, 0xBF3504F3, 0x3F3504F3, 0x80000000, 0x00000000);
        AssertBits(sneak.Values, 0 * Stride + Key, 0xBF800000, 0x80000000, 0x80000000, 0x80000000);
        AssertBits(rat.Values, 0 * Stride + Outgoing, 0x3F7119AD, 0xBB99D23E, 0xB9874482, 0x3EAC1B60);
        AssertBits(rat.Values, 1 * Stride + Incoming, 0x3F7BB240, 0x3A5C29E0, 0xBB249F5B, 0x3E3AF49D);
        AssertBits(rat.Values, 2 * Stride + Incoming, 0x3F7119AD, 0xBB99D23C, 0xB98744A2, 0x3EAC1B62);

        Assert.NotEqual(Quad(sneak.Values, Outgoing), OutgoingOfFirstKeyInDouble(SneakKeys));
        Assert.NotEqual(Quad(rat.Values, Outgoing), OutgoingOfFirstKeyInDouble(RatKeys));
    }

    /// <summary>
    ///     Shared samples the mapped tracks to the engine's sequence output (RE-24 rule step 4) at the receipt's times,
    ///     within Float32 rounding of the final normalize: sneak at t = 0, 0x3D888889 and the key time 0x3E088889 (the
    ///     preceding segment at u = 1); rat at t = 0, 0.6, the key time 1.2, 1.44 and 1.8 (the second segment, which
    ///     also exercises Out[1]). Control: the same keys as LINEAR keys differ by more than 0.01 at sneak's interior time.
    /// </summary>
    [Fact]
    public void SampledPose_MatchesTheEngineSequenceOutput()
    {
        var sneak = Document(IdentityRest, Track(Keyed(
            NifModelCurveMapping.MapRotation(Rotation(false, Tbc, SneakKeys), NifModelSquadPolicy.Pc))));
        AssertPose(sneak, 0x00000000, 0xBF800000, 0x00000000, 0x00000000, 0x00000000);
        AssertPose(sneak, 0x3D888889, 0xBF74FA3D, 0x3E949EE4, 0x00000000, 0x00000000);
        AssertPose(sneak, 0x3E088889, 0xBF3504F3, 0x3F3504F3, 0x00000000, 0x00000000);

        var rat = Document(IdentityRest, Track(Keyed(
            NifModelCurveMapping.MapRotation(Rotation(false, Tbc, RatKeys), NifModelSquadPolicy.Pc))));
        AssertPose(rat, 0x00000000, 0x3F7455CA, 0xBB595853, 0xBA5818CC, 0x3E98C837);
        AssertPose(rat, 0x3F19999A, 0x3F772EAB, 0xBAFD67C8, 0xBAB61BC9, 0x3E853702);
        AssertPose(rat, 0x3F99999A, 0x3F79A326, 0xBA0F6FF8, 0xBAFFE134, 0x3E62DF62);
        AssertPose(rat, 0x3FB851EC, 0x3F789DB1, 0xBA96EBCA, 0xBADFC6C0, 0x3E74286C);
        AssertPose(rat, 0x3FE66666, 0x3F754726, 0xBB3CB369, 0xBA83805D, 0x3E929DE6);

        var linear = Document(IdentityRest, Track(Keyed(NifModelCurveMapping.MapRotation(
            Rotation(false, Linear, SneakKeys.Select(static key => key[..5]).ToArray()), NifModelSquadPolicy.Pc))));
        var linearPose = Quaternion.CreateFromRotationMatrix(NifModelAnimationTestSupport.SampleLocal(linear, F(0x3D888889)));
        var engine = Q(0xBF74FA3D, 0x3E949EE4, 0x00000000, 0x00000000);
        Assert.True(Distance(engine, linearPose) > 0.01f, $"LINEAR keys gave {linearPose}, the Squad engine {engine}.");
    }

    /// <summary>
    ///     RE-17 steps 1 and 2 run before the inner points: key 1 stored as the negated 90-degree rotation is flipped
    ///     (its key triple carries +0.707), the inner points are those of the aligned keys, and the pose at t = 0.5 is the
    ///     Squad of the aligned keys (33.75 degrees: bisectors of bisectors of a_0 at -45 and b_1 at 90). Control: the
    ///     same keys unaligned give different inner points and a pose more than 5 degrees away.
    /// </summary>
    [Fact]
    public void ChainAlignment_PrecedesTheInnerPoints()
    {
        var half = MathF.Sqrt(0.5f);
        uint[][] keys =
        [
            [Bits(0f), Bits(1f), Bits(0f), Bits(0f), Bits(0f), 0, 0, 0],
            [Bits(1f), Bits(-half), Bits(0f), Bits(0f), Bits(-half), 0, 0, 0],
            [Bits(2f), Bits(0f), Bits(0f), Bits(0f), Bits(1f), 0, 0, 0]
        ];

        var curve = Keyed(NifModelCurveMapping.MapRotation(Rotation(false, Tbc, keys), NifModelSquadPolicy.Pc));

        Assert.Equal(half, curve.Values[Stride + Key + 2]);
        Assert.Equal(half, curve.Values[Stride + Key + 3]);
        var aligned = SampleZAngleDegrees(Document(IdentityRest, Track(curve)), 0.5f);
        Assert.Equal(33.75d, aligned, 1e-2);

        var raw = keys.Select(static key => new NifModelSquadQuaternion(F(key[1]), F(key[2]), F(key[3]), F(key[4])))
            .ToArray();
        float[] times = [0f, 1f, 2f];
        float[] zeros = [0f, 0f, 0f];
        var (rawIncoming, rawOutgoing) = NifModelSquadInnerPoints.Tbc(times, raw, zeros, zeros, zeros);
        Assert.NotEqual(Quad(curve.Values, Outgoing), Quad(rawOutgoing[0]));
        Assert.NotEqual(Quad(curve.Values, Stride + Incoming), Quad(rawIncoming[1]));
        var rawValues = new float[3 * Stride];
        for (var key = 0; key < 3; key++)
        {
            rawIncoming[key].WriteXyzw(rawValues, key * Stride + Incoming);
            raw[key].WriteXyzw(rawValues, key * Stride + Key);
            rawOutgoing[key].WriteXyzw(rawValues, key * Stride + Outgoing);
        }

        var unaligned = new SceneTransformTrack(0, SceneTransformProperty.Rotation, times, rawValues,
            SceneInterpolation.GamebryoSquad, gamebryoSquadPolicy: SceneGamebryoSquadPolicy.PcFloat32);
        var unalignedAngle = SampleZAngleDegrees(Document(IdentityRest, unaligned), 0.5f);
        Assert.True(Math.Abs(unalignedAngle - aligned) > 5d, $"Unaligned keys gave {unalignedAngle} degrees against {aligned}.");
    }

    /// <summary>
    ///     A repeated key time is the engine's zero-length span (its inner point is NaN) and is refused as such, the raw
    ///     keys staying native. Controls: distinct times map, and a decreasing time is the ordinary key-time refusal.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroLengthSpan_IsBlocked(bool bigEndian)
    {
        uint[] first = [Bits(0f), Bits(1f), 0, 0, 0, 0, 0, 0];
        uint[] same = [Bits(0f), Bits(0.8f), Bits(0.6f), 0, 0, 0, 0, 0];
        uint[] later = [Bits(1f), Bits(0.8f), Bits(0.6f), 0, 0, 0, 0, 0];
        uint[] earlier = [Bits(-1f), Bits(0.8f), Bits(0.6f), 0, 0, 0, 0, 0];

        Assert.Equal(NifModelCurveBlock.SquadZeroLengthSpan,
            NifModelCurveMapping.MapRotation(Rotation(bigEndian, Tbc, [first, same]), NifModelSquadPolicy.Pc).Block);
        Assert.Equal(NifModelCurveBlock.SquadZeroLengthSpan,
            NifModelCurveMapping.MapRotation(Rotation(bigEndian, Quadratic, [first[..5], same[..5]]), NifModelSquadPolicy.Pc)
                .Block);
        Assert.Equal("zero-length span: the engine's inner point is NaN (RE-24)",
            NifModelAnimationReasons.Reason(NifModelCurveBlock.SquadZeroLengthSpan));

        Assert.False(NifModelCurveMapping.MapRotation(Rotation(bigEndian, Tbc, [first, later]), NifModelSquadPolicy.Pc)
            .IsBlocked);
        Assert.Equal(NifModelCurveBlock.InvalidKeyTimes,
            NifModelCurveMapping.MapRotation(Rotation(bigEndian, Tbc, [first, earlier]), NifModelSquadPolicy.Pc).Block);
    }

    /// <summary>
    ///     A QUADRATIC group's inner point s[i] serves as both the incoming and the outgoing point of every key, bit for
    ///     bit. Controls: the same keys as TBC with continuity 0.5 give different incoming and outgoing points at the
    ///     interior key, and s[0] is not the TBC zero-parameter point (RE-24: QUADRATIC is not TBC with T = C = B = 0).
    /// </summary>
    [Fact]
    public void Quadratic_UsesOneInnerPointTwice()
    {
        uint[][] keys =
        [
            [Bits(0f), Bits(1f), Bits(0f), Bits(0f), Bits(0f)],
            [Bits(0.7f), Bits(MathF.Cos(0.4f)), Bits(0f), Bits(0f), Bits(MathF.Sin(0.4f))],
            [Bits(2f), Bits(MathF.Cos(1.1f)), Bits(0f), Bits(MathF.Sin(1.1f)), Bits(0f)]
        ];

        var quadratic = Keyed(NifModelCurveMapping.MapRotation(Rotation(false, Quadratic, keys), NifModelSquadPolicy.Pc));

        Assert.Equal(Quadratic, quadratic.KeyType);
        for (var key = 0; key < 3; key++)
        {
            Assert.Equal(Quad(quadratic.Values, key * Stride + Incoming), Quad(quadratic.Values, key * Stride + Outgoing));
            Assert.NotEqual(Quad(quadratic.Values, key * Stride + Incoming), Quad(quadratic.Values, key * Stride + Key));
        }

        var continuity = Bits(0.5f);
        var tbc = Keyed(NifModelCurveMapping.MapRotation(Rotation(false, Tbc,
            keys.Select(key => new uint[] { key[0], key[1], key[2], key[3], key[4], 0, continuity, 0 }).ToArray()),
            NifModelSquadPolicy.Pc));
        Assert.NotEqual(Quad(tbc.Values, Stride + Incoming), Quad(tbc.Values, Stride + Outgoing));
        var zeroTbc = Keyed(NifModelCurveMapping.MapRotation(Rotation(false, Tbc,
            keys.Select(static key => new uint[] { key[0], key[1], key[2], key[3], key[4], 0, 0, 0 }).ToArray()),
            NifModelSquadPolicy.Pc));
        Assert.NotEqual(Quad(quadratic.Values, Outgoing), Quad(zeroTbc.Values, Outgoing));
        var document = Document(IdentityRest, Track(quadratic));
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        _ = NifModelAnimationTestSupport.SampleLocal(document, 0.35f);
    }

    /// <summary>
    ///     The policy follows the platform the read resolved (the way the cut-1a packed path does): a little-endian file
    ///     is PC (PcFloat32) and its document validates and samples to the receipt through the evaluator; a big-endian file
    ///     under the default (X360 assumed) selection is Xbox360Estimate, validates, and Shared's evaluator samples it at
    ///     the unit-normalization centre (Shared certifies the hardware estimate as a separate term), within that term of
    ///     the PC receipt; a big-endian file under the PS3 selection keeps
    ///     the rotation native with the RE-24 reason while translation and scale stay typed. Control: the PS3 option on a
    ///     little-endian file changes nothing, because the option names the console a big-endian file was shipped for.
    /// </summary>
    [Fact]
    public void SquadPolicy_FollowsThePlatform()
    {
        var (pcState, pcGraph) = ReadGraph(SquadFixture(false));
        var pc = NifModelAnimationReader.ReadNif(pcState, pcGraph, TestContext.Current.CancellationToken);
        var pcClip = Assert.Single(pc.Clips);
        var pcRotation = Assert.Single(pcClip.TransformTracks, static t => t.Property == SceneTransformProperty.Rotation);
        Assert.Equal(SceneInterpolation.GamebryoSquad, pcRotation.Interpolation);
        Assert.Equal(SceneGamebryoSquadPolicy.PcFloat32, pcRotation.GamebryoSquadPolicy);
        Assert.Null(pcRotation.TbcParameters);
        var pcSource = Assert.Single(Extras(pcClip)["tracks"]!.AsArray(),
            static entry => entry!["property"]!.GetValue<string>() == "rotation")!;
        Assert.Equal("PcFloat32", pcSource["squadPolicy"]!.GetValue<string>());
        Assert.Equal(3u, pcSource["squadKeyType"]!.GetValue<uint>());
        Assert.Equal(NifModelSquadPolicy.PcSource, pcSource["squadPolicySource"]!.GetValue<string>());
        var pcDocument = Assemble(pcGraph, pc.Clips);
        SceneValidation.ValidateStructure(pcDocument, TestContext.Current.CancellationToken);
        var pose = Quaternion.CreateFromRotationMatrix(NifModelAnimationReaderTestSupport.SampleLocal(pcDocument, 0, F(0x3F19999A), 1));
        AssertSameRotation(Q(0x3F772EAB, 0xBAFD67C8, 0xBAB61BC9, 0x3E853702), pose, SampleTolerance, F(0x3F19999A));

        var (x360State, x360Graph) = ReadGraph(SquadFixture(true));
        var x360 = NifModelAnimationReader.ReadNif(x360State, x360Graph, TestContext.Current.CancellationToken);
        var x360Rotation = Assert.Single(Assert.Single(x360.Clips).TransformTracks,
            static t => t.Property == SceneTransformProperty.Rotation);
        Assert.Equal(SceneGamebryoSquadPolicy.Xbox360Estimate, x360Rotation.GamebryoSquadPolicy);
        Assert.True(x360.Dispositions[4].IsTyped);
        var x360Document = Assemble(x360Graph, x360.Clips);
        SceneValidation.ValidateStructure(x360Document, TestContext.Current.CancellationToken);
        var x360Pose = Quaternion.CreateFromRotationMatrix(
            NifModelAnimationReaderTestSupport.SampleLocal(x360Document, 0, F(0x3F19999A), 1));
        AssertSameRotation(Q(0x3F772EAB, 0xBAFD67C8, 0xBAB61BC9, 0x3E853702), x360Pose, X360EstimateTolerance, F(0x3F19999A));

        var ps3 = NifPackedPlatformOption.Resolve(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BethesdaModelRegistration.PlatformOption] = NifPackedPlatformOption.Ps3Value
        });
        var (ps3State, ps3Graph) = ReadGraph(SquadFixture(true));
        var blocked = NifModelAnimationReader.ReadNif(ps3State, ps3Graph, ps3, TestContext.Current.CancellationToken);
        var ps3Clip = Assert.Single(blocked.Clips);
        Assert.DoesNotContain(ps3Clip.TransformTracks, static t => t.Property == SceneTransformProperty.Rotation);
        Assert.Equal(2, ps3Clip.TransformTracks.Count);
        var reason = NifModelAnimationReasons.Reason(NifModelCurveBlock.SquadPolicyPs3);
        Assert.Equal("no measured normalization policy for PS3 (RE-24 covers GECK and X360)", reason);
        foreach (var block in new[] { 4, 5 })
        {
            var decision = Assert.Single(DecisionsFor(blocked, block),
                static d => d.Property == SceneTransformProperty.Rotation);
            Assert.False(decision.IsTyped);
            Assert.Equal(reason, decision.Reason);
            Assert.Equal("squadPolicyPs3", decision.Code);
            Assert.True(blocked.Dispositions[block].IsTyped);
        }

        SceneValidation.ValidateStructure(Assemble(ps3Graph, blocked.Clips), TestContext.Current.CancellationToken);

        var (controlState, controlGraph) = ReadGraph(SquadFixture(false));
        var control = NifModelAnimationReader.ReadNif(controlState, controlGraph, ps3, TestContext.Current.CancellationToken);
        var controlRotation = Assert.Single(Assert.Single(control.Clips).TransformTracks,
            static t => t.Property == SceneTransformProperty.Rotation);
        Assert.Equal(SceneGamebryoSquadPolicy.PcFloat32, controlRotation.GamebryoSquadPolicy);
    }

    /// <summary>The curve of a result that must have mapped.</summary>
    private static NifModelCurve Keyed(NifModelCurveResult result)
    {
        Assert.Equal(NifModelCurveBlock.None, result.Block);
        return Assert.IsType<NifModelCurve>(result.Curve);
    }

    /// <summary>A keyed rotation track of node 0 for a mapped curve.</summary>
    private static SceneTransformTrack Track(NifModelCurve curve)
    {
        return new NifModelTransformChannel(SceneTransformProperty.Rotation, SceneAnimationChannelState.Keyed, null, curve,
            null).ToTrack(0);
    }

    /// <summary>A rotation part (Num Rotation Keys, Rotation Type, then each key's words) read back through the slice-1 reader.</summary>
    private static NifRotationKeysView Rotation(bool bigEndian, uint keyType, uint[][] keys)
    {
        var writer = new NifAnimationByteWriter(bigEndian).U32((uint)keys.Length).U32(keyType);
        foreach (var key in keys)
        {
            writer.Words(key);
        }

        var data = writer.ToArray();
        var pos = 0;
        Assert.True(NifKeyGroupReader.TryReadRotationView(data, ref pos, data.Length, bigEndian, Version20207,
            out var view));
        Assert.Equal(data.Length, pos);
        return view;
    }

    /// <summary>The float of a receipt's bits.</summary>
    private static float F(uint bits)
    {
        return BitConverter.UInt32BitsToSingle(bits);
    }

    /// <summary>A receipt quaternion (W, X, Y, Z bits) as Shared's X, Y, Z, W.</summary>
    private static Quaternion Q(uint w, uint x, uint y, uint z)
    {
        return new Quaternion(F(x), F(y), F(z), F(w));
    }

    /// <summary>The bits of four values at an offset, in Shared's X, Y, Z, W order.</summary>
    private static uint[] Quad(float[] values, int offset)
    {
        return [Bits(values[offset]), Bits(values[offset + 1]), Bits(values[offset + 2]), Bits(values[offset + 3])];
    }

    /// <summary>The bits of an engine-order quaternion, in Shared's X, Y, Z, W order.</summary>
    private static uint[] Quad(NifModelSquadQuaternion q)
    {
        return [Bits(q.X), Bits(q.Y), Bits(q.Z), Bits(q.W)];
    }

    /// <summary>Asserts a receipt quaternion (W, X, Y, Z bits) at an offset of the values, exactly.</summary>
    private static void AssertBits(float[] values, int offset, uint w, uint x, uint y, uint z)
    {
        Assert.Equal(new[] { x, y, z, w }, Quad(values, offset));
    }

    /// <summary>Asserts the evaluated pose at a receipt time equals the engine's sequence quaternion (W, X, Y, Z bits).</summary>
    private static void AssertPose(ModelDocument document, uint timeBits, uint w, uint x, uint y, uint z)
    {
        var actual = Quaternion.CreateFromRotationMatrix(NifModelAnimationTestSupport.SampleLocal(document, F(timeBits)));
        AssertSameRotation(Q(w, x, y, z), actual, SampleTolerance, F(timeBits));
    }

    /// <summary>Asserts two unit quaternions name one rotation within a per-component tolerance (q and -q are one rotation).</summary>
    private static void AssertSameRotation(Quaternion expected, Quaternion actual, float tolerance, float time)
    {
        Assert.True(Distance(expected, actual) <= tolerance,
            $"At t = {time}: expected {expected}, actual {actual}, difference {Distance(expected, actual)}.");
    }

    /// <summary>The largest per-component difference between two quaternions, sign-aligned.</summary>
    private static float Distance(Quaternion expected, Quaternion actual)
    {
        if (Quaternion.Dot(expected, actual) < 0f)
        {
            actual = -actual;
        }

        return MathF.Max(MathF.Max(MathF.Abs(expected.X - actual.X), MathF.Abs(expected.Y - actual.Y)),
            MathF.Max(MathF.Abs(expected.Z - actual.Z), MathF.Abs(expected.W - actual.W)));
    }

    /// <summary>
    ///     The control: A_0 = q0 Exp(0.5 (DD - L2)) of a TBC group with the RE-24 helpers evaluated in double and rounded
    ///     to float once at the end, over the stored keys 0 and 1 (which the chain alignment leaves unchanged on both
    ///     receipt groups). Returned in Shared's X, Y, Z, W bit order.
    /// </summary>
    private static uint[] OutgoingOfFirstKeyInDouble(uint[][] keys)
    {
        double[] q0 = [F(keys[0][1]), F(keys[0][2]), F(keys[0][3]), F(keys[0][4])];
        double[] q1 = [F(keys[1][1]), F(keys[1][2]), F(keys[1][3]), F(keys[1][4])];
        double t0 = F(keys[0][0]), t1 = F(keys[1][0]);
        double tension = F(keys[0][5]), continuity = F(keys[0][6]), bias = F(keys[0][7]);
        var l1 = LogD(MulD(ConjD(q0), q0));
        var l2 = LogD(MulD(ConjD(q0), q1));
        var inverse = 1d / (t1 - t0);
        var a = (t0 - t0) * inverse;
        var c1 = a * (1d - tension) * (1d + continuity) * (1d + bias);
        var c2 = a * (1d - tension) * (1d - continuity) * (1d - bias);
        var argument = new double[4];
        for (var k = 0; k < 4; k++)
        {
            argument[k] = 0.5d * (c2 * l2[k] + c1 * l1[k] - l2[k]);
        }

        var outgoing = MulD(q0, ExpD(argument));
        return [Bits((float)outgoing[1]), Bits((float)outgoing[2]), Bits((float)outgoing[3]), Bits((float)outgoing[0])];
    }

    private static double[] ConjD(double[] q)
    {
        return [q[0], -q[1], -q[2], -q[3]];
    }

    private static double[] MulD(double[] a, double[] b)
    {
        return
        [
            a[0] * b[0] - a[1] * b[1] - a[2] * b[2] - a[3] * b[3],
            a[0] * b[1] + a[1] * b[0] + a[2] * b[3] - a[3] * b[2],
            a[0] * b[2] + a[2] * b[0] + a[3] * b[1] - a[1] * b[3],
            a[0] * b[3] + a[3] * b[0] + a[1] * b[2] - a[2] * b[1]
        ];
    }

    private static double[] LogD(double[] q)
    {
        var angle = q[0] <= -1d ? Math.PI : q[0] >= 1d ? 0d : Math.Acos(q[0]);
        var sine = Math.Sin(angle);
        var factor = Math.Abs(sine) >= 0.001d ? angle / sine : 1d;
        return [0d, factor * q[1], factor * q[2], factor * q[3]];
    }

    private static double[] ExpD(double[] v)
    {
        var angle = Math.Sqrt(v[1] * v[1] + v[2] * v[2] + v[3] * v[3]);
        var sine = Math.Sin(angle);
        var factor = Math.Abs(sine) >= 0.001d ? sine / angle : 1d;
        return [Math.Cos(angle), factor * v[1], factor * v[2], factor * v[3]];
    }

    /// <summary>
    ///     Root (node 0, manager 2) with child Bone (node 1); manager 2 lists sequence 3 'Aim' (CLAMP over [0, 2]) driving
    ///     Bone through interpolator 4, whose NiTransformData 5 holds the h2haim.kf block 17 TBC rotation and two LINEAR
    ///     translation keys.
    /// </summary>
    private static byte[] SquadFixture(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString("Root");
        var bone = builder.AddString("Bone");
        var aim = builder.AddString("Aim");
        var type = builder.AddString(TransformController);
        Add(builder, 0, "NiNode", Node(root, [1], 2));
        Add(builder, 1, "NiNode", Node(bone, []));
        Add(builder, 2, "NiControllerManager", Manager(0, [3], -1));
        Add(builder, 3, "NiControllerSequence", Sequence(aim, [Controlled(4, bone, type)], manager: 2, stop: 2f));
        Add(builder, 4, "NiTransformInterpolator", TransformInterpolator(5));
        Add(builder, 5, "NiTransformData", TransformData(QuaternionRotation(Tbc, RatKeys), (0f, 0f, 0f, 0f), (2f, 1f, 2f, 3f)));
        return builder.Build();
    }

    /// <summary>A quaternion rotation part: Num Rotation Keys, Rotation Type, then each key's words in file order.</summary>
    private static Action<NifTestBlockWriter> QuaternionRotation(uint keyType, uint[][] keys)
    {
        return w =>
        {
            w.U32((uint)keys.Length).U32(keyType);
            foreach (var key in keys)
            {
                foreach (var word in key)
                {
                    w.U32(word);
                }
            }
        };
    }
}
