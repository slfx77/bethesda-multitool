using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Records;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Records;

public sealed class TesFormHeaderProbeTests
{
    [Fact]
    public void TryProbe_ReadsCanonicalTesFormSubobjectIdentity()
    {
        const byte expectedType = 0x28;
        const uint expectedFormId = 0x00123456;
        var header = new byte[TesFormHeaderProbe.RequiredBufferSize];
        header[TesFormHeaderProbe.FormTypeOffset] = expectedType;
        BinaryPrimitives.WriteUInt32BigEndian(
            header.AsSpan(TesFormHeaderProbe.FormIdOffset, sizeof(uint)), expectedFormId);

        var result = TesFormHeaderProbe.TryProbe(
            header, out var formType, out var formId, expectedFormId);

        Assert.True(result);
        Assert.Equal(expectedType, formType);
        Assert.Equal(expectedFormId, formId);
    }

    [Fact]
    public void TryProbe_RejectsCanonicalHeaderWhenExpectedFormIdDiffers()
    {
        var header = new byte[TesFormHeaderProbe.RequiredBufferSize];
        header[TesFormHeaderProbe.FormTypeOffset] = 0x28;
        BinaryPrimitives.WriteUInt32BigEndian(
            header.AsSpan(TesFormHeaderProbe.FormIdOffset, sizeof(uint)), 0x00111111);

        var result = TesFormHeaderProbe.TryProbe(
            header, out var formType, out var formId, 0x00222222);

        Assert.False(result);
        Assert.Equal((byte)0, formType);
        Assert.Equal(0u, formId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(15)]
    public void TryProbe_RejectsShortBuffer(int length)
    {
        var result = TesFormHeaderProbe.TryProbe(
            new byte[length], out var formType, out var formId);

        Assert.False(result);
        Assert.Equal((byte)0, formType);
        Assert.Equal(0u, formId);
    }

    [Theory]
    [InlineData(24, 32, (byte)0x22)] // Old MSTT complete-object candidate.
    [InlineData(16, 24, (byte)0x26)] // Old FLOR complete-object candidate.
    public void TryProbe_DoesNotFallBackToCompleteObjectDecoy(
        int decoyTypeOffset,
        int decoyFormIdOffset,
        byte decoyType)
    {
        const uint canonicalFormId = 0x00111111;
        const uint decoyFormId = 0x00222222;
        var buffer = new byte[36];
        buffer[TesFormHeaderProbe.FormTypeOffset] = 0x28;
        BinaryPrimitives.WriteUInt32BigEndian(
            buffer.AsSpan(TesFormHeaderProbe.FormIdOffset, sizeof(uint)), canonicalFormId);
        buffer[decoyTypeOffset] = decoyType;
        BinaryPrimitives.WriteUInt32BigEndian(
            buffer.AsSpan(decoyFormIdOffset, sizeof(uint)), decoyFormId);

        var result = TesFormHeaderProbe.TryProbe(
            buffer, out var formType, out var formId, decoyFormId);

        Assert.False(result);
        Assert.Equal((byte)0, formType);
        Assert.Equal(0u, formId);
    }
}