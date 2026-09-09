using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Tactics;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of the Fallout Tactics install. The <c>.bos</c>
///     archives are plain PKZIP and already open through the shared <c>ArchiveReader</c>, so these
///     read entities straight out of <c>entities_0.bos</c> with no extraction step.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class TacticsRetailTests
{
    private static string RequireCore()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var core = RealAssetPaths.Classics.FalloutTactics();
        Assert.SkipWhen(core is null, RealAssetPaths.SkipMessage("Fallout Tactics"));
        return core;
    }

    [Fact]
    public void EveryTaggedAssetInTheInstallCarriesTheSharedFraming()
    {
        // ⚑ THE FRAMING IS THE WHOLE GAME'S FILE HEADER, not one format's: '<' name '>' NUL,
        // an ASCII version, NUL. Measured 2026-09-06 over every entry in core\*.bos — 33,368
        // tagged files, ZERO malformed, across seven tags.
        var core = RequireCore();
        var archives = Directory.GetFiles(core, "*.bos")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(archives.Count == 0, RealAssetPaths.SkipMessage("Fallout Tactics .bos archives"));

        var byTag = new Dictionary<string, int>(StringComparer.Ordinal);
        var versionsByTag = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var malformed = 0;

        foreach (var path in archives)
        {
            using var archive = ArchiveReader.Open(path);
            foreach (var entry in archive.ListFiles())
            {
                var bytes = archive.ReadFile(entry.FullPath);
                if (bytes is null || bytes.Length == 0 || bytes[0] != (byte)'<')
                {
                    continue;
                }

                if (!TacticsTagChunk.TryRead(bytes, out var chunk))
                {
                    malformed++;
                    continue;
                }

                byTag[chunk.Tag] = byTag.GetValueOrDefault(chunk.Tag) + 1;
                if (!versionsByTag.TryGetValue(chunk.Tag, out var versions))
                {
                    versions = new HashSet<string>(StringComparer.Ordinal);
                    versionsByTag[chunk.Tag] = versions;
                }

                versions.Add(chunk.Version);
            }
        }

        // ⚑ Not one file that OPENS with '<' fails the framing.
        Assert.Equal(0, malformed);

        // The seven tags the install ships. ⛔ There is no <esh> — the .chr tag is <character>.
        Assert.Equal(
            ["campaign", "character", "entity", "sprite", "tile", "world", "zar"],
            byTag.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.DoesNotContain("esh", byTag.Keys);

        Assert.True(byTag["tile"] > 29_000, $"expected ~29,957 tiles, saw {byTag["tile"]}");
        Assert.True(byTag["zar"] >= 839, $"expected at least 839 ZARs, saw {byTag["zar"]}");

        // ⚠ The version is a STRING of varying length — <tile> alone spans "6".."10" — so a body
        // offset can never be a per-format constant.
        Assert.True(versionsByTag["tile"].Count > 1);
        Assert.Contains("68", versionsByTag["world"]);
        Assert.Contains("69", versionsByTag["world"]);
    }

    [Fact]
    public void EveryMissionInflatesToExactlyItsDeclaredSize()
    {
        // ⚑ The header carries the uncompressed size TWICE, and the stream must inflate to exactly
        // that — so the format self-checks and a truncated file cannot pass. Measured 2026-09-06
        // over the 103 ARCHIVED missions; the 25 LOOSE .mis that also ship are walked by
        // Tactics.TacticsLooseMissionRetailTests, and 103 + 25 = the 128 shipped missions.
        // ⚠ Windows file patterns are case-INSENSITIVE, so "mis*.bos" already matches Mis-Main_0.bos
        // and the second enumeration below is a duplicate that only Distinct() saves — counting
        // without it is how a "206 missions" figure was once published. Do not drop the Distinct.
        var core = RequireCore();
        var archives = Directory.GetFiles(core, "mis*.bos").Concat(Directory.GetFiles(core, "Mis*.bos"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(archives.Count == 0, RealAssetPaths.SkipMessage("Fallout Tactics mission archives"));

        var failures = new List<string>();
        var byVersion = new Dictionary<string, int>(StringComparer.Ordinal);
        var teamCounts = new List<int>();
        var rosters = 0;
        var worlds = 0;

        foreach (var path in archives)
        {
            using var archive = ArchiveReader.Open(path);
            foreach (var entry in archive.ListFiles()
                         .Where(e => e.Name.EndsWith(".mis", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = archive.ReadFile(entry.FullPath);
                if (bytes is null)
                {
                    failures.Add($"{entry.Name}: could not be read");
                    continue;
                }

                if (!TacticsMissionFile.TryParse(bytes, entry.Name, out var mission, out var error))
                {
                    failures.Add(error);
                    continue;
                }

                worlds++;
                byVersion[mission.Version] = byVersion.GetValueOrDefault(mission.Version) + 1;
                if (mission.Teams.Count > 0)
                {
                    rosters++;
                    teamCounts.Add(mission.Teams.Count);
                }

                Assert.Equal('8', mission.WorldVersion);

                // Every inflated world opens with the family's tag framing.
                Assert.StartsWith(TacticsMissionFile.WorldTag,
                    Encoding.ASCII.GetString(mission.World, 0, TacticsMissionFile.WorldTag.Length),
                    StringComparison.Ordinal);
            }
        }

        Assert.Empty(failures);
        Assert.Equal(103, worlds);

        // ⚠ BOTH versions ship. A reader pinned to one rejects a sixth of the campaign.
        Assert.Equal(new Dictionary<string, int>(StringComparer.Ordinal) { ["68"] = 87, ["69"] = 16 }, byVersion);

        // The team roster walks on every mission — the parallel u32 array repeating the count is
        // what confirms it. Rosters run 1-8 teams, most commonly 8.
        Assert.Equal(103, rosters);
        Assert.All(teamCounts, n => Assert.InRange(n, 1, 8));
        Assert.Equal(60, teamCounts.Count(n => n == 8));
    }

    [Fact]
    public void EveryEntityAndCharacterIsConsumedExactlyByTheSelfDescribingWalk()
    {
        // ⚑ PARSING IS THE PROOF: each property declares its own size, so a correct walk lands
        // precisely on the end of the file. Measured 2026-09-06 — 1,498 .ent (23,740 properties)
        // and 39 .chr (886), all exact.
        var core = RequireCore();
        var path = Path.Combine(core, "entities_0.bos");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("entities_0.bos"));

        using var archive = ArchiveReader.Open(path);
        var entries = archive.ListFiles()
            .Where(e => e.Name.EndsWith(".ent", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".chr", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var failures = new List<string>();
        var byExtension = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var properties = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var bytes = archive.ReadFile(entry.FullPath);
            if (bytes is null)
            {
                failures.Add($"{entry.Name}: could not be read from the archive");
                continue;
            }

            if (!TacticsPropertyBag.TryParse(bytes, entry.Name, out var bag, out var error))
            {
                failures.Add(error);
                continue;
            }

            var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
            byExtension[extension] = byExtension.GetValueOrDefault(extension) + 1;
            properties += bag.Properties.Count;
            foreach (var property in bag.Properties)
            {
                names.Add(property.Name);
            }
        }

        Assert.Empty(failures);
        Assert.Equal(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [".ent"] = 1498,
            [".chr"] = 39
        }, byExtension);
        Assert.Equal(23_740 + 886, properties);

        // The oracle is prose: authored property names have to read as English, which no structural
        // check could establish.
        Assert.Contains("Display Name", names);
        Assert.Contains("XP Reward", names);
        Assert.Contains("Race Type", names);
    }
}