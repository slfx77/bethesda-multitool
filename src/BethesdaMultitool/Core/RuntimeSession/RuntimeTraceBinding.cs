using System.Security.Cryptography;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeSourcePlugin(int Index, string Name, string Sha256);

/// <summary>Explicit inspected source order. A directory scan never supplies this order.</summary>
public sealed record RuntimeTraceSources(IReadOnlyList<RuntimeSourcePlugin> Plugins, bool Complete)
{
    public static async Task<RuntimeTraceSources> ReadAsync(IReadOnlyList<string> paths, bool complete,
        CancellationToken token = default)
    {
        if (!complete || paths.Count is 0 or > 255) return new([], false);
        var entries = new List<RuntimeSourcePlugin>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (!names.Add(name)) throw new InvalidDataException("Duplicate plugin name in inspected source order.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            entries.Add(new(entries.Count, name, hash));
        }
        return new(entries.AsReadOnly(), true);
    }
}

public sealed record RuntimeTraceBinding(string Status, string Reason)
{
    public bool Matched => Status == "Matched";

    /// <summary>Only a complete, identical namespace enables joins; old traces remain inspectable.</summary>
    public static RuntimeTraceBinding Match(RuntimeTraceDocument trace, RuntimeTraceSources? sources)
    {
        if (sources is not { Complete: true } || sources.Plugins.Count == 0)
            return new("Unbound", "The inspected source does not provide a complete plugin order.");
        var identity = trace.PluginIdentity;
        if (identity.ValueKind != JsonValueKind.Object ||
            RuntimeTraceDocument.Text(identity, "activePluginIdentityStatus") != "complete" ||
            !identity.TryGetProperty("activePlugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
            return new("Unbound", "The trace has no complete capture-time plugin identity.");
        if (plugins.GetArrayLength() != sources.Plugins.Count)
            return new("Mismatch", "The trace and inspected source have different plugin counts.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slot = 0;
        foreach (var plugin in plugins.EnumerateArray())
        {
            if (plugin.ValueKind != JsonValueKind.Object ||
                !plugin.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number ||
                !index.TryGetInt32(out var actualIndex) || actualIndex != slot ||
                RuntimeTraceDocument.Text(plugin, "status") != "verified" ||
                RuntimeTraceDocument.Text(plugin, "name") is not { } name || !seen.Add(name) ||
                RuntimeTraceDocument.Text(plugin, "sha256") is not { } hash || !IsSha256(hash) ||
                string.IsNullOrWhiteSpace(RuntimeTraceDocument.Text(plugin, "hashScope")))
                return new("Unbound", "The captured plugin identity is incomplete or has ambiguous slots.");
            var source = sources.Plugins[slot++];
            if (source.Index != actualIndex || !name.Equals(source.Name, StringComparison.OrdinalIgnoreCase) ||
                !hash.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                return new("Mismatch", "The plugin slot, name or SHA-256 differs from the inspected source.");
        }
        return new("Matched", "Every captured plugin slot, name and SHA-256 matches the inspected source order.");
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}
