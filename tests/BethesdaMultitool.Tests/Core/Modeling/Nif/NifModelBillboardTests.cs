using System.Numerics;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     NiBillboardNode through the reader: every stored Billboard Mode takes the encoding of its effective mode
///     (<c>value &amp; 7</c>, RE-25), keeps the stored value as <see cref="SceneBillboard.RawMode" /> and in the native
///     payload, and effective modes 6 and 7 leave an ordinary node. Engine agreement is
///     <see cref="NifModelBillboardEngineVectorTests" />; provenance per source key is
///     <see cref="NifModelBillboardSourceTests" />; each occurrence's rest world scalar
///     (<see cref="SceneBillboard.SourceScale" />) is <see cref="NifModelBillboardSourceScaleTests" />.
/// </summary>
public class NifModelBillboardTests
{
    /// <summary>The literal plane-fallback cosine of modes 3 and 4 (DESIGN-rev4 4.1: K*).</summary>
    private const double PlaneFallbackCosine = 0.9999989569187164;

    /// <summary>The literal unfaced squared distance of modes 3 and 4 (DESIGN-rev4 4.1: float32(0.001)).</summary>
    private const double UnfacedDistanceSquared = 0.0010000000474974513;

    /// <summary>The literal FNV PC plane-fallback edge, to its ten printed digits (law_check_rev4.txt L6).</summary>
    private const double FnvPcEdgeTenDigits = 1.8433998931e-3;

    /// <summary>A quarter turn about X, row by row: local +Z maps to -Y.</summary>
    private static readonly float[] QuarterTurnAboutX = [1f, 0f, 0f, 0f, 0f, -1f, 0f, 1f, 0f];

    /// <summary>
    ///     0 NiNode "Root" (uniform scale <paramref name="rootScale" />) [1]; 1 NiBillboardNode "Billboard" with the given
    ///     mode, rotation and uniform scale <paramref name="billboardScale" />, no children.
    /// </summary>
    internal static byte[] Fixture(ushort mode, float[]? rotation = null, float rootScale = 1f, bool bigEndian = false,
        float billboardScale = 1f)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        AddNode(builder, builder.AddString("Root"), [1], scale: rootScale);
        var name = builder.AddString("Billboard");
        builder.AddBlock("NiBillboardNode", w => NifTestBlockLayouts.NodeWithTail(w, 34, name, [],
            tail => NifTestBlockLayouts.BillboardTail(tail, mode), rotation: rotation, scale: billboardScale));
        return builder.Build();
    }

    /// <summary>
    ///     0 NiNode "Root" (stored Scale 1) [1], driven by 2; 1 NiBillboardNode "Billboard" with the given mode; 2 an
    ///     active, free-running NiTransformController on Root through 3; 3 NiTransformInterpolator over 4; 4
    ///     NiTransformData with no rotation or translation keys and the LINEAR scale keys (0, 1) and
    ///     (1, <paramref name="lastScaleKey" />), written field by field from nif.xml (NiKeyframeData at 20.2.0.7: Num
    ///     Rotation Keys, the Translations KeyGroup, then the Scales KeyGroup: Num Keys, Interpolation, time and value per
    ///     key).
    /// </summary>
    internal static byte[] ScaleKeyFixture(ushort mode, float lastScaleKey)
    {
        var builder = new NifTestFileBuilder(false, 34);
        var root = builder.AddString("Root");
        var name = builder.AddString("Billboard");
        NifModelAnimationReaderTestSupport.Add(builder, 0, "NiNode",
            NifModelAnimationReaderTestSupport.Node(root, [1], 2));
        NifModelAnimationReaderTestSupport.Add(builder, 1, "NiBillboardNode",
            w => NifTestBlockLayouts.NodeWithTail(w, 34, name, [], tail => NifTestBlockLayouts.BillboardTail(tail, mode)));
        NifModelAnimationReaderTestSupport.Add(builder, 2, "NiTransformController",
            NifModelAnimationReaderTestSupport.TransformControllerBlock(0, 3, NifModelAnimationReaderTestSupport.Active));
        NifModelAnimationReaderTestSupport.Add(builder, 3, "NiTransformInterpolator",
            NifModelAnimationReaderTestSupport.TransformInterpolator(4));
        NifModelAnimationReaderTestSupport.Add(builder, 4, "NiTransformData", w =>
        {
            w.U32(0);
            w.U32(0);
            w.U32(2).U32(1);
            w.F32s(0f, 1f).F32s(1f, lastScaleKey);
        });
        return builder.Build();
    }

    /// <summary>The app options of an FNV read: the game, and for a console file the Xbox 360.</summary>
    internal static Dictionary<string, string> FnvOptions(bool bigEndian)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BethesdaModelRegistration.GameOption] = "fnv"
        };
        if (bigEndian)
        {
            options[BethesdaModelRegistration.PlatformOption] = "x360";
        }

        return options;
    }

    /// <summary>Stored values 0 to 15 in both byte orders.</summary>
    public static TheoryData<int, bool> EveryStoredValue()
    {
        var data = new TheoryData<int, bool>();
        for (var value = 0; value < 16; value++)
        {
            data.Add(value, false);
            data.Add(value, true);
        }

        return data;
    }

    /// <summary>
    ///     Every stored value 0-15, LE (FNV PC) and BE (FNV X360), pins every field of its effective mode's encoding
    ///     against a literal table (DESIGN-rev4 section 3, the foundation's contract names), RawMode = the stored value,
    ///     both provenances ReverseEngineered, and the native payload's stored and effective modes and bit 3. Values 8-15
    ///     equal 0-7 in every field but RawMode. The plane-fallback edge is declared for FNV PC modes 3 and 4 only.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryStoredValue))]
    public void EveryStoredValue_TakesItsEffectiveModeEncoding(int storedValue, bool bigEndian)
    {
        var result = Read(Fixture((ushort)storedValue, bigEndian: bigEndian), FnvOptions(bigEndian));
        var document = result.Document;
        var effective = storedValue & 7;
        var payload = BlockPayload(document, 1)["node"]!["billboard"]!;

        Assert.Equal(storedValue, (int)payload["mode"]!);
        Assert.Equal(effective, (int)payload["effectiveMode"]!);
        Assert.Equal(storedValue >= 8, (bool)payload["updateControllersBit"]!);
        Assert.Null(document.Nodes[0].Billboard);
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == NifModelLayerReader.BillboardDiagnostic);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:1").Kind);
        SceneValidation.ValidateStructure(document);
        if (effective >= 6)
        {
            Assert.Null(document.Nodes[1].Billboard);
            return;
        }

        var billboard = Assert.IsType<SceneBillboard>(document.Nodes[1].Billboard);
        var expected = ExpectedEncoding(effective);
        Assert.Equal(expected.Aim, billboard.Aim);
        Assert.Equal(expected.LockedAxis, billboard.LockedAxis);
        Assert.Equal(expected.Frame, billboard.LockedAxisFrame);
        Assert.Equal(expected.Rigid, billboard.Rigid);
        Assert.Equal(expected.Front, billboard.Front);
        Assert.Equal(expected.Up, billboard.Up);
        Assert.Equal(expected.Roll, billboard.Roll);
        Assert.Equal(expected.Reflection, billboard.Reflection);
        Assert.Equal(Vector3.Zero, billboard.Pivot);
        Assert.Equal(Vector3.Zero, billboard.Anchor);
        Assert.Equal((ulong)storedValue, billboard.RawMode);
        var thresholds = effective is 3 or 4;
        Assert.Equal(thresholds ? PlaneFallbackCosine : (double?)null, billboard.PlaneFallbackCosine);
        Assert.Equal(thresholds ? UnfacedDistanceSquared : (double?)null, billboard.UnfacedDistanceSquared);
        if (thresholds && !bigEndian)
        {
            var edge = Assert.IsType<double>(billboard.PlaneFallbackEdge);
            Assert.True(Math.Abs(edge - FnvPcEdgeTenDigits) <= 5e-14, $"edge {edge:R}");
        }
        else
        {
            Assert.Null(billboard.PlaneFallbackEdge);
        }

        Assert.Equal(SceneValueProvenance.ReverseEngineered, billboard.FacingProvenance);
        Assert.Equal(SceneValueProvenance.ReverseEngineered, billboard.ScheduleProvenance);
        Assert.Equal(expected.Aim.ToString(), (string?)payload["aim"]);
        Assert.Equal(nameof(SceneValueProvenance.ReverseEngineered), (string?)payload["facingProvenance"]);
    }

    /// <summary>
    ///     The two thresholds are the engine's float32 constants: float32(0.999999) - 2^-25 (the real cosine at which the
    ///     runtime's float32 dot reaches float32(0.999999)) and float32(0.001). Derived here from float arithmetic, not
    ///     copied from production. Control: the naive double 0.999999 and 0.001 differ from both.
    /// </summary>
    [Fact]
    public void Thresholds_AreTheEngineFloat32Constants()
    {
        var cosine = (double)0.999999f - Math.Pow(2, -25);
        var distance = (double)0.001f;

        Assert.Equal(cosine, NifModelBillboards.PlaneFallbackCosine);
        Assert.Equal(distance, NifModelBillboards.UnfacedDistanceSquared);
        Assert.Equal(PlaneFallbackCosine, cosine);
        Assert.Equal(UnfacedDistanceSquared, distance);
        Assert.NotEqual(0.999999, NifModelBillboards.PlaneFallbackCosine);
        Assert.NotEqual(0.001, NifModelBillboards.UnfacedDistanceSquared);
        Assert.True(Math.Acos(cosine) <= NifModelBillboardSource.FnvPcPlaneFallbackEdge);
    }

    /// <summary>
    ///     Values 6, 7, 14 and 15 do not face: no billboard, an ordinary node, the stored value kept in the payload with
    ///     its effective mode. Control: the value two below each (4, 5, 12 and 13, which differ only in bit 1) does face,
    ///     so the rule is <c>&amp; 7</c>, neither "below 6" nor the retired "8 and above are unresolved".
    /// </summary>
    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(14)]
    [InlineData(15)]
    public void NoFacingValues_LeaveAnOrdinaryNode(int storedValue)
    {
        var document = Read(Fixture((ushort)storedValue), FnvOptions(false)).Document;
        var payload = BlockPayload(document, 1)["node"]!["billboard"]!;

        Assert.Null(document.Nodes[1].Billboard);
        Assert.Equal(SceneNodeRole.Transform, document.Nodes[1].Role);
        Assert.Equal(storedValue, (int)payload["mode"]!);
        Assert.Equal(storedValue & 7, (int)payload["effectiveMode"]!);
        Assert.Contains("identity", (string?)payload["facing"], StringComparison.Ordinal);
        Assert.Null(payload["facingProvenance"]);

        var facing = Read(Fixture((ushort)(storedValue - 2)), FnvOptions(false)).Document;
        Assert.NotNull(facing.Nodes[1].Billboard);
    }

    /// <summary>
    ///     The encoding no longer depends on the occurrence's world: a block placed under a parent with a quarter turn
    ///     about X and scale 2 and under an identity parent gets one encoding, whose mode-1 lock is the node-local +Y and
    ///     whose mode-5 lock is document +Z. Only <see cref="SceneBillboard.SourceScale" /> differs, because it is the
    ///     occurrence's own chain: 1 x 2 x 1 under the turned parent and 1 x 1 x 1 under the plain one, with evidence
    ///     naming blocks 0, 1, 3 and 0, 2, 3. Control: the two occurrences really are in different worlds, since the world
    ///     images of their local +Z (the retired rule's lock, magnitude kept) are (0, -2, 0) and (0, 0, 1), so one shared
    ///     encoding could not come from that rule.
    /// </summary>
    [Theory]
    [InlineData(1, 0f, 1f, 0f, SceneBillboardAxisFrame.Node)]
    [InlineData(9, 0f, 1f, 0f, SceneBillboardAxisFrame.Node)]
    [InlineData(5, 0f, 0f, 1f, SceneBillboardAxisFrame.Document)]
    [InlineData(13, 0f, 0f, 1f, SceneBillboardAxisFrame.Document)]
    public void EveryOccurrence_SharesOneEncoding_WhateverItsWorld(int storedValue, float x, float y, float z,
        SceneBillboardAxisFrame frame)
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1, 2]);
        AddNode(builder, builder.AddString("Turned"), [3], rotation: QuarterTurnAboutX, scale: 2f);
        AddNode(builder, builder.AddString("Plain"), [3]);
        var name = builder.AddString("Billboard");
        builder.AddBlock("NiBillboardNode", w => NifTestBlockLayouts.NodeWithTail(w, 34, name, [],
            tail => NifTestBlockLayouts.BillboardTail(tail, (ushort)storedValue)));

        var document = Read(builder.Build(), FnvOptions(false)).Document;

        Assert.Equal(["Root", "Turned", "Billboard", "Plain", "Billboard"], document.Nodes.Select(n => n.Name));
        var under = Assert.IsType<SceneBillboard>(document.Nodes[2].Billboard);
        var plain = Assert.IsType<SceneBillboard>(document.Nodes[4].Billboard);
        NifModelBillboardSourceScaleTests.AssertSameEncoding(plain, under);
        var underScale = Assert.IsType<SceneBillboardSourceScale>(under.SourceScale);
        var plainScale = Assert.IsType<SceneBillboardSourceScale>(plain.SourceScale);
        Assert.Equal(2.0, underScale.RestWorldScale);
        Assert.Equal(1.0, plainScale.RestWorldScale);
        Assert.Equal("NiAVObject Scale, root to node: block 0 (1) x block 1 (2) x block 3 (1) = 2 " +
                     "(exact double product of the stored float32 values)", underScale.Evidence);
        Assert.Equal("NiAVObject Scale, root to node: block 0 (1) x block 2 (1) x block 3 (1) = 1 " +
                     "(exact double product of the stored float32 values)", plainScale.Evidence);
        Assert.Equal(new Vector3(x, y, z), under.LockedAxis);
        Assert.Equal(frame, under.LockedAxisFrame);
        Assert.Equal(2, (int)BlockPayload(document, 3)["node"]!["billboard"]!["occurrences"]!);
        var turnedWorld = document.Nodes[2].LocalTransform * document.Nodes[1].LocalTransform *
                          document.Nodes[0].LocalTransform;
        var plainWorld = document.Nodes[4].LocalTransform * document.Nodes[3].LocalTransform *
                         document.Nodes[0].LocalTransform;
        var retiredTurned = Vector3.TransformNormal(Vector3.UnitZ, turnedWorld);
        var retiredPlain = Vector3.TransformNormal(Vector3.UnitZ, plainWorld);
        Assert.True(Vector3.Distance(retiredTurned, new Vector3(0f, -2f, 0f)) < 1e-5f, $"{retiredTurned}");
        Assert.True(Vector3.Distance(retiredPlain, Vector3.UnitZ) < 1e-6f, $"{retiredPlain}");
        Assert.True(Vector3.Distance(retiredTurned, retiredPlain) > 2f);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A negative rest world scale (review F1): the root's stored Scale -1.25 over the billboard is reported once per
    ///     block with a native <c>scaleSign</c> entry naming the occurrence and the product of the stored Scale fields,
    ///     for every facing mode (DESIGN-rev4 6.7: RE-25 did not exercise a negative scale), and the encoding is the one
    ///     the positive scale gets. The report is kept beside the <see cref="SceneBillboard.SourceScale" /> declaration
    ///     (the foundation's adapter note for Shared 853b6f1), and the entry's value is the one the declaration carries.
    ///     Control: the root's Scale +1.25 reports nothing.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(13)]
    public void NegativeRestWorldScale_IsReported_AndTheEncodingKept(int storedValue)
    {
        var negative = Read(Fixture((ushort)storedValue, rootScale: -1.25f), FnvOptions(false)).Document;
        var positive = Read(Fixture((ushort)storedValue, rootScale: 1.25f), FnvOptions(false)).Document;

        var diagnostic = Assert.Single(negative.Diagnostics, d => d.Code == NifModelBillboardScaleSign.Diagnostic);
        var text = DiagnosticText(diagnostic);
        Assert.Contains("Block 1 (NiBillboardNode): 1 of 1 occurrence(s)", text, StringComparison.Ordinal);
        Assert.Contains("the product of the stored Scale fields is -1.25", text, StringComparison.Ordinal);
        Assert.Contains(NifModelBillboardScaleSign.ContractGap, text, StringComparison.Ordinal);
        var entry = BlockPayload(negative, 1)["node"]!["billboard"]![NifModelBillboardScaleSign.PayloadKey]!;
        Assert.Equal(NifModelBillboardScaleSign.ContractGap, (string?)entry["contractGap"]);
        Assert.Equal(1, (int)entry["reportedOccurrences"]!);
        var occurrence = entry["reported"]![0]!;
        Assert.Equal(1, (int)occurrence["node"]!);
        Assert.Equal(-1.25, (double)occurrence["restWorldScale"]!);
        Assert.True((bool)occurrence["restReported"]!);
        Assert.Empty(occurrence["statedScales"]!.AsArray());
        var declared = Assert.IsType<SceneBillboardSourceScale>(negative.Nodes[1].Billboard!.SourceScale);
        Assert.Equal(declared.RestWorldScale, (double)occurrence["restWorldScale"]!);

        var kept = Assert.IsType<SceneBillboard>(negative.Nodes[1].Billboard);
        var reference = Assert.IsType<SceneBillboard>(positive.Nodes[1].Billboard);
        Assert.Equal(reference.Aim, kept.Aim);
        Assert.Equal(reference.LockedAxis, kept.LockedAxis);
        Assert.Equal(reference.LockedAxisFrame, kept.LockedAxisFrame);
        Assert.Equal(reference.Rigid, kept.Rigid);
        Assert.Equal(reference.Front, kept.Front);
        Assert.Equal(reference.Up, kept.Up);
        Assert.Equal(reference.Roll, kept.Roll);
        Assert.Equal(reference.Reflection, kept.Reflection);
        Assert.Equal(reference.RawMode, kept.RawMode);
        Assert.Equal(SceneValueProvenance.ReverseEngineered, kept.FacingProvenance);
        Assert.DoesNotContain(positive.Diagnostics, d => d.Code == NifModelBillboardScaleSign.Diagnostic);
        Assert.Null(BlockPayload(positive, 1)["node"]!["billboard"]![NifModelBillboardScaleSign.PayloadKey]);
    }

    /// <summary>
    ///     A zero rest world scale is reported for effective modes 1 and 5 only: RE-25's formulas for those modes divide
    ///     by the scale, while modes 0, 2, 3 and 4 draw a zero-extent card in the engine and in a writer alike
    ///     (DESIGN-rev4 6.7). The negative case above reports every mode, so this pins the zero rule separately. The
    ///     zero <see cref="SceneBillboard.SourceScale" /> is declared for every mode, so the report's mode rule is its
    ///     own, and a reported entry carries the declared value.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(9, true)]
    [InlineData(13, true)]
    public void ZeroRestWorldScale_IsReportedForModesOneAndFive(int storedValue, bool reported)
    {
        var document = Read(Fixture((ushort)storedValue, rootScale: 0f), FnvOptions(false)).Document;

        Assert.NotNull(document.Nodes[1].Billboard);
        var declared = Assert.IsType<SceneBillboardSourceScale>(document.Nodes[1].Billboard!.SourceScale);
        Assert.Equal(0.0, declared.RestWorldScale);
        Assert.Equal(reported ? 1 : 0,
            document.Diagnostics.Count(d => d.Code == NifModelBillboardScaleSign.Diagnostic));
        var entry = BlockPayload(document, 1)["node"]!["billboard"]![NifModelBillboardScaleSign.PayloadKey];
        Assert.Equal(reported, entry is not null);
        if (entry is not null)
        {
            Assert.Equal(0.0, (double)entry["reported"]![0]!["restWorldScale"]!);
            Assert.Equal(declared.RestWorldScale, (double)entry["reported"]![0]!["restWorldScale"]!);
        }
    }

    /// <summary>
    ///     The rest world scale is the product of the stored Scale fields along the chain: the root's -2 over the
    ///     billboard's own -0.5 is +1, which the engine draws like a positive scale, so nothing is reported. Control: the
    ///     root's -2 over the billboard's +0.5 is -1 and is reported, so a rule that reported any negative stored Scale, or
    ///     only the billboard's own, would fail one of the two. <see cref="SceneBillboard.SourceScale" /> declares the
    ///     same +1 and -1, with the chain in its evidence.
    /// </summary>
    [Fact]
    public void RestWorldScale_IsTheProductAlongTheChain()
    {
        var cancelled = Read(Fixture(2, rootScale: -2f, billboardScale: -0.5f), FnvOptions(false)).Document;
        var negative = Read(Fixture(2, rootScale: -2f, billboardScale: 0.5f), FnvOptions(false)).Document;

        Assert.DoesNotContain(cancelled.Diagnostics, d => d.Code == NifModelBillboardScaleSign.Diagnostic);
        Assert.Null(BlockPayload(cancelled, 1)["node"]!["billboard"]![NifModelBillboardScaleSign.PayloadKey]);
        Assert.Single(negative.Diagnostics, d => d.Code == NifModelBillboardScaleSign.Diagnostic);
        var entry = BlockPayload(negative, 1)["node"]!["billboard"]![NifModelBillboardScaleSign.PayloadKey]!;
        Assert.Equal(-1.0, (double)entry["reported"]![0]!["restWorldScale"]!);
        var cancelledScale = Assert.IsType<SceneBillboardSourceScale>(cancelled.Nodes[1].Billboard!.SourceScale);
        var negativeScale = Assert.IsType<SceneBillboardSourceScale>(negative.Nodes[1].Billboard!.SourceScale);
        Assert.Equal(1.0, cancelledScale.RestWorldScale);
        Assert.Equal(-1.0, negativeScale.RestWorldScale);
        Assert.Equal("NiAVObject Scale, root to node: block 0 (-2) x block 1 (-0.5) = 1 " +
                     "(exact double product of the stored float32 values)", cancelledScale.Evidence);
    }

    /// <summary>
    ///     A scale key on the chain: the root's free-running controller keys its Scale from 1 to the last key. A negative
    ///     key is reported for every facing mode and a zero key for modes 1 and 5, naming the clip, the keyed node and the
    ///     smallest value; the rest world scale (1, the declared <see cref="SceneBillboard.SourceScale" />) is not what
    ///     reports it. Controls: a positive last key reports nothing, and a zero key under mode 2 reports nothing; in every
    ///     case the clip really keys the root's Scale to the last key, so a silence is not a missed track.
    /// </summary>
    [Theory]
    [InlineData(2, -0.5f, true)]
    [InlineData(1, -0.5f, true)]
    [InlineData(1, 0f, true)]
    [InlineData(5, 0f, true)]
    [InlineData(2, 0f, false)]
    [InlineData(2, 0.5f, false)]
    [InlineData(1, 0.5f, false)]
    public void NonpositiveScaleKeyOnTheChain_IsReported(int mode, float lastScaleKey, bool reported)
    {
        var document = Read(ScaleKeyFixture((ushort)mode, lastScaleKey), FnvOptions(false)).Document;

        var clip = Assert.Single(document.Animations);
        Assert.Equal(NifModelAnimationReader.ControllersClipName, clip.Name);
        var restScale = Assert.IsType<SceneBillboardSourceScale>(document.Nodes[1].Billboard!.SourceScale);
        Assert.Equal(1.0, restScale.RestWorldScale);
        var track = Assert.Single(clip.TransformTracks, t => t.Property == SceneTransformProperty.Scale);
        Assert.Equal(0, track.NodeIndex);
        Assert.Contains(lastScaleKey, track.Values);
        var diagnostics = document.Diagnostics.Where(d => d.Code == NifModelBillboardScaleSign.Diagnostic).ToList();
        var entry = BlockPayload(document, 1)["node"]!["billboard"]![NifModelBillboardScaleSign.PayloadKey];
        if (!reported)
        {
            Assert.Empty(diagnostics);
            Assert.Null(entry);
            return;
        }

        var text = DiagnosticText(Assert.Single(diagnostics));
        Assert.Contains("node 1 in clip 0 '(controllers)' (a Scale value of node 0 is", text, StringComparison.Ordinal);
        var occurrence = entry!["reported"]![0]!;
        Assert.False((bool)occurrence["restReported"]!);
        Assert.Equal(1.0, (double)occurrence["restWorldScale"]!);
        Assert.Equal(restScale.RestWorldScale, (double)occurrence["restWorldScale"]!);
        var stated = occurrence["statedScales"]![0]!;
        Assert.Equal(0, (int)stated["node"]!);
        Assert.Equal(0, (int)stated["clip"]!);
        Assert.Equal(NifModelAnimationReader.ControllersClipName, (string?)stated["clipName"]);
        Assert.Equal((double)lastScaleKey, (double)stated["smallest"]!);
    }

    /// <summary>The literal encoding of one effective mode (DESIGN-rev4 section 3 in the contract's names).</summary>
    private static Encoding ExpectedEncoding(int effective)
    {
        return effective switch
        {
            0 => new Encoding(SceneBillboardAim.CameraPlane, null, null, true, Vector3.UnitZ, Vector3.UnitY,
                SceneBillboardRoll.NodeUp, null),
            1 => new Encoding(SceneBillboardAim.CameraPosition, Vector3.UnitY, SceneBillboardAxisFrame.Node, false,
                Vector3.UnitZ, Vector3.UnitY, null, null),
            2 => new Encoding(SceneBillboardAim.CameraPlane, null, null, true, Vector3.UnitZ, Vector3.UnitY,
                SceneBillboardRoll.Camera, null),
            3 => new Encoding(SceneBillboardAim.CameraPosition, null, null, true, Vector3.UnitZ, Vector3.UnitY,
                SceneBillboardRoll.NodeUp, null),
            4 => new Encoding(SceneBillboardAim.CameraPosition, null, null, true, Vector3.UnitZ, Vector3.UnitY,
                SceneBillboardRoll.CameraSwung, null),
            5 => new Encoding(SceneBillboardAim.CameraPosition, Vector3.UnitZ, SceneBillboardAxisFrame.Document, true,
                Vector3.UnitY, Vector3.UnitZ, null, Vector3.UnitX),
            _ => throw new ArgumentOutOfRangeException(nameof(effective), effective, "Effective modes 6 and 7 do not face.")
        };
    }

    /// <summary>One expected encoding.</summary>
    /// <param name="Aim">The aim.</param>
    /// <param name="LockedAxis">The lock, or null.</param>
    /// <param name="Frame">The lock's frame, or null.</param>
    /// <param name="Rigid">The rotation policy.</param>
    /// <param name="Front">The node-local front.</param>
    /// <param name="Up">The node-local up.</param>
    /// <param name="Roll">The roll rule, or null.</param>
    /// <param name="Reflection">The node-local reflection normal, or null.</param>
    private sealed record Encoding(
        SceneBillboardAim Aim,
        Vector3? LockedAxis,
        SceneBillboardAxisFrame? Frame,
        bool Rigid,
        Vector3 Front,
        Vector3 Up,
        SceneBillboardRoll? Roll,
        Vector3? Reflection);
}
