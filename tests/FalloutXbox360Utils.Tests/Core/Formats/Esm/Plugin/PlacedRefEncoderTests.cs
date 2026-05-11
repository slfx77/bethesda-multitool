using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class PlacedRefEncoderTests
{
    [Fact]
    public void RefrEncoder_DataLayout_IsSixFloats()
    {
        var refr = new PlacedReference
        {
            FormId = 0x0017B37C,
            X = 100.5f,
            Y = -200.25f,
            Z = 50.0f,
            RotX = 0.5f,
            RotY = 1.5f,
            RotZ = 3.14159f,
            Scale = 1.0f
        };

        var encoded = new RefrEncoder().Encode(refr);

        var data = Assert.Single(encoded.Subrecords);
        Assert.Equal("DATA", data.Signature);
        Assert.Equal(24, data.Bytes.Length);
        Assert.Equal(100.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(0, 4)));
        Assert.Equal(-200.25f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(4, 4)));
        Assert.Equal(50.0f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(8, 4)));
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(12, 4)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(16, 4)));
        Assert.Equal(3.14159f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(20, 4)));
    }

    [Fact]
    public void RefrEncoder_DefaultScale_OmitsXscl()
    {
        var refr = new PlacedReference { FormId = 1, Scale = 1.0f };

        var encoded = new RefrEncoder().Encode(refr);

        Assert.Single(encoded.Subrecords);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XSCL");
    }

    [Fact]
    public void RefrEncoder_NonDefaultScale_EmitsXscl()
    {
        var refr = new PlacedReference { FormId = 1, Scale = 2.5f };

        var encoded = new RefrEncoder().Encode(refr);

        Assert.Equal(2, encoded.Subrecords.Count);
        var xscl = Assert.Single(encoded.Subrecords, s => s.Signature == "XSCL");
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(xscl.Bytes));
    }

    [Fact]
    public void AchrEncoder_ProducesSameLayoutAsRefr()
    {
        var placed = new PlacedReference
        {
            FormId = 1, X = 1.0f, Y = 2.0f, Z = 3.0f, Scale = 1.5f
        };

        var refrOut = new RefrEncoder().Encode(placed);
        var achrOut = new AchrEncoder().Encode(placed);

        Assert.Equal(refrOut.Subrecords.Count, achrOut.Subrecords.Count);
        Assert.Equal(refrOut.Subrecords[0].Bytes, achrOut.Subrecords[0].Bytes);
    }

    [Fact]
    public void AcreEncoder_ProducesSameLayoutAsRefr()
    {
        var placed = new PlacedReference { FormId = 1, X = 5.0f };

        var refrOut = new RefrEncoder().Encode(placed);
        var acreOut = new AcreEncoder().Encode(placed);

        Assert.Equal(refrOut.Subrecords[0].Bytes, acreOut.Subrecords[0].Bytes);
    }
}
