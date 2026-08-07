using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public class WeapEncoderTests
{
    [Fact]
    public void Encode_DataPayloadHasCorrectLayout()
    {
        var weap = new WeaponRecord
        {
            FormId = 0x0017B37C,
            Value = 1234,
            Health = 1500,
            Weight = 4.5f,
            Damage = 25,
            ClipSize = 12
        };

        var encoded = new WeapEncoder().Encode(weap);

        Assert.Single(encoded.Subrecords);
        var data = encoded.Subrecords[0];
        Assert.Equal("DATA", data.Signature);
        Assert.Equal(15, data.Bytes.Length);

        // Verify each field decodes correctly in PC little-endian.
        Assert.Equal(1234, BinaryPrimitives.ReadInt32LittleEndian(data.Bytes.AsSpan(0, 4)));
        Assert.Equal(1500, BinaryPrimitives.ReadInt32LittleEndian(data.Bytes.AsSpan(4, 4)));
        Assert.Equal(4.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(8, 4)));
        Assert.Equal((short)25, BinaryPrimitives.ReadInt16LittleEndian(data.Bytes.AsSpan(12, 2)));
        Assert.Equal((byte)12, data.Bytes[14]);
    }

    [Fact]
    public void EncodeNew_NormalizesInvalidAttackAnimToDefault()
    {
        // The Atomic Baby Machinegun class of bug: the runtime capture held AttackAnim=0,
        // which is not a member of the sparse DNAM Attack Animation enum; the engine resolves
        // it to the non-attack 'Idle' group and the weapon can never fire. Emission must clamp
        // out-of-enum bytes to 255 (DEFAULT).
        var weap = new WeaponRecord
        {
            FormId = 0x010060F1,
            EditorId = "WeapNVBabyLauncher",
            AttackAnim = (BethesdaMultitool.Core.Formats.Esm.Enums.AttackAnimation)0 // uninitialized runtime state, not a valid enum member
        };

        var encoded = WeapEncoder.EncodeNew(weap);

        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM");
        Assert.Equal(255, dnam.Bytes[41]); // Attack Animation @41 per xEdit wbDefinitionsFNV
        Assert.Contains(encoded.Warnings, w => w.Contains("Attack Animation"));
    }

    [Theory]
    [InlineData(26)]  // AttackLeft
    [InlineData(102)] // PlaceMine (FNV value)
    [InlineData(255)] // DEFAULT
    public void EncodeNew_KeepsValidAttackAnimBytes(byte valid)
    {
        var weap = new WeaponRecord
        {
            FormId = 0x010060F2,
            EditorId = "ProtoWeap",
            AttackAnim = (BethesdaMultitool.Core.Formats.Esm.Enums.AttackAnimation)valid
        };

        var encoded = WeapEncoder.EncodeNew(weap);

        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM");
        Assert.Equal(valid, dnam.Bytes[41]);
    }
}