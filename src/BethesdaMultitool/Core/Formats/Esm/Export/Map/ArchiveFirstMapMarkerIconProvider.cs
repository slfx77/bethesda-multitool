using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Vfs;

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
                "Fallout - Textures.bsa", [FalloutIconPattern], true),
            [BethesdaGame.FalloutNewVegas] = new(
                "Fallout - Textures2.bsa", [FalloutIconPattern], true),
            [BethesdaGame.Oblivion] = new(
                "Oblivion - Textures - Compressed.bsa",
                [@"textures\menus\map\world\world_map_*.dds"],
                true),
            [BethesdaGame.Skyrim] = new(
                "Skyrim - Interface.bsa", [@"interface\map.swf"], false),
            [BethesdaGame.Fallout4] = new(
                "Fallout4 - Interface.ba2",
                [@"Interface\MapMarkers.swf", @"Interface\Pipboy_MapPage.swf"],
                false),
            [BethesdaGame.Fallout76] = new(
                "SeventySix - Interface.ba2",
                [@"interface\mapmarkerslibrary.swf"],
                false)
        };

    private static readonly Dictionary<int, string> OblivionIconPaths =
        new()
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

    internal static MapMarkerArchiveAssetPlan? For(BethesdaGame game)
    {
        return Plans.GetValueOrDefault(game);
    }

    /// <summary>Returns a directly decodable DDS path for this catalog entry, or null for SWF-only art.</summary>
    internal static string? DirectIconPath(BethesdaGame game, MapMarkerEntry entry)
    {
        return game switch
        {
            BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas
                when entry.IconKey.StartsWith("icon_map_", StringComparison.OrdinalIgnoreCase) =>
                $@"textures\interface\icons\world map\{entry.IconKey}.dds",
            BethesdaGame.Oblivion when OblivionIconPaths.TryGetValue(entry.RawValue, out var path) => path,
            _ => null
        };
    }

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
    private readonly Dictionary<int, MapMarkerIconPayload?> _archiveCache = new();
    private readonly ArchiveLease? _archiveLease;
    private readonly Dictionary<int, MapMarkerIconPayload?> _embeddedCache = new();
    private readonly BethesdaGame _game;
    private readonly Func<string, byte[]?>? _readArchiveFile;
    private readonly Func<string, byte[]?> _readEmbeddedPng;

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
            // Shared handle: icon sets rebuild repeatedly against the same Interface/Textures
            // archive, which the viewer often holds open too — the registry dedups the parse.
            _archiveLease = ArchiveHandleRegistry.Shared.Acquire(ArchivePath);
            _readArchiveFile = _archiveLease.Reader.ReadFile;
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

    public void Dispose()
    {
        _archiveLease?.Dispose();
        _archiveCache.Clear();
        _embeddedCache.Clear();
    }

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
    /// <summary>
    ///     Crops straight (non-premultiplied) RGBA to the bounding box of its non-transparent texels.
    ///     <para>
    ///         Retail FO3/FNV ship each map marker as a 64×64 DDS whose ink occupies only a centred
    ///         35×35 box (measured across all 15 FNV and all 15 FO3 icons: fill 0.547). The bundled PNGs
    ///         they replaced were 35×35 with ink filling the whole image, and every downstream consumer
    ///         normalizes the drawn height over the FULL source extent — so moving to archive art shrank
    ///         the visible glyph to 0.547× linear (16.0 → 8.75 DIP) at every zoom.
    ///     </para>
    ///     <para>
    ///         The engine crops rather than scales: FNV's <c>MapMarkerTemplate</c> in
    ///         <c>menus\main\map_menu.xml</c> sets <c>zoom = width/34*100</c> and
    ///         <c>cropx = cropy = zoom/100*14</c>, i.e. 14 texels off each edge of the 64×64 to display
    ///         the centred 34×34 window. Deriving the box from the art rather than hard-coding 14 keeps
    ///         this correct for mods that ship differently-padded icons.
    ///     </para>
    ///     <para>
    ///         Returns the source array REFERENCE-EQUAL when the ink already touches every edge or when
    ///         nothing clears <paramref name="alphaThreshold" />, so callers can cheaply detect a no-op
    ///         and so a fully transparent icon is never cropped to zero.
    ///     </para>
    /// </summary>
    /// <param name="alphaThreshold">
    ///     Minimum alpha counted as ink. 8 mirrors the hard-transparent cutoff the texture decoder
    ///     already uses for cutout detection, so a DXT1 punch-through fringe does not defeat the trim.
    /// </param>
    internal static (byte[] Pixels, int Width, int Height) CropToOpaqueBounds(
        byte[] straightRgba, int width, int height, byte alphaThreshold = 8)
    {
        if (width <= 0 || height <= 0 || straightRgba.Length < width * height * 4)
        {
            return (straightRgba, width, height);
        }

        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (straightRgba[row + x * 4 + 3] < alphaThreshold) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        // Fully transparent, or already tight against all four edges — nothing to do either way.
        if (maxX < 0 || (minX == 0 && minY == 0 && maxX == width - 1 && maxY == height - 1))
        {
            return (straightRgba, width, height);
        }

        var croppedWidth = maxX - minX + 1;
        var croppedHeight = maxY - minY + 1;
        var cropped = new byte[croppedWidth * croppedHeight * 4];
        for (var y = 0; y < croppedHeight; y++)
        {
            Buffer.BlockCopy(
                straightRgba,
                ((minY + y) * width + minX) * 4,
                cropped,
                y * croppedWidth * 4,
                croppedWidth * 4);
        }

        return (cropped, croppedWidth, croppedHeight);
    }

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
