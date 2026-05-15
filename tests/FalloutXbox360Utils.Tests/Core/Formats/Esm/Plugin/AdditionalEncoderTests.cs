using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Tests for the v1 encoders added in the second batch (ARMO, AMMO, ALCH, BOOK, FACT,
///     NPC_). Each test verifies the subrecord signature, payload size, and at least one
///     little-endian field round-trip.
/// </summary>
public class AdditionalEncoderTests
{
    [Fact]
    public void ArmoEncoder_DataLayout_IsValueHealthWeight()
    {
        var armo = new ArmorRecord { FormId = 1, Value = 250, Health = 1000, Weight = 12.5f };

        var encoded = new ArmoEncoder().Encode(armo);

        var data = Assert.Single(encoded.Subrecords);
        Assert.Equal("DATA", data.Signature);
        Assert.Equal(12, data.Bytes.Length);
        Assert.Equal(250, BinaryPrimitives.ReadInt32LittleEndian(data.Bytes.AsSpan(0, 4)));
        Assert.Equal(1000, BinaryPrimitives.ReadInt32LittleEndian(data.Bytes.AsSpan(4, 4)));
        Assert.Equal(12.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(8, 4)));
    }

    [Fact]
    public void AmmoEncoder_DataLayout_HasExpectedFieldsAndPadding()
    {
        var ammo = new AmmoRecord
        {
            FormId = 1,
            Speed = 5000.0f,
            Flags = 0x03,
            Value = 25,
            ClipRounds = 6
        };

        var encoded = new AmmoEncoder().Encode(ammo);

        var data = Assert.Single(encoded.Subrecords);
        Assert.Equal("DATA", data.Signature);
        Assert.Equal(13, data.Bytes.Length);
        Assert.Equal(5000.0f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(0, 4)));
        Assert.Equal((byte)0x03, data.Bytes[4]);
        // Bytes 5..7 are C-struct padding and must be zero.
        Assert.Equal(0, data.Bytes[5]);
        Assert.Equal(0, data.Bytes[6]);
        Assert.Equal(0, data.Bytes[7]);
        Assert.Equal(25u, BinaryPrimitives.ReadUInt32LittleEndian(data.Bytes.AsSpan(8, 4)));
        Assert.Equal((byte)6, data.Bytes[12]);
    }

    [Fact]
    public void AlchEncoder_DataIsFloatWeightOnly()
    {
        var alch = new ConsumableRecord { FormId = 1, Weight = 0.25f };

        var encoded = new AlchEncoder().Encode(alch);

        var data = Assert.Single(encoded.Subrecords);
        Assert.Equal("DATA", data.Signature);
        Assert.Equal(4, data.Bytes.Length);
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes));
    }

    [Fact]
    public void BookEncoder_DataLayout_FlagsTeachesValueWeight()
    {
        var book = new BookRecord
        {
            FormId = 1,
            Flags = 0x01,
            SkillTaught = 7,
            Value = 50,
            Weight = 1.5f
        };

        var encoded = new BookEncoder().Encode(book);

        var data = Assert.Single(encoded.Subrecords);
        Assert.Equal("DATA", data.Signature);
        Assert.Equal(10, data.Bytes.Length);
        Assert.Equal((byte)0x01, data.Bytes[0]);
        Assert.Equal((byte)7, data.Bytes[1]);
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(data.Bytes.AsSpan(2, 4)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(6, 4)));
    }

    [Fact]
    public void FactEncoder_DataIsUInt32Flags()
    {
        var fact = new FactionRecord { FormId = 1, Flags = 0x4042 };

        var encoded = new FactEncoder().Encode(fact);

        var data = Assert.Single(encoded.Subrecords);
        Assert.Equal("DATA", data.Signature);
        Assert.Equal(4, data.Bytes.Length);
        Assert.Equal(0x4042u, BinaryPrimitives.ReadUInt32LittleEndian(data.Bytes));
    }

    [Fact]
    public void NpcEncoder_AcbsLayout_IsByteForByteCorrect()
    {
        var stats = new ActorBaseSubrecord(
            Flags: 0x12345678,
            FatigueBase: 100,
            BarterGold: 250,
            Level: -5,
            CalcMin: 1,
            CalcMax: 50,
            SpeedMultiplier: 100,
            KarmaAlignment: 1.5f,
            DispositionBase: -25,
            TemplateFlags: 0xABCD,
            Offset: 0,
            IsBigEndian: false);
        var npc = new NpcRecord { FormId = 1, Stats = stats };

        var encoded = new NpcEncoder().Encode(npc);

        var acbs = Assert.Single(encoded.Subrecords);
        Assert.Equal("ACBS", acbs.Signature);
        Assert.Equal(24, acbs.Bytes.Length);
        // NpcEncoder.Encode (override path) forces ACBS bit 0x10 (AutoCalcStats) so the
        // engine derives HP/AP from Level + Class + SPECIAL rather than trusting the
        // captured runtime Flags (which routinely have AutoCalc cleared after computation).
        // Bit 0x01 is Female — do NOT force it; that would sex-swap every male NPC.
        Assert.Equal(0x12345678u | 0x10u, BinaryPrimitives.ReadUInt32LittleEndian(acbs.Bytes.AsSpan(0, 4)));
        Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(4, 2)));
        Assert.Equal((ushort)250, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(6, 2)));
        Assert.Equal((short)-5, BinaryPrimitives.ReadInt16LittleEndian(acbs.Bytes.AsSpan(8, 2)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(10, 2)));
        Assert.Equal((ushort)50, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(12, 2)));
        Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(14, 2)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(acbs.Bytes.AsSpan(16, 4)));
        Assert.Equal((short)-25, BinaryPrimitives.ReadInt16LittleEndian(acbs.Bytes.AsSpan(20, 2)));
        Assert.Equal((ushort)0xABCD, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(22, 2)));
    }

    [Fact]
    public void NpcEncoder_NoStats_ProducesEmptySubrecordsWithWarning()
    {
        var npc = new NpcRecord { FormId = 0xCAFEBABE, Stats = null };

        var encoded = new NpcEncoder().Encode(npc);

        Assert.Empty(encoded.Subrecords);
        Assert.Single(encoded.Warnings);
        Assert.Contains("ACBS", encoded.Warnings[0]);
    }
}
