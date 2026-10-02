using System.Globalization;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeGamepadTracePoint(int Line, int Offset, int Length, ulong? Sequence, ulong? Frame);
public sealed record RuntimeGamepadBindingIdentity(string Session, string RunManifestSha256,
    ulong ConnectionGeneration, ulong CaptureEpoch, ulong BindingSerial, ulong WindowIdentity,
    uint ProcessId, ulong ProcessStartedFileTime);
public sealed record RuntimeGamepadStateObservation(RuntimeGamepadTracePoint Source, string Phase,
    ulong? ObservationOrdinal, ulong? ObservedMonotonicMilliseconds, uint? Packet, uint? Buttons,
    uint? ReturnCode, long? Flags, long? LeftTrigger, long? RightTrigger,
    long? LeftX, long? LeftY, long? RightX, long? RightY);
public sealed record RuntimeGamepadPulseResult(ulong RequestId, string Status, IReadOnlyList<string> Diagnostics,
    int? ActionIndex, int? Slot, uint? Buttons, int? HoldMilliseconds, int? DeadlineMilliseconds,
    ulong? AdmittedMonotonicMilliseconds, RuntimeGamepadBindingIdentity? Identity,
    RuntimeGamepadTracePoint? ActionRequest, RuntimeGamepadTracePoint? Binding,
    RuntimeGamepadTracePoint? Queued, RuntimeGamepadStateObservation? Press,
    RuntimeGamepadStateObservation? Release, RuntimeGamepadTracePoint? Terminal,
    RuntimeGamepadTracePoint? ControllerResult);
public sealed record RuntimeGamepadTraceSummary(string Status, int Observed, int Partial, int Invalid,
    int Unavailable, bool EventLoss, bool LimitReached);
public sealed record RuntimeGamepadTraceReport(string TraceSha256, RuntimeGamepadTraceSummary Summary,
    IReadOnlyList<RuntimeGamepadPulseResult> Pulses, IReadOnlyList<string> Diagnostics);

/// <summary>Links a requested pulse to the two actual guest returns; host cleanup is not a release.</summary>
internal sealed class RuntimeGamepadTraceValidator
{
    private const int MaximumPulses = 128;
    private readonly Dictionary<ulong, Pulse> _pulses = [];
    private readonly Dictionary<int, Specification?> _declared = [];
    private readonly List<string> _diagnostics = [];
    private Binding? _binding;
    private RuntimeGamepadBindingIdentity? _lastBinding;
    private string? _session, _manifest;
    private uint? _processId;
    private ulong? _processStarted;
    private ulong _sequence, _ordinal, _observationTime;
    private long _record;
    private int _version;
    private bool _header, _started, _ended, _eventLoss, _limit, _invalidHeader;

    internal void Observe(JsonElement row, int line = 0, int offset = 0, int length = 0, bool eventLoss = false)
    {
        _eventLoss |= eventLoss;
        ++_record;
        if (row.ValueKind != JsonValueKind.Object) { Note("invalid-row"); return; }
        var kind = Text(row, "kind");
        var point = new RuntimeGamepadTracePoint(line, offset, length, Unsigned(row, "sequence"), Unsigned(row, "frame"));
        if (kind == "capture-header") { Header(row); return; }
        if (point.Sequence is { } sequence)
        {
            if (sequence == 0 || sequence <= _sequence || _sequence != 0 && sequence != _sequence + 1) _eventLoss = true;
            _sequence = sequence;
        }
        if (Unsigned(row, "dropped") is > 0) _eventLoss = true;
        if (kind == "capture-start")
        {
            if (_started || _ended || Text(row, "session") != _session) _invalidHeader = true;
            _started = true;
            return;
        }
        if (kind == "capture-end") { _ended = true; return; }
        if (kind == "guest-run-bound") { Bound(row, point); return; }
        if (kind == "action-request") { Requested(row, point); return; }
        if (kind == "scenario-result") { ControllerResult(row, point); return; }
        if (kind is not ("gamepad-queued" or "gamepad-state" or "gamepad-result" or "gamepad-output" or "error")) return;
        var requestId = Unsigned(row, "requestId");
        if (kind == "error" && (requestId is null || !_pulses.ContainsKey(requestId.Value))) return;
        if (requestId is not > 0) { Note("gamepad-request-identity-unavailable"); return; }
        var pulse = Get(requestId.Value);
        if (pulse is null) return;
        if (Unsigned(row, "protocol") != 1 || point.Sequence is not > 0 || Unsigned(row, "dropped") is null)
            pulse.Fail("invalid-native-envelope");
        if (!_started || _ended) pulse.Fail("outside-active-capture");
        if (kind == "error")
        {
            pulse.Note("native-error:" + (Text(row, "reason") ?? Text(row, "error") ?? "unavailable"));
            pulse.Failed = true;
            return;
        }
        // A cleanup observation deliberately has no authority to complete a pulse.
        if (kind == "gamepad-output") { pulse.Note("host-output-adjusted"); return; }
        var common = Common(row);
        if (common is null) pulse.Fail("gamepad-identity-unavailable");
        else
        {
            if (pulse.Binding is null || common.Identity != pulse.Binding.Identity ||
                _binding?.Identity != pulse.Binding.Identity) pulse.Fail("binding-mismatch");
            if (pulse.Specification != common.Specification) pulse.Fail("requested-pulse-mismatch");
            if (pulse.Common is not null && pulse.Common != common) pulse.Fail("pulse-identity-changed");
        }
        if (kind == "gamepad-queued")
        {
            if (Text(row, "origin") != "bridge-control" || Text(row, "status") != "queued") pulse.Fail("invalid-queue-origin");
            if (pulse.Queued is not null || pulse.Press is not null || pulse.Release is not null || pulse.Terminal is not null)
                pulse.Fail("duplicate-or-late-queue");
            if (pulse.Action is null || pulse.Binding is null || pulse.Binding.Point.Sequence >= point.Sequence)
                pulse.Fail("queue-before-request-or-binding");
            foreach (var other in _pulses.Values)
                if (!ReferenceEquals(other, pulse) && other.Queued is not null && other.Terminal is null && !other.Failed)
                { other.Fail("overlapping-pulses"); pulse.Fail("overlapping-pulses"); }
            pulse.Common ??= common;
            pulse.Queued ??= point;
            return;
        }
        if (pulse.Queued is null || point.Sequence <= pulse.Queued.Sequence) pulse.Fail("observation-before-queue");
        if (kind == "gamepad-state")
        {
            var phase = Text(row, "phase");
            var state = new RuntimeGamepadStateObservation(point, phase ?? "unavailable",
                Unsigned(row, "observationOrdinal"), Unsigned(row, "observedMonotonicMilliseconds"),
                UInt32(row, "packet"), UInt32(row, "buttons"), UInt32(row, "result"), Integer(row, "flags"),
                Integer(row, "leftTrigger"), Integer(row, "rightTrigger"), Integer(row, "leftX"),
                Integer(row, "leftY"), Integer(row, "rightX"), Integer(row, "rightY"));
            if (Text(row, "origin") != "guest-xam-input" || state.ReturnCode != 0 || !SupportedFlags(state.Flags))
                pulse.Fail("guest-return-unavailable");
            if (state.LeftTrigger != 0 || state.RightTrigger != 0 || state.LeftX != 0 || state.LeftY != 0 || state.RightX != 0 || state.RightY != 0)
                pulse.Fail("nonneutral-or-missing-analog");
            if (state.ObservationOrdinal is not > 0 || state.ObservedMonotonicMilliseconds is null || state.Packet is null)
                pulse.Fail("guest-observation-identity-unavailable");
            else
            {
                if (state.ObservationOrdinal <= _ordinal || state.ObservedMonotonicMilliseconds < _observationTime)
                    pulse.Fail("guest-observation-order");
                _ordinal = state.ObservationOrdinal.Value;
                _observationTime = state.ObservedMonotonicMilliseconds.Value;
            }
            if (pulse.Terminal is not null) pulse.Fail("state-after-result");
            if (phase == "pressed")
            {
                if (pulse.Press is not null || pulse.Release is not null) pulse.Fail("duplicate-or-late-press");
                if (state.Buttons != pulse.Specification?.Buttons) pulse.Fail("press-buttons-mismatch");
                pulse.Press ??= state;
            }
            else if (phase == "released")
            {
                if (pulse.Release is not null || pulse.Press is null) pulse.Fail("duplicate-or-early-release");
                if (state.Buttons != 0) pulse.Fail("release-not-neutral");
                pulse.Release ??= state;
            }
            else pulse.Fail("unknown-gamepad-phase");
            return;
        }
        if (pulse.Terminal is not null) pulse.Fail("duplicate-result");
        pulse.Terminal ??= point;
        if (Text(row, "origin") != "bridge-control" || Text(row, "status") != "observed") pulse.Fail("invalid-result-origin");
        if (!True(row, "releaseObserved") || !True(row, "hostButtonsCleared")) pulse.Fail("result-release-state-unavailable");
        if (pulse.Press is null || pulse.Release is null ||
            Unsigned(row, "pressSequence") != pulse.Press.Source.Sequence || Unsigned(row, "releaseSequence") != pulse.Release.Source.Sequence ||
            pulse.Release.Source.Sequence <= pulse.Press.Source.Sequence || point.Sequence <= pulse.Release.Source.Sequence)
            pulse.Fail("result-observation-reference-mismatch");
        ValidateTiming(pulse);
    }

    internal RuntimeGamepadPulseResult Result(ulong requestId) => _pulses.TryGetValue(requestId, out var pulse)
        ? Project(pulse, false) : new(requestId, "Unavailable", ["action-request-unavailable"], null, null, null,
            null, null, null, null, null, null, null, null, null, null, null);

    internal RuntimeGamepadTraceReport Complete(string traceSha256, bool eventLoss)
    {
        _eventLoss |= eventLoss;
        var pulses = _pulses.Values.Select(p => Project(p, true)).ToArray();
        var observed = pulses.Count(p => p.Status == "Observed");
        var partial = pulses.Count(p => p.Status == "Partial");
        var invalid = pulses.Count(p => p.Status == "Invalid");
        var unavailable = pulses.Count(p => p.Status == "Unavailable");
        foreach (var index in _declared.Keys)
            if (!_pulses.Values.Any(p => p.ActionIndex == index)) Note("unrequested-gamepad-action:" + index.ToString(CultureInfo.InvariantCulture));
        var status = invalid > 0 ? "Invalid" : _eventLoss || _limit || partial > 0 || unavailable > 0 || _diagnostics.Count > 0 ? "Partial" :
            observed > 0 ? "Observed" : "Unavailable";
        return new(traceSha256, new(status, observed, partial, invalid, unavailable, _eventLoss, _limit), pulses, _diagnostics.ToArray());
    }

    private void Header(JsonElement row)
    {
        if (_header || Text(row, "schema") != "bmt/runtime-trace") _invalidHeader = true;
        _header = true;
        _version = Int32(row, "version") ?? 0;
        _session = Text(row, "session");
        if (row.TryGetProperty("identity", out var identity) && identity.ValueKind == JsonValueKind.Object)
        {
            _processId = UInt32(identity, "processId");
            _processStarted = Unsigned(identity, "processStartedFileTime");
            if (Text(identity, "processStartedUtc") is { } started &&
                DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                try
                {
                    var fileTime = checked((ulong)timestamp.ToFileTime());
                    if (_processStarted.HasValue && _processStarted != fileTime) _invalidHeader = true;
                    _processStarted ??= fileTime;
                }
                catch (ArgumentOutOfRangeException) { _invalidHeader = true; }
            }
            _sequence = Unsigned(identity, "sequence") ?? 0;
            if (identity.TryGetProperty("guestRunProfile", out var profile))
            {
                _manifest = Text(profile, "sha256");
                if (!Digest(_manifest) || Text(profile, "status") != "validated-file-pins") _invalidHeader = true;
            }
        }
        if (row.TryGetProperty("scenario", out var scenario) && scenario.ValueKind == JsonValueKind.Object &&
            scenario.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            if (actions.GetArrayLength() > MaximumPulses) { _limit = true; return; }
            var index = 0;
            foreach (var action in actions.EnumerateArray())
            {
                if (Text(action, "kind") == "gamepad-pulse") _declared[index] = ActionSpecification(action);
                index++;
            }
        }
    }

    private void Bound(JsonElement row, RuntimeGamepadTracePoint point)
    {
        var identity = BindingIdentity(row);
        if (!_header || !_started || _ended || _invalidHeader || identity is null ||
            Text(row, "origin") != "bridge-control" || Text(row, "status") != "bound" ||
            Unsigned(row, "protocol") != 1 || point.Sequence is not > 0 || Unsigned(row, "requestId") is not > 0 || Unsigned(row, "dropped") is null ||
            identity.Session != _session || identity.ProcessId != _processId || identity.ProcessStartedFileTime != _processStarted ||
            _manifest is not null && !SameDigest(identity.RunManifestSha256, _manifest))
        {
            Note("binding-acknowledgement-invalid");
            _binding = null;
            return;
        }
        if (_lastBinding is { } previous && (identity.ConnectionGeneration != previous.ConnectionGeneration ||
            identity.CaptureEpoch != previous.CaptureEpoch || identity.BindingSerial <= previous.BindingSerial))
        {
            Note("binding-token-reused-or-capture-changed");
            _binding = null;
            return;
        }
        foreach (var pulse in _pulses.Values)
            if (pulse.Action is not null && pulse.Terminal is null) pulse.Fail("binding-replaced-during-pulse");
        _binding = new(identity, point, _record);
        _lastBinding = identity;
        _ordinal = _observationTime = 0;
    }

    private void Requested(JsonElement row, RuntimeGamepadTracePoint point)
    {
        if (!row.TryGetProperty("action", out var action) || Text(action, "kind") != "gamepad-pulse") return;
        var id = Unsigned(row, "requestId");
        if (id is not > 0) { Note("action-request-identity-unavailable"); return; }
        var pulse = Get(id.Value);
        if (pulse is null) return;
        if (pulse.Action is not null || pulse.Queued is not null || pulse.Terminal is not null) pulse.Fail("duplicate-or-late-request");
        pulse.Action ??= point;
        pulse.ActionIndex = Int32(row, "actionIndex");
        pulse.Specification = ActionSpecification(action);
        pulse.Binding = _binding;
        if (row.TryGetProperty("protocol", out _) || row.TryGetProperty("sequence", out _)) pulse.Fail("invalid-controller-envelope");
        if (!_header || _version is not (2 or 3) || _invalidHeader || !_started || _ended) pulse.Fail("request-outside-valid-scenario");
        if (pulse.Specification is null || pulse.ActionIndex is not { } index || !_declared.TryGetValue(index, out var expected) || expected != pulse.Specification)
            pulse.Fail("action-does-not-match-scenario");
        if (pulse.Binding is null || pulse.Binding.Record >= _record) pulse.Note("binding-unavailable");
        foreach (var other in _pulses.Values)
            if (!ReferenceEquals(other, pulse) && other.ActionIndex == pulse.ActionIndex) pulse.Fail("duplicate-action-index");
    }

    private void ControllerResult(JsonElement row, RuntimeGamepadTracePoint point)
    {
        if (Text(row, "actionKind") != "gamepad-pulse") return;
        var id = Unsigned(row, "observedRequestId");
        Pulse? pulse = id is { } value && _pulses.TryGetValue(value, out var byId) ? byId : null;
        var index = Int32(row, "actionIndex");
        pulse ??= _pulses.Values.FirstOrDefault(p => p.ActionIndex == index);
        if (pulse is null) { Note("controller-result-without-request"); return; }
        if (pulse.Controller is not null) pulse.Fail("duplicate-controller-result");
        pulse.Controller ??= point;
        if (Text(row, "origin") != "controller" || index != pulse.ActionIndex || row.TryGetProperty("protocol", out _) || row.TryGetProperty("sequence", out _))
            pulse.Fail("controller-result-identity-mismatch");
        if (Text(row, "status") == "matched")
        {
            if (id != pulse.Id || pulse.Terminal is null || Unsigned(row, "observedSequence") != pulse.Terminal.Sequence ||
                Project(pulse, false).Status != "Observed") pulse.Fail("matched-controller-result-without-observed-pulse");
        }
        else pulse.Note("controller-outcome:" + (Text(row, "status") ?? "unavailable"));
    }

    private void ValidateTiming(Pulse pulse)
    {
        if (pulse.Press is not { } press || pulse.Release is not { } release || pulse.Common is not { } common) return;
        if (press.Packet is not { } packet || release.Packet != unchecked(packet + 1U)) pulse.Fail("packet-transition-mismatch");
        if (press.ObservationOrdinal is not { } ordinal || release.ObservationOrdinal is not { } next || next <= ordinal)
            pulse.Fail("observation-ordinal-mismatch");
        if (press.ObservedMonotonicMilliseconds is not { } pressed || release.ObservedMonotonicMilliseconds is not { } released ||
            pressed < common.Admitted || released < pressed || released - pressed < (ulong)common.Specification.Hold ||
            released - common.Admitted >= (ulong)common.Specification.Deadline)
            pulse.Fail("pulse-timing-mismatch");
    }

    private RuntimeGamepadPulseResult Project(Pulse pulse, bool complete)
    {
        var notes = new List<string>(pulse.Diagnostics);
        if (pulse.Action is null) notes.Add("action-request-missing");
        if (pulse.Binding is null) notes.Add("binding-missing");
        if (pulse.Queued is null) notes.Add("queue-missing");
        if (pulse.Press is null) notes.Add("guest-press-missing");
        if (pulse.Release is null) notes.Add("guest-release-missing");
        if (pulse.Terminal is null) notes.Add("terminal-result-missing");
        if (complete && pulse.Controller is null) notes.Add("controller-result-missing");
        if (_eventLoss) notes.Add("capture-event-loss");
        if (_limit) notes.Add("validation-limit-reached");
        var status = pulse.Invalid ? "Invalid" : pulse.Action is null || pulse.Failed ? "Unavailable" : notes.Count > 0 ? "Partial" : "Observed";
        return new(pulse.Id, status, notes.Distinct(StringComparer.Ordinal).ToArray(), pulse.ActionIndex,
            pulse.Specification?.Slot, pulse.Specification?.Buttons, pulse.Specification?.Hold,
            pulse.Specification?.Deadline, pulse.Common?.Admitted, pulse.Binding?.Identity,
            pulse.Action, pulse.Binding?.Point, pulse.Queued, pulse.Press, pulse.Release, pulse.Terminal, pulse.Controller);
    }

    private Pulse? Get(ulong id)
    {
        if (_pulses.TryGetValue(id, out var found)) return found;
        if (_pulses.Count >= MaximumPulses) { _limit = true; Note("pulse-limit-reached"); return null; }
        var pulse = new Pulse(id);
        _pulses.Add(id, pulse);
        return pulse;
    }

    private void Note(string value) { if (_diagnostics.Count < 32 && !_diagnostics.Contains(value)) _diagnostics.Add(value); }
    private static bool Digest(string? text) => text is { Length: 64 } && text.All(char.IsAsciiHexDigit);
    private static bool SameDigest(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool SupportedFlags(long? flags) => flags is >= 0 and <= uint.MaxValue && (flags == 0 || (flags.Value & 1) != 0);
    private static string? Text(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static ulong? Unsigned(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetUInt64(out var value) ? value : null;
    private static uint? UInt32(JsonElement row, string name) => Unsigned(row, name) is { } value && value <= uint.MaxValue ? (uint)value : null;
    private static int? Int32(JsonElement row, string name) => Integer(row, name) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
    private static long? Integer(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var value) ? value : null;
    private static bool True(JsonElement row, string name) => row.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private static Specification? ActionSpecification(JsonElement action) => action.TryGetProperty("gamepad", out var gamepad)
        ? SpecificationOf(gamepad, "buttons") : null;
    private static Specification? SpecificationOf(JsonElement row, string buttonField)
    {
        var slot = Int32(row, "slot");
        var buttons = UInt32(row, buttonField);
        var hold = Int32(row, "holdMilliseconds");
        var deadline = Int32(row, "deadlineMilliseconds");
        return slot == 0 && buttons is > 0 && (buttons & ~0x301FU) == 0 && (buttons & 3) != 3 && (buttons & 12) != 12 &&
            hold is >= 20 and <= 500 && deadline > hold && deadline <= 2000
            ? new(slot.Value, buttons.Value, hold.Value, deadline!.Value) : null;
    }
    private static RuntimeGamepadBindingIdentity? BindingIdentity(JsonElement row)
    {
        var token = Token(row);
        var pid = UInt32(row, "processId");
        var started = Unsigned(row, "processStartedFileTime");
        return token is null || pid is not > 0 || started is not > 0 ? null : new(token.Session, token.Manifest,
            token.Connection, token.Capture, token.Serial, token.Window, pid.Value, started.Value);
    }
    private static TokenValue? Token(JsonElement row)
    {
        var session = Text(row, "session");
        var digest = Text(row, "runManifestSha256");
        var connection = Unsigned(row, "connectionGeneration");
        var capture = Unsigned(row, "captureEpoch");
        var serial = Unsigned(row, "bindingSerial");
        var window = Unsigned(row, "windowIdentity");
        return session is not { Length: > 0 and <= 128 } || !Digest(digest) || connection is not > 0 || capture is not > 0 || serial is not > 0 || window is not > 0
            ? null : new(session, digest!.ToLowerInvariant(), connection.Value, capture.Value, serial.Value, window.Value);
    }
    private CommonValue? Common(JsonElement row)
    {
        var token = Token(row);
        var spec = SpecificationOf(row, "requestedButtons");
        var admitted = Unsigned(row, "admittedMonotonicMilliseconds");
        return token is null || spec is null || admitted is null || _binding is null ? null : new(new(token.Session, token.Manifest,
            token.Connection, token.Capture, token.Serial, token.Window, _binding.Identity.ProcessId, _binding.Identity.ProcessStartedFileTime), spec, admitted.Value);
    }
    private sealed record TokenValue(string Session, string Manifest, ulong Connection, ulong Capture, ulong Serial, ulong Window);
    private sealed record Specification(int Slot, uint Buttons, int Hold, int Deadline);
    private sealed record Binding(RuntimeGamepadBindingIdentity Identity, RuntimeGamepadTracePoint Point, long Record);
    private sealed record CommonValue(RuntimeGamepadBindingIdentity Identity, Specification Specification, ulong Admitted);
    private sealed class Pulse(ulong id)
    {
        internal ulong Id { get; } = id;
        internal int? ActionIndex;
        internal Specification? Specification;
        internal Binding? Binding;
        internal CommonValue? Common;
        internal RuntimeGamepadTracePoint? Action, Queued, Terminal, Controller;
        internal RuntimeGamepadStateObservation? Press, Release;
        internal bool Invalid, Failed;
        internal List<string> Diagnostics { get; } = [];
        internal void Note(string value) { if (Diagnostics.Count < 32 && !Diagnostics.Contains(value)) Diagnostics.Add(value); }
        internal void Fail(string value) { Invalid = true; Note(value); }
    }
}
