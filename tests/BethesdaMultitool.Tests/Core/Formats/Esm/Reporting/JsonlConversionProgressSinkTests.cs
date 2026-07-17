using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Reporting;

public sealed class JsonlConversionProgressSinkTests
{
    [Fact]
    public void WritesStableEventIdentityAndCompletionFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conversion-events-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var sink = new JsonlConversionProgressSink(path))
            {
                sink.OnEvent(new ConversionProgressEvent
                {
                    Timestamp = DateTimeOffset.UnixEpoch,
                    Severity = ConversionEventSeverity.Warning,
                    Phase = "References",
                    FormType = "SCPT",
                    FormId = 0x0012ABCD,
                    Code = "script.suppress-unsafe-reference-table",
                    Message = "Human wording is not the machine-readable key.",
                });
                sink.OnComplete(new ConversionPipelineStats
                {
                    RecordsConsidered = 4,
                    RecordsEmitted = 3,
                    RecordsSkipped = 1,
                    RecordsFailed = 0,
                    OutputBytes = 123,
                });
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);

            using var eventJson = JsonDocument.Parse(lines[0]);
            var evt = eventJson.RootElement;
            Assert.Equal(1, evt.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal("Event", evt.GetProperty("Kind").GetString());
            Assert.Equal("SCPT", evt.GetProperty("FormType").GetString());
            Assert.Equal("0x0012ABCD", evt.GetProperty("FormId").GetString());
            Assert.Equal("script.suppress-unsafe-reference-table", evt.GetProperty("Code").GetString());

            using var completeJson = JsonDocument.Parse(lines[1]);
            var complete = completeJson.RootElement;
            Assert.Equal("Complete", complete.GetProperty("Kind").GetString());
            Assert.Equal(0, complete.GetProperty("RecordsFailed").GetInt32());
            Assert.Equal(123, complete.GetProperty("OutputBytes").GetInt64());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
