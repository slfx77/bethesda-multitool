using BethesdaMultitool.Core.Actors;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Actors;

public sealed class ActorLevelCalculationTests
{
    // Boundary expectations follow the retained 0047DED0 instruction sequence. They are
    // reconstruction tests, not a claim that these scaled cases were observed in a live game.
    [Theory]
    [InlineData(1500, false, 9, 20, 2, 1500)] // fixed ignores multiplier bounds
    [InlineData(1500, true, 9, 0, 0, 13)] // FISTP uses truncation, not nearest
    [InlineData(1300, true, 10, 0, 0, 13)] // observed x87 24-bit FMUL rounds before FISTP
    [InlineData(500, true, 1, 0, 0, 0)] // no invented minimum of one
    [InlineData(1500, true, 9, 20, 2, 20)] // lower clamp returns before upper clamp
    [InlineData(1500, true, 9, 0, 10, 10)]
    [InlineData(-1, false, 1, 0, 0, 65535)] // engine consumes UInt16 bits
    [InlineData(-1, true, 1000, 0, 0, 65535)]
    public void ReconstructsObservedInstructionOrdering(short raw, bool scaled, ushort player,
        ushort minimum, ushort maximum, ushort expected)
    {
        Assert.Equal(expected, ActorLevelCalculation.Calculate(raw, scaled, player, minimum, maximum));
    }

    [Fact]
    public void InheritedLevelUsesResolvedStatsOwnerAndReportsItsInputs()
    {
        var collection = new RecordCollection { Game = BethesdaGame.FalloutNewVegas };
        collection.Npcs.Add(new NpcRecord { FormId = 1, Template = 2,
            Stats = Stats(0, 50, 0, 0, 2) });
        collection.Npcs.Add(new NpcRecord { FormId = 2, Stats = Stats(0x80, 1500, 1, 10, 0) });
        var inspection = Assert.IsType<ActorInspection>(new ActorInspector(collection).Inspect(1, false));
        var value = ActorLevelCalculation.Evaluate(inspection, ActorEngineProfile.PcRetail, 9, null);
        Assert.Equal("Calculated", value.Status);
        Assert.Equal(10d, value.Value);
        Assert.Equal(2u, Assert.Single(value.Inputs, input => input.Key == "LevelEncoded").SourceActor);
        Assert.Equal("Inherited", Assert.Single(value.Inputs, input => input.Key == "LevelEncoded").Provenance);
        Assert.Equal(ActorEngineProfile.PcRetailId, value.EngineProfile);
        Assert.Equal("Reconstruction", value.CalculationBasis);
    }

    [Theory]
    [InlineData(BethesdaGame.Unknown, true, true, null, "verified Fallout: New Vegas source identity")]
    [InlineData(BethesdaGame.FalloutNewVegas, false, true, null, "engine-profile")]
    [InlineData(BethesdaGame.FalloutNewVegas, true, false, null, "scenario.playerLevel")]
    [InlineData(BethesdaGame.FalloutNewVegas, true, true, "wrong", "scenario executable")]
    public void MissingOrConflictingInputsNeverFallBackToLocalOrGuessedLevels(BethesdaGame game,
        bool hasProfile, bool hasPlayerLevel, string? executable, string dependency)
    {
        var collection = new RecordCollection { Game = game };
        collection.Npcs.Add(new NpcRecord { FormId = 1, Stats = Stats(0x80, 1500, 1, 10, 0) });
        var inspection = Assert.IsType<ActorInspection>(new ActorInspector(collection).Inspect(1, false));
        var value = ActorLevelCalculation.Evaluate(inspection, hasProfile ? ActorEngineProfile.PcRetail : null,
            hasPlayerLevel ? (ushort)9 : null, executable);
        Assert.Equal("Unavailable", value.Status);
        Assert.Null(value.Value);
        Assert.Contains(value.MissingDependencies, item => item.Contains(dependency, StringComparison.Ordinal));
    }

    [Fact]
    public void MissingStatsTemplateDoesNotUseLocalFixedLevel()
    {
        var collection = new RecordCollection { Game = BethesdaGame.FalloutNewVegas };
        collection.Npcs.Add(new NpcRecord { FormId = 1, Template = 2, Stats = Stats(0, 20, 0, 0, 2) });
        var inspection = Assert.IsType<ActorInspection>(new ActorInspector(collection).Inspect(1, false));
        var value = ActorLevelCalculation.Evaluate(inspection, ActorEngineProfile.PcRetail, 9, null);
        Assert.Equal("Unavailable", value.Status);
        Assert.Contains(value.MissingDependencies, item => item.StartsWith("UseStats:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pc-retail")]
    [InlineData(ActorEngineProfile.PcRetailId)]
    public void ProfileAliasesSelectOneExplicitEdition(string name) =>
        Assert.Same(ActorEngineProfile.PcRetail, ActorEngineProfile.Resolve(name));

    [Fact]
    public void UnknownEngineProfileIsRejected() => Assert.Throws<ArgumentException>(() =>
        ActorEngineProfile.Resolve("xbox-prototype"));

    private static ActorBaseSubrecord Stats(uint flags, short level, ushort minimum, ushort maximum, ushort templateFlags) =>
        new(flags, 0, 0, level, minimum, maximum, 100, 0, 0, templateFlags, 0, false);
}
