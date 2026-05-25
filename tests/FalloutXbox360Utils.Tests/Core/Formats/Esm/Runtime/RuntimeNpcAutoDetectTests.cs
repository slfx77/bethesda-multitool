using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeNpcAutoDetectTests : RuntimeStructReaderTestBase
{
    // NPC tests use a two-region synthetic dump: heap (records / pointers) + module (FaceGen
    // float arrays referenced via ModuleVa). The base class's CreateReader builds a
    // single-region dump, so we use MapSyntheticBytes + build our own MinidumpInfo instead.
    private const int HeapRegionSize = 8192;
    private const int ModuleRegionSize = 8192;
    private const int ModuleRegionFileOffset = HeapRegionSize;
    private const int TotalSize = HeapRegionSize + ModuleRegionSize;
    private const uint ModuleBaseVa = 0x66000000;

    [Fact]
    public void CreateWithAutoDetect_DefaultNpcLayout_PreservesFaceGen()
    {
        var data = new byte[TotalSize];
        var entry = WriteNpcRecord(data, 0x100, 0x00122B99, 16, 16);

        var accessor = MapSyntheticBytes(data);
        var reader = RuntimeStructReader.CreateWithAutoDetect(
            accessor,
            data.Length,
            TwoRegionMinidumpInfo(),
            Array.Empty<RuntimeEditorIdEntry>(),
            [entry]);

        var npc = reader.ReadRuntimeNpc(entry);

        Assert.NotNull(npc);
        Assert.Equal(50, npc!.FaceGenGeometrySymmetric!.Length);
        Assert.Equal(30, npc.FaceGenGeometryAsymmetric!.Length);
        Assert.Equal(50, npc.FaceGenTextureSymmetric!.Length);
        Assert.Equal(0x0000000Cu, npc.Race);
        Assert.Equal(0x0000000Au, npc.HairFormId);
        Assert.Equal(0x0000000Bu, npc.EyesFormId);
    }

    [Fact]
    public void CreateWithAutoDetect_ShiftedNpcAppearanceLayout_UsesAppearanceShiftCandidate()
    {
        var data = new byte[TotalSize];
        var entry = WriteNpcRecord(data, 0x180, 0x001300E2, 16, 28);

        var accessor = MapSyntheticBytes(data);
        var reader = RuntimeStructReader.CreateWithAutoDetect(
            accessor,
            data.Length,
            TwoRegionMinidumpInfo(),
            Array.Empty<RuntimeEditorIdEntry>(),
            [entry]);

        var npc = reader.ReadRuntimeNpc(entry);

        Assert.NotNull(npc);
        Assert.Equal(50, npc!.FaceGenGeometrySymmetric!.Length);
        Assert.Equal(30, npc.FaceGenGeometryAsymmetric!.Length);
        Assert.Equal(50, npc.FaceGenTextureSymmetric!.Length);
        Assert.NotNull(npc.HeadPartFormIds);
        Assert.Single(npc.HeadPartFormIds!);
        Assert.Equal(0x0000004Au, npc.CombatStyleFormId);
        Assert.Equal(0x0000000Cu, npc.OriginalRace);
    }

    [Fact]
    public void CreateWithAutoDetect_PrimitiveArrayDebugLayout_UsesContainerCandidate()
    {
        var data = new byte[TotalSize];
        var entry = WritePrimitiveArrayNpcRecord(data, 0x140, 0x0012A111, 16);
        var entry2 = WritePrimitiveArrayNpcRecord(data, 0x700, 0x0012A112, 16);

        var accessor = MapSyntheticBytes(data);
        var minidumpInfo = TwoRegionMinidumpInfo();
        var runtimeContext =
            new RuntimeMemoryContext(new MmfMemoryAccessor(accessor), data.Length, minidumpInfo);
        var probe = RuntimeNpcLayoutProbe.Probe(runtimeContext, [entry, entry2]);
        var reader = new RuntimeStructReader(
            new MmfMemoryAccessor(accessor),
            data.Length,
            minidumpInfo,
            false,
            new RuntimeNpcLayoutProbeResult(
                RuntimeNpcLayout.CreatePrimitiveArrayDebug(16, 0, 640),
                true,
                probe.WinnerScore,
                probe.RunnerUpScore,
                probe.SampleCount));

        var npc = reader.ReadRuntimeNpc(entry);

        Assert.True(probe.IsHighConfidence);
        Assert.Equal(RuntimeNpcFaceGenArrayMode.PrimitiveArray, probe.Layout.FaceGenMode);
        Assert.Equal(16, probe.Layout.CoreShift);
        Assert.Equal(0, probe.Layout.AppearanceShift);
        Assert.NotNull(npc);
        Assert.Equal(50, npc!.FaceGenGeometrySymmetric!.Length);
        Assert.Equal(30, npc.FaceGenGeometryAsymmetric!.Length);
        Assert.Equal(50, npc.FaceGenTextureSymmetric!.Length);
        Assert.Equal(0x0000000Au, npc.HairFormId);
        Assert.Equal(0x0000000Bu, npc.EyesFormId);
        Assert.Equal(0x0000004Au, npc.CombatStyleFormId);
        Assert.NotNull(npc.HeadPartFormIds);
        Assert.Single(npc.HeadPartFormIds!);
    }

    [Fact]
    public void LowConfidenceNpcLayout_DropsRuntimeFaceGenButKeepsCoreFields()
    {
        var data = new byte[TotalSize];
        var entry = WriteNpcRecord(data, 0x1C0, 0x00131F77, 16, 28);

        var accessor = MapSyntheticBytes(data);
        var reader = new RuntimeStructReader(
            new MmfMemoryAccessor(accessor),
            data.Length,
            TwoRegionMinidumpInfo(),
            false,
            new RuntimeNpcLayoutProbeResult(RuntimeNpcLayout.CreateDirect(16, 28, 640), false, 10, 10, 1));

        var npc = reader.ReadRuntimeNpc(entry);

        Assert.NotNull(npc);
        Assert.Null(npc!.FaceGenGeometrySymmetric);
        Assert.Null(npc.FaceGenGeometryAsymmetric);
        Assert.Null(npc.FaceGenTextureSymmetric);
        Assert.Equal(0x0000000Cu, npc.Race);
        Assert.NotNull(npc.SpecialStats);
        Assert.NotNull(npc.Skills);
    }

    private static RuntimeEditorIdEntry WriteNpcRecord(
        byte[] data,
        int npcOffset,
        uint formId,
        int coreShift,
        int appearanceShift)
    {
        const int raceOffset = 0x500;
        const int classOffset = 0x520;
        const int scriptOffset = 0x540;
        const int hairOffset = 0x560;
        const int eyesOffset = 0x580;
        const int combatStyleOffset = 0x5A0;
        const int headPartOffset = 0x5C0;

        WriteTesFormHeader(data, npcOffset, 0x82010000, 0x2A, formId);
        WriteTesFormHeader(data, raceOffset, 0x82010000, 0x0C, 0x0000000C);
        WriteTesFormHeader(data, classOffset, 0x82010000, 0x07, 0x00000007);
        WriteTesFormHeader(data, scriptOffset, 0x82010000, 0x11, 0x00000011);
        WriteTesFormHeader(data, hairOffset, 0x82010000, 0x0A, 0x0000000A);
        WriteTesFormHeader(data, eyesOffset, 0x82010000, 0x0B, 0x0000000B);
        WriteTesFormHeader(data, combatStyleOffset, 0x82010000, 0x4A, 0x0000004A);
        WriteTesFormHeader(data, headPartOffset, 0x82010000, 0x09, 0x00000009);

        WriteAcbs(data, npcOffset + 52 + coreShift);
        WriteNpcAiData(data, npcOffset + 148 + coreShift);
        WriteNpcSpecial(data, npcOffset + 188 + coreShift);
        WriteNpcSkills(data, npcOffset + 276 + coreShift);

        WriteUInt32BE(data, npcOffset + 248 + coreShift, FileOffsetToVa(scriptOffset));
        WriteUInt32BE(data, npcOffset + 272 + coreShift, FileOffsetToVa(raceOffset));
        WriteUInt32BE(data, npcOffset + 304 + coreShift, FileOffsetToVa(classOffset));

        WriteFaceGenArray(data, 0x100, npcOffset + 320 + appearanceShift, npcOffset + 332 + appearanceShift, 50, 0.10f);
        WriteFaceGenArray(data, 0x300, npcOffset + 352 + appearanceShift, npcOffset + 364 + appearanceShift, 30, 0.20f);
        WriteFaceGenArray(data, 0x500, npcOffset + 384 + appearanceShift, npcOffset + 396 + appearanceShift, 50, 0.30f);

        WriteUInt32BE(data, npcOffset + 440 + appearanceShift, FileOffsetToVa(hairOffset));
        WriteFloatBE(data, npcOffset + 444 + appearanceShift, 0.65f);
        WriteUInt32BE(data, npcOffset + 448 + appearanceShift, FileOffsetToVa(eyesOffset));
        WriteUInt16BE(data, npcOffset + 464 + appearanceShift, 7);
        WriteUInt32BE(data, npcOffset + 468 + appearanceShift, FileOffsetToVa(combatStyleOffset));
        WriteUInt32BE(data, npcOffset + 472 + appearanceShift, 0x001E140A);
        WriteUInt32BE(data, npcOffset + 476 + appearanceShift, FileOffsetToVa(headPartOffset));
        WriteUInt32BE(data, npcOffset + 480 + appearanceShift, 0);
        data[npcOffset + 484 + appearanceShift] = 1;
        WriteUInt32BE(data, npcOffset + 492 + appearanceShift, FileOffsetToVa(raceOffset));
        WriteUInt32BE(data, npcOffset + 496 + appearanceShift, FileOffsetToVa(npcOffset));
        WriteFloatBE(data, npcOffset + 500 + appearanceShift, 1.02f);
        WriteFloatBE(data, npcOffset + 504 + appearanceShift, 45.0f);

        return new RuntimeEditorIdEntry
        {
            EditorId = $"Npc_{formId:X8}",
            FormId = formId,
            FormType = 0x2A,
            TesFormOffset = npcOffset,
            TesFormPointer = Xbox360MemoryUtils.VaToLong(FileOffsetToVa(npcOffset)),
            DisplayName = "Synthetic NPC"
        };
    }

    private static RuntimeEditorIdEntry WritePrimitiveArrayNpcRecord(
        byte[] data,
        int npcOffset,
        uint formId,
        int coreShift)
    {
        const int raceOffset = 0x500;
        const int classOffset = 0x520;
        const int scriptOffset = 0x540;
        const int hairOffset = 0x560;
        const int eyesOffset = 0x580;
        const int combatStyleOffset = 0x5A0;
        const int headPartOffset = 0x5C0;

        WriteTesFormHeader(data, npcOffset, 0x82010000, 0x2A, formId);
        WriteTesFormHeader(data, raceOffset, 0x82010000, 0x0C, 0x0000000C);
        WriteTesFormHeader(data, classOffset, 0x82010000, 0x07, 0x00000007);
        WriteTesFormHeader(data, scriptOffset, 0x82010000, 0x11, 0x00000011);
        WriteTesFormHeader(data, hairOffset, 0x82010000, 0x0A, 0x0000000A);
        WriteTesFormHeader(data, eyesOffset, 0x82010000, 0x0B, 0x0000000B);
        WriteTesFormHeader(data, combatStyleOffset, 0x82010000, 0x4A, 0x0000004A);
        WriteTesFormHeader(data, headPartOffset, 0x82010000, 0x09, 0x00000009);

        WriteAcbs(data, npcOffset + 52 + coreShift);
        WriteNpcAiData(data, npcOffset + 148 + coreShift);
        WriteNpcSpecial(data, npcOffset + 188 + coreShift);
        WriteNpcSkills(data, npcOffset + 276 + coreShift);

        WriteUInt32BE(data, npcOffset + 248 + coreShift, FileOffsetToVa(scriptOffset));
        WriteUInt32BE(data, npcOffset + 272 + coreShift, FileOffsetToVa(raceOffset));
        WriteUInt32BE(data, npcOffset + 304 + coreShift, FileOffsetToVa(classOffset));

        WritePrimitiveFaceGenArray(data, 0x100, npcOffset + 332, npcOffset + 336, npcOffset + 340, npcOffset + 344,
            npcOffset + 348, 50, 0.10f);
        WritePrimitiveFaceGenArray(data, 0x300, npcOffset + 360, npcOffset + 364, npcOffset + 368, npcOffset + 372,
            npcOffset + 376, 30, 0.20f);
        WritePrimitiveFaceGenArray(data, 0x500, npcOffset + 388, npcOffset + 392, npcOffset + 396, npcOffset + 400,
            npcOffset + 404, 50, 0.30f);

        WriteUInt32BE(data, npcOffset + 440, FileOffsetToVa(hairOffset));
        WriteFloatBE(data, npcOffset + 444, 0.65f);
        WriteUInt32BE(data, npcOffset + 448, FileOffsetToVa(eyesOffset));
        WriteUInt16BE(data, npcOffset + 464, 7);
        WriteUInt32BE(data, npcOffset + 468, FileOffsetToVa(combatStyleOffset));
        WriteUInt32BE(data, npcOffset + 472, 0x001E140A);
        WriteUInt32BE(data, npcOffset + 476, FileOffsetToVa(headPartOffset));
        WriteUInt32BE(data, npcOffset + 480, 0);
        data[npcOffset + 484] = 1;

        return new RuntimeEditorIdEntry
        {
            EditorId = $"Npc_{formId:X8}_Debug",
            FormId = formId,
            FormType = 0x2A,
            TesFormOffset = npcOffset,
            TesFormPointer = Xbox360MemoryUtils.VaToLong(FileOffsetToVa(npcOffset)),
            DisplayName = "Synthetic NPC Debug"
        };
    }

    private static void WriteAcbs(byte[] data, int offset)
    {
        WriteUInt32BE(data, offset, 0);
        WriteUInt16BE(data, offset + 4, 100);
        WriteUInt16BE(data, offset + 6, 25);
        WriteUInt16BE(data, offset + 8, 10);
        WriteUInt16BE(data, offset + 10, 1);
        WriteUInt16BE(data, offset + 12, 10);
        WriteUInt16BE(data, offset + 14, 100);
        WriteFloatBE(data, offset + 16, 0.0f);
        WriteUInt16BE(data, offset + 20, 5);
        WriteUInt16BE(data, offset + 22, 0);
    }

    private static void WriteNpcAiData(byte[] data, int offset)
    {
        data[offset] = 1;
        data[offset + 1] = 3;
        data[offset + 2] = 50;
        data[offset + 3] = 50;
        data[offset + 4] = 2;
        WriteUInt32BE(data, offset + 8, 0);
        data[offset + 14] = 1;
    }

    private static void WriteNpcSpecial(byte[] data, int offset)
    {
        for (var i = 0; i < 7; i++)
        {
            data[offset + i] = 5;
        }
    }

    private static void WriteNpcSkills(byte[] data, int offset)
    {
        for (var i = 0; i < 14; i++)
        {
            data[offset + i] = (byte)(20 + i);
        }
    }

    private static void WriteFaceGenArray(
        byte[] data,
        int moduleRegionOffset,
        int pointerOffset,
        int countOffset,
        int count,
        float baseValue)
    {
        WriteUInt32BE(data, pointerOffset, ModuleVa(moduleRegionOffset));
        WriteUInt32BE(data, countOffset, (uint)count);

        var fileOffset = ModuleRegionFileOffset + moduleRegionOffset;
        for (var i = 0; i < count; i++)
        {
            WriteFloatBE(data, fileOffset + i * 4, baseValue + i * 0.01f);
        }
    }

    private static void WritePrimitiveFaceGenArray(
        byte[] data,
        int moduleRegionOffset,
        int pointerOffset,
        int endPointerOffset,
        int capacityPointerOffset,
        int countOffset,
        int growByOffset,
        int count,
        float baseValue)
    {
        var startVa = ModuleVa(moduleRegionOffset);
        var endVa = startVa + (uint)(count * 4);

        WriteUInt32BE(data, pointerOffset, startVa);
        WriteUInt32BE(data, endPointerOffset, endVa);
        WriteUInt32BE(data, capacityPointerOffset, endVa);
        WriteUInt32BE(data, countOffset, (uint)count);
        WriteUInt32BE(data, growByOffset, 1);

        var fileOffset = ModuleRegionFileOffset + moduleRegionOffset;
        for (var i = 0; i < count; i++)
        {
            WriteFloatBE(data, fileOffset + i * 4, baseValue + i * 0.01f);
        }
    }

    private static uint ModuleVa(int moduleRegionOffset)
    {
        return ModuleBaseVa + (uint)moduleRegionOffset;
    }

    private static MinidumpInfo TwoRegionMinidumpInfo()
    {
        return new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            NumberOfStreams = 2,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(HeapBaseVa),
                    Size = HeapRegionSize,
                    FileOffset = 0
                },
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(ModuleBaseVa),
                    Size = ModuleRegionSize,
                    FileOffset = ModuleRegionFileOffset
                }
            ]
        };
    }
}