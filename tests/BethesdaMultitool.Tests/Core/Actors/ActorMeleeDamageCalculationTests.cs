using BethesdaMultitool.Core.Actors;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Actors;

public sealed class ActorMeleeDamageCalculationTests
{
    // Expected bits come from retained x86 arithmetic instruction slices executed by the
    // independent artifact generate_expectations.py. No vector uses PC045's observed damage.
    [Theory]
    [InlineData(10, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0x40A00000u)]
    [InlineData(10, 25, 1, 3, 1, 1, 0, 0, 1, 1, 0x41140000u)]
    [InlineData(10, 100, .25, 0, 1, 1, 0, 0, 1, 1, 0x40E00000u)]
    [InlineData(10, 100, 1, 0, .75, 1, 0, 0, 1, 1, 0x41200000u)]
    [InlineData(10, 100, 1, 0, .5, 1, 0, 0, 1, 1, 0x41053333u)]
    [InlineData(10, 100, 1, 0, 0, 1, 0, 0, 1, 1, 0x409F3333u)]
    [InlineData(7, 33, .6, 2, .3, 1.25, -.5, 2, .75, 1.1, 0x409AAA0Eu)]
    [InlineData(0, 100, 1, 3, 1, 2, 1, 0, 1, 1, 0x40800000u)]
    public void ReconstructsRetainedArithmetic(ushort damage, float skill, float arm, float melee,
        float condition, float attack, float extra, float ammo, float mode, float actorMode, uint expectedBits)
    {
        var state = State() with
        {
            WeaponBaseDamage = damage, ClampedWeaponSkill = skill, RightArmHelperResult = arm,
            MeleeDamage = melee, WeaponConditionFraction = condition, AttackMultiplier = attack,
            ExtraDataDamage = extra, AmmoDamage = ammo, WeaponModeMultiplier = mode, ActorModeMultiplier = actorMode
        };
        var value = ActorMeleeDamageCalculation.EvaluateStage(ActorEngineProfile.PcRetail, state);
        Assert.Equal("Calculated", value.Status);
        Assert.Equal("WeaponDamage.preTargetStage", value.Key);
        Assert.Equal(expectedBits, BitConverter.SingleToUInt32Bits((float)value.Value!.Value));
        Assert.Empty(value.MissingDependencies);
        Assert.DoesNotContain(value.Inputs, item => item.Key.Contains("Health", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing-melee")]
    [InlineData("missing-arm")]
    [InlineData("missing-branch")]
    [InlineData("missing-setting")]
    [InlineData("nonfinite-setting")]
    [InlineData("nonfinite-condition")]
    [InlineData("wrong-executable")]
    [InlineData("wrong-precision")]
    [InlineData("unsupported-weapon")]
    [InlineData("overflow")]
    [InlineData("underflow")]
    public void UnresolvedInputsNeverBecomeNeutralDefaults(string control)
    {
        var state = State();
        state = control switch
        {
            "missing-melee" => state with { MeleeDamage = null },
            "missing-arm" => state with { RightArmHelperResult = null },
            "missing-branch" => state with { AttackMultiplier = null },
            "missing-setting" => state with { GameSettings = new Dictionary<string, float>() },
            "nonfinite-setting" => state with { GameSettings = new Dictionary<string, float>(state.GameSettings) { ["fDamageSkillMult"] = float.PositiveInfinity } },
            "nonfinite-condition" => state with { WeaponConditionFraction = float.NaN },
            "wrong-executable" => state with { ExecutableSha256 = new string('0', 64) },
            "wrong-precision" => state with { X87ControlWord = 0x027F },
            "unsupported-weapon" => state with { WeaponType = 2 },
            "overflow" => state with { AttackMultiplier = float.MaxValue },
            "underflow" => state with { AttackMultiplier = float.Epsilon },
            _ => throw new ArgumentOutOfRangeException(nameof(control))
        };
        var value = ActorMeleeDamageCalculation.EvaluateStage(ActorEngineProfile.PcRetail, state);
        Assert.Equal("Unavailable", value.Status);
        Assert.Null(value.Value);
        Assert.NotEmpty(value.MissingDependencies);
    }

    private static ActorMeleeDamageStageState State() => new(
        ActorEngineProfile.PcRetail.ExecutableSha256, 0x007F, 1, 10,
        100, 1, 0, 1, 1, 0, 0, 1, 1,
        new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["fDamageWeaponMult"] = 1,
            ["fDamageArmConditionMult"] = .8f,
            ["fDamageArmConditionBase"] = .2f,
            ["fDamageSkillMult"] = .5f,
            ["fDamageSkillBase"] = .5f
        });
}
