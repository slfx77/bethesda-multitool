using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Recovery;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Recovery;

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

        var result = Scan(data);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(DmpGapRecoveryCandidateKind.RawEsmRecord, candidate.Kind);
        Assert.Equal("FLST", candidate.RecordType);
        Assert.Equal(0x01001234u, candidate.FormId);
        Assert.Equal(DmpGapRecoveryDisposition.PromoteRawRecord, candidate.Disposition);
        Assert.False(candidate.IsBigEndian);
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

        var result = Scan(data);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(DmpGapRecoveryCandidateKind.RawEsmRecord, candidate.Kind);
        Assert.Equal("RGDL", candidate.RecordType);
        Assert.Equal(0x01004567u, candidate.FormId);
        Assert.True(candidate.IsBigEndian);
        Assert.Equal(DmpGapRecoveryDisposition.PromoteRawRecord, candidate.Disposition);
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

        var result = Scan(data, gaps: []);

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
            IsBigEndian = true
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
            rttiReader: null,
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
