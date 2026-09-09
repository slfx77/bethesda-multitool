using System.IO.Compression;
using System.Text;
using BethesdaMultitool.Core.Formats.Archives;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of a retail Fallout 2 save slot — the Bucket-B save
///     fixture the backlog asked for.
///     <para>
///         ⚠ Unlike the install fixtures this depends on a slot existing, so every test here skips
///         rather than fails when none does.
///         <see cref="RealAssetPaths.Classics.Fallout2SaveSlot" /> probes the live Steam install
///         first and the staged <c>Sample/Builds</c> copy second (SAVE.DAT 62,871 B, md5
///         <c>a7168132db9138b3a0f9b1caef8ba8f8</c>), so an emptied install is not a missing fixture.
///     </para>
///     <para>
///         ⚑ This slot is MID-GAME, which is the only reason the duplication and the automap-length
///         findings can be settled at all — the Fallout 1 slot is a start-of-game save and cannot
///         discriminate either. Two of the tests below exist purely as that control.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class Fallout2SaveRetailTests
{
    private static string RequireSlot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var slot = RealAssetPaths.Classics.Fallout2SaveSlot();
        Assert.SkipWhen(slot is null, RealAssetPaths.SkipMessage("a Fallout 2 save slot"));
        return slot;
    }

    [Fact]
    public void TheSaveHeaderDateMatchesTheFilesOwnTimestamp()
    {
        // ⚑ THE ORACLE IS OUTSIDE THE FILE. Three u16 at +0x5B read as a day, month and year, and
        // they agree with the save's filesystem modification date — which no mis-read of the
        // layout could produce by accident.
        var slot = RequireSlot();
        var path = Path.Combine(slot, "SAVE.DAT");
        var bytes = File.ReadAllBytes(path);

        var save = FalloutSaveFile.Parse(bytes, "SAVE.DAT");
        var written = File.GetLastWriteTime(path);

        Assert.Equal(FalloutSaveFile.Fallout2Version, save.Version);
        Assert.Equal(written.Year, save.Year);
        Assert.Equal(written.Month, save.Month);
        Assert.Equal(written.Day, save.Day);
        Assert.NotEmpty(save.PlayerName);
    }

    [Fact]
    public void TheInGameDateReadsMonthBeforeDay()
    {
        // ⚑⚑ THIS SAVE IS WHAT SETTLES THE FIELD ORDER, and only this save can. The in-game date
        // at +0x65 reads 7 and 25 here: 25 cannot be a month, so the pair must be month-then-day,
        // the opposite of the real-world date at +0x5B. 25 July 2241 is Fallout 2's published
        // campaign start, a value from outside the file. ⚠ The Fallout 1 save cannot discriminate
        // — its two values are 12 and 5 and both orderings name a real date.
        var slot = RequireSlot();
        var save = FalloutSaveFile.Parse(File.ReadAllBytes(Path.Combine(slot, "SAVE.DAT")), "SAVE.DAT");

        Assert.InRange(save.GameMonth, 1, 12);
        Assert.InRange(save.GameDay, 1, 31);
        Assert.InRange(save.GameYear, 2241, 2400);
    }

    [Fact]
    public void TheGameVariableBlockIsStoredTwiceEvenMidGame()
    {
        // ⚑⚑ THE CONTROL FOR THE DUPLICATION CLAIM, drawn from the affected population. On a
        // start-of-game Fallout 1 save every global still equals its .GAM initial, so "one array
        // stored twice" and "two arrays that happen to agree" are indistinguishable. This save is
        // MID-GAME — and that has to be MEASURED against VAULT13.GAM, not assumed.
        // ⚠⚠ An earlier revision asserted only `block contains a non-zero byte`, which a table of
        // .GAM initials also satisfies: it could not discriminate the two hypotheses it was cited
        // for. The comparison below is the one the claim actually rests on.
        var slot = RequireSlot();
        var bytes = File.ReadAllBytes(Path.Combine(slot, "SAVE.DAT"));
        Assert.SkipWhen(bytes.Length < 0x7567 + 2048, "the save is shorter than the globals block");

        var root = Path.GetFullPath(Path.Combine(slot, "..", "..", ".."));
        var dat = new[] { "patch000.dat", "master.dat" }
            .Select(n => Path.Combine(root, n))
            .FirstOrDefault(File.Exists);
        Assert.SkipWhen(dat is null, RealAssetPaths.SkipMessage("the Fallout 2 install the slot sits in"));

        using var backend = new Dat2Backend(Dat2Archive.Parse(dat));
        var entry = backend.ListFiles()
            .FirstOrDefault(e => e.FullPath.Equals("data/VAULT13.GAM", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(entry is null, RealAssetPaths.SkipMessage("data/VAULT13.GAM"));

        var declared = FalloutGameVariables
            .Parse(Encoding.Latin1.GetString(backend.Extract(entry)), "VAULT13.GAM").Variables;
        Assert.True(declared.Count > 600, $"VAULT13.GAM declared only {declared.Count} variables");

        Assert.True(
            FalloutSaveBody.TryReadGameVariables(
                bytes, declared.Count, out var values, out var copyOffset, out var error),
            error);

        var changed = declared.Where((v, i) => values[i] != v.Value).Select(v => v.Name).ToList();

        // ⚑ BOTH directions matter. Nearly all values still equal their initial, which is what
        // says we are looking at the globals array at all; but SOME do not, which is what says
        // this block is play state and not a static table — and it is still stored twice.
        // Measured on the staged slot: exactly 5 differ (GVAR_PLAYER_REPUTATION 15,
        // GVAR_START_ARROYO_TRIAL 1, GVAR_KARMA_WANDERER 1, GVAR_TOWN_REP_ARROYO 65,
        // GVAR_BUST_SKEEVE 3).
        Assert.NotEmpty(changed);
        Assert.InRange(changed.Count, 1, declared.Count / 10);
        Assert.True(copyOffset > 0, "the globals block should appear a second time");

        // …and the visited-map list, which sits between the two copies, names exactly the sidecars.
        Assert.True(FalloutSaveBody.TryReadVisitedMaps(bytes, out var maps, out _));
        var onDisk = Directory.EnumerateFiles(slot, "*.SAV")
            .Select(f => Path.GetFileName(f))
            .Where(n => !n.Equals("AUTOMAP.SAV", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(
            onDisk,
            maps.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDwordAfterTheMapListIsTheINFLATEDAutomapLength()
    {
        // ⚑⚑ THE HALF OF THE ORACLE THAT DISCRIMINATES. The u32 between the visited-map names and
        // the second copy of the globals reads 10,449 here while AUTOMAP.SAV is 4,160 bytes on
        // disk — which is exactly why this lead was once published as REFUTED. It is not: the file
        // is GZIPPED and inflates to 10,449. Fallout 1's automap is plain and matches directly, so
        // the field is the UNCOMPRESSED length in both games.
        // ⚠ Comparing against the on-disk size is what fails here; that is the falsifier, and it
        // fires on this fixture and not on Fallout 1's.
        var slot = RequireSlot();
        var path = Path.Combine(slot, "AUTOMAP.SAV");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("AUTOMAP.SAV"));

        var raw = File.ReadAllBytes(path);
        Assert.True(raw[0] == 0x1F && raw[1] == 0x8B, "Fallout 2 gzips its automap");

        using var inflated = new MemoryStream();
        using (var gzip = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress))
        {
            gzip.CopyTo(inflated);
        }

        var bytes = File.ReadAllBytes(Path.Combine(slot, "SAVE.DAT"));
        Assert.True(FalloutSaveBody.TryReadAutomapLength(bytes, out var length, out _));
        Assert.Equal((int)inflated.Length, length);
        Assert.NotEqual(raw.Length, length);
    }

    [Fact]
    public void TheCurrentMapNameResolvesToASidecarBesideTheSave()
    {
        // ⚑ Second independent oracle: the name at +0x73 must be one of the .SAV files present.
        var slot = RequireSlot();
        var save = FalloutSaveFile.Parse(File.ReadAllBytes(Path.Combine(slot, "SAVE.DAT")), "SAVE.DAT");

        var sidecars = Directory.EnumerateFiles(slot, "*.SAV")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(save.MapName, sidecars);
    }

    [Fact]
    public void TheFixedWidthNameFieldsCarryNothingAfterTheirTerminator()
    {
        // ⚠⚠ The Fallout 1 half of a question this track leaves OPEN: whether the three
        // fixed-width name fields are NUL-PADDED or NUL-TERMINATED. Neither fixture can say —
        // in this save the fields terminate at 9 / 9 / 12 and every byte after is zero, exactly as
        // in the Fallout 1 slot, so there is no leftover to discriminate the two readings and
        // FalloutSaveFile.ReadFixed's first-NUL rule is conservative rather than measured.
        // ⚑ A failure here would BE the discriminator; see Fallout1SaveRetailTests for the twin.
        var slot = RequireSlot();
        var bytes = File.ReadAllBytes(Path.Combine(slot, "SAVE.DAT"));

        var fields = new (int Offset, int Length, int Terminator)[]
        {
            (FalloutSaveFile.PlayerNameOffset, FalloutSaveFile.PlayerNameLength, 9),
            (FalloutSaveFile.SaveNameOffset, FalloutSaveFile.SaveNameLength, 9),
            (FalloutSaveFile.MapNameOffset, FalloutSaveFile.MapNameLength, 12)
        };

        foreach (var (offset, length, terminator) in fields)
        {
            var field = bytes[offset..(offset + length)];
            Assert.Equal(terminator, Array.IndexOf(field, (byte)0));
            Assert.All(field[(terminator + 1)..], b => Assert.Equal(0, b));
        }
    }

    [Fact]
    public void TheSavSidecarsAreGzippedMapsThatTheShippedMapReaderParses()
    {
        // ⚑ The backlog's hypothesis, confirmed: a save's per-map state needs NO second codec —
        // gunzip a sidecar and it IS a Fallout MAP, which FalloutMapFile already reads.
        // ⚠ AUTOMAP.SAV is the exception: it gunzips but is not a map, so route by CONTENT.
        var slot = RequireSlot();
        var sidecars = Directory.EnumerateFiles(slot, "*.SAV")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.NotEmpty(sidecars);

        var maps = 0;
        var others = new List<string>();
        foreach (var path in sidecars)
        {
            // ⚠ Routing is by CONTENT, not by game: Fallout 1 stores the same sidecars plain.
            if (FalloutSaveFile.TryReadSidecarMap(
                    File.ReadAllBytes(path), Path.GetFileName(path), out var map, out _))
            {
                maps++;

                // The map's own +4 name field equals the file it came from — the same self-naming
                // rule the 72 shipped maps follow. ⚠ MapName, not Name: Name is the string this
                // test just passed in, so asserting on it could never fail.
                Assert.Equal(Path.GetFileName(path), map.MapName, true);
                Assert.Equal(20u, map.Version);
            }
            else
            {
                others.Add(Path.GetFileName(path));
            }
        }

        Assert.True(maps > 0, "no sidecar parsed as a Fallout map");
        Assert.All(others, n => Assert.Equal("AUTOMAP.SAV", n, true));
    }
}