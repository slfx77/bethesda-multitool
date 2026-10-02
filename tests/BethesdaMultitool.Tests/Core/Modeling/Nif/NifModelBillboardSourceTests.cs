using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The source key of a NIF read decides how well the RE-25 facing and the mirror-cull reflected-face rule are
///     established for the file in hand, never the encoding: the provenance per stream kind, the verbatim evidence
///     (which names the builds examined for the key, including that no PlayStation 3 build was), the FNV PC-only
///     plane-fallback edge, and the document's <see cref="SceneReflectedFaces" />. Every expectation is a literal.
/// </summary>
public class NifModelBillboardSourceTests
{
    /// <summary>The literal FNV PC plane-fallback edge, to its ten printed digits (law_check_rev4.txt L6).</summary>
    private const double FnvPcEdgeTenDigits = 1.8433998931e-3;

    /// <summary>
    ///     One row per source key: byte order, the game and platform options (null = not set), the expected facing,
    ///     schedule and reflected-face provenances (null reflected = not declared), whether the edge is declared, a phrase
    ///     the facing evidence must contain and a phrase the reflected-face evidence must contain (null when none).
    /// </summary>
    public static TheoryData<bool, string?, string?, string, string, string?, bool, string, string?> Keys()
    {
        const string re = nameof(SceneValueProvenance.ReverseEngineered);
        const string assumed = nameof(SceneValueProvenance.Assumed);
        return new TheoryData<bool, string?, string?, string, string, string?, bool, string, string?>
        {
            { false, "fnv", null, re, re, re, true, "This read is FNV PC", "This read is FNV PC, the build examined." },
            { false, "FalloutNewVegas", null, re, re, re, true, "D3DCREATE_FPU_PRESERVE", "set by nothing" },
            { false, "fo3", null, re, re, assumed, false, "This read is Fallout 3 PC", "whose cull path was not examined" },
            { false, null, null, re, re, assumed, false, "with the game not established", "may be a Fallout 3 file" },
            { false, "auto", null, re, re, assumed, false, "with the game not established", "may be a Fallout 3 file" },
            { true, "fnv", "x360", re, re, re, false, "Xbox 360 stream (bmt.platform=x360)", "no caller of SetLeftRightSwap" },
            { true, "fnv", "ps3", re, re, assumed, false, "no PS3 build of NiBillboardNode was examined", "(bmt.platform=ps3)" },
            { true, "fnv", null, re, re, assumed, false, "bmt.platform not set, so Xbox 360 is assumed", "may be a PlayStation 3 file" },
            { true, "fo3", "x360", re, re, assumed, false, "No Fallout 3 console build was examined.", "No Fallout 3 console build" },
            { true, null, "x360", re, re, assumed, false, "The game is not established, and no Fallout 3 console", "The game is not established" },
            { false, "Skyrim", null, assumed, assumed, null, false, "names Skyrim, not Fallout: New Vegas or Fallout 3", null },
            { true, "Oblivion", "x360", assumed, assumed, null, false, "names Oblivion, not Fallout: New Vegas or Fallout 3", null }
        };
    }

    /// <summary>
    ///     A RIGID_FACE_CENTER fixture (it carries both thresholds and the edge) read under each source key: the facing,
    ///     schedule and reflected-face provenances, the edge only for FNV PC, the rule DrawnWinding wherever it is
    ///     declared, the RE-25 or mirror-cull citation at the head of each ReverseEngineered evidence, the key's own
    ///     phrase, the x87 premise only with the edge, and one diagnostic exactly when the facing is Assumed.
    ///     The rows are their own controls: the FNV PC row without its game (row 4) loses the edge, and the X360 row
    ///     without its platform (row 8) loses the ReverseEngineered reflected-face rule; a key-blind reader passes neither.
    /// </summary>
    [Theory]
    [MemberData(nameof(Keys))]
    public void EverySourceKey_GetsItsProvenance(bool bigEndian, string? game, string? platform, string facing,
        string schedule, string? reflected, bool edge, string facingPhrase, string? cullPhrase)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (game is not null)
        {
            options[BethesdaModelRegistration.GameOption] = game;
        }

        if (platform is not null)
        {
            options[BethesdaModelRegistration.PlatformOption] = platform;
        }

        var document = Read(NifModelBillboardTests.Fixture(4, bigEndian: bigEndian), options).Document;
        var billboard = Assert.IsType<SceneBillboard>(document.Nodes[1].Billboard);

        Assert.Equal(facing, billboard.FacingProvenance.ToString());
        Assert.Equal(schedule, billboard.ScheduleProvenance.ToString());
        Assert.Contains(facingPhrase, billboard.FacingEvidence, StringComparison.Ordinal);
        Assert.Equal(edge, billboard.PlaneFallbackEdge is not null);
        Assert.Equal(edge, billboard.FacingEvidence!.Contains("D3DCREATE_FPU_PRESERVE", StringComparison.Ordinal));
        if (billboard.PlaneFallbackEdge is { } declared)
        {
            Assert.True(Math.Abs(declared - FnvPcEdgeTenDigits) <= 5e-14, $"edge {declared:R}");
        }

        var assumedFacing = facing == nameof(SceneValueProvenance.Assumed);
        Assert.Equal(assumedFacing ? 1 : 0,
            document.Diagnostics.Count(d => d.Code == NifModelLayerReader.BillboardDiagnostic));
        if (!assumedFacing)
        {
            Assert.StartsWith("RE-25 (docs/formats/nif-animation-engine-behavior-20260925.md", billboard.FacingEvidence,
                StringComparison.Ordinal);
            Assert.StartsWith("RE-25 item 1 and verifier 3d", billboard.ScheduleEvidence, StringComparison.Ordinal);
        }

        if (reflected is null)
        {
            Assert.Null(document.ReflectedFaces);
            return;
        }

        var faces = Assert.IsType<SceneReflectedFaces>(document.ReflectedFaces);
        Assert.Equal(SceneReflectedFaceRule.DrawnWinding, faces.Rule);
        Assert.Equal(reflected, faces.Provenance.ToString());
        Assert.StartsWith("Mirror cull RE (docs/formats/fnv-pc-framebuffer-and-mirror-cull-20260927.md", faces.Evidence,
            StringComparison.Ordinal);
        Assert.Contains(cullPhrase!, faces.Evidence, StringComparison.Ordinal);
        Assert.Equal(reflected == nameof(SceneValueProvenance.Assumed),
            faces.Evidence!.Contains("no PlayStation 3 build was examined", StringComparison.Ordinal));
    }

    /// <summary>
    ///     No two keys share an evidence text, so a row read on its own says which builds stand behind it. Control: the
    ///     facing evidence differs between FNV PC and FNV X360 even though both are ReverseEngineered.
    /// </summary>
    [Fact]
    public void EveryKey_HasItsOwnEvidence()
    {
        var sources = new List<NifModelBillboardSource>();
        foreach (var bigEndian in new[] { false, true })
        {
            foreach (var game in new BethesdaGame?[] { BethesdaGame.FalloutNewVegas, BethesdaGame.Fallout3, null })
            {
                foreach (var platform in new[] { "x360", "ps3", null })
                {
                    if (!bigEndian && platform is not null)
                    {
                        continue;
                    }

                    sources.Add(new NifModelBillboardSource(NifModelProbe.SupportedVersion,
                        NifModelProbe.SupportedUserVersion, 34, bigEndian, game, Platform(platform)));
                }
            }
        }

        Assert.Equal(12, sources.Count);
        Assert.Equal(sources.Count, sources.Select(s => s.FacingEvidence).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(sources.Count, sources.Select(s => s.Description).Distinct(StringComparer.Ordinal).Count());
        Assert.All(sources, s => Assert.Equal(SceneValueProvenance.ReverseEngineered, s.FacingProvenance));
        Assert.NotEqual(sources[0].FacingEvidence, sources[3].FacingEvidence);
    }

    /// <summary>
    ///     A stream other than the FNV/Fallout 3 one (unreachable through the reader today, whose key admits only
    ///     20.2.0.7, user 11) keeps the encoding but is Assumed, with no edge and no reflected-face rule. Control: the
    ///     same key at 20.2.0.7 is ReverseEngineered.
    /// </summary>
    [Fact]
    public void AnotherStream_IsAssumed_WithTheSameEncoding()
    {
        var other = new NifModelBillboardSource(0x14000005, 11, 34, false, BethesdaGame.FalloutNewVegas,
            Platform(null));
        var fallout = new NifModelBillboardSource(NifModelProbe.SupportedVersion, NifModelProbe.SupportedUserVersion,
            34, false, BethesdaGame.FalloutNewVegas, Platform(null));

        Assert.False(other.IsFalloutStream);
        Assert.Equal(SceneValueProvenance.Assumed, other.FacingProvenance);
        Assert.Equal(SceneValueProvenance.Assumed, other.ScheduleProvenance);
        Assert.Null(other.PlaneFallbackEdge);
        Assert.Null(other.ReflectedFaces);
        Assert.Contains("NIF 20.0.0.5, user 11, BS 34", other.FacingEvidence, StringComparison.Ordinal);
        Assert.NotNull(other.AssumedReason);
        Assert.True(fallout.IsFalloutStream);
        Assert.Equal(SceneValueProvenance.ReverseEngineered, fallout.FacingProvenance);
        Assert.Null(fallout.AssumedReason);

        var assumed = Assert.IsType<SceneBillboard>(NifModelBillboards.Map(4, other));
        var established = Assert.IsType<SceneBillboard>(NifModelBillboards.Map(4, fallout));
        Assert.Equal(established.Aim, assumed.Aim);
        Assert.Equal(established.Roll, assumed.Roll);
        Assert.Equal(established.PlaneFallbackCosine, assumed.PlaneFallbackCosine);
        Assert.Null(assumed.PlaneFallbackEdge);
        Assert.NotNull(established.PlaneFallbackEdge);
        Assert.Equal(SceneValueProvenance.Assumed, assumed.FacingProvenance);
    }

    /// <summary>A platform selection as the reader resolves it from an optional option value.</summary>
    private static NifPackedPlatformSelection Platform(string? value)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (value is not null)
        {
            options[BethesdaModelRegistration.PlatformOption] = value;
        }

        return NifPackedPlatformOption.Resolve(options);
    }
}
