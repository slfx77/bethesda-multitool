using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

/// <summary>Byte-level CREA flag preservation and shared ACBS layout/default controls.</summary>
public sealed class CreaEncoderAcbsFlagPolicyTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(0x10u)]
    [InlineData(0x20u)]
    [InlineData(0x40u)]
    [InlineData(0x100u)]
    public void EncodeNew_PreservesCreatureMovementAndUnknownFlags(uint inputFlags)
    {
        var stats = new ActorBaseSubrecord(
            inputFlags,
            50,
            0,
            5,
            1,
            50,
            100,
            0f,
            0,
            0, // No template fields.
            0,
            false);
        var crea = MakeCrea(stats);

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>());

        var acbs = FindAcbs(encoded);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(acbs.Bytes.AsSpan(0, 4));
        Assert.Equal(inputFlags, flags);
    }

    [Fact]
    public void EncodeNew_TemplateFieldsDoNotAddMovementOrUnknownBits()
    {
        var stats = new ActorBaseSubrecord(
            0x00000002u, // Essential bit set; nothing else.
            50,
            0,
            5,
            1,
            50,
            100,
            0f,
            0,
            0x0001, // Use Traits is retained independently of the Flags dword.
            0,
            false);
        var crea = MakeCrea(stats);

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>());

        var acbs = FindAcbs(encoded);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(acbs.Bytes.AsSpan(0, 4));
        Assert.Equal(0x00000002u, flags);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(22, 2)));
    }

    [Fact]
    public void EncodeNew_ClampsZeroSpeedMultiplierTo100()
    {
        // FNV engine default for SpeedMultiplier is 100; emitting 0 would make the
        // creature unable to move.
        var stats = new ActorBaseSubrecord(
            0,
            0,
            0,
            1,
            0,
            0,
            0, // Should be clamped to 100.
            0f,
            0,
            0,
            0,
            false);
        var crea = MakeCrea(stats);

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>());

        var acbs = FindAcbs(encoded);
        var speedMult = BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(14, 2));
        Assert.Equal((ushort)100, speedMult);
    }

    [Fact]
    public void RestoreMasterIdentityFlags_PatchesFlagsAndTemplateFlagsFromMaster()
    {
        // Override identity policy: runtime captures leak state bits into ACBS Flags and
        // TemplateFlags (Omerta entrance guard: TemplateFlags 0x015F→0x835F added
        // UseScript, silencing his forcegreet). Master's values win on overrides; the
        // captured numeric fields (fatigue, level, …) stay.
        var masterAcbs = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(masterAcbs.AsSpan(0, 4), 0x00000018u);
        BinaryPrimitives.WriteUInt16LittleEndian(masterAcbs.AsSpan(22, 2), 0x015F);
        var master = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "NPC_", DataSize = 0, Flags = 0, FormId = 0x0012795D,
                Timestamp = 0, VcsInfo = 0, Version = 15
            },
            Offset = 0,
            Subrecords = [new ParsedSubrecord { Signature = "ACBS", Data = masterAcbs }]
        };

        // Merged stream: EDID + ACBS with leaked flags 0x58 / TemplateFlags 0x835F and a
        // captured fatigue of 50 that must survive the patch.
        var mergedAcbs = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(mergedAcbs.AsSpan(0, 4), 0x00000058u);
        BinaryPrimitives.WriteUInt16LittleEndian(mergedAcbs.AsSpan(4, 2), 50);
        BinaryPrimitives.WriteUInt16LittleEndian(mergedAcbs.AsSpan(22, 2), 0x835F);
        var edid = "vGOMEntranceGuard\0"u8.ToArray();
        var stream = new List<byte>();
        stream.AddRange("EDID"u8.ToArray());
        stream.AddRange(BitConverter.GetBytes((ushort)edid.Length));
        stream.AddRange(edid);
        stream.AddRange("ACBS"u8.ToArray());
        stream.AddRange(BitConverter.GetBytes((ushort)24));
        stream.AddRange(mergedAcbs);

        var patched = ActorBaseAcbsBuilder.RestoreMasterIdentityFlags([.. stream], master);

        var acbsStart = 6 + edid.Length + 6;
        Assert.Equal(0x00000018u, BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(acbsStart, 4)));
        Assert.Equal((ushort)50, BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(acbsStart + 4, 2)));
        Assert.Equal((ushort)0x015F, BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(acbsStart + 22, 2)));
    }

    [Fact]
    public void EncodeNew_PreservesAllOtherAcbsFieldsByteForByte()
    {
        // Confirm the helper round-trips every non-policy ACBS field exactly. Sanity
        // check that consolidating into ActorBaseAcbsBuilder didn't corrupt the
        // schema mapping.
        var stats = new ActorBaseSubrecord(
            0x00000002u, // Essential
            75,
            250,
            -3,
            2,
            8,
            120,
            2.5f,
            -10,
            0x0080,
            0,
            false);
        var crea = MakeCrea(stats);

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>());

        var acbs = FindAcbs(encoded);
        Assert.Equal(24, acbs.Bytes.Length);
        // All CREA flag bits remain exactly as captured.
        Assert.Equal(0x00000002u, BinaryPrimitives.ReadUInt32LittleEndian(acbs.Bytes.AsSpan(0, 4)));
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

        var encoded = CreaEncoder.EncodeNew(crea, new HashSet<uint>());

        var acbs = FindAcbs(encoded);
        Assert.Equal(24, acbs.Bytes.Length);
        Assert.Equal(0u,
            BinaryPrimitives.ReadUInt32LittleEndian(acbs.Bytes.AsSpan(0, 4))); // No flags forced for default
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(acbs.Bytes.AsSpan(8, 2))); // Level = 1
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