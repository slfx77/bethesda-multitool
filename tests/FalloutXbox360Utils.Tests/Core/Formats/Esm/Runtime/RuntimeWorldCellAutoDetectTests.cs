using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeWorldCellAutoDetectTests
{
    private const int DataSize = 16 * 1024;
    private const uint HeapBaseVa = 0x40000000;

    [Fact]
    public void CreateWithAutoDetect_ShiftedWorldAndCellLayout_UsesDetectedCandidate()
    {
        var data = new byte[DataSize];
        const int worldShift = 8;
        const int cellShift = 12;

        var (worldEntry, cellEntry) = WriteShiftedWorldAndCell(data, worldShift, cellShift);

        using var context = new SyntheticWorldCellDump(data);
        var reader = RuntimeStructReader.CreateWithAutoDetect(
            context.Accessor,
            data.Length,
            context.MinidumpInfo,
            Array.Empty<RuntimeEditorIdEntry>(),
            null,
            [worldEntry],
            [cellEntry]);

        Assert.NotNull(reader.WorldCellLayoutProbe);
        Assert.True(reader.WorldCellLayoutProbe!.IsHighConfidence);
        Assert.Equal(worldShift, reader.WorldCellLayoutProbe.Layout.WorldShift);
        Assert.Equal(cellShift, reader.WorldCellLayoutProbe.Layout.CellShift);

        var worldspace = reader.ReadRuntimeWorldspace(worldEntry);
        Assert.NotNull(worldspace);
        Assert.Equal(512, worldspace!.MapUsableWidth);
        Assert.Equal(256, worldspace.MapUsableHeight);

        var worldCellMaps = reader.ReadAllWorldspaceCellMaps([worldEntry]);
        var worldData = Assert.Single(worldCellMaps).Value;
        var cellMapEntry = Assert.Single(worldData.Cells);
        Assert.Equal([0x00007021u, 0x00007022u], cellMapEntry.ReferenceFormIds);

        var cell = reader.ReadRuntimeCell(cellEntry);
        Assert.NotNull(cell);
        Assert.Equal(worldEntry.FormId, cell!.WorldspaceFormId);
        Assert.Equal(96f, cell.WaterHeight);
        Assert.Equal((byte)0x02, cell.Flags);
    }

    [Fact]
    public void LowConfidenceWorldCellProbe_DisablesStructuralReads()
    {
        var data = new byte[DataSize];
        const uint worldspaceFormId = 0x00008000;
        const int worldOffset = 0;
        const int worldNameOffset = 1024;

        WriteTesFormHeader(data, worldOffset, 0x82010000, 0x41, worldspaceFormId);
        WriteBSStringT(data, worldOffset + 44, FileOffsetToVa(worldNameOffset), "Fallback World", worldNameOffset);

        var worldEntry = MakeEntry("FallbackWorld", worldspaceFormId, 0x41, worldOffset);

        using var context = new SyntheticWorldCellDump(data);
        var reader = new RuntimeStructReader(
            new MmfMemoryAccessor(context.Accessor),
            data.Length,
            context.MinidumpInfo,
            false,
            null,
            new RuntimeWorldCellLayoutProbeResult(
                new RuntimeWorldCellLayout(8, 8),
                false,
                4,
                4,
                1));

        Assert.Empty(reader.ReadAllWorldspaceCellMaps([worldEntry]));

        var worldspace = reader.ReadRuntimeWorldspace(worldEntry);
        Assert.NotNull(worldspace);
        Assert.Equal("Fallback World", worldspace!.FullName);
    }

    private static (RuntimeEditorIdEntry WorldEntry, RuntimeEditorIdEntry CellEntry) WriteShiftedWorldAndCell(
        byte[] data,
        int worldShift,
        int cellShift)
    {
        const uint worldspaceFormId = 0x00007000;
        const uint cellFormId = 0x00007010;
        const int worldOffset = 0;
        const int cellMapOffset = 512;
        const int bucketArrayOffset = 544;
        const int bucketItemOffset = 576;
        const int cellOffset = 1024;
        const int cellRefNodeOffset = 1280;
        const int cellRefAOffset = 1536;
        const int cellRefBOffset = 1792;
        const int landOffset = 2048;
        const int encounterOffset = 2304;
        const int worldNameOffset = 4096;
        const int cellNameOffset = 4352;

        WriteTesFormHeader(data, worldOffset, 0x82010000, 0x41, worldspaceFormId);
        WriteBSStringT(data, worldOffset + 44, FileOffsetToVa(worldNameOffset), "Shifted World", worldNameOffset);
        WriteUInt32BE(data, worldOffset + 64 + worldShift, FileOffsetToVa(cellMapOffset));
        WriteUInt32BE(data, worldOffset + 68 + worldShift, FileOffsetToVa(cellOffset));
        WriteTesFormHeader(data, encounterOffset, 0x82010000, 0x61, 0x00007030);
        WriteUInt32BE(data, worldOffset + 216 + worldShift, FileOffsetToVa(encounterOffset));
        WriteInt32BE(data, worldOffset + 144 + worldShift, 512);
        WriteInt32BE(data, worldOffset + 148 + worldShift, 256);
        WriteUInt16BE(data, worldOffset + 152 + worldShift, unchecked((ushort)-4));
        WriteUInt16BE(data, worldOffset + 154 + worldShift, unchecked((ushort)-2));
        WriteUInt16BE(data, worldOffset + 156 + worldShift, unchecked(12));
        WriteUInt16BE(data, worldOffset + 158 + worldShift, unchecked(8));

        WriteUInt32BE(data, cellMapOffset + 4, 1);
        WriteUInt32BE(data, cellMapOffset + 8, FileOffsetToVa(bucketArrayOffset));
        WriteUInt32BE(data, cellMapOffset + 12, 1);
        WriteUInt32BE(data, bucketArrayOffset, FileOffsetToVa(bucketItemOffset));
        WriteUInt32BE(data, bucketItemOffset, 0);
        WriteUInt32BE(data, bucketItemOffset + 4, PackCellMapKey(6, -5));
        WriteUInt32BE(data, bucketItemOffset + 8, FileOffsetToVa(cellOffset));

        WriteTesFormHeader(data, cellOffset, 0x82010000, 0x39, cellFormId);
        WriteBSStringT(data, cellOffset + 44, FileOffsetToVa(cellNameOffset), "Shifted Cell", cellNameOffset);
        data[cellOffset + 52 + cellShift] = 0x02;
        WriteUInt32BE(data, cellOffset + 92 + cellShift, FileOffsetToVa(landOffset));
        WriteFloatBE(data, cellOffset + 96 + cellShift, 96f);
        WriteUInt32BE(data, cellOffset + 140 + cellShift, FileOffsetToVa(cellRefAOffset));
        WriteUInt32BE(data, cellOffset + 144 + cellShift, FileOffsetToVa(cellRefNodeOffset));
        WriteUInt32BE(data, cellOffset + 160 + cellShift, FileOffsetToVa(worldOffset));
        WriteUInt32BE(data, cellRefNodeOffset, FileOffsetToVa(cellRefBOffset));
        WriteTesFormHeader(data, cellRefAOffset, 0x82010000, 0x3A, 0x00007021);
        WriteTesFormHeader(data, cellRefBOffset, 0x82010000, 0x3B, 0x00007022);
        WriteTesFormHeader(data, landOffset, 0x82010000, 0x42, 0x00007023);

        return (
            MakeEntry("WorldShifted", worldspaceFormId, 0x41, worldOffset),
            MakeEntry("CellShifted", cellFormId, 0x39, cellOffset));
    }

    private static RuntimeEditorIdEntry MakeEntry(string editorId, uint formId, byte formType, long tesFormOffset)
    {
        return new RuntimeEditorIdEntry
        {
            EditorId = editorId,
            FormId = formId,
            FormType = formType,
            TesFormOffset = tesFormOffset,
            TesFormPointer = Xbox360MemoryUtils.VaToLong(FileOffsetToVa((int)tesFormOffset))
        };
    }

    private static void WriteTesFormHeader(byte[] data, int fileOffset, uint vtable, byte formType, uint formId)
    {
        WriteUInt32BE(data, fileOffset, vtable);
        data[fileOffset + 4] = formType;
        WriteUInt32BE(data, fileOffset + 12, formId);
    }

    private static void WriteBSStringT(byte[] data, int bstFileOffset, uint stringVa, string text,
        int stringDataFileOffset)
    {
        WriteUInt32BE(data, bstFileOffset, stringVa);
        WriteUInt16BE(data, bstFileOffset + 4, (ushort)text.Length);
        Encoding.ASCII.GetBytes(text, data.AsSpan(stringDataFileOffset, text.Length));
    }

    private static uint PackCellMapKey(int gridX, int gridY)
    {
        return unchecked((uint)((gridX << 16) | (ushort)gridY));
    }

    private static uint FileOffsetToVa(int fileOffset)
    {
        return HeapBaseVa + (uint)fileOffset;
    }

    private sealed class SyntheticWorldCellDump : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly string _tempFilePath;

        public SyntheticWorldCellDump(byte[] data)
        {
            _tempFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            File.WriteAllBytes(_tempFilePath, data);
            _mmf = MemoryMappedFile.CreateFromFile(_tempFilePath, FileMode.Open, null, data.Length,
                MemoryMappedFileAccess.Read);
            Accessor = _mmf.CreateViewAccessor(0, data.Length, MemoryMappedFileAccess.Read);
            MinidumpInfo = new MinidumpInfo
            {
                IsValid = true,
                ProcessorArchitecture = 0x03,
                NumberOfStreams = 1,
                MemoryRegions =
                [
                    new MinidumpMemoryRegion
                    {
                        VirtualAddress = Xbox360MemoryUtils.VaToLong(HeapBaseVa),
                        Size = data.Length,
                        FileOffset = 0
                    }
                ]
            };
        }

        public MemoryMappedViewAccessor Accessor { get; }
        public MinidumpInfo MinidumpInfo { get; }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Accessor.Dispose();
            _mmf.Dispose();

            if (File.Exists(_tempFilePath))
            {
                File.Delete(_tempFilePath);
            }
        }
    }
}