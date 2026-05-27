using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Phase 4.3: encoder → parser round-trip smoke for CREA ACBS flag policy. Approach (b)
///     from the plan: synthetic-only end-to-end, exercising both the writer
///     (<see cref="CreaEncoder.EncodeNew" />) and reader
///     (<see cref="ActorRecordHandler.ParseActorBase" />) on the same byte stream. This is a
///     strict superset of <see cref="CreaEncoderAcbsFlagPolicyTests" /> — those pin the
///     encoder side only; the smoke test below additionally pins the byte-level contract
///     both sides must agree on.
///     <para>
///     We deliberately skip the full <c>PluginConversionPipeline</c> path: it requires a
///     real DMP file + a real PC FalloutNV.esm master and a writable output directory, all
///     of which would add minutes to a single test run. The encoder→parser pair captures the
///     load-bearing failure mode (flag-policy fixup must survive serialization and re-parse
///     in a way that a downstream consumer would see).
///     </para>
/// </summary>
public sealed class CreaSmokeIntegrationTests
{
    private const uint FlagAutoCalcStats = 0x00000010u;
    private const uint FlagUseTemplate = 0x00000040u;

    [Fact]
    public void EncoderToParser_TemplatedCreature_RoundTripsAllThreeFlagPolicyFixups()
    {
        // Build a captured-state CreatureRecord that triggers ALL three ActorBaseAcbsBuilder
        // policy fixups in one pass:
        //   1. AutoCalcStats (0x10) forced (not present in input Flags).
        //   2. UseTemplate (0x40) set (TemplateFlags is nonzero).
        //   3. SpeedMultiplier clamped to 100 (input is 0).
        var input = new ActorBaseSubrecord(
            Flags: 0x00000002u, // Essential bit only; no AutoCalc, no UseTemplate.
            FatigueBase: 75,
            BarterGold: 0,
            Level: 5,
            CalcMin: 1,
            CalcMax: 50,
            SpeedMultiplier: 0,         // Clamp trigger.
            KarmaAlignment: 0.5f,
            DispositionBase: 25,
            TemplateFlags: 0x0001,      // UseTemplate trigger (Speedy/Sleepy-style templated creature).
            Offset: 0,
            IsBigEndian: false);

        var crea = new CreatureRecord
        {
            FormId = 0x01001234,
            EditorId = "SpeedyTestSubject",
            Stats = input
        };

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>(), null);
        var acbs = encoded.Subrecords.Single(s => s.Signature == "ACBS");

        // Round-trip: the encoder's output bytes are fed back through the parser's
        // standard ACBS decoder. The parser doesn't know or care that the encoder
        // emitted this — it just reads the 24-byte subrecord payload.
        var parsed = ActorRecordHandler.ParseActorBase(acbs.Bytes, offset: 0, bigEndian: false);
        Assert.NotNull(parsed);

        // Flag-policy fixup 1: AutoCalcStats (0x10) must be present in the parser's view.
        Assert.True((parsed.Flags & FlagAutoCalcStats) != 0,
            $"AutoCalcStats bit (0x10) missing from parsed Flags 0x{parsed.Flags:X8}.");

        // Flag-policy fixup 2: UseTemplate (0x40) must be present — without this the engine
        // emits "Speedy (12345)" per-spawn instead of treating it as a proper templated actor.
        Assert.True((parsed.Flags & FlagUseTemplate) != 0,
            $"UseTemplate bit (0x40) missing from parsed Flags 0x{parsed.Flags:X8}.");

        // Source bit (Essential = 0x02) preserved alongside the two fixups.
        Assert.Equal(0x00000052u, parsed.Flags & 0xFFu);

        // Flag-policy fixup 3: SpeedMultiplier clamped to 100.
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
    public void EncoderToParser_NonTemplatedCreature_DoesNotSetUseTemplateBit()
    {
        // Negative case: a captured creature with TemplateFlags = 0 must NOT have
        // UseTemplate (0x40) added during encoding. AutoCalcStats (0x10) still gets
        // forced — that's an always-on fixup independent of templating.
        var input = new ActorBaseSubrecord(
            Flags: 0x00000002u,
            FatigueBase: 100,
            BarterGold: 0,
            Level: 10,
            CalcMin: 5,
            CalcMax: 50,
            SpeedMultiplier: 100,
            KarmaAlignment: 0f,
            DispositionBase: 50,
            TemplateFlags: 0,            // No templating — no UseTemplate fixup.
            Offset: 0,
            IsBigEndian: false);

        var crea = new CreatureRecord
        {
            FormId = 0x01001235,
            EditorId = "NonTemplatedRadroach",
            Stats = input
        };

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>(), null);
        var acbs = encoded.Subrecords.Single(s => s.Signature == "ACBS");

        var parsed = ActorRecordHandler.ParseActorBase(acbs.Bytes, offset: 0, bigEndian: false);
        Assert.NotNull(parsed);

        // AutoCalcStats forced (always-on fixup).
        Assert.True((parsed.Flags & FlagAutoCalcStats) != 0,
            $"AutoCalcStats (0x10) should still be forced even when TemplateFlags is zero. " +
            $"Parsed Flags = 0x{parsed.Flags:X8}.");

        // UseTemplate NOT set because TemplateFlags is zero.
        Assert.True((parsed.Flags & FlagUseTemplate) == 0,
            $"UseTemplate (0x40) should NOT be set when TemplateFlags is zero. " +
            $"Parsed Flags = 0x{parsed.Flags:X8}.");

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

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>(), null);
        var acbs = encoded.Subrecords.Single(s => s.Signature == "ACBS");

        var parsed = ActorRecordHandler.ParseActorBase(acbs.Bytes, offset: 0, bigEndian: false);
        Assert.NotNull(parsed);

        Assert.Equal(0u, parsed.Flags);
        Assert.Equal((short)1, parsed.Level);
        Assert.Equal((ushort)100, parsed.SpeedMultiplier);
        Assert.Equal(0u, parsed.TemplateFlags);
    }
}
