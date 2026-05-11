using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

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
}
