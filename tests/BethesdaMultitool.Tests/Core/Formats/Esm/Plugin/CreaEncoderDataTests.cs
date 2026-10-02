using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class CreaEncoderDataTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(675)]
    [InlineData(-17)]
    public void EncodeNew_PreservesCapturedPcHealthAndSpecial(int health)
    {
        var creature = Captured(health);
        var record = CreaEncoder.EncodeNew(creature);
        var data = Assert.Single(record.Subrecords, subrecord => subrecord.Signature == "DATA").Bytes;
        Assert.Equal(17, data.Length);
        Assert.Equal(new byte[] { 2, 71, 18, 31 }, data[..4]);
        Assert.Equal((short)health, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(4)));
        Assert.Equal(new byte[2], data[6..8]);
        Assert.Equal((short)23, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(8)));
        Assert.Equal(creature.Attributes, data[10..]);
        Assert.DoesNotContain(record.Warnings, warning => warning.Contains("DATA Health") || warning.Contains("DATA Attributes"));
    }

    [Theory]
    [InlineData(32768)]
    [InlineData(-32769)]
    public void EncodeNew_RejectsHealthOutsideTargetWidth(int health)
    {
        var error = Assert.Throws<InvalidDataException>(() => CreaEncoder.EncodeNew(Captured(health)));
        Assert.Contains("PC Int16 range", error.Message);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void EncodeNew_RejectsMalformedCapturedAttributes(int length)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            CreaEncoder.EncodeNew(Captured(675) with { Attributes = new byte[length] }));
        Assert.Contains("exactly seven bytes", error.Message);
    }

    [Fact]
    public void EncodeNew_MissingDataKeepsExplicitCompatibilityFallback()
    {
        var record = CreaEncoder.EncodeNew(Captured(675) with { Health = null, Attributes = null });
        var data = Assert.Single(record.Subrecords, subrecord => subrecord.Signature == "DATA").Bytes;
        Assert.Equal(new byte[4], data[4..8]);
        Assert.Equal(new byte[7], data[10..]);
        Assert.Contains(record.Warnings, warning => warning.Contains("DATA Health unavailable"));
        Assert.Contains(record.Warnings, warning => warning.Contains("DATA Attributes unavailable"));
    }

    private static CreatureRecord Captured(int health) => new()
    {
        FormId = 0x01000800,
        EditorId = "CapturedCreature",
        CreatureType = 2,
        CombatSkill = 71,
        MagicSkill = 18,
        StealthSkill = 31,
        Health = health,
        AttackDamage = 23,
        Attributes = [2, 4, 3, 1, 2, 4, 1]
    };
}
