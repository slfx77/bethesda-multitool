namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Locates retail game files for the opt-in real-asset suites (the ones behind
///     <c>BucketBTestGuard</c> / <c>RUN_BUCKET_B</c>).
///     Tests must not hardcode an install path: the drive letter and library folder differ per machine,
///     so a literal like <c>E:\SteamLibrary\...</c> resolves only on the machine it was written on. The
///     failure is silent — everywhere else the file simply does not exist, the test skips, and the run
///     reads as "no assets installed" rather than "this test can never run for you".
///     <para>
///         Probe order: the <c>BETHESDA_TEST_DATA_ROOT</c> override (first as a flat directory of
///         masters/archives, then as a mirrored Steam layout), then any repo-relative <c>Sample/</c>
///         candidates the caller offers, then every fixed drive's Steam library. Returns null when
///         nothing matches; callers pair that with <c>Assert.SkipWhen</c> so a missing install skips
///         rather than fails.
///     </para>
/// </summary>
internal static class RealAssetPaths
{
    /// <summary>Environment override pointing at a directory of retail assets.</summary>
    public const string RootVariable = "BETHESDA_TEST_DATA_ROOT";

    // Self-contained on purpose: this file is also linked into tools/EsmSchemaGen.Tests, which is
    // outside the solution and cannot see the rest of this project. Probing upward for
    // Directory.Build.props walks out of the nested bin/ output. Null (no root found) is not an
    // error — it just means the repo-relative candidates are skipped.
    private static readonly Lazy<string?> LazyRepoRoot = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    });

    private static string? RepoRoot => LazyRepoRoot.Value;

    /// <summary>Standard skip message naming the override, so a skipped run says how to enable it.</summary>
    public static string SkipMessage(string what)
    {
        return $"{what} not found. Install the game or set {RootVariable} to a directory containing it.";
    }

    /// <summary>
    ///     Resolve a file inside a Steam-installed game. <paramref name="gameFolder" /> is the folder
    ///     under <c>steamapps\common</c> (e.g. "Fallout New Vegas"); <paramref name="relativePath" /> is
    ///     the path beneath it (e.g. <c>Data\FalloutNV.esm</c>). Optional
    ///     <paramref name="repoRelativeCandidates" /> are checked against the repo root first-class, for
    ///     assets that also live under <c>Sample/</c>.
    /// </summary>
    public static string? SteamGameFile(
        string gameFolder, string relativePath, params string[] repoRelativeCandidates)
    {
        var steamRelative = Path.Combine(gameFolder, relativePath);

        var root = Environment.GetEnvironmentVariable(RootVariable);
        if (!string.IsNullOrEmpty(root))
        {
            // Flat first: the common case is one scratch folder holding the masters under test.
            var flat = Path.Combine(root, Path.GetFileName(relativePath));
            if (File.Exists(flat))
            {
                return flat;
            }

            var mirrored = Path.Combine(root, steamRelative);
            if (File.Exists(mirrored))
            {
                return mirrored;
            }
        }

        if (RepoRoot is { } repoRoot)
        {
            foreach (var candidate in repoRelativeCandidates)
            {
                var full = Path.Combine(repoRoot, candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        foreach (var library in SteamLibraryRoots())
        {
            var full = Path.Combine(library, steamRelative);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    /// <summary>
    ///     The New Vegas PC retail DVD's Steam installer manifest.
    ///     <para>
    ///         ⚠ It lives under <c>Sample\Media\…\disc\</c>, NOT in the build directory: the disc's
    ///         installer (<c>.sim</c>, the <c>.sid</c> payload parts, <c>Setup.exe</c>) is the
    ///         PACKAGE, and the build directory beside it holds only the 431 game files decrypted
    ///         out of it.
    ///     </para>
    ///     <para>
    ///         ⚑ The manifest is PLAINTEXT, so a listing needs no depot key — which is the whole
    ///         reason this fixture is usable in a test at all, since no key ships in this repository.
    ///     </para>
    /// </summary>
    public static string? SteamRetailDiscManifest()
    {
        return SampleFile(
            @"Media\Fallout - New Vegas (2010-9-16, Steam Disc - Final)\disc\Fallout- New Vegas_disk1.sim");
    }

    /// <summary>
    ///     Resolve a file under the repo's <c>Sample/</c> tree, or the same relative path under the
    ///     <c>BETHESDA_TEST_DATA_ROOT</c> override. For fixtures that are never a Steam install.
    /// </summary>
    public static string? SampleFile(string sampleRelativePath)
    {
        foreach (var candidate in SampleCandidates(sampleRelativePath))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Directory counterpart of <see cref="SampleFile" />.</summary>
    public static string? SampleDirectory(string sampleRelativePath)
    {
        foreach (var candidate in SampleCandidates(sampleRelativePath))
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> SampleCandidates(string sampleRelativePath)
    {
        // Each spelling is probed as given and then as SampleCorpus rewrites it, so a caller still
        // naming the pre-migration Full_Builds layout resolves into Sample/Builds.
        foreach (var relative in SampleCorpus.Candidates(sampleRelativePath))
        {
            var root = Environment.GetEnvironmentVariable(RootVariable);
            if (!string.IsNullOrEmpty(root))
            {
                yield return Path.Combine(root, relative);
                yield return Path.Combine(root, "Sample", relative);
            }

            if (RepoRoot is { } repoRoot)
            {
                yield return Path.Combine(repoRoot, "Sample", relative);
            }
        }
    }

    /// <summary>
    ///     Resolve a directory inside a Steam-installed game (a Data folder, say) using the same probe
    ///     order as <see cref="SteamGameFile" />.
    /// </summary>
    public static string? SteamGameDirectory(string gameFolder, string relativePath)
    {
        var steamRelative = Path.Combine(gameFolder, relativePath);

        var root = Environment.GetEnvironmentVariable(RootVariable);
        if (!string.IsNullOrEmpty(root))
        {
            var mirrored = Path.Combine(root, steamRelative);
            if (Directory.Exists(mirrored))
            {
                return mirrored;
            }
        }

        foreach (var library in SteamLibraryRoots())
        {
            var full = Path.Combine(library, steamRelative);
            if (Directory.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    // Every fixed drive, not just the one this repo happens to sit on: Steam libraries are routinely
    // placed on a separate data drive. Both casings appear in the wild — Steam writes "steamapps",
    // older installs and manual moves leave "SteamApps" — and the distinction matters on Linux.
    private static IEnumerable<string> SteamLibraryRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            var rootPath = drive.RootDirectory.FullName;
            yield return Path.Combine(rootPath, "SteamLibrary", "steamapps", "common");
            yield return Path.Combine(rootPath, "SteamLibrary", "SteamApps", "common");
            yield return Path.Combine(rootPath, "Steam", "steamapps", "common");
            yield return Path.Combine(rootPath, "Program Files (x86)", "Steam", "steamapps", "common");
        }
    }

    /// <summary>
    ///     The retail masters the opt-in suites load, each resolved through <see cref="SteamGameFile" />
    ///     so the override, the repo's <c>Sample/</c> tree, and every Steam library are all probed.
    ///     <para>
    ///         These exist because a dozen test files had each grown a private
    ///         <c>ResolveFalloutNvEsm()</c> / <c>ResolveOblivionEsm()</c> that re-implemented the same
    ///         probe by hand — and each copy stopped at a different point in the order, so which
    ///         installs a test could find depended on which copy it happened to inherit. Add a member
    ///         here rather than a private resolver in a test file.
    ///     </para>
    /// </summary>
    public static class Masters
    {
        /// <summary>
        ///     The <em>installed</em> FalloutNV master.
        ///     <para>
        ///         Deliberately does NOT fall back to <c>Sample\ESM\pc_final\FalloutNV.esm</c>. The two
        ///         are not interchangeable — measured 2026-08-21, the Sample copy is 245,650,747 bytes
        ///         (md5 <c>fd13cc17…</c>) while the installed master is 266,840,039 bytes (md5
        ///         <c>0ead5755…</c>). Parity suites must read the master the production build and the
        ///         game itself load; substituting the Sample copy changes which records exist and
        ///         turns real parity into false mismatches. (This is not hypothetical: adding that
        ///         fallback silently repointed the profile-parity suite and produced PACK/DIAL
        ///         divergences that had nothing to do with the code under test.)
        ///     </para>
        /// </summary>
        public static string? FalloutNv()
        {
            return SteamGameFile("Fallout New Vegas", @"Data\FalloutNV.esm");
        }

        /// <summary>The installed Fallout 3 master. Same no-Sample-fallback rule as <see cref="FalloutNv" />.</summary>
        public static string? Fallout3()
        {
            return SteamGameFile("Fallout 3 goty", @"Data\Fallout3.esm");
        }

        public static string? Oblivion()
        {
            return SteamGameFile("Oblivion", @"Data\Oblivion.esm");
        }

        public static string? Skyrim()
        {
            return SteamGameFile("Skyrim", @"Data\Skyrim.esm");
        }

        public static string? Fallout4()
        {
            return SteamGameFile("Fallout 4", @"Data\Fallout4.esm");
        }

        public static string? SeventySix()
        {
            return SteamGameFile("Fallout76", @"Data\SeventySix.esm");
        }

        public static string? Starfield()
        {
            return SteamGameFile("Starfield", @"Data\Starfield.esm");
        }

        public static string? Morrowind()
        {
            return SteamGameFile("Morrowind", @"Data Files\Morrowind.esm");
        }
    }

    /// <summary>
    ///     Data roots for the classic pre-Morrowind games. These have no master plugin to resolve —
    ///     their analyzable unit is a directory — so each member returns the data root a
    ///     <c>ClassicGameLocator</c> probe would claim, or null when the game is not installed.
    ///     <para>
    ///         Two installs here are MODDED (Fallout has the Hi-Res patch; Fallout 2 also has sfall
    ///         and the Killap patch), and the Daggerfall/Battlespire save directories ship empty, so
    ///         real-asset tests over these must assert structure, never content counts.
    ///     </para>
    /// </summary>
    public static class Classics
    {
        /// <summary>Arena's data root — game files and saves share one directory.</summary>
        public static string? Arena()
        {
            return SteamGameDirectory("The Elder Scrolls Arena", "ARENA");
        }

        /// <summary>
        ///     Daggerfall's data root. <c>DF\DFCD</c> beside it is a duplicate CD mirror — do not
        ///     use it. Note the Steam folder is the full title, not "Daggerfall": a shortened name
        ///     here resolves nowhere and every real-asset test silently skips.
        /// </summary>
        public static string? Daggerfall()
        {
            return SteamGameDirectory("The Elder Scrolls Daggerfall", @"DF\DAGGER\ARENA2");
        }

        public static string? Battlespire()
        {
            return SteamGameDirectory("An Elder Scrolls Legend Battlespire", "GAMEDATA");
        }

        public static string? Redguard()
        {
            return SteamGameDirectory("The Elder Scrolls Adventures Redguard", "Redguard");
        }

        /// <summary>
        ///     The 3dfx <c>fxart</c> directory — the 415 <c>TEXBSI.###</c> texture sets — from
        ///     Redguard Disc 1's extracted InstallShield cabinet. The Steam build ships only the
        ///     software renderer's <c>3dart</c>, so this resolves ONLY from the staged corpus.
        /// </summary>
        public static string? RedguardDisc1FxArt()
        {
            return SampleDirectory(
                @"Builds\The Elder Scrolls Adventures - Redguard (1998-7-24, PC - Final)\Disc 1 (Install)\extracted\fxart");
        }

        /// <summary>
        ///     The Redguard retail CD's second disc ("Play"), whose data track is the game's eleven
        ///     Smacker cutscenes (<c>Intro.smk</c>, <c>Scene*.smk</c>, <c>*_ruins*.smk</c>,
        ///     <c>WIN.SMK</c>) and nothing else of the game. The Steam install ships NO movies — they
        ///     live only on this disc — so the staged build is the only place these resolve from.
        /// </summary>
        public static string? RedguardDiscTwoMovies()
        {
            return SampleDirectory(
                @"Builds\The Elder Scrolls Adventures - Redguard (1998-7-24, PC - Final)\Disc 2 (Play)");
        }

        /// <summary>
        ///     The Battlespire retail CD's <c>videos</c> directory: six Smacker movies beside
        ///     <c>GAME.EXE</c> (NOT under <c>GAMEDATA</c>), which the Steam install does not ship.
        /// </summary>
        public static string? BattlespireCdMovies()
        {
            return SampleDirectory(
                @"Builds\An Elder Scrolls Legend - Battlespire (1997-11-13, PC - Final)\videos");
        }

        /// <summary>Fallout's install root: loose <c>DATA\</c> overrides the DAT archives.</summary>
        public static string? Fallout1()
        {
            return SteamGameDirectory("Fallout", string.Empty);
        }

        /// <summary>Fallout 2's install root: loose <c>data\</c> then f2_res, patch, critter, master.</summary>
        public static string? Fallout2()
        {
            return SteamGameDirectory("Fallout 2", string.Empty);
        }

        public static string? FalloutTactics()
        {
            return SteamGameDirectory("Fallout Tactics", "core");
        }

        /// <summary>
        ///     The cancelled Van Buren (Fallout 3, Dec 9 2003) prototype's install root: <c>data\</c>
        ///     holds the 24 <c>.grp</c> archives. Never a Steam install — staged under Sample/Builds.
        /// </summary>
        public static string? VanBuren()
        {
            return SampleDirectory(@"Builds\Van Buren (2003-12-9, PC - Prototype)");
        }

        /// <summary>
        ///     A Fallout 2 save slot — <c>SAVE.DAT</c> plus its gzip <c>.SAV</c> map sidecars.
        ///     <para>
        ///         ⚠⚠ A live Steam install's <c>SAVEGAME</c> is NOT a stable fixture — both games'
        ///         emptied themselves mid-session on 2026-09-07 (only <c>SAVEGAME</c>, which reads
        ///         as a cloud sync). The STAGED build under <c>Sample/Builds</c> is, so it is
        ///         probed second, exactly as <see cref="DaggerfallCdPackedDat" /> falls back. Do
        ///         not conclude from an empty install that the fixture is gone.
        ///     </para>
        /// </summary>
        public static string? Fallout2SaveSlot()
        {
            return FirstSaveSlot(
                "data",
                Fallout2(),
                SampleDirectory(@"Builds\Fallout 2 (2022-7-1, Steam - Final)"));
        }

        /// <summary>
        ///     A Fallout (1) save slot — <c>SAVE.DAT</c> plus its <c>.SAV</c> map sidecars, under the
        ///     install's <c>DATA\SAVEGAME</c> (upper-case, unlike Fallout 2's <c>data</c>).
        ///     <para>
        ///         ⚠⚠ Same fallback and same warning as <see cref="Fallout2SaveSlot" />: the staged
        ///         build carries the slot the retail tests were written against (SAVE.DAT 38,339 B,
        ///         md5 <c>87c43024d1b55b9af8594c9a1b5f41bd</c>), so those tests pin its identity.
        ///     </para>
        /// </summary>
        public static string? Fallout1SaveSlot()
        {
            return FirstSaveSlot(
                "DATA",
                Fallout1(),
                SampleDirectory(@"Builds\Fallout (2023-6-13, Steam - Final)"));
        }

        /// <summary>
        ///     First <c>SLOT*</c> holding a <c>SAVE.DAT</c> under <c>&lt;root&gt;/&lt;dataFolder&gt;/SAVEGAME</c>,
        ///     over the given install roots in order. Nulls and missing directories are skipped so
        ///     a machine with only one of the two roots still resolves.
        /// </summary>
        private static string? FirstSaveSlot(string dataFolder, params string?[] roots)
        {
            foreach (var root in roots)
            {
                if (root is null)
                {
                    continue;
                }

                var saves = Path.Combine(root, dataFolder, "SAVEGAME");
                if (!Directory.Exists(saves))
                {
                    continue;
                }

                var slot = Directory.EnumerateDirectories(saves, "SLOT*")
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "SAVE.DAT")));
                if (slot is not null)
                {
                    return slot;
                }
            }

            return null;
        }

        /// <summary>
        ///     A Daggerfall save slot — <c>SAVETREE.DAT</c> plus SAVEVARS.DAT, MAPSAVE.SAV, BIO.DAT,
        ///     RUMOR.DAT, IMAGE.RAW and SAVENAME.TXT. The six slots <c>SAVE0</c>..<c>SAVE5</c> sit
        ///     beside <c>ARENA2</c> (i.e. under <c>DF\DAGGER</c>), one level above what
        ///     <see cref="Daggerfall" /> returns.
        ///     <para>
        ///         ⚠ Depends on the user having PLAYED: all six slot directories exist on a fresh
        ///         install, but an unused one holds only Steam's autocloud stub, so the probe demands
        ///         SAVETREE.DAT and callers must skip rather than fail when no slot carries one.
        ///     </para>
        /// </summary>
        public static string? DaggerfallSaveSlot()
        {
            var arena2 = Daggerfall();
            if (arena2 is null)
            {
                return null;
            }

            var dagger = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(arena2));
            if (dagger is null)
            {
                return null;
            }

            return Enumerable.Range(0, 6)
                .Select(i => Path.Combine(dagger, $"SAVE{i}"))
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "SAVETREE.DAT")));
        }

        /// <summary>
        ///     A Battlespire save slot — <c>SAVETREE.DAT</c> plus SAVEVARS.DAT, SAVENAME.DAT and
        ///     IMAGE.RAW. The ten slots <c>SAVE0</c>..<c>SAVE9</c> sit in the INSTALL ROOT beside
        ///     <c>GAMEDATA</c>, one level above what <see cref="Battlespire" /> returns (there are
        ///     none under GAMEDATA).
        ///     <para>
        ///         ⚠ Depends on the user having PLAYED: like Daggerfall, every slot directory exists
        ///         but an unused one holds only Steam's autocloud stub, so the probe demands
        ///         SAVETREE.DAT and callers must skip rather than fail when no slot carries one.
        ///     </para>
        /// </summary>
        public static string? BattlespireSaveSlot()
        {
            var gameData = Battlespire();
            if (gameData is null)
            {
                return null;
            }

            var installRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(gameData));
            if (installRoot is null)
            {
                return null;
            }

            return Enumerable.Range(0, 10)
                .Select(i => Path.Combine(installRoot, $"SAVE{i}"))
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "SAVETREE.DAT")));
        }

        /// <summary>
        ///     A Fallout Tactics save file — the first <c>*.sav</c> under <c>core\user\save</c>
        ///     (one file per save, named by the player).
        ///     <para>
        ///         ⚠ Depends on the user having PLAYED: the directory and its files only exist once
        ///         a game has been saved, so callers must skip rather than fail when it is absent.
        ///     </para>
        /// </summary>
        public static string? FalloutTacticsSave()
        {
            var core = FalloutTactics();
            if (core is null)
            {
                return null;
            }

            var saves = Path.Combine(core, "user", "save");
            if (!Directory.Exists(saves))
            {
                return null;
            }

            return Directory.EnumerateFiles(saves, "*.sav")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        /// <summary>
        ///     The TES Arena v1.04 floppy release as it is staged: one directory per 1.44 MB disk
        ///     under <c>Sample/Builds/…/Disks</c>. Original media, so unlike the Steam probes above
        ///     it resolves only where the corpus is staged.
        /// </summary>
        public static string? ArenaFloppyDisks()
        {
            return SampleDirectory(@"Builds\The Elder Scrolls - Arena (PC Floppy - v1.04)\Disks");
        }

        /// <summary>
        ///     Disk 1's <c>ARENA.H1</c>, the anchor the installer archive is opened by. The volumes
        ///     live one per disk directory and the reader finds the rest itself.
        /// </summary>
        public static string? ArenaFloppyInstallerAnchor()
        {
            var disks = ArenaFloppyDisks();
            if (disks is null)
            {
                return null;
            }

            return Directory.EnumerateFiles(disks, "ARENA.H1", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        /// <summary>
        ///     The CD's <c>ARENA2\PACKED.DAT</c> — the container holding <c>ARCH3D.BSA</c> and
        ///     <c>DAGGER.SND</c>, which the disc carries in no other form.
        ///     <para>
        ///         Falls back to the Steam install's <c>DF\DFCD</c> mirror, which is the SAME press:
        ///         its PACKED.DAT is md5-identical (<c>fa841d68…</c>) to the staged disc's, so a
        ///         machine with only the Steam install still runs these tests.
        ///     </para>
        /// </summary>
        public static string? DaggerfallCdPackedDat()
        {
            var staged =
                SampleFile(@"Builds\The Elder Scrolls II - Daggerfall (1996-9-6, PC - Final)\DAGGER\ARENA2\PACKED.DAT");
            if (staged is not null)
            {
                return staged;
            }

            var mirror = SteamGameDirectory("The Elder Scrolls Daggerfall", @"DF\DFCD\DAGGER\ARENA2");
            if (mirror is null)
            {
                return null;
            }

            var path = Path.Combine(mirror, "PACKED.DAT");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    ///     Console-only fixtures, which are disc images rather than installs.
    /// </summary>
    public static class Consoles
    {
        /// <summary>
        ///     Fallout: Brotherhood of Steel (2004, PS2). The disc image is not an install, so the
        ///     Steam probes cannot find it: this checks the <c>BETHESDA_TEST_DATA_ROOT</c> override
        ///     and a repo-relative staging first, then the location the USER has supplied for it.
        ///     ⚠ That last literal is exactly what this file warns against elsewhere — it is here
        ///     because the image has no discoverable install, and it is a LAST resort behind two
        ///     portable probes, so another machine sets the override rather than editing code.
        /// </summary>
        public static string? BrotherhoodOfSteelIso()
        {
            const string fileName = "FALLOUTBOS.iso";

            var root = Environment.GetEnvironmentVariable(RootVariable);
            if (!string.IsNullOrEmpty(root))
            {
                var flat = Path.Combine(root, fileName);
                if (File.Exists(flat))
                {
                    return flat;
                }
            }

            if (RepoRoot is { } repoRoot)
            {
                var staged = Path.Combine(repoRoot, "Sample", "Media",
                    "Fallout - Brotherhood of Steel (2003-10-1, PS2 - Final)", fileName);
                if (File.Exists(staged))
                {
                    return staged;
                }
            }

            var supplied = Path.Combine(@"D:\PS2", "Fallout - Brotherhood of Steel", fileName);
            return File.Exists(supplied) ? supplied : null;
        }

        /// <summary>
        ///     The extracted tree of the Fallout: Brotherhood of Steel Xbox disc. Its 39 loose
        ///     <c>.bik</c> movies sit under <c>extracted\resx</c> and are the console half of the
        ///     Bink corpus (the Fallout Tactics half resolves through
        ///     <see cref="Classics.FalloutTactics" />).
        /// </summary>
        public static string? BrotherhoodOfSteelXboxExtracted()
        {
            return SampleDirectory(@"Builds\Fallout - Brotherhood of Steel (2004-1-13, Xbox - Final)\extracted");
        }

        /// <summary>
        ///     Fallout: Brotherhood of Steel (2004, Xbox). A trimmed XDVDFS image — NOT ISO9660,
        ///     though it carries a stub <c>CD001</c> descriptor — so it mounts through
        ///     <c>XdvdfsBackend</c>. Same probe order as <see cref="BrotherhoodOfSteelIso" />: the
        ///     <c>BETHESDA_TEST_DATA_ROOT</c> override, then the repo-relative staging, then the
        ///     pre-migration <c>Full_Builds\BOS_Xbox</c> spelling the image was first staged under.
        /// </summary>
        public static string? BrotherhoodOfSteelXboxIso()
        {
            const string fileName = "Fallout - Brotherhood of Steel (USA).iso";

            var root = Environment.GetEnvironmentVariable(RootVariable);
            if (!string.IsNullOrEmpty(root))
            {
                var flat = Path.Combine(root, fileName);
                if (File.Exists(flat))
                {
                    return flat;
                }
            }

            return SampleFile($@"Media\Fallout - Brotherhood of Steel (2004-1-13, Xbox - Final)\{fileName}")
                   ?? SampleFile($@"Full_Builds\BOS_Xbox\{fileName}");
        }

        /// <summary>
        ///     The <c>xbox_md5.json</c> manifest beside the extracted Xbox tree: entry path to
        ///     <c>[md5, size]</c> for all 491 files, produced when the disc was extracted. Tests pin
        ///     hashes against it rather than re-hashing the extracted copies, so a corrupted
        ///     extraction cannot make a round-trip agree with itself.
        /// </summary>
        public static string? BrotherhoodOfSteelXboxMd5Manifest()
        {
            return SampleFile(@"Builds\Fallout - Brotherhood of Steel (2004-1-13, Xbox - Final)\xbox_md5.json");
        }

        /// <summary>
        ///     Fallout: New Vegas (2010, Xbox 360) — a FULL redump dump, not a trimmed image, so its
        ///     game partition sits at the XGD2 base <c>0x0FD90000</c> rather than at 0. It is the
        ///     only image in this corpus that exercises a non-zero partition base, which is why the
        ///     XDVDFS tests reach for it as well as for the Brotherhood of Steel disc.
        ///     <para>
        ///         ⚠ Do NOT substitute one of the extracted X360 build trees under
        ///         <c>Sample\Builds\</c>: the point of this fixture is the container, and an
        ///         extracted tree has none.
        ///     </para>
        /// </summary>
        public static string? FalloutNewVegasX360Iso()
        {
            return SampleFile(
                @"Media\Fallout - New Vegas (2010-10-19, X360 - Final)\Fallout - New Vegas (USA, Europe).iso");
        }
    }

    /// <summary>
    ///     Fixtures for The Elder Scrolls Travels (mobile) titles. None is a Steam install: the
    ///     three J2ME games are single JARs, Shadowkey is an unpacked Symbian tree, and the
    ///     cancelled PSP Oblivion is a set of extracted UMD trees.
    ///     <para>
    ///         ⚠ A JAR or release ZIP is original MEDIA, so it lives under <c>Sample/Media/</c>;
    ///         the tree unpacked from it lives under <c>Sample/Builds/</c>. The two are different
    ///         paths for the same title — pick by whether the caller wants the package or its
    ///         contents.
    ///     </para>
    ///     <para>
    ///         A JAR is its own install root: <c>ClassicGameLocator.DetectFromArchive</c> claims it
    ///         from the entry names, so tests point the analyzer at the JAR directly and never
    ///         unpack it. Shadowkey's root is the <c>system\apps\6R51</c> application directory.
    ///     </para>
    /// </summary>
    public static class Travels
    {
        /// <summary>The Stormhold 176x208 English JAR (byte-identical to the v1.0.10 release JAR).</summary>
        public static string? StormholdJar()
        {
            return SampleFile(
                @"Media\The Elder Scrolls Travels - Stormhold (J2ME - Variants)\test_stormhold_176x208_eng.jar");
        }

        /// <summary>The second, slightly larger v1.0.10 "(a)" Stormhold JAR — the same game, another build.</summary>
        public static string? StormholdAlternateJar()
        {
            return SampleFile(
                @"Media\The Elder Scrolls Travels - Stormhold (J2ME - Variants)\The Elder Scrolls (2003)(Vir2L Studios)(v1.0.10)(a).jar");
        }

        public static string? DawnstarJar()
        {
            return SampleFile(
                @"Media\The Elder Scrolls Travels - Dawnstar (J2ME - Variants)\test_dawnstar_176x208_eng.jar");
        }

        public static string? OblivionMobileJar()
        {
            return SampleFile(@"Media\The Elder Scrolls Travels - Oblivion (J2ME - Final)\oblivion-repaired.jar");
        }

        /// <summary>The Shadowkey application directory (holds 6R51.APP, the 21 zones and the packs).</summary>
        public static string? ShadowkeyRoot()
        {
            return SampleDirectory(
                @"Builds\The Elder Scrolls Travels - Shadowkey (N-Gage - Final)\The Elder Scrolls Travels - Shadowkey\system\apps\6R51");
        }

        /// <summary>The Shadowkey N-Gage release zip as shipped (the archive-level fixture).</summary>
        public static string? ShadowkeyZip()
        {
            return SampleFile(
                @"Media\The Elder Scrolls Travels - Shadowkey (N-Gage - Final)\The-Elder-Scrolls-Travels-Shadowkey_N-Gage_EN.zip");
        }

        /// <summary>The directory the six dated PSP beta ISOs were extracted into (one subdirectory per build).</summary>
        public static string? OblivionPspBuildsRoot()
        {
            return SampleDirectory(@"Builds\The Elder Scrolls IV - Oblivion (PSP - Prototypes)");
        }
    }
}