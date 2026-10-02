using System.Globalization;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Games;

/// <summary>
///     Pins unit provenance (docs/design/model-document-design-20260923.md §4.1, §9 row 10): every
///     profile states its world unit WITH its grounds: FNV, FO3, Oblivion and Skyrim carry the value
///     read from their executables (RE-1), Fallout 4 and 76 the same chain Assumed, Morrowind, Unknown
///     and Starfield Assumed values, the classics the viewer convention, and the value type refuses
///     a number without evidence or a scale that is not finite and positive. <c>Describe()</c> is
///     the line <c>mesh info</c> and the fidelity report print, so its format and its culture
///     invariance are pinned to literals.
/// </summary>
public class WorldUnitScaleTests
{
    private const double GamebryoMetersPerUnit = 1.0 / 70.0;

    private const string ReverseEngineeredEvidence =
        "FalloutNV.exe sha256 0000000000000000000000000000000000000000000000000000000000000000 @ 0x00000000: 7.0";

    [Fact]
    public void EveryProfile_CarriesUnits_WithProvenanceAndEvidence()
    {
        // Every enum value, Unknown included: the registry plus the neutral fallback For(Unknown) returns.
        var profiles = GameProfiles.All.Append(GameProfiles.For(BethesdaGame.Unknown)).ToList();
        Assert.Equal(Enum.GetValues<BethesdaGame>().Length, profiles.Count);

        foreach (var profile in profiles)
        {
            Assert.NotNull(profile.Units);
            Assert.True(double.IsFinite(profile.Units.MetersPerUnit) && profile.Units.MetersPerUnit > 0,
                $"{profile.Game}: meters per unit must be finite and positive");
            Assert.True(Enum.IsDefined(profile.Units.Provenance), $"{profile.Game}: undefined provenance");
            Assert.False(string.IsNullOrWhiteSpace(profile.Units.Evidence), $"{profile.Game}: blank evidence");
        }
    }

    /// <summary>
    ///     Design §4.1 row 1 after RE-1 (docs/world_scale_units_re1.md): the executables define 128 units
    ///     = 6 feet. FNV and Skyrim carry 1 / 69.99125 m, FO3 and Oblivion 1 / 69.9904 m, both
    ///     ReverseEngineered; Fallout 4 and 76 carry the Skyrim chain Assumed; Morrowind and Unknown keep
    ///     the 1/70 convention Assumed. The literals are pinned here, not derived from the profiles.
    /// </summary>
    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, 1.0 / 69.99125, UnitProvenance.ReverseEngineered, "FalloutNV.exe", "0.0142875 m per unit | ReverseEngineered | ")]
    [InlineData(BethesdaGame.Skyrim, 1.0 / 69.99125, UnitProvenance.ReverseEngineered, "TESV.exe", "0.0142875 m per unit | ReverseEngineered | ")]
    [InlineData(BethesdaGame.Fallout3, 1.0 / 69.9904, UnitProvenance.ReverseEngineered, "Fallout3.exe", "0.0142877 m per unit | ReverseEngineered | ")]
    [InlineData(BethesdaGame.Oblivion, 1.0 / 69.9904, UnitProvenance.ReverseEngineered, "Oblivion.exe", "0.0142877 m per unit | ReverseEngineered | ")]
    [InlineData(BethesdaGame.Fallout4, 1.0 / 69.99125, UnitProvenance.Assumed, "not read", "0.0142875 m per unit | Assumed | ")]
    [InlineData(BethesdaGame.Fallout76, 1.0 / 69.99125, UnitProvenance.Assumed, "not read", "0.0142875 m per unit | Assumed | ")]
    [InlineData(BethesdaGame.Morrowind, 1.0 / 70.0, UnitProvenance.Assumed, "RE-1", "0.0142857 m per unit | Assumed | ")]
    [InlineData(BethesdaGame.Unknown, 1.0 / 70.0, UnitProvenance.Assumed, "RE-1", "0.0142857 m per unit | Assumed | ")]
    public void GamebryoAndCreationProfiles_CarryTheReadOrAssumedUnit(
        BethesdaGame game, double metersPerUnit, UnitProvenance provenance, string evidenceFragment, string linePrefix)
    {
        var units = GameProfiles.For(game).Units;

        Assert.Equal(metersPerUnit, units.MetersPerUnit, 1e-12);
        Assert.Equal(provenance, units.Provenance);
        Assert.Contains(evidenceFragment, units.Evidence, StringComparison.Ordinal);
        Assert.StartsWith(linePrefix, units.Describe(), StringComparison.Ordinal);
        Assert.EndsWith(units.Evidence, units.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A reverse-engineered value must be re-checkable: its evidence names an executable, a hash
    ///     and an address, and points at the findings document (design §4.3: "executable name,
    ///     SHA-256, address, value").
    /// </summary>
    [Fact]
    public void ReverseEngineeredUnits_NameExecutableHashAddressAndDocument()
    {
        var read = GameProfiles.All.Where(p => p.Units.Provenance == UnitProvenance.ReverseEngineered).ToList();
        Assert.Equal(4, read.Count);

        foreach (var profile in read)
        {
            Assert.Contains(".exe", profile.Units.Evidence, StringComparison.Ordinal);
            Assert.Contains("sha256", profile.Units.Evidence, StringComparison.Ordinal);
            Assert.Contains("0x", profile.Units.Evidence, StringComparison.Ordinal);
            Assert.Contains("docs/world_scale_units_re1.md", profile.Units.Evidence, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The measured unit and the viewer convention differ by design: the camera keeps 70 units per
    ///     meter (HumanScaleFactor exactly 1) while the export unit is the read value, 0.0125% away.
    /// </summary>
    [Fact]
    public void ViewerConvention_StaysSeventy_WhileTheMeasuredUnitIsTheReadValue()
    {
        var fnv = GameProfiles.For(BethesdaGame.FalloutNewVegas);

        Assert.Equal(70f, fnv.ViewerUnitsPerMeter);
        Assert.Equal(1f, GameProfiles.HumanScaleFactor(BethesdaGame.FalloutNewVegas));
        Assert.Equal(69.99125, fnv.Units.UnitsPerMeter, 1e-9);
        Assert.NotEqual(70.0, fnv.Units.UnitsPerMeter);
    }

    /// <summary>Design §4.1 row 2: Starfield is metric, Assumed from retail mesh bounds.</summary>
    [Fact]
    public void Starfield_IsOneMeterPerUnit_Assumed()
    {
        var units = GameProfiles.For(BethesdaGame.Starfield).Units;

        Assert.Equal(1.0, units.MetersPerUnit);
        Assert.Equal(1.0, units.UnitsPerMeter);
        Assert.Equal(UnitProvenance.Assumed, units.Provenance);
        Assert.Contains("RE-1", units.Evidence, StringComparison.Ordinal);
        Assert.StartsWith("1 m per unit | Assumed | ", units.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The classic games have no per-game measurement here — their formats' units are design
    ///     §4.1 rows for the cut-1c per-format registry — so their profiles carry the viewer's
    ///     classic-unit assumption and SAY it is one. The value must equal the Gamebryo unit exactly
    ///     (same double), because that is what keeps <see cref="GameProfiles.HumanScaleFactor" /> a
    ///     bit-exact 1 for them, which the camera relies on.
    /// </summary>
    [Fact]
    public void ClassicProfiles_StateTheViewerAssumption_AndKeepHumanScaleAtOne()
    {
        var classics = GameProfiles.All.Where(p => p.Engine == EngineFamily.None).ToList();
        Assert.NotEmpty(classics);

        foreach (var profile in classics)
        {
            // Pinned to the literal 1/70, not to the FNV profile's value: both production values
            // derive from the same constant, so comparing them could never see it change.
            Assert.Equal(GamebryoMetersPerUnit, profile.Units.MetersPerUnit);
            Assert.Equal(UnitProvenance.Assumed, profile.Units.Provenance);
            Assert.StartsWith("Not a measurement of this game", profile.Units.Evidence, StringComparison.Ordinal);
            Assert.Equal(1f, GameProfiles.HumanScaleFactor(profile.Game));
        }

        // Each classic still names its own format's situation, so the shared prefix is not the whole story.
        Assert.Contains("RE-2", GameProfiles.For(BethesdaGame.Daggerfall).Units.Evidence, StringComparison.Ordinal);
        Assert.Contains("RE-3", GameProfiles.For(BethesdaGame.Redguard).Units.Evidence, StringComparison.Ordinal);
        Assert.Contains("RE-6", GameProfiles.For(BethesdaGame.Shadowkey).Units.Evidence, StringComparison.Ordinal);
        Assert.Contains("RE-8", GameProfiles.For(BethesdaGame.FalloutBrotherhoodOfSteel).Units.Evidence,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     The float the camera consumes is the viewer convention, pinned to the exact literals the old
    ///     init-only property held: 70 everywhere but Starfield, 1 there.
    /// </summary>
    [Fact]
    public void ViewerUnits_PinTheCameraConvention()
    {
        Assert.Equal(70f, GameProfiles.For(BethesdaGame.FalloutNewVegas).ViewerUnitsPerMeter);
        Assert.Equal(70f, GameProfiles.For(BethesdaGame.Daggerfall).ViewerUnitsPerMeter);
        Assert.Equal(1f, GameProfiles.For(BethesdaGame.Starfield).ViewerUnitsPerMeter);
    }

    /// <summary>
    ///     <c>Describe()</c> must print a period under a comma-decimal culture. The premise check
    ///     proves the culture switch took effect on this thread — without it a silently ignored
    ///     switch would let a culture-sensitive implementation pass.
    /// </summary>
    [Fact]
    public void Describe_UsesInvariantCulture()
    {
        var units = new WorldUnitScale(
            GamebryoMetersPerUnit,
            UnitProvenance.Assumed,
            "Gamebryo 70 units per meter; executable read pending (RE-1)");

        var previousCulture = CultureInfo.CurrentCulture;
        string line;
        string premise;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            premise = GamebryoMetersPerUnit.ToString("0.#######", CultureInfo.CurrentCulture);
            line = units.Describe();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        Assert.Equal("0,0142857", premise);
        Assert.Equal(
            "0.0142857 m per unit | Assumed | Gamebryo 70 units per meter; executable read pending (RE-1)",
            line);
    }

    [Theory]
    [InlineData(0.025, 40.0)]
    [InlineData(1.0 / 20480, 20480.0)]
    [InlineData(1.0, 1.0)]
    public void UnitsPerMeter_IsTheReciprocal(double metersPerUnit, double expectedUnitsPerMeter)
    {
        var units = new WorldUnitScale(metersPerUnit, UnitProvenance.Assumed, "synthetic");
        Assert.Equal(expectedUnitsPerMeter, units.UnitsPerMeter, 1e-9);
    }

    /// <summary>
    ///     The guard: a reverse-engineered number without the executable, hash, address and value
    ///     it was read from is not a reverse-engineered number, and the type refuses it. The same
    ///     rule holds for the other provenances, since an assumption with no grounds is a bare guess.
    /// </summary>
    [Theory]
    [InlineData(UnitProvenance.ReverseEngineered, "")]
    [InlineData(UnitProvenance.ReverseEngineered, "   ")]
    [InlineData(UnitProvenance.Assumed, "")]
    [InlineData(UnitProvenance.Authored, "")]
    public void BlankEvidence_IsRejected(UnitProvenance provenance, string evidence)
    {
        var error = Assert.Throws<ArgumentException>(() => new WorldUnitScale(0.5, provenance, evidence));
        Assert.Equal("evidence", error.ParamName);
    }

    [Fact]
    public void NullEvidence_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new WorldUnitScale(0.5, UnitProvenance.ReverseEngineered, null!));
    }

    /// <summary>The positive half of the guard, so it cannot be "everything throws".</summary>
    [Fact]
    public void ReverseEngineered_WithEvidence_Constructs_AndPrintsItsProvenance()
    {
        var units = new WorldUnitScale(1.0 / 70, UnitProvenance.ReverseEngineered, ReverseEngineeredEvidence);

        Assert.Equal(UnitProvenance.ReverseEngineered, units.Provenance);
        Assert.Equal(ReverseEngineeredEvidence, units.Evidence);
        Assert.Equal(
            "0.0142857 m per unit | ReverseEngineered | " + ReverseEngineeredEvidence,
            units.Describe());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonPositiveOrNonFiniteScale_IsRejected(double metersPerUnit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorldUnitScale(metersPerUnit, UnitProvenance.Assumed, "synthetic"));
    }

    /// <summary>
    ///     Validation lives in the init accessors, so a <c>with</c> expression cannot smuggle a bad
    ///     value past the constructor.
    /// </summary>
    [Fact]
    public void With_RevalidatesScaleAndEvidence()
    {
        var units = GameProfiles.For(BethesdaGame.FalloutNewVegas).Units;

        Assert.Throws<ArgumentOutOfRangeException>(() => units with { MetersPerUnit = 0 });
        Assert.Throws<ArgumentException>(() => units with { Evidence = " " });

        var rescaled = units with { MetersPerUnit = 1.0, Evidence = "synthetic" };
        Assert.Equal(1.0, rescaled.MetersPerUnit);
        Assert.Equal("synthetic", rescaled.Evidence);
        // The member the expression left alone is copied as is: FNV's unit is ReverseEngineered (RE-1).
        Assert.Equal(UnitProvenance.ReverseEngineered, rescaled.Provenance);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        var a = new WorldUnitScale(0.025, UnitProvenance.Assumed, "synthetic");
        var b = new WorldUnitScale(0.025, UnitProvenance.Assumed, "synthetic");
        var c = new WorldUnitScale(0.025, UnitProvenance.ReverseEngineered, "synthetic");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
