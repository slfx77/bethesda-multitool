namespace BethesdaMultitool.Core.Actors;

internal enum ActorCalculationRole { Unknown, Player, Npc, Creature }

internal sealed record ActorPlayerHealthSelectors(float Selector0, float Selector1, float Selector2);

internal sealed record ActorWeaponCriticalInputs(uint ActorAddress, uint ActorValueOwnerAddress,
    float CurrentCriticalChance, bool WeaponPresent, uint? WeaponFormId, uint? WeaponAddress,
    byte? WeaponFlags, float? FireRate, float? CriticalMultiplier);

internal sealed record ActorCreatureHealthScaling(ushort LevelEncoded, ushort MinimumLevel,
    ushort MaximumLevel, ushort PlayerStoredLevel);

internal sealed record ActorCreatureHealthComponent(uint ActorVtable, uint BaseVtable,
    uint HealthComponentVtable, uint StoredHealth)
{
    // Also proves repeated loaded child fields for fixed actors; presence does not imply flag0x80.
    internal ActorCreatureHealthScaling? Scaling { get; init; }
}

/// <summary>Inputs from one attributed engine observation, never inferred from the output being predicted.</summary>
internal sealed record ActorValueCalculationState(
    string ExecutableSha256,
    ActorCalculationRole Role,
    ushort? X87ControlWord,
    uint? BaseFlags,
    uint? BaseTemplateFlags,
    float? StoredHealth,
    ushort? Level,
    float? PermanentEndurance,
    float? CurrentLuck,
    uint? EnduranceFlags,
    uint? LuckFlags,
    uint? HealthFlags,
    uint? HealthBaseCallback,
    uint? CriticalBaseCallback,
    bool? HasHealthOverride,
    float? HealthOverride,
    uint? HealthOverrideGetter,
    IReadOnlyDictionary<string, float> GameSettings)
{
    internal byte? StoredEndurance { get; init; }
    internal ActorCreatureHealthComponent? CreatureHealth { get; init; }
    internal ActorPlayerHealthSelectors? PlayerHealthSelectors { get; init; }
    internal ActorWeaponCriticalInputs? WeaponCritical { get; init; }
}

/// <summary>Edition-specific callback stages. Missing downstream state does not replace a value with zero.</summary>
internal static class ActorValueCalculation
{
    internal static IReadOnlyList<ActorStatisticValue> Evaluate(ActorEngineProfile profile,
        ActorValueCalculationState state)
    {
        const string healthKey = "Health.derived";
        const string criticalKey = "CritChance.derived";
        if (!string.Equals(state.ExecutableSha256, profile.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            return [Missing(healthKey, "trace executable does not match engine profile"),
                Missing("Health.base", "trace executable does not match engine profile"),
                Missing("Health.current", "trace executable does not match engine profile"),
                Missing(criticalKey, "trace executable does not match engine profile"),
                Missing("Health.permanent", "trace executable does not match engine profile"),
                Missing("CritChance.weaponStage", "trace executable does not match engine profile")];
        if (state.X87ControlWord != 0x007F)
            return [Missing(healthKey, "observed game-thread x87 control word 0x007F"),
                Missing("Health.base", "observed game-thread x87 control word 0x007F"),
                Missing("Health.current", "observed game-thread x87 control word 0x007F"),
                Missing(criticalKey, "observed game-thread x87 control word 0x007F"),
                Missing("Health.permanent", "observed game-thread x87 control word 0x007F"),
                Missing("CritChance.weaponStage", "observed game-thread x87 control word 0x007F")];
        if (state.Role == ActorCalculationRole.Unknown)
            return [Missing(healthKey, "consistent observed actor role and base identity"),
                Missing("Health.base", "consistent observed actor role and base identity"),
                Missing("Health.current", "consistent observed actor role and base identity"),
                Missing(criticalKey, "consistent observed actor role and base identity"),
                Missing("Health.permanent", "consistent observed actor role and base identity"),
                Missing("CritChance.weaponStage", "consistent observed actor role and base identity")];

        var derived = DerivedHealth();
        var baseHealth = BaseHealth(derived);
        return [derived, baseHealth, CurrentHealth(baseHealth), DerivedCritical(),
            PermanentHealth(baseHealth), WeaponCritical()];

        ActorStatisticValue Missing(string key, params string[] dependencies) =>
            new(key, "Unavailable", null, null, dependencies) { EngineProfile = profile.Id };

        ActorStatisticValue Calculated(string key, float value, string rule, List<ActorCalculationInput> inputs) =>
            new(key, "Calculated", value, rule, [])
            {
                EngineProfile = profile.Id,
                CalculationBasis = "Reconstruction",
                Inputs = inputs
            };

        ActorStatisticValue DerivedHealth()
        {
            if (state.HealthBaseCallback != 0x00643670)
                return Missing(healthKey, "observed Health base callback 0x00643670");
            if (state.Role is not (ActorCalculationRole.Player or ActorCalculationRole.Npc))
                return Missing(healthKey, state.Role == ActorCalculationRole.Creature
                    ? "creature Health bypasses this callback in the inspected dispatcher"
                    : "observed actor role");
            if (state.Role == ActorCalculationRole.Npc)
            {
                if (state.BaseFlags is null) return Missing(healthKey, "observed ACBS flags");
                if ((state.BaseFlags.Value & 0x10) != 0)
                    return AutoCalcNpcHealth();
            }
            if (state.PermanentEndurance is not { } endurance || !float.IsFinite(endurance) || state.EnduranceFlags is null)
                return Missing(healthKey, "observed permanent Endurance and actor-value flags");
            var inputs = new List<ActorCalculationInput>
            {
                Input("Endurance.permanent", endurance), Input("Endurance.flags", state.EnduranceFlags.Value),
                Input("x87ControlWord", state.X87ControlWord.Value), Input("Health.baseCallback", state.HealthBaseCallback.Value)
            };
            if (state.BaseFlags is { } flags) inputs.Add(Input("ACBS.flags", flags));
            var prefix = state.Role == ActorCalculationRole.Player ? "fAVDHealth" : "fAVDNPCHealth";
            if (!Setting(prefix + "EnduranceOffset", inputs, out var offset) ||
                !Setting(prefix + "EnduranceMult", inputs, out var multiplier))
                return Missing(healthKey, $"observed {prefix}EnduranceOffset and {prefix}EnduranceMult");
            var clamped = ClampActorValue(endurance, state.EnduranceFlags.Value);
            var value = (clamped + offset) * multiplier;
            if (state.Role == ActorCalculationRole.Player)
            {
                if (state.Level is not { } level || !Setting("fAVDHealthLevelMult", inputs, out var levelMultiplier))
                    return Missing(healthKey, "observed actor level and fAVDHealthLevelMult");
                inputs.Add(Input("Level.current", level));
                if (level > 0) value += (level - 1) * levelMultiplier;
            }
            return float.IsFinite(value)
                ? Calculated(healthKey, value, "PC:00643670 → 006436C0; permanent Endurance clamp; player/NPC branch", inputs)
                : Missing(healthKey, "finite derived Health result");
        }

        ActorStatisticValue AutoCalcNpcHealth()
        {
            if (state.BaseTemplateFlags is not { } templateFlags ||
                (templateFlags & (uint)ActorTemplateGroup.UseStats) != 0)
                return Missing(healthKey, "observed stored NPC inputs without unresolved UseStats inheritance");
            if (state.StoredHealth is not { } stored || !float.IsFinite(stored) ||
                stored < 0 || (double)stored > uint.MaxValue || MathF.Truncate(stored) != stored)
                return Missing(healthKey, "observed unsigned stored NPC Health");
            var inputs = new List<ActorCalculationInput>
            {
                Input("ACBS.flags", state.BaseFlags.Value), Input("ACBS.templateFlags", templateFlags),
                Input("Health.stored", stored), Input("x87ControlWord", state.X87ControlWord.Value),
                Input("Health.baseCallback", state.HealthBaseCallback.Value)
            };
            // 00603FF0..00603FF7 bypasses both stored Endurance and settings for zero Health.
            if (stored == 0)
                return Calculated(healthKey, 0, "PC:00603FC0; zero stored-Health gate", inputs);
            if (state.StoredEndurance is not { } endurance || state.Level is not { } level)
                return Missing(healthKey, "observed stored Endurance byte and unsigned NPC level");
            inputs.Add(Input("Endurance.stored", endurance));
            inputs.Add(Input("Level.current", level));
            if (!Setting("fAVDNPCHealthEnduranceOffset", inputs, out var offset) ||
                !Setting("fAVDNPCHealthEnduranceMult", inputs, out var multiplier) ||
                !Setting("fAVDNPCHealthLevelMult", inputs, out var levelMultiplier))
                return Missing(healthKey, "observed NPC Endurance offset/multiplier and level multiplier");
            var effectiveLevel = Math.Max((int)level, 1);
            // CW 007F rounds each x87 arithmetic operation to 24 significant bits.
            // Its wider exponent range is not equivalent to float underflow/overflow.
            if (!AutoCalcStep((double)endurance + offset, out var enduranceOffset) ||
                !AutoCalcStep((double)enduranceOffset * multiplier, out var enduranceTerm) ||
                !AutoCalcStep((double)enduranceTerm + 0, out enduranceTerm) ||
                !AutoCalcStep((double)(effectiveLevel - 1) * levelMultiplier, out var levelTerm) ||
                !AutoCalcStep((double)levelTerm + enduranceTerm, out var total))
                return Missing(healthKey, "auto-calc arithmetic within supported single-precision exponent range");
            // EC62C0's SSE2 and x87 fallback agree here; their overflow behavior differs.
            if ((double)total < int.MinValue || (double)total >= 2147483648d)
                return Missing(healthKey, "auto-calc conversion within signed 32-bit range");
            var truncated = (int)MathF.Truncate(total);
            inputs.Add(new("Level.floored", effectiveLevel, "Reconstruction", null, null));
            inputs.Add(new("Health.enduranceTerm", enduranceTerm, "Reconstruction", null, null));
            inputs.Add(new("Health.levelTerm", levelTerm, "Reconstruction", null, null));
            inputs.Add(new("Health.beforeConversion", total, "Reconstruction", null, null));
            inputs.Add(new("Health.truncated", truncated, "Reconstruction", null, null));
            return Calculated(healthKey, Math.Max(truncated, 0),
                "PC:006436C0 → 00603FC0 → 00EC62C0; stored Endurance; level floor; truncation and negative clamp", inputs);
        }

        ActorStatisticValue BaseHealth(ActorStatisticValue derivedHealth)
        {
            if (state.HasHealthOverride is null)
                return Missing("Health.base", "observed Health base-override presence");
            // Native39 also verifies the shared Actor getter for ACRE/CREA identities.
            if (state.Role is not (ActorCalculationRole.Player or ActorCalculationRole.Npc or ActorCalculationRole.Creature) ||
                state.HealthOverrideGetter != (state.Role == ActorCalculationRole.Player ? 0x0094C640u : 0x00880660u))
                return Missing("Health.base", "verified PC actor Health override getter");
            var inputs = new List<ActorCalculationInput>
            {
                Input("Health.overridePresent", state.HasHealthOverride.Value ? 1 : 0),
                Input("Health.overrideGetter", state.HealthOverrideGetter.Value)
            };
            if (state.HasHealthOverride.Value)
            {
                if (state.HealthOverride is not { } value || !float.IsFinite(value))
                    return Missing("Health.base", "observed Health base override value");
                inputs.Add(Input("Health.override", value));
                return Calculated("Health.base", value, "PC:008803A0; explicit non-skill base override branch",
                    inputs);
            }
            if (state.Role == ActorCalculationRole.Creature)
                return CreatureBaseHealth(inputs);
            // Either flag reaches the callback without relying on unobserved virtual predicates.
            if (state.HealthFlags is not { } flags || (flags & (0x80 | 0x800)) == 0)
                return Missing("Health.base", "observed Health callback-selection flag 0x80 or 0x800");
            if (derivedHealth.Value is not { } derivedValue || derivedHealth.Status != "Calculated")
                return Missing("Health.base", "available derived Health stage");
            inputs.AddRange(derivedHealth.Inputs);
            inputs.Add(Input("Health.flags", flags));
            var includesStored = (flags & 0x40) != 0;
            inputs.Add(Input("Health.includesStored", includesStored ? 1 : 0));
            var valueWithStored = (float)derivedValue;
            if (includesStored)
            {
                if (state.BaseTemplateFlags is not { } templateFlags || (templateFlags & (uint)ActorTemplateGroup.UseStats) != 0)
                    return Missing("Health.base", "observed stored Health owner without unresolved UseStats inheritance");
                if (state.StoredHealth is not { } stored || !float.IsFinite(stored))
                    return Missing("Health.base", "observed stored Health selected by flag 0x40");
                inputs.Add(Input("ACBS.templateFlags", templateFlags));
                inputs.Add(Input("Health.stored", stored));
                valueWithStored += stored;
            }
            return float.IsFinite(valueWithStored)
                ? Calculated("Health.base", valueWithStored, "PC:008803A0 → 0066ED60; callback and stored-Health flag 0x40", inputs)
                : Missing("Health.base", "finite base Health result");
        }

        ActorStatisticValue CreatureBaseHealth(List<ActorCalculationInput> inputs)
        {
            if (state.CreatureHealth is not { } component || component.ActorVtable != 0x010870AC ||
                component.BaseVtable != 0x01048F5C || component.HealthComponentVtable != 0x01048E6C)
                return Missing("Health.base", "observed creature tables matching the retained PC Health route");
            if (state.BaseFlags is not { } baseFlags)
                return Missing("Health.base", "observed creature flags");
            if (state.BaseTemplateFlags is not { } templateFlags ||
                ((templateFlags & (uint)ActorTemplateGroup.UseStats) != 0 && component.Scaling is null))
                return Missing("Health.base", "repeated effective child component inputs for UseStats");
            if (state.HealthFlags is not { } flags || (flags & 0x1000) == 0 || (flags & 0x400) != 0)
                return Missing("Health.base", "observed creature Health stored-component flags");
            // 005F8E90 stores AX and returns MOVZX in both branches; never round Health via float.
            var multiplier = 1;
            if ((baseFlags & 0x80) != 0)
            {
                if (component.Scaling is not { } scaling)
                    return Missing("Health.base", "owned repeated creature scaling and player stored-level inputs");
                var effective = ActorLevelCalculation.Calculate(unchecked((short)scaling.LevelEncoded), true,
                    scaling.PlayerStoredLevel, scaling.MinimumLevel, scaling.MaximumLevel);
                multiplier = Math.Max(1, (int)unchecked((short)effective));
                inputs.Add(Input("ACBS.levelEncodedUnsigned", scaling.LevelEncoded));
                inputs.Add(Input("ACBS.minimumLevel", scaling.MinimumLevel));
                inputs.Add(Input("ACBS.maximumLevel", scaling.MaximumLevel));
                inputs.Add(Input("Player.levelStored", scaling.PlayerStoredLevel));
                inputs.Add(new("Level.effectiveUInt16", effective, "Reconstruction", null, null));
                inputs.Add(new("Health.levelMultiplier", multiplier, "Reconstruction", null, null));
            }
            var value = unchecked((ushort)(component.StoredHealth * (uint)multiplier));
            inputs.Add(Input("Actor.vtable", component.ActorVtable));
            inputs.Add(Input("Base.vtable", component.BaseVtable));
            inputs.Add(Input("Health.componentVtable", component.HealthComponentVtable));
            inputs.Add(Input("ACBS.flags", baseFlags));
            inputs.Add(Input("ACBS.templateFlags", templateFlags));
            inputs.Add(Input("Health.flags", flags));
            inputs.Add(Input("Health.stored", component.StoredHealth));
            inputs.Add(new("Health.componentUInt16", value, "Reconstruction", null, null));
            var rule = "PC:008803A0; creature selector 008D0360; 005FA180 -> 005F0FB0 -> 005F0B00 -> 005F8E90; fixed or 0047DED0 scaled level, signed Int16 floor1, low UInt16 Health component";
            if (component.Scaling is not null)
                rule += "; observed effective child component";
            return Calculated("Health.base", value, rule, inputs);
        }

        ActorStatisticValue CurrentHealth(ActorStatisticValue baseHealth)
        {
            if (state.Role != ActorCalculationRole.Player)
                return Missing("Health.current", "supported player current-Health route");
            if (state.PlayerHealthSelectors is not { } selectors)
                return Missing("Health.current", "owned repeated player Health selector 0/1/2 observations");
            if (baseHealth.Status != "Calculated" || baseHealth.Value is not { } baseValue)
                return Missing("Health.current", "independently reconstructed base Health");
            // 0093ACB0: selector0 + base, selector1 + prior, selector2 + prior.
            // CW 007F rounds every FADD to 24 bits; the final FSTP stores binary32.
            if (!AutoCalcStep((double)selectors.Selector0 + baseValue, out var first) ||
                !AutoCalcStep((double)selectors.Selector1 + first, out var second) ||
                !AutoCalcStep((double)selectors.Selector2 + second, out var value))
                return Missing("Health.current", "current-Health arithmetic within supported single-precision exponent range");
            var inputs = new List<ActorCalculationInput>(baseHealth.Inputs)
            {
                new("Health.base", baseValue, "Reconstruction", null, null),
                Input("Health.selector0", selectors.Selector0),
                Input("Health.selector1", selectors.Selector1),
                Input("Health.selector2", selectors.Selector2),
                new("Health.afterSelector0", first, "Reconstruction", null, null),
                new("Health.afterSelector1", second, "Reconstruction", null, null)
            };
            return Calculated("Health.current", value,
                "PC:0093ACB0 -> 0094C3D0; ordered player base + raw selectors 0/1/2", inputs);
        }

        ActorStatisticValue PermanentHealth(ActorStatisticValue baseHealth)
        {
            const string key = "Health.permanent";
            if (state.Role != ActorCalculationRole.Player)
                return Missing(key, "supported player permanent-Health route");
            if (state.PlayerHealthSelectors is not { } selectors || state.HealthFlags is not { } flags)
                return Missing(key, "owned player Health selector 1 and actor-value flags");
            if (baseHealth.Status != "Calculated" || baseHealth.Value is not { } baseValue)
                return Missing(key, "independently reconstructed base Health");
            // 0093AD60's ordinary AV16 branch adds selector1, then calls 0066F190.
            if (!AutoCalcStep((double)selectors.Selector1 + baseValue, out var sum))
                return Missing(key, "permanent-Health arithmetic within supported single-precision exponent range");
            var inputs = new List<ActorCalculationInput>(baseHealth.Inputs)
            {
                new("Health.base", baseValue, "Reconstruction", null, null),
                Input("Health.selector1", selectors.Selector1),
                Input("Health.permanentClampFlags", flags),
                new("Health.beforePermanentClamp", sum, "Reconstruction", null, null)
            };
            return Calculated(key, ClampActorValue(sum, flags),
                "PC:0093AD60 -> 0066F190; player base + raw selector 1 and AV flags", inputs);
        }

        ActorStatisticValue WeaponCritical()
        {
            const string key = "CritChance.weaponStage";
            if (state.WeaponCritical is not { } weapon || !float.IsFinite(weapon.CurrentCriticalChance))
                return Missing(key, "owned current CritChance and equipped-weapon observation");
            var inputs = new List<ActorCalculationInput>
            {
                Input("Actor.address", weapon.ActorAddress),
                Input("ActorValue.ownerAddress", weapon.ActorValueOwnerAddress),
                Input("CritChance.current", weapon.CurrentCriticalChance),
                Input("Weapon.present", weapon.WeaponPresent ? 1 : 0),
                Input("x87ControlWord", state.X87ControlWord.Value)
            };
            var divisor = 1f;
            var multiplier = -1f;
            if (weapon.WeaponPresent)
            {
                if (weapon.WeaponFormId is not { } id || weapon.WeaponAddress is not { } address ||
                    weapon.WeaponFlags is not { } flags || weapon.CriticalMultiplier is not { } critical ||
                    !float.IsFinite(critical))
                    return Missing(key, "owned weapon identity, automatic flag and critical multiplier");
                inputs.Add(Input("Weapon.formId", id));
                inputs.Add(Input("Weapon.address", address));
                inputs.Add(Input("Weapon.flags", flags));
                inputs.Add(Input("Weapon.criticalMultiplier", critical));
                multiplier = critical;
                if ((flags & 2) != 0)
                {
                    if (weapon.FireRate is not { } rate || !float.IsFinite(rate))
                        return Missing(key, "observed automatic-weapon fire rate");
                    inputs.Add(Input("Weapon.fireRate", rate));
                    divisor = rate == 0 ? 1 : rate;
                }
            }
            // 00646D80 stores the division before an optional nonnegative multiplier.
            if (!AutoCalcStep((double)weapon.CurrentCriticalChance / divisor, out var quotient))
                return Missing(key, "weapon-critical arithmetic within supported single-precision exponent range");
            var result = quotient;
            if (multiplier >= 0 && !AutoCalcStep((double)multiplier * quotient, out result))
                return Missing(key, "weapon-critical arithmetic within supported single-precision exponent range");
            inputs.Add(new("CritChance.divisor", divisor, "Reconstruction", null, null));
            inputs.Add(new("CritChance.afterDivision", quotient, "Reconstruction", null, null));
            return Calculated(key, result,
                "PC:00646D80; equipped-weapon adjustment before 009B7060 modifiers", inputs);
        }

        ActorStatisticValue DerivedCritical()
        {
            if (state.CriticalBaseCallback != 0x00643B10)
                return Missing(criticalKey, "observed CritChance base callback 0x00643B10");
            if (state.CurrentLuck is not { } luck || !float.IsFinite(luck) || state.LuckFlags is null)
                return Missing(criticalKey, "observed current Luck and actor-value flags");
            var inputs = new List<ActorCalculationInput>
            {
                Input("Luck.current", luck), Input("Luck.flags", state.LuckFlags.Value),
                Input("x87ControlWord", state.X87ControlWord.Value), Input("CritChance.baseCallback", state.CriticalBaseCallback.Value)
            };
            if (!Setting("fAVDCritLuckBase", inputs, out var baseline) || !Setting("fAVDCritLuckMult", inputs, out var multiplier))
                return Missing(criticalKey, "observed fAVDCritLuckBase and fAVDCritLuckMult");
            var value = ClampActorValue(luck, state.LuckFlags.Value) * multiplier + baseline;
            return float.IsFinite(value)
                ? Calculated(criticalKey, value, "PC:00643B10; clamped current Luck and named GMSTs", inputs)
                : Missing(criticalKey, "finite derived CritChance result");
        }

        bool Setting(string name, List<ActorCalculationInput> inputs, out float value)
        {
            if (!state.GameSettings.TryGetValue(name, out value) || !float.IsFinite(value)) return false;
            inputs.Add(Input(name, value));
            return true;
        }
    }

    // 0066F190 calls min then max. Flags are observed per actor-value definition; they are not guessed from its name.
    internal static float ClampActorValue(float value, uint flags) => (flags & 0x10) != 0
        ? MathF.Max(0, MathF.Min(value, 100))
        : (flags & 8) != 0 ? MathF.Max((flags & 0x8000) != 0 ? 1 : 0, MathF.Min(value, 10)) : value;

    private static bool AutoCalcStep(double exact, out float rounded)
    {
        const double minimumNormal = 1.1754943508222875E-38;
        rounded = 0;
        if (!double.IsFinite(exact) || (exact != 0 &&
            (Math.Abs(exact) < minimumNormal || Math.Abs(exact) > float.MaxValue))) return false;
        rounded = (float)exact;
        return true;
    }

    private static ActorCalculationInput Input(string key, double value) => new(key, value, "Observed", null, null);
}
