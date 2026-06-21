using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Regression coverage for partial-recovery ("partial data") BSAs whose file table is
///     intact but whose payload tail is zero-filled. Such an archive otherwise extracts
///     ghost entries as all zeros, which downstream converters reject (e.g. the DDX parser
///     surfaces "Unknown DDX magic: 0x00000000"). The indexer must drop those entries so
///     resolution falls through to a complete source, and the packer must never pack a
///     zero-filled payload even if one slips through.
/// </summary>
public sealed class BsaTruncationTests : IDisposable
{
    private readonly string _scratchRoot = Path.Combine(
        Path.GetTempPath(),
        $"bsa-truncation-{Guid.NewGuid():N}");

    private bool _disposed;

    public BsaTruncationTests()
    {
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Directory.Exists(_scratchRoot))
            {
                Directory.Delete(_scratchRoot, true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void FindDataTruncationBoundary_IntactArchive_ReturnsFileLength()
    {
        var bsaPath = Path.Combine(_scratchRoot, "intact", "Fallout - Textures.bsa");
        BuildBsa(bsaPath,
            ("textures\\a\\one.dds", Fill(0xAB, 4096)),
            ("textures\\a\\two.dds", Fill(0xCD, 8192)));

        using var extractor = new BsaExtractor(bsaPath);
        var boundary = extractor.FindDataTruncationBoundary();

        Assert.Equal(new FileInfo(bsaPath).Length, boundary);
        // Every record sits below the boundary on an intact archive.
        Assert.All(extractor.Archive.AllFiles, r => Assert.True(r.Offset < boundary));
    }

    [Fact]
    public void FindDataTruncationBoundary_ZeroedTail_BoundaryExcludesZeroedRecord()
    {
        var bsaPath = Path.Combine(_scratchRoot, "tail", "Fallout - Textures.bsa");
        BuildBsa(bsaPath,
            ("textures\\a\\intact.dds", Fill(0xAB, 4096)),
            ("textures\\a\\zeroed.dds", Fill(0xCD, 8192)));

        var (intact, zeroed) = SplitByOffset(bsaPath);
        ZeroRegionToEof(bsaPath, zeroed.Offset);

        using var extractor = new BsaExtractor(bsaPath);
        var boundary = extractor.FindDataTruncationBoundary();

        // The zeroed (trailing) record is at/after the boundary; the intact one is below it.
        Assert.True(zeroed.Offset >= boundary, $"zeroed.Offset=0x{zeroed.Offset:X} boundary=0x{boundary:X}");
        Assert.True(intact.Offset < boundary, $"intact.Offset=0x{intact.Offset:X} boundary=0x{boundary:X}");
    }

    [Fact]
    public void DataFolderIndex_ZeroedTail_SkipsZeroedEntryButKeepsIntact()
    {
        var folder = Path.Combine(_scratchRoot, "partial");
        var bsaPath = Path.Combine(folder, "Fallout - Textures.bsa");
        BuildBsa(bsaPath,
            ("textures\\a\\intact.dds", Fill(0xAB, 4096)),
            ("textures\\a\\zeroed.dds", Fill(0xCD, 8192)));

        var (intact, zeroed) = SplitByOffset(bsaPath);
        ZeroRegionToEof(bsaPath, zeroed.Offset);

        using var index = new DataFolderIndex(folder, true);
        index.Build();

        Assert.Equal(1, index.TruncatedEntrySkipCount);
        Assert.True(index.TryResolveExact(intact.FullPath, out _), "intact entry should remain indexed");
        Assert.False(index.TryResolveExact(zeroed.FullPath, out _), "zeroed entry should be skipped");
    }

    [Fact]
    public void Resolve_ZeroedInFirstSecondary_FallsThroughToIntactSecondary()
    {
        const string path = "textures\\a\\shared.dds";

        // Secondary #0: the file lives in a zero-filled tail → skipped at index time.
        var partialFolder = Path.Combine(_scratchRoot, "partial");
        var partialBsa = Path.Combine(partialFolder, "Fallout - Textures.bsa");
        BuildBsa(partialBsa, (path, Fill(0xCD, 8192)));
        var only = SingleRecord(partialBsa);
        ZeroRegionToEof(partialBsa, only.Offset);

        // Secondary #1: the same file, intact.
        var completeFolder = Path.Combine(_scratchRoot, "complete");
        var completeBsa = Path.Combine(completeFolder, "Fallout - Textures.bsa");
        BuildBsa(completeBsa, (path, Fill(0xEE, 8192)));

        var baselineFolder = Path.Combine(_scratchRoot, "baseline");
        Directory.CreateDirectory(baselineFolder);

        using var baseline = new DataFolderIndex(baselineFolder, false);
        baseline.Build();
        using var partial = new DataFolderIndex(partialFolder, true);
        partial.Build();
        using var complete = new DataFolderIndex(completeFolder, true);
        complete.Build();

        Assert.Equal(1, partial.TruncatedEntrySkipCount);

        var resolver = new DataFolderResolver(baseline, [partial, complete]);
        var result = resolver.Resolve(path);

        Assert.Equal(AssetResolutionKind.ResolvedExact, result.Kind);
        Assert.NotNull(result.Source);
        Assert.Equal(1, result.SourceFolderIndex); // came from the complete secondary, not the partial one
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Fill(byte value, int length)
    {
        var data = new byte[length];
        Array.Fill(data, value);
        return data;
    }

    private static void BuildBsa(string bsaPath, params (string Path, byte[] Data)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(bsaPath)!);
        // Uncompressed, no embedded names → raw, contiguous data section, so a zeroed
        // trailing region maps cleanly onto one file record.
        using var writer = new BsaWriter(false, embedFileNames: false);
        foreach (var (path, data) in files)
        {
            writer.AddFile(path, data);
        }

        writer.Write(bsaPath);
    }

    private static (BsaFileRecord Intact, BsaFileRecord Zeroed) SplitByOffset(string bsaPath)
    {
        var records = BsaParser.Parse(bsaPath).AllFiles.OrderBy(r => r.Offset).ToList();
        Assert.Equal(2, records.Count);
        return (records[0], records[1]);
    }

    private static BsaFileRecord SingleRecord(string bsaPath)
    {
        return BsaParser.Parse(bsaPath).AllFiles.Single();
    }

    private static void ZeroRegionToEof(string bsaPath, uint fromOffset)
    {
        using var fs = new FileStream(bsaPath, FileMode.Open, FileAccess.ReadWrite);
        fs.Seek(fromOffset, SeekOrigin.Begin);
        var zeros = new byte[fs.Length - fromOffset];
        fs.Write(zeros, 0, zeros.Length);
    }
}