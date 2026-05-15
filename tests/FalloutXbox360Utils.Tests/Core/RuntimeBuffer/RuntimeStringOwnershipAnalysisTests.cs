using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.RuntimeBuffer;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.RuntimeBuffer;

public sealed class RuntimeStringOwnershipAnalysisTests
{
    private const uint BaseVa = 0x40000000;

    [Fact]
    public void ExtractStringDataOnly_KeepsIdenticalTextAtDifferentOffsets()
    {
        var data = new byte[256];
        WriteCString(data, 0x20, @"meshes\props\crate01.nif");
        WriteCString(data, 0x60, @"meshes\props\crate01.nif");

        var result = Analyze(data, CreateCoverage(data.Length, StringGap(0, data.Length)));

        Assert.Collection(
            result.OwnershipAnalysis.AllHits,
            hit => Assert.Equal(@"meshes\props\crate01.nif", hit.Text),
            hit => Assert.Equal(@"meshes\props\crate01.nif", hit.Text));
        Assert.Equal(2, result.OwnershipAnalysis.AllHits.Count(h => h.Text == @"meshes\props\crate01.nif"));
        Assert.Single(result.StringPool.AllFilePaths);
        Assert.NotEqual(
            result.OwnershipAnalysis.AllHits[0].FileOffset,
            result.OwnershipAnalysis.AllHits[1].FileOffset);
    }

    [Fact]
    public void ExtractStringDataOnly_FiltersOtherCategoryOutOfOwnershipReports()
    {
        var data = new byte[256];
        WriteCString(data, 0x20, @"meshes\props\crate01.nif");
        WriteCString(data, 0x80, "abcd");

        var result = Analyze(data, CreateCoverage(data.Length, StringGap(0, data.Length)));

        Assert.Single(result.OwnershipAnalysis.AllHits);
        Assert.DoesNotContain(result.OwnershipAnalysis.AllHits, hit => hit.Text == "abcd");
        Assert.Equal(1, result.StringPool.Other);
    }

    [Fact]
    public void ExtractStringDataOnly_RuntimeEditorIdClaimMarksHitAsOwned()
    {
        var data = new byte[256];
        WriteCString(data, 0x30, "TestEditor_One");

        var result = Analyze(
            data,
            CreateCoverage(data.Length, StringGap(0, data.Length)),
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "TestEditor_One",
                    FormId = 0x00123456,
                    FormType = 42,
                    StringOffset = 0x30,
                    TesFormOffset = 0x90,
                    TesFormPointer = BaseVa + 0x90
                }
            ]);

        var hit = Assert.Single(result.OwnershipAnalysis.OwnedHits);
        Assert.Equal(RuntimeStringOwnershipStatus.Owned, hit.OwnershipStatus);
        Assert.Equal("RuntimeEditorId", hit.OwnerResolution?.OwnerKind);
        Assert.Equal("TestEditor_One", hit.OwnerResolution?.OwnerName);
        Assert.Equal(0x00123456u, hit.OwnerResolution?.OwnerFormId);
    }

    [Fact]
    public void ExtractStringDataOnly_RuntimeScriptVariableClaimMarksHitAsOwned()
    {
        var data = new byte[0x400];
        const string variableName = "bAcceptedMortimerQuest";
        WriteCString(data, 0x40, variableName);

        const int scriptOffset = 0x100;
        const int variableOffset = 0x200;
        WriteBeUInt32(data, scriptOffset + 12, 0x00111111);
        WriteBeUInt32(data, scriptOffset + 40, 1); // Script.variableCount, PDB + 16 shift
        WriteBeUInt32(data, scriptOffset + 92, BaseVa + variableOffset); // Script.listVariables

        WriteBeUInt32(data, variableOffset, 42);
        WriteBeUInt32(data, variableOffset + 12, 0);
        WriteBsStringT(data, variableOffset + 24, BaseVa + 0x40, variableName.Length);

        var result = Analyze(
            data,
            CreateCoverage(data.Length, StringGap(0, data.Length)),
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "TestScript",
                    FormId = 0x00111111,
                    FormType = 0x11,
                    TesFormOffset = scriptOffset,
                    TesFormPointer = BaseVa + scriptOffset
                }
            ]);

        var hit = Assert.Single(result.OwnershipAnalysis.OwnedHits, h => h.Text == variableName);
        Assert.Equal("SCPT", hit.OwnerResolution?.OwnerRecordType);
        Assert.Equal("ScriptVariable.cName", hit.OwnerResolution?.OwnerFieldOrSubrecord);
        Assert.Equal(ClaimSource.RuntimeStructField, hit.OwnerResolution?.ClaimSource);
    }

    [Fact]
    public void ExtractStringDataOnly_RuntimeQuestObjectiveClaimMarksHitAsOwned()
    {
        var data = new byte[0x500];
        const string objectiveText = "Ask around the saloon in Goodsprings for information about your attackers.";
        WriteCString(data, 0x40, objectiveText);

        const int questOffset = 0x120;
        const int objectiveOffset = 0x260;
        WriteBeUInt32(data, questOffset + 12, 0x00222222);
        WriteBeUInt32(data, questOffset + 92, BaseVa + objectiveOffset); // TESQuest.m_listObjectives

        WriteBeUInt32(data, objectiveOffset + 4, 20);
        WriteBsStringT(data, objectiveOffset + 8, BaseVa + 0x40, objectiveText.Length);
        WriteBeUInt32(data, objectiveOffset + 16, BaseVa + questOffset);
        data[objectiveOffset + 28] = 1;
        WriteBeUInt32(data, objectiveOffset + 32, 1);

        var result = Analyze(
            data,
            CreateCoverage(data.Length, StringGap(0, data.Length)),
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "TestQuest",
                    FormId = 0x00222222,
                    FormType = 0x47,
                    TesFormOffset = questOffset,
                    TesFormPointer = BaseVa + questOffset
                }
            ]);

        var hit = Assert.Single(result.OwnershipAnalysis.OwnedHits, h => h.Text == objectiveText);
        Assert.Equal("QUST", hit.OwnerResolution?.OwnerRecordType);
        Assert.Equal("BGSQuestObjective.displayText", hit.OwnerResolution?.OwnerFieldOrSubrecord);
        Assert.Equal(0x00222222u, hit.OwnerResolution?.OwnerFormId);
    }

    [Fact]
    public void ExtractStringDataOnly_RuntimeMessageButtonClaimMarksHitAsOwned()
    {
        var data = new byte[0x500];
        const string buttonText = "Added the infected Brahmin meat to the mix.";
        WriteCString(data, 0x40, buttonText);

        const int messageOffset = 0x140;
        const int buttonOffset = 0x280;
        WriteBeUInt32(data, messageOffset + 12, 0x00333333);
        WriteBeUInt32(data, messageOffset + 64, BaseVa + buttonOffset); // BGSMessage.ButtonList
        WriteBsStringT(data, buttonOffset + 8, BaseVa + 0x40, buttonText.Length);

        var result = Analyze(
            data,
            CreateCoverage(data.Length, StringGap(0, data.Length)),
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "TestMessage",
                    FormId = 0x00333333,
                    FormType = 0x62,
                    TesFormOffset = messageOffset,
                    TesFormPointer = BaseVa + messageOffset
                }
            ]);

        var hit = Assert.Single(result.OwnershipAnalysis.OwnedHits, h => h.Text == buttonText);
        Assert.Equal("MESG", hit.OwnerResolution?.OwnerRecordType);
        Assert.Equal("MESSAGEBOX_BUTTON.text", hit.OwnerResolution?.OwnerFieldOrSubrecord);
        Assert.Equal(ClaimSource.RuntimeStructField, hit.OwnerResolution?.ClaimSource);
    }

    [Fact]
    public void ExtractStringDataOnly_RawRecordStringSubrecordClaimMarksHitAsOwned()
    {
        var data = new byte[128];
        const string fullName = "California Sunset Drive-in";
        const int recordOffset = 0;
        const int recordDataOffset = recordOffset + 24;
        const int subrecordDataOffset = recordDataOffset + 6;

        Encoding.ASCII.GetBytes("FULL").CopyTo(data, recordDataOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(recordDataOffset + 4, 2), (ushort)(fullName.Length + 1));
        WriteCString(data, subrecordDataOffset, fullName);

        var result = Analyze(
            data,
            CreateCoverage(data.Length, StringGap(0, data.Length)),
            mainRecords:
            [
                new DetectedMainRecord(
                    "CELL",
                    (uint)(6 + fullName.Length + 1),
                    0,
                    0x00444444,
                    recordOffset,
                    false)
            ]);

        var hit = Assert.Single(result.OwnershipAnalysis.OwnedHits, h => h.Text == fullName);
        Assert.Equal("CELL", hit.OwnerResolution?.OwnerRecordType);
        Assert.Equal("FULL", hit.OwnerResolution?.OwnerFieldOrSubrecord);
        Assert.Equal(ClaimSource.RawRecordSubrecord, hit.OwnerResolution?.ClaimSource);
        Assert.Equal(0x00444444u, hit.OwnerResolution?.OwnerFormId);
    }

    [Fact]
    public void ExtractStringDataOnly_InboundPointerWithoutOwner_IsReferencedOwnerUnknown()
    {
        var data = new byte[256];
        WriteCString(data, 0x40, "SomeRuntimeAllocatedStringValue");
        WriteBeUInt32(data, 0x80, BaseVa + 0x40);

        var result = Analyze(data, CreateCoverage(data.Length, StringGap(0, data.Length)));

        var hit = Assert.Single(result.OwnershipAnalysis.ReferencedOwnerUnknownHits);
        Assert.Equal(RuntimeStringOwnershipStatus.ReferencedOwnerUnknown, hit.OwnershipStatus);
        Assert.Equal(1, hit.InboundPointerCount);
        Assert.Equal(BaseVa + 0x80, hit.OwnerResolution?.ReferrerVa);
    }

    [Fact]
    public void ExtractStringDataOnly_NonAlignedPointerPattern_DoesNotCountAsReference()
    {
        var data = new byte[256];
        WriteCString(data, 0x40, @"textures\armor\helmet.dds");
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x81, 4), BaseVa + 0x40);

        var result = Analyze(data, CreateCoverage(data.Length, StringGap(0, data.Length)));

        var hit = Assert.Single(result.OwnershipAnalysis.UnreferencedHits);
        Assert.Equal(RuntimeStringOwnershipStatus.Unreferenced, hit.OwnershipStatus);
        Assert.Equal(0, hit.InboundPointerCount);
    }

    private static RuntimeStringReportData Analyze(
        byte[] data,
        CoverageResult coverage,
        IReadOnlyList<RuntimeEditorIdEntry>? runtimeEditorIds = null,
        IReadOnlyList<DetectedMainRecord>? mainRecords = null)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(tempFile, data);

            using var mmf = MemoryMappedFile.CreateFromFile(tempFile, FileMode.Open, null, data.Length,
                MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, data.Length, MemoryMappedFileAccess.Read);

            var analyzer = new RuntimeBufferAnalyzer(
                accessor,
                data.Length,
                CreateMinidumpInfo(data.Length),
                coverage,
                null,
                runtimeEditorIds,
                mainRecords: mainRecords);

            return analyzer.ExtractStringDataOnly();
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static CoverageResult CreateCoverage(int fileSize, params CoverageGap[] gaps)
    {
        return new CoverageResult
        {
            FileSize = fileSize,
            TotalMemoryRegions = 1,
            TotalRegionBytes = fileSize,
            Gaps = gaps.ToList()
        };
    }

    private static CoverageGap StringGap(int fileOffset, int size)
    {
        return new CoverageGap
        {
            FileOffset = fileOffset,
            Size = size,
            VirtualAddress = BaseVa + fileOffset,
            Classification = GapClassification.StringPool,
            Context = "synthetic"
        };
    }

    private static MinidumpInfo CreateMinidumpInfo(int fileSize)
    {
        return new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = BaseVa,
                    FileOffset = 0,
                    Size = fileSize
                }
            ]
        };
    }

    private static void WriteCString(byte[] buffer, int offset, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text + "\0");
        bytes.CopyTo(buffer, offset);
    }

    private static void WriteBeUInt32(byte[] buffer, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), value);
    }

    private static void WriteBeUInt16(byte[] buffer, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), value);
    }

    private static void WriteBsStringT(byte[] buffer, int offset, uint stringVa, int length)
    {
        WriteBeUInt32(buffer, offset, stringVa);
        WriteBeUInt16(buffer, offset + 4, (ushort)length);
        WriteBeUInt16(buffer, offset + 6, (ushort)length);
    }
}
