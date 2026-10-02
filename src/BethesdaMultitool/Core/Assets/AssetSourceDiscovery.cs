using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;

namespace BethesdaMultitool.Core.Assets;

/// <summary>One BMT discovery policy for selected world and actor views. Never infers engine activation.</summary>
internal static class AssetSourceDiscovery
{
    internal static BsaDiscoveryResult Discover(string primaryPath, IEnumerable<string>? additionalPaths = null,
        IEnumerable<string>? donorDirectories = null, bool includeLooseFiles = false,
        CancellationToken cancellationToken = default)
    {
        var directories = new List<(string Path, string Role, string Origin)>();
        AddFile(primaryPath, "primary");
        foreach (var path in additionalPaths ?? []) AddFile(path, "selected-source");
        foreach (var path in donorDirectories ?? []) directories.Add((Path.GetFullPath(path), "donor", path));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mesh = new List<AssetMount>();
        var texture = new List<AssetMount>();
        var loose = new List<AssetMount>();
        foreach (var (path, role, origin) in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(path)) continue;
            var discovery = BsaDiscovery.DiscoverInDirectory(path);
            // Discovery can descend from an install root into Data; loose paths use the same root.
            var effective = discovery.MeshesBsaPaths.Concat(discovery.TexturesBsaPaths)
                .Select(Path.GetDirectoryName).FirstOrDefault() ?? path;
            if (includeLooseFiles && Directory.Exists(effective))
                loose.Add(new(effective, AssetMountKind.LooseDirectory, role, origin));
            mesh.AddRange(discovery.MeshesBsaPaths.Select(p => new AssetMount(p, AssetMountKind.Archive, role, origin)));
            texture.AddRange(discovery.TexturesBsaPaths.Select(p => new AssetMount(p, AssetMountKind.Archive, role, origin)));
        }
        const string policy = "bmt-selected-loose-archives-then-donors-v1";
        IEnumerable<AssetMount> Ordered(IEnumerable<AssetMount> archives) =>
            loose.Where(m => m.Role != "donor").Concat(archives.Where(m => m.Role != "donor"))
                .Concat(loose.Where(m => m.Role == "donor")).Concat(archives.Where(m => m.Role == "donor"));
        var meshPlan = new AssetSourcePlan(Ordered(mesh), policy);
        var texturePlan = new AssetSourcePlan(Ordered(texture), policy);
        return new(meshPlan.Mounts.Where(m => m.Kind == AssetMountKind.Archive).Select(m => m.Path).ToArray(),
            texturePlan.Mounts.Where(m => m.Kind == AssetMountKind.Archive).Select(m => m.Path).ToArray(), true)
            { MeshPlan = meshPlan, TexturePlan = texturePlan };

        void AddFile(string path, string role)
        {
            var full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is { } dir) directories.Add((dir, role, full));
        }
    }
}
