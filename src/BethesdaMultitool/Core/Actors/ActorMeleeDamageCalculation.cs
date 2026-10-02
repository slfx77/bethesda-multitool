namespace BethesdaMultitool.Core.Actors;

/// <summary>
/// Inputs from one attributed PC one-hand melee invocation.
/// </summary>
internal sealed record ActorMeleeDamageStageState(
    string ExecutableSha256,
    ushort? X87ControlWord,
    int? WeaponType,
    ushort? WeaponBaseDamage,
    float? ClampedWeaponSkill,
    float? RightArmHelperResult,
    float? MeleeDamage,
    float? WeaponConditionFraction,
    float? AttackMultiplier,
    float? ExtraDataDamage,
    float? AmmoDamage,
    float? WeaponModeMultiplier,
    float? ActorModeMultiplier,
    IReadOnlyDictionary<string, float> GameSettings);

/// <summary>
/// Pre-target damage arithmetic at 00644CE0/00646D00.
/// </summary>
internal static class ActorMeleeDamageCalculation
{
    internal static ActorStatisticValue EvaluateStage(ActorEngineProfile profile, ActorMeleeDamageStageState state)
    {
        const string key = "WeaponDamage.preTargetStage";
        var missing = new List<string>();
        var inputs = new List<ActorCalculationInput>();
        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        if (profile.Id != ActorEngineProfile.PcRetailId ||
            !string.Equals(state.ExecutableSha256, ActorEngineProfile.PcRetail.ExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(profile.ExecutableSha256, state.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            missing.Add("inspected PC retail executable");
        if (state.X87ControlWord != 0x007F) missing.Add("game-thread x87 control word 0x007F");
        if (state.WeaponType != 1) missing.Add("one-hand melee weapon type 1; other branches unsupported");
        if (state.WeaponBaseDamage is { } damage) AddInput("WeaponBaseDamage", damage);
        else missing.Add("WeaponBaseDamage");
        Read("ClampedWeaponSkill", state.ClampedWeaponSkill);
        Read("RightArmHelperResult", state.RightArmHelperResult);
        Read("MeleeDamage", state.MeleeDamage);
        Read("WeaponConditionFraction", state.WeaponConditionFraction);
        Read("AttackMultiplier", state.AttackMultiplier);
        Read("ExtraDataDamage", state.ExtraDataDamage);
        Read("AmmoDamage", state.AmmoDamage);
        Read("WeaponModeMultiplier", state.WeaponModeMultiplier);
        Read("ActorModeMultiplier", state.ActorModeMultiplier);
        foreach (var name in new[] { "fDamageWeaponMult", "fDamageArmConditionMult", "fDamageArmConditionBase", "fDamageSkillMult", "fDamageSkillBase" })
            Read(name, state.GameSettings.TryGetValue(name, out var setting) ? setting : null);
        if (missing.Count != 0) return Unavailable();
        var unsupportedExponent = false;

        // x87 PC=24 at 0x007F: preserve rounding after each arithmetic operation.
        // 00644D41..46: weapon virtual UInt16 damage times fDamageWeaponMult.
        var baseScaled = Multiply(values["WeaponBaseDamage"], values["fDamageWeaponMult"]);
        // 00644E3F..56: resolved 00646880 result (type1 uses right-arm route).
        var armScaled = Multiply(values["fDamageArmConditionMult"], values["RightArmHelperResult"]);
        var armFactor = Add(values["fDamageArmConditionBase"], armScaled);
        // 00644EAD..BD: double constant 01016408 is the widened float 0.01f.
        var skillFraction = Round(values["ClampedWeaponSkill"] * (double)0.01f);
        var skillScaled = Multiply(skillFraction, values["fDamageSkillMult"]);
        var skillArm = Multiply(skillScaled, armFactor);
        var skillFactor = Add(skillArm, values["fDamageSkillBase"]);
        var attackFactor = Multiply(skillFactor, values["AttackMultiplier"]);
        // 00644DA8..AB adds current AV17, outside the skill/attack multiplier.
        var additiveDamage = Add(values["MeleeDamage"], values["ExtraDataDamage"]);
        var ratio = values["WeaponConditionFraction"];
        var conditionFactor = 1f;
        if (ratio <= 0.75f)
        {
            // Exact double memory operands: 0.75 and widened 0.67f, independently captured PC012.
            var deficit = Round(0.75d - ratio);
            var reduction = Round(deficit * (double)0.67f);
            conditionFactor = Round(1d - reduction);
        }
        // 00645079..8E. Optional engine paths are supplied explicitly, never assumed neutral.
        var withAmmo = Add(baseScaled, values["AmmoDamage"]);
        var withAttack = Multiply(withAmmo, attackFactor);
        var withAdditive = Add(withAttack, additiveDamage);
        var withCondition = Multiply(withAdditive, conditionFactor);
        var withWeaponMode = Multiply(withCondition, values["WeaponModeMultiplier"]);
        var result = Multiply(withWeaponMode, values["ActorModeMultiplier"]);
        if (unsupportedExponent)
        {
            missing.Add("normal binary32 intermediate range; extended-exponent cases unsupported");
            return Unavailable();
        }
        return new(key, "Calculated", result, "PC:00644CE0; type1 resolved inputs; 00646D00 condition stage", [])
        {
            EngineProfile = profile.Id,
            CalculationBasis = "Reconstruction",
            Inputs = inputs
        };

        float Multiply(float left, float right) => Round((double)left * right);
        float Add(float left, float right) => Round((double)left + right);
        float Round(double value)
        {
            var rounded = (float)value;
            // x87 retains its extended exponent even at 24-bit precision. Do not silently
            // substitute binary32 overflow/underflow behavior for those unimplemented cases.
            unsupportedExponent |= !float.IsFinite(rounded) || float.IsSubnormal(rounded) || (value != 0 && rounded == 0);
            return rounded;
        }

        void AddInput(string name, float value)
        {
            values.Add(name, value);
            inputs.Add(new(name, value, "Observed invocation input", null, null));
        }

        void Read(string name, float? value)
        {
            if (value is { } observed && float.IsFinite(observed)) AddInput(name, observed);
            else missing.Add(name);
        }

        ActorStatisticValue Unavailable() => new(key, "Unavailable", null, null, missing)
        {
            EngineProfile = profile.Id,
            Inputs = inputs
        };
    }
}
