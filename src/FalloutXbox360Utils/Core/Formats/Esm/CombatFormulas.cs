namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Pure calculation helpers derived from decompiled CombatFormulas.cpp
///     (MemDebug XEX, Aug 22 2010 build). All calculations assume 100% weapon condition
///     and no actor-specific modifiers (skill, perks, ammo bonuses).
///     <para>
///         Source: tools/GhidraProject/combat_formulas_decompiled_xenon.txt
///         (19/19 functions decompiled from CombatFormulas::* and Actor::*)
///     </para>
/// </summary>
internal static class CombatFormulas
{
    /// <summary>
    ///     DPS with critical hits factored in.
    ///     Formula: (damage + critDamage * critChanceMult) * shotsPerSec
    ///     <para>
    ///         Derived from CalcWeaponDamagePerSecond — the crit contribution is
    ///         critDamage * (luck / critChanceMult) scaled by fire rate, but without
    ///         actor luck we use the weapon's raw critChanceMult as the multiplier.
    ///     </para>
    /// </summary>
    internal static float CalcDpsWithCriticals(short damage, float shotsPerSec,
        short critDamage, float critChanceMult)
    {
        if (shotsPerSec <= 0f) return 0f;
        // critChanceMult from the weapon record is the multiplier applied to base crit chance.
        // Base crit chance = luck / critChanceMult. Without actor luck, we treat critChanceMult
        // as a scaling factor on the crit contribution itself.
        var critContribution = critDamage * critChanceMult;
        return (damage + critContribution) * shotsPerSec;
    }

    /// <summary>
    ///     Effective fire rate accounting for reload downtime.
    ///     Formula: clipSize / (clipSize / shotsPerSec + animReloadTime)
    ///     <para>
    ///         Derived from GetWeaponShotsPerSecond — when fire animation and reload
    ///         animation data are present, effective rate = shots-in-clip / total-cycle-time.
    ///     </para>
    /// </summary>
    internal static float CalcEffectiveFireRate(float shotsPerSec, byte clipSize,
        float animReloadTime)
    {
        if (shotsPerSec <= 0f || clipSize == 0 || animReloadTime <= 0f)
            return shotsPerSec;

        var fireTime = clipSize / shotsPerSec;
        var cycleTime = fireTime + animReloadTime;
        return cycleTime > 0f ? clipSize / cycleTime : shotsPerSec;
    }

    /// <summary>
    ///     Sustained DPS accounting for reload downtime.
    ///     Formula: damage * effectiveFireRate
    /// </summary>
    internal static float CalcSustainedDps(short damage, float shotsPerSec,
        byte clipSize, float animReloadTime)
    {
        var effectiveRate = CalcEffectiveFireRate(shotsPerSec, clipSize, animReloadTime);
        return damage * effectiveRate;
    }

    /// <summary>
    ///     Sustained DPS with critical hits, accounting for reload downtime.
    ///     Formula: (damage + critDamage * critChanceMult) * effectiveFireRate
    /// </summary>
    internal static float CalcSustainedDpsWithCriticals(short damage, float shotsPerSec,
        byte clipSize, float animReloadTime, short critDamage, float critChanceMult)
    {
        var effectiveRate = CalcEffectiveFireRate(shotsPerSec, clipSize, animReloadTime);
        if (effectiveRate <= 0f) return 0f;
        var critContribution = critDamage * critChanceMult;
        return (damage + critContribution) * effectiveRate;
    }

    /// <summary>
    ///     Skill requirement penalty steps (0-10).
    ///     Formula: min(10, ceil((skillRequired - actualSkill) * 0.1))
    ///     <para>
    ///         Derived from GetWeaponSkillRequirementModifier — each step applies a
    ///         multiplicative penalty from the fWeaponSkillPenalty game setting array.
    ///     </para>
    /// </summary>
    internal static int CalcSkillPenaltySteps(uint skillRequired, uint actualSkill)
    {
        if (actualSkill >= skillRequired) return 0;
        var deficit = (int)(skillRequired - actualSkill);
        var steps = (int)MathF.Ceiling(deficit * 0.1f);
        return Math.Min(steps, 10);
    }
}
