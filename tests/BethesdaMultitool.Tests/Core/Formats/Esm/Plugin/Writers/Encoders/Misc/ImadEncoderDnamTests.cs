using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Phase 3.2 IMAD encoder coverage. DNAM is the 244-byte mixed-endian payload that
///     <see cref="BethesdaMultitool.Core.Formats.Esm.Conversion.Schema.SubrecordSchemaProcessor" />
///     special-cases (bytes 0..3 and packed byte quartets 200/224 retain byte order;
///     numeric DWORDs are swapped). The PC-output encoder writes the canonical form.
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
            AnimatableFlag = 1,
            Duration = 2.5f,
            RawPayload = payload
        };
    }

    protected override byte[] GetExpectedBytes()
    {
        var expected = new byte[244];
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(0, 4), 1);
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

    protected override byte[] EncodeModel(ImageSpaceModifierData model)
    {
        return ImadEncoder.EncodeDnam(model);
    }

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