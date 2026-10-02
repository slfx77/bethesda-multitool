using System.Security.Cryptography;
using System.Text;

namespace BethesdaMultitool.Core.Assets;

/// <summary>Stat-based cache admission; payload hashes belong to actual-read receipts.</summary>
internal static class AssetCacheIdentity
{
    private const string Marker = "\u001easset=";
    internal static string PathOf(string key) => key.Split(Marker, 2, StringSplitOptions.None)[0];
    internal static string Qualify(AssetSelectionSession? session, string key, string sourcePath,
        IEnumerable<AssetSelectionReceipt>? dependencies = null)
    {
        if (session is null) return key;
        key = PathOf(key);
        var paths = new List<string> { sourcePath };
        if (new[] { ".tga", ".bmp", ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(sourcePath), StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(Path.ChangeExtension(sourcePath, ".dds"));
            paths.Add(Path.ChangeExtension(sourcePath, ".ddx"));
        }
        if (sourcePath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            paths.Add(Path.ChangeExtension(sourcePath, ".ddx"));
            if (sourcePath.EndsWith("_n.dds", StringComparison.OrdinalIgnoreCase))
                paths.Add(sourcePath[..^6] + "_s.ddx");
        }
        if (sourcePath.EndsWith("_n.ddx", StringComparison.OrdinalIgnoreCase))
            paths.Add(sourcePath[..^6] + "_s.ddx");
        var identity = new StringBuilder();
        paths.AddRange((dependencies ?? []).Select(receipt => receipt.RequestedPath));
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            identity.Append(session.StatIdentity(path));
            var receipt = session.LastReceipt(path);
            // A transient failure must be retried even when its file metadata did not change.
            if (receipt is not null && (receipt.Status == AssetSelectionStatus.Unavailable ||
                receipt.Attempts.Any(a => a.Status != "read"))) identity.Append(':').Append(receipt.Sequence);
        }
        return key + Marker + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
    }
}
