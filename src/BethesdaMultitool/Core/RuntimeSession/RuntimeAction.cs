using System.Globalization;
using System.Text.RegularExpressions;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Actions are requests; their effects require separate engine observations.</summary>
public sealed record RuntimeAction(string Kind, string? Target = null, string? Name = null, double? Value = null,
    string? Command = null, string? Plugin = null, string? FormId = null,
    string? Message = null, string? Button = null, int? ButtonIndex = null,
    int? TimeoutMilliseconds = null, int? PollMilliseconds = null,
    string? OtherPlugin = null, string? OtherFormId = null, string? OtherTarget = null,
    int? Count = null, int? Index = null, RuntimeGamepadPulse? Gamepad = null,
    string? SubjectPlugin = null, string? SubjectFormId = null)
{
    internal RuntimeRequestKind RequestKind
    {
        get
        {
            if (Kind != "gamepad-pulse") return ToFrame(1).Kind;
            ValidateGamepad();
            return RuntimeRequestKind.GamepadPulse;
        }
    }

    internal void Validate() => _ = RequestKind;

    public RuntimeFrame ToFrame(ulong requestId) => ToFrame(requestId, null);

    internal RuntimeFrame ToFrame(ulong requestId, string? boundManifestSha256)
    {
        if (Kind != "read-quest-conditions" && (SubjectPlugin is not null || SubjectFormId is not null))
            throw new ArgumentException("Explicit condition subjects require read-quest-conditions.");
        if (Kind == "gamepad-pulse")
        {
            ValidateGamepad();
            return new(RuntimeRequestKind.GamepadPulse, requestId, Gamepad!.Payload(boundManifestSha256));
        }
        if (Gamepad is not null) throw new ArgumentException("Gamepad data requires the gamepad-pulse action.");
        if (RuntimeOwnerConditionAction.TryCreate(this, requestId) is { } conditions) return conditions;
        if (RuntimeGameSettingAction.TryCreate(this, requestId) is { } setting) return setting;
        if (RuntimeReferenceVariableAction.TryCreate(this, requestId) is { } local) return local;
        if (RuntimeTypedAction.TryCreate(this, requestId) is { } typed) return typed;
        var numeric = Value is { } value && double.IsFinite(value)
            ? value.ToString("R", CultureInfo.InvariantCulture) : null;
        return Kind switch
        {
            "read-quest-variable" when Plugin is not null || FormId is not null =>
                Frame(RuntimeRequestKind.QuestRead, QuestIdentity()),
            "read-quest-variable" => Frame(RuntimeRequestKind.Evaluate, $"{Id(Target)}.{Id(Name)}"),
            "read-global" => Frame(RuntimeRequestKind.Evaluate, Id(Target)),
            "read-actor-value" => Frame(RuntimeRequestKind.Evaluate, $"{Id(Target)}.GetAV {Id(Name)}"),
            "read-base-actor-value" => Frame(RuntimeRequestKind.Evaluate, $"{Id(Target)}.GetBaseAV {Id(Name)}"),
            "read-permanent-actor-value" => Frame(RuntimeRequestKind.Evaluate, $"{Id(Target)}.GetPermAV {Id(Name)}"),
            "read-actor-state" => Frame(RuntimeRequestKind.ActorSnapshot, ActorIdentity()),
            "set-actor-value" when Name == "Endurance" && Value is double requested && requested is >= 1 and <= 10 && requested == Math.Truncate(requested)
                => Frame(RuntimeRequestKind.ActorSet, $"{ActorIdentity()}\tEndurance\t{Number()}"),
            "read-message-state" => Frame(RuntimeRequestKind.MessageMenu, "inspect"),
            "wait-message-state" => WaitForMenu(),
            "choose-message" when Target is "probe" or "visible" && ButtonIndex == 0
                => Frame(RuntimeRequestKind.MessageMenu, $"choose\t{Target}\t0\t{MenuText(Message, 1023)}\t{MenuText(Button, 127)}"),
            "read-condition-probe" => Frame(RuntimeRequestKind.ConditionProbe, "player-get-dead"),
            "message-probe" => Frame(RuntimeRequestKind.MessageProbe, "show-ok"),
            "set-quest-variable" when Plugin is not null || FormId is not null =>
                Frame(RuntimeRequestKind.QuestSet, $"{QuestIdentity()}\t{QuestNumber()}"),
            "set-quest-variable" => Frame(RuntimeRequestKind.Execute, $"set {Id(Target)}.{Id(Name)} to {Number()}"),
            "set-global" => Frame(RuntimeRequestKind.Execute, $"set {Id(Target)} to {Number()}"),
            "damage-actor-value" => Frame(RuntimeRequestKind.Execute, $"{Id(Target)}.DamageAV {Id(Name)} {Number()}"),
            "console" when !string.IsNullOrWhiteSpace(Command) && Command.Length <= 4096 &&
                           Command.All(c => c is >= ' ' and <= '~')
                => Frame(RuntimeRequestKind.Execute, Command),
            _ => throw new ArgumentException($"Unsupported or invalid runtime action: {Kind}.")
        };

        string Number() => numeric ?? throw new ArgumentException("Action requires a finite value.");
        RuntimeFrame WaitForMenu()
        {
            _ = MenuText(Message, 1023);
            _ = MenuText(Button, 127);
            if (ButtonIndex != 0 || Target is not (null or "visible" or "probe") ||
                TimeoutMilliseconds is not (>= 1 and <= 30000) ||
                PollMilliseconds is not (>= 50 and <= 1000))
                throw new ArgumentException("Menu waits require exact text/button/index, a 1–30000 ms timeout and a 50–1000 ms poll interval.");
            return Frame(RuntimeRequestKind.MessageMenu, "inspect");
        }
        static string MenuText(string? text, int limit) => text is { Length: > 0 } && text.Length <= limit &&
            text.All(c => c is >= ' ' and < '\x7F') ? text : throw new ArgumentException("Message actions require bounded exact ASCII text and button labels.");
        string QuestNumber() => Value is { } number && Math.Abs(number) <= float.MaxValue
            ? Number() : throw new ArgumentException("Quest-variable writes require a finite single-precision value.");
        string QuestIdentity() => $"{PluginLocalIdentity()}\t{Id(Name)}";
        string ActorIdentity()
        {
            if (Plugin is null && FormId is null)
                return Target == "player" ? "player" : throw new ArgumentException("Actor state requires player or a plugin/local reference identity.");
            if (Target is not null) throw new ArgumentException("Actor state must use one target identity.");
            return PluginLocalIdentity();
        }
        string PluginLocalIdentity()
        {
            if (Plugin is not { Length: > 4 and <= 255 } ||
                !Plugin.All(c => c is >= ' ' and <= '~' && c is not ('"' or '\\' or '/' or ':' or '\t')) ||
                !(Plugin.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || Plugin.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Runtime form actions require a plugin filename.");
            var local = FormId?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true ? FormId[2..] : FormId;
            if (local is null || local.Length is < 1 or > 8 ||
                !uint.TryParse(local, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) || id is 0 or > 0xFFFFFF)
                throw new ArgumentException("Runtime form actions require a nonzero local FormID with no runtime load-order prefix.");
            return $"{Plugin}\t{id:X6}";
        }
        RuntimeFrame Frame(RuntimeRequestKind kind, string text) => new(kind, requestId, text);
    }

    private void ValidateGamepad()
    {
        if (Gamepad is null || Target is not null || Name is not null || Value is not null ||
            Command is not null || Plugin is not null || FormId is not null || Message is not null ||
            Button is not null || ButtonIndex is not null || TimeoutMilliseconds is not null ||
            PollMilliseconds is not null || OtherPlugin is not null || OtherFormId is not null ||
            OtherTarget is not null || Count is not null || Index is not null || SubjectPlugin is not null || SubjectFormId is not null)
            throw new ArgumentException("Gamepad actions require only a typed gamepad pulse.");
        Gamepad.Validate();
    }

    private static string Id(string? text) =>
        text is not null && Regex.IsMatch(text, @"^[A-Za-z_][A-Za-z_0-9]{0,127}$", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100))
            ? text : throw new ArgumentException("Runtime state targets must be EditorIDs or variable/actor-value names.");
}

public sealed record RuntimeScenario(IReadOnlyList<RuntimeAction> Actions, int ObserveMilliseconds = 1000,
    RuntimeScriptTraceOptions? ScriptTrace = null, IReadOnlyList<RuntimeMilestone>? Milestones = null);
