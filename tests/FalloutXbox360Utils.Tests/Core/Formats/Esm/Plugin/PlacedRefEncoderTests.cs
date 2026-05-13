using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class PlacedRefEncoderTests
{
    [Fact]
    public void RefrEncoder_Override_EmitsDataWithDmpPosition()
    {
        // v22: override path emits DATA carrying the DMP-captured X/Y/Z/RotX/RotY/RotZ.
        // Earlier versions dropped DATA to retain vanilla's editor spawn (sinking-bug
        // mitigation); the root cause was traced to dropped vanilla NAVMs (v21 fix) and
        // DATA is now safe to re-emit.
        var refr = new PlacedReference
        {
            FormId = 0x0017B37C,
            X = 100.5f,
            Y = -200.25f,
            Z = 50.0f,
            RotX = 0.5f,
            RotY = -1.25f,
            RotZ = 3.14159f,
            Scale = 1.0f
        };

        var encoded = new RefrEncoder().Encode(refr);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(24, data.Bytes.Length);
        Assert.Equal(100.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(0, 4)));
        Assert.Equal(-200.25f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(4, 4)));
        Assert.Equal(50.0f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(8, 4)));
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(12, 4)));
        Assert.Equal(-1.25f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(16, 4)));
        Assert.Equal(3.14159f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(20, 4)));
    }

    [Fact]
    public void RefrEncoder_DefaultScale_EmitsDataOnly()
    {
        // Default scale → XSCL omitted; DATA still emits with the DMP position.
        var refr = new PlacedReference { FormId = 1, X = 7.0f, Scale = 1.0f };

        var encoded = new RefrEncoder().Encode(refr);

        var data = Assert.Single(encoded.Subrecords);
        Assert.Equal("DATA", data.Signature);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XSCL");
    }

    [Fact]
    public void RefrEncoder_NonDefaultScale_EmitsDataAndXscl()
    {
        var refr = new PlacedReference { FormId = 1, Scale = 2.5f };

        var encoded = new RefrEncoder().Encode(refr);

        Assert.Equal(2, encoded.Subrecords.Count);
        Assert.Equal("DATA", encoded.Subrecords[0].Signature);
        Assert.Equal("XSCL", encoded.Subrecords[1].Signature);
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(encoded.Subrecords[1].Bytes));
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
        for (var i = 0; i < refrOut.Subrecords.Count; i++)
        {
            Assert.Equal(refrOut.Subrecords[i].Signature, achrOut.Subrecords[i].Signature);
            Assert.Equal(refrOut.Subrecords[i].Bytes, achrOut.Subrecords[i].Bytes);
        }
    }

    [Fact]
    public void AcreEncoder_ProducesSameLayoutAsRefr()
    {
        var placed = new PlacedReference { FormId = 1, Scale = 2.0f, X = 5.0f };

        var refrOut = new RefrEncoder().Encode(placed);
        var acreOut = new AcreEncoder().Encode(placed);

        Assert.Equal(refrOut.Subrecords.Count, acreOut.Subrecords.Count);
        for (var i = 0; i < refrOut.Subrecords.Count; i++)
        {
            Assert.Equal(refrOut.Subrecords[i].Signature, acreOut.Subrecords[i].Signature);
            Assert.Equal(refrOut.Subrecords[i].Bytes, acreOut.Subrecords[i].Bytes);
        }
    }
}
