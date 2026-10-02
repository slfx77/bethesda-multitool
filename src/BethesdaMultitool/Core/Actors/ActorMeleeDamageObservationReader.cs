using System.Buffers.Binary;
using System.Text.Json;

namespace BethesdaMultitool.Core.Actors;

internal sealed record ActorMeleeDamageObservation(ActorMeleeDamageStageState? Inputs,
    float? StageResult, float? HitHealthDamage, uint? HitFlags, IReadOnlyList<string> MissingDependencies);

/// <summary>Admits one bound natural-hit invocation. Engine returns remain comparison-only.</summary>
internal static class ActorMeleeDamageObservationReader
{
    private const string ImageHash = "e46b43cdaa32d9b79b7816fa45cb076c7ed59e335b1533118dcb6bf1d9da692d";
    private static readonly (string Name, uint Address)[] Settings =
    [
        ("fDamageWeaponMult", 0x011CE2C4), ("fDamageArmConditionBase", 0x011CFCB4),
        ("fDamageArmConditionMult", 0x011CFAF0), ("fDamageSkillBase", 0x011CECEC),
        ("fDamageSkillMult", 0x011CF354)
    ];

    internal static ActorMeleeDamageObservation Read(JsonElement row, uint reference, uint baseId,
        string actorKind, ulong captureGeneration, ulong connectionGeneration)
    {
        var stage = Object(row, "damageStage");
        if (!UniqueProperties(row) || Text(row, "kind") != "damage-stage" || Text(row, "targetKind") != "actor" ||
            Text(row, "executableSha256") != ActorEngineProfile.PcRetail.ExecutableSha256 ||
            Text(row, "codeEvidenceImageSha256") != ImageHash ||
            !ValidReference(reference) || !ValidId(baseId) || UInt(row, "engineTargetFormId") != reference ||
            UInt(row, "engineTargetBaseFormId") != baseId ||
            UInt(row, "engineTargetFormType") != (actorKind == "NPC_" ? 0x3Bu : actorKind == "CREA" ? 0x3Cu : 0u) ||
            UInt(row, "engineTargetBaseFormType") != (actorKind == "NPC_" ? 0x2Au : actorKind == "CREA" ? 0x2Bu : 0u) ||
            actorKind is not ("NPC_" or "CREA") || !ValidReference(UInt(row, "engineOtherFormId")) ||
            !ValidId(UInt(row, "engineOtherBaseFormId")) ||
            captureGeneration == 0 || connectionGeneration == 0 ||
            Counter(row, "captureGeneration") != captureGeneration || Counter(row, "connectionGeneration") != connectionGeneration ||
            Counter(row, "loadEpoch") is not > 0 || Counter(row, "threadId") is not > 0 ||
            Counter(row, "requestId") is not > 0 || Counter(row, "leaseId") is not > 0 ||
            Counter(row, "frame") is not > 0 || Counter(row, "frame") != Counter(row, "entryFrame") ||
            Counter(row, "hitInvocationId") is not > 0 || Counter(row, "hitInvocationId") != Counter(row, "stageInvocationId") ||
            UInt(row, "retainedEventLimit") != 64 || UInt(row, "omittedBefore") is null ||
            UInt(stage, "version") != 1 || Text(stage, "route") != "009B5170/004BDF00/00644CE0" ||
            Text(stage, "codeProfile") != "pc-retail-damage-v1" || Boolean(stage, "codeProfileMatched") != true ||
            Boolean(stage, "returnIsFinalHealthDamage") != false)
            return Missing("damage invocation identity, generation and inspected route");

        var attacker = UInt(row, "attackerAddress");
        var target = UInt(row, "targetAddress");
        var weapon = UInt(row, "weaponAddress");
        var hit = UInt(row, "hitAddress");
        var item = UInt(row, "itemAddress");
        if (!Address(attacker, 0xA4) || !Address(target, 0x20) || !Address(hit, 0x64) ||
            weapon is null || item is null ||
            (weapon == 0 ? UInt(row, "weaponFormId") != 0 : !Address(weapon, 0x160) || !ValidId(UInt(row, "weaponFormId"))) ||
            item != 0 && !Address(item, 1)) return Missing("damage invocation participant addresses");

        float? hitValue = null;
        uint? hitFlags = null;
        var hitRaw = Hex(stage, "hitReturnRawHex", 0x64);
        if (Boolean(stage, "hitIdentityRepeated") == true && hitRaw is not null &&
            U32(hitRaw, 0) == attacker && U32(hitRaw, 4) == target && U32(hitRaw, 0x30) == weapon &&
            Scalar(Object(stage, "hitReturnHealthDamage"), hit!.Value + 0x14, U32(hitRaw, 0x14)) is { } observedHit &&
            Integer(Object(stage, "hitReturnFlags"), hit.Value + 0x58, U32(hitRaw, 0x58)) is { } flags)
        {
            hitValue = observedHit;
            hitFlags = flags;
        }

        // A bound post-stage hit can survive an unavailable pre-target reconstruction.
        var state = ReadInputs(row, stage, attacker!.Value, weapon.Value, item.Value);
        if (state is null) return new(null, null, hitValue, hitFlags, ["complete coherent type1 operands"]);
        var frame = UInt(stage, "frameAddress")!.Value;
        var raw = Hex(stage, "operandFrameRawHex", 0x9C)!;
        var result = Scalar(Object(stage, "output"), frame - 0x74, U32(raw, 0));
        return new(state, result, hitValue, hitFlags, result.HasValue ? [] : ["coherent engine stage return"]);
    }

    private static ActorMeleeDamageStageState? ReadInputs(JsonElement row, JsonElement stage,
        uint attacker, uint weapon, uint item)
    {
        var frame = UInt(stage, "frameAddress");
        var caller = UInt(stage, "callerFrameAddress");
        var raw = Hex(stage, "operandFrameRawHex", 0x9C);
        var weaponRaw = Hex(stage, "weaponRawHex", 0x160);
        var arguments = Array(stage, "arguments");
        var inputs = Object(stage, "inputs");
        if (weapon == 0 || item == 0 || Text(stage, "status") != "observed" ||
            !stage.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.Null ||
            Boolean(stage, "constantsRepeated") != true ||
            Boolean(stage, "inputsRepeated") != true || UInt(stage, "meleeOperandCalls") != 1 || UInt(stage, "limbOperandCalls") != 1 ||
            UInt(stage, "x87ControlWord") != 0x007F || UInt(stage, "x87ReturnControlWord") != 0x007F ||
            UInt(stage, "returnAddress") != 0x004BDF76 || frame is null or < 0x74 || !Address(frame, 0x28) ||
            !Address(caller, 8) || raw is null || weaponRaw is null || arguments.Length != 8 ||
            U32(raw, 0x74) != caller || U32(raw, 0x78) != 0x004BDF76 ||
            weaponRaw[4] != 0x28 || (U32(weaponRaw, 8) & 0x4020) != 0 ||
            U32(weaponRaw, 12) != UInt(row, "weaponFormId") || weaponRaw[0xF4] != 1 || U32(raw, 0x2C) != 1)
            return null;
        for (var i = 0; i < arguments.Length; i++)
            if (arguments[i].ValueKind != JsonValueKind.Number || !arguments[i].TryGetUInt32(out var arg) ||
                arg != U32(raw, 0x7C + i * 4)) return null;
        if (U32(raw, 0x7C) != attacker + 0xA4 || U32(raw, 0x80) != weapon || U32(raw, 0x94) != item) return null;

        var damage = Integer(Object(inputs, "weaponBaseDamage"), weapon + 0xA0, U32(raw, 0x38));
        if (damage is null || damage != BinaryPrimitives.ReadUInt16LittleEndian(weaponRaw.AsSpan(0xA0)) ||
            Integer(Object(inputs, "governingSkill"), weapon + 0x15C, U32(raw, 0x44)) != U32(weaponRaw, 0x15C)) return null;
        var skill = Scalar(Object(inputs, "skillValue"), frame.Value - 0x20, U32(raw, 0x54));
        var condition = Scalar(Object(inputs, "conditionFraction"), frame.Value + 0x10, U32(raw, 0x84));
        var attack = Scalar(Object(inputs, "attackMultiplier"), frame.Value + 0x14, U32(raw, 0x88));
        var ammo = Scalar(Object(inputs, "ammunitionDamage"), frame.Value - 0x24, U32(raw, 0x50));
        var weaponMode = Scalar(Object(inputs, "weaponModeMultiplier"), frame.Value - 8, U32(raw, 0x6C));
        var actorMode = Scalar(Object(inputs, "actorModeMultiplier"), frame.Value - 4, U32(raw, 0x70));
        var extra = Scalar(Object(inputs, "extraDataDamage"), frame.Value - 0x28);
        var melee = Object(inputs, "meleeDamageActorValue");
        var meleeRaw = Hex(melee, "rawHex", 10);
        var meleeValue = Scalar(melee, null);
        if (meleeRaw is null || UInt(melee, "actorValueCode") != 17 || meleeValue is null ||
            !Float80(meleeRaw, out var meleeBits) || meleeBits != BitConverter.SingleToUInt32Bits(meleeValue.Value) ||
            extra is null || BitConverter.SingleToUInt32Bits((float)((double)extra.Value + meleeValue.Value)) != U32(raw, 0x4C)) return null;
        var limb = Object(inputs, "rightArmFactor");
        var limbRaw = Hex(limb, "rawHex", 8);
        var limbValue = Number(limb, "value");
        if (Text(limb, "status") != "observed" || limbRaw is null || !limbRaw.AsSpan().SequenceEqual(raw.AsSpan(0x24, 8)) ||
            limbValue is null || BitConverter.DoubleToUInt64Bits(limbValue.Value) != BinaryPrimitives.ReadUInt64LittleEndian(limbRaw) ||
            (double)(float)limbValue.Value != limbValue.Value) return null;
        if (skill is null || condition is null || attack is null || ammo is null || weaponMode is null || actorMode is null) return null;
        var settings = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (name, address) in Settings)
        {
            if (Scalar(Object(inputs, name), address) is not { } value) return null;
            settings.Add(name, value);
        }
        return new(ActorEngineProfile.PcRetail.ExecutableSha256, 0x007F, 1, (ushort)damage.Value,
            skill, (float)limbValue.Value, meleeValue, condition, attack, extra, ammo, weaponMode, actorMode, settings);
    }

    private static ActorMeleeDamageObservation Missing(string reason) => new(null, null, null, null, [reason]);
    private static bool ValidReference(uint? id) => id is > 0 and < uint.MaxValue;
    private static bool ValidId(uint? id) => id is > 0 && (id.Value >> 24) != 0xFF;
    private static bool Address(uint? address, uint length) => address is > 0 && length <= uint.MaxValue - address.Value;
    private static uint U32(byte[] raw, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset, 4));

    private static float? Scalar(JsonElement value, uint? address, uint? expected = null)
    {
        if (Text(value, "status") != "observed" || Number(value, "value") is not { } number ||
            !float.IsFinite((float)number) || (double)(float)number != number || UInt(value, "rawUInt32") is not { } bits ||
            bits != BitConverter.SingleToUInt32Bits((float)number) || expected.HasValue && expected != bits ||
            address.HasValue && UInt(value, "sourceAddress") != address) return null;
        return (float)number;
    }

    private static uint? Integer(JsonElement value, uint address, uint expected) =>
        Text(value, "status") == "observed" && UInt(value, "value") == expected &&
        UInt(value, "rawUInt32") == expected && UInt(value, "sourceAddress") == address ? expected : null;

    private static byte[]? Hex(JsonElement value, string name, int length)
    {
        if (Text(value, name) is not { } hex || hex.Length != length * 2) return null;
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }

    private static bool Float80(byte[] raw, out uint bits)
    {
        var mantissa = BinaryPrimitives.ReadUInt64LittleEndian(raw);
        var signExponent = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(8));
        var exponent = signExponent & 0x7FFF;
        var sign = (signExponent & 0x8000) != 0 ? 0x80000000u : 0u;
        bits = sign;
        if (mantissa == 0 && exponent == 0) return true;
        if (exponent is 0 or 0x7FFF || (mantissa & 0x8000000000000000UL) == 0) return false;
        var binaryExponent = exponent - 16383;
        if (binaryExponent is > 127 or < -149) return false;
        var shift = binaryExponent >= -126 ? 40 : -binaryExponent - 86;
        if (shift >= 64 || (mantissa & ((1UL << shift) - 1)) != 0) return false;
        var significand = (uint)(mantissa >> shift);
        bits |= binaryExponent >= -126 ? ((uint)(binaryExponent + 127) << 23) | (significand & 0x7FFFFF) : significand;
        return (bits & 0x7F800000) != 0x7F800000;
    }

    private static bool UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().All(UniqueProperties);
        if (value.ValueKind != JsonValueKind.Object) return true;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Add(property.Name) && UniqueProperties(property.Value));
    }

    private static JsonElement Object(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Object ? field : default;
    private static JsonElement[] Array(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Array ? field.EnumerateArray().ToArray() : [];
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static uint? UInt(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetUInt32(out var number) ? number : null;
    private static ulong? Counter(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetUInt64(out var number) ? number : null;
    private static bool? Boolean(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean() : null;
    private static double? Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
        field.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}
