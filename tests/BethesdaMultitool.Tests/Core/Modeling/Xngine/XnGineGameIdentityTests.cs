using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Units;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Xngine;

/// <summary>
///     The identification chain (cut-1c plan section 6.2, D7) on synthetic sources: the three container kinds through
///     the facts query (real archives in temp files opened through the production session), <c>bmt.game</c>,
///     <c>bmt.classic-game</c>, and the content rules with header +20 zero and non-zero; the step precedence, each
///     proved by a control that gives another answer when the earlier step is removed; and the plan's controls:
///     <c>--game battlespire</c> on an 8-byte-only mesh throws, and an 8-byte-only mesh with +20 = 0 is NOT Redguard.
/// </summary>
public sealed class XnGineGameIdentityTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> NoOptions =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly ClassicContainerFixture _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
    }

    [Fact]
    public void AppOptionKeys_AreTheDocumentedNames()
    {
        Assert.Equal("bmt.classic-game", BethesdaModelRegistration.ClassicGameOption);
        Assert.Equal("bmt.classic-game-evidence", BethesdaModelRegistration.ClassicGameEvidenceOption);
        Assert.NotEqual(BethesdaModelRegistration.GameOption, BethesdaModelRegistration.ClassicGameOption);
    }

    // ---- Step 2: the container, through the facts query.

    [Fact]
    public void NumberedBsa_IsDaggerfall_ByContainer_WithTheObjectId()
    {
        var record = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 0);
        var source = _fixture.OpenArchive(_fixture.WriteNumberedBsa((44004u, record), (44005u, record)));
        var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "44005"));

        var identity = XnGineGameIdentity.Resolve(NoOptions, facts, XnGineContentFacts.Measure(record));

        Assert.Equal(BethesdaGame.Daggerfall, identity.Game);
        Assert.Equal(XnGineGameIdentityStep.Container, identity.Step);
        Assert.Equal(XnGineMeshLayout.Daggerfall, identity.Layout);
        Assert.Equal(44005u, identity.ObjectId);
        Assert.True(identity.IsEstablished);
        Assert.Contains("numbered XnGine BSA", identity.Evidence, StringComparison.Ordinal);
        Assert.Contains("record 44005 at index 1 of 2", identity.Evidence, StringComparison.Ordinal);
        Assert.Equal(ClassicModelUnits.DaggerfallMetersPerUnit, identity.Units.MetersPerUnit);
        Assert.Empty(identity.Diagnostics);

        // Control: the same bytes without the container are ambiguous (+20 = 0), so the container is what answered.
        var loose = XnGineGameIdentity.Resolve(NoOptions, null, XnGineContentFacts.Measure(record));
        Assert.Equal(BethesdaGame.Unknown, loose.Game);
        Assert.Null(loose.ObjectId);
    }

    [Fact]
    public void NamedBsaWithLzssEntries_IsBattlespire_ByContainer()
    {
        var record = XnGineTestMeshBuilder.TenByteRecord();
        var source = _fixture.OpenArchive(_fixture.WriteNamedBsa(("HUTVANE.3D", record, true), ("STRAY.BIN", [1, 2, 3], false)));
        var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "HUTVANE.3D"));

        var identity = XnGineGameIdentity.Resolve(NoOptions, facts, XnGineContentFacts.Measure(record));

        Assert.Equal(BethesdaGame.Battlespire, identity.Game);
        Assert.Equal(XnGineGameIdentityStep.Container, identity.Step);
        Assert.Equal(XnGineMeshLayout.Battlespire, identity.Layout);
        Assert.Null(identity.ObjectId);
        Assert.Contains("named XnGine BSA", identity.Evidence, StringComparison.Ordinal);
        Assert.Contains("1 of 2 LZSS entries", identity.Evidence, StringComparison.Ordinal);
        Assert.Equal(ClassicModelUnits.BattlespireMetersPerUnit, identity.Units.MetersPerUnit);
    }

    /// <summary>A named archive without LZSS entries (Daggerfall's BLOCKS/MAPS/MONSTER shape) answers nothing.</summary>
    [Fact]
    public void NamedBsaWithoutLzssEntries_AnswersNothing_AndTheContentDecides()
    {
        var record = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714);
        var source = _fixture.OpenArchive(_fixture.WriteNamedBsa(("THING.3D", record, false)));
        var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "THING.3D"));

        var identity = XnGineGameIdentity.Resolve(NoOptions, facts, XnGineContentFacts.Measure(record));

        Assert.Equal(XnGineGameIdentityStep.Content, identity.Step);
        Assert.Equal(BethesdaGame.Redguard, identity.Game);
        Assert.Contains(identity.Diagnostics, d => d.Code == XnGineGameIdentity.ContainerSilentDiagnostic);
    }

    /// <summary>
    ///     The silent-container diagnostic is raised only when the chain reaches the container step: under an
    ///     accepted <c>bmt.game</c> the container is never consulted, so the same archive raises nothing (and no
    ///     disagreement either, since it gave no answer to disagree with).
    /// </summary>
    [Fact]
    public void NamedBsaWithoutLzssEntries_UnderTheGameOption_RaisesNoContainerDiagnostic()
    {
        var record = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714);
        var source = _fixture.OpenArchive(_fixture.WriteNamedBsa(("THING.3D", record, false)));
        var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "THING.3D"));
        var options = new Dictionary<string, string> { [BethesdaModelRegistration.GameOption] = "daggerfall" };

        var identity = XnGineGameIdentity.Resolve(options, facts, XnGineContentFacts.Measure(record));

        Assert.NotNull(facts);
        Assert.Equal(XnGineGameIdentityStep.GameOption, identity.Step);
        Assert.Equal(BethesdaGame.Daggerfall, identity.Game);
        Assert.Empty(identity.Diagnostics);

        // Control: without the option the same inputs reach the container step and record its silence.
        var byContent = XnGineGameIdentity.Resolve(NoOptions, facts, XnGineContentFacts.Measure(record));
        Assert.Equal(XnGineGameIdentityStep.Content, byContent.Step);
        Assert.Single(byContent.Diagnostics, d => d.Code == XnGineGameIdentity.ContainerSilentDiagnostic);
    }

    [Fact]
    public void Rob_IsRedguard_ByContainer()
    {
        var record = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714);
        var source = _fixture.OpenArchive(_fixture.WriteRob(("GR_COMP", 0u, record), ("BWAGA001", 512u, [])));
        var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "GR_COMP.3D"));

        var identity = XnGineGameIdentity.Resolve(NoOptions, facts, XnGineContentFacts.Measure(record));

        Assert.Equal(BethesdaGame.Redguard, identity.Game);
        Assert.Equal(XnGineGameIdentityStep.Container, identity.Step);
        Assert.Equal(XnGineMeshLayout.Daggerfall, identity.Layout);
        Assert.Contains("Redguard ROB", identity.Evidence, StringComparison.Ordinal);
        Assert.Contains("segment GR_COMP at index 0 of 2 (type 0)", identity.Evidence, StringComparison.Ordinal);
        Assert.Equal(ClassicModelUnits.RedguardMetersPerUnit, identity.Units.MetersPerUnit);
    }

    /// <summary>A container answer the content refutes is skipped with a diagnostic, and the chain goes on.</summary>
    [Fact]
    public void ContainerRefutedByTheContent_IsSkippedWithADiagnostic()
    {
        var record = XnGineTestMeshBuilder.TenByteRecord();
        var source = _fixture.OpenArchive(_fixture.WriteRob(("ODD", 0u, record)));
        var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "ODD.3D"));

        var identity = XnGineGameIdentity.Resolve(NoOptions, facts, XnGineContentFacts.Measure(record));

        Assert.Equal(BethesdaGame.Battlespire, identity.Game);
        Assert.Equal(XnGineGameIdentityStep.Content, identity.Step);
        var refuted = Assert.Single(identity.Diagnostics, d => d.Code == XnGineGameIdentity.StepRefutedDiagnostic);
        Assert.Contains("the container says Redguard", refuted.Message, StringComparison.Ordinal);
        Assert.Contains("8-byte plane headers", refuted.Message, StringComparison.Ordinal);
    }

    // ---- Step 1: bmt.game.

    [Theory]
    [InlineData("daggerfall", BethesdaGame.Daggerfall, 8)]
    [InlineData("Redguard", BethesdaGame.Redguard, 8)]
    [InlineData("BATTLESPIRE", BethesdaGame.Battlespire, 10)]
    public void GameOption_NamingAnXnGineGame_Answers_WithTheOptionQuoted(string value, BethesdaGame game,
        int planeHeaderLength)
    {
        var record = planeHeaderLength == 10
            ? XnGineTestMeshBuilder.TenByteRecord()
            : XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 0);
        var options = new Dictionary<string, string>
        {
            [BethesdaModelRegistration.GameOption] = value,
            [BethesdaModelRegistration.GameEvidenceOption] = "--game " + value
        };

        var identity = XnGineGameIdentity.Resolve(options, null, XnGineContentFacts.Measure(record));

        Assert.Equal(game, identity.Game);
        Assert.Equal(XnGineGameIdentityStep.GameOption, identity.Step);
        Assert.Equal(XnGineGameIdentity.LayoutOf(game), identity.Layout);
        Assert.Equal($"{game} per bmt.game={value} (--game {value})", identity.Evidence);
        Assert.Equal(ClassicModelUnits.For(game).MetersPerUnit, identity.Units.MetersPerUnit);
        Assert.Empty(identity.Diagnostics);
    }

    [Fact]
    public void GameOption_WithoutEvidence_QuotesTheOptionAlone()
    {
        var options = new Dictionary<string, string> { [BethesdaModelRegistration.GameOption] = "redguard" };

        var identity = XnGineGameIdentity.Resolve(options, null,
            XnGineContentFacts.Measure(XnGineTestMeshBuilder.EightByteRecord()));

        Assert.Equal("Redguard per bmt.game=redguard", identity.Evidence);
    }

    [Fact]
    public void GameOption_BeatsTheContainer_AndNotesTheDisagreement()
    {
        var record = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714);
        var source = _fixture.OpenArchive(_fixture.WriteRob(("GR_COMP", 0u, record)));
        var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "GR_COMP.3D"));
        var options = new Dictionary<string, string> { [BethesdaModelRegistration.GameOption] = "daggerfall" };

        var identity = XnGineGameIdentity.Resolve(options, facts, XnGineContentFacts.Measure(record));

        Assert.Equal(BethesdaGame.Daggerfall, identity.Game);
        Assert.Equal(XnGineGameIdentityStep.GameOption, identity.Step);
        var note = Assert.Single(identity.Diagnostics, d => d.Code == XnGineGameIdentity.ContainerDisagreesDiagnostic);
        Assert.Contains("the container says Redguard", note.Message, StringComparison.Ordinal);

        // Control: without the option the same inputs answer Redguard by the container.
        var byContainer = XnGineGameIdentity.Resolve(NoOptions, facts, XnGineContentFacts.Measure(record));
        Assert.Equal(BethesdaGame.Redguard, byContainer.Game);
        Assert.Equal(XnGineGameIdentityStep.Container, byContainer.Step);
    }

    [Fact]
    public void GameOption_Auto_IsTreatedAsAbsent()
    {
        var options = new Dictionary<string, string> { [BethesdaModelRegistration.GameOption] = "auto" };

        var identity = XnGineGameIdentity.Resolve(options, null,
            XnGineContentFacts.Measure(XnGineTestMeshBuilder.TenByteRecord()));

        Assert.Equal(BethesdaGame.Battlespire, identity.Game);
        Assert.Equal(XnGineGameIdentityStep.Content, identity.Step);
    }

    /// <summary>The plan's control: <c>--game battlespire</c> on an 8-byte-only mesh throws (and the mirror image).</summary>
    [Theory]
    [InlineData("battlespire", 8)]
    [InlineData("daggerfall", 10)]
    [InlineData("redguard", 10)]
    public void GameOption_ConflictingWithTheContent_Throws(string value, int planeHeaderLength)
    {
        var record = planeHeaderLength == 10
            ? XnGineTestMeshBuilder.TenByteRecord()
            : XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 5510);
        var options = new Dictionary<string, string> { [BethesdaModelRegistration.GameOption] = value };

        var error = Assert.Throws<ArgumentException>(() =>
            XnGineGameIdentity.Resolve(options, null, XnGineContentFacts.Measure(record)));

        Assert.Contains("plane headers", error.Message, StringComparison.Ordinal);
        Assert.Contains(value, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fnv")]
    [InlineData("FalloutNewVegas")]
    [InlineData("Arena")]
    [InlineData("banana")]
    public void GameOption_NamingNoXnGineGame_Throws(string value)
    {
        var options = new Dictionary<string, string> { [BethesdaModelRegistration.GameOption] = value };

        var error = Assert.Throws<ArgumentException>(() =>
            XnGineGameIdentity.Resolve(options, null, XnGineContentFacts.Measure(XnGineTestMeshBuilder.EightByteRecord())));

        Assert.Contains("names no XnGine game", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => XnGineGameIdentity.ParseGame(value));
        Assert.False(XnGineGameIdentity.TryParseXnGineGame(value, out _));
    }

    // ---- Step 3: bmt.classic-game.

    [Fact]
    public void ClassicGameOption_Answers_AfterTheContainer_AndBeforeTheContent()
    {
        var record = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 5510);
        var options = new Dictionary<string, string>
        {
            [BethesdaModelRegistration.ClassicGameOption] = "Daggerfall",
            [BethesdaModelRegistration.ClassicGameEvidenceOption] = @"install root C:\games\DF\DAGGER"
        };

        var identity = XnGineGameIdentity.Resolve(options, null, XnGineContentFacts.Measure(record));

        Assert.Equal(BethesdaGame.Daggerfall, identity.Game);
        Assert.Equal(XnGineGameIdentityStep.Install, identity.Step);
        Assert.Equal(@"Daggerfall per install: bmt.classic-game=Daggerfall (install root C:\games\DF\DAGGER)",
            identity.Evidence);

        // Control: without the option the content calls the same record Redguard (+20 non-zero).
        var byContent = XnGineGameIdentity.Resolve(NoOptions, null, XnGineContentFacts.Measure(record));
        Assert.Equal(BethesdaGame.Redguard, byContent.Game);
        Assert.Equal(XnGineGameIdentityStep.Content, byContent.Step);

        // Control: a container beats the install option.
        var source = _fixture.OpenArchive(_fixture.WriteNumberedBsa((7u, record)));
        var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "7"));
        var redguardInstall = new Dictionary<string, string>
        {
            [BethesdaModelRegistration.ClassicGameOption] = "Redguard"
        };
        var byContainer = XnGineGameIdentity.Resolve(redguardInstall, facts, XnGineContentFacts.Measure(record));
        Assert.Equal(BethesdaGame.Daggerfall, byContainer.Game);
        Assert.Equal(XnGineGameIdentityStep.Container, byContainer.Step);
    }

    [Fact]
    public void ClassicGameOption_RefutedByTheContent_IsSkippedWithADiagnostic()
    {
        var record = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 5510);
        var options = new Dictionary<string, string>
        {
            [BethesdaModelRegistration.ClassicGameOption] = "Battlespire"
        };

        var identity = XnGineGameIdentity.Resolve(options, null, XnGineContentFacts.Measure(record));

        Assert.Equal(BethesdaGame.Redguard, identity.Game);
        Assert.Equal(XnGineGameIdentityStep.Content, identity.Step);
        var refuted = Assert.Single(identity.Diagnostics, d => d.Code == XnGineGameIdentity.StepRefutedDiagnostic);
        Assert.Contains("the install says Battlespire", refuted.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassicGameOption_NamingANonXnGineInstall_AnswersNothing()
    {
        var options = new Dictionary<string, string>
        {
            [BethesdaModelRegistration.ClassicGameOption] = "Arena"
        };

        var identity = XnGineGameIdentity.Resolve(options, null,
            XnGineContentFacts.Measure(XnGineTestMeshBuilder.TenByteRecord()));

        Assert.Equal(BethesdaGame.Battlespire, identity.Game);
        Assert.Equal(XnGineGameIdentityStep.Content, identity.Step);
        Assert.Contains(identity.Diagnostics, d => d.Code == XnGineGameIdentity.InstallNotXnGineDiagnostic);
    }

    // ---- Step 4: the content.

    [Fact]
    public void Content_TenByteOnly_IsBattlespire()
    {
        var identity = XnGineGameIdentity.Resolve(NoOptions, null,
            XnGineContentFacts.Measure(XnGineTestMeshBuilder.TenByteRecord()));

        Assert.Equal(BethesdaGame.Battlespire, identity.Game);
        Assert.Equal(XnGineGameIdentityStep.Content, identity.Step);
        Assert.Equal(XnGineMeshLayout.Battlespire, identity.Layout);
        Assert.StartsWith("Battlespire per content: walks only with 10-byte plane headers", identity.Evidence,
            StringComparison.Ordinal);
        Assert.Empty(identity.Diagnostics);
    }

    [Fact]
    public void Content_EightByteOnly_WithHeaderPlus20NonZero_IsRedguard()
    {
        var identity = XnGineGameIdentity.Resolve(NoOptions, null,
            XnGineContentFacts.Measure(XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 5510)));

        Assert.Equal(BethesdaGame.Redguard, identity.Game);
        Assert.Equal(XnGineGameIdentityStep.Content, identity.Step);
        Assert.Equal(XnGineMeshLayout.Daggerfall, identity.Layout);
        Assert.Contains("header +20 = 5510", identity.Evidence, StringComparison.Ordinal);
        Assert.Equal(ClassicModelUnits.RedguardMetersPerUnit, identity.Units.MetersPerUnit);
        Assert.Empty(identity.Diagnostics);
    }

    /// <summary>
    ///     The plan's control: an 8-byte-only mesh with +20 = 0 is an extracted ARCH3D record or one of 315 ROB meshes,
    ///     and calling it Redguard would halve a Daggerfall record. It stays Unknown with the diagnostic naming --game.
    /// </summary>
    [Fact]
    public void Content_EightByteOnly_WithHeaderPlus20Zero_IsUnknown_NeverRedguard()
    {
        var identity = XnGineGameIdentity.Resolve(NoOptions, null,
            XnGineContentFacts.Measure(XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 0)));

        Assert.NotEqual(BethesdaGame.Redguard, identity.Game);
        Assert.NotEqual(BethesdaGame.Daggerfall, identity.Game);
        Assert.Equal(BethesdaGame.Unknown, identity.Game);
        Assert.False(identity.IsEstablished);
        Assert.Equal(XnGineGameIdentityStep.None, identity.Step);
        Assert.Equal(XnGineMeshLayout.Daggerfall, identity.Layout);
        Assert.StartsWith(XnGineGameIdentity.NotEstablishedEvidence, identity.Evidence, StringComparison.Ordinal);
        var ambiguous = Assert.Single(identity.Diagnostics, d => d.Code == XnGineGameIdentity.AmbiguousDiagnostic);
        Assert.Contains("--game", ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains("+20 = 0", ambiguous.Message, StringComparison.Ordinal);

        // The guard row: factor one, Unknown provenance, never a Redguard or Daggerfall factor.
        Assert.Equal(1.0, identity.Units.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Unknown, identity.Units.Provenance);
        Assert.NotEqual(ClassicModelUnits.RedguardMetersPerUnit, identity.Units.MetersPerUnit);
    }

    [Fact]
    public void Content_WalkingBothWays_IsUnknown_ReadWithEightByteHeaders()
    {
        var identity = XnGineGameIdentity.Resolve(NoOptions, null,
            XnGineContentFacts.Measure(XnGineTestMeshBuilder.BothWaysRecord()));

        Assert.Equal(BethesdaGame.Unknown, identity.Game);
        Assert.Equal(XnGineMeshLayout.Daggerfall, identity.Layout);
        var ambiguous = Assert.Single(identity.Diagnostics, d => d.Code == XnGineGameIdentity.AmbiguousDiagnostic);
        Assert.Contains("both plane-header sizes", ambiguous.Message, StringComparison.Ordinal);

        // Any --game value settles it, because every layout walks.
        foreach (var game in new[] { BethesdaGame.Daggerfall, BethesdaGame.Battlespire, BethesdaGame.Redguard })
        {
            var options = new Dictionary<string, string> { [BethesdaModelRegistration.GameOption] = game.ToString() };
            var settled = XnGineGameIdentity.Resolve(options, null,
                XnGineContentFacts.Measure(XnGineTestMeshBuilder.BothWaysRecord()));
            Assert.Equal(game, settled.Game);
            Assert.Equal(XnGineGameIdentity.LayoutOf(game), settled.Layout);
        }
    }

    [Fact]
    public void Content_WalkingNeitherWay_Throws()
    {
        var facts = XnGineContentFacts.Measure(XnGineTestMeshBuilder.EightByteRecord(tag: "v5.0"));
        Assert.True(facts.WalksWithNeitherLayout);

        var error = Assert.Throws<InvalidDataException>(() => XnGineGameIdentity.Resolve(NoOptions, null, facts));
        Assert.Contains("v5.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutOf_FollowsTheGame()
    {
        Assert.Equal(XnGineMeshLayout.Daggerfall, XnGineGameIdentity.LayoutOf(BethesdaGame.Daggerfall));
        Assert.Equal(XnGineMeshLayout.Daggerfall, XnGineGameIdentity.LayoutOf(BethesdaGame.Redguard));
        Assert.Equal(XnGineMeshLayout.Battlespire, XnGineGameIdentity.LayoutOf(BethesdaGame.Battlespire));
        Assert.Throws<ArgumentOutOfRangeException>(() => XnGineGameIdentity.LayoutOf(BethesdaGame.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => XnGineGameIdentity.LayoutOf(BethesdaGame.Arena));
    }
}
