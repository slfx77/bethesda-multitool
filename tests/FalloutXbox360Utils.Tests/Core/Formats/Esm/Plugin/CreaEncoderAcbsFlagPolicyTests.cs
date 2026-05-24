using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Regression tests for the CREA encoder's ACBS flag-policy fixups, extracted into
///     <see cref="ActorBaseAcbsBuilder" /> as part of Tier 3.1. CreaEncoder previously
///     emitted ACBS via raw schema serialization with no flag policy — captured
///     templated creatures (TemplateFlags != 0) were missing the UseTemplate (0x40) bit
///     and showed up in-game with per-spawn numeric suffixes ("Speedy (12345)"), the
///     same bug class as the Ulysses-suffix bug previously fixed on NPC placements.
///
///     These tests pin the three fixups now applied uniformly to both NPC and CREA ACBS
///     emission via the shared helper: AutoCalcStats (0x10) forced, UseTemplate (0x40)
///     set when TemplateFlags is nonzero, and SpeedMultiplier clamped to 100 when zero.
/// </summary>
public sealed class CreaEncoderAcbsFlagPolicyTests
{
    [Fact]
    public void EncodeNew_ForcesAutoCalcStatsBit_WhenMissingFromCapturedFlags()
    {
        // Captured DMP runtime often clears AutoCalc (0x10) once stats were computed.
        // Without re-asserting it on emission, the engine reads manual stats from
        // CalcMin/CalcMax + Level, which routinely yields 0 HP → creature spawns dead.
        var stats = new ActorBaseSubrecord(
            Flags: 0x00000000u, // No bits set, especially NOT 0x10.
            FatigueBase: 50,
            BarterGold: 0,
            Level: 5,
            CalcMin: 1,
            CalcMax: 50,
            SpeedMultiplier: 100,
            KarmaAlignment: 0f,
            DispositionBase: 0,
            TemplateFlags: 0, // No template — only AutoCalc should be added.
            Offset: 0,
            IsBigEndian: false);
        var crea = MakeCrea(stats);

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>(), null);

        var acbs = FindAcbs(encoded);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(acbs.Bytes.AsSpan(0, 4));
        Assert.Equal(0x00000010u, flags);
    }

    [Fact]
    public void EncodeNew_SetsUseTemplateBit_WhenTemplateFlagsNonzero()
    {
        // Mirror of the Ulysses fix: templated creatures must emit ACBS with the
        // UseTemplate (0x40) bit so the engine treats them as proper templated
        // unique actors, not per-spawn numeric-suffix instances.
        var stats = new ActorBaseSubrecord(
            Flags: 0x00000002u, // Essential bit set; nothing else.
            FatigueBase: 50,
            BarterGold: 0,
            Level: 5,
            CalcMin: 1,
            CalcMax: 50,
            SpeedMultiplier: 100,
            KarmaAlignment: 0f,
            DispositionBase: 0,
            TemplateFlags: 0x0001, // Any nonzero TemplateFlags triggers UseTemplate.
            Offset: 0,
            IsBigEndian: false);
        var crea = MakeCrea(stats);

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>(), null);

        var acbs = FindAcbs(encoded);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(acbs.Bytes.AsSpan(0, 4));
        // 0x02 (Essential, preserved) | 0x10 (AutoCalc, forced) | 0x40 (UseTemplate, set because TemplateFlags=0x0001).
        Assert.Equal(0x00000052u, flags);
    }

    [Fact]
    public void EncodeNew_ClampsZeroSpeedMultiplierTo100()
    {
        // FNV engine default for SpeedMultiplier is 100; emitting 0 would make the
        // creature unable to move.
        var stats = new ActorBaseSubrecord(
            Flags: 0,
            FatigueBase: 0,
            BarterGold: 0,
            Level: 1,
            CalcMin: 0,
            CalcMax: 0,
            SpeedMultiplier: 0, // Should be clamped to 100.
            KarmaAlignment: 0f,
            DispositionBase: 0,
            TemplateFlags: 0,
            Offset: 0,
            IsBigEndian: false);
        var crea = MakeCrea(stats);

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>(), null);

        var acbs = FindAcbs(encoded);
        var speedMult = BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(14, 2));
        Assert.Equal((ushort)100, speedMult);
    }

    [Fact]
    public void EncodeNew_PreservesAllOtherAcbsFieldsByteForByte()
    {
        // Confirm the helper round-trips every non-policy ACBS field exactly. Sanity
        // check that consolidating into ActorBaseAcbsBuilder didn't corrupt the
        // schema mapping.
        var stats = new ActorBaseSubrecord(
            Flags: 0x00000002u,    // Essential
            FatigueBase: 75,
            BarterGold: 250,
            Level: -3,
            CalcMin: 2,
            CalcMax: 8,
            SpeedMultiplier: 120,
            KarmaAlignment: 2.5f,
            DispositionBase: -10,
            TemplateFlags: 0x0080,
            Offset: 0,
            IsBigEndian: false);
        var crea = MakeCrea(stats);

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>(), null);

        var acbs = FindAcbs(encoded);
        Assert.Equal(24, acbs.Bytes.Length);
        // Flags: input (0x02) | AutoCalc (0x10) | UseTemplate (0x40, TemplateFlags=0x80) = 0x52.
        Assert.Equal(0x00000052u, BinaryPrimitives.ReadUInt32LittleEndian(acbs.Bytes.AsSpan(0, 4)));
        Assert.Equal((ushort)75, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(4, 2)));
        Assert.Equal((ushort)250, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(6, 2)));
        Assert.Equal((short)-3, BinaryPrimitives.ReadInt16LittleEndian(acbs.Bytes.AsSpan(8, 2)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(10, 2)));
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(12, 2)));
        Assert.Equal((ushort)120, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(14, 2)));
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(acbs.Bytes.AsSpan(16, 4)));
        Assert.Equal((short)-10, BinaryPrimitives.ReadInt16LittleEndian(acbs.Bytes.AsSpan(20, 2)));
        Assert.Equal((ushort)0x0080, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(22, 2)));
    }

    [Fact]
    public void EncodeNew_NoStats_EmitsEngineDefaultsNotZeroFill()
    {
        // Previously CreaEncoder emitted `new byte[24]` (all zeros) when Stats was
        // null — including Level=0, SpeedMult=0. The engine treats Level=0 as
        // unrecoverable in a few code paths; default-stats should land on Level=1,
        // SpeedMult=100 to mirror engine fallbacks.
        var crea = new CreatureRecord
        {
            FormId = 0x01000800,
            EditorId = "TestCreaNoStats",
            Stats = null
        };

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>(), null);

        var acbs = FindAcbs(encoded);
        Assert.Equal(24, acbs.Bytes.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(acbs.Bytes.AsSpan(0, 4))); // No flags forced for default
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(acbs.Bytes.AsSpan(8, 2)));    // Level = 1
        Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(14, 2))); // SpeedMult = 100
        Assert.Contains(encoded.Warnings, w => w.Contains("no ACBS"));
    }

    private static CreatureRecord MakeCrea(ActorBaseSubrecord stats)
    {
        return new CreatureRecord
        {
            FormId = 0x01000800,
            EditorId = "TestCrea",
            Stats = stats
        };
    }

    private static EncodedSubrecord FindAcbs(EncodedRecord encoded)
    {
        var acbs = encoded.Subrecords.FirstOrDefault(s => s.Signature == "ACBS");
        Assert.NotNull(acbs);
        return acbs;
    }
}
