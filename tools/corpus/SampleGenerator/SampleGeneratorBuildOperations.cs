using static SampleGenerator.SampleGeneratorConfig;
using static SampleGenerator.SampleGeneratorFileOperations;

namespace SampleGenerator;

/// <summary>
///     End-to-end pipeline for one catalog entry: resolve each source part, populate it into the
///     build directory, then write the manifest that records where the bytes came from.
/// </summary>
internal static class SampleGeneratorBuildOperations
{
    /// <summary>
    ///     Per-build provenance, written under <c>Sample/Catalog/builds/</c> rather than into the
    ///     build itself — a build directory holds game files and nothing else.
    /// </summary>
    internal static string ManifestPath(string buildName) =>
        SampleGeneratorPathSafety.ResolveDestinationPath(
            Path.Combine(SampleCatalog, "builds"), buildName + ".json");

    internal static BuildResult RunBuild(CatalogEntry entry, bool repopulate, bool dryRun)
    {
        var name = SampleGeneratorCatalog.DirectoryName(entry);
        var buildDir = SampleGeneratorPathSafety.ResolveDestinationPath(SampleBuilds, name);

        var resolved = new List<ResolvedPart>();
        foreach (var part in entry.Parts)
        {
            var source = ResolveSource(part);
            if (source is not null)
            {
                resolved.Add(new ResolvedPart(part, source));
            }
        }

        // ⚠⚠ Presence is decided by the BUILD, never by whether its sources still resolve. After
        // migration a StagedTree source under Full_Builds is gone by design — it was MOVED into the
        // corpus — so judging by sources reports a fully populated build as MISSING, drops it from
        // catalog.json, and returns a failing exit code on a healthy corpus.
        var alreadyPopulated = File.Exists(ManifestPath(name)) ||
                               File.Exists(Path.Combine(buildDir, "build.json"));

        if (alreadyPopulated)
        {
            // Repopulating with sources gone would delete the migrated tree and restore only the
            // parts that still resolve, silently losing the rest — refuse rather than half-rebuild.
            if (repopulate && resolved.Count < entry.Parts.Length)
            {
                var lost = entry.Parts.Except(resolved.Select(r => r.Spec))
                    .Select(p => $"{p.Kind}:{p.Hint}");
                var (heldFiles, heldBytes) = MeasureTree(buildDir);
                return new BuildResult(name, heldFiles, heldBytes, Missing: false, Skipped: true,
                    Detail: "refusing --repopulate: these sources are gone (already migrated), so a " +
                            $"rebuild would drop them — {string.Join(", ", lost)}");
            }

            if (!repopulate)
            {
                // The corpus is ~125 GB, so an existing build is left alone — except that parts
                // whose destination is absent are still added, letting a catalog entry grow a new
                // part without a full rebuild.
                var added = PopulateAbsentParts(entry, resolved, buildDir);
                var finished = Finish(entry, name, buildDir, resolved, dryRun);
                var (existingFiles, existingBytes) = MeasureTree(buildDir);

                var notes = new List<string>();
                if (added > 0) notes.Add($"added {added} missing part(s)");
                if (finished.Media.Extracted > 0) notes.Add($"extracted {finished.Media.Extracted} media");
                if (finished.Media.Relocated > 0) notes.Add($"moved {finished.Media.Relocated} media file(s)");
                if (finished.Symbols.Copied > 0) notes.Add($"collected {finished.Symbols.Copied} symbol file(s)");
                if (finished.Media.Failed > 0) notes.Add($"{finished.Media.Failed} media extraction(s) FAILED");

                return new BuildResult(name, existingFiles, existingBytes, Missing: false, Skipped: true,
                    Detail: notes.Count == 0 ? "already populated" : string.Join(", ", notes));
            }
        }
        else if (resolved.Count == 0)
        {
            return new BuildResult(name, 0, 0, Missing: true, Skipped: false,
                Detail: DescribeMissing(entry));
        }

        if (dryRun)
        {
            var descriptions = resolved.Select(p =>
                p.Spec.DestinationSubdir is null ? p.Source : $"{p.Source} -> {p.Spec.DestinationSubdir}");
            return new BuildResult(name, 0, 0, Missing: false, Skipped: true,
                Detail: $"would populate {resolved.Count}/{entry.Parts.Length} part(s):" +
                        $"{Environment.NewLine}             {string.Join(Environment.NewLine + "             ", descriptions)}");
        }

        if (repopulate && Directory.Exists(buildDir))
        {
            DeleteTreeUnder(buildDir, SampleBuilds);
        }

        Directory.CreateDirectory(buildDir);

        var files = 0;
        long bytes = 0;
        foreach (var part in resolved)
        {
            var destination = part.Spec.DestinationSubdir is null
                ? buildDir
                : SampleGeneratorPathSafety.ResolveDestinationPath(buildDir, part.Spec.DestinationSubdir);

            var (partFiles, partBytes) = PopulatePart(part, destination, repopulate);
            files += partFiles;
            bytes += partBytes;
        }

        var outcome = Finish(entry, name, buildDir, resolved, dryRun);
        (files, bytes) = MeasureTree(buildDir);

        var detail = new List<string>();
        if (resolved.Count < entry.Parts.Length) detail.Add($"{resolved.Count}/{entry.Parts.Length} parts available");
        if (outcome.Media.Extracted > 0) detail.Add($"extracted {outcome.Media.Extracted} media");
        if (outcome.Media.Failed > 0) detail.Add($"{outcome.Media.Failed} media extraction(s) FAILED");

        return new BuildResult(name, files, bytes, Missing: false, Skipped: false,
            Detail: detail.Count == 0 ? null : string.Join(", ", detail));
    }

    /// <summary>
    ///     Populates parts of an existing build whose destination is not there yet, leaving
    ///     everything already present untouched. Only parts whose presence can be decided are
    ///     considered: a named file, or a tree with its own subdirectory. A tree part that lands on
    ///     the build root is indistinguishable from the build itself, so it is left alone.
    /// </summary>
    private static int PopulateAbsentParts(
        CatalogEntry entry, IReadOnlyList<ResolvedPart> resolved, string buildDir)
    {
        var added = 0;
        foreach (var part in resolved)
        {
            // ⚠⚠ A part that landed as declared MEDIA is SUPPOSED to be gone from the build — the
            // media stage moved it to Sample/Media. Treating its absence as a gap re-copies the
            // image from the research cache, re-extracts it, and the media stage moves it out
            // again: an infinite churn that re-expanded a 10 GB PS3 image on every single run.
            if (IsRelocatedMedia(entry, part))
            {
                continue;
            }

            var destination = part.Spec.DestinationSubdir is null
                ? buildDir
                : SampleGeneratorPathSafety.ResolveDestinationPath(buildDir, part.Spec.DestinationSubdir);

            var absent = part.Spec.Kind switch
            {
                SourceKind.StagedFile or SourceKind.MediaFile =>
                    !File.Exists(Path.Combine(destination, Path.GetFileName(part.Source))),
                _ => part.Spec.DestinationSubdir is not null &&
                     (!Directory.Exists(destination) || !Directory.EnumerateFileSystemEntries(destination).Any()),
            };

            if (!absent)
            {
                continue;
            }

            PopulatePart(part, destination, repopulate: false);
            added++;
        }

        return added;
    }

    /// <summary>
    ///     The stages every build passes through once its parts are in place: expand and relocate
    ///     its media, collect its symbols, then record what happened. Run on new and existing
    ///     builds alike so a corpus generated before these stages existed converges on a re-run.
    /// </summary>
    private static (SampleGeneratorMediaStage.MediaResult Media, SampleGeneratorSymbolStage.SymbolResult Symbols)
        Finish(CatalogEntry entry, string name, string buildDir, IReadOnlyList<ResolvedPart> resolved, bool dryRun)
    {
        var media = SampleGeneratorMediaStage.Apply(entry, buildDir, name, dryRun);
        var symbols = SampleGeneratorSymbolStage.Apply(entry, buildDir, name, dryRun);

        if (!dryRun)
        {
            var (files, bytes) = MeasureTree(buildDir);
            SampleGeneratorManifest.Write(entry, buildDir, resolved, files, bytes);

            // Drop the manifest the previous layout left inside the build directory.
            var legacyManifest = Path.Combine(buildDir, "build.json");
            if (File.Exists(legacyManifest))
            {
                File.SetAttributes(legacyManifest, FileAttributes.Normal);
                File.Delete(legacyManifest);
            }
        }

        return (media, symbols);
    }

    /// <summary>Resolves one source part to an absolute path, or null when the media is absent.</summary>
    internal static string? ResolveSource(SourcePart part)
    {
        switch (part.Kind)
        {
            case SourceKind.SteamGame:
                return FindSteamGame(part.Hint);

            case SourceKind.StagedTree:
            {
                var staged = Path.Combine(SampleRoot, part.Hint);
                return Directory.Exists(staged) ? staged : null;
            }

            case SourceKind.StagedFile:
            {
                var staged = Path.Combine(SampleRoot, part.Hint);
                return File.Exists(staged) ? staged : null;
            }

            case SourceKind.MediaTree:
                return FindMedia(part.Hint, directory: true);

            case SourceKind.MediaFile:
            case SourceKind.MediaArchive:
                return FindMedia(part.Hint, directory: false);

            default:
                return null;
        }
    }

    private static (int Files, long Bytes) PopulatePart(ResolvedPart part, string destination, bool repopulate)
    {
        var source = part.Source;

        switch (part.Spec.Kind)
        {
            case SourceKind.MediaArchive:
            {
                // Expand into the research cache first, so the (slow) extraction survives a corpus
                // rebuild, then mirror the cached tree into the corpus.
                var cacheName = SampleGeneratorPathSafety.SanitizePathSegment(
                    Path.GetFileNameWithoutExtension(source));
                var cacheDir = SampleGeneratorPathSafety.ResolveDestinationPath(ResearchRoot, cacheName);
                if (!ExpandArchive(source, cacheDir, force: false))
                {
                    return (0, 0);
                }

                var root = part.Spec.SearchSubdir is null
                    ? cacheDir
                    : Path.Combine(cacheDir, part.Spec.SearchSubdir);
                return Directory.Exists(root) ? MirrorTree(root, destination) : (0, 0);
            }

            case SourceKind.StagedTree:
            {
                var root = part.Spec.SearchSubdir is null ? source : Path.Combine(source, part.Spec.SearchSubdir);
                return MoveTree(root, destination);
            }

            case SourceKind.StagedFile:
                return MoveFile(source, destination);

            case SourceKind.MediaFile:
                return MirrorFile(source, destination);

            case SourceKind.SteamGame:
            case SourceKind.MediaTree:
            {
                var root = part.Spec.SearchSubdir is null ? source : Path.Combine(source, part.Spec.SearchSubdir);
                return MirrorTree(root, destination);
            }

            default:
                _ = repopulate;
                return (0, 0);
        }
    }

    /// <summary>
    ///     Whether this part's destination is declared media, and so is expected to have been moved
    ///     out of the build already.
    /// </summary>
    private static bool IsRelocatedMedia(CatalogEntry entry, ResolvedPart part)
    {
        if (entry.Media is not { Length: > 0 } items)
        {
            return false;
        }

        var destination = part.Spec.DestinationSubdir;
        return items.Any(item =>
            item.PathInBuild == "." ||
            (destination is not null &&
             item.PathInBuild.Equals(destination, SampleGeneratorPathSafety.PathComparison)) ||
            (destination is null &&
             item.PathInBuild.Equals(Path.GetFileName(part.Source), SampleGeneratorPathSafety.PathComparison)));
    }

    private static string DescribeMissing(CatalogEntry entry)
    {
        var hints = entry.Parts.Select(p => $"{p.Kind}:{p.Hint}");
        return $"no source found for {string.Join(", ", hints)}";
    }

    /// <summary>A catalog source part paired with the path it resolved to.</summary>
    internal readonly record struct ResolvedPart(SourcePart Spec, string Source);
}
