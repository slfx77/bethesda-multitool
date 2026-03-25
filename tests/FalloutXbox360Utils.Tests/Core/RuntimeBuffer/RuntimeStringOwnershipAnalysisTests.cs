using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.RuntimeBuffer;
using FalloutXbox360Utils.Core.Strings;
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
        Assert.Equal(1, result.StringPool.AllFilePaths.Count);
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
    public void ExtractStringDataOnly_InboundPointerWithoutOwner_IsReferencedOwnerUnknown()
    {
        var data = new byte[256];
        WriteCString(data, 0x40, @"textures\armor\helmet.dds");
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
        IReadOnlyList<RuntimeEditorIdEntry>? runtimeEditorIds = null)
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
                runtimeEditorIds);

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
}
