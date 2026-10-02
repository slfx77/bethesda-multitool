using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>An indexed observation and its exact line in an immutable imported trace.</summary>
public sealed record RuntimeTraceEvent(int Line, int Offset, int Length, string Kind, ulong Sequence,
    ulong? Frame, uint? TargetFormId, uint? BaseFormId, uint? ScriptFormId, uint? SecondaryFormId,
    string? Statistic, string? Component, double? Value, string? TargetKind)
{
    public bool RefersTo(uint formId) => TargetFormId == formId || BaseFormId == formId ||
        ScriptFormId == formId || SecondaryFormId == formId;

    public string Description => Value is { } value
        ? $"{Kind}: {Statistic}.{Component} = {value.ToString("R", CultureInfo.InvariantCulture)}"
        : Kind;
}

/// <summary>A controller outcome for the session, without native sequence or record ownership.</summary>
public sealed record RuntimeTraceControllerResult(int Line, int Offset, int Length, string Status,
    int? ActionIndex, string? ActionKind, double? ElapsedMilliseconds,
    ulong? ObservedRequestId, ulong? ObservedSequence, ulong? ObservedFrame, string? MilestoneId = null);

/// <summary>Keeps one captured byte buffer; source links never reread a potentially changed file.</summary>
public sealed class RuntimeTraceDocument
{
    private readonly byte[] _bytes;

    internal RuntimeTraceDocument(byte[] bytes, RuntimeTraceSummary summary, JsonElement identity,
        JsonElement pluginIdentity, IReadOnlyList<RuntimeTraceEvent> events,
        IReadOnlyList<RuntimeTraceControllerResult> controllerResults, RuntimeScriptCallReport scriptCalls,
        RuntimeGamepadTraceReport gamepad)
    {
        _bytes = bytes;
        Summary = summary;
        Identity = identity;
        PluginIdentity = pluginIdentity;
        Events = events;
        ControllerResults = controllerResults;
        ScriptCalls = scriptCalls;
        Gamepad = gamepad;
    }

    public RuntimeTraceSummary Summary { get; }
    public JsonElement Identity { get; }
    /// <summary>Capture-start identity when supplied, otherwise the preserved handshake identity.</summary>
    public JsonElement PluginIdentity { get; }
    public IReadOnlyList<RuntimeTraceEvent> Events { get; }
    /// <summary>Session outcomes; these are never attached to a selected game record.</summary>
    public IReadOnlyList<RuntimeTraceControllerResult> ControllerResults { get; }
    /// <summary>Observed call pairing; partial scopes do not change transport completion.</summary>
    public RuntimeScriptCallReport ScriptCalls { get; }
    /// <summary>Requested pulses matched to their binding and actual guest press/release returns.</summary>
    public RuntimeGamepadTraceReport Gamepad { get; }

    public string ReadSourceLine(RuntimeTraceEvent observation) =>
        Encoding.UTF8.GetString(_bytes, observation.Offset, observation.Length);

    public string ReadSourceLine(RuntimeTraceControllerResult result) =>
        Encoding.UTF8.GetString(_bytes, result.Offset, result.Length);

    public string ReadSourceLine(RuntimeGamepadTracePoint point) =>
        Encoding.UTF8.GetString(_bytes, point.Offset, point.Length);

    internal static uint? FormId(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number)) return number;
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()!;
        return uint.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.AsSpan(2) : text.AsSpan(),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    internal static string? Text(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
