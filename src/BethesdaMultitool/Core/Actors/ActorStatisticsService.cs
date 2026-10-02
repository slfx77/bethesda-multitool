using System.Globalization;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.RuntimeSession;

namespace BethesdaMultitool.Core.Actors;

internal sealed record ActorCalculationInput(string Key, double Value, string Provenance,
    uint? SourceActor, string? SourcePlugin);

internal sealed record ActorStatisticValue(string Key, string Status, double? Value, string? Evidence,
    IReadOnlyList<string> MissingDependencies)
{
    internal uint? ReferenceFormId { get; init; }
    internal ulong? Sequence { get; init; }
    internal uint? HitFlags { get; init; }
    internal string? TraceSha256 { get; init; }
    internal string? TraceStatus { get; init; }
    internal IReadOnlyList<string> TraceDiagnostics { get; init; } = [];
    internal string? EngineProfile { get; init; }
    internal string? CalculationBasis { get; init; }
    internal IReadOnlyList<ActorCalculationInput> Inputs { get; init; } = [];
}

/// <summary>Shared authored, inherited and observed values; calculations require a calibrated engine profile.</summary>
internal static class ActorStatisticsService
{
    internal static ActorStatisticValue StoredHealth(NpcRecord npc) => new("BaseHealth",
        npc.BaseHealth.HasValue ? "Stored" : "Unavailable", npc.BaseHealth, "NPC_.DATA.BaseHealth", []);

    /// <summary>Stored SPECIAL alone cannot supply a runtime critical-chance value.</summary>
    internal static ActorStatisticValue CriticalChance(string? engineProfile = null) => new(
        "RuntimeCriticalChance", "Unavailable", null, null,
        [engineProfile == null ? "engine-profile" : $"calibration:{engineProfile}", "actor-value snapshot"]);

    internal static string Format(ActorStatisticValue value) => value.Value?.ToString("R", CultureInfo.InvariantCulture) ?? "Unavailable";

    internal static async Task<IReadOnlyList<ActorStatisticValue>> InspectAsync(ActorInspection actor,
        string? engineProfile = null, string? scenarioPath = null, CancellationToken cancellationToken = default,
        RuntimeTraceSources? sources = null)
    {
        var profile = ActorEngineProfile.Resolve(engineProfile);
        ushort? scenarioPlayerLevel = null;
        string? scenarioExecutableHash = null;
        if (scenarioPath is not null)
        {
            if (new FileInfo(scenarioPath).Length > 65536) throw new InvalidDataException("Statistics scenario exceeds 64 KiB.");
            using var scenario = JsonDocument.Parse(await File.ReadAllTextAsync(scenarioPath, cancellationToken));
            if (scenario.RootElement.TryGetProperty("playerLevel", out var scenarioLevel))
                scenarioPlayerLevel = scenarioLevel.GetUInt16();
            if (scenario.RootElement.TryGetProperty("executableSha256", out var executable))
                scenarioExecutableHash = executable.GetString();
        }
        var values = new List<ActorStatisticValue>();
        foreach (var stored in actor.Statistics)
        {
            if (double.TryParse(stored.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                values.Add(new(stored.Key, "Stored", number, $"{actor.Kind}:0x{actor.FormId:X8}", []));
        }
        foreach (var inherited in actor.EffectiveStatistics.Where(item => item.Provenance == "Inherited"))
        {
            if (double.TryParse(inherited.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                values.Add(new(inherited.Key, "Inherited", number, $"{inherited.SourcePlugin}:0x{inherited.SourceActor:X8}", []));
        }
        // These dependencies identify the next required engine checks without substituting SPECIAL estimates.
        var profileDependency = engineProfile == null ? "engine-profile" : $"calibration:{engineProfile}";
        var scenarioDependency = scenarioPath == null ? "scenario" : $"scenario:{Path.GetFullPath(scenarioPath)}";
        values.Add(new("RuntimeHealth", "Unavailable", null, null, [profileDependency, "actor-value snapshot"]));
        values.Add(CriticalChance(engineProfile));
        values.Add(ActorLevelCalculation.Evaluate(actor, profile, scenarioPlayerLevel, scenarioExecutableHash));
        values.Add(new("RuntimeDamage", "Unavailable", null, null,
            [profileDependency, scenarioDependency, "weapon/ammo/effects/target snapshot"]));
        if (scenarioPath != null)
            await AddObservations(values, actor, profile, scenarioPath, sources, cancellationToken);
        return values;
    }

    private static async Task AddObservations(List<ActorStatisticValue> values, ActorInspection actor, ActorEngineProfile? profile,
        string scenarioPath, RuntimeTraceSources? sources, CancellationToken cancellationToken)
    {
        if (new FileInfo(scenarioPath).Length > 65536) throw new InvalidDataException("Statistics scenario exceeds 64 KiB.");
        using var scenario = JsonDocument.Parse(await File.ReadAllTextAsync(scenarioPath, cancellationToken));
        if (!scenario.RootElement.TryGetProperty("runtimeTrace", out var traceProperty))
        {
            if (scenario.RootElement.TryGetProperty("calculationObservationSequence", out _))
                values.Add(new("ActorCalculations", "Unavailable", null, null, ["runtimeTrace"]));
            if (scenario.RootElement.TryGetProperty("damageObservationSequence", out _))
                values.Add(new("WeaponDamage.preTargetStage", "Unavailable", null, null, ["runtimeTrace"]));
            if (scenario.RootElement.TryGetProperty("criticalObservationSequence", out _))
                values.Add(new("CritChance.invocation", "Unavailable", null, null, ["runtimeTrace"]));
            return;
        }
        var tracePath = Path.GetFullPath(traceProperty.GetString()!, Path.GetDirectoryName(Path.GetFullPath(scenarioPath))!);
        var reference = ReadFormId(scenario.RootElement.GetProperty("referenceFormId"));
        var expectedHash = scenario.RootElement.GetProperty("executableSha256").GetString();
        await using var trace = File.OpenRead(tracePath);
        var document = await RuntimeTraceImporter.ReadDocumentAsync(trace, cancellationToken);
        var summary = document.Summary;
        var executableHash = document.Identity.ValueKind == JsonValueKind.Object
            ? RuntimeTraceDocument.Text(document.Identity, "executableFileSha256") : null;
        if (string.IsNullOrWhiteSpace(expectedHash) || !string.Equals(executableHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Trace executable hash does not match the statistics scenario.");
        var binding = RuntimeTraceBinding.Match(document, sources);
        if (!binding.Matched)
        {
            values.Add(new("RuntimeTrace", binding.Status, null, tracePath, [binding.Reason])
            {
                TraceSha256 = summary.Sha256, TraceStatus = summary.Complete ? "Complete" : "Partial",
                TraceDiagnostics = summary.Diagnostics
            });
            return;
        }
        foreach (var item in document.Events)
        {
            if (item.Kind != "snapshot" || item.TargetFormId != reference || item.BaseFormId != actor.FormId ||
                item.TargetKind != "actor" || item.Statistic is null || item.Component is null || item.Value is not { } observed) continue;
            values.Add(new($"{item.Statistic}.{item.Component}", "Observed", observed,
                $"{tracePath}#L{item.Line}", [])
            {
                ReferenceFormId = reference, Sequence = item.Sequence,
                TraceSha256 = summary.Sha256, TraceStatus = summary.Complete ? "Complete" : "Partial",
                TraceDiagnostics = summary.Diagnostics
            });
        }
        AddInvocationObservations(values, actor, profile, scenario.RootElement, document, reference, tracePath, executableHash);
        if (scenario.RootElement.TryGetProperty("calculationObservationSequence", out var selected))
        {
            var sequence = selected.GetUInt64();
            var observation = document.Events.SingleOrDefault(item => item.Kind == "actor-state" && item.Sequence == sequence &&
                item.TargetFormId == reference && item.BaseFormId == actor.FormId && item.TargetKind == "actor");
            if (profile is null || actor.Game != BethesdaGame.FalloutNewVegas || !summary.Complete || observation is null)
            {
                values.Add(new("ActorCalculations", "Unavailable", null, tracePath,
                    ["engine profile, complete bound FNV trace and exact actor-state sequence"]));
                return;
            }
            using var source = JsonDocument.Parse(document.ReadSourceLine(observation));
            var state = ActorValueObservationReader.Read(source.RootElement);
            var consistentKind = (actor.Kind == "NPC_" && state.Role is ActorCalculationRole.Player or ActorCalculationRole.Npc) ||
                (actor.Kind == "CREA" && state.Role == ActorCalculationRole.Creature);
            if (!consistentKind || !string.Equals(state.ExecutableSha256, executableHash, StringComparison.OrdinalIgnoreCase))
            {
                values.Add(new("ActorCalculations", "Unavailable", null, $"{tracePath}#L{observation.Line}",
                    ["actor-state executable and actor kind matching the admitted trace and selected record"])
                {
                    ReferenceFormId = reference, Sequence = observation.Sequence, TraceSha256 = summary.Sha256,
                    TraceStatus = "Complete", TraceDiagnostics = summary.Diagnostics
                });
                return;
            }
            List<ActorStatisticValue> stages = [.. ActorValueCalculation.Evaluate(profile, state)];
            if (state.WeaponCritical is not null &&
                ActorValueObservationReader.ReadWeaponCriticalResult(source.RootElement) is { } observedCritical)
                stages.Add(new("CritChance.weaponStage", "Observed", observedCritical,
                    "PC:00646D80; equipped-weapon stage return", []) { EngineProfile = profile.Id });
            foreach (var value in stages)
                values.Add(value with
                {
                    Evidence = value.Evidence is null ? $"{tracePath}#L{observation.Line}" : $"{value.Evidence}; {tracePath}#L{observation.Line}",
                    ReferenceFormId = reference, Sequence = observation.Sequence,
                    TraceSha256 = summary.Sha256, TraceStatus = "Complete", TraceDiagnostics = summary.Diagnostics
                });
        }
    }

    private static void AddInvocationObservations(List<ActorStatisticValue> values, ActorInspection actor,
        ActorEngineProfile? profile, JsonElement scenario, RuntimeTraceDocument document, uint reference,
        string tracePath, string? executableHash)
    {
        var start = document.Events.SingleOrDefault(item => item.Kind == "capture-start");
        using var startJson = start is null ? null : JsonDocument.Parse(document.ReadSourceLine(start));
        JsonElement boundary = default;
        if (startJson is not null && startJson.RootElement.TryGetProperty("dispatchWindow", out var window)) boundary = window;
        var capture = Counter(boundary, "captureGeneration");
        var connection = Counter(boundary, "connectionGeneration");
        ReadSelection("damageObservationSequence", "damage-stage", "WeaponDamage.preTargetStage", false);
        ReadSelection("criticalObservationSequence", "critical-invocation", "CritChance.invocation", true);

        void ReadSelection(string selector, string kind, string key, bool critical)
        {
            if (!scenario.TryGetProperty(selector, out var selected)) return;
            var sequence = selected.ValueKind == JsonValueKind.Number && selected.TryGetUInt64(out var number) ? number : 0;
            var observation = document.Events.SingleOrDefault(item => item.Sequence == sequence && item.Kind == kind &&
                item.TargetKind == "actor" && item.TargetFormId == reference && item.BaseFormId == actor.FormId);
            if (profile is null || profile.Id != ActorEngineProfile.PcRetailId || actor.Game != BethesdaGame.FalloutNewVegas ||
                !string.Equals(profile.ExecutableSha256, executableHash, StringComparison.OrdinalIgnoreCase) ||
                !document.Summary.Complete || observation is null || capture is not > 0 || connection is not > 0)
            {
                values.Add(new(key, "Unavailable", null, tracePath,
                    ["complete bound PC trace, exact invocation sequence and capture generations"]));
                return;
            }
            using var source = JsonDocument.Parse(document.ReadSourceLine(observation));
            IReadOnlyList<ActorStatisticValue> stages;
            if (critical)
            {
                stages = ActorCriticalInvocationObservationReader.Read(source.RootElement, reference, actor.FormId,
                    actor.Kind, capture.Value, connection.Value);
            }
            else
            {
                var row = ActorMeleeDamageObservationReader.Read(source.RootElement, reference, actor.FormId,
                    actor.Kind, capture.Value, connection.Value);
                var damage = new List<ActorStatisticValue>();
                damage.Add(row.Inputs is { } inputs ? ActorMeleeDamageCalculation.EvaluateStage(profile, inputs) :
                    new(key, "Unavailable", null, null, row.MissingDependencies));
                if (row.StageResult is { } stageResult)
                    damage.Add(new(key, "Observed", stageResult, "PC:00644CE0 return at 0064508E", []));
                if (row.HitHealthDamage is { } hitDamage)
                    damage.Add(new("RuntimeDamage", "Observed", hitDamage,
                        "Prepared hit: PC:009B5170 return hit+0x14", []) { HitFlags = row.HitFlags });
                stages = damage;
            }
            foreach (var value in stages)
            {
                if ((value.Key is "RuntimeDamage" or "RuntimeCriticalChance") && value.Status == "Observed")
                    values.RemoveAll(item => item.Key == value.Key && item.Status == "Unavailable");
                values.Add(value with
                {
                    Evidence = value.Evidence is null ? $"{tracePath}#L{observation.Line}" : $"{value.Evidence}; {tracePath}#L{observation.Line}",
                    EngineProfile = profile.Id, ReferenceFormId = reference, Sequence = observation.Sequence,
                    TraceSha256 = document.Summary.Sha256, TraceStatus = "Complete",
                    TraceDiagnostics = [.. document.Summary.Diagnostics, .. value.TraceDiagnostics]
                });
            }
        }

        static ulong? Counter(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
            field.TryGetUInt64(out var number) ? number : null;
    }

    private static uint ReadFormId(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.GetUInt32();
        var text = value.GetString()!;
        return uint.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.AsSpan(2) : text.AsSpan(),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    internal static void Write(Utf8JsonWriter writer, IReadOnlyList<ActorStatisticValue> values,
        string? engineProfile, string? scenarioPath)
    {
        writer.WriteStartObject("runtimeStatistics");
        writer.WriteString("engineProfile", engineProfile);
        writer.WriteString("scenario", scenarioPath);
        writer.WriteStartArray("values");
        foreach (var value in values)
        {
            writer.WriteStartObject();
            writer.WriteString("key", value.Key);
            writer.WriteString("status", value.Status);
            if (value.Value is { } number) writer.WriteNumber("value", number); else writer.WriteNull("value");
            writer.WriteString("evidence", value.Evidence);
            writer.WriteString("engineProfile", value.EngineProfile);
            writer.WriteString("calculationBasis", value.CalculationBasis);
            writer.WriteStartArray("inputs");
            foreach (var input in value.Inputs)
            {
                writer.WriteStartObject();
                writer.WriteString("key", input.Key);
                writer.WriteNumber("value", input.Value);
                writer.WriteString("provenance", input.Provenance);
                if (input.SourceActor is { } sourceActor) writer.WriteNumber("sourceActor", sourceActor);
                writer.WriteString("sourcePlugin", input.SourcePlugin);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (value.ReferenceFormId is { } reference) writer.WriteNumber("referenceFormId", reference);
            if (value.Sequence is { } sequence) writer.WriteNumber("sequence", sequence);
            if (value.HitFlags is { } hitFlags) writer.WriteNumber("hitFlags", hitFlags);
            writer.WriteString("traceSha256", value.TraceSha256);
            writer.WriteString("traceStatus", value.TraceStatus);
            writer.WriteStartArray("traceDiagnostics");
            foreach (var diagnostic in value.TraceDiagnostics) writer.WriteStringValue(diagnostic);
            writer.WriteEndArray();
            writer.WriteStartArray("missingDependencies");
            foreach (var dependency in value.MissingDependencies) writer.WriteStringValue(dependency);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
