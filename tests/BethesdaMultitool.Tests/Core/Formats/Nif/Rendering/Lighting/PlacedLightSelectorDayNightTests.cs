using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.WorldData.DayNight;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     The per-frame emitter gate: a scripted day/night light is dark at noon and lit at night
///     regardless of its authored initial state, Off-By-Default LIGH bases stay dark inside the
///     on-window, and the explicit per-instance On/Off preview still outranks the schedule.
/// </summary>
public sealed class PlacedLightSelectorDayNightTests
{
    private const string NightScript = """
        scn nightlight
        float Time
        int State
        begin GameMode
        	set Time to GetCurrentTime
        	if (Time > 20 || Time < 6) && State == 0
        		enable
        		set State to 1
        	elseif Time < 20 && Time > 6 && State == 1
        		disable
        		set State to 0
        	endif
        end
        """;

    private static DayNightRefStateStore StoreAt(float hour)
    {
        var scripts = new List<ScriptRecord>
        {
            new() { FormId = 0x100, EditorId = "nightlight", SourceText = NightScript },
        };
        var lights = new List<LightRecord>
        {
            new() { FormId = 0x300, EditorId = "GatedLightBase", Script = 0x100 },
        };
        var cell = new CellRecord
        {
            FormId = 0x400,
            PlacedObjects =
            {
                new PlacedReference { FormId = 0x1000, BaseFormId = 0x300 },
            },
        };
        var schedule = DayNightRefSchedule.Build(scripts, [cell], [], lights);
        Assert.NotNull(schedule);
        var store = new DayNightRefStateStore();
        store.Apply(schedule, hour);
        return store;
    }

    private static PlacedLight Light(uint formId, uint flags = 0, bool initiallyDisabled = false) => new(
        FormId: formId,
        BaseFormId: 0x300,
        Position: Vector3.Zero,
        Radius: 256f,
        Color: Vector3.One,
        FalloffExponent: 1f,
        FieldOfView: 90f,
        Intensity: 1f,
        Flags: flags,
        IsInitiallyDisabled: initiallyDisabled);

    private static List<PlacedLight> Select(
        PlacedLight light, DayNightRefStateStore store, ReferenceEnabledOverrideStore? overrides = null)
    {
        var destination = new List<PlacedLight>();
        PlacedLightSelector.AppendNearest(
            [light],
            Vector3.Zero,
            maxPerCell: 16,
            enabledOverrides: overrides ?? new ReferenceEnabledOverrideStore(),
            includeInitiallyDisabled: false,
            destination: destination,
            scratch: [],
            dayNightStates: store);
        return destination;
    }

    [Fact]
    public void GatedLight_DarkAtNoon_LitAtNight_RegardlessOfAuthoredState()
    {
        // Authored ENABLED (the user-visible bug: lamps glowing at midday) → noon suppresses it.
        Assert.Empty(Select(Light(0x1000), StoreAt(12f)));
        Assert.Single(Select(Light(0x1000), StoreAt(23f)));

        // Authored DISABLED → the night window still lights it.
        Assert.Single(Select(Light(0x1000, initiallyDisabled: true), StoreAt(23f)));
    }

    [Fact]
    public void OffByDefaultBaseStaysDarkInsideTheOnWindow()
    {
        var offByDefault = Light(0x1000, flags: PlacedLight.OffByDefaultFlag, initiallyDisabled: true);
        Assert.Empty(Select(offByDefault, StoreAt(23f)));
    }

    [Fact]
    public void ExplicitPerInstanceOverrideOutranksTheSchedule()
    {
        var overrides = new ReferenceEnabledOverrideStore();
        overrides.Set(0x1000, ReferenceEnabledOverride.On);
        Assert.Single(Select(Light(0x1000), StoreAt(12f), overrides));

        overrides.Set(0x1000, ReferenceEnabledOverride.Off);
        Assert.Empty(Select(Light(0x1000), StoreAt(23f), overrides));
    }

    [Fact]
    public void UngatedLight_KeepsAuthoredBehavior()
    {
        // FormID 0x2000 is not in the schedule — authored state decides, day or night.
        Assert.Single(Select(Light(0x2000), StoreAt(12f)));
        Assert.Empty(Select(Light(0x2000, initiallyDisabled: true), StoreAt(23f)));
    }
}
