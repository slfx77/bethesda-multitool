using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Checks controller assertions against the retained scenario and native source lines.</summary>
internal sealed class RuntimeMilestoneTraceValidator
{
    private readonly RuntimeScenario _scenario;
    private readonly Dictionary<string, RuntimeMilestone> _milestones;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, (string Id, ulong Boundary)> _probes = [];
    private readonly Dictionary<int, ulong> _boundaries = [];
    private readonly Dictionary<ulong, int> _actions = [];
    private readonly Dictionary<int, ulong> _actionBegins = [];
    private int _nextAction;

    internal RuntimeMilestoneTraceValidator(JsonElement header)
    {
        try
        {
            _scenario = header.GetProperty("scenario").Deserialize(RuntimeJsonContext.Default.RuntimeScenario)
                ?? throw new ArgumentException("Scenario is missing.");
            if (_scenario.Actions is not { Count: <= 128 } || _scenario.Milestones is not { Count: > 0 } ||
                _scenario.ObserveMilliseconds is <= 0 or > 3600000)
                throw new ArgumentException("Invalid scenario bounds.");
            foreach (var action in _scenario.Actions)
                (action ?? throw new ArgumentException("Action cannot be null.")).Validate();
            _ = new RuntimeMilestoneMonitor(_scenario.Milestones, _scenario.Actions.Count);
            _milestones = _scenario.Milestones.ToDictionary(item => item.Id, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("Invalid trace scenario.", error); }
    }

    internal void Started(ulong sequence) => _boundaries.Add(-1, sequence);

    internal void ActionRequested(JsonElement row, ulong previous)
    {
        if (!_boundaries.ContainsKey(-1) || !row.TryGetProperty("actionIndex", out var index) ||
            !index.TryGetInt32(out var actionIndex) || actionIndex != _nextAction || actionIndex >= _scenario.Actions.Count ||
            ReadAction(row) != _scenario.Actions[actionIndex])
            throw new InvalidDataException("Scenario action does not match its declared index and value.");
        _boundaries.Add(actionIndex, previous);
        if (_scenario.Actions[actionIndex].Kind != "wait-message-state")
        {
            if (!row.TryGetProperty("requestId", out var request) || !request.TryGetUInt64(out var requestId) || requestId == 0 ||
                !_actions.TryAdd(requestId, actionIndex))
                throw new InvalidDataException("Scenario action is missing its unique request identity.");
        }
        ++_nextAction;
    }

    internal void ProbeRequested(JsonElement row, ulong previous)
    {
        var id = RuntimeTraceDocument.Text(row, "milestoneId");
        if (id == null || !_milestones.TryGetValue(id, out var milestone) || milestone.Probe == null ||
            _reported.Contains(id) || !_boundaries.ContainsKey(milestone.AfterActionIndex) ||
            !row.TryGetProperty("requestId", out var request) || !request.TryGetUInt64(out var requestId) || requestId == 0 ||
            ReadAction(row) != milestone.Probe || _actions.ContainsKey(requestId) || !_probes.TryAdd(requestId, (id, previous)))
            throw new InvalidDataException("Milestone probe does not match its declared read action.");
    }

    internal void Observe(JsonElement row, ulong sequence)
    {
        if (RuntimeTraceDocument.Text(row, "kind") != "action-begin") return;
        if (_actions.TryGetValue(row.GetProperty("requestId").GetUInt64(), out var actionIndex) &&
            (!_actionBegins.TryAdd(actionIndex, sequence) || !row.TryGetProperty("requestKind", out var kind) ||
                !kind.TryGetUInt16(out var requestKind) || requestKind != (ushort)_scenario.Actions[actionIndex].RequestKind))
            throw new InvalidDataException("Invalid game-thread action boundary.");
    }

    internal void Result(JsonElement row, string id, JsonElement identity, IReadOnlyList<RuntimeTraceEvent> observations,
        byte[] bytes, bool eventLoss)
    {
        if (!_milestones.TryGetValue(id, out var milestone) || !_reported.Add(id) ||
            !row.TryGetProperty("actionIndex", out var index) || !index.TryGetInt32(out var actionIndex) ||
            actionIndex != milestone.AfterActionIndex || RuntimeTraceDocument.Text(row, "actionKind") != "milestone:" + id)
            throw new InvalidDataException("Unknown, duplicate or misattributed milestone result.");
        if (RuntimeTraceDocument.Text(row, "status") != "matched") return;
        if (eventLoss || !_boundaries.TryGetValue(actionIndex, out var boundary) ||
            !row.TryGetProperty("observedSequence", out var sequence) || !sequence.TryGetUInt64(out var observedSequence) ||
            observedSequence <= boundary ||
            !row.TryGetProperty("observedRequestId", out var request) || !request.TryGetUInt64(out var requestId) ||
            !row.TryGetProperty("matchedElapsedMilliseconds", out var elapsed) || !elapsed.TryGetDouble(out var milliseconds) ||
            !double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds >= milestone.TimeoutMilliseconds ||
            !row.TryGetProperty("elapsedMilliseconds", out var reportedElapsed) || !reportedElapsed.TryGetDouble(out var reportedMilliseconds) ||
            !double.IsFinite(reportedMilliseconds) || reportedMilliseconds < milliseconds ||
            milestone.Probe != null && (!_probes.TryGetValue(requestId, out var probe) || probe.Id != id || observedSequence <= probe.Boundary) ||
            milestone.Probe == null && actionIndex >= 0 &&
                (!_actionBegins.TryGetValue(actionIndex, out var actionBegin) || observedSequence <= actionBegin))
            throw new InvalidDataException("Matched milestone lacks a valid observation identity or deadline.");
        // Native sequences are strictly increasing; retain their original bytes rather than copied values.
        RuntimeTraceEvent? observation = null;
        for (int lower = 0, upper = observations.Count - 1; lower <= upper;)
        {
            var middle = lower + (upper - lower) / 2;
            var candidate = observations[middle];
            if (candidate.Sequence == observedSequence) { observation = candidate; break; }
            if (candidate.Sequence < observedSequence) lower = middle + 1;
            else upper = middle - 1;
        }
        if (observation == null) throw new InvalidDataException("Milestone observation is absent from the trace.");
        using var source = JsonDocument.Parse(bytes.AsMemory(observation.Offset, observation.Length));
        var actual = source.RootElement;
        if (actual.GetProperty("requestId").GetUInt64() != requestId ||
            row.TryGetProperty("observedFrame", out var frame) &&
                (!frame.TryGetUInt64(out var observedFrame) || observation.Frame != observedFrame) ||
            !RuntimeMilestoneMonitor.MatchesEvent(milestone, identity, actual))
            throw new InvalidDataException("Milestone result does not match its cited engine observation.");
    }

    internal bool HasMissingResults => _reported.Count != _milestones.Count;

    private static RuntimeAction? ReadAction(JsonElement row) => row.TryGetProperty("action", out var action)
        ? action.Deserialize(RuntimeJsonContext.Default.RuntimeAction) : null;
}
