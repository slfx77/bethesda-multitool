using System.Text.Json;
using System.Text.Json.Nodes;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Joins capture-time engine slot queries to verified isolated backing files.</summary>
internal sealed class RuntimePluginNamespace
{
    private readonly Dictionary<string, RuntimeProfileFile> _candidates;
    private readonly Dictionary<string, RuntimeBackingFile>? _guestCandidates;
    private readonly string _backend = "xnvse";
    private readonly string _queryEvidence = "engine-get-mod-index-and-count";

    internal RuntimePluginNamespace(RuntimeRunProfile profile)
    {
        var directory = profile.Isolation is null ? null : Path.Combine(profile.Isolation.GameCopy, "Data");
        _candidates = profile.Files.Where(f => directory is not null &&
                string.Equals(Path.GetDirectoryName(f.CopyPath), directory, StringComparison.OrdinalIgnoreCase) &&
                Path.GetExtension(f.CopyPath) is { } extension &&
                (extension.Equals(".esm", StringComparison.OrdinalIgnoreCase) || extension.Equals(".esp", StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(f => Path.GetFileName(f.CopyPath), StringComparer.OrdinalIgnoreCase);
    }

    internal RuntimePluginNamespace(RuntimeGuestManifest manifest)
    {
        _guestCandidates = manifest.Plugins.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        _candidates = manifest.Plugins.ToDictionary(f => f.Name,
            f => new RuntimeProfileFile(f.Path, f.Path, f.Sha256), StringComparer.OrdinalIgnoreCase);
        _backend = "xenia-canary";
        _queryEvidence = "validated-guest-compiled-file-table";
    }

    internal string StartPayload(string session)
    {
        if (_candidates.Count is 0 or > 255 || _candidates.Keys.Any(name => name.Length > 255 ||
            name.Any(c => c < 32 || c >= 127 || c is '"' or '\\' or '/' or ':'))) return session;
        return session + "\n" + string.Join('\n', _candidates.Keys.Order(StringComparer.OrdinalIgnoreCase));
    }

    internal async Task<JsonObject> EnrichAsync(JsonElement identity, JsonElement? query, CancellationToken token)
    {
        var result = JsonNode.Parse(identity.GetRawText())!.AsObject();
        result["activePluginIdentityStatus"] = "unavailable";
        result["activePlugins"] = new JsonArray();
        if (RuntimeTraceDocument.Text(identity, "backend") != _backend) return result;
        if (query is not { ValueKind: JsonValueKind.Object } value ||
            RuntimeTraceDocument.Text(value, "evidence") != _queryEvidence ||
            !value.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String) return result;
        if (status.GetString() == "partial") { result["activePluginIdentityStatus"] = "partial"; return result; }
        if (status.GetString() != "complete" ||
            !value.TryGetProperty("count", out var countValue) || countValue.ValueKind != JsonValueKind.Number ||
            !countValue.TryGetInt32(out var count) || count is < 1 or > 255 ||
            !value.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != count)
            return result;
        var plugins = new JsonArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedIndex = 0;
        result["activePluginIdentityStatus"] = "partial";
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("index", out var slot) ||
                slot.ValueKind != JsonValueKind.Number || !slot.TryGetInt32(out var index) || index != expectedIndex++ ||
                !entry.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
                nameValue.GetString() is not { } name || !names.Add(name) || !_candidates.TryGetValue(name, out var file)) return result;
            string hash;
            try
            {
                if (_guestCandidates is not null)
                {
                    var pinned = _guestCandidates[name];
                    if (!RuntimeGuestManifest.MatchesObservedBacking(pinned, entry) ||
                        !RuntimeGuestManifest.SameBacking(pinned, await RuntimeGuestManifest.ReadBackingAsync(pinned.Path, name, token)))
                        return result;
                    hash = pinned.Sha256;
                }
                else hash = await RuntimeIsolationService.Hash(file.CopyPath, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return result; }
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return result;
            plugins.Add(new JsonObject { ["index"] = index, ["name"] = name, ["sha256"] = hash,
                ["hashScope"] = _guestCandidates is null ? "verified-isolated-plugin-file-on-disk" : "verified-guest-open-handle-and-pinned-file",
                ["status"] = "verified", ["nameEvidence"] = _queryEvidence,
                ["backingPath"] = file.CopyPath,
                ["backingObservation"] = _guestCandidates is null ? null : JsonNode.Parse(entry.GetProperty("backingFiles")[0].GetRawText()) });
        }
        result["activePluginIdentityStatus"] = "complete";
        result["activePlugins"] = plugins;
        return result;
    }
}
