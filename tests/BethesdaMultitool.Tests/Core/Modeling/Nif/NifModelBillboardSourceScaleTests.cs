using System.Numerics;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Export;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     <see cref="SceneBillboard.SourceScale" /> through the reader (Shared 853b6f1, docs/billboard-source-scale.md): every
///     NiBillboardNode occurrence declares the signed product of the stored NiAVObject Scale fields from the document root
///     down to and including the billboard node, Authored, with evidence naming the chain's blocks. The value comes from
///     the fields, never from a matrix. The writers' classification of it is Shared's
///     <see cref="SceneBillboardSourceScaleFidelity" />, pinned here only as far as the reader's declaration decides it.
///     The rest report of <see cref="NifModelBillboardScaleSign" /> is kept beside the declaration (the foundation's
///     adapter note for Shared 853b6f1) and carries the same value. Expected values and evidence strings are literals
///     worked out by hand, not read back from production.
/// </summary>
public class NifModelBillboardSourceScaleTests
{
    /// <summary>The evidence closing of an exact product, as a literal.</summary>
    private const string Exact = "(exact double product of the stored float32 values)";

    /// <summary>The point reflection -I, row by row: orthonormal with determinant -1 (an improper rotation).</summary>
    private static readonly float[] PointReflection = [-1f, 0f, 0f, 0f, -1f, 0f, 0f, 0f, -1f];

    /// <summary>The app options of an FNV PC read.</summary>
    private static Dictionary<string, string> Fnv => NifModelBillboardTests.FnvOptions(false);

    /// <summary>The stored values the facing cases run on: effective modes 0 to 5, and 13 (5 with bit 3).</summary>
    public static TheoryData<int> FacingValues()
    {
        return new TheoryData<int> { 0, 1, 2, 3, 4, 5, 13 };
    }

    /// <summary>
    ///     The pin: a billboard under an ancestor whose stored Scale is -2, with its own Scale 0.5, declares RestWorldScale
    ///     -1.0 exactly, Authored, with evidence naming blocks 0 and 1, for every facing mode. The ancestor stores the
    ///     point reflection -I, so its matrix is (-2)(-I) = 2I. Control: every matrix-derived reading disagrees in sign,
    ///     so a reader that took one would fail the pin: the composed rest world matrix is the identity (determinant +1,
    ///     cube root +1), and the TRS the reader writes carries scale +2 on the root (<see cref="NifModelTransform" />
    ///     moves the improper rotation's sign into the scale) and +0.5 on the billboard, product +1. Second control: the
    ///     same stored Scales over the identity rotation declare the same -1 while that matrix's determinant is negative,
    ///     so the declaration follows the Scale fields and not the rotation.
    /// </summary>
    [Theory]
    [MemberData(nameof(FacingValues))]
    public void AncestorMinusTwo_OwnHalf_DeclaresMinusOne_WhereTheMatrixReadsPlusOne(int storedValue)
    {
        var reflected = Read(ChainFixture((ushort)storedValue, -2f, 0.5f, PointReflection), Fnv).Document;
        var proper = Read(ChainFixture((ushort)storedValue, -2f, 0.5f), Fnv).Document;

        var scale = DeclaredScale(reflected, 1);
        Assert.Equal(-1.0, scale.RestWorldScale);
        Assert.Equal(SceneValueProvenance.Authored, scale.Provenance);
        Assert.Equal("NiAVObject Scale, root to node: block 0 (-2) x block 1 (0.5) = -1 " + Exact, scale.Evidence);
        Assert.Null(reflected.Nodes[0].Billboard);

        var world = reflected.Nodes[1].LocalTransform * reflected.Nodes[0].LocalTransform;
        var determinant = (double)world.GetDeterminant();
        Assert.True(Math.Abs(determinant - 1.0) < 1e-6, $"determinant {determinant:R}");
        Assert.NotEqual(Math.Sign(scale.RestWorldScale), Math.Sign(Math.Cbrt(determinant)));
        var rootTrs = Assert.IsType<SceneTrs>(reflected.Nodes[0].LocalTrs);
        var ownTrs = Assert.IsType<SceneTrs>(reflected.Nodes[1].LocalTrs);
        Assert.Equal(new Vector3(2f), rootTrs.Scale);
        Assert.Equal(new Vector3(0.5f), ownTrs.Scale);
        Assert.NotEqual(Math.Sign(scale.RestWorldScale), Math.Sign(rootTrs.Scale.X * ownTrs.Scale.X));

        var same = DeclaredScale(proper, 1);
        Assert.Equal(-1.0, same.RestWorldScale);
        Assert.Equal(scale.Evidence, same.Evidence);
        var properWorld = proper.Nodes[1].LocalTransform * proper.Nodes[0].LocalTransform;
        Assert.True(properWorld.GetDeterminant() < 0f, $"determinant {properWorld.GetDeterminant():R}");
    }

    /// <summary>
    ///     A zero stored Scale declares the zero whose sign is the product of the factors' signs, bit for bit: the
    ///     billboard's own 0 under the root's 1 is +0, under the root's -2 it is -0, and the root's 0 over the billboard's
    ///     -0.5 is -0. The writers classify either zero as Degraded <c>billboard.source-scale-zero-unlowered</c>, and under
    ///     mode 1 the kept rest report fires too, its entry carrying the same value (compared as a number: the payload is
    ///     JSON text, so the sign of its zero is not pinned there). Control: +0 and -0 differ in their bits, so a reader
    ///     that dropped the sign fails one of the cases.
    /// </summary>
    [Theory]
    [InlineData(1f, 0f, false, "block 0 (1) x block 1 (0) = 0")]
    [InlineData(-2f, 0f, true, "block 0 (-2) x block 1 (0) = -0")]
    [InlineData(0f, -0.5f, true, "block 0 (0) x block 1 (-0.5) = -0")]
    public void ZeroScale_DeclaresTheSignedZero(float rootScale, float billboardScale, bool negative, string chain)
    {
        var document = Read(ChainFixture(1, rootScale, billboardScale), Fnv).Document;

        var scale = DeclaredScale(document, 1);
        Assert.Equal(BitConverter.DoubleToInt64Bits(negative ? double.NegativeZero : 0.0),
            BitConverter.DoubleToInt64Bits(scale.RestWorldScale));
        Assert.Equal(SceneValueProvenance.Authored, scale.Provenance);
        Assert.Equal("NiAVObject Scale, root to node: " + chain + " " + Exact, scale.Evidence);
        var row = Assert.Single(SceneBillboardSourceScaleFidelity.CreateRows(document));
        Assert.Equal(new SceneElementRef(SceneElementKind.Node, 1), row.Target);
        Assert.Equal("billboard/source-scale", row.FeatureId);
        Assert.Equal(ModelFidelityOutcome.Degraded, row.Outcome);
        Assert.Equal("billboard.source-scale-zero-unlowered", row.ReasonCode);
        Assert.Single(document.Diagnostics, d => d.Code == NifModelBillboardScaleSign.Diagnostic);
        var entry = BlockPayload(document, 1)["node"]!["billboard"]![NifModelBillboardScaleSign.PayloadKey]!;
        Assert.Equal(scale.RestWorldScale, (double)entry["reported"]![0]!["restWorldScale"]!);
    }

    /// <summary>
    ///     A negative rest world scale (review F1) is declared as well as reported: the root's stored Scale -1.25 over the
    ///     billboard declares -1.25 for every facing mode, the encoding is the one the positive scale gets, and the kept
    ///     scale-sign report is still there (its value is pinned in <see cref="NifModelBillboardTests" />). The writers
    ///     classify the declaration as Degraded <c>billboard.source-scale-negative-unlowered</c>. Control: the root's
    ///     +1.25 declares +1.25, which the writers keep as Metadata <c>billboard.source-scale-retained</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(FacingValues))]
    public void NegativeRestWorldScale_IsDeclared_AndTheWritersDegradeIt(int storedValue)
    {
        var negative = Read(NifModelBillboardTests.Fixture((ushort)storedValue, rootScale: -1.25f), Fnv).Document;
        var positive = Read(NifModelBillboardTests.Fixture((ushort)storedValue, rootScale: 1.25f), Fnv).Document;

        var declared = DeclaredScale(negative, 1);
        Assert.Equal(-1.25, declared.RestWorldScale);
        Assert.Equal("NiAVObject Scale, root to node: block 0 (-1.25) x block 1 (1) = -1.25 " + Exact, declared.Evidence);
        Assert.Equal(1.25, DeclaredScale(positive, 1).RestWorldScale);
        AssertSameEncoding(positive.Nodes[1].Billboard!, negative.Nodes[1].Billboard!);
        Assert.Equal(SceneValueProvenance.ReverseEngineered, negative.Nodes[1].Billboard!.FacingProvenance);
        Assert.Single(negative.Diagnostics, d => d.Code == NifModelBillboardScaleSign.Diagnostic);

        var row = Assert.Single(SceneBillboardSourceScaleFidelity.CreateRows(negative));
        Assert.Equal(ModelFidelityOutcome.Degraded, row.Outcome);
        Assert.Equal("billboard.source-scale-negative-unlowered", row.ReasonCode);
        var control = Assert.Single(SceneBillboardSourceScaleFidelity.CreateRows(positive));
        Assert.Equal(ModelFidelityOutcome.Metadata, control.Outcome);
        Assert.Equal("billboard.source-scale-retained", control.ReasonCode);
    }

    /// <summary>
    ///     The source scale's provenance does not follow the source key: under a Skyrim read, whose facing is Assumed, and
    ///     an FNV PC read, whose facing is ReverseEngineered, the root's -2 declares -2, Authored, with the same evidence.
    ///     Control: the facing provenance does differ between the two reads, so the key really changed.
    /// </summary>
    [Fact]
    public void SourceScaleProvenance_IsAuthored_WhateverTheSourceKey()
    {
        var skyrimOptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BethesdaModelRegistration.GameOption] = "Skyrim"
        };
        var skyrim = Read(NifModelBillboardTests.Fixture(4, rootScale: -2f), skyrimOptions).Document;
        var fnv = Read(NifModelBillboardTests.Fixture(4, rootScale: -2f), Fnv).Document;

        var assumedKey = DeclaredScale(skyrim, 1);
        var establishedKey = DeclaredScale(fnv, 1);
        Assert.Equal(-2.0, assumedKey.RestWorldScale);
        Assert.Equal(-2.0, establishedKey.RestWorldScale);
        Assert.Equal(SceneValueProvenance.Authored, assumedKey.Provenance);
        Assert.Equal(SceneValueProvenance.Authored, establishedKey.Provenance);
        Assert.Equal("NiAVObject Scale, root to node: block 0 (-2) x block 1 (1) = -2 " + Exact, assumedKey.Evidence);
        Assert.Equal(establishedKey.Evidence, assumedKey.Evidence);
        Assert.Equal(SceneValueProvenance.Assumed, skyrim.Nodes[1].Billboard!.FacingProvenance);
        Assert.Equal(SceneValueProvenance.ReverseEngineered, fnv.Nodes[1].Billboard!.FacingProvenance);
    }

    /// <summary>
    ///     Both outputs of one read carry one value: under the ancestor whose stored Scale is -2 over the point reflection
    ///     -I, with its own Scale 0.5, the kept rest report states -1 in its text and in its <c>restWorldScale</c>, the
    ///     value <see cref="SceneBillboard.SourceScale" /> declares. Control: that fixture's composed rest matrix is the
    ///     identity (determinant +1), so a report that read the matrix would see a positive scale and report nothing.
    ///     Second control, the order: over the chain 1.1, 1.1, 1.1, -1.2 the root-first double product is
    ///     -1.5972001673221652 and the leaf-first one, which the stage computed for itself before this change, is
    ///     -1.597200167322165 (both worked out outside this code base); the report carries the first, the declaration's.
    /// </summary>
    [Fact]
    public void RestReport_CarriesTheValueSourceScaleDeclares()
    {
        var reflected = Read(ChainFixture(2, -2f, 0.5f, PointReflection), Fnv).Document;
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1], scale: 1.1f);
        AddNode(builder, builder.AddString("Upper"), [2], scale: 1.1f);
        AddNode(builder, builder.AddString("Lower"), [3], scale: 1.1f);
        var name = builder.AddString("Billboard");
        builder.AddBlock("NiBillboardNode", w => NifTestBlockLayouts.NodeWithTail(w, 34, name, [],
            tail => NifTestBlockLayouts.BillboardTail(tail, 2), scale: -1.2f));
        var deep = Read(builder.Build(), Fnv).Document;

        var declared = DeclaredScale(reflected, 1);
        var (reportedValue, text) = RestReport(reflected, 1);
        Assert.Equal(-1.0, declared.RestWorldScale);
        Assert.Equal(declared.RestWorldScale, reportedValue);
        Assert.Contains("node 1 at rest (the product of the stored Scale fields is -1)", text, StringComparison.Ordinal);
        var world = reflected.Nodes[1].LocalTransform * reflected.Nodes[0].LocalTransform;
        Assert.True(world.GetDeterminant() > 0f, $"determinant {world.GetDeterminant():R}");

        var deepDeclared = DeclaredScale(deep, 3);
        var (deepValue, deepText) = RestReport(deep, 3);
        Assert.Equal(-1.5972001673221652, deepDeclared.RestWorldScale);
        Assert.Equal(deepDeclared.RestWorldScale, deepValue);
        Assert.NotEqual(-1.597200167322165, deepValue);
        Assert.Contains("node 3 at rest (the product of the stored Scale fields is -1.5972001673221652)", deepText,
            StringComparison.Ordinal);
        Assert.Equal("NiAVObject Scale, root to node: block 0 (1.1) x block 1 (1.1) x block 2 (1.1) x block 3 (-1.2) = " +
                     "-1.5972001673221652 (double product of the stored float32 values, rounded)", deepDeclared.Evidence);
    }

    /// <summary>
    ///     No billboard, no declaration: a chain of plain NiNodes with the same stored Scales (-2 over 0.5) and an
    ///     NiBillboardNode whose stored mode 6 does not face declare nothing, so the writers emit no source-scale row.
    ///     Control: the same chain with mode 2 declares -1 and yields exactly one row, so the silence is the absent
    ///     billboard and not the chain.
    /// </summary>
    [Fact]
    public void BillboardFreeDocument_DeclaresNoSourceScale()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1], scale: -2f);
        AddNode(builder, builder.AddString("Child"), [], scale: 0.5f);
        var plain = Read(builder.Build(), Fnv).Document;
        var noFacing = Read(ChainFixture(6, -2f, 0.5f), Fnv).Document;
        var facing = Read(ChainFixture(2, -2f, 0.5f), Fnv).Document;

        Assert.Equal(2, plain.Nodes.Count);
        Assert.All(plain.Nodes, node => Assert.Null(node.Billboard));
        Assert.All(noFacing.Nodes, node => Assert.Null(node.Billboard));
        Assert.Empty(SceneBillboardSourceScaleFidelity.CreateRows(plain));
        Assert.Empty(SceneBillboardSourceScaleFidelity.CreateRows(noFacing));
        Assert.Equal(-1.0, DeclaredScale(facing, 1).RestWorldScale);
        Assert.Single(SceneBillboardSourceScaleFidelity.CreateRows(facing));
    }

    /// <summary>
    ///     A scale key on the chain (the root's controller keys its Scale from 1 to the last key): the occurrence declares
    ///     only its rest scalar (+1), and Shared's analysis adds the Degraded <c>billboard/source-scale-animation</c> row
    ///     whatever the key's sign. That row cannot tell -0.5 from 0.5, which is why the reader keeps
    ///     <see cref="NifModelBillboardScaleSign" /> for stated values: it reports -0.5 and not 0.5.
    /// </summary>
    [Theory]
    [InlineData(-0.5f, true)]
    [InlineData(0.5f, false)]
    public void ScaleKeyOnTheChain_TheContractFlagsThePath_AndTheReaderNamesTheSign(float lastScaleKey, bool reported)
    {
        var document = Read(NifModelBillboardTests.ScaleKeyFixture(2, lastScaleKey), Fnv).Document;

        Assert.Equal(1.0, DeclaredScale(document, 1).RestWorldScale);
        var rows = SceneBillboardSourceScaleFidelity.CreateRows(document).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(new SceneElementRef(SceneElementKind.Node, 1), row.Target));
        Assert.Equal("billboard/source-scale", rows[0].FeatureId);
        Assert.Equal(ModelFidelityOutcome.Metadata, rows[0].Outcome);
        Assert.Equal("billboard/source-scale-animation", rows[1].FeatureId);
        Assert.Equal(ModelFidelityOutcome.Degraded, rows[1].Outcome);
        Assert.Equal("billboard.source-scale-animation-unlowered", rows[1].ReasonCode);
        Assert.Equal(reported ? 1 : 0, document.Diagnostics.Count(d => d.Code == NifModelBillboardScaleSign.Diagnostic));
    }

    /// <summary>
    ///     The evidence says whether the double product is exact: 1.2f cubed needs more than 53 significant bits, so the
    ///     root-first product rounds to 1.7280002059936606 (worked out outside this code base) and the evidence says
    ///     "rounded"; -2 x 0.5 is exact. The block indices are the chain's, not positions.
    /// </summary>
    [Fact]
    public void Evidence_StatesWhetherTheProductRounded()
    {
        var rounded = Assert.IsType<SceneBillboardSourceScale>(
            NifModelBillboards.SourceScale([(0, 1.2f), (4, 1.2f), (9, 1.2f)]));
        var exact = Assert.IsType<SceneBillboardSourceScale>(NifModelBillboards.SourceScale([(3, -2f), (7, 0.5f)]));

        Assert.Equal(1.7280002059936606, rounded.RestWorldScale);
        Assert.Equal("NiAVObject Scale, root to node: block 0 (1.2) x block 4 (1.2) x block 9 (1.2) = " +
                     "1.7280002059936606 (double product of the stored float32 values, rounded)", rounded.Evidence);
        Assert.Equal("NiAVObject Scale, root to node: block 3 (-2) x block 7 (0.5) = -1 " + Exact, exact.Evidence);
        Assert.Equal(SceneValueProvenance.Authored, rounded.Provenance);
    }

    /// <summary>
    ///     A chain longer than sixteen links lists its first and last eight and counts the links between them; a chain of
    ///     sixteen lists every link. The product still runs over every link (-2 x 1 ... x 0.5 = -1).
    /// </summary>
    [Fact]
    public void LongChainEvidence_ListsTheFirstAndLastEightLinks()
    {
        var chain = Enumerable.Range(0, 20).Select(i => (i * 2, 1f)).ToList();
        chain[0] = (0, -2f);
        chain[19] = (38, 0.5f);
        var sixteen = chain.Take(15).Append((38, 0.5f)).ToList();

        var scale = Assert.IsType<SceneBillboardSourceScale>(NifModelBillboards.SourceScale(chain));
        var full = Assert.IsType<SceneBillboardSourceScale>(NifModelBillboards.SourceScale(sixteen));

        Assert.Equal(-1.0, scale.RestWorldScale);
        Assert.Equal("NiAVObject Scale, root to node: block 0 (-2) x block 2 (1) x block 4 (1) x block 6 (1) x " +
                     "block 8 (1) x block 10 (1) x block 12 (1) x block 14 (1) x (4 links not listed) x block 24 (1) x " +
                     "block 26 (1) x block 28 (1) x block 30 (1) x block 32 (1) x block 34 (1) x block 36 (1) x " +
                     "block 38 (0.5) = -1 " + Exact, scale.Evidence);
        Assert.Equal(-1.0, full.RestWorldScale);
        Assert.Equal("NiAVObject Scale, root to node: block 0 (-2) x block 2 (1) x block 4 (1) x block 6 (1) x " +
                     "block 8 (1) x block 10 (1) x block 12 (1) x block 14 (1) x block 16 (1) x block 18 (1) x " +
                     "block 20 (1) x block 22 (1) x block 24 (1) x block 26 (1) x block 28 (1) x block 38 (0.5) = -1 " +
                     Exact, full.Evidence);
    }

    /// <summary>
    ///     A product double cannot state is left unstated (null, never a guessed sign class): nine stored Scales of 3e38
    ///     overflow, and nine of 2e-38 underflow to zero though no factor is zero. Control: a zero after the overflowing
    ///     run still declares the exact signed zero IEEE multiplication gives (+0, and -0 when one factor is negative), so
    ///     the refusal is the double range, not the chain's length or magnitude.
    /// </summary>
    [Fact]
    public void UnrepresentableProduct_IsLeftUnstated()
    {
        var huge = Enumerable.Range(0, 9).Select(i => (i, 3e38f)).ToList();
        var tiny = Enumerable.Range(0, 9).Select(i => (i, 2e-38f)).ToList();
        var zeroAfter = huge.Append((9, 0f)).ToList();
        var negativeZeroAfter = huge.Skip(1).Prepend((0, -3e38f)).Append((9, 0f)).ToList();

        Assert.Null(NifModelBillboards.SourceScale(huge));
        Assert.Null(NifModelBillboards.SourceScale(tiny));
        var zero = Assert.IsType<SceneBillboardSourceScale>(NifModelBillboards.SourceScale(zeroAfter));
        var negativeZero = Assert.IsType<SceneBillboardSourceScale>(NifModelBillboards.SourceScale(negativeZeroAfter));
        Assert.Equal(BitConverter.DoubleToInt64Bits(0.0), BitConverter.DoubleToInt64Bits(zero.RestWorldScale));
        Assert.Equal(BitConverter.DoubleToInt64Bits(double.NegativeZero),
            BitConverter.DoubleToInt64Bits(negativeZero.RestWorldScale));
    }

    /// <summary>Asserts every encoding field of two billboards is equal; the source scale is not an encoding field.</summary>
    internal static void AssertSameEncoding(SceneBillboard expected, SceneBillboard actual)
    {
        Assert.Equal(expected.Aim, actual.Aim);
        Assert.Equal(expected.LockedAxis, actual.LockedAxis);
        Assert.Equal(expected.LockedAxisFrame, actual.LockedAxisFrame);
        Assert.Equal(expected.Rigid, actual.Rigid);
        Assert.Equal(expected.Pivot, actual.Pivot);
        Assert.Equal(expected.Anchor, actual.Anchor);
        Assert.Equal(expected.Front, actual.Front);
        Assert.Equal(expected.Up, actual.Up);
        Assert.Equal(expected.Roll, actual.Roll);
        Assert.Equal(expected.Reflection, actual.Reflection);
        Assert.Equal(expected.RawMode, actual.RawMode);
        Assert.Equal(expected.PlaneFallbackCosine, actual.PlaneFallbackCosine);
        Assert.Equal(expected.UnfacedDistanceSquared, actual.UnfacedDistanceSquared);
        Assert.Equal(expected.PlaneFallbackEdge, actual.PlaneFallbackEdge);
        Assert.Equal(expected.FacingProvenance, actual.FacingProvenance);
        Assert.Equal(expected.FacingEvidence, actual.FacingEvidence);
        Assert.Equal(expected.ScheduleProvenance, actual.ScheduleProvenance);
        Assert.Equal(expected.ScheduleEvidence, actual.ScheduleEvidence);
    }

    /// <summary>
    ///     0 NiNode "Root" (stored rotation <paramref name="rootRotation" />, the identity when null, and uniform Scale
    ///     <paramref name="rootScale" />) [1]; 1 NiBillboardNode "Billboard" with the given mode and uniform Scale
    ///     <paramref name="billboardScale" />, no children.
    /// </summary>
    private static byte[] ChainFixture(ushort mode, float rootScale, float billboardScale, float[]? rootRotation = null)
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1], rotation: rootRotation, scale: rootScale);
        var name = builder.AddString("Billboard");
        builder.AddBlock("NiBillboardNode", w => NifTestBlockLayouts.NodeWithTail(w, 34, name, [],
            tail => NifTestBlockLayouts.BillboardTail(tail, mode), scale: billboardScale));
        return builder.Build();
    }

    /// <summary>
    ///     The document's single scale-sign report, which must be a rest report of the billboard whose block and node
    ///     index are both <paramref name="index" />: the entry's <c>restWorldScale</c> and the diagnostic's text.
    /// </summary>
    private static (double Value, string Text) RestReport(ModelDocument document, int index)
    {
        var diagnostic = Assert.Single(document.Diagnostics, d => d.Code == NifModelBillboardScaleSign.Diagnostic);
        var entry = BlockPayload(document, index)["node"]!["billboard"]![NifModelBillboardScaleSign.PayloadKey]!;
        var occurrence = Assert.Single(entry["reported"]!.AsArray())!;
        Assert.Equal(index, (int)occurrence["node"]!);
        Assert.True((bool)occurrence["restReported"]!);
        return ((double)occurrence["restWorldScale"]!, DiagnosticText(diagnostic));
    }

    /// <summary>The declared source scale of a node's billboard, failing when either is absent.</summary>
    private static SceneBillboardSourceScale DeclaredScale(ModelDocument document, int node)
    {
        var billboard = Assert.IsType<SceneBillboard>(document.Nodes[node].Billboard);
        return Assert.IsType<SceneBillboardSourceScale>(billboard.SourceScale);
    }
}
