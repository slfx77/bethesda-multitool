namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

internal static class ReferenceDiskCachePaths
{
    internal static string ResolveDefaultCacheDirectory(string cacheName, int decoderVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), "BethesdaMultitool", cacheName);
        PruneStaleVersionDirectories(root, decoderVersion);
        return Path.Combine(root, $"v{decoderVersion}");
    }

    /// <summary>
    ///     Best-effort deletion of sibling <c>v{N}</c> directories left behind by earlier decoder
    ///     versions. The version is baked into the DIRECTORY name, so every <c>DecoderVersion</c> bump
    ///     used to orphan the previous version's entire cache (multi-GB per bump, nothing ever touched
    ///     it again — <c>DiskBlobCache.Prune</c> only walks the CURRENT version's directory). Failures
    ///     are swallowed like every other prune in this cache family: a locked file (another session
    ///     mid-read on the old version) just means the orphan survives until the next launch.
    /// </summary>
    private static void PruneStaleVersionDirectories(string cacheRoot, int decoderVersion)
    {
        try
        {
            if (!Directory.Exists(cacheRoot))
            {
                return;
            }

            var current = $"v{decoderVersion}";
            foreach (var dir in Directory.EnumerateDirectories(cacheRoot, "v*"))
            {
                var name = Path.GetFileName(dir);
                if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Only recognised version directories: "v" + digits. Anything else is not ours to delete.
                var isVersionDirectory =
                    name.Length >= 2 && name[0] == 'v' && name.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
                if (!isVersionDirectory)
                {
                    continue;
                }

                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Locked by a concurrent session on the old version — retry next launch.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Enumeration failed — the cache still works, the orphan sweep just didn't run.
        }
    }
}
