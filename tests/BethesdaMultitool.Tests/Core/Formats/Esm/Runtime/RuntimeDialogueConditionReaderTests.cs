using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeDialogueConditionReaderTests
{
    private const uint HeapVa = 0x40000000;
    private const int ConditionOffset = 0x40;
    private const int GlobalOffset = 0x80;
    private const int SpeakerOffset = 0xC0;
    private const int ReferenceOffset = 0xE0;
    private const uint GlobalFormId = 0x00123456;
    private const uint SpeakerFormId = 0x000ED239;
    private const uint ReferenceFormId = 0x0010CAFE;

    [Fact]
    public void ReadConditions_UseGlobal_ResolvesGlobalFormIdWithoutInferringSpeaker()
    {
        var buffer = BuildConditionDump(0x24, 0f);
        SyntheticStructFactory.WriteFormHeader(buffer, GlobalOffset, 0x06, GlobalFormId);
        BinaryTestWriter.WriteUInt32BE(buffer, ConditionOffset + 4, HeapVa + GlobalOffset);

        var result = new RuntimeDialogueConditionReader(CreateContext(buffer)).ReadConditions(buffer, 0);

        var condition = Assert.Single(result.Conditions);
        Assert.Equal(GlobalFormId, BitConverter.SingleToUInt32Bits(condition.ComparisonValue));
        Assert.Equal(SpeakerFormId, condition.Parameter1);
        Assert.Null(result.ConditionSpeakerFormId);
    }

    [Fact]
    public void ReadConditions_LiteralPositiveGetIsId_StillInfersSpeaker()
    {
        var buffer = BuildConditionDump(0x00, 1f);

        var result = new RuntimeDialogueConditionReader(CreateContext(buffer)).ReadConditions(buffer, 0);

        var condition = Assert.Single(result.Conditions);
        Assert.Equal(1f, condition.ComparisonValue);
        Assert.Equal(SpeakerFormId, result.ConditionSpeakerFormId);
    }

    [Fact]
    public void ReadConditions_UseGlobal_RejectsWrongTypedPointerTarget()
    {
        var buffer = BuildConditionDump(0x04, 0f);
        BinaryTestWriter.WriteUInt32BE(buffer, ConditionOffset + 4, HeapVa + SpeakerOffset);

        var result = new RuntimeDialogueConditionReader(CreateContext(buffer)).ReadConditions(buffer, 0);

        var condition = Assert.Single(result.Conditions);
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(condition.ComparisonValue));
    }

    [Fact]
    public void ReadConditions_SemanticReference_ResolvesRuntimePointer()
    {
        var buffer = BuildConditionDump(0, 1f, 1, 2, HeapVa + ReferenceOffset);
        SyntheticStructFactory.WriteFormHeader(buffer, ReferenceOffset, 0x3B, ReferenceFormId);

        var condition = Assert.Single(
            new RuntimeDialogueConditionReader(CreateContext(buffer)).ReadConditions(buffer, 0).Conditions);

        Assert.Equal(ReferenceFormId, condition.Reference);
    }

    [Theory]
    [InlineData(0x0001, 4u)]
    [InlineData(0x006A, 2u)]
    [InlineData(0x011D, 2u)]
    public void ReadConditions_NonsemanticReference_DoesNotExposePointerTarget(int functionIndex, uint runOn)
    {
        var buffer = BuildConditionDump(
            0,
            1f,
            (ushort)functionIndex,
            runOn,
            HeapVa + ReferenceOffset);
        SyntheticStructFactory.WriteFormHeader(buffer, ReferenceOffset, 0x3B, ReferenceFormId);

        var condition = Assert.Single(
            new RuntimeDialogueConditionReader(CreateContext(buffer)).ReadConditions(buffer, 0).Conditions);

        Assert.Equal(0u, condition.Reference);
    }

    [Fact]
    public void ReadConditions_RawIndexAtOpcodeBase_DoesNotResolveThroughScriptTable()
    {
        var buffer = BuildConditionDump(0, 1f, 0x1001);

        var condition = Assert.Single(
            new RuntimeDialogueConditionReader(CreateContext(buffer)).ReadConditions(buffer, 0).Conditions);

        Assert.Equal(HeapVa + SpeakerOffset, condition.Parameter1);
    }

    [Fact]
    public void ReadConditions_ActorValueParameter_DoesNotFollowPointerShapedRawValue()
    {
        // GetActorValue's parameter is an enum code, never a TESForm pointer. A pointer-shaped
        // adversarial value makes the old misclassification observable: following it would yield
        // SpeakerFormId from the synthetic target instead of preserving the raw storage.
        var buffer = BuildConditionDump(0, 1f, 0x000E);

        var condition = Assert.Single(
            new RuntimeDialogueConditionReader(CreateContext(buffer)).ReadConditions(buffer, 0).Conditions);

        Assert.Equal(HeapVa + SpeakerOffset, condition.Parameter1);
        Assert.NotEqual(SpeakerFormId, condition.Parameter1);
    }

    private static byte[] BuildConditionDump(
        byte type,
        float comparisonValue,
        ushort functionIndex = 0x48,
        uint runOn = 0,
        uint referencePtr = 0)
    {
        var buffer = new byte[0x120];

        // Inline BSSimpleList head at INFO+0: condition item pointer followed by a null next node.
        BinaryTestWriter.WriteUInt32BE(buffer, 0, HeapVa + ConditionOffset);

        buffer[ConditionOffset] = type;
        BinaryTestWriter.WriteFloatBE(buffer, ConditionOffset + 4, comparisonValue);
        BinaryTestWriter.WriteUInt16BE(buffer, ConditionOffset + 8, functionIndex);
        BinaryTestWriter.WriteUInt32BE(buffer, ConditionOffset + 12, HeapVa + SpeakerOffset);
        BinaryTestWriter.WriteUInt32BE(buffer, ConditionOffset + 20, runOn);
        BinaryTestWriter.WriteUInt32BE(buffer, ConditionOffset + 24, referencePtr);
        SyntheticStructFactory.WriteFormHeader(buffer, SpeakerOffset, 0x2A, SpeakerFormId);

        return buffer;
    }

    private static RuntimeMemoryContext CreateContext(byte[] buffer)
    {
        var minidumpInfo = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = HeapVa,
                    Size = buffer.Length,
                    FileOffset = 0
                }
            ]
        };

        return new RuntimeMemoryContext(new ByteArrayMemoryAccessor(buffer), buffer.Length, minidumpInfo);
    }
}
