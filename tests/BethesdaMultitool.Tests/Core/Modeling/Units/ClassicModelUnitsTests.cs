using System.Globalization;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Modeling.Units;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Units;

/// <summary>
///     The classic unit rows (cut-1c plan section 6.3): literal pins for 1/10240, 1/20480 and 2^-14, each row Assumed
///     with the design's evidence and its RE item, the Unknown guard row, the Redguard actor row, and the control that
///     applying the Daggerfall factor to a Redguard document fails the pin (the two factors differ by exactly 2x, which
///     is the error the plan's ambiguity rule exists to prevent).
/// </summary>
public class ClassicModelUnitsTests
{
    /// <summary>The literal pins. A recomputed or transposed factor fails here before any document is read.</summary>
    [Fact]
    public void Factors_ArePinnedLiterals()
    {
        Assert.Equal(1.0 / 10240, ClassicModelUnits.DaggerfallMetersPerUnit);
        Assert.Equal(1.0 / 20480, ClassicModelUnits.RedguardMetersPerUnit);
        Assert.Equal(1.0 / 16384, ClassicModelUnits.BattlespireMetersPerUnit);
        Assert.Equal(Math.ScaleB(1.0, -14), ClassicModelUnits.BattlespireMetersPerUnit);
        Assert.Equal(0.025 / 256, ClassicModelUnits.DaggerfallMetersPerUnit, 15);
        Assert.Equal(0.0125 / 256, ClassicModelUnits.RedguardMetersPerUnit, 15);
        Assert.Equal(1.0 / 64 / 256, ClassicModelUnits.BattlespireMetersPerUnit);
        Assert.Equal(1.0, ClassicModelUnits.UnknownMetersPerUnit);

        // The three rows are distinct, and Daggerfall is exactly twice Redguard (the halving the plan guards against).
        Assert.Equal(2.0, ClassicModelUnits.DaggerfallMetersPerUnit / ClassicModelUnits.RedguardMetersPerUnit);
        Assert.NotEqual(ClassicModelUnits.BattlespireMetersPerUnit, ClassicModelUnits.DaggerfallMetersPerUnit);
        Assert.NotEqual(ClassicModelUnits.BattlespireMetersPerUnit, ClassicModelUnits.RedguardMetersPerUnit);
    }

    [Theory]
    [InlineData(BethesdaGame.Daggerfall, 1.0 / 10240, "RE-2")]
    [InlineData(BethesdaGame.Redguard, 1.0 / 20480, "RE-3")]
    [InlineData(BethesdaGame.Battlespire, 1.0 / 16384, "RE-4")]
    public void For_AGame_IsTheRowsFactor_Assumed_WithTheDesignEvidence(BethesdaGame game, double metersPerUnit,
        string reverseEngineeringItem)
    {
        var units = ClassicModelUnits.For(game);

        Assert.Equal(metersPerUnit, units.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Assumed, units.Provenance);
        Assert.NotNull(units.Evidence);
        Assert.Contains(reverseEngineeringItem, units.Evidence, StringComparison.Ordinal);
        Assert.Contains("1/256 world unit per native unit", units.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void For_Unknown_IsTheGuardRow()
    {
        var units = ClassicModelUnits.For(BethesdaGame.Unknown);

        Assert.Equal(1.0, units.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Unknown, units.Provenance);
        Assert.Equal(ClassicModelUnits.UnknownEvidence, units.Evidence);
        Assert.Contains("--game", units.Evidence, StringComparison.Ordinal);
        Assert.Contains("not a claim of meters", units.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BethesdaGame.Arena)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Morrowind)]
    public void For_AGameWithoutARow_Throws(BethesdaGame game)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassicModelUnits.For(game));
    }

    [Fact]
    public void ForRedguardActor_KeepsTheRedguardFactor_AndStatesTheActorScaleAssumption()
    {
        var units = ClassicModelUnits.ForRedguardActor();

        Assert.Equal(ClassicModelUnits.RedguardMetersPerUnit, units.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Assumed, units.Provenance);
        Assert.Contains("human actors only", units.Evidence, StringComparison.Ordinal);
        Assert.Contains(ClassicModelUnits.ActorScaleDiagnostic, units.Evidence, StringComparison.Ordinal);
        Assert.Contains("32,766", units.Evidence, StringComparison.Ordinal);
        Assert.Equal("bmt.redguard.3dc.actor-scale-unknown", ClassicModelUnits.ActorScaleDiagnostic);
        Assert.Contains("normalized to the int16 range", ClassicModelUnits.ActorScaleDiagnosticMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_AreTheDesignTable_GuardLast()
    {
        var rows = ClassicModelUnits.Rows;

        Assert.Equal(5, rows.Count);
        Assert.Equal(
            new[]
            {
                "XnGine .3D, Daggerfall ARCH3D", "XnGine .3D, Redguard loose and ROB", "Redguard .3DC actors",
                "XnGine .3D, Battlespire", "Game unknown"
            },
            rows.Select(r => r.Name).ToArray());
        Assert.Equal(new[] { "RE-2", "RE-3", "RE-3", "RE-4", "none" },
            rows.Select(r => r.ReverseEngineeringItem).ToArray());
        Assert.Equal(BethesdaGame.Unknown, rows[^1].Game);
        Assert.All(rows.Take(4), r => Assert.Equal(SceneValueProvenance.Assumed, r.Provenance));

        // Each row's SceneUnits equals what For() returns for its game (the actor row against ForRedguardActor()).
        Assert.Equal(ClassicModelUnits.For(BethesdaGame.Daggerfall).MetersPerUnit, rows[0].ToSceneUnits().MetersPerUnit);
        Assert.Equal(ClassicModelUnits.For(BethesdaGame.Redguard).Evidence, rows[1].ToSceneUnits().Evidence);
        Assert.Equal(ClassicModelUnits.ForRedguardActor().Evidence, rows[2].ToSceneUnits().Evidence);
        Assert.Equal(ClassicModelUnits.For(BethesdaGame.Battlespire).Evidence, rows[3].ToSceneUnits().Evidence);
        Assert.Equal(ClassicModelUnits.For(BethesdaGame.Unknown).Evidence, rows[4].ToSceneUnits().Evidence);
    }

    [Fact]
    public void DescribeRows_NamesEveryRowWithItsFactorProvenanceAndItem()
    {
        var text = ClassicModelUnits.DescribeRows();

        foreach (var row in ClassicModelUnits.Rows)
        {
            Assert.Contains(row.Name + ": " + row.MetersPerUnit.ToString("R", CultureInfo.InvariantCulture) +
                            " m per native unit | " + row.Provenance + " | " + row.ReverseEngineeringItem,
                text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The slice-3 control: a Daggerfall factor applied to a Redguard document fails the Redguard pin. The Redguard
    ///     actor row's own control (design section 4.1): CYRSA001 spans 32,766 native units in Y, which is 1.60 m at
    ///     1/20480 and 3.20 m at 1/10240; a reader that substituted the Daggerfall row would double every Redguard actor.
    ///     ⚠ This is the CONSTANT-TABLE form: <c>IsPinnedFactor</c> compares the table with itself and can only catch a
    ///     transposition inside <see cref="ClassicModelUnits.For" />. The chain-level form is
    ///     <c>XnGineGameIdentityTests.Content_EightByteOnly_WithHeaderPlus20NonZero_IsRedguard</c>; the DOCUMENT-level
    ///     forms (a Redguard <c>ModelDocument</c> whose units are the Daggerfall row fails, and the DOGA001 extent) are
    ///     owed by slices 5 and 6, which must not count this test as having covered them.
    /// </summary>
    [Fact]
    public void DaggerfallFactor_AppliedToARedguardDocument_FailsThePin()
    {
        const int cyrsaExtentY = 32766;
        var redguard = ClassicModelUnits.For(BethesdaGame.Redguard);

        Assert.True(IsPinnedFactor(BethesdaGame.Redguard, redguard.MetersPerUnit));
        Assert.False(IsPinnedFactor(BethesdaGame.Redguard, ClassicModelUnits.DaggerfallMetersPerUnit));
        Assert.False(IsPinnedFactor(BethesdaGame.Daggerfall, redguard.MetersPerUnit));

        var redguardHeight = cyrsaExtentY * redguard.MetersPerUnit;
        var daggerfallHeight = cyrsaExtentY * ClassicModelUnits.DaggerfallMetersPerUnit;
        Assert.InRange(redguardHeight, 1.59, 1.61);
        Assert.NotInRange(daggerfallHeight, 1.59, 1.61);
        Assert.Equal(2.0, daggerfallHeight / redguardHeight);
    }

    private static bool IsPinnedFactor(BethesdaGame game, double metersPerUnit)
    {
        return metersPerUnit == ClassicModelUnits.For(game).MetersPerUnit;
    }
}
