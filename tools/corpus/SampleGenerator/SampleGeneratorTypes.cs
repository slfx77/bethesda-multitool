namespace SampleGenerator;

/// <summary>Where one part of a catalog entry's bytes comes from.</summary>
internal enum SourceKind
{
    /// <summary>A Steam install, resolved by folder name under any drive's <c>steamapps\common</c>.</summary>
    SteamGame,

    /// <summary>A directory already inside this repository's <c>Sample/</c> tree; migrated by MOVE.</summary>
    StagedTree,

    /// <summary>A single file already inside <c>Sample/</c> (a JAR, a disc image); migrated by MOVE.</summary>
    StagedFile,

    /// <summary>A directory found under one of the configured media roots; copied.</summary>
    MediaTree,

    /// <summary>A file (disc image, JAR) found under one of the configured media roots; copied.</summary>
    MediaFile,

    /// <summary>
    ///     A <c>.7z</c>/<c>.zip</c> under a media root whose <em>contents</em> are the build. Expanded
    ///     into the research cache with 7z, then mirrored.
    /// </summary>
    MediaArchive,
}

/// <summary>How the date in a catalog entry's directory name was established.</summary>
internal enum DateProvenance
{
    /// <summary>No date is claimed; the directory name omits it.</summary>
    None,

    /// <summary>The ISO 9660 primary volume descriptor's creation record.</summary>
    VolumeDescriptor,

    /// <summary>The COFF header timestamp of the build's main executable.</summary>
    ExecutableTimestamp,

    /// <summary>
    ///     The documented release date. Used only where nothing on the media carries a usable one —
    ///     each such entry says why in its <see cref="CatalogEntry.Notes" />.
    /// </summary>
    ReleaseDate,

    /// <summary>The date the leaked build itself is named for.</summary>
    BuildLabel,

    /// <summary>
    ///     Steam's own <c>appmanifest_*.acf</c> <c>LastUpdated</c> — when the depot content this
    ///     install holds was last changed.
    ///     <para>
    ///         The right date for a Steam build, because a Steam install is not a dated release: it
    ///         is whatever the depot last shipped. An executable's COFF timestamp records when the
    ///         exe was COMPILED and can be years older than the content beside it — Oblivion's is
    ///         2007 while its depot is 2022.
    ///     </para>
    /// </summary>
    SteamDepot,
}

/// <summary>
///     How a media part is expanded into the build tree.
/// </summary>
internal enum MediaExtraction
{
    /// <summary>Not media: ordinary build content that stays where it lands.</summary>
    NotMedia,

    /// <summary>Expand with 7z — ISO 9660, UDF, FAT floppy images, ZIP/JAR/7z packages.</summary>
    Package,

    /// <summary>
    ///     Raw 2352-byte-sector CD (redump <c>.cue</c> + <c>.bin</c>). 7z cannot read these, so
    ///     extraction shells to this repository's own CLI, whose <c>DiscImageBackend</c> can.
    /// </summary>
    RawDisc,

    /// <summary>
    ///     Expand the container but leave it where it is — it is part of the tree as shipped, not
    ///     a separate package that was supplied alongside.
    ///     <para>
    ///         This is the installer-cabinet case. A <c>.CAB</c> is a container that HOLDS the
    ///         game's assets, so its contents belong in the build; a <c>.BSA</c> is itself a game
    ///         asset that the engine reads, so it stays packed. Redguard's <c>DATA1.CAB</c> is the
    ///         whole install (1,664 files, 481 MB) and sits inside the disc's own ISO 9660 tree.
    ///     </para>
    /// </summary>
    ExpandInPlace,

    /// <summary>
    ///     Media that is kept but never expanded here — the tree already exists beside it, or no
    ///     reader can do it correctly.
    ///     <para>
    ///         ⚠⚠ The Xbox and Xbox 360 XGD images are the reason this exists. 7z <em>appears</em>
    ///         to read the 360 disc — it reports type <c>Udf</c> and succeeds — but returns 13 files
    ///         and 90 MB from a secondary partition instead of the game partition at 0xFD90000.
    ///         A silent wrong answer, so those images are never handed to it.
    ///     </para>
    /// </summary>
    KeepPacked,
}

/// <summary>
///     One source of bytes for a build, landing at <paramref name="DestinationSubdir" /> beneath the
///     build directory. Multi-part entries are how a single build keeps its disc image beside the
///     tree extracted from it (Brotherhood of Steel), or its two discs side by side (Redguard).
/// </summary>
/// <param name="Kind">How to obtain this part.</param>
/// <param name="Hint">Steam folder name, <c>Sample/</c>-relative path, or media file/dir name.</param>
/// <param name="DestinationSubdir">Subdirectory of the build to populate; null means the build root.</param>
/// <param name="SearchSubdir">Populate from this subdirectory of the source rather than its root.</param>
internal sealed record SourcePart(
    SourceKind Kind,
    string Hint,
    string? DestinationSubdir = null,
    string? SearchSubdir = null);

/// <summary>
///     Original media inside a build: a disc image, floppy set or release package that is expanded
///     into the build tree and then relocated to <c>Sample/Media</c>.
///     <para>
///         ⚠⚠ Naming each item EXPLICITLY is the whole point. Detecting media by file extension
///         instead would relocate real game content: the Battlespire and Redguard Steam installs
///         each ship a raw-sector <c>.bin</c> CD image that DOSBox mounts (484 MB and 709 MB, both
///         passing a sync-pattern test), and Daggerfall's hundreds of <c>.IMG</c> files are its
///         texture format, not disk images. Moving any of those would gut the build. The only true
///         disk-image set in this corpus is Arena's eight <c>.ima</c> floppies.
///     </para>
/// </summary>
/// <param name="PathInBuild">
///     Build-relative file or directory. A directory means every file directly inside it is media
///     (a disc's <c>.cue</c> plus its <c>.bin</c> tracks, or the eight floppy images).
/// </param>
/// <param name="Extraction">How to expand it, or that it is kept packed.</param>
/// <param name="ExtractTo">
///     Build-relative destination for the contents. Null unpacks to the media's own directory —
///     i.e. a disc that IS the build unpacks to the build root.
/// </param>
/// <param name="Recursive">
///     Move the named directory and everything under it. Opt-in, and rarely right: a media
///     directory usually holds the image at its top level and the tree extracted FROM it in
///     subdirectories. ⚠ Redguard's <c>Disc 1 (Install)</c> is exactly that shape, and treating
///     every media directory as recursive swept its <c>iso/</c> and <c>extracted/</c> trees out of
///     the build. Set this only where the directory is media all the way down — the Oblivion PSP
///     source archive, whose seven UMD images nest three levels deep.
/// </param>
internal sealed record MediaItem(
    string PathInBuild,
    MediaExtraction Extraction,
    string? ExtractTo = null,
    bool Recursive = false);

/// <summary>
///     One build in the corpus. The directory name is <em>computed</em> from these fields by
///     <see cref="SampleGeneratorCatalog.DirectoryName" />, so the naming convention lives in one
///     place and a change to it renames the whole corpus consistently.
/// </summary>
/// <param name="Game">Full title, e.g. "The Elder Scrolls IV - Oblivion".</param>
/// <param name="Date">Build/release date as <c>yyyy-M-d</c>, or null when none is claimed.</param>
/// <param name="Platform">Platform token: PC, X360, PS3, PS2, Xbox, PSP, J2ME, N-Gage.</param>
/// <param name="Kind">Final, Prototype, Prototypes, Demo, Beta or Variants.</param>
/// <param name="Parts">The sources that make up the build, in the order they are populated.</param>
/// <param name="DateSource">How <paramref name="Date" /> was established.</param>
/// <param name="MainExecutable">
///     Path (relative to the build root) of the executable whose COFF timestamp dates the build.
///     When set, the generator re-measures it and warns if it disagrees with <paramref name="Date" />
///     — a Steam patch that moves the build on silently would otherwise leave a stale name.
/// </param>
/// <param name="Notes">Provenance and caveats, written verbatim into the build manifest.</param>
/// <param name="Media">
///     Original media the build carries, expanded into the tree and then moved to
///     <c>Sample/Media</c>. Explicit by design — see <see cref="MediaItem" />.
/// </param>
/// <param name="SteamAppId">
///     The Steam application id, used to pick this build's appmanifest.
///     <para>
///         ⚠⚠ Matching an appmanifest by its <c>installdir</c> alone is WRONG: several apps install
///         into one directory. <c>Fallout New Vegas</c> is claimed by both app 22380 (the game,
///         buildid 1510068, 2022-05-24) and app 22480 (the GECK, buildid 52174, 2026-01-19) — and a
///         "take the most recently updated" rule dated the game from the editor, which is how this
///         build spent a while named 2026-1-19. Half-Life, Half-Life 2 and Starfield share
///         directories with their episodes and Creation Kit the same way.
///     </para>
/// </param>
/// <param name="ShippedExtras">
///     Mod-audit markers this build legitimately SHIPS, so they are reported as expected rather
///     than as modifications.
///     <para>
///         ⚑ The Steam releases of Fallout and Fallout 2 bundle sfall: a completely fresh
///         redownload on 2026-09-07 still carried <c>ddraw.dll</c> (and Fallout 2 its
///         <c>sfall-readme.txt</c>). That is what Steam distributes, not something a user added —
///         so calling it a modification was wrong. It is still a deviation from the Interplay CD,
///         which is why both CD releases are separate builds.
///     </para>
/// </param>
/// <param name="LegacyNames">
///     Pre-migration <c>Sample/Full_Builds/</c> directory names this build replaces, for entries
///     whose staged source is no longer one of their <see cref="Parts" />.
///     <para>
///         Normally the legacy name is recovered from a <c>StagedTree</c> part, but re-sourcing a
///         build from Steam removes that part and silently breaks the rewrite for every note and
///         test still using the old path. Naming them here keeps the mapping.
///     </para>
/// </param>
internal sealed record CatalogEntry(
    string Game,
    string? Date,
    string Platform,
    string Kind,
    SourcePart[] Parts,
    DateProvenance DateSource = DateProvenance.None,
    string? MainExecutable = null,
    string? Notes = null,
    MediaItem[]? Media = null,
    string[]? LegacyNames = null,
    string[]? ShippedExtras = null,
    string? SteamAppId = null);

/// <summary>
///     A title deliberately kept out of the corpus, recorded so the exclusion is visible in the
///     catalog rather than showing up as an unexplained absence.
/// </summary>
/// <param name="Game">Title.</param>
/// <param name="Location">Where it lives instead.</param>
/// <param name="ApproximateGigabytes">Measured install size at the time of exclusion.</param>
/// <param name="Reason">Why it is excluded.</param>
internal sealed record ExcludedTitle(
    string Game,
    string Location,
    double ApproximateGigabytes,
    string Reason);

/// <summary>Outcome of processing one catalog entry.</summary>
internal readonly record struct BuildResult(
    string Name,
    int FileCount,
    long TotalBytes,
    bool Missing,
    bool Skipped,
    string? Detail);
