using System.Buffers.Binary;
using System.Text.Json;

namespace BethesdaMultitool.Core.Actors;

/// <summary>Reads one attributed natural critical decision. Perk stages remain observed.</summary>
internal static class ActorCriticalInvocationObservationReader
{
    internal static IReadOnlyList<ActorStatisticValue> Read(JsonElement row, uint reference, uint baseId,
        string actorKind, ulong captureGeneration, ulong connectionGeneration)
    {
        if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1) ||
            Text(row, "kind") != "critical-invocation" || Number(row, "schemaVersion") != 1 ||
            Text(row, "profile") != "pc-retail-critical-invocation-v1" || Text(row, "status") != "Observed" ||
            Text(row, "executableSha256") != ActorEngineProfile.PcRetail.ExecutableSha256 ||
            Text(row, "codeEvidenceImageSha256") != "e46b43cdaa32d9b79b7816fa45cb076c7ed59e335b1533118dcb6bf1d9da692d" ||
            Boolean(row, "complete") != true || Boolean(row, "codeVerified") != true || Number(row, "failureCode") != 0 ||
            UInt(row, "abandonedInvocations") is null || Number(row, "dropped") != 0 || Number(row, "x87ControlWord") != 0x007F ||
            captureGeneration == 0 || connectionGeneration == 0 ||
            Number(row, "captureGeneration") != captureGeneration || Number(row, "connectionGeneration") != connectionGeneration ||
            !Positive(row, "loadEpoch") || !Positive(row, "threadId") || !Positive(row, "hitInvocationId") || !Positive(row, "leaseId") ||
            Text(row, "targetKind") != "actor" || UInt(row, "engineTargetFormId") != reference ||
            UInt(row, "engineTargetBaseFormId") != baseId || UInt(row, "sourceFormId") != reference || UInt(row, "sourceBaseFormId") != baseId || reference == 0 || baseId == 0 ||
            actorKind is not ("NPC_" or "CREA") || UInt(row, "engineTargetFormType") != (actorKind == "NPC_" ? 0x3Bu : 0x3Cu) ||
            UInt(row, "engineTargetBaseFormType") != (actorKind == "NPC_" ? 0x2Au : 0x2Bu))
            return Missing("complete critical invocation bound to this actor and capture");

        var before = Hex(row, "hitBeforeHex", 0x64);
        var after = Hex(row, "hitAfterHex", 0x64);
        var header = Hex(row, "selectedWeaponHeaderHex", 16);
        if (before is null || after is null || header is null || UInt(row, "hitAddress") is not (> 0 and <= uint.MaxValue - 0x64) ||
            UInt(row, "sourceAddress") is not { } source || source == 0 || UInt(row, "targetAddress") is not { } target || target == 0 ||
            source == target || UInt(row, "sourceFormId") is not { } sourceId || sourceId == 0 ||
            UInt(row, "targetFormId") is not { } targetId || targetId == 0 ||
            UInt(row, "weaponAddress") is not { } weapon || UInt(row, "fallbackAddress") is not { } fallback ||
            UInt(row, "selectedWeaponAddress") is not { } selected || UInt(row, "selectedWeaponFormId") is not { } selectedId ||
            UInt(row, "context") is not { } context || selected != (weapon == 0 ? fallback : weapon) ||
            Word(before, 0) != source || Word(after, 0) != source || Word(before, 4) != target || Word(after, 4) != target ||
            Word(before, 0x30) != weapon || Word(after, 0x30) != weapon || Word(before, 0x54) != context || Word(after, 0x54) != context ||
            (selected == 0 ? selectedId != 0 || header.Any(b => b != 0) : selectedId == 0 || header[4] != 0x28 ||
                Word(header, 12) != selectedId || (Word(header, 8) & 0x4020) != 0))
            return Missing("stable hit participants and selected weapon or fallback identity");

        var flagsBefore = Word(before, 0x58);
        var flagsAfter = Word(after, 0x58);
        if (Boolean(row, "criticalFlagBefore") != ((flagsBefore & 4) != 0) || Boolean(row, "criticalFlagAfter") != ((flagsAfter & 4) != 0) ||
            (flagsBefore & ~0x40Cu) != (flagsAfter & ~0x40Cu))
            return Missing("critical flags matching the captured hit bytes");

        var abandoned = UInt(row, "abandonedInvocations")!.Value;
        IReadOnlyList<string> diagnostics = abandoned > 0 ? [$"Earlier critical invocations abandoned: {abandoned}"] : [];
        var inputs = new List<ActorCalculationInput>
        {
            Input("Source.formId", sourceId), Input("Target.formId", targetId), Input("Hit.address", UInt(row, "hitAddress")!.Value),
            Input("Weapon.address", weapon), Input("Weapon.fallbackAddress", fallback), Input("Weapon.selectedAddress", selected),
            Input("Weapon.selectedFormId", selectedId), Input("Hit.context", context),
            Input("Hit.flagsBefore", flagsBefore), Input("Hit.flagsAfter", flagsAfter)
        };
        if (Boolean(row, "earlyExit") == true)
        {
            if (Number(row, "stepCount") != 2 || Boolean(row, "randomObserved") != false || Boolean(row, "thresholdObserved") != false ||
                !Null(row, "randomRaw") || !Null(row, "randomRemainder") || !Null(row, "thresholdRaw") ||
                !Null(row, "thresholdSigned") || !Null(row, "comparisonAccepted") || !before.SequenceEqual(after))
                return Missing("early exit without a random draw or decision");
            return [Observed("CritChance.eligible", 0, "PC:009B7060 early exit", inputs, flagsAfter, diagnostics)];
        }
        if (Boolean(row, "earlyExit") != false || Number(row, "stepCount") != 9 || Boolean(row, "randomObserved") != true ||
            Boolean(row, "thresholdObserved") != true || UInt(row, "randomRaw") is not { } random ||
            UInt(row, "randomRemainder") is not { } remainder || remainder != random % 1000 ||
            UInt(row, "thresholdRaw") is not { } thresholdRaw || Signed(row, "thresholdSigned") is not { } threshold ||
            threshold != unchecked((int)thresholdRaw) || Boolean(row, "comparisonAccepted") is not { } accepted ||
            accepted != ((int)remainder < threshold) || (flagsBefore & 0x800) != 0 ||
            ((flagsAfter & 4) != 0) != (accepted || (flagsBefore & 4) != 0))
            return Missing("original draw, signed threshold comparison and returned critical flag");

        var stages = new (string Field, string Key)[]
        {
            ("weaponStageBits", "CritChance.weaponStage.invocation"),
            ("sourceModifierStageBits", "CritChance.sourceModifiers"),
            ("targetModifierStageBits", "CritChance.targetModifiers"),
            ("finalChanceScaledBits", "CritChance.scaled")
        };
        var values = new List<ActorStatisticValue>();
        foreach (var (field, key) in stages)
        {
            if (Float(row, field) is not { } value) return Missing("finite captured critical stage values");
            values.Add(Observed(key, value, "PC:009B7060 original invocation", inputs, flagsAfter, diagnostics));
        }
        if (Float(row, "vatsBonusBits") is not { } bonus || Float(row, "sneakMultiplierBits") is not { } multiplier)
            return Missing("resident critical context settings");
        inputs.Add(Input("fVATSCriticalChanceBonus", bonus));
        inputs.Add(Input("fCombatSneakAttackBonusMult", multiplier));
        inputs.Add(Input("Random.raw", random));
        inputs.Add(Input("Random.remainder", remainder));
        var scaled = Float(row, "finalChanceScaledBits")!.Value;
        // Both inspected conversion paths truncate representable signed32 inputs.
        if (scaled >= int.MinValue && (double)scaled < (double)int.MaxValue + 1 && Math.Truncate(scaled) != threshold)
            return Missing("threshold matching the captured representable conversion input");
        values.Add(Observed("CritChance.threshold", threshold, "PC:009B72C4 original conversion return", inputs, flagsAfter, diagnostics));
        values.Add(Observed("CritChance.randomRemainder", remainder, "PC:009B72BC original random remainder", inputs, flagsAfter, diagnostics));
        values.Add(Observed("CritChance.accepted", accepted ? 1 : 0, "PC:009B72C9 signed comparison and returned hit flag", inputs, flagsAfter, diagnostics));
        return values;
    }

    private static IReadOnlyList<ActorStatisticValue> Missing(string reason) =>
        [new ActorStatisticValue("CritChance.invocation", "Unavailable", null, null, [reason])];
    private static ActorStatisticValue Observed(string key, double value, string evidence,
        IReadOnlyList<ActorCalculationInput> inputs, uint flags, IReadOnlyList<string> diagnostics) => new(key, "Observed", value, evidence, [])
        { Inputs = inputs, HitFlags = flags, TraceDiagnostics = diagnostics };
    private static ActorCalculationInput Input(string key, double value) => new(key, value, "Observed", null, null);
    private static uint Word(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static string? Text(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool? Boolean(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static ulong? Number(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var result) ? result : null;
    private static uint? UInt(JsonElement row, string key) => Number(row, key) is { } value && value <= uint.MaxValue ? (uint)value : null;
    private static int? Signed(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : null;
    private static bool Positive(JsonElement row, string key) => Number(row, key) is > 0;
    private static bool Null(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Null;
    private static float? Float(JsonElement row, string key) => UInt(row, key) is { } raw &&
        float.IsFinite(BitConverter.UInt32BitsToSingle(raw)) ? BitConverter.UInt32BitsToSingle(raw) : null;
    private static byte[]? Hex(JsonElement row, string key, int bytes)
    {
        var text = Text(row, key);
        if (text?.Length != bytes * 2) return null;
        try { return Convert.FromHexString(text); }
        catch (FormatException) { return null; }
    }
}
