using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.EsmTestRecordBuilder;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

public sealed class DialogueTesFileRecoveryTests
{
    private const uint HeapBaseVa = 0x40000000;

    [Fact]
    public void MergeRuntimeDialogueData_IgnoresRuntimeOnlyOffsetsAndBackfillsScripts()
    {
        const long fileSize = 0x5000;
        const long rawOffsetA = 0x1000;
        const long rawOffsetB = 0x1800;
        const long rawOffsetC = 0x1900;
        const long runtimeOffsetB = 0x3000;
        const long runtimeOffsetA = 0x3100;
        const long runtimeOffsetC = 0x3200;
        const uint tesFileOffsetA = 0x200;
        const uint tesFileOffsetB = 0xA00;
        const uint tesFileOffsetC = 0xB00;
        const uint formIdA = 0x0000A001;
        const uint formIdB = 0x0000B002;
        const uint formIdC = 0x0000C003;

        var accessor = new SparseMemoryAccessor();
        accessor.AddRange(rawOffsetA, BuildMappedInfoRecordBytes(formIdA,
            ("EDID", NullTermString("InfoA"))));
        accessor.AddRange(rawOffsetB, BuildMappedInfoRecordBytes(formIdB,
            ("EDID", NullTermString("InfoB")),
            ("SCHR", new byte[20]),
            ("SCTX", NullTermString("ShowBarterMenu"))));
        accessor.AddRange(rawOffsetC, BuildMappedInfoRecordBytes(formIdC,
            ("EDID", NullTermString("InfoC"))));
        accessor.AddRange(runtimeOffsetA, BuildRuntimeInfoStruct(formIdA, tesFileOffsetA));
        accessor.AddRange(runtimeOffsetB, BuildRuntimeInfoStruct(formIdB, tesFileOffsetB));
        accessor.AddRange(runtimeOffsetC, BuildRuntimeInfoStruct(formIdC, tesFileOffsetC));

        var scanResult = MakeScanResult(
            runtimeEditorIds:
            [
                new RuntimeEditorIdEntry
                {
                    FormId = formIdB,
                    EditorId = "InfoB",
                    FormType = 0x45,
                    TesFormOffset = runtimeOffsetB,
                    DialogueLine = "Prompt B"
                },
                new RuntimeEditorIdEntry
                {
                    FormId = formIdA,
                    EditorId = "InfoA",
                    FormType = 0x45,
                    TesFormOffset = runtimeOffsetA,
                    DialogueLine = "Prompt A"
                },
                new RuntimeEditorIdEntry
                {
                    FormId = formIdC,
                    EditorId = "InfoC",
                    FormType = 0x45,
                    TesFormOffset = runtimeOffsetC,
                    DialogueLine = "Prompt C"
                }
            ]);

        var context = new RecordParserContext(
            scanResult,
            null,
            accessor,
            fileSize,
            CreateMinidumpInfo(fileSize));
        var merger = new DialogueRuntimeMerger(context);
        var dialogues = new List<DialogueRecord>
        {
            new()
            {
                FormId = formIdB,
                EditorId = "InfoB",
                Offset = runtimeOffsetB,
                RuntimeStructOffset = runtimeOffsetB,
                IsBigEndian = true
            },
            new()
            {
                FormId = formIdA,
                EditorId = "InfoA",
                Offset = rawOffsetA,
                RawRecordOffset = rawOffsetA,
                IsBigEndian = true
            },
            new()
            {
                FormId = formIdC,
                EditorId = "InfoC",
                Offset = rawOffsetC,
                RawRecordOffset = rawOffsetC,
                IsBigEndian = true
            }
        };

        merger.MergeRuntimeDialogueData(dialogues);

        var recovered = Assert.Single(dialogues, dialogue => dialogue.FormId == formIdB);
        Assert.Equal(tesFileOffsetB, recovered.TesFileOffset);
        Assert.Equal(runtimeOffsetB, recovered.RuntimeStructOffset);
        Assert.True(recovered.HasResultScript);
        Assert.Single(recovered.ResultScripts);
        Assert.Equal("ShowBarterMenu", recovered.ResultScripts[0].SourceText);

        var calibrator = Assert.Single(dialogues, dialogue => dialogue.FormId == formIdA);
        Assert.Equal(rawOffsetA, calibrator.RawRecordOffset);
        Assert.Equal(tesFileOffsetA, calibrator.TesFileOffset);
    }

    [Fact]
    public void TryRecover_WithoutTesFileOffset_ReturnsNoTesFileOffset()
    {
        var result = DialogueTesFileScriptRecovery.TryRecover(
            CreateContext(new SparseMemoryAccessor(), 0x2000),
            [],
            0,
            0x0000BEEF,
            "InfoTest");

        Assert.Equal(DialogueTesFileScriptRecoveryStatus.NoTesFileOffset, result.Status);
    }

    [Fact]
    public void TryRecover_WithoutCalibration_ReturnsUncalibratedBase()
    {
        var result = DialogueTesFileScriptRecovery.TryRecover(
            CreateContext(new SparseMemoryAccessor(), 0x2000),
            [],
            0x100,
            0x0000BEEF,
            "InfoTest");

        Assert.Equal(DialogueTesFileScriptRecoveryStatus.UncalibratedBase, result.Status);
    }

    [Fact]
    public void TryRecover_WhenMappedPageMissing_ReturnsMappedPageMissing()
    {
        var context = CreateContext(new SparseMemoryAccessor(), 0x80);
        var result = DialogueTesFileScriptRecovery.TryRecover(
            context,
            [
                new DialogueTesFileMappingSegment
                {
                    BaseVirtualAddress = HeapBaseVa,
                    MinTesFileOffset = 0x100,
                    MaxTesFileOffset = 0x100,
                    MatchCount = 1
                }
            ],
            0x100,
            0x0000BEEF,
            "InfoTest");

        Assert.Equal(DialogueTesFileScriptRecoveryStatus.MappedPageMissing, result.Status);
    }

    [Fact]
    public void TryRecover_WhenSignatureMismatches_ReturnsSignatureMismatch()
    {
        var accessor = new SparseMemoryAccessor();
        accessor.AddRange(0x100, BuildHeader("DIAL", 0x0000BEEF));
        var result = DialogueTesFileScriptRecovery.TryRecover(
            CreateContext(accessor, 0x2000),
            [CreateSingleSegment(0x100)],
            0x100,
            0x0000BEEF,
            "InfoTest");

        Assert.Equal(DialogueTesFileScriptRecoveryStatus.SignatureMismatch, result.Status);
    }

    [Fact]
    public void TryRecover_WhenFormIdMismatches_ReturnsFormIdMismatch()
    {
        var accessor = new SparseMemoryAccessor();
        accessor.AddRange(0x100, BuildHeader("INFO", 0x0000CAFE));
        var result = DialogueTesFileScriptRecovery.TryRecover(
            CreateContext(accessor, 0x2000),
            [CreateSingleSegment(0x100)],
            0x100,
            0x0000BEEF,
            "InfoTest");

        Assert.Equal(DialogueTesFileScriptRecoveryStatus.FormIdMismatch, result.Status);
    }

    [Fact]
    public void TryRecover_WhenRecordIsCompressed_ReturnsCompressedRecord()
    {
        var accessor = new SparseMemoryAccessor();
        accessor.AddRange(0x100, BuildHeader("INFO", 0x0000BEEF, 0x00040000));
        var result = DialogueTesFileScriptRecovery.TryRecover(
            CreateContext(accessor, 0x2000),
            [CreateSingleSegment(0x100)],
            0x100,
            0x0000BEEF,
            "InfoTest");

        Assert.Equal(DialogueTesFileScriptRecoveryStatus.CompressedRecord, result.Status);
    }

    [Fact]
    public void TryRecover_WhenRecordHasNoScripts_ReturnsNoScriptSubrecords()
    {
        var accessor = new SparseMemoryAccessor();
        accessor.AddRange(0x100, BuildMappedInfoRecordBytes(0x0000BEEF,
            ("EDID", NullTermString("InfoTest"))));
        var result = DialogueTesFileScriptRecovery.TryRecover(
            CreateContext(accessor, 0x2000),
            [CreateSingleSegment(0x100)],
            0x100,
            0x0000BEEF,
            "InfoTest");

        Assert.Equal(DialogueTesFileScriptRecoveryStatus.NoScriptSubrecords, result.Status);
    }

    [Fact]
    public void TryRecover_WhenScriptsPresent_ReturnsRecovered()
    {
        var accessor = new SparseMemoryAccessor();
        accessor.AddRange(0x100, BuildMappedInfoRecordBytes(0x0000BEEF,
            ("EDID", NullTermString("InfoTest")),
            ("SCHR", new byte[20]),
            ("SCTX", NullTermString("ShowBarterMenu"))));
        var result = DialogueTesFileScriptRecovery.TryRecover(
            CreateContext(accessor, 0x2000),
            [CreateSingleSegment(0x100)],
            0x100,
            0x0000BEEF,
            "InfoTest");

        Assert.Equal(DialogueTesFileScriptRecoveryStatus.Recovered, result.Status);
        Assert.Single(result.Scripts);
        Assert.Equal("ShowBarterMenu", result.Scripts[0].SourceText);
    }

    private static DialogueTesFileMappingSegment CreateSingleSegment(uint tesFileOffset)
    {
        return new DialogueTesFileMappingSegment
        {
            BaseVirtualAddress = HeapBaseVa,
            MinTesFileOffset = tesFileOffset,
            MaxTesFileOffset = tesFileOffset,
            MatchCount = 1
        };
    }

    private static RecordParserContext CreateContext(IMemoryAccessor accessor, long fileSize)
    {
        return new RecordParserContext(
            MakeScanResult(),
            null,
            accessor,
            fileSize,
            CreateMinidumpInfo(fileSize));
    }

    private static MinidumpInfo CreateMinidumpInfo(long fileSize)
    {
        return new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            NumberOfStreams = 1,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = HeapBaseVa,
                    Size = fileSize,
                    FileOffset = 0
                }
            ]
        };
    }

    private static byte[] BuildRuntimeInfoStruct(uint formId, uint tesFileOffset)
    {
        var buffer = new byte[RuntimeDialogueLayouts.InfoLayout.StructSize];
        buffer[4] = 0x45;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12), formId);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(RuntimeDialogueLayouts.InfoFileOffsetOffset),
            tesFileOffset);
        return buffer;
    }

    private static byte[] BuildHeader(string signature, uint formId, uint flags = 0, uint dataSize = 0)
    {
        var buffer = new byte[24];
        Encoding.ASCII.GetBytes(signature, buffer.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), dataSize);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), flags);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12), formId);
        return buffer;
    }

    private static byte[] BuildMappedInfoRecordBytes(uint formId, params (string sig, byte[] data)[] subrecords)
    {
        var dataSize = subrecords.Sum(subrecord => 6 + subrecord.data.Length);
        var buffer = new byte[24 + dataSize];
        Encoding.ASCII.GetBytes("INFO", buffer.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), (uint)dataSize);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12), formId);

        var offset = 24;
        foreach (var (sig, data) in subrecords)
        {
            var sigBytes = Encoding.ASCII.GetBytes(sig);
            buffer[offset] = sigBytes[3];
            buffer[offset + 1] = sigBytes[2];
            buffer[offset + 2] = sigBytes[1];
            buffer[offset + 3] = sigBytes[0];
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 4), (ushort)data.Length);
            Array.Copy(data, 0, buffer, offset + 6, data.Length);
            offset += 6 + data.Length;
        }

        return buffer;
    }
}