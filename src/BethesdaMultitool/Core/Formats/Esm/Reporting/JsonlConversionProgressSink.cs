using System.Text;
using System.Text.Json;

namespace BethesdaMultitool.Core.Formats.Esm.Reporting;

/// <summary>
///     Writes the conversion event stream as one stable JSON object per line. This is the
///     machine-readable authority for corpus gates; console text remains human-facing and
///     may wrap, localize numeric separators, or change wording.
/// </summary>
public sealed class JsonlConversionProgressSink : IConversionProgressSink, IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public JsonlConversionProgressSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(
            new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    public void OnPhaseStart(string phase, int? totalItems)
    {
        Write(json =>
        {
            WriteEnvelope(json, "PhaseStart");
            json.WriteString("Phase", phase);
            if (totalItems.HasValue)
            {
                json.WriteNumber("TotalItems", totalItems.Value);
            }
            else
            {
                json.WriteNull("TotalItems");
            }
        });
    }

    public void OnEvent(ConversionProgressEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        Write(json =>
        {
            WriteEnvelope(json, "Event");
            json.WriteString("Timestamp", evt.Timestamp);
            json.WriteString("Severity", evt.Severity.ToString());
            json.WriteString("Phase", evt.Phase);
            WriteNullableString(json, "FormType", evt.FormType);
            WriteNullableString(json, "FormId", evt.FormId.HasValue ? $"0x{evt.FormId.Value:X8}" : null);
            WriteNullableString(json, "Code", evt.Code);
            json.WriteString("Message", evt.Message);
            if (evt.Metadata is not null)
            {
                json.WriteStartObject("Metadata");
                foreach (var (key, value) in evt.Metadata.OrderBy(
                             pair => pair.Key, StringComparer.Ordinal))
                {
                    WriteNullableString(json, key, value);
                }

                json.WriteEndObject();
            }
        });
    }

    public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats)
    {
        ArgumentNullException.ThrowIfNull(partialStats);
        Write(json =>
        {
            WriteEnvelope(json, "PhaseEnd");
            json.WriteString("Phase", phase);
            WriteRecordCounts(json, partialStats);
        });
    }

    public void OnComplete(ConversionPipelineStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        Write(json =>
        {
            WriteEnvelope(json, "Complete");
            WriteRecordCounts(json, stats);
            json.WriteNumber("OverridesEmitted", stats.OverridesEmitted);
            json.WriteNumber("NewRecordsEmitted", stats.NewRecordsEmitted);
            json.WriteNumber("CellsMerged", stats.CellsMerged);
            json.WriteNumber("Warnings", stats.Warnings);
            json.WriteNumber("Errors", stats.Errors);
            json.WriteNumber("OutputBytes", stats.OutputBytes);
            json.WriteNumber("ElapsedMilliseconds", stats.Elapsed.TotalMilliseconds);
            WriteCountMap(json, "EmittedByType", stats.EmittedByType);
            WriteCountMap(json, "SkippedByType", stats.SkippedByType);
            WriteCountMap(json, "DropReasonCounts", stats.DropReasonCounts);
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }

    private void Write(Action<Utf8JsonWriter> writeProperties)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                writeProperties(json);
                json.WriteEndObject();
            }

            _writer.WriteLine(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)));
        }
    }

    private static void WriteEnvelope(Utf8JsonWriter json, string kind)
    {
        json.WriteNumber("SchemaVersion", 1);
        json.WriteString("Kind", kind);
    }

    private static void WriteRecordCounts(Utf8JsonWriter json, ConversionPipelineStats stats)
    {
        json.WriteNumber("RecordsConsidered", stats.RecordsConsidered);
        json.WriteNumber("RecordsEmitted", stats.RecordsEmitted);
        json.WriteNumber("RecordsSkipped", stats.RecordsSkipped);
        json.WriteNumber("RecordsFailed", stats.RecordsFailed);
    }

    private static void WriteNullableString(Utf8JsonWriter json, string propertyName, string? value)
    {
        if (value is null)
        {
            json.WriteNull(propertyName);
        }
        else
        {
            json.WriteString(propertyName, value);
        }
    }

    private static void WriteCountMap(
        Utf8JsonWriter json,
        string propertyName,
        IReadOnlyDictionary<string, int> values)
    {
        json.WriteStartObject(propertyName);
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            json.WriteNumber(key, value);
        }
        json.WriteEndObject();
    }
}
