using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Recovery;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.BinaryTestWriter;

namespace BethesdaMultitool.Tests.Core.Recovery;

public sealed class DmpGapRecoveryScannerTests
{
    [Fact]
    public void Scan_DetectsValidNormalRawRecordHeader()
    {
        var data = new byte[64];
        WriteAscii(data, 0, "FLST");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 0x01001234);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20, 2), 97);
        WriteAscii(data, 24, "LNAM"); // data begins with a real FLST subrecord (first-subrecord gate)

        var result = Scan(data);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(DmpGapRecoveryCandidateKind.RawEsmRecord, candidate.Kind);
        Assert.Equal("FLST", candidate.RecordType);
        Assert.Equal(0x01001234u, candidate.FormId);
        Assert.Equal(DmpGapRecoveryDisposition.PromoteRawRecord, candidate.Disposition);
        Assert.False(candidate.IsBigEndian);
        Assert.Equal((ushort)97, candidate.FormVersion);
        Assert.Equal(28, candidate.Length);
    }

    [Fact]
    public void Scan_DetectsValidXboxReversedRawRecordHeader()
    {
        var data = new byte[64];
        WriteAscii(data, 0, "LDGR"); // RGDL with Xbox-reversed signature bytes.
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), 4);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12, 4), 0x01004567);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(20, 2), 111);
        WriteAscii(data, 24, "ATAD"); // Xbox-reversed "DATA" — a real RGDL subrecord

        var result = Scan(data);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(DmpGapRecoveryCandidateKind.RawEsmRecord, candidate.Kind);
        Assert.Equal("RGDL", candidate.RecordType);
        Assert.Equal(0x01004567u, candidate.FormId);
        Assert.True(candidate.IsBigEndian);
        Assert.Equal((ushort)111, candidate.FormVersion);
        Assert.Equal(DmpGapRecoveryDisposition.PromoteRawRecord, candidate.Disposition);
    }

    [Fact]
    public void Scan_RejectsRawRecordWhoseDataIsNotAKnownSubrecord()
    {
        // Valid scalar header (signature/size/flags/FormID) but the "data" is heap garbage that does not
        // begin with a known subrecord signature — exactly the phantom-record shape found in DMP gaps.
        var data = new byte[64];
        WriteAscii(data, 0, "FLST");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 0x01001234);
        WriteAscii(data, 24, "zzzz"); // not a registered subrecord signature

        var result = Scan(data);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Scan_RejectsIncidentalTextSignatureWithoutValidHeader()
    {
        var data = Encoding.ASCII.GetBytes(
            "This is just an incidental DIAL string inside text, not a record header.");

        var result = Scan(data);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Scan_DoesNotReportRawRecordOutsideCoverageGaps()
    {
        var data = new byte[64];
        WriteAscii(data, 0, "FLST");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 0x01001234);

        var result = Scan(data, []);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Scan_RuntimeTesFormCandidateRequiresRtti()
    {
        var data = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 0x82001000);
        data[4] = 0x3F;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12, 4), 0x01009999);

        var result = Scan(
            data,
            modules:
            [
                new MinidumpModule
                {
                    Name = "falloutnv.exe",
                    BaseAddress = 0x82000000,
                    Size = 0x20000
                }
            ]);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Promoter_AddsRawRecordOnlyWhenRecoveryEnabled()
    {
        var candidate = new DmpGapRecoveryCandidate
        {
            Kind = DmpGapRecoveryCandidateKind.RawEsmRecord,
            RecordType = "FLST",
            FormId = 0x01001234,
            FileOffset = 0x40,
            Length = 28,
            Disposition = DmpGapRecoveryDisposition.PromoteRawRecord,
            RawDataSize = 4,
            RawFlags = 0,
            IsBigEndian = true,
            FormVersion = 123
        };
        var scanResult = new EsmRecordScanResult();

        var disabled = DmpGapRecoveryPromoter.Apply(
            scanResult,
            [candidate],
            DmpGapRecoveryOptions.DiscoverOnly);
        var enabled = DmpGapRecoveryPromoter.Apply(
            scanResult,
            [candidate],
            DmpGapRecoveryOptions.PromoteAllValidated);

        Assert.Equal(0, disabled.RawRecordsPromoted);
        Assert.Equal(1, enabled.RawRecordsPromoted);
        var promoted = Assert.Single(scanResult.MainRecords);
        Assert.Equal("FLST", promoted.RecordType);
        Assert.Equal(0x01001234u, promoted.FormId);
        Assert.Equal((ushort)123, promoted.FormVersion);
    }

    [Fact]
    public void Scan_FindsRuntimeTesFormWhenGapFileOffsetParityDiffersFromVa()
    {
        // Debug-era corpus dumps have Memory64 BaseRva ≡ 2 (mod 4), so every captured byte sits at
        // file offset ≡ VA+2 (mod 4). The vtable-probe stride must anchor on the VA, not the file
        // offset — pre-fix this fixture yields zero candidates.
        var result = ScanRuntimeFixture(heapFileOffset: 0x302);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(DmpGapRecoveryCandidateKind.RuntimeTesForm, candidate.Kind);
        Assert.Equal("PBEA", candidate.RecordType);
        Assert.Equal(0x40000000, candidate.VirtualAddress);
        Assert.Equal(0x302, candidate.FileOffset);
    }

    [Fact]
    public void Scan_FindsRuntimeTesFormAtMatchedParity()
    {
        // Control: file offset ≡ VA (mod 4) — the Release-dump shape the old stride already handled.
        var result = ScanRuntimeFixture(heapFileOffset: 0x300);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("PBEA", candidate.RecordType);
    }

    private static DmpGapRecoveryResult ScanRuntimeFixture(int heapFileOffset)
    {
        const uint moduleVa = 0x82000000;
        const int rttiFileOffset = 0x100;
        const long heapVa = 0x40000000;
        const int heapSize = 0x400;

        var file = new byte[heapFileOffset + heapSize];
        var vtableVa = BuildTesFormRttiChain(file, rttiFileOffset, moduleVa);

        // Candidate struct at the heap region start (VA 4-aligned): vtable ptr, FormType, FormID.
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(heapFileOffset, 4), vtableVa);
        file[heapFileOffset + 4] = 0x3F; // PBEA, structSize 400
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(heapFileOffset + 12, 4), 0x01009999);

        var minidump = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            Modules =
            [
                new MinidumpModule { Name = "falloutnv.exe", BaseAddress = moduleVa, Size = 0x1000 }
            ],
            MemoryRegions =
            [
                // Module-space VAs (bit 31 set) are stored sign-extended in minidumps.
                new MinidumpMemoryRegion
                {
                    VirtualAddress = BethesdaMultitool.Core.Utils.Xbox360MemoryUtils.VaToLong(moduleVa),
                    FileOffset = rttiFileOffset,
                    Size = 0x200
                },
                new MinidumpMemoryRegion { VirtualAddress = heapVa, FileOffset = heapFileOffset, Size = heapSize }
            ]
        };

        var analysis = new AnalysisResult { FileSize = file.Length, MinidumpInfo = minidump };
        var coverage = new CoverageResult
        {
            FileSize = file.Length,
            TotalMemoryRegions = 2,
            TotalRegionBytes = 0x200 + heapSize,
            Gaps =
            [
                new CoverageGap
                {
                    FileOffset = heapFileOffset,
                    Size = heapSize,
                    VirtualAddress = heapVa,
                    Classification = GapClassification.BinaryData,
                    Context = "Synthetic heap gap"
                }
            ]
        };

        using var stream = new MemoryStream(file);
        var rttiReader = new RttiReader(minidump, stream);
        return DmpGapRecoveryScanner.Scan(
            analysis,
            coverage,
            new ByteArrayMemoryAccessor(file),
            rttiReader,
            DmpGapRecoveryOptions.DiscoverOnly with
            {
                MinGapSize = 1,
                MaxScanBytesPerGap = heapSize
            });
    }

    /// <summary>
    ///     Writes a minimal MSVC RTTI chain (vtable[-1] → COL → TypeDescriptor/hierarchy) whose class
    ///     derives from TESForm, mirroring RttiReaderTests.BuildSyntheticDump. Returns the vtable VA.
    /// </summary>
    private static uint BuildTesFormRttiChain(byte[] file, int fileOffset, uint baseVa)
    {
        var colVa = baseVa + 0x10;
        var tdVa = baseVa + 0x30;
        var chdVa = baseVa + 0x60;
        var bcaVa = baseVa + 0x80;
        var baseTdVa = baseVa + 0xB0;

        WriteUInt32BE(file, fileOffset, colVa); // vtable[-1] → COL

        // COL
        WriteUInt32BE(file, fileOffset + 0x10, 0); // signature
        WriteUInt32BE(file, fileOffset + 0x14, 0); // objectOffset
        WriteUInt32BE(file, fileOffset + 0x18, 0); // cdOffset
        WriteUInt32BE(file, fileOffset + 0x1C, tdVa);
        WriteUInt32BE(file, fileOffset + 0x20, chdVa);

        // TypeDescriptor (main class)
        WriteUInt32BE(file, fileOffset + 0x30, 0x82FFFFFF);
        WriteUInt32BE(file, fileOffset + 0x34, 0);
        WriteAsciiString(file, fileOffset + 0x38, ".?AVBeamProjectile@@");

        // ClassHierarchyDescriptor: self + TESForm base
        WriteUInt32BE(file, fileOffset + 0x60, 0);
        WriteUInt32BE(file, fileOffset + 0x64, 0);
        WriteUInt32BE(file, fileOffset + 0x68, 2);
        WriteUInt32BE(file, fileOffset + 0x6C, bcaVa);

        // BaseClassArray
        WriteUInt32BE(file, fileOffset + 0x80, baseVa + 0x90);
        WriteUInt32BE(file, fileOffset + 0x84, baseVa + 0xA0);

        // BCD[0] — main class
        WriteUInt32BE(file, fileOffset + 0x90, tdVa);
        WriteUInt32BE(file, fileOffset + 0x94, 1);
        WriteInt32BE(file, fileOffset + 0x98, 0);

        // BCD[1] — TESForm base
        WriteUInt32BE(file, fileOffset + 0xA0, baseTdVa);
        WriteUInt32BE(file, fileOffset + 0xA4, 0);
        WriteInt32BE(file, fileOffset + 0xA8, 0);

        // TypeDescriptor (base class)
        WriteUInt32BE(file, fileOffset + 0xB0, 0x82FFFFFF);
        WriteUInt32BE(file, fileOffset + 0xB4, 0);
        WriteAsciiString(file, fileOffset + 0xB8, ".?AVTESForm@@");

        return baseVa + 0x04; // vtable[0]
    }

    private static DmpGapRecoveryResult Scan(
        byte[] data,
        IReadOnlyList<CoverageGap>? gaps = null,
        IReadOnlyList<MinidumpModule>? modules = null)
    {
        var analysis = new AnalysisResult
        {
            FileSize = data.Length,
            MinidumpInfo = new MinidumpInfo
            {
                IsValid = true,
                ProcessorArchitecture = 0x03,
                Modules = modules?.ToList() ?? [],
                MemoryRegions =
                [
                    new MinidumpMemoryRegion
                    {
                        VirtualAddress = 0x40000000,
                        FileOffset = 0,
                        Size = data.Length
                    }
                ]
            }
        };
        var coverage = new CoverageResult
        {
            FileSize = data.Length,
            TotalMemoryRegions = 1,
            TotalRegionBytes = data.Length,
            Gaps = gaps?.ToList() ??
            [
                new CoverageGap
                {
                    FileOffset = 0,
                    Size = data.Length,
                    VirtualAddress = 0x40000000,
                    Classification = GapClassification.BinaryData,
                    Context = "Synthetic gap"
                }
            ]
        };

        return DmpGapRecoveryScanner.Scan(
            analysis,
            coverage,
            new ByteArrayMemoryAccessor(data),
            null,
            DmpGapRecoveryOptions.DiscoverOnly with
            {
                MinGapSize = 1,
                MaxScanBytesPerGap = data.Length
            });
    }

    private static void WriteAscii(byte[] data, int offset, string value)
    {
        Encoding.ASCII.GetBytes(value, data.AsSpan(offset, value.Length));
    }
}