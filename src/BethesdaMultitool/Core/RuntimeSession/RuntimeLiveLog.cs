using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Append-only compact evidence with an explicit size cap; it never removes older evidence.</summary>
public sealed class RuntimeLiveLog : IDisposable
{
    public const long DefaultMaximumBytes = 64L * 1024 * 1024;
    private readonly FileStream _file;
    private readonly long _maximumBytes;
    private readonly Lock _gate = new();
    public bool Full { get; private set; }

    public RuntimeLiveLog(string path, long maximumBytes = DefaultMaximumBytes)
    {
        if (maximumBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _maximumBytes = maximumBytes;
    }

    internal void Write(string direction, JsonElement value)
    {
        var row = new JsonObject
        {
            ["utc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["direction"] = direction,
            ["data"] = JsonNode.Parse(value.GetRawText())
        };
        if (direction == "request" && row["data"] is JsonObject request)
        {
            foreach (var field in new[] { "source", "sequence", "steps" })
            {
                if (request[field] is not { } content) continue;
                var text = field == "source" ? content.GetValue<string>() : content.ToJsonString();
                request[field + "Sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                request.Remove(field);
            }
        }
        var bytes = Encoding.UTF8.GetBytes(row.ToJsonString() + "\n");
        lock (_gate)
        {
            if (Full) return;
            if (_file.Length + bytes.Length > _maximumBytes) { Full = true; return; }
            _file.Write(bytes);
            _file.Flush();
        }
    }

    public void Dispose() { lock (_gate) _file.Dispose(); }
}
