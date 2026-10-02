using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeExpectedField(string Path, JsonElement Value, string Comparison = "equal", double Tolerance = 0);
public sealed record RuntimeExpectedForm(string Path, string? Plugin = null, string? FormId = null, string? Target = null);

/// <summary>Expected engine observations following an action; -1 checks the initial state.</summary>
public sealed record RuntimeMilestone(string Id, int AfterActionIndex, string EventKind,
    IReadOnlyList<RuntimeExpectedField> Fields, int TimeoutMilliseconds = 5000,
    RuntimeAction? Probe = null, int PollMilliseconds = 100, IReadOnlyList<RuntimeExpectedForm>? Forms = null);

internal sealed class RuntimeMilestoneMonitor
{
    internal sealed class Pending(RuntimeMilestone specification, IReadOnlyDictionary<string, uint> forms)
    {
        private readonly object _gate = new();
        private JsonElement? _observation;
        private string? _failure;
        private double? _observedMilliseconds;
        internal RuntimeMilestone Specification { get; } = specification;
        internal IReadOnlyDictionary<string, uint> Forms { get; } = forms;
        internal long Started { get; } = Stopwatch.GetTimestamp();
        internal TimeSpan Elapsed => Stopwatch.GetElapsedTime(Started);
        internal ulong? ProbeRequest { get; set; }
        internal ulong? ActionRequest { get; set; }
        internal bool ActionStarted { get; set; } = specification.AfterActionIndex == -1;
        internal bool ProbeAwaitingResponse { get; set; }
        internal long LastProbe { get; set; }
        internal JsonElement? Observation { get { lock (_gate) return _observation; } }
        internal double? ObservedMilliseconds { get { lock (_gate) return _observedMilliseconds; } }
        internal bool Reported { get; set; }
        internal string? Failure { get { lock (_gate) return _failure; } }
        internal bool Matched { get { lock (_gate) return _observation.HasValue && _failure == null; } }
        internal TimeSpan Remaining => TimeSpan.FromMilliseconds(Specification.TimeoutMilliseconds) - Elapsed;
        internal void Fail(string reason) { lock (_gate) _failure ??= reason; }
        internal void Match(JsonElement row)
        {
            lock (_gate)
            {
                var elapsed = Elapsed.TotalMilliseconds;
                if (_failure == null && elapsed < Specification.TimeoutMilliseconds)
                { _observation = row.Clone(); _observedMilliseconds = elapsed; }
            }
        }
        internal bool Expire()
        {
            lock (_gate)
            {
                if (_observation.HasValue || _failure != null || Remaining > TimeSpan.Zero) return false;
                _failure = "timeout";
                return true;
            }
        }
    }

    private readonly IReadOnlyList<RuntimeMilestone> _specifications;
    internal IReadOnlyList<Pending> Active { get; private set; } = [];

    internal RuntimeMilestoneMonitor(IReadOnlyList<RuntimeMilestone> specifications, int actionCount)
    {
        if (specifications.Count > 64) throw new ArgumentException("A scenario supports at most 64 milestones.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var milestone in specifications)
        {
            if (milestone == null || !Name(milestone.Id, 64) || !ids.Add(milestone.Id) ||
                milestone.AfterActionIndex < -1 || milestone.AfterActionIndex >= actionCount ||
                !Name(milestone.EventKind, 64) || milestone.EventKind is "action-begin" or "action-result" or "error" or "capture-start" or "capture-end" ||
                milestone.TimeoutMilliseconds is < 1 or > 30000 || milestone.PollMilliseconds is < 50 or > 1000 ||
                milestone.Fields is not { Count: > 0 and <= 32 } || milestone.Forms?.Count > 8)
                throw new ArgumentException("Invalid milestone identity, action index, observation or deadline.");
            if (milestone.Probe is { } probe)
            {
                if (probe.Kind?.StartsWith("read-", StringComparison.Ordinal) != true)
                    throw new ArgumentException("Milestone probes must be read-only actions.");
                _ = probe.ToFrame(1);
                if (RuntimeResponse.Kind(probe.ToFrame(1).Kind, probe.Kind) != milestone.EventKind)
                    throw new ArgumentException("A probe milestone must expect that read action's terminal observation.");
            }
            else if (milestone.Forms is not { Count: > 0 })
                throw new ArgumentException("Passive milestones require an explicit observed form identity.");
            foreach (var field in milestone.Fields)
            {
                if (field == null) throw new ArgumentException("Expected field cannot be null.");
                ValidatePath(field.Path);
                if (field.Comparison is not ("equal" or "not-equal" or "less" or "less-or-equal" or "greater" or "greater-or-equal") ||
                    !double.IsFinite(field.Tolerance) || field.Tolerance < 0 ||
                    field.Value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Expected fields require finite numeric, text or Boolean comparisons.");
                if (field.Value.ValueKind == JsonValueKind.Number)
                {
                    if (!field.Value.TryGetDouble(out var number) || !double.IsFinite(number))
                        throw new ArgumentException("Expected numbers must be finite.");
                }
                else if (field.Comparison is not ("equal" or "not-equal") || field.Tolerance != 0)
                    throw new ArgumentException("Ordering and tolerance require numeric values.");
            }
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var form in milestone.Forms ?? [])
            {
                if (form == null) throw new ArgumentException("Expected form cannot be null.");
                ValidatePath(form.Path);
                if (!paths.Add(form.Path)) throw new ArgumentException("Duplicate form expectation path.");
                _ = new RuntimeAction("read-actor-state", Target: form.Target, Plugin: form.Plugin, FormId: form.FormId).ToFrame(1);
            }
        }
        _specifications = specifications;
    }

    internal void Begin(int actionIndex, JsonElement identity)
    {
        Active = _specifications.Where(item => item.AfterActionIndex == actionIndex).Select(item =>
        {
            var forms = new Dictionary<string, uint>(StringComparer.Ordinal);
            var unavailable = false;
            foreach (var form in item.Forms ?? [])
            {
                if (!TryResolve(form, identity, out var id)) unavailable = true;
                else if (!forms.TryAdd(form.Path, id)) throw new ArgumentException("Duplicate form expectation path.");
            }
            var pending = new Pending(item, forms);
            if (unavailable) pending.Fail("unavailable");
            return pending;
        }).ToArray();
    }

    internal void Observe(JsonElement row, bool eventLoss)
    {
        foreach (var item in Active.Where(item => !item.Reported))
        {
            if (eventLoss) { item.Fail("event-loss"); continue; }
            if (item.Failure != null || item.Matched || item.Remaining <= TimeSpan.Zero) continue;
            var request = row.GetProperty("requestId").GetUInt64();
            var kind = RuntimeTraceDocument.Text(row, "kind");
            if (item.Specification.Probe != null)
            {
                if (item.ProbeRequest != request) continue;
                if (kind == "error") { item.ProbeAwaitingResponse = false; item.Fail("native-error"); continue; }
                if (kind == item.Specification.EventKind) item.ProbeAwaitingResponse = false;
            }
            else
            {
                if (kind == "action-begin" && item.ActionRequest == request) item.ActionStarted = true;
                if (!item.ActionStarted) continue;
            }
            if (RuntimeTraceDocument.Text(row, "kind") != item.Specification.EventKind) continue;
            if (item.Specification.Probe?.Kind == "read-message-state" &&
                RuntimeMenuInspection.Classify(row) == RuntimeMenuInspectionState.Invalid)
            { item.Fail("mismatch"); continue; }
            if (item.Forms.Any(pair => !ReadPath(row, pair.Key, out var actual) || FormId(actual) != pair.Value)) continue;
            if (item.Specification.Fields.All(field => ReadPath(row, field.Path, out var value) && Matches(value, field)))
                item.Match(row);
        }
    }

    internal static bool Matches(JsonElement actual, RuntimeExpectedField expected)
    {
        if (actual.ValueKind != expected.Value.ValueKind &&
            !(actual.ValueKind is JsonValueKind.True or JsonValueKind.False && expected.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)) return false;
        if (expected.Value.ValueKind == JsonValueKind.Number)
        {
            if (!actual.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
            var target = expected.Value.GetDouble();
            var equal = Math.Abs(number - target) <= expected.Tolerance;
            return expected.Comparison switch
            {
                "equal" => equal, "not-equal" => !equal,
                "less" => number < target, "less-or-equal" => number <= target || equal,
                "greater" => number > target, "greater-or-equal" => number >= target || equal,
                _ => false
            };
        }
        var same = actual.ValueKind == JsonValueKind.String
            ? actual.GetString() == expected.Value.GetString() : actual.GetBoolean() == expected.Value.GetBoolean();
        return expected.Comparison == "equal" ? same : !same;
    }

    internal static bool IsFailure(string? outcome, bool milestone) =>
        outcome is "mismatch" or "timeout" or "capture-ended" or "capture-stopping" ||
        milestone && outcome is "unavailable" or "event-loss" or "not-run";

    internal static bool MatchesEvent(RuntimeMilestone milestone, JsonElement identity, JsonElement row) =>
        RuntimeTraceDocument.Text(row, "kind") == milestone.EventKind &&
        (milestone.Probe?.Kind != "read-message-state" || RuntimeMenuInspection.Classify(row) != RuntimeMenuInspectionState.Invalid) &&
        (milestone.Forms ?? []).All(form => TryResolve(form, identity, out var id) &&
            ReadPath(row, form.Path, out var value) && FormId(value) == id) &&
        milestone.Fields.All(field => ReadPath(row, field.Path, out var value) && Matches(value, field));

    internal static bool TryResolve(RuntimeExpectedForm form, JsonElement identity, out uint id)
    {
        id = 0;
        if (form.Target == "player") { id = 0x14; return true; }
        if (identity.ValueKind != JsonValueKind.Object || RuntimeTraceDocument.Text(identity, "activePluginIdentityStatus") != "complete" ||
            !identity.TryGetProperty("activePlugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array) return false;
        var matches = plugins.EnumerateArray().Where(plugin =>
            string.Equals(RuntimeTraceDocument.Text(plugin, "name"), form.Plugin, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || RuntimeTraceDocument.Text(matches[0], "status") != "verified" ||
            !matches[0].TryGetProperty("index", out var slot) || !slot.TryGetUInt32(out var index) || index >= 255) return false;
        var local = form.FormId!.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? form.FormId[2..] : form.FormId;
        id = (index << 24) | uint.Parse(local, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return true;
    }

    private static uint? FormId(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var id)) return id;
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()!;
        return uint.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.AsSpan(2) : text.AsSpan(),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) ? id : null;
    }

    private static bool ReadPath(JsonElement row, string path, out JsonElement value)
    {
        value = row;
        foreach (var part in path.Split('.'))
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(part, out var child)) value = child;
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < value.GetArrayLength()) value = value[index];
            else return false;
        }
        return true;
    }

    private static bool Name(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void ValidatePath(string? path)
    {
        if (path is not { Length: > 0 and <= 256 })
            throw new ArgumentException("Expected fields require a bounded dotted property/array path.");
        var parts = path.Split('.');
        if (parts.Length > 8 || parts.Any(part => !Name(part, 64)))
            throw new ArgumentException("Expected fields require a bounded dotted property/array path.");
    }
}
