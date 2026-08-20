using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeMemoryContextSimpleListTests
{
    [Fact]
    public void WalkSimpleList_StitchesVaContiguousNodeHeaderAtNonContiguousFileOffsets()
    {
        const uint inlineItem = 0x81111111;
        const uint nodeItem = 0x82222222;
        const uint tailItem = 0x83333333;
        const uint nodeVa = 0x82002000;
        const uint tailNodeVa = 0x82003000;
        var data = new byte[128];
        WriteUInt32(data, 12, nodeItem);
        WriteUInt32(data, 80, tailNodeVa);
        WriteNode(data, 96, tailItem, 0);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion
            {
                VirtualAddress = Xbox360MemoryUtils.VaToLong(nodeVa),
                Size = 4,
                FileOffset = 12
            },
            new MinidumpMemoryRegion
            {
                VirtualAddress = Xbox360MemoryUtils.VaToLong(nodeVa) + 4,
                Size = 4,
                FileOffset = 80
            },
            new MinidumpMemoryRegion
            {
                VirtualAddress = Xbox360MemoryUtils.VaToLong(tailNodeVa),
                Size = 8,
                FileOffset = 96
            });
        var listHead = CreateListHead(inlineItem, nodeVa);

        var items = context.WalkInlineBSSimpleListItemPointers(listHead, 0, 3).ToArray();

        Assert.Equal([inlineItem, nodeItem, tailItem], items);
    }

    [Fact]
    public void WalkSimpleList_StopsAtVaGapEvenWhenFlatNodeHeaderLooksValid()
    {
        const uint inlineItem = 0x83333333;
        const uint falseNodeItem = 0x84444444;
        const uint nodeVa = 0x3000;
        var data = new byte[64];
        WriteNode(data, 12, falseNodeItem, 0);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion { VirtualAddress = nodeVa, Size = 4, FileOffset = 12 },
            new MinidumpMemoryRegion { VirtualAddress = nodeVa + 5, Size = 4, FileOffset = 16 });
        var listHead = CreateListHead(inlineItem, nodeVa);

        var items = context.WalkInlineBSSimpleListItemPointers(listHead, 0, 2).ToArray();

        Assert.Equal([inlineItem], items);
    }

    [Fact]
    public void WalkSimpleList_MaxOneDoesNotFollowNextPointerWhenInlineItemIsNull()
    {
        const uint nodeItem = 0x85555555;
        const uint nodeVa = 0x4000;
        var data = new byte[32];
        WriteNode(data, 8, nodeItem, 0);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion { VirtualAddress = nodeVa, Size = 8, FileOffset = 8 });
        var listHead = CreateListHead(0, nodeVa);

        var items = context.WalkInlineBSSimpleListItemPointers(listHead, 0, 1).ToArray();

        Assert.Empty(items);
    }

    [Fact]
    public void WalkSimpleList_RejectsOverflowingListOffset()
    {
        var context = CreateContext([]);

        var items = context.WalkInlineBSSimpleListItemPointers(
            new byte[8], int.MaxValue, 1).ToArray();

        Assert.Empty(items);
    }

    [Fact]
    public void WalkSimpleList_NullInlineAndHeapItemsConsumeTraversalBudget()
    {
        const uint nodeVa = 0x5000;
        const uint beyondBudgetItem = 0x86666666;
        var data = new byte[64];
        WriteNode(data, 8, 0, nodeVa + 8);
        WriteNode(data, 16, 0, nodeVa + 16);
        WriteNode(data, 24, beyondBudgetItem, 0);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion { VirtualAddress = nodeVa, Size = 24, FileOffset = 8 });
        var listHead = CreateListHead(0, nodeVa);

        var items = context.WalkInlineBSSimpleListItemPointers(listHead, 0, 3).ToArray();

        Assert.Empty(items);
    }

    [Fact]
    public void WalkSimpleList_StopsAtSelfAndTwoNodeCycles()
    {
        const uint inlineItem = 0x87777777;
        const uint firstNodeItem = 0x88888888;
        const uint secondNodeItem = 0x89999999;
        const uint selfNodeVa = 0x6000;
        var selfCycleData = new byte[32];
        WriteNode(selfCycleData, 8, firstNodeItem, selfNodeVa);
        var selfCycleContext = CreateContext(
            selfCycleData,
            new MinidumpMemoryRegion { VirtualAddress = selfNodeVa, Size = 8, FileOffset = 8 });

        var selfCycleItems = selfCycleContext.WalkInlineBSSimpleListItemPointers(
            CreateListHead(inlineItem, selfNodeVa), 0, 10).ToArray();

        Assert.Equal([inlineItem, firstNodeItem], selfCycleItems);

        const uint firstNodeVa = 0x7000;
        var twoNodeCycleData = new byte[40];
        WriteNode(twoNodeCycleData, 8, firstNodeItem, firstNodeVa + 8);
        WriteNode(twoNodeCycleData, 16, secondNodeItem, firstNodeVa);
        var twoNodeCycleContext = CreateContext(
            twoNodeCycleData,
            new MinidumpMemoryRegion { VirtualAddress = firstNodeVa, Size = 16, FileOffset = 8 });

        var twoNodeCycleItems = twoNodeCycleContext.WalkInlineBSSimpleListItemPointers(
            CreateListHead(inlineItem, firstNodeVa), 0, 10).ToArray();

        Assert.Equal([inlineItem, firstNodeItem, secondNodeItem], twoNodeCycleItems);
    }

    [Fact]
    public void ReadSimpleList_RejectedItemsConsumeTraversalBudget()
    {
        const uint inlineRejectedItem = 0x8A111111;
        const uint heapRejectedItem = 0x8A222222;
        const uint beyondBudgetItem = 0x8A333333;
        const uint nodeVa = 0x8000;
        var data = new byte[48];
        WriteNode(data, 8, heapRejectedItem, nodeVa + 8);
        WriteNode(data, 16, beyondBudgetItem, 0);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion { VirtualAddress = nodeVa, Size = 16, FileOffset = 8 });
        var fields = new RuntimePdbFieldAccessor(context);

        var items = fields.ReadSimpleList(
            CreateListHead(inlineRejectedItem, nodeVa),
            0,
            pointer => pointer == beyondBudgetItem ? "accepted" : null,
            2);

        Assert.Empty(items);
    }

    [Fact]
    public void ReadFormIdSimpleList_NullAndRejectedItemsConsumeTraversalBudget()
    {
        const byte expectedFormType = 0x29;
        const uint rejectedFormVa = 0xA000;
        const uint acceptedFormVa = 0xA100;
        const uint acceptedFormId = 0x00123456;
        const uint nodeVa = 0x9000;
        var data = new byte[128];
        WriteNode(data, 8, rejectedFormVa, nodeVa + 8);
        WriteNode(data, 16, acceptedFormVa, 0);
        WriteTesFormHeader(data, 48, 0x28, 0x00654321);
        WriteTesFormHeader(data, 80, expectedFormType, acceptedFormId);
        var context = CreateContext(
            data,
            new MinidumpMemoryRegion { VirtualAddress = nodeVa, Size = 16, FileOffset = 8 },
            new MinidumpMemoryRegion { VirtualAddress = rejectedFormVa, Size = 16, FileOffset = 48 },
            new MinidumpMemoryRegion { VirtualAddress = acceptedFormVa, Size = 16, FileOffset = 80 });
        var fields = new RuntimePdbFieldAccessor(context);

        var formIds = fields.ReadFormIdSimpleList(
            CreateListHead(0, nodeVa),
            0,
            expectedFormType,
            2);

        Assert.Empty(formIds);
    }

    private static RuntimeMemoryContext CreateContext(
        byte[] data,
        params MinidumpMemoryRegion[] regions)
    {
        var info = new MinidumpInfo
        {
            IsValid = true,
            MemoryRegions = [.. regions]
        };
        return new RuntimeMemoryContext(new ByteArrayMemoryAccessor(data), data.Length, info);
    }

    private static byte[] CreateListHead(uint itemPointer, uint nextPointer)
    {
        var result = new byte[8];
        WriteNode(result, 0, itemPointer, nextPointer);
        return result;
    }

    private static void WriteNode(byte[] data, int offset, uint itemPointer, uint nextPointer)
    {
        WriteUInt32(data, offset, itemPointer);
        WriteUInt32(data, offset + 4, nextPointer);
    }

    private static void WriteTesFormHeader(byte[] data, int offset, byte formType, uint formId)
    {
        data[offset + 4] = formType;
        WriteUInt32(data, offset + 12, formId);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
    }
}