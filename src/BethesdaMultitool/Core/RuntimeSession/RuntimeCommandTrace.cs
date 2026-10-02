using System.Globalization;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeCommandResult(string Status, uint? ReturnType, string ReturnTypeName,
    string? ResultBits, double? NumericValue, IReadOnlyList<string> Diagnostics);

public sealed record RuntimeCommandCallAssociation(string Status, ulong? CallId,
    RuntimeScriptCallEndpoint? Entry, RuntimeScriptCallEndpoint? Exit, IReadOnlyList<string> Diagnostics);

public sealed record RuntimeCommandObservation(int Line, int Offset, int Length, ulong Sequence, ulong? Frame,
    string Kind, string? Command, uint? Opcode, bool? HandlerReturned, ulong? RequestId, uint? ThreadId,
    uint? ScriptFormId, ulong? ScriptAddress, RuntimeCommandResult Result,
    RuntimeCommandCallAssociation ScriptCall, JsonElement Evidence);

public sealed record RuntimeCommandCoverageEvidence(int? Line, ulong? Sequence, JsonElement Evidence);

public sealed record RuntimeCommandTraceReport(string TraceSha256, string Status, bool EventLoss,
    int ObservedCalls, int PartialCalls, int AmbiguousCalls, int UnavailableCalls,
    IReadOnlyList<RuntimeCommandCoverageEvidence> Coverage,
    IReadOnlyList<RuntimeCommandObservation> Commands);

/// <summary>Typed command results and exact call attribution over immutable imported evidence.</summary>
public static class RuntimeCommandTrace
{
    public static string Serialize(RuntimeCommandTraceReport report) =>
        JsonSerializer.Serialize(report, RuntimeJsonContext.Default.RuntimeCommandTraceReport);

    public static RuntimeCommandTraceReport Build(RuntimeTraceDocument document, CancellationToken token = default)
    {
        var calls = document.ScriptCalls.Calls.Where(call => call.CallId.HasValue)
            .ToDictionary(call => call.CallId!.Value);
        var entries = document.ScriptCalls.Calls.Where(call => call.Entry is not null)
            .ToDictionary(call => call.Entry!.Line);
        var exits = document.ScriptCalls.Calls.Where(call => call.Exit is not null)
            .ToDictionary(call => call.Exit!.Line);
        var threads = new Dictionary<uint, List<RuntimeScriptCallScope>>();
        var observations = new List<RuntimeCommandObservation>();
        var coverage = new List<RuntimeCommandCoverageEvidence>();
        var loss = document.Summary.Dropped > 0 || document.Summary.MissingSequences > 0;
        AddCoverage(document.Identity, null, null, coverage);
        foreach (var observation in document.Events)
        {
            token.ThrowIfCancellationRequested();
            if (entries.TryGetValue(observation.Line, out var entry) && entry.ThreadId is { } entryThread)
            {
                if (!threads.TryGetValue(entryThread, out var stack)) threads[entryThread] = stack = [];
                stack.Add(entry);
            }
            if (exits.TryGetValue(observation.Line, out var exit) && exit.ThreadId is { } exitThread &&
                threads.TryGetValue(exitThread, out var exitStack))
            {
                if (exitStack.Count > 0 && ReferenceEquals(exitStack[^1], exit)) exitStack.RemoveAt(exitStack.Count - 1);
                else exitStack.Remove(exit);
            }
            if (observation.Kind == "capture-start")
            {
                using var start = JsonDocument.Parse(document.ReadSourceLine(observation));
                AddCoverage(start.RootElement, observation.Line, observation.Sequence, coverage);
                if (start.RootElement.TryGetProperty("identity", out var identity))
                    AddCoverage(identity, observation.Line, observation.Sequence, coverage);
            }
            if (observation.Kind == "command-hook-coverage")
            {
                using var chunk = JsonDocument.Parse(document.ReadSourceLine(observation));
                coverage.Add(new(observation.Line, observation.Sequence, chunk.RootElement.Clone()));
            }
            if (observation.Kind is not ("command-execute" or "message-command" or "message-choice-read" or
                "condition-function")) continue;
            using var source = JsonDocument.Parse(document.ReadSourceLine(observation));
            var row = source.RootElement;
            var thread = Unsigned32(row, "threadId");
            var association = Associate(row, observation, calls,
                thread.HasValue && threads.TryGetValue(thread.Value, out var active) ? active.LastOrDefault() : null,
                loss);
            observations.Add(new(observation.Line, observation.Offset, observation.Length, observation.Sequence,
                observation.Frame, observation.Kind, RuntimeTraceDocument.Text(row, "command") ??
                    RuntimeTraceDocument.Text(row, "function"),
                Unsigned32(row, "opcode"), Boolean(row, "handlerReturned"), Unsigned(row, "requestId"), thread,
                RuntimeTraceDocument.FormId(row, "scriptFormId"), Unsigned(row, "scriptAddress"),
                ReadResult(row), association, row.Clone()));
        }
        var observed = observations.Count(command => command.ScriptCall.Status == "Observed");
        var partial = observations.Count(command => command.ScriptCall.Status == "Partial");
        var ambiguous = observations.Count(command => command.ScriptCall.Status == "Ambiguous");
        var unavailable = observations.Count - observed - partial - ambiguous;
        var status = ambiguous > 0 || observations.Any(command => command.Result.Status == "Ambiguous") ? "Ambiguous" :
            loss || partial > 0 ? "Partial" :
            observations.Count == 0 || unavailable == observations.Count &&
                observations.All(command => command.Result.Status == "Unavailable") ? "Unavailable" :
            unavailable > 0 || observations.Any(command => command.Result.Status != "Observed") ? "Partial" : "Observed";
        return new(document.Summary.Sha256, status, loss, observed, partial, ambiguous, unavailable,
            coverage.ToArray(), observations.ToArray());
    }

    private static void AddCoverage(JsonElement row, int? line, ulong? sequence,
        List<RuntimeCommandCoverageEvidence> coverage)
    {
        if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("commandTraceCoverage", out var value))
            coverage.Add(new(line, sequence, value.Clone()));
    }

    private static RuntimeCommandResult ReadResult(JsonElement row)
    {
        var diagnostics = new List<string>();
        var type = Unsigned32(row, "returnType");
        var typeName = type switch { 0 => "Default", 1 => "Form", 2 => "String", 3 => "Array",
            4 => "ArrayIndex", 5 => "Ambiguous", null => "Unavailable", _ => "Unknown" };
        var bits = RuntimeTraceDocument.Text(row, "resultBits");
        var hasBits = bits is { Length: 16 } && ulong.TryParse(bits, NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out _);
        if (bits is not null && !hasBits) diagnostics.Add("invalid-result-bits");
        if (RuntimeTraceDocument.Text(row, "resultStatus") != "observed" || !hasBits)
        {
            if (row.TryGetProperty("numericValue", out var unexpected) && unexpected.ValueKind != JsonValueKind.Null)
                diagnostics.Add("numeric-value-without-observed-bits");
            return new(diagnostics.Count > 0 ? "Ambiguous" : "Unavailable", type, typeName, bits, null, diagnostics);
        }
        double? numeric = null;
        if (type is null || RuntimeTraceDocument.Text(row, "returnTypeStatus") != "observed")
            diagnostics.Add("return-type-unavailable");
        else if (type > 5) diagnostics.Add("unknown-return-type");
        if (row.TryGetProperty("numericValue", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            var raw = BitConverter.UInt64BitsToDouble(ulong.Parse(bits!, NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture));
            if (type != 0 || RuntimeTraceDocument.Text(row, "returnTypeStatus") != "observed")
                diagnostics.Add("numeric-value-without-default-return-type");
            else if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
                     !double.IsFinite(number) || !double.IsFinite(raw) ||
                     BitConverter.DoubleToUInt64Bits(number) != BitConverter.DoubleToUInt64Bits(raw))
                diagnostics.Add("numeric-value-bits-mismatch");
            else numeric = number;
        }
        // Raw result bits remain usable even when their semantic return type is unavailable.
        var conflicting = diagnostics.Any(diagnostic => diagnostic.StartsWith("numeric-", StringComparison.Ordinal));
        return new(conflicting ? "Ambiguous" : diagnostics.Count > 0 ? "Partial" : "Observed",
            type, typeName, bits, numeric, diagnostics.ToArray());
    }

    private static RuntimeCommandCallAssociation Associate(JsonElement row, RuntimeTraceEvent observation,
        IReadOnlyDictionary<ulong, RuntimeScriptCallScope> calls, RuntimeScriptCallScope? active, bool loss)
    {
        var id = Unsigned(row, "observedScriptCallId");
        if (id is null or 0 || !calls.TryGetValue(id.Value, out var call))
            return new("Unavailable", id, null, null, ["script-call-unavailable"]);
        var diagnostics = new List<string>();
        var thread = Unsigned32(row, "threadId");
        var request = Unsigned(row, "requestId");
        var script = RuntimeTraceDocument.FormId(row, "scriptFormId");
        var address = Unsigned(row, "scriptAddress");
        var missing = thread is null || request is null || script is null || address is not > 0 ||
            call.ThreadId is null || call.RequestId is null || call.ScriptFormId is null || call.ScriptAddress is not > 0;
        if (missing) diagnostics.Add("command-call-identity-unavailable");
        var mismatch = thread.HasValue && call.ThreadId.HasValue && thread != call.ThreadId ||
            request.HasValue && call.RequestId.HasValue && request != call.RequestId ||
            script.HasValue && call.ScriptFormId.HasValue && script != call.ScriptFormId ||
            address.HasValue && call.ScriptAddress.HasValue && address != call.ScriptAddress;
        if (mismatch) diagnostics.Add("command-call-identity-mismatch");
        if (call.Entry is { } entry && observation.Sequence <= entry.Sequence ||
            call.Exit is { } exit && observation.Sequence >= exit.Sequence)
        { mismatch = true; diagnostics.Add("command-outside-call"); }
        if (active is not null && active.CallId != id)
        { mismatch = true; diagnostics.Add("command-call-not-innermost"); }
        if (row.TryGetProperty("commandLocation", out var location))
        {
            foreach (var phase in new[] { "before", "after" })
            {
                if (location.ValueKind != JsonValueKind.Object || !location.TryGetProperty(phase, out var sample) ||
                    sample.ValueKind != JsonValueKind.Object || !sample.TryGetProperty("script", out var identity) ||
                    identity.ValueKind != JsonValueKind.Object || RuntimeTraceDocument.Text(identity, "status") != "observed" ||
                    RuntimeTraceDocument.FormId(identity, "formId") is not { } phaseId ||
                    Unsigned(identity, "address") is not > 0)
                { missing = true; diagnostics.Add("command-script-" + phase + "-unavailable"); continue; }
                if (phaseId != call.ScriptFormId || Unsigned(identity, "address") != call.ScriptAddress)
                { mismatch = true; diagnostics.Add("command-script-" + phase + "-mismatch"); }
            }
        }
        if (RuntimeTraceDocument.Text(row, "scriptCallStatus") is { } state && state is not ("Observed" or "observed-scope"))
        { missing = true; diagnostics.Add("native-script-call-unavailable"); }
        if (loss) diagnostics.Add("capture-event-loss");
        if (call.Status != "Observed") diagnostics.Add("script-call-" + call.Status.ToLowerInvariant());
        var status = mismatch || call.Status == "Ambiguous" ? "Ambiguous" :
            missing || call.Status == "Unavailable" ? "Unavailable" :
            loss || call.Status == "Partial" || active is null ? "Partial" : "Observed";
        return new(status, id, call.Entry, call.Exit, diagnostics.ToArray());
    }

    private static bool? Boolean(JsonElement row, string key) => row.TryGetProperty(key, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static ulong? Unsigned(JsonElement row, string key) => row.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number : null;
    private static uint? Unsigned32(JsonElement row, string key) => Unsigned(row, key) is { } number &&
        number <= uint.MaxValue ? (uint)number : null;
}
