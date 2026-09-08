namespace SampleGenerator;

/// <summary>
///     The corpus: every build <c>Sample/Builds</c> holds, where its bytes come from, and how its
///     date was established.
///     <para>
///         <b>Naming.</b> <c>Game Name (yyyy-M-d, Platform - Kind)</c>, matching the sibling
///         NeversoftMultitool corpus so the two read alike. A build with no defensible date drops
///         the date and reads <c>Game Name (Platform - Kind)</c> — the convention that repo already
///         uses for undated cartridge and mobile variants.
///     </para>
///     <para>
///         <b>Dates are measured, not assumed.</b> Every date below was read off the media on
///         2026-09-07: ISO 9660 volume descriptors for the discs, COFF header timestamps for the
///         directory builds, the leak's own labels for the prototypes. Where a measurement was
///         unusable the entry says so and falls back to the documented release date — two do.
///         ⚠ Morrowind's executable stamps <c>2030-10-02</c> and the Brotherhood of Steel Xbox
///         disc's XDVDFS descriptor reads <c>1601-01-03</c>; both are junk, and taking either at
///         face value would have put a fictional date in a directory name.
///     </para>
/// </summary>
internal static class SampleGeneratorCatalog
{
    /// <summary>
    ///     Composes a build directory name. The single place the convention lives: changing this
    ///     renames the entire corpus consistently on the next run.
    /// </summary>
    internal static string DirectoryName(CatalogEntry entry)
    {
        var qualifier = entry.Date is null
            ? $"{entry.Platform} - {entry.Kind}"
            : $"{entry.Date}, {entry.Platform} - {entry.Kind}";
        return SampleGeneratorPathSafety.SanitizePathSegment($"{entry.Game} ({qualifier})");
    }

    /// <summary>
    ///     Every build in the corpus.
    ///     <para>
    ///         PC titles after Skyrim Special Edition are deliberately absent — see
    ///         <see cref="Excluded" />. Console and prototype builds are in scope regardless of era,
    ///         because nothing else in the collection holds them.
    ///     </para>
    /// </summary>
    internal static readonly CatalogEntry[] Builds =
    [
        // ------------------------------------------------------------------
        // The Elder Scrolls — pre-Morrowind
        // ------------------------------------------------------------------

        // The v1.04 floppy release: eight FAT12 1.44M images. No date is claimed — the images'
        // own FAT timestamps are the 2026 repack's, and the release month is not settled. The
        // version is what distinguishes this from the CD, so it rides in the platform token.
        new("The Elder Scrolls - Arena", null, "PC Floppy", "v1.04",
            [new SourcePart(SourceKind.StagedTree, @"Full_Builds\Arena_Floppy_v1.04")],
            Notes: "Eight 1.44M FAT12 images. The installer container is a split 96-byte directory " +
                   "plus one LZHUF block stream; this A.EXE is a second exe build with the same " +
                   "string pools at shifted offsets.",
            Media:
            [
                new MediaItem(".", MediaExtraction.Package, "Disks"),
            ]),

        // The v1.07 CD. ⚑ Its Arena data is byte-identical to the current Steam root, which
        // Steam's 2026-08-31 depot update populated from this disc — so the Steam install is NOT
        // a separate entry. The disc is kept because it additionally carries two non-Arena demos.
        new("The Elder Scrolls - Arena", "1994-10-18", "PC", "Final",
            [new SourcePart(SourceKind.StagedTree, @"Full_Builds\Arena_Disc")],
            DateProvenance.VolumeDescriptor,
            Notes: "PVD creation record 1994-10-18 16:04:06, volume 'ARENA_CD'. The Arena data is " +
                   "byte-identical to the Steam install, which is why no separate Steam entry exists.",
            Media:
            [
                new MediaItem(".", MediaExtraction.RawDisc, "."),
            ]),

        // The 1.0 press (Build #165). ARCH3D.BSA and DAGGER.SND exist only imploded inside
        // PACKED.DAT on this disc, which is why it is kept beside the patched Steam install.
        new("The Elder Scrolls II - Daggerfall", "1996-9-6", "PC", "Final",
            [new SourcePart(SourceKind.StagedTree, @"Full_Builds\Daggerfall_Disc")],
            DateProvenance.VolumeDescriptor,
            Notes: "PVD creation record 1996-09-06 01:03:28, volume 'Daggerfall'. Build #165, the " +
                   "retail 1.0 press; ARCH3D.BSA and DAGGER.SND ship imploded inside PACKED.DAT.",
            Media:
            [
                new MediaItem(".", MediaExtraction.RawDisc, "."),
            ]),

        // The patched Steam build (v1.07.213). Undated: it is DOS-era, so there is no COFF
        // timestamp to read, and the patch level is not a date.
        new("The Elder Scrolls II - Daggerfall", "2026-8-31", "Steam", "Final",
            [new SourcePart(SourceKind.SteamGame, "The Elder Scrolls Daggerfall")],
            DateProvenance.SteamDepot,
            Notes: "Patched Steam build (v1.07.213), distinct from the 1.0 disc. Steam depot buildid " +
                   "8808522, repackaged 2026-08-31.",
            SteamAppId: "1812390"),

        new("An Elder Scrolls Legend - Battlespire", "1997-11-13", "PC", "Final",
            [
                new SourcePart(SourceKind.StagedTree, @"Full_Builds\Battlespire_Disc"),
            ],
            DateProvenance.VolumeDescriptor,
            Notes: "PVD creation record 1997-11-13 16:13:17, volume 'Battlespire'. Track 1 is the " +
                   "data track; tracks 2-9 are Redbook audio.",
            Media:
            [
                new MediaItem(".", MediaExtraction.RawDisc, "."),
            ]),

        new("An Elder Scrolls Legend - Battlespire", "2026-8-31", "Steam", "Final",
            [new SourcePart(SourceKind.SteamGame, "An Elder Scrolls Legend Battlespire")],
            DateProvenance.SteamDepot,
            Notes: "Steam depot buildid 8808329, repackaged 2026-08-31. Ships its own raw-sector " +
                   "Battlespire.bin CD image that DOSBox mounts — build content, NOT media.",
            SteamAppId: "1812420"),

        // Both discs under one build: Disc 1 is the install tree (its DATA1.CAB holds the fxart
        // TEXBSI 3dfx art the Steam release omits), Disc 2 the movies and seven audio tracks.
        new("The Elder Scrolls Adventures - Redguard", "1998-7-24", "PC", "Final",
            [
                new SourcePart(SourceKind.StagedTree, @"Full_Builds\Redguard_Disc1", "Disc 1 (Install)"),
                new SourcePart(SourceKind.StagedTree, @"Full_Builds\Redguard_Disc1_iso", @"Disc 1 (Install)\iso"),
                new SourcePart(SourceKind.StagedTree, @"Full_Builds\Redguard_Disc1_extracted", @"Disc 1 (Install)\extracted"),
                new SourcePart(SourceKind.StagedTree, @"Full_Builds\Redguard_Disc2", "Disc 2 (Play)"),
            ],
            DateProvenance.VolumeDescriptor,
            Notes: "PVD creation records: Disc 1 1998-07-24 16:43:19, Disc 2 1998-07-24 15:59:58, " +
                   "both volume 'Redguard'. Disc 1's DATA1.CAB is the whole install (1,664 files, " +
                   "481 MB) including the fxart TEXBSI 3dfx art the Steam release omits.",
            Media:
            [
                // Disc 1 was extracted previously into its own "extracted" subdirectory, so the
                // image is kept rather than expanded a second time.
                new MediaItem("Disc 1 (Install)", MediaExtraction.KeepPacked),
                new MediaItem("Disc 2 (Play)", MediaExtraction.RawDisc, "Disc 2 (Play)"),
                // The install cabinet holds the game and is expanded in place; it stays in
                // the disc tree because that is where it shipped.
                new MediaItem(@"Disc 1 (Install)\iso\DATA1.CAB", MediaExtraction.ExpandInPlace,
                    @"Disc 1 (Install)\extracted"),
            ]),

        new("The Elder Scrolls Adventures - Redguard", "2026-8-31", "Steam", "Final",
            [new SourcePart(SourceKind.SteamGame, "The Elder Scrolls Adventures Redguard")],
            DateProvenance.SteamDepot,
            Notes: "Steam depot buildid 8762689, repackaged 2026-08-31. Omits the 3dfx fxart the " +
                   "install disc carries. Ships its own raw-sector REDGUARD.bin CD image that " +
                   "DOSBox mounts — build content, NOT media.",
            SteamAppId: "1812410"),

        // ------------------------------------------------------------------
        // The Elder Scrolls — Morrowind onward (PC, through Skyrim SE)
        // ------------------------------------------------------------------

        // ⚠ Morrowind.exe's COFF timestamp reads 2030-10-02, which is impossible for a 2002 title
        // and cannot be used. The documented release date stands in; the manifest records the
        // measured stamp so the discrepancy stays visible.
        new("The Elder Scrolls III - Morrowind", "2026-6-15", "Steam", "Final",
            [new SourcePart(SourceKind.SteamGame, "Morrowind")],
            DateProvenance.SteamDepot, "Morrowind.exe",
            Notes: "Steam depot buildid 1510067, last updated 2026-06-15. ⚠ Morrowind.exe's COFF " +
                   "timestamp reads 2030-10-02 — impossible, and not used for anything.",
            SteamAppId: "22320"),

        new("The Elder Scrolls IV - Oblivion", "2022-6-18", "Steam", "Final",
            [new SourcePart(SourceKind.SteamGame, "Oblivion")],
            DateProvenance.SteamDepot, "Oblivion.exe",
            Notes: "Steam depot buildid 1510065, last updated 2022-06-18. Oblivion.exe itself stamps " +
                   "2007-04-16 (patch 1.2). ⚠ MODDED: OBSE is installed.",
            SteamAppId: "22330"),

        new("The Elder Scrolls V - Skyrim", "2026-6-16", "Steam", "Final",
            [new SourcePart(SourceKind.SteamGame, "Skyrim")],
            DateProvenance.SteamDepot, "TESV.exe",
            Notes: "Steam depot buildid 15039560, last updated 2026-06-16. TESV.exe itself stamps " +
                   "2013-03-15 (patch 1.9). Carries the official HighResTexturePack ESPs.",
            SteamAppId: "72850"),

        new("The Elder Scrolls V - Skyrim Special Edition", "2026-8-31", "Steam", "Final",
            [new SourcePart(SourceKind.SteamGame, "Skyrim Special Edition")],
            DateProvenance.SteamDepot, "SkyrimSE.exe",
            Notes: "Steam depot buildid 24914197, last updated 2026-08-31 — patched this year. " +
                   "SkyrimSE.exe itself stamps 2026-08-24. The newest PC title in the corpus; " +
                   "everything after it is excluded (see Excluded). ⚠ MODDED: SKSE64 is installed.",
            SteamAppId: "489830"),

        // ------------------------------------------------------------------
        // The Elder Scrolls Travels — mobile and handheld
        // ------------------------------------------------------------------

        // Two byte-distinct v1.0.10 JARs plus the 176x208 English test JAR, kept together the way
        // the sibling corpus keeps its "(J2ME - Variants)" sets. J2ME is BIG-endian.
        new("The Elder Scrolls Travels - Stormhold", null, "J2ME", "Variants",
            [
                new SourcePart(SourceKind.StagedTree, @"Full_Builds\Stormhold (J2ME)"),
                new SourcePart(SourceKind.StagedFile, @"Full_Builds\test_stormhold_176x208_eng.jar"),
                new SourcePart(SourceKind.StagedFile, @"Full_Builds\The-Elder-Scrolls-Travels-Stormhold_J2ME_EN_v1010.zip"),
            ],
            Notes: "A J2ME title IS its JAR — the analyzer mounts it directly. BIG-endian. " +
                   "test_stormhold_176x208_eng.jar is byte-identical to the v1.0.10 release JAR.",
            Media:
            [
                new MediaItem("The Elder Scrolls (2003)(Vir2L Studios)(v1.0.10).jar", MediaExtraction.Package, "v1.0.10"),
                new MediaItem("The Elder Scrolls (2003)(Vir2L Studios)(v1.0.10)(a).jar", MediaExtraction.Package, "v1.0.10 (a)"),
                new MediaItem("test_stormhold_176x208_eng.jar", MediaExtraction.Package, "176x208 English"),
                new MediaItem("The-Elder-Scrolls-Travels-Stormhold_J2ME_EN_v1010.zip", MediaExtraction.KeepPacked),
            ]),

        new("The Elder Scrolls Travels - Dawnstar", null, "J2ME", "Variants",
            [new SourcePart(SourceKind.StagedFile, @"Full_Builds\test_dawnstar_176x208_eng.jar")],
            Notes: "BIG-endian J2ME. Tables live in datfiles.lmp, 43 PNG in imgfiles.lmp.",
            Media:
            [
                new MediaItem("test_dawnstar_176x208_eng.jar", MediaExtraction.Package, "176x208 English"),
            ]),

        // ⚠⚠ Shadowkey is the endianness exception in this block: N-Gage/Symbian ARM, LITTLE-endian.
        new("The Elder Scrolls Travels - Shadowkey", null, "N-Gage", "Final",
            [
                new SourcePart(SourceKind.StagedTree, @"Full_Builds\Shadowkey (N-Gage)"),
                new SourcePart(SourceKind.StagedFile, @"Full_Builds\The-Elder-Scrolls-Travels-Shadowkey_N-Gage_EN.zip"),
            ],
            Notes: "⚠⚠ LITTLE-endian, unlike the three J2ME titles. The application root is " +
                   "system\\apps\\6R51; 21 zones x 12 per-zone formats.",
            Media:
            [
                new MediaItem("The-Elder-Scrolls-Travels-Shadowkey_N-Gage_EN.zip", MediaExtraction.KeepPacked),
            ]),

        new("The Elder Scrolls Travels - Oblivion", null, "J2ME", "Final",
            [new SourcePart(SourceKind.StagedFile, @"Full_Builds\oblivion-repaired.jar")],
            Notes: "BIG-endian J2ME. .jtm/.cml/.scr plus lang_N.txt.",
            Media:
            [
                new MediaItem("oblivion-repaired.jar", MediaExtraction.Package, "Unpacked"),
            ]),

        // The cancelled PSP port: seven dated UMD builds kept as one set, matching how the tooling
        // reads them (one root, one subdirectory per build) and how the sibling corpus groups
        // multi-build mobile titles.
        new("The Elder Scrolls IV - Oblivion", null, "PSP", "Prototypes",
            [new SourcePart(SourceKind.StagedTree, @"Full_Builds\Oblivion PSP (cancelled betas)")],
            Notes: "Cancelled. Seven dated UMD builds (2006-06-09, 2006-11-21, 2007-01-11, " +
                   "2007-01-31, 2007-02-01, 2007-04-27, plus a modified 2007-02-01), one GR.ARC " +
                   "pack each. ⚠ The untagged June 2006 revision stores payload offsets RELATIVE " +
                   "to the data area.",
            Media:
            [
                new MediaItem("_zip", MediaExtraction.KeepPacked, Recursive: true),
            ]),

        // ------------------------------------------------------------------
        // Fallout — classic isometric
        // ------------------------------------------------------------------

        // ⚠ Both Fallout installs are MODDED (Hi-Res patch; Fallout 2 also sfall + Killap UP), so
        // real-asset tests over them assert structure only, never content counts.
        new("Fallout", "2023-6-13", "Steam", "Final",
            [new SourcePart(SourceKind.SteamGame, "Fallout")],
            DateProvenance.SteamDepot, "FALLOUTW.EXE",
            Notes: "Steam depot buildid 300289, last updated 2023-06-13. FALLOUTW.EXE itself stamps " +
                   "1997-11-11. ⚠ The Steam release SHIPS sfall's ddraw.dll — proven by a fresh " +
                   "redownload on 2026-09-07 still carrying it. Not a user mod, but not the CD " +
                   "release either; loose DATA overrides the DATs.",
            ShippedExtras: ["DirectDraw wrapper"],
            SteamAppId: "38400"),

        new("Fallout 2", "2022-7-1", "Steam", "Final",
            [new SourcePart(SourceKind.SteamGame, "Fallout 2")],
            DateProvenance.SteamDepot, "FALLOUT2.EXE",
            Notes: "Steam depot buildid 303136, last updated 2022-07-01. FALLOUT2.EXE itself stamps " +
                   "1998-12-12. ⚠ The Steam release SHIPS sfall (ddraw.dll + sfall-readme.txt) — " +
                   "proven by a fresh redownload on 2026-09-07 still carrying both. Precedence: " +
                   "loose > f2_res > patch* > critter > master.",
            ShippedExtras: ["DirectDraw wrapper", "sfall"],
            SteamAppId: "38410"),

        new("Fallout Tactics - Brotherhood of Steel", "2023-6-13", "Steam", "Final",
            [new SourcePart(SourceKind.SteamGame, "Fallout Tactics")],
            DateProvenance.SteamDepot, "BOS.exe",
            Notes: "Steam depot buildid 304255, last updated 2023-06-13. BOS.exe itself stamps " +
                   "2001-06-01. 40 .bos archives, all plain PKZIP.",
            SteamAppId: "38420"),

        // ------------------------------------------------------------------
        // Fallout — the original Interplay CD releases
        // ------------------------------------------------------------------
        //
        // These are the true vanilla reference for the isometric Fallouts. The Steam re-releases
        // are NOT: a fresh redownload on 2026-09-07 still shipped sfall's ddraw.dll (and Fallout 2
        // its sfall-readme.txt), so that wrapper is part of what Steam distributes, not something
        // the user installed. Anything measured against a Steam copy is measured against sfall.

        new("Fallout", "1997-9-30", "PC", "Final",
            [new SourcePart(SourceKind.MediaArchive, "Fallout (USA).zip")],
            DateProvenance.VolumeDescriptor,
            Notes: "Original Interplay CD. PVD creation record 1997-09-30 17:53:34, volume " +
                   "'FALLOUT'. Redump MODE1/2352.",
            Media: [new MediaItem(".", MediaExtraction.RawDisc, ".")]),

        new("Fallout 2", "1998-10-22", "PC", "Final",
            [new SourcePart(SourceKind.MediaArchive, "Fallout 2 (USA).7z")],
            DateProvenance.VolumeDescriptor,
            Notes: "Original Interplay CD. PVD creation record 1998-10-22 03:40:32, volume " +
                   "'FALLOUT2'. Redump MODE1/2352.",
            Media: [new MediaItem(".", MediaExtraction.RawDisc, ".")]),

        // Three discs under one build. ⚠ Disc 3 is MODE2/2352 while discs 1 and 2 are
        // MODE1/2352, so its ISO 9660 descriptor sits at sector offset +24 rather than +16 — a
        // reader that assumes MODE1 finds no CD001 on it at all and reports the disc as unreadable.
        new("Fallout Tactics - Brotherhood of Steel", "2001-3-7", "PC", "Final",
            [
                new SourcePart(SourceKind.MediaArchive,
                    "Fallout Tactics - Brotherhood of Steel (USA) (Disc 1).7z", "Disc 1"),
                new SourcePart(SourceKind.MediaArchive,
                    "Fallout Tactics - Brotherhood of Steel (USA) (Disc 2).7z", "Disc 2"),
                new SourcePart(SourceKind.MediaArchive,
                    "Fallout Tactics - Brotherhood of Steel (USA) (Disc 3).7z", "Disc 3"),
            ],
            DateProvenance.VolumeDescriptor,
            Notes: "Original Interplay 3-CD release. PVD creation records: Disc 1 2001-03-07 " +
                   "16:05:01 ('FOT_1'), Disc 2 2001-03-07 15:45:23 ('FOT_2'), Disc 3 2001-03-08 " +
                   "04:55:00 ('FOT_3'). Disc 3 is MODE2/2352; the other two are MODE1/2352.",
            Media:
            [
                new MediaItem("Disc 1", MediaExtraction.RawDisc, "Disc 1"),
                new MediaItem("Disc 2", MediaExtraction.RawDisc, "Disc 2"),
                new MediaItem("Disc 3", MediaExtraction.RawDisc, "Disc 3"),
            ]),

        // ------------------------------------------------------------------
        // Fallout — console and cancelled
        // ------------------------------------------------------------------

        new("Fallout - Brotherhood of Steel", "2003-10-1", "PS2", "Final",
            [new SourcePart(SourceKind.MediaFile, "FALLOUTBOS.iso")],
            DateProvenance.VolumeDescriptor,
            Notes: "PVD creation record 2003-10-01 19:15:00, volume 'FALLOUTBOS'. 352 files; the " +
                   ".CLP container is solved (magic 'CLMP' read little-endian).",
            Media:
            [
                new MediaItem("FALLOUTBOS.iso", MediaExtraction.Package, "."),
            ]),

        // ⚠ The Xbox disc's XDVDFS volume descriptor timestamp reads 1601-01-03, i.e. a near-zero
        // FILETIME, so it carries no date. The documented US release date stands in.
        new("Fallout - Brotherhood of Steel", "2004-1-13", "Xbox", "Final",
            [new SourcePart(SourceKind.StagedTree, @"Full_Builds\BOS_Xbox")],
            DateProvenance.ReleaseDate,
            Notes: "US release date, NOT a measurement: the XDVDFS volume descriptor's FILETIME " +
                   "reads 1601-01-03, a near-zero value carrying no date. Holds the disc image and " +
                   "the tree extracted from it.",
            Media:
            [
                new MediaItem("Fallout - Brotherhood of Steel (USA).iso", MediaExtraction.KeepPacked),
            ]),

        new("Van Buren", "2003-12-9", "PC", "Prototype",
            [
                new SourcePart(SourceKind.StagedTree, @"Full_Builds\Van Buren (Dec 9 2003)"),
                new SourcePart(SourceKind.StagedFile, @"Full_Builds\Fallout 3 (Dec 9, 2003 prototype).zip"),
            ],
            DateProvenance.BuildLabel,
            Notes: "The cancelled Black Isle Fallout 3 tech demo, dated by the leak's own label. " +
                   "Shipped publicly as 'Fallout 3 (Dec 9, 2003 prototype)'.",
            Media:
            [
                new MediaItem("Fallout 3 (Dec 9, 2003 prototype).zip", MediaExtraction.KeepPacked),
            ]),

        // ------------------------------------------------------------------
        // Fallout 3 / New Vegas
        // ------------------------------------------------------------------

        // The staged PC Final tree and the Steam "Fallout 3 goty" install stamp identically
        // (2021-05-21 19:47:34), i.e. they are the same 2021 Steam repack — one entry, sourced
        // from the staged copy so the corpus does not depend on Steam staying installed.
        // Verified byte-identical to the installed Steam copy on 2026-09-07 (Fallout3.exe,
        // Fallout3.esm and Anchorage - Main.bsa all match), so it is sourced from Steam directly
        // rather than from the older staged tree.
        new("Fallout 3", "2026-2-15", "Steam", "Final",
            [new SourcePart(SourceKind.SteamGame, "Fallout 3 goty")],
            DateProvenance.SteamDepot, "Fallout3.exe",
            Notes: "Steam depot buildid 14365463, last updated 2026-02-15. Fallout3.exe itself " +
                   "stamps 2021-05-21 (the 2021 repack).",
            LegacyNames: ["Fallout 3 (PC Final)"],
            SteamAppId: "22370"),

        // Retail PC DVD, distinct from the patched install below.
        // ⚠⚠ NOT a standalone PC release. The disc ships SteamService.exe, an
        // SteamInstall_English.msi and a .sis naming appID 22380 and its depots: it is a Steam
        // retail box whose payload is ENCRYPTED Steam depot data that only Steam can install.
        new("Fallout - New Vegas", "2010-9-16", "Steam Disc", "Final",
            [new SourcePart(SourceKind.MediaFile, "FalloutNewVegas.iso")],
            DateProvenance.VolumeDescriptor,
            Notes: "Steam retail DVD. PVD creation record 2010-09-16 09:36:00, volume " +
                   "'FALLOUTNEWVEGAS' — the mastering date, a month before release. ⚠ The five " +
                   ".sid files (6.38 GB) are ENCRYPTED Steam depot payload: 7.997 bits/byte, all " +
                   "256 byte values, at every offset sampled. They cannot be opened without " +
                   "Steam's depot key, so this build carries no game tree. ⛑ What IS readable " +
                   "is the .sim manifest — 431 file names and 45 directories, the complete 2010 " +
                   "retail depot listing (FalloutNV.esm, the BSAs, FalloutNV.exe) — and the .sis " +
                   "install script (appID 22380).",
            Media:
            [
                new MediaItem("FalloutNewVegas.iso", MediaExtraction.Package, "."),
            ]),

        // The staged PC Final tree and the Steam install stamp identically (2011-07-01 04:45:33),
        // i.e. both are patch 1.4 — one entry, sourced from the staged copy.
        // ⚠⚠ RE-SOURCED FROM STEAM 2026-09-07. The staged copy this replaced was MODDED:
        // eight xNVSE files and a large-address-aware FalloutNV.exe (characteristics 0x0122 against
        // the vanilla 0x0102). Its master ESM and BSAs were already identical to Steam's, so only
        // the executable and the extender differed — which is exactly the kind of difference that
        // silently invalidates a parity measurement.
        new("Fallout - New Vegas", "2022-5-24", "Steam", "Final",
            [new SourcePart(SourceKind.SteamGame, "Fallout New Vegas")],
            DateProvenance.SteamDepot, "FalloutNV.exe",
            Notes: "Steam depot buildid 1510068, last updated 2022-05-24 (app 22380). " +
                   "FalloutNV.exe itself stamps 2011-07-01 (patch 1.4). Vanilla: no script " +
                   "extender, LAA bit clear. ⚠⚠ This build was briefly dated 2026-1-19 / buildid " +
                   "52174 — that is app 22480, the GECK, which installs into the SAME directory. " +
                   "Match an appmanifest by appid, never by installdir.",
            LegacyNames: ["Fallout New Vegas (PC Final)"],
            SteamAppId: "22380"),

        // The retail 360 disc plus the tree already extracted from it. The XGD2 image is 7.84 GB
        // and this repository has no XDVDFS reader, so the image is mirrored, not expanded.
        new("Fallout - New Vegas", "2010-10-19", "X360", "Final",
            [
                // The extracted tree sits at the build root, the way every other extracted build
                // does, so Data\ is one level down rather than two; the disc image rides beside it.
                new SourcePart(SourceKind.StagedTree, @"Full_Builds\Fallout New Vegas (360 Final)"),
                new SourcePart(SourceKind.MediaArchive, "Fallout - New Vegas (USA, Europe).7z", "disc"),
            ],
            DateProvenance.ReleaseDate,
            Notes: "US release date. The XGD2 image (7,838,695,424 B) is mirrored rather than " +
                   "expanded — this repository has no XDVDFS reader, unlike its Neversoft sibling.",
            Media:
            [
                new MediaItem("disc", MediaExtraction.KeepPacked),
            ]),

        new("Fallout - New Vegas", "2010-10-19", "PS3", "Final",
            [new SourcePart(SourceKind.MediaArchive, "Fallout - New Vegas (USA Brazil).zip", "disc")],
            DateProvenance.ReleaseDate,
            Notes: "US release date. UDF 2.50 Blu-ray image (10,470,555,648 B), mirrored rather " +
                   "than expanded — no UDF reader here.",
            Media:
            [
                new MediaItem("disc", MediaExtraction.Package, "."),
            ]),

        // ------------------------------------------------------------------
        // Fallout: New Vegas — the Xbox 360 prototype leak
        // ------------------------------------------------------------------

        new("Fallout - New Vegas", "2010-4-17", "X360", "Prototype",
            [new SourcePart(SourceKind.StagedTree, @"Full_Builds\Fallout New Vegas (April 17, 2010 partial data)")],
            DateProvenance.BuildLabel,
            Notes: "Partial data only — four BSAs and Credits.txt, no executable."),

        new("Fallout - New Vegas", "2010-7-21", "X360", "Prototype",
            [new SourcePart(SourceKind.StagedTree, @"Full_Builds\Fallout New Vegas (July 21, 2010)")],
            DateProvenance.BuildLabel),

        new("Fallout - New Vegas", "2010-8-22", "X360", "Prototype",
            [new SourcePart(SourceKind.StagedTree, @"Full_Builds\Fallout New Vegas (Aug 22, 2010)")],
            DateProvenance.BuildLabel),

        // Not previously staged: this is the one build in the collection that the corpus was
        // missing entirely. 7.3 GB compressed, from the "Christmas New Vegas DLC" drop.
        new("Fallout - New Vegas", "2011-2-15", "X360", "Prototype",
            [
                new SourcePart(SourceKind.MediaArchive, "Fallout New Vegas (Feb 15, 2011).7z"),
                new SourcePart(SourceKind.MediaFile, "Fallout_Release_Beta.XeniaPatch.xex", "XeniaPatch"),
            ],
            DateProvenance.BuildLabel,
            Notes: "The post-release DLC-era build. Ships with a Xenia-patched executable beside it."),

        new("Fallout - New Vegas", null, "X360", "Prototype",
            [new SourcePart(SourceKind.StagedTree, @"Full_Builds\Manually recovered textures BSA (Unknown date)")],
            Notes: "A single manually recovered Fallout - Textures.bsa. No date is claimed: the " +
                   "build it came from is not established."),
    ];

    /// <summary>
    ///     Titles deliberately kept out of the corpus. Recorded here, and echoed into the catalog
    ///     report, so the exclusion reads as a decision rather than an oversight.
    /// </summary>
    internal static readonly ExcludedTitle[] Excluded =
    [
        new("Fallout 4", @"steamapps\common\Fallout 4", 35.7,
            "PC title after Skyrim Special Edition — mirroring it would duplicate the install for no gain."),
        new("Fallout 76", @"steamapps\common\Fallout76", 103.3,
            "PC title after Skyrim Special Edition."),
        new("Starfield", @"steamapps\common\Starfield", 145.0,
            "PC title after Skyrim Special Edition."),
        new("The Elder Scrolls IV - Oblivion Remastered", @"steamapps\common\Oblivion Remastered", 119.2,
            "PC title after Skyrim Special Edition."),
    ];
}
