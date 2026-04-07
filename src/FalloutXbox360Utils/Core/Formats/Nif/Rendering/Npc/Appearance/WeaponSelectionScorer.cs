using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;

/// <summary>
///     Centralized weapon selection logic shared between NPC and creature render paths.
///     Combines CombatStyle (CSTY) Weapon Restrictions filtering with a DPS+skill score
///     so the chosen weapon matches both the actor's combat role and stat profile.
/// </summary>
internal static class WeaponSelectionScorer
{
    /// <summary>
    ///     Picks the highest-scoring renderable weapon from <paramref name="candidates" />.
    ///     If a CombatStyle restriction is present, candidates are filtered to matching
    ///     weapons first; if the filtered set is empty, falls back to all candidates so
    ///     the actor still renders with something.
    /// </summary>
    internal static WeapScanEntry? PickBestWeapon(
        IReadOnlyList<WeapScanEntry> candidates,
        WeaponRestriction restriction,
        byte[]? skills,
        byte? combatSkillAggregate,
        byte strength)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        IReadOnlyList<WeapScanEntry> pool = candidates;
        if (restriction != WeaponRestriction.None)
        {
            var filtered = new List<WeapScanEntry>();
            foreach (var w in candidates)
            {
                if (MatchesRestriction(w.WeaponType, restriction))
                {
                    filtered.Add(w);
                }
            }

            if (filtered.Count > 0)
            {
                pool = filtered;
            }
        }

        WeapScanEntry? best = null;
        var bestScore = float.MinValue;
        foreach (var w in pool)
        {
            var score = Score(w, skills, combatSkillAggregate, strength);
            if (score > bestScore)
            {
                bestScore = score;
                best = w;
            }
        }

        return best;
    }

    internal static bool MatchesRestriction(WeaponType type, WeaponRestriction restriction)
    {
        return restriction switch
        {
            WeaponRestriction.MeleeOnly => IsMeleeWeapon(type),
            WeaponRestriction.RangedOnly => !IsMeleeWeapon(type),
            _ => true
        };
    }

    internal static bool IsMeleeWeapon(WeaponType type)
    {
        return type is WeaponType.HandToHandMelee
            or WeaponType.OneHandMelee
            or WeaponType.TwoHandMelee;
    }

    /// <summary>
    ///     Strengthened DPS+skill scoring formula. Skill is a stronger gate than the
    ///     legacy 0.5–1.5 multiplier so high-skill weapons dominate raw DPS.
    ///     Falls back to <paramref name="combatSkillAggregate" /> when the per-skill
    ///     DNAM array is absent (templated NPCs and creatures).
    /// </summary>
    internal static float Score(
        WeapScanEntry weapon,
        byte[]? skills,
        byte? combatSkillAggregate,
        byte strength)
    {
        // sqrt-dampened DPS: real sustained DPS doesn't scale linearly with fire rate
        // because of reload, ammo consumption, and accuracy degradation. A linear
        // (Damage × ShotsPerSec) wildly overstates fast-firing weapons.
        var dampedDps = MathF.Max(weapon.Damage, 0) *
                        MathF.Sqrt(MathF.Max(weapon.ShotsPerSec, 0.1f));

        var isMelee = IsMeleeWeapon(weapon.WeaponType);

        var effectiveRange = weapon.MaxRange > 0f
            ? MathF.Max(weapon.MaxRange - weapon.MinRange, weapon.MaxRange)
            : 0f;
        var rangeFactor = 1f + MathF.Min(effectiveRange / 2000f, 0.75f);

        // Spread only matters for ranged weapons. On melee weapons WEAP.Spread
        // encodes animation/swing variance and is often a large value (10+) that
        // would otherwise crush the score by 6x.
        var spreadPenalty = isMelee
            ? 1f
            : 1f / (1f + MathF.Max(weapon.Spread, 0f) * 0.5f);

        var skill = ResolveSkill(skills, combatSkillAggregate, weapon.SkillActorValue);
        // Range 0.25–1.0: skill is a meaningful gate, not a soft 3x bump.
        var skillFactor = 0.25f + 0.75f * Math.Clamp(skill / 100f, 0f, 1f);
        var skillRequirementPenalty = weapon.SkillRequirement > 0 && skill < weapon.SkillRequirement
            ? 0.35f
            : 1f;
        var strengthPenalty = weapon.StrengthRequirement > 0 && strength < weapon.StrengthRequirement
            ? 0.65f
            : 1f;

        // Strength-scaled melee bonus matches FNV game mechanics: each STR point
        // above 5 adds to melee damage, so high-strength actors (creatures, Super
        // Mutants, power-armored brutes) hit much harder in melee than the raw
        // Damage value suggests. STR 5 = no bonus, STR 10 = 3x melee weight.
        var meleeBonus = 1f;
        if (isMelee)
        {
            var strBoost = MathF.Max(0, strength - 5);
            meleeBonus = 1f + strBoost * 0.4f;
        }

        // Preferred-category bonus: when one DNAM skill clearly dominates the others
        // by 25+ points, weapons in that category get a 1.5x boost. Independent of
        // the strength bonus above so they can stack for an iconic role match.
        var preferenceBonus = ComputePreferenceBonus(skills, weapon.SkillActorValue);

        return dampedDps * rangeFactor * spreadPenalty * skillFactor *
               skillRequirementPenalty * strengthPenalty *
               meleeBonus * preferenceBonus;
    }

    private static float ResolveSkill(byte[]? skills, byte? combatSkillAggregate, uint skillActorValue)
    {
        var index = ActorValueToSkillIndex(skillActorValue);
        if (skills != null && index >= 0 && index < skills.Length)
        {
            return skills[index];
        }

        // Fallback: use the Combat Skill aggregate from DATA when DNAM is absent.
        return combatSkillAggregate ?? 50f;
    }

    private static float ComputePreferenceBonus(byte[]? skills, uint skillActorValue)
    {
        if (skills == null || skills.Length == 0)
        {
            return 1f;
        }

        var index = ActorValueToSkillIndex(skillActorValue);
        if (index < 0 || index >= skills.Length)
        {
            return 1f;
        }

        var weaponSkill = skills[index];
        // Average all combat-related skills (Melee, Guns, Energy, BigGuns, Explosives, Unarmed, Thrown)
        int sum = 0;
        int count = 0;
        ReadOnlySpan<int> combatSkillIndices = stackalloc int[] { 1, 2, 3, 6, 9, 12, 13 };
        foreach (var i in combatSkillIndices)
        {
            if (i < skills.Length && i != index)
            {
                sum += skills[i];
                count++;
            }
        }

        if (count == 0)
        {
            return 1f;
        }

        var otherAvg = sum / (float)count;
        return weaponSkill >= otherAvg + 25f ? 1.5f : 1f;
    }

    private static int ActorValueToSkillIndex(uint actorValueId)
    {
        return actorValueId switch
        {
            32 => 0, // Barter
            33 => 1, // Big Guns
            34 => 2, // Energy Weapons
            35 => 3, // Explosives
            38 => 6, // Melee Weapons
            41 => 9, // Guns
            44 => 12, // Survival/Thrown
            45 => 13, // Unarmed
            _ => -1
        };
    }
}
