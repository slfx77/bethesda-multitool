using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Emits the human-reviewable diagnostics that accompany an asset packing run:
///     the per-asset <c>.missing.txt</c> audit (missing / conversion-failed / fuzzy-matched
///     sections) and the voice/LIP pairing sanity check.
/// </summary>
internal static class AssetPackAuditWriter
{
    public static void ReportVoiceLipPairDiagnostics(
        List<(string Path, byte[] Data)> packedFiles,
        List<AssetResolution> resolutions,
        IReadOnlyDictionary<string, string>? packPathRenames,
        IConversionProgressSink sink)
    {
        var oggStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lipStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _) in packedFiles)
        {
            if (!IsVoicePath(path))
            {
                continue;
            }

            var ext = Path.GetExtension(path);
            if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                oggStems.Add(PathStem(path));
            }
            else if (ext.Equals(".lip", StringComparison.OrdinalIgnoreCase))
            {
                lipStems.Add(PathStem(path));
            }
        }

        if (oggStems.Count == 0)
        {
            return;
        }

        var missingLipStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resolution in resolutions)
        {
            if (resolution.Kind != AssetResolutionKind.Missing
                || !resolution.RequestedPath.EndsWith(".lip", StringComparison.OrdinalIgnoreCase)
                || !IsVoicePath(resolution.RequestedPath))
            {
                continue;
            }

            var packPath = resolution.RequestedPath;
            if (packPathRenames is not null
                && packPathRenames.TryGetValue(resolution.RequestedPath, out var renamed))
            {
                packPath = renamed;
            }

            missingLipStems.Add(PathStem(packPath));
        }

        var unpaired = oggStems
            .Where(stem => !lipStems.Contains(stem))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unpaired.Count == 0)
        {
            sink.Info("AssetPacking",
                $"Voice lip pairing: {oggStems.Count:N0} packed OGG voice line(s), every line has a paired LIP.");
            return;
        }

        var missingSourceCount = unpaired.Count(stem => missingLipStems.Contains(stem));
        var samples = string.Join(", ", unpaired.Take(5));
        sink.Warn("AssetPacking",
            $"Voice lip pairing: {unpaired.Count:N0}/{oggStems.Count:N0} packed OGG voice line(s) " +
            $"have no paired LIP in the output ({missingSourceCount:N0} were unresolved source LIP requests). " +
            $"Samples: {samples}");
    }

    private static bool IsVoicePath(string path)
        => path.StartsWith("sound\\voice\\", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("sound/voice/", StringComparison.OrdinalIgnoreCase);

    private static string PathStem(string path)
    {
        var normalized = path.Replace('/', '\\');
        var ext = Path.GetExtension(normalized);
        return (ext.Length == 0 ? normalized : normalized[..^ext.Length]).ToLowerInvariant();
    }

    /// <summary>
    ///     Writes a human-reviewable per-asset audit next to the output BSA. Sections:
    ///     missing paths (the most useful — these are what the runtime won't find), fuzzy-
    ///     matched paths (sanity check the renames), and conversion-failed paths. Each
    ///     section is sorted alphabetically with one path per line so the user can diff
    ///     between runs.
    /// </summary>
    public static void TryWriteAuditFile(
        string outputBsaPath,
        IReadOnlyList<AssetResolution> resolutions,
        AssetPackingService.RunningStats stats,
        IConversionProgressSink sink)
    {
        try
        {
            var auditPath = outputBsaPath + ".missing.txt";
            using var writer = new StreamWriter(auditPath);

            writer.WriteLine($"# Asset packing audit for {Path.GetFileName(outputBsaPath)}");
            writer.WriteLine($"# Generated: {DateTime.UtcNow:O}");
            writer.WriteLine($"# Total paths scanned: {stats.Total}");
            writer.WriteLine($"# Already in baseline (skipped pack): {stats.AlreadyInBaseline}");
            writer.WriteLine($"# Resolved exact:                     {stats.ResolvedExact}");
            writer.WriteLine($"# Resolved fuzzy:                     {stats.ResolvedFuzzy}");
            writer.WriteLine($"# 360 → PC converted:                 {stats.Converted360}");
            writer.WriteLine($"# Conversion failed:                  {stats.ConversionFailed}");
            writer.WriteLine($"# Missing (unresolved):               {stats.Missing}");
            writer.WriteLine();

            WriteAuditSection(writer,
                "## MISSING — runtime will fail to load these (not in baseline, no fuzzy hit)",
                resolutions.Where(r => r.Kind == AssetResolutionKind.Missing));

            WriteAuditSection(writer,
                "## CONVERSION FAILED — 360→PC conversion errored; original bytes packed as fallback",
                resolutions.Where(r => r.Kind == AssetResolutionKind.ConversionFailed),
                includeError: true);

            WriteAuditSection(writer,
                "## FUZZY-MATCHED — same basename, different directory; sanity-check these",
                resolutions.Where(r =>
                    r.Kind is AssetResolutionKind.ResolvedFuzzy
                        or AssetResolutionKind.ResolvedFuzzyConverted),
                true);

            sink.Info("AssetPacking", $"Audit file written: {auditPath}");
        }
        catch (Exception ex)
        {
            sink.Warn("AssetPacking", $"Could not write audit file: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void WriteAuditSection(
        TextWriter writer,
        string header,
        IEnumerable<AssetResolution> entries,
        bool includeResolved = false,
        bool includeError = false)
    {
        var sorted = entries
            .OrderBy(r => r.RequestedPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        writer.WriteLine(header);
        writer.WriteLine($"# Count: {sorted.Count}");
        writer.WriteLine();

        foreach (var entry in sorted)
        {
            if (includeResolved && !string.IsNullOrEmpty(entry.ResolvedPath))
            {
                writer.WriteLine($"{entry.RequestedPath}  →  {entry.ResolvedPath}");
            }
            else if (includeError && !string.IsNullOrEmpty(entry.ConversionError))
            {
                writer.WriteLine($"{entry.RequestedPath}  ({entry.ConversionError})");
            }
            else
            {
                writer.WriteLine(entry.RequestedPath);
            }
        }

        writer.WriteLine();
    }
}
