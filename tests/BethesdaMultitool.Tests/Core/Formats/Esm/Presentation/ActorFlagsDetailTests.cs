using System.Buffers.Binary;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;
using BethesdaMultitool.Core.Formats.Esm.RecordModel;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Presentation;

public sealed class ActorFlagsDetailTests
{
    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, "NPC_", 0x299u, "Female, Respawn, Auto-calc stats, PC Level Mult, No Low Level Processing (0x0299)")]
    [InlineData(BethesdaGame.Fallout3, "NPC_", 0x290u, "Auto-calc stats, PC Level Mult, No Low Level Processing (0x0290)")]
    [InlineData(BethesdaGame.FalloutNewVegas, "CREA", 0x01400258u, "Respawn, Swims, Walks, No Low Level Processing, Can't Open Doors, Tilt Front/Back (0x1400258)")]
    [InlineData(BethesdaGame.Fallout3, "CREA", 0x01400258u, "Respawn, Swims, Walks, No Low Level Processing, Can't Open Doors, Tilt Front/Back (0x1400258)")]
    [InlineData(BethesdaGame.Unknown, "NPC_", 0x299u, "0x00000299")]
    [InlineData(BethesdaGame.Unknown, "CREA", 0x170u, "0x00000170")]
    [InlineData(BethesdaGame.FalloutNewVegas, "CREA", 0u, "None")]
    [InlineData(BethesdaGame.Skyrim, "NPC_", 0x299u, null)]
    [InlineData(BethesdaGame.Oblivion, "CREA", 0x170u, null)]
    public void Record_lookup_and_schema_routes_keep_context_and_existing_fields(
        BethesdaGame game, string signature, uint flags, string? expected)
    {
        var (record, records) = Records(game, signature, flags);
        Assert.True(RecordDetailPresenter.TryBuildForRecord(record, records, FormIdResolver.Empty, out var selected));
        Assert.True(RecordDetailPresenter.TryBuildForLookup(records, FormIdResolver.Empty, 0x800, null, out var lookup));
        var profiled = Profile(signature).Build(0x800, "TestActor", "Test actor", Tree(flags, complete: true),
            game, FormIdResolver.Empty, records);

        foreach (var model in new[] { selected!, lookup!, profiled })
        {
            Assert.Equal(expected, Value(model, "Actor Flags"));
            Assert.Equal("Test actor", Value(model, "Name"));
            Assert.Null(Value(model, "Gender"));
            string? expectedFemale = null;
            if (signature == "NPC_") expectedFemale = (flags & 1) != 0 ? "Yes" : "No";
            Assert.Equal(expectedFemale, Value(model, "Female"));
        }
    }

    [Theory]
    [InlineData("NPC_")]
    [InlineData("CREA")]
    public void Missing_or_partial_stats_do_not_become_observed_zero_flags(string signature)
    {
        var (record, records) = Records(BethesdaGame.FalloutNewVegas, signature, null);
        Assert.True(RecordDetailPresenter.TryBuildForRecord(record, records, FormIdResolver.Empty, out var selected));
        Assert.True(RecordDetailPresenter.TryBuildForLookup(records, FormIdResolver.Empty, 0x800, null, out var lookup));
        foreach (var tree in new IReadOnlyList<DecodedNode>[] { [], Tree(0x170, complete: false) })
        {
            var profiled = Profile(signature).Build(0x800, null, null, tree, records.Game, FormIdResolver.Empty, records);
            Assert.Null(Value(profiled, "Actor Flags"));
            Assert.Equal("(unknown)", Value(profiled, "Level"));
            Assert.Null(Value(profiled, "Level Multiplier (stored)"));
        }
        Assert.Null(Value(selected!, "Actor Flags"));
        Assert.Null(Value(lookup!, "Actor Flags"));
    }

    [Theory]
    [InlineData("NPC_")]
    [InlineData("CREA")]
    public void Record_without_collection_retains_only_numeric_flag_context(string signature)
    {
        var (record, _) = Records(BethesdaGame.FalloutNewVegas, signature, 0x171);
        Assert.True(RecordDetailPresenter.TryBuildForRecord(record, null, FormIdResolver.Empty, out var model));
        Assert.Equal("0x00000171", Value(model!, "Actor Flags"));
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, "NPC_", 0x299u, 1200, 25, 0, "1.2")]
    [InlineData(BethesdaGame.Fallout3, "NPC_", 0x290u, 2000, 6, 12, "2")]
    [InlineData(BethesdaGame.FalloutNewVegas, "CREA", 0x80u, 0, 0, 65535, "0")]
    [InlineData(BethesdaGame.Fallout3, "CREA", 0x80u, 1500, 1, 20, "1.5")]
    [InlineData(BethesdaGame.FalloutNewVegas, "NPC_", 0x80u, -1500, 0, 0, "-1.5")]
    [InlineData(BethesdaGame.FalloutNewVegas, "CREA", 0x40u, 50, 3, 60, null)]
    [InlineData(BethesdaGame.Unknown, "NPC_", 0x80u, 1200, 25, 0, null)]
    [InlineData(BethesdaGame.Skyrim, "NPC_", 0x80u, 2000, 6, 12, null)]
    public void Stored_level_presentation_distinguishes_multiplier_from_fixed_level(
        BethesdaGame game, string signature, uint flags, int level, int minimum, int maximum, string? multiplier)
    {
        var (record, records) = Records(game, signature, flags, (short)level, (ushort)minimum, (ushort)maximum);
        Assert.True(RecordDetailPresenter.TryBuildForRecord(record, records, FormIdResolver.Empty, out var selected));
        Assert.True(RecordDetailPresenter.TryBuildForLookup(records, FormIdResolver.Empty, 0x800, null, out var lookup));
        var tree = Tree(flags, true, level, minimum, maximum);
        if (game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas)
        {
            var acbs = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(acbs, flags);
            BinaryPrimitives.WriteInt16LittleEndian(acbs.AsSpan(8), (short)level);
            BinaryPrimitives.WriteUInt16LittleEndian(acbs.AsSpan(10), (ushort)minimum);
            BinaryPrimitives.WriteUInt16LittleEndian(acbs.AsSpan(12), (ushort)maximum);
            tree = SchemaRecordDecoder.Decode(EsmSchemas.IndexForGame(game)![signature],
                [new RawSubrecord("ACBS", acbs)], game: game);
        }
        var profiled = Profile(signature).Build(0x800, "TestActor", "Test actor", tree,
            game, FormIdResolver.Empty, records);

        foreach (var model in new[] { selected!, lookup!, profiled })
        {
            if (multiplier is null)
            {
                Assert.Equal(level.ToString(CultureInfo.InvariantCulture), Value(model, "Level"));
                Assert.Null(Value(model, "Level Multiplier (stored)"));
                Assert.Null(Value(model, "Level (encoded)"));
                Assert.Null(Value(model, "Minimum Level"));
                Assert.Null(Value(model, "Maximum Level"));
                continue;
            }

            Assert.Null(Value(model, "Level"));
            Assert.Equal(multiplier, Value(model, "Level Multiplier (stored)"));
            Assert.Equal(level.ToString(CultureInfo.InvariantCulture), Value(model, "Level (encoded)"));
            Assert.Equal(minimum.ToString(CultureInfo.InvariantCulture), Value(model, "Minimum Level"));
            Assert.Equal(maximum.ToString(CultureInfo.InvariantCulture), Value(model, "Maximum Level"));
        }
    }

    private static string? Value(RecordDetailModel model, string label) =>
        model.Sections.SelectMany(section => section.Entries).SingleOrDefault(entry => entry.Label == label)?.Value;

    private static IRecordProfile Profile(string signature) =>
        signature == "NPC_" ? new NpcProfile() : new CreatureProfile();

    private static IReadOnlyList<DecodedNode> Tree(uint flags, bool complete, int level = 7, int minimum = 0, int maximum = 0)
    {
        var fields = new List<DecodedNode>
        {
            new() { Label = "Flags", RawValue = (long)flags },
            new() { Label = "Level", RawValue = (long)level },
            new() { Label = "Calc min", RawValue = (long)minimum },
            new() { Label = "Calc max", RawValue = (long)maximum }
        };
        if (complete) fields.Add(new() { Label = "Template Flags", RawValue = 0L });
        return [new() { Signature = "ACBS", Label = "Configuration", Children = fields }];
    }

    private static (object Record, RecordCollection Records) Records(
        BethesdaGame game, string signature, uint? flags, short level = 7, ushort minimum = 0, ushort maximum = 0)
    {
        var stats = flags is { } value ? new ActorBaseSubrecord(value, 0, 0, level, minimum, maximum, 100, 0, 0, 0, 0, false) : null;
        if (signature == "NPC_")
        {
            var npc = new NpcRecord { FormId = 0x800, EditorId = "TestActor", FullName = "Test actor", Stats = stats };
            return (npc, new RecordCollection { Game = game, Npcs = [npc] });
        }

        var creature = new CreatureRecord { FormId = 0x800, EditorId = "TestActor", FullName = "Test actor", Stats = stats };
        return (creature, new RecordCollection { Game = game, Creatures = [creature] });
    }
}
