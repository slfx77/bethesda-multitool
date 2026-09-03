using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks of TEXT.RSC and the BOOKS directory against the retail ARENA2
///     (<c>RUN_BUCKET_B=1</c>). Every number here was measured with an independent Python walk
///     (2026-09-03) before the parsers were written.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class DaggerfallTextRetailTests
{
    private static string RequireArena2()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        return root!;
    }

    [Fact]
    public void TextRsc_HasTheRetailTable_WithSixSharedRecords()
    {
        var path = Path.Combine(RequireArena2(), DaggerfallTextFile.FileName);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("TEXT.RSC"));

        var text = DaggerfallTextFile.Parse(File.ReadAllBytes(path));

        Assert.Equal(1_408, text.Records.Count);
        Assert.Equal(1_408, text.Records.Select(r => r.Id).Distinct().Count());
        Assert.Equal(1_396, text.Records.Select(r => r.Offset).Distinct().Count());
        Assert.Equal(0, text.Records[0].Id);
        Assert.Equal(9_999, text.Records.Max(r => r.Id));

        Assert.StartsWith("STRENGTH\nStrength governs encumbrance", text.FindById(0)!.Text, StringComparison.Ordinal);
        Assert.Equal(text.FindById(1200)!.Offset, text.FindById(1202)!.Offset);
        Assert.Equal(29_354, text.Records.Max(r => r.Raw.Length));
        Assert.Equal(9_000, text.Records.MaxBy(r => r.Raw.Length)!.Id);

        // 914 records are single-phrase; the rest carry alternatives, up to 51 of them.
        Assert.Equal(914, text.Records.Count(r => r.Subrecords.Count == 1));
        Assert.Equal(51, text.Records.Max(r => r.Subrecords.Count));
    }

    [Fact]
    public void Books_AllNinetyOneParse_WithTheirKnownQuirks()
    {
        var directory = Path.Combine(RequireArena2(), "BOOKS");
        Assert.SkipWhen(!Directory.Exists(directory), RealAssetPaths.SkipMessage("BOOKS"));

        var books = Directory.EnumerateFiles(directory)
            .Where(p => DaggerfallBookFile.IsBookFileName(Path.GetFileName(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => DaggerfallBookFile.Parse(File.ReadAllBytes(p), Path.GetFileName(p)))
            .ToList();

        Assert.Equal(91, books.Count);
        Assert.Equal(91, books.Select(b => b.Number).Distinct().Count());
        // The two trailing unknowns are placeholders: 1234/2345 on 90 books, one book differs.
        Assert.Equal(90, books.Count(b => b.Unknown2 == 1234 && b.Unknown3 == 2345));
        Assert.All(books, b => Assert.InRange(b.Unknown1, 1, 4));
        Assert.Equal(844, books.Sum(b => b.Pages.Count));
        Assert.Equal(38, books.Max(b => b.Pages.Count));

        // BOK00088's "naughty " (trailing space) counts; the reference's exact compare misses it.
        Assert.Equal(15, books.Count(b => b.IsNaughty));
        Assert.True(books.Single(b => b.Number == 88).IsNaughty);

        // 26 pages across the set lack their end-of-page byte and are clipped at the next offset.
        Assert.Equal(26, books.Sum(b => b.UnterminatedPageCount));
        Assert.Equal(4, books.Single(b => b.Number == 101).UnterminatedPageCount);

        var first = books.Single(b => b.Number == 0);
        Assert.Equal("The First Scroll of Baan Dar", first.Title);
        Assert.Equal("Arkan", first.Author);
        Assert.Equal(9, first.Pages.Count);
        Assert.StartsWith("The First Scroll of Baan Dar\n\nWhat follows is a translation", first.PageTexts[0], StringComparison.Ordinal);

        // BOK10000 is German, written in code page 437.
        var german = books.Single(b => b.Number == 10_000);
        Assert.Contains("verkündet", string.Join('\n', german.PageTexts), StringComparison.Ordinal);
        Assert.Contains("daß die Götter", string.Join('\n', german.PageTexts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyzer_AddsTextAndBookRecordsToTheInstall()
    {
        var arena2 = RequireArena2();
        var installRoot = Path.GetDirectoryName(arena2)!;

        var result = await ClassicGameAnalyzer.LoadAsync(installRoot);

        Assert.Equal(BethesdaGame.Daggerfall, result.Records.Game);
        var records = result.Records.GenericRecords;
        Assert.Equal(1_408, records.Count(r => r.RecordType == DaggerfallRecordSource.TextRecordType));
        Assert.Equal(91, records.Count(r => r.RecordType == DaggerfallRecordSource.BookRecordType));
        Assert.Equal(62 + 15_251 + 1_408 + 91, records.Count);
        Assert.Equal(records.Count, records.Select(r => r.FormId).Distinct().Count());
    }
}
