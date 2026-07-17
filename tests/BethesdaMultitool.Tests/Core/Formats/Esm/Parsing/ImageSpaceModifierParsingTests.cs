using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class ImageSpaceModifierParsingTests
{
    [Fact]
    public void ParameterEnum_MatchesRecoveredPdbOrdinalTable()
    {
        ImageSpaceModifierParameter[] expected =
        [
            ImageSpaceModifierParameter.EyeAdaptSpeed,
            ImageSpaceModifierParameter.HdrBlurRadius,
            ImageSpaceModifierParameter.HdrSkinDimmer,
            ImageSpaceModifierParameter.HdrEmissiveMult,
            ImageSpaceModifierParameter.HdrTargetLum,
            ImageSpaceModifierParameter.HdrUpperLumClamp,
            ImageSpaceModifierParameter.HdrBrightScale,
            ImageSpaceModifierParameter.HdrBrightClamp,
            ImageSpaceModifierParameter.HdrLumRampNoTex,
            ImageSpaceModifierParameter.HdrLumRampMin,
            ImageSpaceModifierParameter.HdrLumRampMax,
            ImageSpaceModifierParameter.HdrSunlightDimmer,
            ImageSpaceModifierParameter.HdrGrassDimmer,
            ImageSpaceModifierParameter.HdrTreeDimmer,
            ImageSpaceModifierParameter.BloomBlurRadius,
            ImageSpaceModifierParameter.BloomAlphaAddInterior,
            ImageSpaceModifierParameter.BloomAlphaAddExterior,
            ImageSpaceModifierParameter.CinematicSaturation,
            ImageSpaceModifierParameter.CinematicContrastAvgLum,
            ImageSpaceModifierParameter.CinematicContrast,
            ImageSpaceModifierParameter.CinematicBrightness,
        ];

        Assert.Equal(Enumerable.Range(0, 21), expected.Select(value => (int)value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dnam_PreservesAsymmetricXboxEndianLayout(bool bigEndian)
    {
        var bytes = new byte[244];
        // DNAM byte 0 is bAnimatable; the remaining three bytes are padding.
        bytes[0] = 1;
        WriteSingle(bytes.AsSpan(4, 4), 2.5f, bigEndian);
        WriteUInt32(bytes.AsSpan(8, 4), 7, bigEndian);
        bytes[200] = 1; // packed radial-target bool; never DWORD-swapped
        bytes[224] = 1; // packed DoF target + mode; never DWORD-swapped
        bytes[225] = 0x15;
        WriteUInt32(bytes.AsSpan(240, 4), 0xA1B2C3D4, bigEndian);

        var data = MiscEnvironmentHandler.ReadImageSpaceModifierData(bytes, bigEndian);

        Assert.Equal(1u, data.AnimatableFlag);
        Assert.Equal(2.5f, data.Duration);
        Assert.Equal(59, data.RawPayload.Count);
        Assert.Equal(7u, data.RawPayload[0]);
        Assert.Equal(1u, data.RawPayload[48]);
        Assert.Equal(0x00001501u, data.RawPayload[54]);
        Assert.Equal(0xA1B2C3D4u, data.RawPayload[^1]);
    }

    [Fact]
    public void Dnam_XboxToPcConversion_SwapsNumericDwordsButPreservesPackedByteFields()
    {
        var xbox = new byte[244];
        BinaryPrimitives.WriteUInt32LittleEndian(xbox.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteSingleBigEndian(xbox.AsSpan(4, 4), 3.5f);
        BinaryPrimitives.WriteUInt32BigEndian(xbox.AsSpan(8, 4), 2);
        xbox[200] = 1;
        xbox[224] = 1;
        xbox[225] = 0x2A;
        BinaryPrimitives.WriteUInt32BigEndian(xbox.AsSpan(228, 4), 3);

        var pc = SubrecordSchemaProcessor.ConvertWithSchema("DNAM", xbox, "IMAD");

        Assert.NotNull(pc);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(pc.AsSpan(0, 4)));
        Assert.Equal(3.5f, BinaryPrimitives.ReadSingleLittleEndian(pc.AsSpan(4, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(pc.AsSpan(8, 4)));
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, pc[200..204]);
        Assert.Equal(new byte[] { 1, 0x2A, 0, 0 }, pc[224..228]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(pc.AsSpan(228, 4)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameTables_ParseEveryCompleteScalarAndColorKey(bool bigEndian)
    {
        var scalar = new byte[17]; // trailing byte is preserved by raw-subrecord authority, not invented as a key
        WriteSingle(scalar.AsSpan(0, 4), 0f, bigEndian);
        WriteSingle(scalar.AsSpan(4, 4), 1.25f, bigEndian);
        WriteSingle(scalar.AsSpan(8, 4), 1f, bigEndian);
        WriteSingle(scalar.AsSpan(12, 4), 2.5f, bigEndian);
        scalar[^1] = 0xCC;

        var color = new byte[20];
        var values = new[] { 0.5f, 0.1f, 0.2f, 0.3f, 0.4f };
        for (var i = 0; i < values.Length; i++) WriteSingle(color.AsSpan(i * 4, 4), values[i], bigEndian);

        var scalarKeys = MiscEnvironmentHandler.ReadImageSpaceModifierFloatKeys(scalar, bigEndian);
        var colorKeys = MiscEnvironmentHandler.ReadImageSpaceModifierColorKeys(color, bigEndian);

        Assert.Equal(2, scalarKeys.Count);
        Assert.Equal(new ImageSpaceModifierFloatKey(0f, 1.25f), scalarKeys[0]);
        Assert.Equal(new ImageSpaceModifierFloatKey(1f, 2.5f), scalarKeys[1]);
        Assert.Single(colorKeys);
        Assert.Equal(new ImageSpaceModifierColorKey(0.5f, 0.1f, 0.2f, 0.3f, 0.4f), colorKeys[0]);
    }

    [Theory]
    [InlineData('\0', ImageSpaceModifierParameter.EyeAdaptSpeed, ImageSpaceModifierOperation.Multiply)]
    [InlineData('\u0014', ImageSpaceModifierParameter.CinematicBrightness, ImageSpaceModifierOperation.Multiply)]
    [InlineData('@', ImageSpaceModifierParameter.EyeAdaptSpeed, ImageSpaceModifierOperation.Add)]
    [InlineData('T', ImageSpaceModifierParameter.CinematicBrightness, ImageSpaceModifierOperation.Add)]
    public void ParameterSignatures_RetainAllTwentyOnePairedChannels(
        char prefix, ImageSpaceModifierParameter expectedParameter, ImageSpaceModifierOperation expectedOperation)
    {
        Assert.True(MiscEnvironmentHandler.TryImageSpaceModifierParameterSignature(
            $"{prefix}IAD", out var parameter, out var operation));
        Assert.Equal(expectedParameter, parameter);
        Assert.Equal(expectedOperation, operation);
    }

    private static void WriteSingle(Span<byte> destination, float value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteSingleBigEndian(destination, value);
        else BinaryPrimitives.WriteSingleLittleEndian(destination, value);
    }

    private static void WriteUInt32(Span<byte> destination, uint value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(destination, value);
        else BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
    }
}
