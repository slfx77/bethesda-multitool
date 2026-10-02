using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Checks capture-local combat admission and its independently observed cleanup.</summary>
internal sealed class RuntimeCombatCleanupValidator
{
    private readonly Dictionary<ulong, Lease> _requests = [];
    private readonly HashSet<string> _diagnostics = new(StringComparer.Ordinal);
    private readonly HashSet<ulong> _leaseIds = [];
    private readonly HashSet<ulong> _actionIndices = [];
    private JsonElement _identity;
    private JsonElement _declaredActions;
    private string? _session;
    private bool _started, _ended, _loss, _matchingSession;

    internal bool HasRequests => _requests.Count != 0;
    internal IReadOnlyList<string> GetDiagnostics() => _diagnostics.Concat(_requests.Where(pair => !pair.Value.Rejected &&
        !pair.Value.Completed).Select(pair => $"combat-cleanup-missing:{pair.Key}")).Order(StringComparer.Ordinal).ToArray();

    internal static (uint Attacker, uint Target) RequireTargets(RuntimeAction action, JsonElement identity)
    {
        action.Validate();
        if (!RuntimeMilestoneMonitor.TryResolve(new("", action.Plugin, action.FormId, action.Target), identity, out var attacker) ||
            !RuntimeMilestoneMonitor.TryResolve(new("", action.OtherPlugin, action.OtherFormId, action.OtherTarget), identity, out var target) ||
            attacker == 0x14 || attacker == target)
            throw new InvalidDataException("Leased combat requires distinct actors in a complete capture-time plugin namespace.");
        return (attacker, target);
    }

    internal void Observe(JsonElement row, bool eventLoss = false)
    {
        _loss |= eventLoss;
        var kind = Text(row, "kind");
        if (kind == "capture-header")
        {
            _session = Text(row, "session");
            if (row.TryGetProperty("scenario", out var scenario) && scenario.ValueKind == JsonValueKind.Object &&
                scenario.TryGetProperty("actions", out var actions)) _declaredActions = actions.Clone();
            return;
        }
        if (kind == "capture-start")
        {
            _started = true;
            _matchingSession = !string.IsNullOrWhiteSpace(_session) && Text(row, "session") == _session;
            if (row.TryGetProperty("identity", out var identity)) _identity = identity.Clone();
            return;
        }
        if (kind == "capture-end") { _ended = true; return; }
        if (kind == "action-request" && row.TryGetProperty("action", out var actionRow) &&
            Text(actionRow, "kind") == "start-combat-leased")
        {
            try
            {
                var action = JsonSerializer.Deserialize(actionRow, RuntimeJsonContext.Default.RuntimeAction)!;
                var targets = RequireTargets(action, _identity);
                var request = Number(row, "requestId");
                if (!_started || !_matchingSession || _ended || request is not > 0 || _requests.Count >= 128 ||
                    _declaredActions.ValueKind != JsonValueKind.Array || Number(row, "actionIndex") is not { } actionIndex ||
                    actionIndex >= (ulong)_declaredActions.GetArrayLength() ||
                    !JsonElement.DeepEquals(actionRow, _declaredActions[(int)actionIndex]) ||
                    !_actionIndices.Add(actionIndex) ||
                    !_requests.TryAdd(request.Value, new(action, targets.Attacker, targets.Target)))
                    Fail("request-invalid");
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or JsonException or InvalidOperationException)
            { Fail("request-identity-unavailable"); }
            return;
        }
        var requestId = Number(row, "requestId");
        if (requestId is null || !_requests.TryGetValue(requestId.Value, out var lease))
        {
            if (kind == "combat-cleanup" || kind == "action-result" && Text(row, "operation") == "start-combat-leased")
                Fail("unrequested-evidence");
            return;
        }
        if (kind is not ("action-result" or "combat-cleanup" or "error")) return;
        if (!_started || _ended || Number(row, "protocol") != 1 || Number(row, "sequence") is not > 0 ||
            Number(row, "dropped") is not 0 || _loss)
            Fail("event-order-or-loss");
        if (kind == "error")
        {
            if (lease.Admission.ValueKind == JsonValueKind.Undefined)
            {
                if (!row.TryGetProperty("leaseId", out _)) lease.Rejected = true;
                else Fail("armed-error-without-admission");
            }
            return;
        }
        if (kind == "action-result")
        {
            if (Text(row, "operation") != "start-combat-leased" || lease.Admission.ValueKind != JsonValueKind.Undefined ||
                lease.Rejected || Number(row, "leaseId") is not > 0 || !_leaseIds.Add(Number(row, "leaseId")!.Value) ||
                Text(row, "originSession") != _session || Number(row, "captureGeneration") is not > 0 ||
                Number(row, "connectionGeneration") is not > 0 || Number(row, "loadEpoch") is null ||
                Number(row, "engineTargetFormId") != lease.Attacker || Number(row, "engineOtherFormId") != lease.Target ||
                Number(row, "engineTargetBaseFormId") is not > 0 || Number(row, "engineOtherBaseFormId") is not > 0 ||
                Number(row, "attackerAddress") is not (> 0 and <= uint.MaxValue) ||
                Number(row, "targetAddress") is not (> 0 and <= uint.MaxValue) ||
                Number(row, "attackerBaseAddress") is not (> 0 and <= uint.MaxValue) ||
                Number(row, "targetBaseAddress") is not (> 0 and <= uint.MaxValue) ||
                !Boolean(row, "accepted") ||
                Number(row, "durationMilliseconds") != (ulong)lease.Action.TimeoutMilliseconds!.Value ||
                Number(row, "armedMonotonicMilliseconds") is not { } armed ||
                Number(row, "deadlineMonotonicMilliseconds") is not { } deadline || deadline < armed ||
                deadline - armed != (ulong)lease.Action.TimeoutMilliseconds.Value)
                Fail("admission-mismatch");
            lease.Admission = row.Clone();
            return;
        }
        if (lease.Completed) Fail("duplicate-terminal");
        lease.Completed = true;
        string[] fields = ["leaseId", "originSession", "captureGeneration", "connectionGeneration", "loadEpoch",
            "engineTargetFormId", "engineTargetBaseFormId", "engineOtherFormId", "engineOtherBaseFormId",
            "attackerAddress", "targetAddress", "attackerBaseAddress", "targetBaseAddress",
            "durationMilliseconds", "armedMonotonicMilliseconds", "deadlineMonotonicMilliseconds"];
        if (lease.Admission.ValueKind != JsonValueKind.Object || lease.Rejected || fields.Any(field =>
                !lease.Admission.TryGetProperty(field, out var expected) || !row.TryGetProperty(field, out var actual) ||
                !JsonElement.DeepEquals(expected, actual)) ||
            Text(row, "status") != "observed" || !KnownTrigger(Text(row, "trigger")) ||
            Text(row, "evidence") != "game-thread-fixed-cleanup-and-independent-readback" ||
            Number(row, "cleanupStartedMonotonicMilliseconds") is not { } started ||
            Number(row, "completedMonotonicMilliseconds") is not { } completed || completed < started ||
            started < Number(lease.Admission, "armedMonotonicMilliseconds") ||
            Text(row, "trigger") == "deadline" && started < Number(lease.Admission, "deadlineMonotonicMilliseconds") ||
            Text(row, "trigger") == "start-rejected" && True(lease.Admission, "accepted"))
        { Fail("terminal-mismatch-or-unavailable"); return; }
        if (!row.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() != 3)
        { Fail("steps-unavailable"); return; }
        var names = new[] { "stop-attacker", "stop-target", "disable-attacker" };
        for (var index = 0; index < names.Length; ++index)
        {
            var step = steps[index];
            var target = index == 1;
            if (Text(step, "step") != names[index] || Text(step, "status") != "observed" ||
                !True(step, "accepted") || !True(step, "identityResolved") ||
                Number(step, "engineTargetFormId") != (target ? lease.Target : lease.Attacker) ||
                Number(step, "engineTargetBaseFormId") != Number(lease.Admission, target ? "engineOtherBaseFormId" : "engineTargetBaseFormId") ||
                !step.TryGetProperty("readback", out var readback) || Text(readback, "status") != "observed" ||
                Text(readback, "statistic") != (index == 2 ? "Disabled" : "IsInCombat") ||
                !readback.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDouble(out var actual) || actual != (index == 2 ? 1 : 0))
                Fail("step-unobserved:" + names[index]);
        }
    }

    private void Fail(string diagnostic) => _diagnostics.Add("combat-cleanup-" + diagnostic);
    private static string? Text(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object ? RuntimeTraceDocument.Text(row, key) : null;
    private static ulong? Number(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object &&
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number : null;
    private static bool True(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object &&
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool Boolean(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object &&
        row.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool KnownTrigger(string? trigger) => trigger is "deadline" or "stop" or "cancel" or "disconnect" or
        "capture-ended" or "load-epoch-changed" or "engine-exiting" or "start-rejected" or "explicit-stop" or "queue-full";

    private sealed class Lease(RuntimeAction action, uint attacker, uint target)
    {
        internal RuntimeAction Action { get; } = action;
        internal uint Attacker { get; } = attacker;
        internal uint Target { get; } = target;
        internal JsonElement Admission;
        internal bool Rejected, Completed;
    }
}
