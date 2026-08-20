using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.WorldData.DayNight;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData.DayNight;

/// <summary>
///     End-to-end resolution over synthetic records: named persistent-ref targets, SCRI-bound
///     self-togglers, GetLinkedRef targets, and XESP chains (with the opposite-state parity bit)
///     hanging off a scheduled root.
/// </summary>
public sealed class DayNightRefScheduleTests
{
    private const string QuestScript = """
                                       scn StreetScript
                                       Short LightsOn
                                       Begin GameMode
                                       If GetCurrentTime < 20.00 && GetCurrentTime > 6.00
                                       	If LightsOn == 1
                                       		StarterREF.Disable
                                       		Set LightsOn to 0
                                       	Endif
                                       Elseif GetCurrentTime > 20.00 || GetCurrentTime < 6.00
                                       	If LightsOn != 1
                                       		StarterREF.Enable
                                       		Set LightsOn to 1
                                       	Endif
                                       Endif
                                       End
                                       """;

    private const string SelfScript = """
                                      scn glowdaynight
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

    private static DayNightRefSchedule? BuildWorld()
    {
        var scripts = new List<ScriptRecord>
        {
            new() { FormId = 0x100, EditorId = "StreetScript", SourceText = QuestScript },
            new() { FormId = 0x200, EditorId = "glowdaynight", SourceText = SelfScript }
        };
        var activators = new List<ActivatorRecord>
        {
            new() { FormId = 0x300, EditorId = "GlowFxBase", Script = 0x200 }
        };
        var cell = new CellRecord
        {
            FormId = 0x400,
            PlacedObjects =
            {
                // The named quest-script target.
                new PlacedReference { FormId = 0x1000, EditorId = "StarterREF", BaseFormId = 0x9990 },
                // A light XESP'd to the starter (normal polarity).
                new PlacedReference { FormId = 0x1001, BaseFormId = 0x9991, EnableParentFormId = 0x1000 },
                // A "day-only" ref with the opposite-state flag against the same starter.
                new PlacedReference
                {
                    FormId = 0x1002, BaseFormId = 0x9991,
                    EnableParentFormId = 0x1000, EnableParentFlags = 0x01
                },
                // A two-hop chain: child -> mid -> starter.
                new PlacedReference { FormId = 0x1003, BaseFormId = 0x9991, EnableParentFormId = 0x1001 },
                // Two self-toggling glow instances, one with a linked ref.
                new PlacedReference { FormId = 0x2000, BaseFormId = 0x300 },
                new PlacedReference { FormId = 0x2001, BaseFormId = 0x300, LinkedRefFormId = 0x2002 },
                new PlacedReference { FormId = 0x2002, BaseFormId = 0x9992 }
            }
        };

        return DayNightRefSchedule.Build(scripts, [cell], activators, []);
    }

    [Fact]
    public void NamedTargetAndXespChainFollowTheSchedule()
    {
        var schedule = BuildWorld();

        Assert.NotNull(schedule);
        Assert.True(schedule.TryGetDisabledAt(0x1000, 12f, out var starterNoon) && starterNoon);
        Assert.True(schedule.TryGetDisabledAt(0x1000, 23f, out var starterNight) && !starterNight);
        // Chain children follow the root, including through two hops.
        Assert.True(schedule.TryGetDisabledAt(0x1001, 12f, out var childNoon) && childNoon);
        Assert.True(schedule.TryGetDisabledAt(0x1003, 23f, out var grandChildNight) && !grandChildNight);
        // Opposite-state parity inverts: this ref is ON at noon, OFF at night.
        Assert.True(schedule.TryGetDisabledAt(0x1002, 12f, out var dayOnlyNoon) && !dayOnlyNoon);
        Assert.True(schedule.TryGetDisabledAt(0x1002, 23f, out var dayOnlyNight) && dayOnlyNight);
    }

    [Fact]
    public void SelfTogglingInstancesGateThemselves()
    {
        var schedule = BuildWorld();

        Assert.NotNull(schedule);
        Assert.True(schedule.TryGetDisabledAt(0x2000, 12f, out var noon) && noon);
        Assert.True(schedule.TryGetDisabledAt(0x2001, 22f, out var night) && !night);
        // An unrelated ref stays ungated.
        Assert.False(schedule.TryGetDisabledAt(0x9990, 12f, out _));
    }

    [Fact]
    public void StateStoreTracksHourAndBumpsVersionOnlyOnFlips()
    {
        var schedule = BuildWorld();
        Assert.NotNull(schedule);

        var store = new DayNightRefStateStore();
        Assert.Equal(0, store.Version);

        store.Apply(schedule, 12f);
        var noonVersion = store.Version;
        Assert.True(noonVersion > 0);
        Assert.True(store.TryGetDisabled(0x1000, out var noonDisabled) && noonDisabled);
        Assert.True(store.EffectiveDisabled(0x1000, false));
        // Ungated refs fall through to their authored state.
        Assert.False(store.EffectiveDisabled(0x9990, false));

        // Re-applying within the same 3-minute slot is a no-op.
        store.Apply(schedule, 12.001f);
        Assert.Equal(noonVersion, store.Version);

        // A different slot with no state change keeps the version too.
        store.Apply(schedule, 13f);
        Assert.Equal(noonVersion, store.Version);

        // Crossing dusk flips the network and bumps the version once.
        store.Apply(schedule, 22f);
        Assert.True(store.Version > noonVersion);
        Assert.True(store.TryGetDisabled(0x1000, out var nightDisabled) && !nightDisabled);
    }
}