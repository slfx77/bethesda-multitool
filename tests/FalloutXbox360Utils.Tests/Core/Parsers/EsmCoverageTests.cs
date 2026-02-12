using FalloutXbox360Utils.Core.Formats.Esm;
using Xunit;
using Xunit.Abstractions;

namespace FalloutXbox360Utils.Tests.Core.Parsers;

/// <summary>
///     Tests for validating ESM file coverage.
///     These tests use local sample files and are skipped when files are not available.
/// </summary>
public class EsmCoverageTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    // Path to sample ESM files - relative to repository root
    // Tests run from bin/Debug/net10.0, so navigate up to find Sample folder

    private static string SampleEsmPath => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Sample", "ESM", "360_proto",
            "FalloutNV.esm"));

    [Fact]
    public void EsmCoverage_Xbox360Esm_ShouldCoverEntireFile()
    {
        // Skip if file doesn't exist
        if (!File.Exists(SampleEsmPath))
        {
            _output.WriteLine($"Sample file not found: {SampleEsmPath}");
            return;
        }

        // Load the file
        var fileData = File.ReadAllBytes(SampleEsmPath);
        var fileSize = fileData.Length;
        _output.WriteLine($"File size: {fileSize:N0} bytes");

        // Parse records and GRUP headers
        var (records, grupHeaders) = EsmParser.EnumerateRecordsWithGrups(fileData);
        _output.WriteLine($"Parsed {records.Count:N0} records");
        _output.WriteLine($"Parsed {grupHeaders.Count:N0} GRUP headers");

        var isBigEndian = EsmParser.IsBigEndian(fileData);
        _output.WriteLine($"Big-endian: {isBigEndian}");

        // Build coverage map
        var coveredRanges = new List<(long start, long end)>();

        // Add TES4 header
        var tes4Header = EsmParser.ParseRecordHeader(fileData, isBigEndian);
        if (tes4Header != null)
        {
            coveredRanges.Add((0, EsmParser.MainRecordHeaderSize + tes4Header.DataSize));
        }

        // Add all GRUP headers (24 bytes each)
        foreach (var grup in grupHeaders)
        {
            coveredRanges.Add((grup.Offset, grup.Offset + 24));
        }

        // Add all parsed records
        foreach (var record in records)
        {
            var recordEnd = record.Offset + EsmParser.MainRecordHeaderSize + record.Header.DataSize;
            coveredRanges.Add((record.Offset, recordEnd));
        }

        // Sort and calculate coverage
        coveredRanges = coveredRanges.OrderBy(r => r.start).ToList();

        long totalCovered = 0;
        long prevEnd = 0;
        var gapCount = 0;

        foreach (var (start, end) in coveredRanges)
        {
            if (start > prevEnd)
            {
                gapCount++;
            }

            totalCovered += end - start;
            prevEnd = Math.Max(prevEnd, end);
        }

        if (prevEnd < fileSize)
        {
            gapCount++;
        }

        var coveragePercent = totalCovered * 100.0 / fileSize;

        _output.WriteLine($"Coverage: {coveragePercent:F2}% ({totalCovered:N0} / {fileSize:N0} bytes)");
        _output.WriteLine($"Gaps: {gapCount}");

        // ESM files should have near-100% coverage
        Assert.True(coveragePercent > 99.9, $"ESM coverage should be > 99.9%, but was {coveragePercent:F2}%");
        Assert.Equal(0, gapCount);
    }
}