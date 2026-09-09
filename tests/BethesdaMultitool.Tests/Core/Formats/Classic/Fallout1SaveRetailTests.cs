using System.Text;
using BethesdaMultitool.Core.Formats.Archives;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of a retail Fallout 1 save slot, mirroring
///     <see cref="Fallout2SaveRetailTests" /> and adding the body sections located 2026-09-07.
///     <para>
///         The fixture is the SLOT01 staged into the corpus as
///         <c>Sample/Builds/Fallout (2023-6-13, Steam - Final)/DATA/SAVEGAME/SLOT01</c> —
///         <c>SAVE.DAT</c> 38,339 bytes, md5 <c>87c43024d1b55b9af8594c9a1b5f41bd</c>, player "Cal",
///         at V13ENT with primaries 7/5/6/3/8/5/6. Because it is a staged build and not a live
///         install it is stable, so these tests PIN that identity: a fixture swap should fail
///         loudly here rather than quietly weaken every measurement below.
///     </para>
///     <para>
///         ⚠⚠ An earlier revision asserted only format invariants, on the belief that the slot had
///         been deleted mid-session. It had not — the live Steam install's <c>SAVEGAME</c> emptied
///         itself, and the corpus copy was there the whole time;
///         <see cref="RealAssetPaths.Classics.Fallout1SaveSlot" /> now probes both. ⛔ Do not
///         conclude "the fixture is gone" from one empty directory.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class Fallout1SaveRetailTests
{
    /// <summary>Declarations in retail <c>VAULT13.GAM</c>; the authors' own last comment is "// (617)".</summary>
    private const int Fallout1GlobalCount = 618;

    private static string RequireSlot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var slot = RealAssetPaths.Classics.Fallout1SaveSlot();
        Assert.SkipWhen(slot is null, RealAssetPaths.SkipMessage("a Fallout 1 save slot"));
        return slot;
    }

    private static byte[] ReadSave(string slot)
    {
        return File.ReadAllBytes(Path.Combine(slot, "SAVE.DAT"));
    }

    /// <summary>
    ///     The install the slot belongs to — <c>&lt;root&gt;/DATA/SAVEGAME/SLOT01</c>, so three
    ///     levels up. ⚠ Taken from the SLOT rather than from
    ///     <see cref="RealAssetPaths.Classics.Fallout1" />, so the <c>.GAM</c> oracle always comes
    ///     from the same install as the save even when one resolves to the staged build and the
    ///     other to a live Steam copy.
    /// </summary>
    private static string InstallRoot(string slot)
    {
        return Path.GetFullPath(Path.Combine(slot, "..", "..", ".."));
    }

    private static List<string> MapSidecars(string slot)
    {
        return Directory.EnumerateFiles(slot, "*.SAV")
            .Select(f => Path.GetFileName(f))
            .Where(n => !n.Equals("AUTOMAP.SAV", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [Fact]
    public void TheSaveHeaderDeclaresFallout1sVersionAndMatchesTheFilesOwnTimestamp()
    {
        // ⚑ THE ORACLE IS OUTSIDE THE FILE. The three u16 at +0x5B read as a day, month and year
        // and agree with the save's filesystem modification date. The version word is the second
        // check: Fallout 1 declares 0x00010001 where Fallout 2 declares 0x00010002, so the same
        // header serves both games and the low word is what separates them.
        var slot = RequireSlot();
        var path = Path.Combine(slot, "SAVE.DAT");
        var save = FalloutSaveFile.Parse(File.ReadAllBytes(path), "SAVE.DAT");
        var written = File.GetLastWriteTime(path);

        Assert.Equal(0x0001_0001u, save.Version);
        Assert.Equal(FalloutSaveFile.Fallout1Version, save.Version);
        Assert.Equal('R', save.ReleaseType);
        Assert.Equal(written.Year, save.Year);
        Assert.Equal(written.Month, save.Month);
        Assert.Equal(written.Day, save.Day);

        // The staged slot's identity, pinned. ⚠ These are the numbers every other measurement in
        // this class was taken against, so a swapped fixture must fail HERE and not silently.
        Assert.Equal(38339, File.ReadAllBytes(path).Length);
        Assert.Equal("Cal", save.PlayerName);
        Assert.Equal("Cal1", save.SaveName);
        Assert.Equal("V13ENT.SAV", save.MapName, true);
        Assert.Equal(264861u, save.GameTime);
    }

    [Fact]
    public void TheInGameDateIsACalendarDateAtOrAfterFallout1sCampaignStart()
    {
        // ⚑ A SECOND VALUE FROM OUTSIDE THE FILE: Fallout 1's campaign opens on 5 December 2161
        // and the clock only runs forwards, so no save can predate it. The slot this was written
        // against read exactly 12 / 5 / 2161. A wrong offset lands in the map-name field or the
        // clock dword and produces values far outside these ranges.
        // ⚠ The FIELD ORDER (month before day) cannot be settled here — 12 and 5 name a real date
        // either way round. Fallout2SaveRetailTests carries the save that settles it.
        var slot = RequireSlot();
        var save = FalloutSaveFile.Parse(ReadSave(slot), "SAVE.DAT");

        Assert.InRange(save.GameMonth, 1, 12);
        Assert.InRange(save.GameDay, 1, 31);
        Assert.InRange(save.GameYear, 2161, 2300);

        // The staged slot has not left the vault, so it still stands on the campaign's opening day.
        Assert.Equal((12, 5, 2161), (save.GameMonth, save.GameDay, save.GameYear));
    }

    [Fact]
    public void TheCurrentMapNameResolvesToASidecarBesideTheSave()
    {
        // ⚑ The name at +0x73 must be one of the .SAV files actually present.
        var slot = RequireSlot();
        var save = FalloutSaveFile.Parse(ReadSave(slot), "SAVE.DAT");

        var sidecars = Directory.EnumerateFiles(slot, "*.SAV")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(save.MapName, sidecars);
    }

    [Fact]
    public void TheFixedWidthNameFieldsCarryNothingAfterTheirTerminator()
    {
        // ⚠⚠ THIS PINS AN OPEN QUESTION, not a decode. A fixed-width field is NUL-PADDED or
        // NUL-TERMINATED and the two want opposite code; for these three fields the retail bytes
        // CANNOT say which, because there is no authoring leftover after the terminator to
        // discriminate them — unlike FalloutMapFile's name field, where CAVES.MAP's leftovers
        // settle it. The reader takes the first NUL, which is correct under both readings while
        // this holds. ⚑ If this test ever fails on a new fixture, that fixture is the missing
        // discriminator and the question can finally be MEASURED rather than assumed.
        var slot = RequireSlot();
        var bytes = ReadSave(slot);

        var fields = new (string Name, int Offset, int Length, int Terminator)[]
        {
            ("player", FalloutSaveFile.PlayerNameOffset, FalloutSaveFile.PlayerNameLength, 3),
            ("save name", FalloutSaveFile.SaveNameOffset, FalloutSaveFile.SaveNameLength, 4),
            ("map name", FalloutSaveFile.MapNameOffset, FalloutSaveFile.MapNameLength, 10)
        };

        foreach (var (_, offset, length, terminator) in fields)
        {
            var field = bytes[offset..(offset + length)];
            Assert.Equal(terminator, Array.IndexOf(field, (byte)0));
            Assert.All(field[(terminator + 1)..], b => Assert.Equal(0, b));
        }
    }

    [Fact]
    public void TheSidecarsAreUncompressedVersion19MapsThatNameThemselves()
    {
        // ⚠⚠ Fallout 2 gzips its .SAV sidecars and Fallout 1 does NOT, which is why the reader
        // routes on the gzip magic rather than on the game. Each map then proves itself: version
        // 19 (Fallout 1's) and a +4 name field equal to the file it came from.
        var slot = RequireSlot();
        var sidecars = MapSidecars(slot);
        Assert.SkipWhen(sidecars.Count == 0, RealAssetPaths.SkipMessage("a Fallout 1 map sidecar"));

        foreach (var name in sidecars)
        {
            var raw = File.ReadAllBytes(Path.Combine(slot, name));
            Assert.False(raw[0] == 0x1F && raw[1] == 0x8B, $"{name} must not be gzipped");

            Assert.True(FalloutSaveFile.TryReadSidecarMap(raw, name, out var map, out var error), error);
            Assert.Equal(19u, map.Version);
            Assert.Equal(name, map.MapName, true);
            Assert.NotEmpty(map.Elevations);
        }
    }

    [Fact]
    public void TheAutomapSidecarIsRejectedAsAMap()
    {
        // ⚠ AUTOMAP.SAV carries the .SAV extension and is not a map in either game, so routing by
        // extension would hand the map reader the automap overlay. Its leading dword is no map
        // version, and that is what refuses it.
        var slot = RequireSlot();
        var path = Path.Combine(slot, "AUTOMAP.SAV");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("AUTOMAP.SAV"));

        var raw = File.ReadAllBytes(path);
        Assert.False(raw[0] == 0x1F && raw[1] == 0x8B);
        Assert.False(FalloutSaveFile.TryReadSidecarMap(raw, "AUTOMAP.SAV", out _, out var error));
        Assert.Contains("version", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePreviewThumbnailEndsWhereTheZeroRunBeginsAndCarriesAPicture()
    {
        // The field is 224 x 133 palette indices immediately after the 16-byte map-name field.
        // ⚠ What FIXES its length is the file: the picture is followed by exactly 131 NULs and
        // then one non-zero byte at 0x7566, immediately before the globals array. Shorten or
        // lengthen PreviewLength by a row and this fails — the last picture byte is not zero and
        // the run does not reach the array.
        // ⚠⚠ An earlier revision asserted `bytes.Length - Body.Length - Preview.Length == 0x83`,
        // which is arithmetic on two properties computed from the same two constants and could
        // never fail. It is replaced, not weakened.
        var slot = RequireSlot();
        var bytes = ReadSave(slot);
        var save = FalloutSaveFile.Parse(bytes, "SAVE.DAT");

        Assert.Equal(224 * 133, save.Preview.Length);

        var end = FalloutSaveFile.PreviewOffset + FalloutSaveFile.PreviewLength;
        Assert.Equal(0x74E3, end);

        // The FINAL row still carries data, so the field is not over-stated by a row. ⚠ Its very
        // last byte IS 0 — on this save the last 224 bytes are not picture but small signed 16-bit
        // pairs (see FalloutSaveFile) — so assert on the row, not on that one byte.
        Assert.Contains(bytes[(end - 224)..end], v => v != 0);

        // ⚠ Bounded by the file: a truncated or all-zero-tailed save must fail the assertion
        // below, not throw an IndexOutOfRangeException out of the measurement itself.
        var zeros = 0;
        while (end + zeros < bytes.Length && bytes[end + zeros] == 0)
        {
            zeros++;
        }

        Assert.Equal(131, zeros);
        Assert.Equal(FalloutSaveBody.GameVariablesOffset - 1, end + zeros);

        Assert.True(save.Preview.ToArray().Distinct().Count() > 64,
            "the preview field should carry an image, not a flat fill");
    }

    [Fact]
    public void TheGameVariableArrayEqualsVault13GamsInitialValues()
    {
        // ⚑⚑ THE ORACLE IS A DIFFERENT FILE ENTIRELY: VAULT13.GAM inside MASTER.DAT declares the
        // campaign globals, and the save stores them as a flat int32 array in that declaration
        // order. 618 declarations — the authors' own "// (617)" comment on the last one — and on a
        // save from before the player has left Vault 13 all 618 stored values still equal their
        // declared initial.
        // ⚠⚠ WHAT MAKES THIS TEST ABLE TO FAIL IS EXACTNESS, NOT THE SCORE. 586 of the 618
        // initials are ZERO and zero is endian-symmetric, so a near miss still scores high —
        // measured on this fixture, big-endian at 0x7563 / 0x7566 / 0x7568 / 0x756B scores
        // 571 / 566 / 586 / 572 of 618, and a LITTLE-endian read at the correct 0x7567 scores
        // 586 of 618. Only Assert.Empty on the whole mismatch list separates those from the 618
        // the right reading gives. ⛔ Do NOT restate this as "a wrong reading scores near zero"
        // (it was once written that way here): it does not, and that reasoning would license a
        // "most of them match" test that every near miss above would also pass.
        // ⚠ The gate is a FILESYSTEM fact, not a parse: a slot with more than one visited map has
        // been played on and its globals have moved, so it cannot serve as this oracle.
        var slot = RequireSlot();
        var sidecars = MapSidecars(slot);
        Assert.SkipUnless(sidecars.Count == 1, "this oracle needs a save from the starting map only");

        var root = InstallRoot(slot);
        Assert.SkipWhen(
            !File.Exists(Path.Combine(root, "MASTER.DAT")),
            RealAssetPaths.SkipMessage("the Fallout 1 install the slot sits in"));

        var master = Dat1Archive.Parse(Path.Combine(root, "MASTER.DAT"));
        using var backend = new Dat1Backend(master);
        var entry = backend.ListFiles()
            .FirstOrDefault(e => e.FullPath.Equals("DATA/VAULT13.GAM", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(entry is null, RealAssetPaths.SkipMessage("DATA/VAULT13.GAM"));

        var declared = FalloutGameVariables.Parse(
            Encoding.Latin1.GetString(backend.Extract(entry)), "VAULT13.GAM").Variables;
        Assert.Equal(Fallout1GlobalCount, declared.Count);

        Assert.True(
            FalloutSaveBody.TryReadGameVariables(
                ReadSave(slot), declared.Count, out var values, out _, out var error),
            error);

        var mismatched = new List<string>();
        for (var i = 0; i < declared.Count; i++)
        {
            if (values[i] != declared[i].Value)
            {
                mismatched.Add($"{i} {declared[i].Name}: .GAM {declared[i].Value}, save {values[i]}");
            }
        }

        Assert.Empty(mismatched);

        // The three distinctive initials, pinned so a table of zeros could not pass.
        Assert.Equal(9, values[2]);
        Assert.Equal(180, values[9]);
        Assert.Equal(150, values[10]);
    }

    [Fact]
    public void TheGameVariableArrayIsStoredTwiceWithTheVisitedMapListBetweenTheCopies()
    {
        // ⚠ A start-of-game save cannot prove the duplication on its own — everything still equals
        // its initial value, so "two copies" and "two arrays that happen to agree" look identical.
        // The control is the MID-GAME Fallout 2 save; see Fallout2SaveRetailTests. Here we pin the
        // STRUCTURE: a second copy exists, at a measured offset, with the map list and the automap
        // length between it and the first.
        // ⚠⚠ An earlier revision asserted that the block at copyOffset equalled the block at the
        // start — but copyOffset is produced by SEARCHING for a byte-identical copy, so that
        // assertion restated the production search and could never fail. It is gone.
        var slot = RequireSlot();
        var bytes = ReadSave(slot);

        Assert.True(
            FalloutSaveBody.TryReadGameVariables(
                bytes, Fallout1GlobalCount, out var values, out var copyOffset, out var error),
            error);
        Assert.Equal(0x7F23, copyOffset);
        Assert.Equal(618, values.Length);

        Assert.True(FalloutSaveBody.TryReadVisitedMaps(bytes, out _, out var mapListOffset));
        Assert.InRange(
            mapListOffset, FalloutSaveBody.GameVariablesOffset + values.Length * 4, copyOffset);
    }

    [Fact]
    public void TheDwordAfterTheMapListIsTheUncompressedAutomapLength()
    {
        // ⚑⚑ AN ORACLE FROM A DIFFERENT FILE. The u32 between the visited-map names and the second
        // copy of the globals equals the byte length of AUTOMAP.SAV — uncompressed. Fallout 1
        // stores its automap plain, so the number equals the file on disk; Fallout 2 gzips it, and
        // Fallout2SaveRetailTests checks the same field against the INFLATED size.
        // ⚠⚠ This lead was once published as REFUTED because Fallout 2's automap was measured
        // without inflating it. Two games now match exactly.
        var slot = RequireSlot();
        var automap = Path.Combine(slot, "AUTOMAP.SAV");
        Assert.SkipWhen(!File.Exists(automap), RealAssetPaths.SkipMessage("AUTOMAP.SAV"));

        var raw = File.ReadAllBytes(automap);
        Assert.False(raw[0] == 0x1F && raw[1] == 0x8B, "Fallout 1 stores its automap plain");

        Assert.True(FalloutSaveBody.TryReadAutomapLength(ReadSave(slot), out var length, out var offset));
        Assert.Equal(raw.Length, length);
        Assert.Equal(0x7F1F, offset);
    }

    [Fact]
    public void TheVisitedMapListNamesExactlyTheSavSidecarsBesideTheSave()
    {
        // ⚑ The list is located by its own contents — a count byte then that many NUL-terminated
        // .SAV names — and what it names is checked against the directory, which is outside the
        // file. AUTOMAP.SAV is not a visited map and must not appear.
        var slot = RequireSlot();

        Assert.True(FalloutSaveBody.TryReadVisitedMaps(ReadSave(slot), out var maps, out _));
        Assert.Equal(
            MapSidecars(slot),
            maps.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePlayerStatBlockIsLocatedAndCarriesACharactersAgeAndGender()
    {
        // ⚑ The block is located by CONTENT, because one save cannot fix an offset: eleven of
        // Fallout's published derivations have to hold at once, and on the slot this was written
        // against they did at exactly one of the 38,200 candidate byte offsets (38,339 bytes, a
        // 140-byte window, the locator's bound being at <= length - 140).
        // ⚠ State the margin honestly: the cheap pre-filter alone — seven consecutive int32 all
        // in 1..10 — already cuts those 38,200 to TWO here, so on this save the eleven separate
        // 1 from 2, not 1 from 38,200. Where they earn their keep is the Fallout 2 save, where 25
        // offsets pass the pre-filter and none passes the eleven.
        // ⚠ The assertions below deliberately AVOID the eleven constraints the locator itself
        // uses — restating them would only prove the locator agrees with itself. Slots 33 and 34
        // are outside that set: a block found by the formulas must also carry a legal character
        // age and gender, and nothing in the search made that true.
        var slot = RequireSlot();

        Assert.True(FalloutSaveBody.TryReadCharacter(ReadSave(slot), out var player));
        Assert.Equal(35, player.Stats.Count);
        Assert.Equal(0x8BF8, player.Offset);
        Assert.Equal(29, player.Age);
        Assert.Equal(0, player.Gender);
        Assert.Equal(40, player.PrimaryTotal);
        Assert.Equal(
            new[] { 7, 5, 6, 3, 8, 5, 6 },
            new[]
            {
                player.Strength, player.Perception, player.Endurance, player.Charisma,
                player.Intelligence, player.Agility, player.Luck
            });
    }
}
