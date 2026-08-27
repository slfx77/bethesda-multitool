using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

/// <summary>
///     Discovers texture-BSA paths for a loaded <see cref="WorldViewData" />, walking the
///     primary <see cref="WorldViewData.SourceFilePath" /> plus every
///     <see cref="WorldViewData.AdditionalDataPaths" /> entry (typically the active Load Order).
///     Each candidate's containing folder is probed once via <see cref="BsaDiscovery" />, with
///     directory + BSA-path dedup so a DLC ESM in the same Data folder as the main game ESM
///     doesn't double-list shared archives.
///     Ordering: primary first, then load-order entries in order. <c>NifTextureLoader</c>
///     walks the sources list and returns the FIRST hit, so the primary's archives win any
///     path collision and load-order entries only fill gaps — matching the engine's
///     SArchiveList convention, where the first registered archive containing a path wins
///     (the reason ArchiveInvalidation exists). Later DLC does NOT override the primary here.
///     A DMP file has no adjacent BSAs of its own, so without <c>AdditionalDataPaths</c> the
///     world map's terrain-texture layer renders only the fallback brown. This helper is
///     the single source of truth for both the 2D map (<c>LandscapeTexturePalette</c>) and
///     the 3D viewer (<c>WorldView3DControl</c>).
/// </summary>
internal static class WorldDataBsaPathResolver
{
    private static readonly Logger Log = Logger.Instance;

    internal static string[] DiscoverTextureBsaPaths(WorldViewData data)
    {
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBsas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        AddFrom(data.SourceFilePath, "primary");
        // Defensive null guard: AdditionalDataPaths is settable, so a caller could in principle
        // null it out. Empty-list default is what we want.
        if (data.AdditionalDataPaths is not null)
        {
            var i = 0;
            foreach (var path in data.AdditionalDataPaths) AddFrom(path, $"load-order[{i++}]");
        }

        // Asset-only donor builds, in declared priority order and AFTER the load order, so an
        // explicit load-order entry still wins a path collision. These are directories, not files,
        // so they skip the GetDirectoryName step the load-order entries need.
        if (data.AssetDataDirectories is not null)
        {
            var i = 0;
            foreach (var dir in data.AssetDataDirectories) AddFromDirectory(dir, $"asset-dir[{i++}]");
        }

        return result.ToArray();

        void AddFrom(string? candidatePath, string label)
        {
            if (string.IsNullOrEmpty(candidatePath))
            {
                Log.Info("BsaResolver {0}: skipped (path empty)", label);
                return;
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(candidatePath));
            if (string.IsNullOrEmpty(dir))
            {
                Log.Info("BsaResolver {0} '{1}': skipped (no parent directory)", label, candidatePath);
                return;
            }

            AddFromDirectory(dir, label);
        }

        void AddFromDirectory(string? candidateDir, string label)
        {
            if (string.IsNullOrEmpty(candidateDir))
            {
                Log.Info("BsaResolver {0}: skipped (path empty)", label);
                return;
            }

            var dir = Path.GetFullPath(candidateDir);
            if (!seenDirs.Add(dir))
            {
                Log.Info("BsaResolver {0} '{1}': dir already probed", label, dir);
                return;
            }

            var discovery = BsaDiscovery.DiscoverInDirectory(dir);
            if (discovery.TexturesBsaPaths.Length == 0)
            {
                Log.Info(
                    "BsaResolver {0} '{1}': BsaDiscovery returned 0 texture BSAs ({2} mesh BSA(s)). " +
                    "Archives are classified by BsaFileFlags content bits, not filename.",
                    label, dir, discovery.MeshesBsaPaths.Length);
                return;
            }

            var added = 0;
            foreach (var bsa in discovery.TexturesBsaPaths)
            {
                if (seenBsas.Add(bsa))
                {
                    result.Add(bsa);
                    added++;
                }
            }

            Log.Info("BsaResolver {0} '{1}': +{2} texture BSA(s).", label, dir, added);
        }
    }
}
