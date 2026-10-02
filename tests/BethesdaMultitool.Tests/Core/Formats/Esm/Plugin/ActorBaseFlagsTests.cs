using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class ActorBaseFlagsTests
{
    [Theory]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    public void Fallout_actor_names_distinguish_record_types(BethesdaGame game)
    {
        var npc = FlagRegistry.GetActorBaseFlags(game, "NPC_");
        var creature = FlagRegistry.GetActorBaseFlags(game, "CREA");
        Assert.Equal("Female", FlagRegistry.DecodeFlagNames(1, npc));
        Assert.Equal("Biped", FlagRegistry.DecodeFlagNames(1, creature));
        Assert.Equal("Auto-calc stats", FlagRegistry.DecodeFlagNames(0x10, npc));
        Assert.Equal("Swims, Flies, Walks", FlagRegistry.DecodeFlagNames(0x70, creature));
        Assert.DoesNotContain(npc, flag => flag.Mask is 0x20 or 0x40);
        Assert.Equal("PC Level Mult", FlagRegistry.DecodeFlagNames(0x80, npc));
        Assert.Equal("PC Level Mult", FlagRegistry.DecodeFlagNames(0x80, creature));
        Assert.Equal("Use Template", FlagRegistry.DecodeFlagNames(0x100, npc));
        Assert.DoesNotContain(creature, flag => flag.Name == "Use Template");
        Assert.Equal("No Low Level Processing", FlagRegistry.DecodeFlagNames(0x200, npc));
        Assert.Equal("No Blood Spray", FlagRegistry.DecodeFlagNames(0x800, creature));
        Assert.Equal("No Knockdowns", FlagRegistry.DecodeFlagNames(0x4000000, creature));
        Assert.Equal("Invulnerable", FlagRegistry.DecodeFlagNames(0x80000000, creature));
    }

    [Theory]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    public void Other_game_callers_keep_existing_definitions(BethesdaGame game)
    {
        Assert.Same(FlagRegistry.ActorBaseFlags, FlagRegistry.GetActorBaseFlags(game, "NPC_"));
        Assert.Same(FlagRegistry.ActorBaseFlags, FlagRegistry.GetActorBaseFlags(game, "CREA"));
    }

    [Fact]
    public void Unknown_game_keeps_flags_numeric()
    {
        var flags = FlagRegistry.GetActorBaseFlags(BethesdaGame.Unknown, "CREA");
        Assert.Empty(flags);
        Assert.Equal("0x00000171", FlagRegistry.DecodeFlagNames(0x171, flags));
    }

    [Theory]
    [InlineData("NPC_", 0u, false, 1, 0x100u)]
    [InlineData("NPC_", 0x80u, true, 1, 0x190u)]
    [InlineData("NPC_", 0u, true, 0, 0x10u)]
    [InlineData("CREA", 0u, true, 1, 0u)]
    [InlineData("CREA", 0x170u, true, 1, 0x170u)]
    public void Builder_writes_record_specific_flags_and_retains_template_fields(
        string recordType, uint capturedFlags, bool forceAutoCalc, int extraTemplateFlags, uint expectedFlags)
    {
        var stats = new ActorBaseSubrecord(capturedFlags, 75, 4, 1300, 2, 20, 120, 0, 3, 0, 0, false);
        var bytes = ActorBaseAcbsBuilder.Build(recordType, stats, forceAutoCalc, (ushort)extraTemplateFlags);
        Assert.Equal(expectedFlags, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal((ushort)extraTemplateFlags, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)));
        Assert.Equal((short)1300, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(8)));
        Assert.Equal((ushort)120, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14)));
    }

    [Theory]
    [InlineData("NPC_", 0x100u)]
    [InlineData("CREA", 0u)]
    public void Missing_stats_defaults_do_not_add_creature_flags(string recordType, uint expectedFlags)
    {
        var bytes = ActorBaseAcbsBuilder.BuildDefault(recordType, 1);
        Assert.Equal(expectedFlags, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)));
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(8)));
        Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14)));
    }
}
