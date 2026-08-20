using BethesdaMultitool.Core.WorldData.DayNight;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData.DayNight;

/// <summary>
///     Pins the GECK-text scan on trimmed replicas of the retail FNV script families it must
///     handle: the Strip's quest script (named targets + nested flicker show), the generic
///     <c>daynightenable</c> self-toggler (hour staged through a local), Freeside's narrow window,
///     the fire-barrel linked-ref swap, and the broken Disable-only bonfires that must yield no
///     schedule at all.
/// </summary>
public sealed class HourScheduleScriptParserTests
{
    private const string StreetLightingScript = """
                                                scn VStreetLightingScript
                                                Short LightsOn
                                                Float FlickerTimer

                                                Begin GameMode

                                                If GetCurrentTime < 20.00 && GetCurrentTime > 6.00
                                                	If LightsOn != 1
                                                		Return
                                                	Elseif LightsOn == 1
                                                		StarterOneREF.Disable
                                                		Set LightsOn to 0
                                                	Endif
                                                Elseif GetCurrentTime > 20.00 || GetCurrentTime < 6.00
                                                	If LightsOn == 1
                                                		Return
                                                	Elseif LightsOn != 1 && Player.GetInWorldSpace TheStripWorldnew != 1
                                                		StarterOneREF.Enable
                                                		Set LightsOn to 1
                                                	Elseif LightsOn != 1 && Player.GetInWorldSpace TheStripWorldnew == 1
                                                		If FlickerTimer >= 0.3 && FlickerTimer < 0.5
                                                			StarterOneREF.Enable
                                                		Elseif FlickerTimer >= 0.5 && FlickerTimer < 0.7
                                                			StarterOneREF.Disable
                                                		Elseif FlickerTimer >= 0.7
                                                			StarterOneREF.Enable
                                                		Endif
                                                	Endif
                                                Endif

                                                End
                                                """;

    private const string DayNightEnableScript = """
                                                scn daynightenable

                                                float Time
                                                int State

                                                begin onLoad
                                                	if State == 1
                                                		enable
                                                	else
                                                		disable
                                                	endif
                                                end

                                                begin GameMode

                                                	set Time to GetCurrentTime

                                                	if (Time > 20 || Time < 6) && State == 0
                                                		enable 0  ;Turn On
                                                		set state to 1
                                                	elseif Time < 20 && Time > 6 && State == 1
                                                		disable  ;Turn Off
                                                		set State to 0
                                                	endif
                                                end
                                                """;

    private const string FireBarrelScript = """
                                            scn FireBarrelSwitchingScript
                                            int Lit
                                            ref UnlitRef

                                            begin GameMode

                                            	if Lit
                                            		if GetCurrentTime > 6.0 && GetCurrentTime < 20.0
                                            			set UnlitRef to GetLinkedRef
                                            			Disable 5
                                            			UnlitRef.enable 5
                                            			set Lit to 0
                                            		endif
                                            	else
                                            		if GetCurrentTime < 6.0 || GetCurrentTime > 20.0
                                            			set UnlitRef to GetLinkedRef
                                            			UnlitRef.disable 5
                                            			Enable 5
                                            			set Lit to 1
                                            		endif
                                            	endif

                                            end
                                            """;

    private const string DisableOnlyBonfireScript = """
                                                    scn NellisBonfireScript
                                                    int Lit
                                                    ref FireREF

                                                    begin GameMode

                                                    	if Lit
                                                    		if GetCurrentTime > 6.0 && GetCurrentTime < 20.0
                                                    			set FireREF to GetLinkedRef
                                                    			NellisCampfire1REF.Disable 2
                                                    			set Lit to 0
                                                    		endif
                                                    	endif

                                                    end
                                                    """;

    private const string NonHourSceneScript = """
                                              scn SceneScript
                                              short iCount
                                              float fTimer

                                              Begin GameMode
                                              	If SomeQuest.SomeVar == 1
                                              		If iCount < 1 && fTimer >= 0.1
                                              			SarahREF.Disable
                                              			Set iCount to 1
                                              		Elseif iCount == 2 && fTimer >= 0.6
                                              			SarahREF.Enable
                                              			Set iCount to 3
                                              		Endif
                                              	Endif
                                              End
                                              """;

    private static HourSchedule? BuildFor(
        string script, HourScheduleTargetKind kind, string name = "")
    {
        var actions = HourScheduleScriptParser.Parse(script)
            .Where(action => action.TargetKind == kind &&
                             string.Equals(action.TargetName, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return HourSchedule.Build(actions);
    }

    [Fact]
    public void StreetLighting_NamedTarget_OnAtNight_OffAtNoon_FlickerShowDoesNotConfuse()
    {
        var schedule = BuildFor(
            StreetLightingScript, HourScheduleTargetKind.NamedReference, "StarterOneREF");

        Assert.NotNull(schedule);
        Assert.True(schedule.IsEnabledAt(23f));
        Assert.True(schedule.IsEnabledAt(2f));
        Assert.False(schedule.IsEnabledAt(12f));
        Assert.False(schedule.IsEnabledAt(19.5f));
        Assert.True(schedule.IsEnabledAt(20.5f));
    }

    [Fact]
    public void DayNightEnable_SelfTarget_HourStagedThroughLocalVariable()
    {
        var schedule = BuildFor(DayNightEnableScript, HourScheduleTargetKind.SelfInstance);

        Assert.NotNull(schedule);
        Assert.True(schedule.IsEnabledAt(21f));
        Assert.True(schedule.IsEnabledAt(5f));
        Assert.False(schedule.IsEnabledAt(12f));
        Assert.False(schedule.IsEnabledAt(7f));
    }

    [Fact]
    public void FireBarrel_SelfAndLinkedRefSwapInOpposition()
    {
        var self = BuildFor(FireBarrelScript, HourScheduleTargetKind.SelfInstance);
        var linked = BuildFor(FireBarrelScript, HourScheduleTargetKind.LinkedReference);

        Assert.NotNull(self);
        Assert.NotNull(linked);
        Assert.True(self.IsEnabledAt(23f));
        Assert.False(self.IsEnabledAt(12f));
        Assert.False(linked.IsEnabledAt(23f));
        Assert.True(linked.IsEnabledAt(12f));
    }

    [Fact]
    public void DisableOnlyScript_YieldsNoSchedule()
    {
        var schedule = BuildFor(
            DisableOnlyBonfireScript, HourScheduleTargetKind.NamedReference, "NellisCampfire1REF");

        Assert.Null(schedule);
    }

    [Fact]
    public void NonHourSceneScript_YieldsNoSchedule()
    {
        var schedule = BuildFor(NonHourSceneScript, HourScheduleTargetKind.NamedReference, "SarahREF");

        Assert.Null(schedule);
    }

    [Fact]
    public void OnLoadAndOtherBlocks_AreIgnored()
    {
        var actions = HourScheduleScriptParser.Parse(DayNightEnableScript);

        // The onLoad enable/disable pair must not surface; only the GameMode pair does.
        Assert.Equal(2, actions.Count);
        Assert.All(actions, action => Assert.True(action.Guard.ContainsHourComparison));
    }

    [Fact]
    public void NarrowEveningWindow_FreesideStyle()
    {
        const string script = """
                              Scn FreesideLightsScript
                              Float fTime
                              Short bLightOn

                              Begin GameMode
                              	Set fTime to GetCurrentTime
                              	If fTime > 20 && fTime < 23.20 && bLightOn == 0
                              		Enable
                              		Set bLightOn to 1
                              	Elseif ( fTime > 23.20 || fTime < 20 ) && bLightOn == 1
                              		Disable
                              		Set bLightOn to 0
                              	Endif
                              End
                              """;

        var schedule = BuildFor(script, HourScheduleTargetKind.SelfInstance);

        Assert.NotNull(schedule);
        Assert.True(schedule.IsEnabledAt(21f));
        Assert.False(schedule.IsEnabledAt(23.5f));
        Assert.False(schedule.IsEnabledAt(12f));
        Assert.False(schedule.IsEnabledAt(2f));
    }
}