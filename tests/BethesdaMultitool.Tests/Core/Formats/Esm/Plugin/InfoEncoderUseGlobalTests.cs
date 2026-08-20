using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class InfoEncoderUseGlobalTests
{
    [Fact]
    public void BuildCtdaSubrecord_WritesUseGlobalFormIdBitsExactly()
    {
        const uint globalFormId = 0x01ABCDEF;
        var condition = new DialogueCondition
        {
            Type = 0x04,
            ComparisonValue = BitConverter.UInt32BitsToSingle(globalFormId),
            FunctionIndex = 0x48
        };

        var bytes = InfoEncoder.BuildCtdaSubrecord(condition);

        Assert.Equal(28, bytes.Length);
        Assert.Equal((byte)0x04, bytes[0]);
        Assert.Equal(globalFormId, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(globalFormId, condition.ComparisonGlobalFormId);
    }

    [Fact]
    public void BuildCtdaSubrecord_WritesNumericComparisonAsFloat()
    {
        var bytes = InfoEncoder.BuildCtdaSubrecord(new DialogueCondition
        {
            Type = 0,
            ComparisonValue = 3.5f,
            FunctionIndex = 0x0E
        });

        Assert.Equal(3.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4)));
    }

    [Fact]
    public void BuildCtdaSubrecord_RemainsClassic28BytesWhenTypedModelCarriesParameter3()
    {
        var bytes = InfoEncoder.BuildCtdaSubrecord(new DialogueCondition
        {
            FunctionIndex = 0x062,
            RunOn = 7,
            Parameter3 = 0x3152
        });

        Assert.Equal(28, bytes.Length);
    }
}