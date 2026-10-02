using System.Text.Json;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Maps the pre-2026-09-07 <c>Sample/Full_Builds/…</c> fixture paths onto the generated
///     <c>Sample/Builds/…</c> corpus.
///     <para>
///         The corpus generator (today the sibling CorpusTool repo, driven by
///         <c>../CorpusTool/profiles/BethesdaMultitool.json</c>) renamed every build to
///         <c>Game Name (yyyy-M-d, Platform - Kind)</c> and regrouped the non-build fixtures
///         (<c>MemoryDump</c> → <c>MemoryDumps</c>, <c>PDB</c> → <c>DebugSymbols/…</c>). Rather than
///         hard-coding that rename table a second time here — where it would drift from the
///         generator's copy the first time a build is re-dated — the mapping is read back out of the
///         <c>catalog.json</c> the generator writes, which records the legacy path each build was
///         migrated from.
///     </para>
///     <para>
///         The rewrite is a transitional safety net, not the preferred spelling: new code should
///         name the corpus path directly. It exists because roughly thirty test files carry a
///         literal <c>Sample\Full_Builds\…</c> constant, and because concurrent sessions may still
///         have the old paths in flight.
///     </para>
///     <para>
///         No corpus means no <c>catalog.json</c>, which means no mapping and no rewriting — the
///         same state a machine without the private media is already in, where these suites skip.
///     </para>
/// </summary>
internal static class SampleCorpus
{
    private const string LegacyBuildsDirectory = "Full_Builds";
    private const string CorpusDirectory = "Builds";

    /// <summary>Fixed regroupings that are not per-build, so are not in the catalog.</summary>
    private static readonly (string Legacy, string Current)[] FixedMoves =
    [
        // ⚠⚠ The left-hand spellings are deliberately the OLD directory names, and a repo-wide
        // path sweep that "fixes" them here turns the rewrite into a silent no-op that still
        // compiles and still passes every other test. That happened three times while this
        // migration was being written. SampleCorpusTests pins both rows against their expected
        // output, so the next sweep that eats them fails loudly instead.
        (@"Sample\MemoryDump\", @"Sample\MemoryDumps\"),
        (@"Sample\PDB\", @"Sample\DebugSymbols\Fallout - New Vegas (X360)\")
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> LazyMap = new(BuildMap);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> LazyAliases = new(() =>
        ReadBuildAliases(FindProfile()));

    // Sample/Catalog is where the generator writes now; Sample/Builds is the earlier location,
    // kept in the probe so a corpus generated before the split still resolves.
    private static readonly string[] CatalogRelativePaths =
    [
        Path.Combine("Catalog", "catalog.json"),
        Path.Combine(CorpusDirectory, "catalog.json")
    ];

    /// <summary>
    ///     Rewrites a <c>Sample/</c>-relative (or repo-relative) legacy fixture path to its current
    ///     location. Returns null when the path names nothing the migration moved, so callers can
    ///     tell "no rewrite applies" from "rewritten to itself".
    /// </summary>
    public static string? Rewrite(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Replace('/', '\\');

        foreach (var (legacy, current) in FixedMoves)
        {
            if (normalized.StartsWith(legacy, StringComparison.OrdinalIgnoreCase))
            {
                return current + normalized[legacy.Length..];
            }
        }

        // Accept both "Sample\Full_Builds\X" and the Sample-relative "Full_Builds\X".
        var prefix = $@"Sample\{LegacyBuildsDirectory}\";
        var samplePrefixed = normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        if (!samplePrefixed &&
            !normalized.StartsWith($@"{LegacyBuildsDirectory}\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var afterPrefix = samplePrefixed
            ? normalized[prefix.Length..]
            : normalized[(LegacyBuildsDirectory.Length + 1)..];

        // Longest legacy key first: "Redguard_Disc1_iso" must win over "Redguard_Disc1".
        foreach (var (legacyName, currentRelative) in LazyMap.Value.OrderByDescending(p => p.Key.Length))
        {
            if (!afterPrefix.StartsWith(legacyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remainder = afterPrefix[legacyName.Length..].TrimStart('\\');
            var rebuilt = remainder.Length == 0
                ? currentRelative
                : Path.Combine(currentRelative, remainder);
            return samplePrefixed ? Path.Combine("Sample", rebuilt) : rebuilt;
        }

        return null;
    }

    /// <summary>
    ///     Yields the caller's path, then its rewritten form when one exists — the probe order every
    ///     resolver in this suite should use so both spellings resolve during the transition.
    /// </summary>
    public static IEnumerable<string> Candidates(string relativePath)
        => Candidates(relativePath, LazyAliases.Value);

    internal static IEnumerable<string> CandidatesForProfile(string relativePath, string profilePath)
        => Candidates(relativePath, ReadBuildAliases(profilePath));

    private static IEnumerable<string> Candidates(string relativePath, IReadOnlyDictionary<string, string> aliases)
    {
        yield return relativePath;

        var renamed = RewriteBuildAlias(relativePath, aliases);
        if (renamed is not null) yield return renamed;

        var rewritten = Rewrite(relativePath);
        if (rewritten is not null)
        {
            yield return rewritten;
            renamed = RewriteBuildAlias(rewritten, aliases);
            if (renamed is not null) yield return renamed;
        }
    }

    // Ownership profiles are optional and private. Keep historical fixture spellings usable when
    // a build receives a better evidenced date, without copying that inventory into the test suite.
    private static string? FindProfile()
    {
        var configured = Environment.GetEnvironmentVariable("BETHESDA_CORPUS_PROFILE");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory?.Parent is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
                return Path.Combine(directory.Parent.FullName, "CorpusTool", "profiles", "BethesdaMultitool.json");
        }
        return null;
    }

    private static string? RewriteBuildAlias(string path, IReadOnlyDictionary<string, string> aliases)
    {
        var parts = path.Replace('/', '\\').Split('\\');
        var area = parts.Length > 0 && parts[0].Equals("Sample", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (parts.Length <= area + 1 ||
            !(parts[area].Equals("Builds", StringComparison.OrdinalIgnoreCase) ||
              parts[area].Equals("Media", StringComparison.OrdinalIgnoreCase)) ||
            parts.Any(part => part is "" or "." or "..") ||
            !aliases.TryGetValue(parts[area + 1], out var canonical) ||
            canonical.Equals(parts[area + 1], StringComparison.OrdinalIgnoreCase)) return null;
        parts[area + 1] = canonical;
        return string.Join('\\', parts);
    }

    private static IReadOnlyDictionary<string, string> ReadBuildAliases(string? profilePath)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (profilePath is null || !File.Exists(profilePath)) return aliases;
        using var document = JsonDocument.Parse(File.ReadAllText(profilePath), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
        });
        var root = document.RootElement;
        var naming = root.TryGetProperty("naming", out var templates) ? templates : default;
        foreach (var build in root.GetProperty("builds").EnumerateArray())
        {
            var date = Text(build, "date");
            var template = Text(naming, date is null ? "undated" : "dated") ??
                (date is null ? "{game} ({platform} - {kind})" : "{game} ({date}, {platform} - {kind})");
            var name = template.Replace("{game}", Text(build, "game") ?? "", StringComparison.Ordinal)
                .Replace("{date}", date ?? "", StringComparison.Ordinal)
                .Replace("{platform}", Text(build, "platform") ?? "", StringComparison.Ordinal)
                .Replace("{kind}", Text(build, "kind") ?? "", StringComparison.Ordinal);
            name = new string(name.Select(character => Invalid(character) ? '_' : character).ToArray()).TrimEnd('.', ' ');
            var variant = Text(build, "variant");
            if (!string.IsNullOrEmpty(variant))
            {
                if (!char.IsAsciiLetterOrDigit(variant[0]) || !char.IsAsciiLetterOrDigit(variant[^1]) ||
                    variant.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
                    throw new InvalidDataException("Invalid corpus build variant.");
                name = name.EndsWith(')') ? $"{name[..^1]}; {variant})" : $"{name} ({variant})";
            }
            Add(name, name);
            if (build.TryGetProperty("previousNames", out var previousNames))
                foreach (var previous in previousNames.EnumerateArray()) Add(previous.GetString()!, name);
        }
        return aliases;

        void Add(string previous, string canonical)
        {
            if (string.IsNullOrWhiteSpace(previous) || previous is "." or ".." || previous.Any(Invalid) ||
                previous.EndsWith('.') || previous.EndsWith(' '))
                throw new InvalidDataException("Corpus build names must be single directory names.");
            if (aliases.TryGetValue(previous, out var existing) && !existing.Equals(canonical, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Ambiguous corpus build alias: " + previous);
            aliases[previous] = canonical;
        }
        static bool Invalid(char character) => char.IsControl(character) || "\\/:*?\"<>|".Contains(character);
        static string? Text(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    }

    /// <summary>
    ///     Legacy build-directory name (e.g. <c>Arena_Disc</c>) to its <c>Sample/</c>-relative
    ///     corpus path (e.g. <c>Builds\The Elder Scrolls - Arena (1994-10-18, PC - Final)</c>).
    /// </summary>
    private static Dictionary<string, string> BuildMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var catalogPath = FindCatalog();
        if (catalogPath is null)
        {
            return map;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(catalogPath));
            if (!document.RootElement.TryGetProperty("Builds", out var builds))
            {
                return map;
            }

            foreach (var build in builds.EnumerateArray())
            {
                if (!build.TryGetProperty("Name", out var nameElement) ||
                    !build.TryGetProperty("LegacySources", out var sources) ||
                    sources.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var buildName = nameElement.GetString();
                if (string.IsNullOrEmpty(buildName))
                {
                    continue;
                }

                foreach (var source in sources.EnumerateArray())
                {
                    if (!source.TryGetProperty("Path", out var pathElement))
                    {
                        continue;
                    }

                    var legacy = pathElement.GetString();
                    if (string.IsNullOrEmpty(legacy))
                    {
                        continue;
                    }

                    // Recorded as "Full_Builds\Arena_Disc"; the map is keyed on the name alone.
                    var legacyName = legacy.Replace('/', '\\');
                    var separator = legacyName.IndexOf('\\', StringComparison.Ordinal);
                    if (separator >= 0)
                    {
                        legacyName = legacyName[(separator + 1)..];
                    }

                    var destination = source.TryGetProperty("DestinationSubdir", out var subdir)
                        ? subdir.GetString()
                        : null;

                    var target = string.IsNullOrEmpty(destination)
                        ? Path.Combine(CorpusDirectory, buildName)
                        : Path.Combine(CorpusDirectory, buildName, destination);

                    map[legacyName] = target;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable catalog means no rewriting, which is the same as no corpus.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return map;
    }

    private static string? FindCatalog()
    {
        var root = Environment.GetEnvironmentVariable(RealAssetPaths.RootVariable);
        if (!string.IsNullOrEmpty(root))
        {
            foreach (var relative in CatalogRelativePaths)
            {
                foreach (var candidate in new[]
                         {
                             Path.Combine(root, relative),
                             Path.Combine(root, "Sample", relative)
                         })
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            foreach (var relative in CatalogRelativePaths)
            {
                var candidate = Path.Combine(dir, "Sample", relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
