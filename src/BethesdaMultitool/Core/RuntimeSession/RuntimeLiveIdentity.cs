using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Explicit loaded-order refresh, with hashes cached by backing path, length and modification time.</summary>
internal sealed class RuntimeLiveIdentity
{
    private readonly Func<JsonElement, (string MappedExecutable, string DataDirectory)> _resolve;
    private readonly Func<string, CancellationToken, Task<string>> _hash;
    private readonly Dictionary<string, CachedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private sealed record CachedFile(long Length, DateTime ModifiedUtc, string Sha256);

    internal RuntimeLiveIdentity(Func<JsonElement, (string MappedExecutable, string DataDirectory)>? resolve = null,
        Func<string, CancellationToken, Task<string>>? hash = null)
    {
        _resolve = resolve ?? ResolveBackingDirectory;
        _hash = hash ?? HashAsync;
    }

    internal async Task<JsonElement> RefreshAsync(JsonElement identity,
        Func<JsonElement, CancellationToken, Task<JsonElement>> send, CancellationToken token)
    {
        var result = JsonNode.Parse(identity.GetRawText())!.AsObject();
        var plugins = new JsonArray();
        result["activePlugins"] = plugins;
        result["activePluginIdentityStatus"] = "unavailable";
        result.Remove("activePluginIdentityReason");
        result.Remove("activePluginCount");
        result["identityRefreshedUtc"] = DateTimeOffset.UtcNow.ToString("O");
        result["pluginHashCache"] = new JsonObject { ["hashed"] = 0, ["reused"] = 0 };
        try
        {
            var loaded = await send(RuntimeLiveRequest.Parse("{\"op\":\"snapshot\",\"fields\":[\"player.cell\"]}"), token);
            if (!loaded.TryGetProperty("snapshot", out var snapshot) || !snapshot.TryGetProperty("player.cell", out var cell) ||
                cell.ValueKind != JsonValueKind.Object || Text(cell, "status") != "observed")
                return Finish("unavailable", "no-loaded-player-cell");
            var backing = _resolve(identity);
            result["executableMappedFile"] = backing.MappedExecutable;
            result["activePluginDirectory"] = backing.DataDirectory;
            if (!Count(await Evaluate("GetNumLoadedMods"), out var count))
                return Finish("unavailable", "engine-mod-count-unavailable");
            result["activePluginCount"] = count;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var complete = true;
            var hashed = 0;
            var reused = 0;
            for (var index = 0; index < count; ++index)
            {
                var value = await Evaluate("GetNthModName " + index.ToString(CultureInfo.InvariantCulture));
                var plugin = new JsonObject { ["index"] = index, ["status"] = "unavailable",
                    ["nameEvidence"] = "engine-get-nth-mod-name-and-count" };
                plugins.Add(plugin);
                if (!Value(value, "string", out var element) || !element.TryGetProperty("complete", out var full) ||
                    full.ValueKind != JsonValueKind.True || Text(element, "value") is not { } name || !ValidName(name) || !names.Add(name))
                {
                    plugin["reason"] = "engine-mod-name-unavailable-or-invalid";
                    complete = false;
                    continue;
                }
                plugin["name"] = name;
                var path = Path.Combine(backing.DataDirectory, name);
                plugin["backingPath"] = path;
                try
                {
                    var before = new FileInfo(path);
                    var length = before.Length;
                    var modified = before.LastWriteTimeUtc;
                    string sha256;
                    if (_files.TryGetValue(path, out var cached) && cached.Length == length && cached.ModifiedUtc == modified)
                    {
                        sha256 = cached.Sha256;
                        ++reused;
                    }
                    else
                    {
                        sha256 = await _hash(path, token);
                        var after = new FileInfo(path);
                        if (after.Length != length || after.LastWriteTimeUtc != modified)
                            throw new IOException("Plugin changed while its hash was being read.");
                        _files[path] = new(length, modified, sha256);
                        ++hashed;
                    }
                    plugin["sha256"] = sha256;
                    plugin["length"] = length;
                    plugin["lastWriteUtc"] = modified.ToString("O");
                    plugin["hashScope"] = "mapped-executable-data-directory-file-on-disk";
                    plugin["status"] = "observed";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    _files.Remove(path);
                    plugin["reason"] = error.Message;
                    complete = false;
                }
            }
            result["pluginHashCache"] = new JsonObject { ["hashed"] = hashed, ["reused"] = reused };
            if (!Count(await Evaluate("GetNumLoadedMods"), out var finalCount) || finalCount != count)
                return Finish("partial", "engine-mod-count-changed-or-unavailable");
            return Finish(complete ? "complete" : "partial", complete ? null : "some-plugin-identities-unavailable");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                                     System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            return Finish(plugins.Count == 0 ? "unavailable" : "partial", error.Message);
        }

        async Task<JsonElement> Evaluate(string expression) => await send(Element(new JsonObject
            { ["op"] = "eval", ["expression"] = expression }), token);
        JsonElement Finish(string status, string? reason)
        {
            result["activePluginIdentityStatus"] = status;
            if (reason is not null) result["activePluginIdentityReason"] = reason;
            return Element(result);
        }
    }

    private static bool Value(JsonElement response, string type, out JsonElement value)
    {
        value = default;
        return Text(response, "status") == "completed" && response.TryGetProperty("result", out value) &&
               value.ValueKind == JsonValueKind.Object && Text(value, "type") == type;
    }

    private static bool Count(JsonElement response, out int count)
    {
        count = 0;
        if (!Value(response, "number", out var result) || !result.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number) || number < 1 || number > 255 || Math.Floor(number) != number) return false;
        count = (int)number;
        return true;
    }

    private static bool ValidName(string name) => name.Length is > 0 and <= 255 &&
        name.All(c => c >= 32 && c < 127 && c is not ('/' or '\\' or ':' or '"' or '*' or '?' or '<' or '>' or '|')) &&
        (Path.GetExtension(name).Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
         Path.GetExtension(name).Equals(".esp", StringComparison.OrdinalIgnoreCase));

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static (string, string) ResolveBackingDirectory(JsonElement identity)
    {
        using var process = Process.GetProcessById(identity.GetProperty("processId").GetInt32());
        var module = process.MainModule ?? throw new IOException("Engine main module is unavailable.");
        var mapped = RuntimeModuleIdentity.MappedFile(process, module);
        var readable = RuntimeModuleIdentity.ReadableMappedPath(mapped);
        return (mapped, Path.Combine(Path.GetDirectoryName(readable)!, "Data"));
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
    }

    private static JsonElement Element(JsonNode node)
    {
        using var json = JsonDocument.Parse(node.ToJsonString());
        return json.RootElement.Clone();
    }
}
