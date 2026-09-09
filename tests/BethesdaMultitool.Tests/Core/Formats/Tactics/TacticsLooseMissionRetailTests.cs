using System.Text;
using BethesdaMultitool.Core.Formats.Tactics;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Opt-in (<c>RUN_BUCKET_B=1</c>) checks over the <b>25 LOOSE missions that ship on disk</b> —
///     the half of the shipped corpus no other test walked, and the half an earlier corpus claim
///     omitted entirely while double-counting the archived one.
///     <para>
///         ⚑ The shipped population, counted once on 2026-09-07 by an INDEPENDENT Python walk
///         (scratchpad <c>census2.py</c> / <c>loose.py</c>): <b>128 missions</b> = 103 inside the
///         seven <c>mis-*.bos</c> / <c>Mis-Main_0.bos</c> archives (covered by
///         <c>Classic.TacticsRetailTests.EveryMissionInflatesToExactlyItsDeclaredSize</c>) + the 25
///         loose ones pinned by name below. Versions are "68" on 98 and "69" on 30 across the whole
///         128; the loose split is 11 / 14, so the loose files are NOT a version subset of the
///         archived ones and a corpus that skips them cannot claim "every shipped mission".
///     </para>
///     <para>
///         ⛔ The count this class exists to correct: "206 shipped <c>.mis</c> entries ... 207/207"
///         was a doubled archive list (a case-insensitive <c>mis*.bos</c> plus <c>Mis*.bos</c>
///         enumeration) minus the loose tree. The true Adler-32 trailer figure is <b>129/129</b> —
///         103 archived, these 25, and the world inside <c>Snake.sav</c>.
///     </para>
///     <para>
///         ⚑⚑ Extending the corpus was not book-keeping: it found a READER BUG. Of the 128 shipped
///         missions exactly one — <c>core/editor/ambientExample.mis</c>, a loose file — writes its
///         <c>&lt;mph&gt;</c> roster names WIDE (bit 31 set in the length prefix), and
///         <c>TacticsMissionFile.ReadTeams</c> decoded every name as ASCII, so that mission came
///         back with an EMPTY roster and no error. Fixed 2026-09-07; the 103 archived missions could
///         never have shown it.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class TacticsLooseMissionRetailTests
{
    /// <summary>
    ///     The ONE shipped mission whose <c>&lt;mph&gt;</c> roster names carry the wide flag, and the
    ///     two names it holds — read out of the inflated world in Python before the reader was
    ///     touched. An ASCII-only roster reader returns nothing here and says nothing about it.
    /// </summary>
    private const string WideRosterMission = "editor/ambientExample.mis";

    /// <summary>
    ///     Every loose <c>.mis</c> under <c>core</c> with the version measured in Python, keyed by
    ///     the path relative to <c>core</c>. Pinned as literals so the expectation cannot come from
    ///     the reader.
    /// </summary>
    private static readonly Dictionary<string, (string Version, int Teams)> Measured =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["campaigns/missions/core/bunker01.mis"] = ("69", 8),
            ["campaigns/missions/core/bunker03.mis"] = ("69", 8),
            ["campaigns/missions/core/bunker05.mis"] = ("69", 8),
            ["campaigns/missions/core/mission01.mis"] = ("69", 6),
            ["campaigns/missions/core/mission02.mis"] = ("69", 7),
            ["campaigns/missions/core/mission03.mis"] = ("69", 6),
            ["campaigns/missions/core/mission04.mis"] = ("69", 6),
            ["campaigns/missions/core/mission06.mis"] = ("69", 8),
            ["campaigns/missions/core/mission09.mis"] = ("69", 8),
            ["campaigns/missions/core/mission11.mis"] = ("69", 7),
            ["campaigns/missions/core/mission23.mis"] = ("69", 6),
            ["campaigns/missions/core/mission24.mis"] = ("69", 3),
            ["editor/ambientExample.mis"] = ("69", 2),
            ["missions/Assault/Downtown.mis"] = ("68", 2),
            ["missions/Assault/Lost Vault.mis"] = ("68", 2),
            ["missions/Assault/Robotica.mis"] = ("68", 2),
            ["missions/Assault/Uphill Battle.mis"] = ("68", 2),
            ["missions/CTF/All your flag are belong to us.mis"] = ("68", 2),
            ["missions/CTF/Motor Sports.mis"] = ("69", 4),
            ["missions/CTF/Plains of Carnage.mis"] = ("68", 2),
            ["missions/Scavenger/Taking in the Trash.mis"] = ("68", 8),
            ["missions/Skirmish/High Noon.mis"] = ("68", 8),
            ["missions/Skirmish/Industrial Disease.mis"] = ("68", 8),
            ["missions/Skirmish/Junkyard.mis"] = ("68", 8),
            ["missions/Skirmish/Shellshocked.mis"] = ("68", 8)
        };

    private static List<(string Relative, string Path)> RequireLooseMissions()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var core = RealAssetPaths.Classics.FalloutTactics();
        Assert.SkipWhen(core is null, RealAssetPaths.SkipMessage("Fallout Tactics"));

        // ⚠ The Windows wildcard "*.mis" also matches longer extensions (the old 8.3 rule), so the
        // extension is re-checked in managed code rather than trusted to the pattern.
        var found = Directory.GetFiles(core, "*.mis", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".mis", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Relative: Path.GetRelativePath(core, path).Replace('\\', '/'), Path: path))
            .OrderBy(pair => pair.Relative, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(found.Count == 0, RealAssetPaths.SkipMessage("loose Fallout Tactics missions"));
        return found;
    }

    /// <summary>Counts a marker's occurrences without using the scanner under test.</summary>
    private static int CountMarkers(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        var count = 0;
        var offset = 0;
        while (offset <= haystack.Length - needle.Length)
        {
            var at = haystack[offset..].IndexOf(needle);
            if (at < 0)
            {
                break;
            }

            count++;
            offset += at + needle.Length;
        }

        return count;
    }

    [Fact]
    public void TheInstallShipsExactlyTheTwentyFiveLooseMissionsMeasured()
    {
        // A retail install carries these and only these; a user-authored map would legitimately
        // change the count, and that is a new measurement rather than a silent pass.
        var found = RequireLooseMissions();

        Assert.Equal(25, found.Count);
        Assert.Equal(
            Measured.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase),
            found.Select(pair => pair.Relative));
    }

    [Fact]
    public void EveryLooseMissionInflatesExactlyAndCarriesItsMeasuredVersion()
    {
        var found = RequireLooseMissions();

        var byVersion = new Dictionary<string, int>(StringComparer.Ordinal);
        var teamMarkers = 0;

        foreach (var (relative, path) in found)
        {
            var bytes = File.ReadAllBytes(path);
            Assert.True(TacticsMissionFile.TryParse(bytes, relative, out var mission, out var error), error);

            // The version each file carries was read in Python, file by file, before this ran.
            Assert.Equal(Measured[relative].Version, mission.Version);
            byVersion[mission.Version] = byVersion.GetValueOrDefault(mission.Version) + 1;

            // The same framing the archived missions show: <mph> + NUL, world version '8'.
            Assert.Equal('8', mission.WorldVersion);
            Assert.StartsWith(
                TacticsMissionFile.WorldTag,
                Encoding.ASCII.GetString(mission.World, 0, TacticsMissionFile.WorldTag.Length),
                StringComparison.Ordinal);

            // ⚠⚠ The roster walks on every one — and the count is the one read out of each world in
            // Python, not a range. This is the assertion that CAUGHT the reader reading a roster's
            // names as ASCII regardless of the length prefix's bit 31: ambientExample.mis writes
            // its two names WIDE and came back with an EMPTY roster and no error.
            Assert.Equal(Measured[relative].Teams, mission.Teams.Count);

            // ⚑ Cross-check independent of the scanner: the number of <Team> chunks it returns
            // equals the number of "<Team>\0" markers counted here by brute force. Measured in
            // Python as exactly 9 in every one of the 25 worlds.
            var markers = CountMarkers(mission.World, "<Team>\0"u8);
            Assert.Equal(9, markers);
            Assert.Equal(markers, TacticsMissionFile.ScanTeams(mission.World).Count);
            teamMarkers += markers;
        }

        Assert.Equal(
            new Dictionary<string, int>(StringComparer.Ordinal) { ["68"] = 11, ["69"] = 14 },
            byVersion);
        Assert.Equal(225, teamMarkers);
    }

    [Fact]
    public void TheEditorExampleIsTheOneMissionWhoseRosterNamesAreWide()
    {
        // ⚑ The name prefix's bit 31 is the encoding flag in a world's roster too, and exactly one
        // shipped mission exercises it. Both halves are pinned: the wide file reads its two names,
        // and a mission from the ASCII majority still reads its own — so this is a flag being
        // honoured, not one encoding replacing the other.
        var found = RequireLooseMissions();

        var wide = found.Single(pair =>
            string.Equals(pair.Relative, WideRosterMission, StringComparison.OrdinalIgnoreCase));
        var wideMission = TacticsMissionFile.Parse(File.ReadAllBytes(wide.Path), wide.Relative);
        Assert.Equal(["Team 1", "Team 2"], wideMission.Teams);

        var ascii = found.Single(pair =>
            string.Equals(pair.Relative, "campaigns/missions/core/mission01.mis", StringComparison.OrdinalIgnoreCase));
        var asciiMission = TacticsMissionFile.Parse(File.ReadAllBytes(ascii.Path), ascii.Relative);
        Assert.Equal(
            ["BOS", "Tribals (T)", "Raiders (R)", "Sentries (B)", "Hostages", "Brahmin"],
            asciiMission.Teams);
    }

    [Fact]
    public void ALooseMissionWithATrailingByteIsRefused()
    {
        // The falsification for the trailer check, on REAL data rather than a synthetic vector: the
        // inflate still succeeds and still matches the declared size, so only the Adler-32 at the
        // buffer's end can reject this.
        var found = RequireLooseMissions();
        var (relative, path) = found[0];
        var bytes = File.ReadAllBytes(path);

        Assert.True(TacticsMissionFile.TryParse(bytes, relative, out _, out var error), error);

        var padded = new byte[bytes.Length + 1];
        bytes.CopyTo(padded, 0);
        Assert.False(TacticsMissionFile.TryParse(padded, relative, out _, out var padError));
        Assert.Contains("does not end at the last byte", padError, StringComparison.Ordinal);
    }
}