using System.Security.Cryptography;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeTraceSummary(string Sha256, string Status, bool Complete, long Events,
    ulong Dropped, long MissingSequences, long Snapshots, long Errors, IReadOnlyList<string> Diagnostics,
    long ControllerResults = 0, long ControllerErrors = 0, RuntimeScriptCallSummary? ScriptCalls = null);

/// <summary>Deterministic validation; imports preserve event order and never fill missing observations.</summary>
public static class RuntimeTraceImporter
{
    public static async Task<RuntimeTraceSummary> ImportAsync(Stream input, CancellationToken token = default)
        => (await ReadDocumentAsync(input, token)).Summary;

    /// <summary>Validates a trace and retains exact source lines for offline inspection.</summary>
    public static async Task<RuntimeTraceDocument> ReadDocumentAsync(Stream input, CancellationToken token = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var copy = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, token);
            if (count == 0) break;
            total += count;
            if (total > 256L * 1024 * 1024) throw new InvalidDataException("Runtime trace exceeds the 256 MiB import limit.");
            hash.AppendData(buffer.AsSpan(0, count));
            await copy.WriteAsync(buffer.AsMemory(0, count), token);
        }
        var bytes = copy.ToArray();
        var observations = new List<RuntimeTraceEvent>();
        var outcomes = new List<RuntimeTraceControllerResult>();
        JsonElement identity = default, pluginIdentity = default;
        var diagnostics = new List<string>();
        long events = 0, snapshots = 0, errors = 0, missing = 0;
        long controllerResults = 0, controllerErrors = 0;
        ulong dropped = 0, previous = 0;
        var header = false;
        var footer = false;
        var ended = false;
        var started = false;
        string? engineStatus = null;
        var status = "incomplete";
        var lineNumber = 0;
        var traceVersion = 0;
        RuntimeMilestoneTraceValidator? milestones = null;
        var scriptCalls = new RuntimeScriptCallValidator();
        var gamepad = new RuntimeGamepadTraceValidator();
        var combatCleanup = new RuntimeCombatCleanupValidator();
        for (var offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            var start = offset;
            var newline = Array.IndexOf(bytes, (byte)'\n', start);
            var end = newline < 0 ? bytes.Length : newline;
            offset = newline < 0 ? bytes.Length : newline + 1;
            if (end > start && bytes[end - 1] == '\r') end--;
            var length = end - start;
            lineNumber++;
            // UTF-8 BOM is permitted only at the start, as with the previous StreamReader.
            if (start == 0 && length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            { start += 3; length -= 3; }
            // Controller-enriched plugin identities may exceed the 64 KiB wire frame.
            if (length > 1024 * 1024)
                throw new InvalidDataException("Runtime trace line exceeds the record limit.");
            using var json = JsonDocument.Parse(bytes.AsMemory(start, length));
            var entry = json.RootElement;
            var kind = entry.GetProperty("kind").GetString();
            if (footer) throw new InvalidDataException("Records follow the capture footer.");
            gamepad.Observe(entry, lineNumber, start, length, missing > 0 || dropped > 0);
            // The current cleanup event itself may be the first one after a lost frame.
            // Pass that gap before validating its terminal receipt, not on the next row.
            var combatEventLoss = missing > 0 || dropped > 0 || OptionalUnsigned(entry, "dropped") is > 0 ||
                OptionalUnsigned(entry, "sequence") is { } combatSequence && previous != 0 &&
                combatSequence > previous && combatSequence - previous > 1;
            combatCleanup.Observe(entry, combatEventLoss);
            if (kind == "capture-header")
            {
                if (header || events > 0) throw new InvalidDataException("Duplicate or misplaced capture header.");
                traceVersion = entry.GetProperty("version").GetInt32();
                if (entry.GetProperty("schema").GetString() != "bmt/runtime-trace" || traceVersion is not (1 or 2 or 3))
                    throw new InvalidDataException("Unsupported trace schema.");
                if (traceVersion == 3) milestones = new RuntimeMilestoneTraceValidator(entry);
                header = true;
                if (entry.TryGetProperty("identity", out var headerIdentity))
                {
                    identity = pluginIdentity = headerIdentity.Clone();
                    if (identity.TryGetProperty("sequence", out var sequence)) previous = sequence.GetUInt64();
                }
                continue;
            }
            if (!header) throw new InvalidDataException("Capture header is missing.");
            if (kind == "action-request")
            {
                if (ended) throw new InvalidDataException("Scenario action follows capture-end.");
                milestones?.ActionRequested(entry, previous);
                continue;
            }
            if (kind == "milestone-probe")
            {
                if (milestones == null || ended || entry.TryGetProperty("protocol", out _) || entry.TryGetProperty("sequence", out _))
                    throw new InvalidDataException("Invalid milestone probe.");
                milestones.ProbeRequested(entry, previous);
                continue;
            }
            if (kind == "scenario-result")
            {
                if (traceVersion < 2 || entry.TryGetProperty("protocol", out _) || entry.TryGetProperty("sequence", out _) ||
                    entry.GetProperty("origin").GetString() != "controller")
                    throw new InvalidDataException("Invalid controller scenario result.");
                var outcome = entry.GetProperty("status").GetString();
                var milestoneId = RuntimeTraceDocument.Text(entry, "milestoneId");
                if (milestoneId != null)
                {
                    if (milestones == null) throw new InvalidDataException("Milestone results require trace version 3.");
                    milestones.Result(entry, milestoneId, pluginIdentity, observations, bytes, missing > 0 || dropped > 0);
                }
                if (outcome is not ("matched" or "unavailable" or "mismatch" or "timeout" or "native-error" or "cancelled" or "capture-ended" or "capture-stopping" or "disconnected") &&
                    !(milestoneId != null && outcome is "event-loss" or "not-run"))
                    throw new InvalidDataException("Unsupported controller scenario outcome.");
                ++controllerResults;
                if (RuntimeMilestoneMonitor.IsFailure(outcome, milestoneId != null)) { ++errors; ++controllerErrors; }
                outcomes.Add(new RuntimeTraceControllerResult(lineNumber, start, length, outcome,
                    entry.TryGetProperty("actionIndex", out var actionIndex) && actionIndex.ValueKind == JsonValueKind.Number &&
                        actionIndex.TryGetInt32(out var index) ? index : null,
                    RuntimeTraceDocument.Text(entry, "actionKind"),
                    entry.TryGetProperty("elapsedMilliseconds", out var elapsed) && elapsed.ValueKind == JsonValueKind.Number &&
                        elapsed.TryGetDouble(out var milliseconds) &&
                        double.IsFinite(milliseconds) ? milliseconds : null,
                    OptionalUnsigned(entry, "observedRequestId"), OptionalUnsigned(entry, "observedSequence"),
                    OptionalUnsigned(entry, "observedFrame"), milestoneId));
                continue; // Host observations have no native sequence/frame or event count.
            }
            if (kind == "capture-footer")
            {
                var cleanupDiagnostics = combatCleanup.GetDiagnostics();
                errors += cleanupDiagnostics.Count;
                diagnostics.AddRange(cleanupDiagnostics);
                if (combatCleanup.HasRequests && (!entry.TryGetProperty("combatCleanupDiagnostics", out var cleanupFooter) ||
                    cleanupFooter.ValueKind != JsonValueKind.Array || !cleanupFooter.EnumerateArray().Select(value =>
                        value.ValueKind == JsonValueKind.String ? value.GetString() : null).SequenceEqual(cleanupDiagnostics)))
                    diagnostics.Add("combat-cleanup-footer-mismatch");
                footer = true;
                status = entry.GetProperty("status").GetString() ?? "incomplete";
                if (entry.GetProperty("events").GetInt64() != events || entry.GetProperty("dropped").GetUInt64() != dropped ||
                    entry.GetProperty("snapshots").GetInt64() != snapshots || entry.GetProperty("errors").GetInt64() != errors)
                    diagnostics.Add("footer-count-mismatch");
                if (traceVersion >= 2 && (!entry.TryGetProperty("controllerResults", out var resultCount) || resultCount.GetInt64() != controllerResults ||
                    !entry.TryGetProperty("controllerErrors", out var errorCount) || errorCount.GetInt64() != controllerErrors))
                    diagnostics.Add("controller-footer-count-mismatch");
                if (entry.TryGetProperty("missingSequences", out var sequenceCount) && sequenceCount.GetInt64() != missing)
                    diagnostics.Add("sequence-footer-count-mismatch");
                continue;
            }
            if (ended) throw new InvalidDataException("Engine events follow capture-end.");
            if (kind == "capture-start")
            {
                if (started) throw new InvalidDataException("Duplicate engine capture-start.");
                started = true;
                if (entry.TryGetProperty("identity", out var startIdentity)) pluginIdentity = startIdentity.Clone();
            }
            if (entry.GetProperty("protocol").GetInt32() != 1) throw new InvalidDataException("Unsupported event protocol.");
            var current = entry.GetProperty("sequence").GetUInt64();
            if (current <= previous) throw new InvalidDataException("Event sequence is duplicated or out of order.");
            if (previous != 0 && current != previous + 1) missing += checked((long)(current - previous - 1));
            previous = current;
            if (kind == "capture-start") milestones?.Started(current);
            milestones?.Observe(entry, current);
            dropped = Math.Max(dropped, entry.GetProperty("dropped").GetUInt64());
            ++events;
            observations.Add(new RuntimeTraceEvent(lineNumber, start, length, kind ?? "unknown", current,
                entry.TryGetProperty("frame", out var frame) && frame.TryGetUInt64(out var frameValue) ? frameValue : null,
                RuntimeTraceDocument.FormId(entry, "engineTargetFormId"),
                RuntimeTraceDocument.FormId(entry, "engineTargetBaseFormId"),
                RuntimeTraceDocument.FormId(entry, "scriptFormId") ??
                    (RuntimeTraceDocument.Text(entry, "scriptIdentityStatus") == "Resolved"
                        ? RuntimeTraceDocument.FormId(entry, "engineScriptFormId") : null),
                RuntimeTraceDocument.FormId(entry, "secondaryFormId"),
                RuntimeTraceDocument.Text(entry, "statistic"), RuntimeTraceDocument.Text(entry, "component"),
                entry.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number &&
                    value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null,
                RuntimeTraceDocument.Text(entry, "targetKind")));
            scriptCalls.Observe(entry, observations[^1]);
            if (kind == "snapshot") ++snapshots;
            if (kind is "error" or "script-error" || kind == "action-result" &&
                entry.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.False) ++errors;
            if (kind == "capture-end")
            {
                ended = true;
                engineStatus = entry.GetProperty("status").GetString();
            }
        }
        if (!header) diagnostics.Add("missing-header");
        if (!footer) diagnostics.Add("missing-footer");
        if (!footer) diagnostics.AddRange(combatCleanup.GetDiagnostics());
        if (!started) diagnostics.Add("missing-engine-capture-start");
        if (!ended) diagnostics.Add("missing-engine-capture-end");
        if (ended && engineStatus != status) diagnostics.Add("engine-footer-status-mismatch");
        if (missing > 0) diagnostics.Add("sequence-gaps");
        if (dropped > 0) diagnostics.Add("bridge-dropped-events");
        if (milestones?.HasMissingResults == true) diagnostics.Add("missing-milestone-results");
        var traceHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        var calls = scriptCalls.Complete(traceHash, missing > 0 || dropped > 0);
        var summary = new RuntimeTraceSummary(traceHash, status,
            status == "completed" && diagnostics.Count == 0, events, dropped, missing, snapshots, errors, diagnostics,
            controllerResults, controllerErrors, calls.Summary);
        return new RuntimeTraceDocument(bytes, summary, identity, pluginIdentity, observations.AsReadOnly(), outcomes.AsReadOnly(), calls,
            gamepad.Complete(traceHash, missing > 0 || dropped > 0));
    }

    private static ulong? OptionalUnsigned(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetUInt64(out var number) ? number : null;
}
