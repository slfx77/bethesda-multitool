using System.Text.Json;
using System.Text.Json.Serialization;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Builds and persists the mesh-path rename map for a memory dump: the same
///     <see cref="DataFolderIndex" /> + <see cref="DataFolderResolver" /> pass the DMP→ESM
///     conversion runs (<see cref="AssetRenameService" />), reduced to mesh paths and captured as a
///     durable path→path map instead of in-place record rewrites.
///     <para>
///         The conversion pass discards its matches once the plugin is written — nothing
///         machine-readable survives it. Persisting the map as a sidecar next to the dump lets the
///         renderer preview the dump with the same resolutions the converter would apply, without
///         re-running the (expensive, full-Data-folder) indexing on every load: loaders pick the
///         sidecar up when present and <see cref="MeshArchiveSet" /> consults it ahead of its own
///         fuzzy fallback.
///     </para>
///     <para>
///         Mesh-only on purpose: paths in and out are always <c>.nif</c>, so the Xbox-360
///         container-extension prediction that matters for sound/texture renames
///         (<c>.xma</c>/<c>.ddx</c>) never changes a result here, and donor folders need no
///         360-format flag.
///     </para>
/// </summary>
internal static class MeshRenameMapService
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>Sidecar path for a dump: the dump's own path plus this suffix.</summary>
    public static string SidecarPathFor(string sourceFilePath) => sourceFilePath + ".assetrenames.json";

    public sealed record BuildResult(
        IReadOnlyDictionary<string, string> Renames,
        int Considered,
        int Renamed,
        int Exact,
        int Missing,
        int CrossRootDeclined);

    /// <summary>
    ///     Resolves every distinct mesh path against the donor Data folders, conversion-style, and
    ///     returns the entries where the asset survives under a DIFFERENT name. Exact hits and
    ///     unresolvable paths produce no entry; a cross-category match is declined exactly as the
    ///     conversion pass declines it (<see cref="AssetPathRewriter" />).
    /// </summary>
    /// <param name="donorDataDirectories">
    ///     Data folders in priority order (highest first). The LAST doubles as the resolver's
    ///     baseline — the retail-most build, mirroring conversion where the baseline is the folder
    ///     the output plugin ships against.
    /// </param>
    public static BuildResult Build(
        IEnumerable<string> meshModelPaths,
        IReadOnlyList<string> donorDataDirectories,
        CancellationToken ct)
    {
        var dirs = donorDataDirectories
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (dirs.Count == 0)
        {
            return new BuildResult(new Dictionary<string, string>(), 0, 0, 0, 0, 0);
        }

        using var baseline = new DataFolderIndex(dirs[^1], false, ArchiveHandleRegistry.Shared);
        baseline.Build();

        var secondaries = new List<DataFolderIndex>();
        try
        {
            foreach (var dir in dirs)
            {
                ct.ThrowIfCancellationRequested();
                var index = new DataFolderIndex(dir, false, ArchiveHandleRegistry.Shared);
                index.Build();
                secondaries.Add(index);
            }

            var resolver = new DataFolderResolver(baseline, secondaries);
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int considered = 0, renamed = 0, exact = 0, missing = 0, crossRoot = 0;
            foreach (var raw in meshModelPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (AssetPathRules.TryNormalizeRequestPath(raw) is not { } normalized) continue;

                considered++;
                var resolution = resolver.Resolve(normalized);
                if (resolution.Kind == AssetResolutionKind.Missing)
                {
                    missing++;
                    continue;
                }

                var resolvedPath = resolution.ResolvedPath ?? normalized;
                var target = PrototypeAssetConverter.PredictPackedPath(
                    resolvedPath,
                    resolution.Source?.NormalizedPath ?? resolvedPath,
                    resolution.Source?.IsXbox360 ?? false);

                if (string.Equals(target, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    exact++;
                    continue;
                }

                if (!AssetPathRules.SharesCategoryRoot(target, normalized))
                {
                    crossRoot++;
                    continue;
                }

                renames[normalized] = target;
                renamed++;
            }

            return new BuildResult(renames, considered, renamed, exact, missing, crossRoot);
        }
        finally
        {
            foreach (var index in secondaries)
            {
                index.Dispose();
            }
        }
    }

    public static void Save(
        string sidecarPath,
        IReadOnlyDictionary<string, string> renames,
        IReadOnlyList<string> donorDataDirectories)
    {
        var file = new SidecarFile
        {
            Version = FormatVersion,
            CreatedUtc = DateTime.UtcNow,
            DonorDataDirectories = [.. donorDataDirectories],
            Renames = renames.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => kv.Value)
        };
        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(file, JsonOptions));
    }

    /// <summary>Loads a sidecar's rename map, or null when absent/unreadable/empty.</summary>
    public static IReadOnlyDictionary<string, string>? TryLoad(string sidecarPath)
    {
        try
        {
            if (!File.Exists(sidecarPath)) return null;
            var file = JsonSerializer.Deserialize<SidecarFile>(File.ReadAllText(sidecarPath), JsonOptions);
            if (file?.Renames is not { Count: > 0 } renames) return null;
            return new Dictionary<string, string>(renames, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class SidecarFile
    {
        public int Version { get; set; }
        public DateTime CreatedUtc { get; set; }

        [JsonPropertyName("donor_data_directories")]
        public List<string> DonorDataDirectories { get; set; } = [];

        public Dictionary<string, string> Renames { get; set; } = [];
    }
}
