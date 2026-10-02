using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Presentation;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The reader's billboard encodings, evaluated through Shared's ideal <see cref="SceneBillboardEvaluator" />,
///     reproduce the engine: RE-25's verifier emulated the GECK's RotateToCamera on random scenes
///     (<c>re25_contract_vectors.json</c>, 18 vectors, three per effective mode 0-5) and on the plane-fallback cone and
///     unfaced ball (<c>re25_rev2_fallback_vectors.json</c>); the FNV PC runtime is bit-identical to the GECK on every
///     scene (RE-25 runtime re-check). Both fixtures are the design's receipts, copied byte for byte into
///     <c>tests/BethesdaMultitool.Tests/Resources/Billboards/</c> and pinned by SHA-256.
/// </summary>
/// <remarks>
///     <para>
///         Conventions (the fixtures' own): 3x3 matrices are column-vector matrices listed by rows (the NIF Matrix33
///         order), W0 = P L, the camera C has columns f (view), u (up) and r (right), the engine's world translation is
///         <c>engineP</c> and the camera position <c>Ct</c>. The document is row-vector, so its pre-facing world has
///         rows s W0[:, k] and translation <c>engineP</c>, and an output row k is compared with <c>engineW[:, k]</c>.
///     </para>
///     <para>
///         Tolerance: 1.5e-6 on every matrix element, the element metric RE-25 established (its verifier: 1e-6 on every
///         mode but 3 and 4, which reach 1.48e-6 on random scenes through float32 conditioning). Measured before staging
///         by a line-for-line double replica of the evaluator (the implementation report's precheck): at most 1.9e-7 on
///         the 18 vectors and 4.8e-7 on the fallback cells used here.
///     </para>
/// </remarks>
public class NifModelBillboardEngineVectorTests
{
    /// <summary>The per-element tolerance RE-25 established (see the type remarks).</summary>
    private const double ElementTolerance = 1.5e-6;

    /// <summary>The tolerance of the end-to-end check through the reader's own TRS world (DESIGN.md 3.2: 1e-5).</summary>
    private const double EndToEndTolerance = 1e-5;

    /// <summary>
    ///     SHA-256 of the contract vectors with CRLF line ends normalized to LF (git's line-end conversion may check the
    ///     file out with either line end; <see cref="ContractVectorsCrlfSha256" /> is the receipt as written).
    /// </summary>
    private const string ContractVectorsSha256 = "3e6afa44814a31e0e7d8117d8cd2b3b463df6ec140ed8e1990a047db5730d02a";

    /// <summary>SHA-256 of the contract vectors as the design wrote them (CRLF line ends).</summary>
    private const string ContractVectorsCrlfSha256 = "fc2192ddc164c0209e14ffecc501101a88ab7a223b43b3f4e708308d1ad92fc8";

    /// <summary>SHA-256 of the fallback vectors (one line, no line ends to normalize).</summary>
    private const string FallbackVectorsSha256 = "0adb23e739721ce3e13c171ccb2aba864316ab8e8db36e563ed7e32520c3bbbd";

    /// <summary>The contract vectors' file name under Resources/Billboards.</summary>
    private const string ContractVectorsFile = "re25_contract_vectors.json";

    /// <summary>The fallback vectors' file name under Resources/Billboards.</summary>
    private const string FallbackVectorsFile = "re25_rev2_fallback_vectors.json";

    /// <summary>The 18 contract vectors, read once per test run.</summary>
    private static readonly Lazy<IReadOnlyList<EngineScene>> LazyContractVectors = new(LoadContractVectors);

    /// <summary>The 804 fallback records (cone, ball, and nested placeholders), read once per test run.</summary>
    private static readonly Lazy<IReadOnlyList<EngineScene>> LazyFallbackCells = new(LoadFallbackCells);

    /// <summary>The stored values that face (effective modes 0-5 and their bit-3 twins).</summary>
    public static TheoryData<int> FacingStoredValues => new() { 0, 1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 13 };

    /// <summary>
    ///     Both fixtures are the design's receipts. Control: the hash of the contract vectors as read without line-end
    ///     normalization matches either the LF or the CRLF receipt, never anything else.
    /// </summary>
    [Fact]
    public void Fixtures_AreTheDesignReceipts()
    {
        var contract = File.ReadAllBytes(FixturePath(ContractVectorsFile));
        var fallback = File.ReadAllBytes(FixturePath(FallbackVectorsFile));

        Assert.Equal(ContractVectorsSha256, NormalizedSha256(contract));
        Assert.Equal(FallbackVectorsSha256, NormalizedSha256(fallback));
        var raw = Sha256(contract);
        Assert.True(raw is ContractVectorsSha256 or ContractVectorsCrlfSha256, $"contract vectors hash {raw}");
        Assert.Equal(18, LazyContractVectors.Value.Count);
        Assert.Equal(804, LazyFallbackCells.Value.Count);
    }

    /// <summary>
    ///     Per stored value 0-5 and 8-13, the billboard the reader emits (FNV PC read) reproduces the three engine vectors
    ///     of its effective mode within 1.5e-6 per element, with the engine's handedness (mode 5 mirrored). Values 8-13
    ///     run on the 0-5 vectors: the engine's output for w and w | 8 is bit-identical (RE-25 verifier 3c, 2,400 of 2,400
    ///     scenes; runtime re-check, 300 of 300 per pair). Values 6, 7, 14 and 15 give no billboard
    ///     (<see cref="NifModelBillboardTests" />).
    /// </summary>
    [Theory]
    [MemberData(nameof(FacingStoredValues))]
    public void ReaderEncoding_ReproducesTheEngineVectors(int storedValue)
    {
        var billboard = ReadBillboard(storedValue);
        var effective = storedValue & 7;
        var vectors = LazyContractVectors.Value.Where(v => v.EffectiveMode == effective).ToList();

        Assert.Equal((ulong)storedValue, billboard.RawMode);
        Assert.Equal(3, vectors.Count);
        foreach (var vector in vectors)
        {
            var faced = SceneBillboardEvaluator.Evaluate(billboard, vector.PreFacing(), vector.Camera());
            var error = ElementError(faced, vector);
            Assert.True(error <= ElementTolerance,
                $"stored value {storedValue}, trial {vector.Trial}: element error {error:E3} > {ElementTolerance:E1}");
            Assert.Equal(effective == 5 ? -1 : 1, Math.Sign(faced.GetDeterminant()));
        }
    }

    /// <summary>
    ///     End to end through the reader's own transforms: each vector written as a NIF (root = P, Pt, Ps; billboard = L,
    ///     Lt, Ls, the rows in file order) reads to a world equal to the vector's s W0 and engine translation, and the
    ///     reader's billboard faced on that world matches the engine within 1e-5 per element (measured by an emulation of
    ///     the TRS path before staging: 1.0e-6). Controls: the transposed reading misses the world by more than 1e-2
    ///     (measured 0.025 at the least), and the wrong composition order (L P) by more than 0.1 (measured 0.495).
    /// </summary>
    [Fact]
    public void DocumentWorld_ReadsTheVectorNotation_AndFacesLikeTheEngine()
    {
        foreach (var vector in LazyContractVectors.Value)
        {
            var document = Read(VectorFixture(vector), NifModelBillboardTests.FnvOptions(false)).Document;
            var billboard = Assert.IsType<SceneBillboard>(document.Nodes[1].Billboard);
            var world = Compose(document.Nodes[1].LocalTransform, document.Nodes[0].LocalTransform);
            var w0 = Multiply(vector.P, vector.L);
            var wrongOrder = Multiply(vector.L, vector.P);
            double rotation = 0, transposed = 0, reordered = 0;
            for (var k = 0; k < 3; k++)
            {
                for (var i = 0; i < 3; i++)
                {
                    var element = world[k, i] / vector.Scale;
                    rotation = Math.Max(rotation, Math.Abs(element - w0[i, k]));
                    transposed = Math.Max(transposed, Math.Abs(element - w0[k, i]));
                    reordered = Math.Max(reordered, Math.Abs(element - wrongOrder[i, k]));
                }
            }

            var translation = Math.Max(Math.Abs(world[3, 0] - vector.EngineP[0]),
                Math.Max(Math.Abs(world[3, 1] - vector.EngineP[1]), Math.Abs(world[3, 2] - vector.EngineP[2])));
            Assert.True(rotation <= 2e-6, $"trial {vector.Trial}: world rotation off by {rotation:E3}");
            Assert.True(translation <= 1e-4, $"trial {vector.Trial}: world translation off by {translation:E3}");
            Assert.True(transposed > 1e-2, $"trial {vector.Trial}: the transposed reading matches ({transposed:E3})");
            Assert.True(reordered > 0.1, $"trial {vector.Trial}: the reordered product matches ({reordered:E3})");

            var faced = SceneBillboardEvaluator.Evaluate(billboard, ToMatrix(world), vector.Camera());
            var error = ElementError(faced, vector);
            Assert.True(error <= EndToEndTolerance, $"trial {vector.Trial}: end-to-end element error {error:E3}");
        }
    }

    /// <summary>
    ///     The plane-fallback cone and the unfaced ball, where the evaluator implements them, for stored values 11 and 12
    ///     (the fixture's file values, modes 3 and 4 with bit 3):
    ///     <list type="bullet">
    ///         <item>
    ///             cone cells with theta at most 1.0e-3 rad (inside the runtime's skip band, 99.9% skipped below 1.37e-3,
    ///             and below the camera-norm law's lower edge 1.07e-3), for mode 4 at every phi and for mode 3 at phi of
    ///             20 degrees or more, the design's selection (the Gram-Schmidt conditioning stays below 3; phi 5 degrees
    ///             also passes, at 6.8e-7, while at 1 and 0.2 degrees the engine's own float32 noise reaches 2.9e-6 and
    ///             9.4e-6, above the tolerance);
    ///         </item>
    ///         <item>every ball cell: the evaluator returns the input unchanged exactly when |c - p|^2 is below float32(0.001).</item>
    ///     </list>
    ///     The shell cells (theta 1.35e-3 to 2e-3), where the engine's float32 compare and near-axis tilt differ from
    ///     ideal geometry by up to 0.3 per element, are excluded: they are the writer's approximation rows, not this
    ///     contract.
    ///     Controls: the selection has exactly 54, 108 and 56 cells, every selected cone cell satisfies the declared
    ///     cosine, and the ball splits 24 unfaced / 32 faced.
    /// </summary>
    [Fact]
    public void ReaderEncoding_ReproducesTheConeAndBallCells()
    {
        var billboards = new Dictionary<int, SceneBillboard> { [11] = ReadBillboard(11), [12] = ReadBillboard(12) };
        var cone3 = LazyFallbackCells.Value.Where(c => c is { Kind: "cone", EffectiveMode: 3, ThetaTarget: <= 1.0e-3, PhiDeg: >= 20 })
            .ToList();
        var cone4 = LazyFallbackCells.Value.Where(c => c is { Kind: "cone", EffectiveMode: 4, ThetaTarget: <= 1.0e-3 })
            .ToList();
        var ball = LazyFallbackCells.Value.Where(c => c.Kind == "ball").ToList();

        Assert.Equal(54, cone3.Count);
        Assert.Equal(108, cone4.Count);
        Assert.Equal(56, ball.Count);
        var worst = 0.0;
        var unfaced = 0;
        foreach (var cell in cone3.Concat(cone4).Concat(ball))
        {
            Assert.Equal(cell.EffectiveMode | 8, cell.FileValue);
            var billboard = billboards[cell.FileValue];
            var preFacing = cell.PreFacing();
            var camera = cell.Camera();
            var faced = SceneBillboardEvaluator.Evaluate(billboard, preFacing, camera);
            var error = ElementError(faced, cell);
            worst = Math.Max(worst, error);
            Assert.True(error <= ElementTolerance,
                $"{cell.Kind} mode {cell.EffectiveMode} phi {cell.PhiDeg} theta {cell.ThetaTarget} distance " +
                $"{cell.Distance}: element error {error:E3}");
            var delta = Subtract(cell.Ct, cell.EngineP);
            if (cell.Kind == "cone")
            {
                var cosine = -Dot(Column(cell.C, 0), delta) / Math.Sqrt(Dot(delta, delta));
                Assert.True(cosine >= billboard.PlaneFallbackCosine!.Value, $"cone cell outside the cone: {cosine:R}");
            }
            else if (Dot(delta, delta) < billboard.UnfacedDistanceSquared!.Value)
            {
                unfaced++;
                Assert.Equal(preFacing, faced);
            }
            else
            {
                Assert.NotEqual(preFacing, faced);
            }
        }

        Assert.Equal(24, unfaced);
        Assert.True(worst <= ElementTolerance, $"worst {worst:E3}");
    }

    /// <summary>
    ///     CONTROL: the mapping the worktree shipped before this change fails every vector.
    ///     <list type="bullet">
    ///         <item>
    ///             As built: its encodings (a verbatim copy of the retired <c>Map</c>) leave Front, and for modes 1 and 5
    ///             Rigid, unresolved, so the evaluator refuses every one and the writer builds no facing; the card keeps
    ///             W0, which misses every vector by at least 0.937 rad (the design measured 0.938).
    ///         </item>
    ///         <item>
    ///             Completed as favorably as its null fields allow (receipt E of contract_check.py): minimum arc of +Z for
    ///             modes 0 and 3, a lock about the node's own +Z with the best of +-X and +-Y tracked for modes 1 and 5,
    ///             TRACK_TO with world or camera up for mode 4, no facing for mode 2. It misses every vector by at least
    ///             1.081 rad (the design measured 1.082), and each vector's miss agrees with the receipt's
    ///             <c>currentMappingBestAxisErrorRad</c> within 1e-6.
    ///         </item>
    ///     </list>
    /// </summary>
    [Fact]
    public void Control_TheRetiredMappingMissesEveryVector()
    {
        var asBuilt = double.MaxValue;
        var favorable = double.MaxValue;
        foreach (var vector in LazyContractVectors.Value)
        {
            var preFacing = vector.PreFacing();
            var camera = vector.Camera();
            var worldUp = Vector3.TransformNormal(Vector3.UnitZ, preFacing);
            var retired = RetiredMap((ulong)vector.EffectiveMode, worldUp);
            Assert.Throws<NotSupportedException>(() =>
            {
                _ = SceneBillboardEvaluator.Evaluate(retired, preFacing, camera);
            });

            var keepsW0 = AxisError(Rows(preFacing), vector.EngineW);
            asBuilt = Math.Min(asBuilt, keepsW0);
            var best = FavorableCompletions(vector, preFacing, camera).Min(rows => AxisError(rows, vector.EngineW));
            favorable = Math.Min(favorable, best);
            Assert.True(Math.Abs(best - vector.CurrentMappingBest) <= 1e-6,
                $"trial {vector.Trial} mode {vector.EffectiveMode}: {best:R} vs receipt {vector.CurrentMappingBest:R}");
        }

        Assert.True(asBuilt >= 0.937, $"as built, the smallest miss is {asBuilt:R} rad");
        Assert.True(favorable >= 1.081, $"favorably completed, the smallest miss is {favorable:R} rad");
    }

    /// <summary>
    ///     CONTROL: every field of every encoding matters. Each single-field mutation of the reader's encoding misses every
    ///     vector of its mode by more than 1e-2 rad (DESIGN-rev4 5.2 item 4; smallest measured: mode 3 with CameraSwung,
    ///     0.121 rad; mode 1 aimed at the camera plane, 0.139 rad). A mutation the contract refuses (for example a Node
    ///     lock parallel to the front) is replaced by its nearest legal form, named in the case.
    /// </summary>
    [Fact]
    public void Control_EverySingleFieldMutationMissesItsVectors()
    {
        var smallest = double.MaxValue;
        var smallestName = "";
        foreach (var (mode, name, mutated) in Mutations())
        {
            foreach (var vector in LazyContractVectors.Value.Where(v => v.EffectiveMode == mode))
            {
                var faced = SceneBillboardEvaluator.Evaluate(mutated, vector.PreFacing(), vector.Camera());
                var miss = AxisError(Rows(faced), vector.EngineW);
                Assert.True(miss > 1e-2, $"mode {mode} {name}, trial {vector.Trial}: misses by only {miss:R} rad");
                if (miss < smallest)
                {
                    (smallest, smallestName) = (miss, $"mode {mode} {name}");
                }
            }
        }

        Assert.True(smallest > 0.1, $"smallest mutation miss {smallest:R} rad ({smallestName})");
    }

    /// <summary>
    ///     The gap a negative world scale leaves when only the matrix is seen (review F1; VERIFY-rev4-math W7), which
    ///     the reader declares as <see cref="SceneBillboard.SourceScale" />, reports with
    ///     <see cref="BethesdaMultitool.Core.Modeling.Nif.NifModelBillboardScaleSign" /> and both writers classify as
    ///     Degraded (<see cref="NifModelBillboardSourceScaleTests" />): each vector's scene with its world scale
    ///     negated. The engine keeps the negative scalar through the facing (RE-25 item 1) and draws s F, with F its
    ///     faced rotation (for mode 1, RE-25's formula divides the camera offset by s, so F is the vector's rotation
    ///     turned a further half turn about its +Y); the evaluator sees only the matrix, keeps |s| and lets the
    ///     encoding set the handedness. For effective modes 0 and 2 to 5 the reader's encoding therefore misses the
    ///     engine by more than 1 per element over |s| (measured by the replica before staging: at least 1.356, mode 0;
    ///     mode 5 exactly 2, the point reflection); for mode 1 it matches within the element tolerance (measured
    ///     1.3e-7). RE-25 did not exercise a negative scale, so the mode-1 agreement is the formula's, and the reader
    ///     declares and reports mode 1 as well. Mode 5's formula also divides the camera offset by s (the foundation's
    ///     review of Shared 853b6f1), so the mode-5 miss is measured against this replica, not an engine oracle for a
    ///     negative scale. Control: the same vectors at their positive scale match the engine
    ///     (<see cref="ReaderEncoding_ReproducesTheEngineVectors" />), so the miss is the sign alone.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void NegativeWorldScale_IsTheGapTheReaderReports(int effectiveMode)
    {
        var billboard = ReadBillboard(effectiveMode);
        // Mode 1's engine rotation is the vector's turned a half turn about +Y: its X and Z columns change sign.
        var flip = effectiveMode == 1 ? -1.0 : 1.0;
        double[] turn = [flip, 1.0, flip];
        foreach (var vector in LazyContractVectors.Value.Where(v => v.EffectiveMode == effectiveMode))
        {
            var p = vector.PreFacing();
            var negated = new Matrix4x4(
                -p.M11, -p.M12, -p.M13, 0f,
                -p.M21, -p.M22, -p.M23, 0f,
                -p.M31, -p.M32, -p.M33, 0f,
                p.M41, p.M42, p.M43, 1f);
            var rows = Rows(SceneBillboardEvaluator.Evaluate(billboard, negated, vector.Camera()));
            var error = 0.0;
            for (var k = 0; k < 3; k++)
            {
                for (var i = 0; i < 3; i++)
                {
                    // The engine's row k is -s F[:, k]; divided by |s| = s it is -F[i, k].
                    error = Math.Max(error, Math.Abs(rows[k][i] / vector.Scale + vector.EngineW[i, k] * turn[k]));
                }
            }

            if (effectiveMode == 1)
            {
                Assert.True(error <= ElementTolerance, $"trial {vector.Trial}: mode 1 misses by {error:E3}");
            }
            else
            {
                Assert.True(error > 1.0, $"trial {vector.Trial}, mode {effectiveMode}: misses by only {error:R}");
            }
        }
    }

    /// <summary>Reads the reader's billboard for one stored value from an FNV PC fixture.</summary>
    private static SceneBillboard ReadBillboard(int storedValue)
    {
        var document = Read(NifModelBillboardTests.Fixture((ushort)storedValue), NifModelBillboardTests.FnvOptions(false))
            .Document;
        return Assert.IsType<SceneBillboard>(document.Nodes[1].Billboard);
    }

    /// <summary>A NIF holding one vector's scene: root (P, Pt, Ps) [1]; NiBillboardNode (L, Lt, Ls) of its mode.</summary>
    private static byte[] VectorFixture(EngineScene vector)
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1], Triple(vector.Pt), RowsOf(vector.P), (float)vector.Ps);
        var name = builder.AddString("Billboard");
        builder.AddBlock("NiBillboardNode", w => NifTestBlockLayouts.NodeWithTail(w, 34, name, [],
            tail => NifTestBlockLayouts.BillboardTail(tail, (ushort)vector.EffectiveMode), translation: Triple(vector.Lt),
            rotation: RowsOf(vector.L), scale: (float)vector.Ls));
        return builder.Build();
    }

    /// <summary>A verbatim copy of the retired NifModelBillboards.Map (worktree before this change).</summary>
    private static SceneBillboard RetiredMap(ulong rawMode, Vector3 worldUp)
    {
        var zero = Vector3.Zero;
        return rawMode switch
        {
            0 => new SceneBillboard(SceneBillboardAim.CameraPlane, null, false, zero, zero, rawMode),
            2 => new SceneBillboard(SceneBillboardAim.CameraPlane, null, true, zero, zero, rawMode),
            3 => new SceneBillboard(SceneBillboardAim.CameraPosition, null, false, zero, zero, rawMode),
            4 => new SceneBillboard(SceneBillboardAim.CameraPosition, null, true, zero, zero, rawMode),
            1 or 5 or 9 => new SceneBillboard(SceneBillboardAim.CameraPosition, worldUp, null, zero, zero, rawMode),
            _ => new SceneBillboard(null, null, null, zero, zero, rawMode)
        };
    }

    /// <summary>The retired mapping's most favorable completions (receipt E), as output rows.</summary>
    private static IEnumerable<double[][]> FavorableCompletions(EngineScene vector, Matrix4x4 preFacing, CameraPose camera)
    {
        var zero = Vector3.Zero;
        var raw = (ulong)vector.EffectiveMode;
        switch (vector.EffectiveMode)
        {
            case 0:
                yield return Rows(SceneBillboardEvaluator.Evaluate(new SceneBillboard(SceneBillboardAim.CameraPlane, null,
                    false, zero, zero, raw, Vector3.UnitZ), preFacing, camera));
                break;
            case 3:
                yield return Rows(SceneBillboardEvaluator.Evaluate(new SceneBillboard(SceneBillboardAim.CameraPosition,
                    null, false, zero, zero, raw, Vector3.UnitZ), preFacing, camera));
                break;
            case 1 or 5:
                foreach (var front in new[] { Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, -Vector3.UnitX })
                {
                    yield return Rows(SceneBillboardEvaluator.Evaluate(new SceneBillboard(
                        SceneBillboardAim.CameraPosition, Vector3.UnitZ, false, zero, zero, raw, front, null, null,
                        SceneBillboardAxisFrame.Node), preFacing, camera));
                }

                break;
            case 4:
                var t = Unit(Subtract(vector.Ct, vector.EngineP));
                foreach (var up in new[] { new[] { 0.0, 0.0, 1.0 }, Column(vector.C, 1) })
                {
                    var y = Unit(Subtract(up, Scale(t, Dot(up, t))));
                    yield return [Cross(y, t), y, t];
                }

                break;
            default:
                yield return Rows(preFacing);
                break;
        }
    }

    /// <summary>The single-field mutations of each mode's encoding (DESIGN-rev4 5.2 item 4).</summary>
    private static IEnumerable<(int Mode, string Name, SceneBillboard Billboard)> Mutations()
    {
        var zero = Vector3.Zero;
        var x = Vector3.UnitX;
        var y = Vector3.UnitY;
        var z = Vector3.UnitZ;
        const SceneBillboardAim plane = SceneBillboardAim.CameraPlane;
        const SceneBillboardAim position = SceneBillboardAim.CameraPosition;
        const double cosine = 0.9999989569187164;
        const double distance = 0.0010000000474974513;
        yield return (0, "roll Camera", new SceneBillboard(plane, null, true, zero, zero, 0, z, y,
            SceneBillboardRoll.Camera, lockedAxisFrame: null));
        yield return (0, "aim CameraPosition", new SceneBillboard(position, null, true, zero, zero, 0, z, y,
            SceneBillboardRoll.NodeUp, lockedAxisFrame: null));
        yield return (1, "aim CameraPlane", new SceneBillboard(plane, y, false, zero, zero, 1, z, y, null,
            SceneBillboardAxisFrame.Node));
        yield return (1, "frame Document (so Rigid true)", new SceneBillboard(position, y, true, zero, zero, 1, z, y,
            null, SceneBillboardAxisFrame.Document));
        yield return (1, "lock +Z (so front +Y, up unset)", new SceneBillboard(position, z, false, zero, zero, 1, y,
            null, null, SceneBillboardAxisFrame.Node));
        yield return (2, "roll NodeUp", new SceneBillboard(plane, null, true, zero, zero, 2, z, y,
            SceneBillboardRoll.NodeUp, lockedAxisFrame: null));
        yield return (2, "aim CameraPosition with CameraSwung", new SceneBillboard(position, null, true, zero, zero, 2,
            z, y, SceneBillboardRoll.CameraSwung, lockedAxisFrame: null));
        yield return (3, "roll CameraSwung", new SceneBillboard(position, null, true, zero, zero, 3, z, y,
            SceneBillboardRoll.CameraSwung, lockedAxisFrame: null, planeFallbackCosine: cosine,
            unfacedDistanceSquared: distance));
        yield return (3, "aim CameraPlane (so no thresholds)", new SceneBillboard(plane, null, true, zero, zero, 3, z,
            y, SceneBillboardRoll.NodeUp, lockedAxisFrame: null));
        yield return (3, "Rigid false (so no roll)", new SceneBillboard(position, null, false, zero, zero, 3, z, y,
            null, lockedAxisFrame: null, planeFallbackCosine: cosine, unfacedDistanceSquared: distance));
        yield return (4, "roll NodeUp", new SceneBillboard(position, null, true, zero, zero, 4, z, y,
            SceneBillboardRoll.NodeUp, lockedAxisFrame: null, planeFallbackCosine: cosine,
            unfacedDistanceSquared: distance));
        yield return (4, "aim CameraPlane with roll Camera (so no thresholds)", new SceneBillboard(plane, null, true,
            zero, zero, 4, z, y, SceneBillboardRoll.Camera, lockedAxisFrame: null));
        yield return (5, "no reflection", new SceneBillboard(position, z, true, zero, zero, 5, y, z, null,
            SceneBillboardAxisFrame.Document));
        yield return (5, "reflection +Y", new SceneBillboard(position, z, true, zero, zero, 5, y, z, null,
            SceneBillboardAxisFrame.Document, reflection: y));
        yield return (5, "frame Node (so Rigid false)", new SceneBillboard(position, z, false, zero, zero, 5, y, z,
            null, SceneBillboardAxisFrame.Node, reflection: x));
        yield return (5, "front and up swapped", new SceneBillboard(position, z, true, zero, zero, 5, z, y, null,
            SceneBillboardAxisFrame.Document, reflection: x));
    }

    /// <summary>The largest element difference between output rows (divided by the scene's scale) and engine columns.</summary>
    private static double ElementError(Matrix4x4 faced, EngineScene scene)
    {
        var rows = Rows(faced);
        var error = 0.0;
        for (var k = 0; k < 3; k++)
        {
            for (var i = 0; i < 3; i++)
            {
                error = Math.Max(error, Math.Abs(rows[k][i] / scene.Scale - scene.EngineW[i, k]));
            }
        }

        return error;
    }

    /// <summary>The largest angle between corresponding output rows and engine columns, in radians.</summary>
    private static double AxisError(double[][] rows, double[,] engineW)
    {
        var error = 0.0;
        for (var k = 0; k < 3; k++)
        {
            var cosine = Dot(Unit(rows[k]), Unit(Column(engineW, k)));
            error = Math.Max(error, Math.Acos(Math.Clamp(cosine, -1, 1)));
        }

        return error;
    }

    /// <summary>The three linear rows of a row-vector matrix, in double.</summary>
    private static double[][] Rows(Matrix4x4 m)
    {
        return
        [
            [m.M11, m.M12, m.M13],
            [m.M21, m.M22, m.M23],
            [m.M31, m.M32, m.M33]
        ];
    }

    /// <summary>The row-vector composition <c>child x parent</c> of two float32 matrices, in double.</summary>
    private static double[,] Compose(Matrix4x4 child, Matrix4x4 parent)
    {
        var a = Elements(child);
        var b = Elements(parent);
        var result = new double[4, 4];
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                for (var k = 0; k < 4; k++)
                {
                    result[i, j] += a[i, k] * b[k, j];
                }
            }
        }

        return result;
    }

    /// <summary>A double 4x4 rounded once to a float32 matrix.</summary>
    private static Matrix4x4 ToMatrix(double[,] m)
    {
        return new Matrix4x4(
            (float)m[0, 0], (float)m[0, 1], (float)m[0, 2], (float)m[0, 3],
            (float)m[1, 0], (float)m[1, 1], (float)m[1, 2], (float)m[1, 3],
            (float)m[2, 0], (float)m[2, 1], (float)m[2, 2], (float)m[2, 3],
            (float)m[3, 0], (float)m[3, 1], (float)m[3, 2], (float)m[3, 3]);
    }

    /// <summary>The elements of a float32 matrix, in double.</summary>
    private static double[,] Elements(Matrix4x4 m)
    {
        return new double[,]
        {
            { m.M11, m.M12, m.M13, m.M14 },
            { m.M21, m.M22, m.M23, m.M24 },
            { m.M31, m.M32, m.M33, m.M34 },
            { m.M41, m.M42, m.M43, m.M44 }
        };
    }

    /// <summary>The 3x3 product a b of two column-vector matrices.</summary>
    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var result = new double[3, 3];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                result[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
            }
        }

        return result;
    }

    /// <summary>Column k of a 3x3 matrix.</summary>
    private static double[] Column(double[,] m, int k)
    {
        return [m[0, k], m[1, k], m[2, k]];
    }

    /// <summary>The dot product of two 3-vectors.</summary>
    private static double Dot(double[] a, double[] b)
    {
        return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    }

    /// <summary>The cross product a x b.</summary>
    private static double[] Cross(double[] a, double[] b)
    {
        return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    }

    /// <summary>The difference a - b.</summary>
    private static double[] Subtract(double[] a, double[] b)
    {
        return [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    }

    /// <summary>The vector a times the scalar s.</summary>
    private static double[] Scale(double[] a, double s)
    {
        return [a[0] * s, a[1] * s, a[2] * s];
    }

    /// <summary>The vector a divided by its length.</summary>
    private static double[] Unit(double[] a)
    {
        return Scale(a, 1 / Math.Sqrt(Dot(a, a)));
    }

    /// <summary>A fixture triple as the NIF writer's translation argument (the values are float32-exact).</summary>
    private static (float X, float Y, float Z) Triple(double[] v)
    {
        return ((float)v[0], (float)v[1], (float)v[2]);
    }

    /// <summary>A fixture matrix as the nine Matrix33 floats in file order (row by row).</summary>
    private static float[] RowsOf(double[,] m)
    {
        return
        [
            (float)m[0, 0], (float)m[0, 1], (float)m[0, 2],
            (float)m[1, 0], (float)m[1, 1], (float)m[1, 2],
            (float)m[2, 0], (float)m[2, 1], (float)m[2, 2]
        ];
    }

    /// <summary>The SHA-256 of the bytes, lowercase hex.</summary>
    private static string Sha256(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>The SHA-256 of the text with CRLF normalized to LF, lowercase hex.</summary>
    private static string NormalizedSha256(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        return Sha256(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>A fixture under Resources/Billboards, found from the repository root.</summary>
    private static string FixturePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BethesdaMultitool.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "tests", "BethesdaMultitool.Tests", "Resources", "Billboards", name);
        Assert.True(File.Exists(path), $"fixture missing: {path}");
        return path;
    }

    /// <summary>Reads every record of the contract vectors fixture.</summary>
    private static IReadOnlyList<EngineScene> LoadContractVectors()
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(FixturePath(ContractVectorsFile)));
        return json.RootElement.GetProperty("vectors").EnumerateArray().Select(v => EngineScene.From(v, v, "contract"))
            .ToList();
    }

    /// <summary>Reads every fallback record; a nested record becomes <see cref="EngineScene.Nested" />.</summary>
    private static IReadOnlyList<EngineScene> LoadFallbackCells()
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(FixturePath(FallbackVectorsFile)));
        return json.RootElement.GetProperty("vectors").EnumerateArray()
            .Select(v => v.GetProperty("kind").GetString() == "nested"
                ? EngineScene.Nested
                : EngineScene.From(v, v.GetProperty("scene"), v.GetProperty("kind").GetString()!))
            .ToList();
    }

    /// <summary>One emulated engine run: the scene, the engine's faced world rotation and translation, and its labels.</summary>
    private sealed class EngineScene
    {
        /// <summary>The placeholder for a nested-billboard record, which this test does not use.</summary>
        public static readonly EngineScene Nested = new() { Kind = "nested" };

        /// <summary>The record kind: "contract", "cone" or "ball" ("nested" for the unused placeholder).</summary>
        public string Kind { get; private init; } = "";

        /// <summary>The effective Billboard Mode the engine ran (0 to 5).</summary>
        public int EffectiveMode { get; private init; } = -1;

        /// <summary>A fallback cell's stored value, or -1 for a contract vector (which lists its values).</summary>
        public int FileValue { get; private init; } = -1;

        /// <summary>The contract vector's trial index, or -1.</summary>
        public int Trial { get; private init; } = -1;

        /// <summary>A cone cell's azimuth of the camera about the aim, in degrees; NaN elsewhere.</summary>
        public double PhiDeg { get; private init; } = double.NaN;

        /// <summary>A cone cell's target angle between camera front and aim, in radians; NaN elsewhere.</summary>
        public double ThetaTarget { get; private init; } = double.NaN;

        /// <summary>A ball cell's camera distance; NaN elsewhere.</summary>
        public double Distance { get; private init; } = double.NaN;

        /// <summary>
        ///     The receipt's best axis error of the retired mapping's favorable completion, in radians; NaN for a
        ///     fallback cell.
        /// </summary>
        public double CurrentMappingBest { get; private init; } = double.NaN;

        /// <summary>The parent's world rotation (column-vector, listed by rows).</summary>
        public double[,] P { get; private init; } = new double[3, 3];

        /// <summary>The billboard node's local rotation (column-vector, listed by rows).</summary>
        public double[,] L { get; private init; } = new double[3, 3];

        /// <summary>The camera's world rotation; its columns are forward, up and right.</summary>
        public double[,] C { get; private init; } = new double[3, 3];

        /// <summary>The engine's faced world rotation (column-vector, listed by rows).</summary>
        public double[,] EngineW { get; private init; } = new double[3, 3];

        /// <summary>The parent's world translation.</summary>
        public double[] Pt { get; private init; } = [];

        /// <summary>The billboard node's local translation.</summary>
        public double[] Lt { get; private init; } = [];

        /// <summary>The camera's world position.</summary>
        public double[] Ct { get; private init; } = [];

        /// <summary>The engine's world translation of the billboard node.</summary>
        public double[] EngineP { get; private init; } = [];

        /// <summary>The parent's world scale.</summary>
        public double Ps { get; private init; }

        /// <summary>The billboard node's local scale.</summary>
        public double Ls { get; private init; }

        /// <summary>The node's world scale Ps Ls.</summary>
        public double Scale => Ps * Ls;

        /// <summary>The document's pre-facing world: rows s W0[:, k], translation engineP, rounded once.</summary>
        public Matrix4x4 PreFacing()
        {
            var w0 = Multiply(P, L);
            var s = Scale;
            return new Matrix4x4(
                (float)(s * w0[0, 0]), (float)(s * w0[1, 0]), (float)(s * w0[2, 0]), 0f,
                (float)(s * w0[0, 1]), (float)(s * w0[1, 1]), (float)(s * w0[2, 1]), 0f,
                (float)(s * w0[0, 2]), (float)(s * w0[1, 2]), (float)(s * w0[2, 2]), 0f,
                (float)EngineP[0], (float)EngineP[1], (float)EngineP[2], 1f);
        }

        /// <summary>The camera: position Ct, forward f, right r, up u (the fixture's float32 columns, exactly).</summary>
        public CameraPose Camera()
        {
            return CameraPose.FromBasis(Vector(Ct), Vector(Column(C, 0)), Vector(Column(C, 2)), Vector(Column(C, 1)));
        }

        /// <summary>Reads one record; <paramref name="scene" /> holds P, L, C and their translations and scales.</summary>
        public static EngineScene From(JsonElement record, JsonElement scene, string kind)
        {
            return new EngineScene
            {
                Kind = kind,
                EffectiveMode = record.GetProperty("effectiveMode").GetInt32(),
                FileValue = record.TryGetProperty("fileValue", out var file) ? file.GetInt32() : -1,
                Trial = record.TryGetProperty("trial", out var trial) ? trial.GetInt32() : -1,
                PhiDeg = record.TryGetProperty("phiDeg", out var phi) ? phi.GetDouble() : double.NaN,
                ThetaTarget = record.TryGetProperty("thetaTarget", out var theta) ? theta.GetDouble() : double.NaN,
                Distance = record.TryGetProperty("distance", out var distance) ? distance.GetDouble() : double.NaN,
                CurrentMappingBest = record.TryGetProperty("currentMappingBestAxisErrorRad", out var best)
                    ? best.GetDouble()
                    : double.NaN,
                P = Matrix(scene.GetProperty("P")),
                L = Matrix(scene.GetProperty("L")),
                C = Matrix(scene.GetProperty("C")),
                EngineW = Matrix(record.GetProperty("engineW")),
                Pt = Triple3(scene.GetProperty("Pt")),
                Lt = Triple3(scene.GetProperty("Lt")),
                Ct = Triple3(scene.GetProperty("Ct")),
                EngineP = Triple3(record.GetProperty("engineP")),
                Ps = scene.GetProperty("Ps").GetDouble(),
                Ls = scene.GetProperty("Ls").GetDouble()
            };
        }

        /// <summary>A fixture triple as a float32 vector.</summary>
        private static Vector3 Vector(double[] v)
        {
            return new Vector3((float)v[0], (float)v[1], (float)v[2]);
        }

        /// <summary>A fixture matrix: three rows of three numbers (column-vector, NIF Matrix33 order).</summary>
        private static double[,] Matrix(JsonElement rows)
        {
            var result = new double[3, 3];
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    result[i, j] = rows[i][j].GetDouble();
                }
            }

            return result;
        }

        /// <summary>A fixture triple of three numbers.</summary>
        private static double[] Triple3(JsonElement values)
        {
            return [values[0].GetDouble(), values[1].GetDouble(), values[2].GetDouble()];
        }
    }
}
