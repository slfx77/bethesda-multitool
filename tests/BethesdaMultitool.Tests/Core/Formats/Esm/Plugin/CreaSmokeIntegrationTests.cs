using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

/// <summary>Encoder-to-parser CREA flag preservation, numeric fields and defaults.</summary>
public sealed class CreaSmokeIntegrationTests
{
    [Fact]
    public void EncoderToParser_TemplatedCreature_PreservesFlagsAndNumericFields()
    {
        var input = new ActorBaseSubrecord(
            0x00000162u, // Essential, Flies, Walks and unknown bit 8.
            75,
            0,
            5,
            1,
            50,
            0, // Clamp trigger.
            0.5f,
            25,
            0x0001, // Authored Use Traits field.
            0,
            false);

        var crea = new CreatureRecord
        {
            FormId = 0x01001234,
            EditorId = "SpeedyTestSubject",
            Stats = input
        };

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>());
        var acbs = encoded.Subrecords.Single(s => s.Signature == "ACBS");

        // Round-trip: the encoder's output bytes are fed back through the parser's
        // standard ACBS decoder. The parser doesn't know or care that the encoder
        // emitted this — it just reads the 24-byte subrecord payload.
        var parsed = ActorRecordHandler.ParseActorBase(acbs.Bytes, 0, false);
        Assert.NotNull(parsed);

        Assert.Equal(input.Flags, parsed.Flags);
        Assert.Equal((ushort)100, parsed.SpeedMultiplier);

        // Non-flag fields survive the round-trip unchanged.
        Assert.Equal(input.FatigueBase, parsed.FatigueBase);
        Assert.Equal(input.BarterGold, parsed.BarterGold);
        Assert.Equal(input.Level, parsed.Level);
        Assert.Equal(input.CalcMin, parsed.CalcMin);
        Assert.Equal(input.CalcMax, parsed.CalcMax);
        Assert.Equal(input.KarmaAlignment, parsed.KarmaAlignment);
        Assert.Equal(input.DispositionBase, parsed.DispositionBase);
        Assert.Equal(input.TemplateFlags, parsed.TemplateFlags);
    }

    [Fact]
    public void EncoderToParser_NonTemplatedCreature_DoesNotAddFlagBits()
    {
        var input = new ActorBaseSubrecord(
            0x00000002u,
            100,
            0,
            10,
            5,
            50,
            100,
            0f,
            50,
            0, // No template fields.
            0,
            false);

        var crea = new CreatureRecord
        {
            FormId = 0x01001235,
            EditorId = "NonTemplatedRadroach",
            Stats = input
        };

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>());
        var acbs = encoded.Subrecords.Single(s => s.Signature == "ACBS");

        var parsed = ActorRecordHandler.ParseActorBase(acbs.Bytes, 0, false);
        Assert.NotNull(parsed);

        Assert.Equal(input.Flags, parsed.Flags);

        Assert.Equal(0u, parsed.TemplateFlags);
    }

    [Fact]
    public void EncoderToParser_DefaultsPathProducesEngineDefaults()
    {
        // When Stats is null (e.g. a CreatureRecord that came from a scan-only path with
        // no captured ACBS), CreaEncoder emits engine defaults (Level=1, SpeedMult=100,
        // Flags=0) instead of the previous all-zero buffer. The parser should see those
        // defaults — which is what the engine needs for newly-emitted CREAs.
        var crea = new CreatureRecord
        {
            FormId = 0x01001236,
            EditorId = "ScanOnlyCrea",
            Stats = null
        };

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>());
        var acbs = encoded.Subrecords.Single(s => s.Signature == "ACBS");

        var parsed = ActorRecordHandler.ParseActorBase(acbs.Bytes, 0, false);
        Assert.NotNull(parsed);

        Assert.Equal(0u, parsed.Flags);
        Assert.Equal((short)1, parsed.Level);
        Assert.Equal((ushort)100, parsed.SpeedMultiplier);
        Assert.Equal(0u, parsed.TemplateFlags);
    }
}