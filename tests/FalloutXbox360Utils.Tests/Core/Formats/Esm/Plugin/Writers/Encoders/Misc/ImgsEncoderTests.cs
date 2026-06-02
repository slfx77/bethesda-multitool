using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Phase 3.2 IMGS encoder coverage. HNAM is the largest fixed-size IMGS subrecord
///     (9 floats) so it gets the focused byte-level scaffold; CNAM (3 floats), TNAM
///     (4 floats), and DNAM (variable float array) share the same single-precision-LE
///     write path and are exercised at the encoder integration level only.
/// </summary>
public sealed class ImgsEncoderHnamTests : SubrecordEncoderTestBase<ImageSpaceHdr>
{
    protected override string RecordSignature => "IMGS";

    protected override IReadOnlyCollection<string> EmittedSubrecordSignatures => ["HNAM"];

    protected override ImageSpaceHdr MakeSyntheticModel()
    {
        return new ImageSpaceHdr
        {
            EyeAdaptSpeed = 0.5f,
            BlurRadius = 7.0f,
            BlurPasses = 1.0f,
            EmissiveMult = 2.0f,
            TargetLum = 0.4f,
            UpperLumClamp = 1.0f,
            BrightScale = 1.5f,
            BrightClamp = 0.9f,
            LumRampNoTex = 0.7f
        };
    }

    protected override byte[] GetExpectedBytes()
    {
        var expected = new byte[36];
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(0, 4), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(4, 4), 7.0f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(8, 4), 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(12, 4), 2.0f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(16, 4), 0.4f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(20, 4), 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(24, 4), 1.5f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(28, 4), 0.9f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(32, 4), 0.7f);
        return expected;
    }

    protected override byte[] EncodeModel(ImageSpaceHdr model) => ImgsEncoder.EncodeHnam(model);

    protected override (bool Parsed, ImageSpaceHdr? Model) TryParseBytes(byte[] bytes)
    {
        if (bytes.Length != 36)
        {
            return (false, null);
        }

        return (true, new ImageSpaceHdr
        {
            EyeAdaptSpeed = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(0, 4)),
            BlurRadius = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4, 4)),
            BlurPasses = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(8, 4)),
            EmissiveMult = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(12, 4)),
            TargetLum = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(16, 4)),
            UpperLumClamp = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(20, 4)),
            BrightScale = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(24, 4)),
            BrightClamp = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(28, 4)),
            LumRampNoTex = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(32, 4))
        });
    }
}
