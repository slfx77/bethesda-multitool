using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) that Fallout: Brotherhood of Steel's records
///     synthesize from the XBOX disc as well as the PS2 one. Same record source, same mount seam —
///     the only difference is where the disc puts its data: <c>resx\</c> and the per-level
///     directories under <c>resx\c1</c>…<c>resx\c4</c> instead of <c>DATA\</c>.
///     <para>
///         ⚠ These drive <see cref="BosRecordSource.Populate" /> over the mounted image directly,
///         which is exactly what <c>ClassicGameAnalyzer</c> does for this game once the profile
///         claims the disc. The marker test also verifies that the actual archive entry names
///         identify the Xbox layout through <see cref="ClassicGameLocator" />.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class BrotherhoodOfSteelXboxRecordTests
{
    private static string RequireIso()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var iso = RealAssetPaths.Consoles.BrotherhoodOfSteelXboxIso();
        Assert.SkipWhen(iso is null, RealAssetPaths.SkipMessage("Fallout: Brotherhood of Steel Xbox disc image"));
        return iso;
    }

    private static List<GenericEsmRecord> Synthesize(string iso)
    {
        using var disc = GameFileSystem.OpenArchive(iso);
        var records = new RecordCollection();
        BosRecordSource.Populate(disc, records, CancellationToken.None);
        return records.GenericRecords;
    }

    [Fact]
    public void TheXboxDiscCarriesBothOfTheProfileMarkersUnderItsOwnLayout()
    {
        var iso = RequireIso();
        using var reader = ArchiveReader.Open(iso);
        var names = reader.ListFiles()
            .Select(e => e.FullPath.Replace('/', '\\'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The Xbox disc has its own boot image and master-table layout, with neither PS2 marker.
        Assert.Contains("default.xbe", names);
        Assert.Contains(@"resx\all.ddf", names);
        Assert.DoesNotContain("SYSTEM.CNF", names);
        Assert.DoesNotContain(@"DATA\ALL.DDF", names);
        Assert.Equal(BethesdaGame.FalloutBrotherhoodOfSteel,
            ClassicGameLocator.DetectFromArchiveNames(names)?.Game);
    }

    [Fact]
    public void TheMasterCatalogueAndLevelCountMatchThePs2Release()
    {
        var iso = RequireIso();
        var records = Synthesize(iso);

        // ⚑ Both pins are independently readable off the disc without this code: the master's own
        // record-count dword at resx\all.ddf +0 is 2,243 (identical to the PS2 master's, though the
        // two files are NOT byte-identical — same 801,648 bytes, different md5), and the disc holds
        // 55 .DDF files, of which one is that master.
        Assert.Equal(2_243, records.Count(r => r.RecordType == BosRecordSource.DefinitionRecordType));
        Assert.Equal(54, records.Count(r => r.RecordType == BosRecordSource.LevelRecordType));

        var ids = records.Select(r => r.FormId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        // ⚑ The one place the two discs DISAGREE, and the reason is the string tables, not the
        // reader: the Xbox ships 61 .SDB files to the PS2's 56 — deftexte.sdb plus gtext_f/g/i/j
        // (French, German, Italian, Japanese). That cuts BOST from 3,933 to 3,857 (−76) even though
        // there are MORE strings, because a key carrying different text in different languages is
        // ambiguous and is dropped rather than guessed, and because deftexte.sdb turns keys that
        // were text-only into record NAMES: named definitions go 1,586 → 2,171 (+585).
        //
        // ⚑ INDEPENDENTLY DERIVED, 2026-09-08, not read out of this code. A throwaway Python
        // reader written from the documented layouts alone — .SDB magic 1499 / u32 table offset /
        // u32 count / UTF-16LE strings from +12 / 8-byte (hash, offset) slots stopping at the
        // 0x002F0178 + 3,080,568 terminator; .DDF 772-byte header + 12-byte directory records; and
        // the hash h = (h*0x80104025) ^ ((int32)h >> 27) ^ c over UTF-16 code units — walking the
        // EXTRACTED tree under Sample\Builds\…(Xbox - Final)\extracted reproduces every figure
        // here exactly: 61 .SDB parsing to 14,829 entries of which 8,758 hash to their own key,
        // 55 .DDF, master count dword 2,243, BOST 3,857, named definitions 2,171, total 6,154.
        // ⚠ An earlier round of this test read the three numbers out of a deliberately-failing
        // assertion — i.e. from the code under test — and labelled them "regression pins". They
        // were right, but that provenance is not evidence and the label is now retired.
        Assert.Equal(3_857, records.Count(r => r.RecordType == BosRecordSource.TextRecordType));
        Assert.Equal(2_171, records.Count(r =>
            r.RecordType == BosRecordSource.DefinitionRecordType && r.FullName is not null));

        // ⚑ The one-record gap between "2,243 definitions" and "2,242 distinct keys", pinned so the
        // named-definition figure above is reconcilable rather than magic: the master lists hash
        // 0x1F2D72DB TWICE, and it carries a proven name, so it contributes two named records.
        // 2,170 distinct named keys + that duplicate = 2,171.
        var definitionHashes = records
            .Where(r => r.RecordType == BosRecordSource.DefinitionRecordType)
            .Select(r => (string)r.Fields["Hash"]!)
            .ToList();
        Assert.Equal(2_243, definitionHashes.Count);
        Assert.Equal(2_242, definitionHashes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, definitionHashes.Count(h => h == "0x1F2D72DB"));
        Assert.Equal(2_170, records
            .Where(r => r.RecordType == BosRecordSource.DefinitionRecordType && r.FullName is not null)
            .Select(r => (string)r.Fields["Hash"]!)
            .Distinct(StringComparer.Ordinal)
            .Count());

        // The total `stats` reports for this disc, against the PS2 disc's 6,230.
        Assert.Equal(6_154, records.Count);
    }

    [Fact]
    public void DefinitionsAreStillNamedByTheStringDatabasesAndReadAsProse()
    {
        var iso = RequireIso();
        var records = Synthesize(iso);
        var named = records
            .Where(r => r.RecordType == BosRecordSource.DefinitionRecordType && r.FullName is not null)
            .Select(r => r.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        // PROSE IS THE ORACLE, exactly as on the PS2 disc: the game's three player characters and
        // one of its AI behaviours come back as names. No sector arithmetic could produce these.
        Assert.Contains("Cyrus", named);
        Assert.Contains("Cain", named);
        Assert.Contains("Nadia", named);
        Assert.Contains("DuckAndCover", named);
    }
}
