using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Map;

/// <summary>Encoding of one marker-icon payload before it is materialized by the UI.</summary>
internal enum MapMarkerIconPayloadFormat
{
    Dds,
    Png
}

/// <summary>Where an icon payload came from.</summary>
internal enum MapMarkerIconPayloadSource
{
    GameArchive,
    EmbeddedFallback
}

/// <summary>Encoded icon bytes plus the source/format needed by the UI decoder.</summary>
internal sealed record MapMarkerIconPayload(
    byte[] Bytes,
    MapMarkerIconPayloadFormat Format,
    MapMarkerIconPayloadSource Source);

/// <summary>
///     The shipped container and source artwork for one game's map markers. A source path containing
///     <c>{iconKey}</c> denotes directly loadable per-icon DDS data. SWF-only games are documented here
///     too, but deliberately have <see cref="HasDirectPerIconAssets" /> false: the runtime has no SWF
///     vector rasterizer, so their already-rasterized embedded PNGs remain the supported fallback.
/// </summary>
internal sealed record MapMarkerArchiveAssetPlan(
    string ArchiveFileName,
    IReadOnlyList<string> SourceAssetPaths,
    bool HasDirectPerIconAssets);

/// <summary>
///     Exact, installed-game archive locations for authentic map marker art. The mapping is intentionally
///     pure: archive discovery and provider tests can lock the paths without depending on a local game install.
/// </summary>
internal static class MapMarkerArchiveAssetCatalog
{
    private const string FalloutIconPattern = @"textures\interface\icons\world map\{iconKey}.dds";

    private static readonly IReadOnlyDictionary<BethesdaGame, MapMarkerArchiveAssetPlan> Plans =
        new Dictionary<BethesdaGame, MapMarkerArchiveAssetPlan>
        {
            [BethesdaGame.Fallout3] = new(
                "Fallout - Textures.bsa", [FalloutIconPattern], HasDirectPerIconAssets: true),
            [BethesdaGame.FalloutNewVegas] = new(
                "Fallout - Textures2.bsa", [FalloutIconPattern], HasDirectPerIconAssets: true),
            [BethesdaGame.Oblivion] = new(
                "Oblivion - Textures - Compressed.bsa",
                [@"textures\menus\map\world\world_map_*.dds"],
                HasDirectPerIconAssets: true),
            [BethesdaGame.Skyrim] = new(
                "Skyrim - Interface.bsa", [@"interface\map.swf"], HasDirectPerIconAssets: false),
            [BethesdaGame.Fallout4] = new(
                "Fallout4 - Interface.ba2",
                [@"Interface\MapMarkers.swf", @"Interface\Pipboy_MapPage.swf"],
                HasDirectPerIconAssets: false),
            [BethesdaGame.Fallout76] = new(
                "SeventySix - Interface.ba2",
                [@"interface\mapmarkerslibrary.swf"],
                HasDirectPerIconAssets: false)
        };

    private static readonly IReadOnlyDictionary<int, string> OblivionIconPaths =
        new Dictionary<int, string>
        {
            [1] = @"textures\menus\map\world\world_map_icon_camp.dds",
            [2] = @"textures\menus\map\world\world_map_icon_cave.dds",
            [3] = @"textures\menus\map\world\world_map_city_icon.dds",
            [4] = @"textures\menus\map\world\world_map_icon_elven_ruin.dds",
            [5] = @"textures\menus\map\world\world_map_icon_fort_ruin.dds",
            [6] = @"textures\menus\map\world\world_map_icon_mine.dds",
            [7] = @"textures\menus\map\world\world_map_icon_mountain_peak.dds",
            [8] = @"textures\menus\map\world\world_map_icon_tavern.dds",
            [9] = @"textures\menus\map\world\world_map_icon_settlement.dds",
            // Bethesda's names are misleading: these two were verified by visual content.
            [10] = @"textures\menus\map\world\world_map_daedric_shrine_icon.dds",
            [11] = @"textures\menus\map\world\world_map_icon_daedric_shrine.dds"
        };

    internal static MapMarkerArchiveAssetPlan? For(BethesdaGame game) =>
        Plans.GetValueOrDefault(game);

    /// <summary>Returns a directly decodable DDS path for this catalog entry, or null for SWF-only art.</summary>
    internal static string? DirectIconPath(BethesdaGame game, MapMarkerEntry entry) => game switch
    {
        BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas
            when entry.IconKey.StartsWith("icon_map_", StringComparison.OrdinalIgnoreCase) =>
            $@"textures\interface\icons\world map\{entry.IconKey}.dds",
        BethesdaGame.Oblivion when OblivionIconPaths.TryGetValue(entry.RawValue, out var path) => path,
        _ => null
    };

    /// <summary>
    ///     Builds one exact archive candidate beside each primary/load-order data file. No directory glob
    ///     is involved, so icon initialization probes at most one path per distinct data directory.
    /// </summary>
    internal static IReadOnlyList<string> BuildArchiveCandidates(
        BethesdaGame game,
        IEnumerable<string?> dataSourcePaths)
    {
        var plan = For(game);
        if (plan is null || !plan.HasDirectPerIconAssets)
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in dataSourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(sourcePath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            var candidate = string.Equals(
                Path.GetFileName(fullPath),
                plan.ArchiveFileName,
                StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : Path.Combine(Path.GetDirectoryName(fullPath) ?? string.Empty, plan.ArchiveFileName);
            if (seen.Add(candidate))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    internal static string? FindExistingArchive(
        BethesdaGame game,
        IEnumerable<string?> dataSourcePaths,
        Func<string, bool> fileExists)
    {
        return BuildArchiveCandidates(game, dataSourcePaths).FirstOrDefault(fileExists);
    }
}

/// <summary>
///     Cache-once archive-first byte provider. It opens at most one exact game archive, indexes it once,
///     and extracts each requested icon at most once. The caller supplies a materializer (Win2D in the
///     app): if an archive DDS is absent or cannot be materialized, the provider then tries the embedded
///     PNG. This keeps fallback policy pure and unit-testable without a GPU or installed games.
/// </summary>
internal sealed class ArchiveFirstMapMarkerIconProvider : IDisposable
{
    private readonly BethesdaGame _game;
    private readonly Func<string, byte[]?>? _readArchiveFile;
    private readonly Func<string, byte[]?> _readEmbeddedPng;
    private readonly Dictionary<int, MapMarkerIconPayload?> _archiveCache = new();
    private readonly Dictionary<int, MapMarkerIconPayload?> _embeddedCache = new();
    private readonly ArchiveReader? _archiveReader;

    internal ArchiveFirstMapMarkerIconProvider(
        BethesdaGame game,
        IEnumerable<string?> dataSourcePaths)
    {
        _game = game;
        _readEmbeddedPng = MapMarkerIconProvider.GetIconPng;
        ArchivePath = MapMarkerArchiveAssetCatalog.FindExistingArchive(game, dataSourcePaths, File.Exists);
        if (ArchivePath is null)
        {
            return;
        }

        try
        {
            _archiveReader = ArchiveReader.Open(ArchivePath);
            _readArchiveFile = _archiveReader.ReadFile;
        }
        catch
        {
            // A missing/locked/corrupt game archive is non-fatal: every supported type has an embedded
            // or glyph fallback. Leave the archive reader null and resolve those below.
        }
    }

    /// <summary>Test seam for pure precedence/cache/fallback tests.</summary>
    internal ArchiveFirstMapMarkerIconProvider(
        BethesdaGame game,
        Func<string, byte[]?>? readArchiveFile,
        Func<string, byte[]?> readEmbeddedPng)
    {
        _game = game;
        _readArchiveFile = readArchiveFile;
        _readEmbeddedPng = readEmbeddedPng;
    }

    internal string? ArchivePath { get; }

    /// <summary>
    ///     Materializes the archive candidate first. Returning null from <paramref name="materialize" />
    ///     (for example, an unsupported/corrupt DDS) activates the embedded PNG fallback.
    /// </summary>
    internal TResult? Resolve<TResult>(
        MapMarkerEntry entry,
        Func<MapMarkerIconPayload, TResult?> materialize)
        where TResult : class
    {
        var archive = GetArchiveIcon(entry);
        if (archive is not null)
        {
            var result = materialize(archive);
            if (result is not null)
            {
                return result;
            }
        }

        var embedded = GetEmbeddedFallback(entry);
        return embedded is null ? null : materialize(embedded);
    }

    public void Dispose()
    {
        _archiveReader?.Dispose();
        _archiveCache.Clear();
        _embeddedCache.Clear();
    }

    private MapMarkerIconPayload? GetArchiveIcon(MapMarkerEntry entry)
    {
        if (_archiveCache.TryGetValue(entry.RawValue, out var cached))
        {
            return cached;
        }

        MapMarkerIconPayload? payload = null;
        var assetPath = MapMarkerArchiveAssetCatalog.DirectIconPath(_game, entry);
        if (assetPath is not null && _readArchiveFile is not null)
        {
            try
            {
                var bytes = _readArchiveFile(assetPath);
                if (bytes is { Length: > 0 })
                {
                    payload = new MapMarkerIconPayload(
                        bytes,
                        MapMarkerIconPayloadFormat.Dds,
                        MapMarkerIconPayloadSource.GameArchive);
                }
            }
            catch
            {
                // Per-entry extraction/decompression failure falls through to the embedded PNG.
            }
        }

        _archiveCache[entry.RawValue] = payload;
        return payload;
    }

    private MapMarkerIconPayload? GetEmbeddedFallback(MapMarkerEntry entry)
    {
        if (_embeddedCache.TryGetValue(entry.RawValue, out var cached))
        {
            return cached;
        }

        MapMarkerIconPayload? payload = null;
        if (!string.IsNullOrEmpty(entry.IconKey))
        {
            var bytes = _readEmbeddedPng(entry.IconKey);
            if (bytes is { Length: > 0 })
            {
                payload = new MapMarkerIconPayload(
                    bytes,
                    MapMarkerIconPayloadFormat.Png,
                    MapMarkerIconPayloadSource.EmbeddedFallback);
            }
        }

        _embeddedCache[entry.RawValue] = payload;
        return payload;
    }
}

/// <summary>Pixel helpers shared by the archive materializer and pure tests.</summary>
internal static class MapMarkerIconPixels
{
    /// <summary>Returns a premultiplied RGBA copy suitable for Win2D's default alpha mode.</summary>
    internal static byte[] PremultiplyRgba(byte[] straightRgba)
    {
        var result = (byte[])straightRgba.Clone();
        for (var i = 0; i + 3 < result.Length; i += 4)
        {
            var alpha = result[i + 3];
            if (alpha == byte.MaxValue)
            {
                continue;
            }

            result[i] = (byte)((result[i] * alpha + 127) / 255);
            result[i + 1] = (byte)((result[i + 1] * alpha + 127) / 255);
            result[i + 2] = (byte)((result[i + 2] * alpha + 127) / 255);
        }

        return result;
    }
}
