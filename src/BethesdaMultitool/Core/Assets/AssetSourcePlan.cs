using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace BethesdaMultitool.Core.Assets;

internal enum AssetMountKind { Archive, LooseDirectory }

/// <summary>A declared source, not evidence of engine archive activation or priority.</summary>
internal sealed record AssetMount(string Path, AssetMountKind Kind, string Role, string? Origin = null)
{
    internal long? SourceLength { get; init; }
    internal long? SourceWriteTicks { get; init; }
}

/// <summary>Immutable BMT priority order, kept separate from plugin record order.</summary>
internal sealed class AssetSourcePlan
{
    internal const string DeclaredOrderPolicy = "bmt-declared-order-v1";

    internal AssetSourcePlan(IEnumerable<AssetMount> mounts, string policy = DeclaredOrderPolicy)
    {
        Policy = policy;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Mounts = mounts.Select(Snapshot)
            .Where(m => seen.Add($"{m.Kind}:{m.Path}" )).ToImmutableArray();
        Identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            policy + "\n" + string.Join("\n", Mounts.Select(m =>
                $"{m.Kind}|{m.Path}|{m.Role}|{m.Origin}|{m.SourceLength}|{m.SourceWriteTicks}")))));
    }

    internal string Policy { get; }
    internal string Identity { get; }
    internal ImmutableArray<AssetMount> Mounts { get; }
    internal bool EnginePriorityVerified => false;

    internal static AssetSourcePlan FromPaths(IEnumerable<string> paths) => new(paths.Select(path =>
        new AssetMount(path, Directory.Exists(path) ? AssetMountKind.LooseDirectory : AssetMountKind.Archive,
            "explicit")));

    private static AssetMount Snapshot(AssetMount mount)
    {
        var info = new FileInfo(System.IO.Path.GetFullPath(mount.Path));
        return mount with { Path = info.FullName,
            SourceLength = mount.Kind == AssetMountKind.Archive && info.Exists ? info.Length : null,
            SourceWriteTicks = mount.Kind == AssetMountKind.Archive && info.Exists ? info.LastWriteTimeUtc.Ticks : null };
    }
}
