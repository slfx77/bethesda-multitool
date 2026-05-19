using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class GlobEncoderTests
{
    [Fact]
    public void Encode_EmitsFltvWithLittleEndianFloat()
    {
        var globModel = new GlobalRecord
        {
            FormId = 0x0001234A,
            EditorId = "TestGlob",
            ValueType = 'f',
            Value = 42.25f
        };

        var encoded = new GlobEncoder().Encode(globModel);

        Assert.Single(encoded.Subrecords);
        Assert.Equal("FLTV", encoded.Subrecords[0].Signature);
        var bytes = encoded.Subrecords[0].Bytes;
        Assert.Equal(4, bytes.Length);

        var decoded = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes);
        Assert.Equal(42.25f, decoded);
    }

    [Fact]
    public void Encode_DoesNotEmitEdid()
    {
        // EDID is retained from ESM by the merge engine — encoder must not produce it.
        var encoded = new GlobEncoder().Encode(new GlobalRecord { FormId = 1, ValueType = 'f', Value = 1.0f });
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "EDID");
    }
}
