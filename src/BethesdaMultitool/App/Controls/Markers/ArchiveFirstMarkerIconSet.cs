using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Export.Map;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Games;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

namespace BethesdaMultitool;

/// <summary>
///     Per-game marker icons loaded from the installed game's directly decodable DDS assets first, with
///     the bundled PNGs as a per-icon fallback. FO3/FNV and Oblivion ship individual DDS icons; Skyrim,
///     FO4, and FO76 ship vector sprites inside SWFs, which the current runtime cannot rasterize, so those
///     games take the embedded fallback without opening an archive. Decoded once on construction.
/// </summary>
internal sealed class ArchiveFirstMarkerIconSet : IMapMarkerIconSet
{
    private readonly Dictionary<int, CanvasBitmap> _icons = new();

    internal ArchiveFirstMarkerIconSet(
        BethesdaGame game,
        ICanvasResourceCreator resourceCreator,
        IEnumerable<string?> dataSourcePaths)
    {
        Game = game;
        RequiresTinting = GameProfiles.For(game).MarkersAreTinted;

        var archiveCount = 0;
        var fallbackCount = 0;
        using var provider = new ArchiveFirstMapMarkerIconProvider(game, dataSourcePaths);
        foreach (var entry in MapMarkerCatalog.For(game))
        {
            var loaded = provider.Resolve(entry, payload =>
            {
                var bitmap = TryCreateBitmap(game, resourceCreator, payload);
                return bitmap is null ? null : new LoadedIcon(bitmap, payload.Source);
            });
            if (loaded is null)
            {
                continue;
            }

            _icons[entry.RawValue] = loaded.Bitmap;
            if (loaded.Source == MapMarkerIconPayloadSource.GameArchive)
            {
                archiveCount++;
            }
            else
            {
                fallbackCount++;
            }
        }

        Logger.Instance.Info(
            "Map marker icons {0}: {1} archive, {2} embedded fallback, {3} glyph fallback (archive: {4}).",
            game,
            archiveCount,
            fallbackCount,
            MapMarkerCatalog.For(game).Count - _icons.Count,
            provider.ArchivePath ?? "none");
    }

    public BethesdaGame Game { get; }
    public bool RequiresTinting { get; }
    public IReadOnlyDictionary<int, CanvasBitmap> Icons => _icons;

    public void Dispose()
    {
        foreach (var bmp in _icons.Values)
        {
            bmp.Dispose();
        }

        _icons.Clear();
    }

    /// <summary>
    ///     True for games whose retail marker DDS carries transparent padding the draw path would
    ///     otherwise normalize over, shrinking the visible glyph.
    ///     <para>
    ///         Scoped to FO3/FNV deliberately, per the project's per-game-explicit-switch rule.
    ///         Oblivion takes the SAME archive-DDS branch (11/11 catalog paths resolve against a real
    ///         install), but its padding is unmeasured, its parchment tiles are not guaranteed square —
    ///         so a trim could change <c>iconAspect</c>, which is inert for FNV's square icons but not
    ///         for Oblivion's — and its <c>MarkerIconScale = 1.5</c> / <c>MarkerMinScreenScale = 0.55</c>
    ///         were hand-tuned against the embedded 32×32 PNG thirteen days BEFORE it moved to archive
    ///         DDS. Widen this only after measuring the Oblivion art.
    ///     </para>
    /// </summary>
    private static bool TrimsArchiveIconPadding(BethesdaGame game) =>
        game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas;

    private static CanvasBitmap? TryCreateBitmap(
        BethesdaGame game,
        ICanvasResourceCreator resourceCreator,
        MapMarkerIconPayload payload)
    {
        try
        {
            if (payload.Format == MapMarkerIconPayloadFormat.Dds)
            {
                var decoded = NifTextureLoader.DecodeTextureData(payload.Bytes);
                if (decoded is null)
                {
                    return null;
                }

                // Trim the retail icon's transparent border so the bitmap's own SizeInPixels becomes the
                // corrected source of truth. Every consumer — live draw, PNG export, hover/selection
                // outline, hit radius, accessibility bounds — already sizes off that, so nothing
                // downstream needs to change. Embedded fallback art is already tight, so restrict this
                // to archive payloads.
                var pixels = decoded.Pixels;
                var width = decoded.Width;
                var height = decoded.Height;
                if (payload.Source == MapMarkerIconPayloadSource.GameArchive &&
                    TrimsArchiveIconPadding(game))
                {
                    (pixels, width, height) = MapMarkerIconPixels.CropToOpaqueBounds(pixels, width, height);
                }

                var premultiplied = MapMarkerIconPixels.PremultiplyRgba(pixels);
                return CanvasBitmap.CreateFromBytes(
                    resourceCreator,
                    premultiplied,
                    width,
                    height,
                    DirectXPixelFormat.R8G8B8A8UIntNormalized);
            }

            using var stream = new MemoryStream(payload.Bytes);
            var loadTask = CanvasBitmap.LoadAsync(resourceCreator, stream.AsRandomAccessStream()).AsTask();
            Core.Orchestration.NonPumpingWait.Wait(loadTask);
            return loadTask.GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    private sealed record LoadedIcon(CanvasBitmap Bitmap, MapMarkerIconPayloadSource Source);
}
