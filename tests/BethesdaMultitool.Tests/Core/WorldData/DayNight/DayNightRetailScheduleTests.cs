using BethesdaMultitool.Core.WorldData.DayNight;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData.DayNight;

/// <summary>
///     Retail gate for the day/night scan on the PC final master: the Strip's
///     <c>VStreetLightingScript</c> starter refs (0x0013BAE1/2/3) must come out gated with the
///     authored 20:00→6:00 night window, the generic <c>daynightenable</c> self-togglers
///     (FXGlowSimpFillRnd02DayNight instances) must gate themselves, and the scan must stay
///     bounded — a runaway parser that schedules hundreds of quest-scene refs would light up as a
///     count explosion here.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class DayNightRetailScheduleTests(SampleFileFixture samples)
{
    private const uint RetailStreetLightOneParent = 0x0013BAE3;
    private const uint RetailStreetLightTwoParent = 0x0013BAE2;
    private const uint RetailStreetLightThreeParent = 0x0013BAE1;

    [Fact]
    public async Task PcFinalMaster_SchedulesTheKnownDayNightNetworks()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC ESM sample not available");

        var result = await RealAssetEsmCache.LoadAsync(
            samples.PcFinalEsm!, TestContext.Current.CancellationToken);
        var records = result.Records;

        var schedule = DayNightRefSchedule.Build(
            records.Scripts, records.Cells, records.Activators, records.Lights);

        Assert.NotNull(schedule);

        // Strip street-light starters: ON at night, OFF at noon (VStreetLightingScript,
        // GetCurrentTime > 20 || < 6 — the nested flicker show must not confuse the scan).
        foreach (var starter in new[]
                 {
                     RetailStreetLightOneParent, RetailStreetLightTwoParent,
                     RetailStreetLightThreeParent,
                 })
        {
            Assert.True(
                schedule.TryGetDisabledAt(starter, 12f, out var noonDisabled) && noonDisabled,
                $"Starter 0x{starter:X8} should be scheduled and OFF at noon.");
            Assert.True(
                schedule.TryGetDisabledAt(starter, 23f, out var nightDisabled) && !nightDisabled,
                $"Starter 0x{starter:X8} should be ON at 23:00.");
            Assert.True(
                schedule.TryGetDisabledAt(starter, 5f, out var dawnDisabled) && !dawnDisabled,
                $"Starter 0x{starter:X8} should still be ON at 5:00.");
        }

        // The generic self-toggling glow activator: every placed FXGlowSimpFillRnd02DayNight
        // instance gates itself on the same 20→6 window.
        var glowBase = records.Activators.Single(
            activator => string.Equals(
                activator.EditorId, "FXGlowSimpFillRnd02DayNight", StringComparison.OrdinalIgnoreCase));
        var glowInstances = records.Cells
            .SelectMany(cell => cell.PlacedObjects)
            .Where(placement => placement.BaseFormId == glowBase.FormId)
            .Select(placement => placement.FormId)
            .Distinct()
            .ToList();
        Assert.NotEmpty(glowInstances);
        foreach (var instance in glowInstances)
        {
            Assert.True(
                schedule.TryGetDisabledAt(instance, 12f, out var disabled) && disabled,
                $"Glow instance 0x{instance:X8} should be OFF at noon.");
            Assert.True(
                schedule.TryGetDisabledAt(instance, 22f, out var nightDisabled) && !nightDisabled,
                $"Glow instance 0x{instance:X8} should be ON at 22:00.");
        }

        // Scan discipline: the retail master's hour-driven networks are numbered in the hundreds
        // (street-light XESP children dominate). Thousands would mean quest-scene refs leaked in.
        Assert.InRange(schedule.RootCount, 4, 400);
        Assert.InRange(schedule.GatedRefCount, schedule.RootCount, 4000);
    }
}
