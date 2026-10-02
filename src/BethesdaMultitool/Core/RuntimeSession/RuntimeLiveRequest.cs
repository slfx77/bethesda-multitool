using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Small JSON requests shared by the live client and the persistent broker.</summary>
public static class RuntimeLiveRequest
{
    public static string PipeName(string session)
    {
        if (string.IsNullOrWhiteSpace(session) || session.Length > 64 ||
            session.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Session must contain 1–64 ASCII letters, digits, underscores or hyphens.", nameof(session));
        return "BMT.Live." + session;
    }

    public static JsonElement Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > RuntimeProtocol.MaximumPayloadBytes)
            throw new InvalidDataException("Live request exceeds 64 KiB.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("op", out var operation) ||
            operation.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(operation.GetString()))
            throw new InvalidDataException("Live request requires a string op.");
        return value.Clone();
    }

    public static async Task<JsonElement> ReadFileAsync(string path, CancellationToken token = default)
    {
        var json = Parse(await ReadBoundedTextAsync(path, token));
        if (json.GetProperty("op").GetString() != "script.load") return json;
        var node = JsonNode.Parse(json.GetRawText())!.AsObject();
        var scriptFile = node["file"] ?? node["scriptFile"];
        if (scriptFile is null) return json;
        if (node["source"] is not null || node["file"] is not null && node["scriptFile"] is not null)
            throw new InvalidDataException("script.load accepts exactly one of source, file or scriptFile.");
        var scriptPath = Path.GetFullPath(scriptFile.GetValue<string>(), Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!Path.GetExtension(scriptPath).Equals(".gek", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Investigation script files must use the .gek extension.");
        node["source"] = await ReadBoundedTextAsync(scriptPath, token);
        node.Remove("file");
        node.Remove("scriptFile");
        return Parse(node.ToJsonString());
    }

    private static async Task<string> ReadBoundedTextAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous);
        if (stream.Length > RuntimeProtocol.MaximumPayloadBytes)
            throw new InvalidDataException("Live input exceeds 64 KiB.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token);
        return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
    }

    internal static bool IsPriority(JsonElement request) =>
        request.GetProperty("op").GetString() is "job.stop" or "script.stop";

    internal static bool IsTerminal(JsonElement response) =>
        response.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
        status.GetString() is "completed" or "cancelled" or "failed" or "disconnected" or "unavailable";

    internal static JsonElement Result(string status, string? error = null)
    {
        var node = new JsonObject { ["kind"] = "live-result", ["status"] = status };
        if (error is not null) node["error"] = error;
        using var json = JsonDocument.Parse(node.ToJsonString());
        return json.RootElement.Clone();
    }
}
