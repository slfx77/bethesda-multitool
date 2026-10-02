using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Analysis;

namespace BethesdaMultitool.CLI.Shared;

/// <summary>Durable JSONL stage timings; human progress always goes to stderr.</summary>
internal sealed class AnalysisProgressJournal : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();
    private long _sequence;

    internal AnalysisProgressJournal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _writer = new StreamWriter(new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
    }

    internal void Report(AnalysisStageEvent item)
    {
        lock (_gate)
        {
            var row = new JsonObject
            {
                ["sequence"] = ++_sequence,
                ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["stage"] = item.Stage,
                ["status"] = item.Status,
                ["elapsedSeconds"] = item.ElapsedSeconds,
                ["completed"] = item.Completed,
                ["total"] = item.Total,
                ["error"] = item.Error
            };
            _writer?.WriteLine(row.ToJsonString());
            var count = item.Total.HasValue ? $" {item.Completed}/{item.Total}" : "";
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[{item.Stage}] {item.Status}{count} ({item.ElapsedSeconds:F1}s)"));
        }
    }

    public void Dispose() => _writer?.Dispose();
}
