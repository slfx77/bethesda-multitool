namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

internal static class ReferenceDiskCachePaths
{
    internal static string ResolveDefaultCacheDirectory(string cacheName, int decoderVersion)
    {
        return Path.Combine(
            Path.GetTempPath(),
            "BethesdaMultitool",
            cacheName,
            $"v{decoderVersion}");
    }
}
