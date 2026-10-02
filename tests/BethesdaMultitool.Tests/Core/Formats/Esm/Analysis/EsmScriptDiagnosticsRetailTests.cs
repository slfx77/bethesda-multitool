using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Analysis;

/// <summary>
///     <c>diagnose-scripts</c> scoping and package-union decoding against the retail New Vegas
///     masters in <c>Sample/Builds</c>. The expected values were measured independently of this
///     tool by the 2026-09-28 audit's read-only mmap walks: the CTDA bytes of INFO 0x0015E9CC, the
///     PLDT bytes of PACK 0x000FE923 in both builds (<c>01 00 00 00 85 A2 09 00 ..</c> on PC,
///     <c>01 00 00 00 00 09 A2 85 ..</c> on the July 2010 Xbox 360 master), the PTDT of 0x000E62E1,
///     and the per-type census of every PLDT/PLD2/PTDT/PTD2 in the PACK group, whose pad bytes were
///     zero throughout. A wrong type offset or byte order cannot reproduce the census.
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class EsmScriptDiagnosticsRetailTests
{
    private const uint HorowitzInfo = 0x0015E9CC;
    private const uint Horowitz = 0x0015E9E6;
    private const uint Ves34VaultQuest = 0x00159FA0;
    private const uint AliceHostetlerRunAway = 0x000FE923;
    private const uint ChompsPackage = 0x000E62E1;

    public static TheoryData<string> Builds => ["Steam2022", "X360July2010"];

    [Fact]
    public void Retail_FalloutNv2022_Info0015E9CC_ExplicitOnly()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var path = RealAssetPaths.NewVegasBuilds.Steam2022();
        Assert.SkipWhen(path is null, RealAssetPaths.SkipMessage("2022 Steam FalloutNV.esm"));

        // The reported crash: --record 0x0015E9CC alone fanned the INFO out under the two legacy
        // actors and the audit threw on the duplicate INFO key.
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeFile(path, [], new HashSet<uint> { HorowitzInfo });

        Assert.Equal(BethesdaGame.FalloutNewVegas, result.Game);
        Assert.Empty(result.Targets);
        Assert.Empty(result.TargetMatches);
        Assert.Equal([HorowitzInfo], result.ExplicitRecordFormIds);
        Assert.Empty(result.MissingExplicitRecordFormIds);

        var record = Assert.Single(result.Records);
        Assert.Equal(EsmScriptDiagnosticsAnalyzer.ExplicitTargetLabel, record.Target);
        Assert.Equal("explicit-record", record.Relation);
        Assert.Equal("INFO", record.RecordType);
        Assert.Equal(HorowitzInfo, record.FormId);

        var dialogue = Assert.Single(result.Dialogue);
        Assert.Equal(Horowitz, dialogue.SpeakerFormId);
        Assert.Equal(Ves34VaultQuest, dialogue.QuestFormId);
        Assert.Equal("explicit", Assert.Single(result.DialogueAudit).Target);

        // GetIsID (0x48) VVault34Horowitz == 1.0, run on subject: the INFO's only condition.
        var condition = Assert.Single(result.Conditions);
        Assert.Equal("GetIsID", condition.FunctionName);
        Assert.Equal(Horowitz, condition.Parameter1);
        Assert.Equal("VVault34Horowitz", condition.Parameter1Label);
        Assert.Equal("000000000000803F48000000E6E91500000000000000000000000000", condition.RawBytes);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void Retail_PackageUnionCensus_And_Destinations_PcAndJuly(string build)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var path = build == "Steam2022"
            ? RealAssetPaths.NewVegasBuilds.Steam2022()
            : RealAssetPaths.NewVegasBuilds.X360July2010();
        Assert.SkipWhen(path is null, RealAssetPaths.SkipMessage($"{build} FalloutNV.esm"));

        var data = File.ReadAllBytes(path);
        var game = GameDetector.DetectFromBytes(data, Path.GetFileName(path)).Game;
        Assert.Equal(BethesdaGame.FalloutNewVegas, game);
        var (records, grups) = EsmParser.EnumerateRecordsWithGrups(data);
        var packs = RecordsOfTopLevelGroup(records, grups, "PACK");
        Assert.NotEmpty(packs);

        // Census: every union decodes (no short, non-zero-pad or unknown-type fallback), no unused
        // arm carries a value, and the per-type counts are the measured ones.
        var census = new SortedDictionary<string, SortedDictionary<byte, int>>(StringComparer.Ordinal);
        var undecoded = new List<string>();
        var nonZeroUnusedArms = 0;
        foreach (var pack in packs)
        {
            foreach (var sub in pack.Subrecords.Where(s => s.Signature is "PLDT" or "PLD2" or "PTDT" or "PTD2"))
            {
                if (!EsmScriptSubrecordSummaryFormatter.TryDecodePackageUnion("PACK", sub, game, out var union,
                        out var status))
                {
                    undecoded.Add($"0x{pack.Header.FormId:X8} {sub.Signature} {status}");
                    continue;
                }

                if (!census.TryGetValue(sub.Signature, out var byType))
                {
                    byType = new SortedDictionary<byte, int>();
                    census[sub.Signature] = byType;
                }

                byType[union.Type] = byType.GetValueOrDefault(union.Type) + 1;
                if (union.Arm == EsmScriptSubrecordSummaryFormatter.PackageUnionArm.None && union.Value != 0)
                {
                    nonZeroUnusedArms++;
                }
            }
        }

        Assert.Empty(undecoded);
        Assert.Equal(0, nonZeroUnusedArms);
        Assert.Equal(ExpectedCensus(build), FormatCensus(census));

        // Destinations, through the diagnostics summary itself: every PACK in one explicit run.
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            path,
            records,
            [],
            game,
            packs.Select(p => p.Header.FormId).ToHashSet());
        var packRows = result.Records.Where(r => r.RecordType == "PACK").ToList();
        Assert.All(packRows, row => Assert.Equal(EsmScriptDiagnosticsAnalyzer.ExplicitTargetLabel, row.Target));
        var summaries = packRows.ToDictionary(r => r.FormId, r => r.InterestingSubrecords);

        var alice = summaries[AliceHostetlerRunAway];
        if (build == "Steam2022")
        {
            Assert.Equal("AliceHostetlerRunAway",
                Assert.Single(packRows, row => row.FormId == AliceHostetlerRunAway).EditorId);
        }

        Assert.Contains(
            build == "Steam2022"
                ? "PLDT(type=1:InCell,cell=0x0009A285 (PrimmGenericHouse01),radius=0)"
                : "PLDT(type=1:InCell,cell=0x0009A285",
            alice,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DoorMarker", alice, StringComparison.Ordinal);
        Assert.DoesNotContain("0x01000000", alice, StringComparison.Ordinal);
        if (build == "Steam2022")
        {
            Assert.Contains("PTDT(type=0:SpecificReference,ref=0x00000014,count=750",
                summaries[ChompsPackage], StringComparison.Ordinal);
        }

        // No union is summarized the old way (first DWORD as a FormID) or falls back to raw.
        string[] forbidden =
        [
            "PLDT=0x", "PLD2=0x", "PTDT=0x", "PTD2=0x",
            "PLDT(length=", "PLD2(length=", "PTDT(length=", "PTD2(length="
        ];
        Assert.DoesNotContain(summaries.Values,
            s => forbidden.Any(token => s.Contains(token, StringComparison.Ordinal)));
    }

    /// <summary>The audit's per-type census (type:count), PC 2022 and July 2010 Xbox 360.</summary>
    private static string ExpectedCensus(string build)
    {
        return build == "Steam2022"
            ? "PLD2 0:198 1:10 2:18 3:78 6:22 7:94 | PLDT 0:1822 1:156 2:185 3:1414 6:227 | " +
              "PTD2 0:78 | PTDT 0:621 1:100 2:389 3:77"
            : "PLD2 0:190 1:6 2:16 3:76 6:22 7:94 | PLDT 0:1741 1:160 2:190 3:1384 6:210 | " +
              "PTD2 0:78 | PTDT 0:573 1:97 2:393 3:76";
    }

    private static string FormatCensus(SortedDictionary<string, SortedDictionary<byte, int>> census)
    {
        return string.Join(" | ", census.Select(signature =>
            signature.Key + " " + string.Join(' ', signature.Value.Select(type => $"{type.Key}:{type.Value}"))));
    }

    /// <summary>
    ///     The records inside the single top-level (type 0) GRUP with the given label, so a record
    ///     repeated elsewhere (an Xbox 360 streaming block) is not counted twice.
    /// </summary>
    private static List<ParsedMainRecord> RecordsOfTopLevelGroup(
        IReadOnlyList<ParsedMainRecord> records,
        IReadOnlyList<GrupHeaderInfo> grups,
        string label)
    {
        var group = Assert.Single(grups, g =>
            g.GroupType == 0 && string.Equals(Encoding.ASCII.GetString(g.Label), label, StringComparison.Ordinal));
        var end = group.Offset + group.GroupSize;
        return records
            .Where(r => r.Header.Signature == label && r.Offset > group.Offset && r.Offset < end)
            .ToList();
    }
}
