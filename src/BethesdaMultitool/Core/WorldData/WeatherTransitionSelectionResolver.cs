using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Runtime;

namespace BethesdaMultitool;

/// <summary>
///     Pure result used by the renderer to distinguish the runtime Sky transition from an
///     explicitly selected preview weather. Authored IDs/weight remain available for diagnostics
///     even when a referenced WTHR was not retained and the applied transition must fail closed.
/// </summary>
internal readonly record struct ResolvedWeatherTransition(
    WeatherRecord? CurrentWeather,
    WeatherRecord? OutgoingWeather,
    float CurrentWeatherWeight,
    uint? AuthoredCurrentWeatherFormId,
    uint? AuthoredOutgoingWeatherFormId,
    float? AuthoredCurrentWeatherWeight,
    float? ModifierElapsedSeconds,
    bool UsesRuntimeTransition,
    string Telemetry)
{
    /// <summary>The WTHR identity actually applied after resolver fail-closed behavior.</summary>
    internal uint? AppliedCurrentWeatherFormId => CurrentWeather?.FormId;

    /// <summary>
    ///     The outgoing WTHR identity actually applied. This is null for atomic fallback even when
    ///     the dump authored an unresolved outgoing FormID retained by
    ///     <see cref="AuthoredOutgoingWeatherFormId" />.
    /// </summary>
    internal uint? AppliedOutgoingWeatherFormId => OutgoingWeather?.FormId;
}

internal static class WeatherTransitionSelectionResolver
{
    /// <summary>
    ///     Runtime state is consumed only for the climate-default sentinel. A concrete dropdown or
    ///     profiler selection is an atomic preview and therefore never inherits a stale outgoing weather.
    /// </summary>
    internal static ResolvedWeatherTransition Resolve(
        WeatherRecord? selectedWeather,
        bool isClimateDefaultSelection,
        WeatherRecord? climateDefaultWeather,
        WeatherTransitionSnapshot? runtimeSnapshot,
        IReadOnlyDictionary<uint, WeatherRecord>? weathersByFormId,
        WeatherRecord? deterministicRegionWeather = null)
    {
        if (!isClimateDefaultSelection)
        {
            var explicitCurrent = selectedWeather ?? climateDefaultWeather;
            return Atomic(explicitCurrent, "explicit", runtimeSnapshotIgnored: runtimeSnapshot is not null);
        }

        if (runtimeSnapshot is null)
        {
            return deterministicRegionWeather is not null
                ? Atomic(deterministicRegionWeather, "region-rdwt", runtimeSnapshotIgnored: false)
                : Atomic(climateDefaultWeather, "climate-default", runtimeSnapshotIgnored: false);
        }

        var current = ResolveWeather(runtimeSnapshot.CurrentWeatherFormId, weathersByFormId);
        if (current is null)
        {
            return RuntimeFallback(
                climateDefaultWeather,
                runtimeSnapshot,
                runtimeSnapshot.CurrentWeatherFormId is null
                    ? "runtime-current-null"
                    : "runtime-current-unresolved");
        }

        var weight = runtimeSnapshot.CurrentWeatherWeight;
        if (!float.IsFinite(weight) || weight < 0f || weight > 1f)
        {
            return RuntimeFallback(current, runtimeSnapshot, "runtime-weight-invalid");
        }

        var outgoing = ResolveWeather(runtimeSnapshot.OutgoingWeatherFormId, weathersByFormId);
        if (outgoing is null)
        {
            // A missing outgoing weather must not attenuate the resolved current weather. Retain the
            // authored ID/weight in telemetry while applying an atomic current result.
            return RuntimeFallback(
                current,
                runtimeSnapshot,
                runtimeSnapshot.OutgoingWeatherFormId is null
                    ? "runtime-outgoing-null"
                    : "runtime-outgoing-unresolved");
        }

        return new ResolvedWeatherTransition(
            current,
            outgoing,
            weight,
            runtimeSnapshot.CurrentWeatherFormId,
            runtimeSnapshot.OutgoingWeatherFormId,
            runtimeSnapshot.CurrentWeatherWeight,
            runtimeSnapshot.ModifierElapsedSeconds,
            UsesRuntimeTransition: true,
            FormatTelemetry(
                "runtime-sky",
                current.FormId,
                outgoing.FormId,
                weight,
                runtimeSnapshot,
                reason: null));
    }

    private static ResolvedWeatherTransition Atomic(
        WeatherRecord? current,
        string source,
        bool runtimeSnapshotIgnored)
    {
        var reason = runtimeSnapshotIgnored ? "explicit-selection-ignores-runtime" : null;
        return new ResolvedWeatherTransition(
            current,
            OutgoingWeather: null,
            CurrentWeatherWeight: 1f,
            AuthoredCurrentWeatherFormId: current?.FormId,
            AuthoredOutgoingWeatherFormId: null,
            AuthoredCurrentWeatherWeight: 1f,
            ModifierElapsedSeconds: null,
            UsesRuntimeTransition: false,
            Telemetry: FormatTelemetry(source, current?.FormId, null, 1f, null, reason));
    }

    private static ResolvedWeatherTransition RuntimeFallback(
        WeatherRecord? current,
        WeatherTransitionSnapshot snapshot,
        string reason)
    {
        return new ResolvedWeatherTransition(
            current,
            OutgoingWeather: null,
            CurrentWeatherWeight: 1f,
            AuthoredCurrentWeatherFormId: snapshot.CurrentWeatherFormId,
            AuthoredOutgoingWeatherFormId: snapshot.OutgoingWeatherFormId,
            AuthoredCurrentWeatherWeight: snapshot.CurrentWeatherWeight,
            ModifierElapsedSeconds: snapshot.ModifierElapsedSeconds,
            UsesRuntimeTransition: true,
            Telemetry: FormatTelemetry(
                "runtime-sky-fallback",
                current?.FormId,
                null,
                1f,
                snapshot,
                reason));
    }

    private static WeatherRecord? ResolveWeather(
        uint? formId,
        IReadOnlyDictionary<uint, WeatherRecord>? weathersByFormId)
    {
        return formId is { } id && weathersByFormId?.TryGetValue(id, out var weather) == true
            ? weather
            : null;
    }

    private static string FormatTelemetry(
        string source,
        uint? appliedCurrentId,
        uint? appliedOutgoingId,
        float appliedWeight,
        WeatherTransitionSnapshot? snapshot,
        string? reason)
    {
        static string Id(uint? value) => value is { } id ? $"{id:X8}" : "none";
        static string Weight(float? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";

        var elapsed = snapshot switch
        {
            null => "n/a",
            { ModifierElapsedSeconds: { } seconds } => seconds.ToString("0.###", CultureInfo.InvariantCulture),
            _ => "unknown"
        };
        var suffix = reason is null ? string.Empty : $" reason={reason}";
        return $"weatherSource={source} formIdDomain=TESForm.formID appliedCurrent={Id(appliedCurrentId)} " +
               $"appliedOutgoing={Id(appliedOutgoingId)} appliedCurrentWeight={Weight(appliedWeight)} " +
               $"runtimeCurrent={Id(snapshot?.CurrentWeatherFormId)} " +
               $"runtimeOutgoing={Id(snapshot?.OutgoingWeatherFormId)} " +
               $"runtimeCurrentWeight={Weight(snapshot?.CurrentWeatherWeight)} " +
               $"modifierElapsed={elapsed}{suffix}";
    }
}
