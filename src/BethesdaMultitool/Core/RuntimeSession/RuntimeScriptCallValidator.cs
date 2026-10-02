using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeScriptCallSummary(string Status, int Observed, int Partial, int Ambiguous,
    int Unavailable, bool EventLoss, bool LimitReached);

public sealed record RuntimeScriptCallEndpoint(int Line, ulong Sequence, ulong? Frame);

public sealed record RuntimeScriptCallScope(ulong? CallId, uint? ThreadId, ulong? ParentCallId, uint? Depth,
    uint? ScriptFormId, ulong? ScriptAddress, RuntimeScriptCallEndpoint? Entry, RuntimeScriptCallEndpoint? Exit,
    bool? Returned, string Status, IReadOnlyList<string> Diagnostics, IReadOnlyList<int> EvidenceLines,
    ulong? RequestId = null);

public sealed record RuntimeScriptCallReport(string TraceSha256, RuntimeScriptCallSummary Summary,
    IReadOnlyList<RuntimeScriptCallScope> Calls);

/// <summary>Validates observed Script::Execute pairs independently of capture transport completion.</summary>
internal sealed class RuntimeScriptCallValidator
{
    private const int MaxScopes = 100_000;
    private readonly Dictionary<ulong, Scope> _byId = [];
    private readonly List<Scope> _scopes = [];
    private readonly Dictionary<uint, List<Scope>> _threads = [];
    private bool _limit;

    internal void Observe(JsonElement row, RuntimeTraceEvent observation)
    {
        if (observation.Kind is not ("script-entry" or "script-exit")) return;
        var callId = Unsigned(row, "callId");
        if (callId == 0) callId = null;
        var identity = new Identity(Unsigned32(row, "threadId"), Unsigned(row, "parentCallId"),
            Unsigned32(row, "depth"), RuntimeTraceDocument.FormId(row, "scriptFormId"),
            Unsigned(row, "scriptAddress"), Unsigned(row, "requestId"));
        if (identity.ThreadId == 0) identity = identity with { ThreadId = null };
        Scope scope;
        if (callId.HasValue && _byId.TryGetValue(callId.Value, out var existing)) scope = existing;
        else
        {
            if (_scopes.Count >= MaxScopes) { _limit = true; return; }
            scope = new Scope(callId, identity);
            _scopes.Add(scope);
            if (callId.HasValue) _byId.Add(callId.Value, scope);
        }
        scope.Lines.Add(observation.Line);
        var endpoint = new RuntimeScriptCallEndpoint(observation.Line, observation.Sequence, observation.Frame);
        if (scope.Identity != identity) scope.Conflict("call-identity-mismatch");
        if (!callId.HasValue || identity.ThreadId is null)
        {
            scope.Unavailable = true;
            scope.Note("call-or-thread-identity-unavailable");
        }
        if (identity.ParentCallId is null || identity.Depth is null) scope.Note("nesting-unavailable");
        if (identity.ScriptFormId is null || identity.ScriptAddress is not > 0) scope.Note("script-identity-unavailable");
        if (observation.Kind == "script-entry")
        {
            if (scope.Entry is not null) { scope.Conflict("duplicate-entry"); return; }
            scope.Entry = endpoint;
            if (scope.Exit is not null) scope.Conflict("exit-before-entry");
            if (!callId.HasValue || identity.ThreadId is not { } thread) return;
            if (!_threads.TryGetValue(thread, out var stack)) _threads[thread] = stack = [];
            var parent = stack.LastOrDefault();
            if (identity.ParentCallId == callId) scope.Conflict("self-parent");
            else if (parent is not null && parent.Id != identity.ParentCallId) scope.Conflict("parent-call-mismatch");
            else if (parent is null && identity.ParentCallId is > 0)
            {
                scope.Note("parent-entry-unavailable");
                if (_byId.TryGetValue(identity.ParentCallId.Value, out var known))
                {
                    if (known.Identity.ThreadId != thread) scope.Conflict("parent-thread-mismatch");
                    if (known.Exit is not null) scope.Conflict("parent-already-exited");
                }
            }
            if (identity.Depth is { } depth && identity.ParentCallId is { } parentId)
            {
                if (parentId == 0 && depth != 0) scope.Conflict("root-depth-mismatch");
                else if (parent is not null && parent.Identity.Depth is { } parentDepth &&
                         (ulong)depth != (ulong)parentDepth + 1) scope.Conflict("nested-depth-mismatch");
            }
            stack.Add(scope);
        }
        else
        {
            if (scope.Exit is not null) { scope.Conflict("duplicate-exit"); return; }
            scope.Exit = endpoint;
            if (row.TryGetProperty("returned", out var returned) && returned.ValueKind is JsonValueKind.True or JsonValueKind.False)
                scope.Returned = returned.GetBoolean();
            else scope.Note("return-unavailable");
            if (identity.ThreadId is not { } thread || !_threads.TryGetValue(thread, out var stack)) return;
            var index = stack.Count > 0 && ReferenceEquals(stack[^1], scope) ? stack.Count - 1 : stack.IndexOf(scope);
            if (index < 0) return;
            if (index != stack.Count - 1)
            {
                scope.Conflict("out-of-order-exit");
                foreach (var child in stack.Skip(index + 1)) child.Conflict("parent-exited-before-child");
            }
            stack.RemoveAt(index);
        }
    }

    internal RuntimeScriptCallReport Complete(string traceSha256, bool eventLoss)
    {
        var calls = _scopes.Select(scope =>
        {
            if (scope.Entry is null) scope.Note("missing-entry");
            if (scope.Exit is null) scope.Note("missing-exit");
            if (eventLoss) scope.Note("capture-event-loss");
            var status = scope.Conflicting ? "Ambiguous" : scope.Unavailable ? "Unavailable" :
                scope.Diagnostics.Count > 0 ? "Partial" : "Observed";
            return new RuntimeScriptCallScope(scope.Id, scope.Identity.ThreadId, scope.Identity.ParentCallId,
                scope.Identity.Depth, scope.Identity.ScriptFormId, scope.Identity.ScriptAddress, scope.Entry,
                scope.Exit, scope.Returned, status, scope.Diagnostics.ToArray(), scope.Lines.ToArray(),
                scope.Identity.RequestId);
        }).ToArray();
        var observed = calls.Count(call => call.Status == "Observed");
        var partial = calls.Count(call => call.Status == "Partial");
        var ambiguous = calls.Count(call => call.Status == "Ambiguous");
        var unavailable = calls.Count(call => call.Status == "Unavailable");
        var status = ambiguous > 0 ? "Ambiguous" : _limit || eventLoss || partial > 0 || unavailable > 0 ? "Partial" :
            calls.Length == 0 ? "Unavailable" : "Observed";
        return new(traceSha256, new(status, observed, partial, ambiguous, unavailable, eventLoss, _limit), calls);
    }

    private static ulong? Unsigned(JsonElement row, string name) => row.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number : null;
    private static uint? Unsigned32(JsonElement row, string name) => Unsigned(row, name) is { } number &&
        number <= uint.MaxValue ? (uint)number : null;

    private sealed record Identity(uint? ThreadId, ulong? ParentCallId, uint? Depth, uint? ScriptFormId,
        ulong? ScriptAddress, ulong? RequestId);

    private sealed class Scope(ulong? id, Identity identity)
    {
        internal ulong? Id { get; } = id;
        internal Identity Identity { get; } = identity;
        internal RuntimeScriptCallEndpoint? Entry { get; set; }
        internal RuntimeScriptCallEndpoint? Exit { get; set; }
        internal bool? Returned { get; set; }
        internal bool Conflicting { get; private set; }
        internal bool Unavailable { get; set; }
        internal List<string> Diagnostics { get; } = [];
        internal List<int> Lines { get; } = [];
        internal void Note(string diagnostic) { if (!Diagnostics.Contains(diagnostic)) Diagnostics.Add(diagnostic); }
        internal void Conflict(string diagnostic) { Conflicting = true; Note(diagnostic); }
    }
}
