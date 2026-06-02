using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Phase 3.2 IMAD encoder coverage. DNAM is the 244-byte mixed-endian payload that
///     <see cref="FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordSchemaProcessor" />
///     special-cases (bytes 0..3 already-LE on Xbox, bytes 4..243 swapped). The PC-output
///     encoder writes the canonical fully-little-endian form; this test pins that form.
/// </summary>
public sealed class ImadEncoderDnamTests : SubrecordEncoderTestBase<ImageSpaceModifierData>
{
    protected override string RecordSignature => "IMAD";

    protected override IReadOnlyCollection<string> EmittedSubrecordSignatures => ["DNAM"];

    protected override ImageSpaceModifierData MakeSyntheticModel()
    {
        // Populate the first few payload slots with distinct uint32 values so a wrong
        // byte order or wrong offset surfaces in the byte-equality check. Leave the
        // remainder as default (zeros).
        var payload = new uint[]
        {
            0x11111111, 0x22222222, 0x33333333, 0x44444444, 0x55555555,
            0x66666666, 0x77777777, 0x88888888
        };
        return new ImageSpaceModifierData
        {
            AnimatableFlag = 0xABCDEF01u,
            Duration = 2.5f,
            RawPayload = payload
        };
    }

    protected override byte[] GetExpectedBytes()
    {
        var expected = new byte[244];
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(0, 4), 0xABCDEF01u);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(4, 4), 2.5f);
        var payload = new uint[]
        {
            0x11111111, 0x22222222, 0x33333333, 0x44444444, 0x55555555,
            0x66666666, 0x77777777, 0x88888888
        };
        for (var i = 0; i < payload.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(8 + i * 4, 4), payload[i]);
        }
        // Remaining bytes 8 + 8*4 = 40 onwards stay zero.
        return expected;
    }

    protected override byte[] EncodeModel(ImageSpaceModifierData model) => ImadEncoder.EncodeDnam(model);

    protected override (bool Parsed, ImageSpaceModifierData? Model) TryParseBytes(byte[] bytes)
    {
        if (bytes.Length != 244)
        {
            return (false, null);
        }

        var animatable = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        var duration = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4, 4));
        var payload = new uint[(244 - 8) / 4];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8 + i * 4, 4));
        }

        return (true, new ImageSpaceModifierData
        {
            AnimatableFlag = animatable,
            Duration = duration,
            RawPayload = payload
        });
    }
}
