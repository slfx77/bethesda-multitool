using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class WeatherTransitionSelectionResolverTests
{
    [Fact]
    public void ClimateDefault_ResolvesRuntimeCurrentOutgoingAndAuthoredWeight()
    {
        var current = Weather(0x000FFC88, "Current");
        var outgoing = Weather(0x001237D7, "Outgoing");
        var snapshot = Snapshot(current.FormId, outgoing.FormId, 0.625f);

        var result = WeatherTransitionSelectionResolver.Resolve(
            selectedWeather: null,
            isClimateDefaultSelection: true,
            climateDefaultWeather: Weather(0x10, "Default"),
            runtimeSnapshot: snapshot,
            weathersByFormId: new Dictionary<uint, WeatherRecord>
            {
                [current.FormId] = current,
                [outgoing.FormId] = outgoing,
            });

        Assert.Same(current, result.CurrentWeather);
        Assert.Same(outgoing, result.OutgoingWeather);
        Assert.Equal(0.625f, result.CurrentWeatherWeight);
        Assert.True(result.UsesRuntimeTransition);
        Assert.Null(result.ModifierElapsedSeconds);
        Assert.Contains("runtimeOutgoing=001237D7", result.Telemetry, StringComparison.Ordinal);
        Assert.Contains("runtimeCurrentWeight=0.625", result.Telemetry, StringComparison.Ordinal);
        Assert.Contains("modifierElapsed=unknown", result.Telemetry, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitWeather_IsAtomicAndIgnoresRuntimeTransition()
    {
        var explicitWeather = Weather(0x20, "Explicit");
        var runtimeCurrent = Weather(0x30, "Runtime");

        var result = WeatherTransitionSelectionResolver.Resolve(
            explicitWeather,
            isClimateDefaultSelection: false,
            climateDefaultWeather: Weather(0x10, "Default"),
            runtimeSnapshot: Snapshot(runtimeCurrent.FormId, 0x40, 0.25f),
            weathersByFormId: new Dictionary<uint, WeatherRecord>
            {
                [runtimeCurrent.FormId] = runtimeCurrent,
            });

        Assert.Same(explicitWeather, result.CurrentWeather);
        Assert.Null(result.OutgoingWeather);
        Assert.Equal(1f, result.CurrentWeatherWeight);
        Assert.False(result.UsesRuntimeTransition);
        Assert.Contains("explicit-selection-ignores-runtime", result.Telemetry, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedOutgoing_FailsClosedWithoutDimmingResolvedCurrent()
    {
        var current = Weather(0x30, "Runtime");
        var result = WeatherTransitionSelectionResolver.Resolve(
            selectedWeather: null,
            isClimateDefaultSelection: true,
            climateDefaultWeather: Weather(0x10, "Default"),
            runtimeSnapshot: Snapshot(current.FormId, 0xDEADBEEF, 0.25f),
            weathersByFormId: new Dictionary<uint, WeatherRecord> { [current.FormId] = current });

        Assert.Same(current, result.CurrentWeather);
        Assert.Null(result.OutgoingWeather);
        Assert.Equal(1f, result.CurrentWeatherWeight);
        Assert.Equal(0xDEADBEEFu, result.AuthoredOutgoingWeatherFormId);
        Assert.Equal(0.25f, result.AuthoredCurrentWeatherWeight);
        Assert.Contains("reason=runtime-outgoing-unresolved", result.Telemetry, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedCurrent_FallsBackToClimateDefaultAtomically()
    {
        var fallback = Weather(0x10, "Default");
        var outgoing = Weather(0x40, "Outgoing");
        var result = WeatherTransitionSelectionResolver.Resolve(
            selectedWeather: null,
            isClimateDefaultSelection: true,
            climateDefaultWeather: fallback,
            runtimeSnapshot: Snapshot(0xDEADBEEF, outgoing.FormId, 0.25f),
            weathersByFormId: new Dictionary<uint, WeatherRecord> { [outgoing.FormId] = outgoing });

        Assert.Same(fallback, result.CurrentWeather);
        Assert.Null(result.OutgoingWeather);
        Assert.Equal(1f, result.CurrentWeatherWeight);
        Assert.Contains("reason=runtime-current-unresolved", result.Telemetry, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordCollectionMerge_PreservesNonNullSnapshotAndLetsLaterRuntimeDataWin()
    {
        var snapshot = Snapshot(0x30, 0x40, 0.5f);
        var baseRecords = new RecordCollection { RuntimeWeatherTransition = snapshot };
        var merged = baseRecords.MergeWith(new RecordCollection());

        Assert.Same(snapshot, merged.RuntimeWeatherTransition);

        var overlaySnapshot = Snapshot(0x50, 0x60, 0.75f);
        var overlaid = merged.MergeWith(new RecordCollection { RuntimeWeatherTransition = overlaySnapshot });
        Assert.Same(overlaySnapshot, overlaid.RuntimeWeatherTransition);
    }

    [Fact]
    public void RuntimeTransition_UsesAppliedAtomicFallbackButRetainsAuthoredIdsForDiagnostics()
    {
        var current = Weather(0x30, "Current");
        var weatherIndex = new Dictionary<uint, WeatherRecord> { [current.FormId] = current };
        var first = WeatherTransitionSelectionResolver.Resolve(
            selectedWeather: null, isClimateDefaultSelection: true, climateDefaultWeather: null,
            runtimeSnapshot: Snapshot(current.FormId, 0xDEAD0001, 0.25f),
            weathersByFormId: weatherIndex);
        var second = WeatherTransitionSelectionResolver.Resolve(
            selectedWeather: null, isClimateDefaultSelection: true, climateDefaultWeather: null,
            runtimeSnapshot: Snapshot(current.FormId, 0xDEAD0002, 0.75f),
            weathersByFormId: weatherIndex);

        Assert.Equal(0x30u, first.AppliedCurrentWeatherFormId);
        Assert.Null(first.AppliedOutgoingWeatherFormId);
        Assert.Equal(1f, first.CurrentWeatherWeight);
        Assert.Equal(0xDEAD0001u, first.AuthoredOutgoingWeatherFormId);
        Assert.Equal(0xDEAD0002u, second.AuthoredOutgoingWeatherFormId);
    }

    private static WeatherRecord Weather(uint formId, string editorId) =>
        new() { FormId = formId, EditorId = editorId };

    private static WeatherTransitionSnapshot Snapshot(uint? current, uint? outgoing, float weight) =>
        new(
            SkyVirtualAddress: 0x40000100,
            CurrentWeatherPointer: current is null ? null : 0x40000400,
            CurrentWeatherFormId: current,
            OutgoingWeatherPointer: outgoing is null ? null : 0x40000440,
            OutgoingWeatherFormId: outgoing,
            CurrentWeatherWeight: weight,
            ModifierElapsedSeconds: null);
}
