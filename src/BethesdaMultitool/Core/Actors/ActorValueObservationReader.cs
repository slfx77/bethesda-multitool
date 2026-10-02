using System.Buffers.Binary;
using System.Text.Json;
using BethesdaMultitool.Core.RuntimeSession;

namespace BethesdaMultitool.Core.Actors;

/// <summary>Reads one typed native actor-state row; missing, duplicate and unavailable inputs stay missing.</summary>
internal static class ActorValueObservationReader
{
    internal static ActorValueCalculationState Read(JsonElement row)
    {
        var player = Boolean(row, "isPlayer");
        var creature = Boolean(row, "isCreature");
        var type = Unsigned(row, "engineTargetFormType");
        var reference = RuntimeTraceDocument.FormId(row, "engineTargetFormId");
        var baseId = RuntimeTraceDocument.FormId(row, "engineTargetBaseFormId");
        var role = player == true && creature == false && type == 0x3B && reference == 0x14 && baseId == 7
            ? ActorCalculationRole.Player
            : player == false && creature == true && type == 0x3C ? ActorCalculationRole.Creature
            : player == false && creature == false && type == 0x3B ? ActorCalculationRole.Npc
            : ActorCalculationRole.Unknown;
        if (reference is null or 0 or uint.MaxValue || baseId is null or 0 or uint.MaxValue)
            role = ActorCalculationRole.Unknown;
        var baseData = Object(row, "baseActorData");
        var expectedBaseType = role == ActorCalculationRole.Creature ? 0x2Bu : 0x2Au;
        if (!Observed(baseData) || RuntimeTraceDocument.FormId(baseData, "formId") != baseId ||
            Unsigned(baseData, "formType") != expectedBaseType)
        {
            baseData = default;
            role = ActorCalculationRole.Unknown;
        }
        var metadata = Array(row, "actorValueInfo");
        var health = Unique(metadata, item => Unsigned(item, "code") == 16 && Text(item, "name") == "Health");
        var endurance = Unique(metadata, item => Unsigned(item, "code") == 7 && Text(item, "name") == "Endurance");
        var luck = Unique(metadata, item => Unsigned(item, "code") == 11 && Text(item, "name") == "Luck");
        var critical = Unique(metadata, item => Unsigned(item, "code") == 14 && Text(item, "name") == "CritChance");
        var settings = new Dictionary<string, float>(StringComparer.Ordinal);
        var settingRows = Array(row, "gameSettings");
        foreach (var group in settingRows.Where(item => Text(item, "name") is not null).GroupBy(item => Text(item, "name")!))
        {
            var entries = group.ToArray();
            if (entries.Length == 1 && Observed(entries[0]) && Number(entries[0], "value") is { } value)
                settings.Add(group.Key, value);
        }
        var statistics = Array(row, "statistics");
        var level = Statistic("Level", "current");
        var floating = Object(row, "floatingPoint");
        var control = Text(floating, "scope") == "game-thread-before-actor-observation" && Text(floating, "evidence") == "fnstcw"
            ? Unsigned(floating, "x87ControlWord") : null;
        var overrides = Object(row, "baseOverride");
        var expectedGetter = role == ActorCalculationRole.Player ? 0x0094C640u : 0x00880660u;
        var hasOwnedOverride = Observed(overrides) &&
            (role is ActorCalculationRole.Player or ActorCalculationRole.Npc or ActorCalculationRole.Creature) &&
            (role != ActorCalculationRole.Creature ||
                (Unsigned(overrides, "engineTargetFormType") == 0x3C && Unsigned(overrides, "engineTargetBaseFormType") == 0x2B)) &&
            RuntimeTraceDocument.FormId(overrides, "engineTargetFormId") == reference &&
            RuntimeTraceDocument.FormId(overrides, "engineTargetBaseFormId") == baseId &&
            Text(overrides, "statistic") == "Health" &&
            Text(overrides, "evidence") == "pc012-loaded-getter;game-thread-call;identity-rechecked" &&
            Unsigned(overrides, "vtableOffset") == 0x48C && Unsigned(overrides, "getterAddress") == expectedGetter &&
            Unsigned(overrides, "actorAddress") is > 0 && Unsigned(baseData, "address") is > 0 &&
            Unsigned(overrides, "baseAddress") == Unsigned(baseData, "address");
        var storedEndurance = Unsigned(baseData, "storedEndurance");
        return new ActorValueCalculationState(Text(row, "executableSha256") ?? "", role,
            control is <= ushort.MaxValue ? (ushort)control.Value : null,
            Unsigned(baseData, "flags"), Unsigned(baseData, "templateFlags"), Number(baseData, "storedHealth"),
            level is >= 0 and <= ushort.MaxValue && MathF.Truncate(level.Value) == level ? (ushort)level.Value : null,
            Statistic("Endurance", "permanent"), Statistic("Luck", "current"),
            Unsigned(endurance, "flags"), Unsigned(luck, "flags"), Unsigned(health, "flags"),
            Unsigned(health, "baseCallback"), Unsigned(critical, "baseCallback"),
            hasOwnedOverride ? Boolean(overrides, "hasOverride") : null,
            hasOwnedOverride ? Number(overrides, "value") : null,
            hasOwnedOverride ? Unsigned(overrides, "getterAddress") : null, settings)
        {
            StoredEndurance = storedEndurance is <= byte.MaxValue ? (byte)storedEndurance.Value : null,
            CreatureHealth = role == ActorCalculationRole.Creature && hasOwnedOverride
                ? ReadCreatureHealth(row, baseData, overrides) : null,
            PlayerHealthSelectors = role == ActorCalculationRole.Player && hasOwnedOverride
                ? ReadPlayerHealthSelectors(row, baseData, overrides) : null,
            WeaponCritical = role == ActorCalculationRole.Player ? ReadWeaponCritical(row, baseData, overrides) : null
        };

        float? Statistic(string name, string component) => Number(Unique(statistics,
            item => Text(item, "statistic") == name && Text(item, "component") == component), "value");
    }

    private const string WeaponCriticalRoutineHex =
        "558bec83ec1c837d0c00740d8b4d0ce8fcf5dfff8945e8eb07c745e8ffffffff8b45e88945ecd9e8d95df4c645ff00837d0c00741a8b4d0ce883ddedff0fb6c885c9740b8b4d0ce874a81d00d95df4d945f4dc1d60200101dfe0f6c4447a05d9e8d95df4837d080074146a0e8b55088b028b4d088b500cffd2d95de4eb05d9eed95de4d945e4d95df0d945f0d875f4d95df8837d0c0074238b4d0ce890b02800dc1d60200101dfe0f6c401750e8b4d0ce87bb02800d84df8d95df8d945f88be55dc3";

    private const string PlayerCurrentGetterHex =
        "558bec83ec20894dfc8b4508508b4dfc8b55fc8b028b5004ffd28b4508506a008b4dfc81e9a4000000dd5df4e8ef160100dc45f48b4d08516a018b4dfc81e9a4000000dd5dece8d5160100dc45ec8b5508526a028b4dfc81e9a4000000dd5de4e8bb160100dc45e4d95de0d945e08be55dc20400";

    private static readonly (uint Address, string Hex)[] WeaponCriticalHelpers =
    [
        (0x00446390, "558bec51894dfc8b45fc0fbe80f40000008be55dc3"),
        (0x00524B40, "558bec51894dfc8b45fc0fb6880001000083e1020f95c08be55dc3"),
        (0x00821640, "558bec51894dfc8b45fcd980340100008be55dc3"),
        (0x008D1EB0, "558bec51894dfc8b45fcd980c40100008be55dc3"),
        (0x01012060, "0000000000000000"),
    ];

    private static ActorWeaponCriticalInputs? ReadWeaponCritical(JsonElement row, JsonElement baseData,
        JsonElement overrides)
    {
        var stage = Object(row, "weaponCriticalStage");
        if (!Observed(stage) || Unsigned(stage, "schemaVersion") != 1 ||
            Text(stage, "scope") != "equipped-weapon-pre-modifier" ||
            Text(stage, "equippedWeaponRoute") != "sdk-explicit-owner-GetEquippedObject-slot5" ||
            Text(stage, "readConsistency") != "bracketed-equal" || Boolean(stage, "identityStable") != true ||
            !string.Equals(Text(stage, "executableSha256"), Text(row, "executableSha256"), StringComparison.OrdinalIgnoreCase) ||
            Unsigned(stage, "engineTargetFormId") != 0x14 || Unsigned(stage, "baseFormId") != 7 ||
            Unsigned(stage, "actorFormType") != 0x3B || Unsigned(stage, "baseFormType") != 0x2A ||
            Unsigned(stage, "actorAddress") is not { } actor || actor == 0 || actor > uint.MaxValue - 0xA4 ||
            Unsigned(stage, "baseAddress") is not > 0 || Unsigned(stage, "baseAddress") != Unsigned(baseData, "address") ||
            (Unsigned(overrides, "actorAddress") is { } observedActor && observedActor != actor) ||
            Unsigned(stage, "actorValueOwnerAddress") != actor + 0xA4 ||
            Unsigned(stage, "actorValueOwnerVtable") != 0x0108A974 ||
            Unsigned(stage, "currentGetterSlot") != 0x0108A980 || Unsigned(stage, "currentGetter") != 0x0093ACB0 ||
            !HexEquals(stage, "currentGetterHex", PlayerCurrentGetterHex) ||
            Unsigned(stage, "routineAddress") != 0x00646D80 || !HexEquals(stage, "routineHex", WeaponCriticalRoutineHex) ||
            Unsigned(stage, "x87ControlWordBefore") != 0x007F || Unsigned(stage, "x87ControlWordAfter") != 0x007F ||
            !PositiveCounter(stage, "captureGeneration") || !PositiveCounter(stage, "connectionGeneration") ||
            !PositiveCounter(stage, "loadEpoch") || Boolean(stage, "weaponPresent") is not { } present ||
            FloatBits(stage, "currentCriticalChance", "currentCriticalChanceBeforeBits") is not { } current ||
            FloatBits(stage, "currentCriticalChanceAfter", "currentCriticalChanceAfterBits") is not { } after ||
            BitConverter.SingleToUInt32Bits(current) != BitConverter.SingleToUInt32Bits(after))
            return null;
        var helpers = Array(stage, "helperEvidence");
        if (helpers.Length != WeaponCriticalHelpers.Length || WeaponCriticalHelpers.Any(expected =>
                helpers.Count(item => Unsigned(item, "address") == expected.Address &&
                    HexEquals(item, "hex", expected.Hex)) != 1))
            return null;
        if (!present)
        {
            string[] emptyFields = ["weaponAddress", "weaponFormId", "weaponFormType", "weaponFlags", "weaponFlagsHex",
                "fireRate", "fireRateHex", "criticalMultiplier", "criticalMultiplierHex",
                "weaponFieldsBeforeHex", "weaponFieldsAfterHex"];
            return emptyFields.All(name => stage.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Null)
                ? new(actor, actor + 0xA4, current, false, null, null, null, null, null) : null;
        }
        if (Unsigned(stage, "weaponAddress") is not { } weapon || weapon == 0 || weapon > uint.MaxValue - 0x1C8 ||
            Unsigned(stage, "weaponFormId") is not { } id || id is 0 or uint.MaxValue ||
            Unsigned(stage, "weaponFormType") != 0x28 || Unsigned(stage, "weaponFlags") is not { } flags || flags > byte.MaxValue ||
            Text(stage, "weaponFieldsBeforeHex") is not { Length: 20 } rawHex ||
            !HexEquals(stage, "weaponFieldsAfterHex", rawHex) ||
            Text(stage, "weaponFlagsHex") is not { Length: 2 } flagsHex ||
            Text(stage, "fireRateHex") is not { Length: 8 } rateHex ||
            Text(stage, "criticalMultiplierHex") is not { Length: 8 } multiplierHex ||
            Number(stage, "criticalMultiplier") is not { } multiplier)
            return null;
        try
        {
            var raw = Convert.FromHexString(rawHex);
            var rate = Number(stage, "fireRate");
            if (raw[1] != flags || !flagsHex.Equals(rawHex.Substring(2, 2), StringComparison.OrdinalIgnoreCase) ||
                !rateHex.Equals(rawHex.Substring(4, 8), StringComparison.OrdinalIgnoreCase) ||
                !multiplierHex.Equals(rawHex.Substring(12, 8), StringComparison.OrdinalIgnoreCase) ||
                BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(6)) != BitConverter.SingleToUInt32Bits(multiplier) ||
                (rate is { } value && BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(2)) != BitConverter.SingleToUInt32Bits(value)) ||
                ((flags & 2) != 0 && rate is null)) return null;
            return new(actor, actor + 0xA4, current, true, id, weapon, (byte)flags, rate, multiplier);
        }
        catch (FormatException) { return null; }
    }

    // Call only after the input/identity reader admits this stage. The return is never an input.
    internal static float? ReadWeaponCriticalResult(JsonElement row) =>
        FloatBits(Object(row, "weaponCriticalStage"), "stageValue", "stageValueBits");

    private static bool PositiveCounter(JsonElement value, string name) => value.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.Number && field.TryGetUInt64(out var count) && count > 0;

    private static bool HexEquals(JsonElement value, string name, string expected) =>
        string.Equals(Text(value, name), expected, StringComparison.OrdinalIgnoreCase);

    private static float? FloatBits(JsonElement value, string name, string bitsName) =>
        Number(value, name) is { } number && Unsigned(value, bitsName) == BitConverter.SingleToUInt32Bits(number) ? number : null;

    private const string PlayerSelectorLookupHex =
        "558bec83ec10894df8d9eed95dfc8b45088945f4837df400743c837df4017448837df4027402eb508b4d0c894df0837df0107402eb0e8b55f8d982ac040000d95dfceb108b450c8b4df8d98481b0040000d95dfceb228b550c8b45f8d9849044020000d95dfceb108b4d0c8b55f8d9848a78030000d95dfcd945fc8be55dc20800";

    private static ActorPlayerHealthSelectors? ReadPlayerHealthSelectors(JsonElement row,
        JsonElement baseData, JsonElement overrides)
    {
        var selectors = Object(row, "modifierSelectors");
        if (!Observed(selectors) || Text(selectors, "resolverRoute") != "player-direct-loaded-lookup" ||
            Text(selectors, "semanticStatus") != "raw-selectors" ||
            Text(selectors, "readConsistency") != "repeated-field-and-identity-reads" ||
            Text(selectors, "evidence") != "pc007-player-av-wrappers-and-pc025-loaded-modifier-lookup" ||
            Boolean(selectors, "identityStable") != true ||
            !string.Equals(Text(selectors, "executableSha256"), Text(row, "executableSha256"), StringComparison.OrdinalIgnoreCase) ||
            Unsigned(selectors, "actorAddress") is not { } actor || actor == 0 || actor > uint.MaxValue - 0x4AC ||
            actor != Unsigned(overrides, "actorAddress") ||
            Unsigned(selectors, "baseAddress") != Unsigned(baseData, "address") ||
            Unsigned(selectors, "actorValueOwnerAddress") != actor + 0xA4 ||
            Unsigned(selectors, "ownerVtable") != 0x0108A974 ||
            Unsigned(selectors, "lookupAddress") != 0x0094C3D0 ||
            !string.Equals(Text(selectors, "lookupObservedHex"), PlayerSelectorLookupHex, StringComparison.OrdinalIgnoreCase))
            return null;
        var wrappers = Array(selectors, "wrappers");
        if (wrappers.Length != 3 ||
            !Wrapper(4, 0, 0x0094C460, "558bec51894dfc8b4508506a008b4dfc81e9a4000000e855ffffff8be55dc20400") ||
            !Wrapper(5, 2, 0x0094C490, "558bec51894dfc8b4508506a028b4dfc81e9a4000000e825ffffff8be55dc20400") ||
            !Wrapper(6, 1, 0x0094C4C0, "558bec51894dfc8b4508506a018b4dfc81e9a4000000e8f5feffff8be55dc20400"))
            return null;
        var observations = Array(selectors, "observations");
        return Sample(0, 0x284) is { } zero && Sample(1, 0x3B8) is { } one && Sample(2, 0x4AC) is { } two
            ? new(zero, one, two) : null;

        bool Wrapper(uint slot, uint selector, uint address, string code)
        {
            var matches = wrappers.Where(item => Unsigned(item, "slot") == slot).ToArray();
            return matches.Length == 1 && Unsigned(matches[0], "selector") == selector &&
                Unsigned(matches[0], "address") == address &&
                string.Equals(Text(matches[0], "observedHex"), code, StringComparison.OrdinalIgnoreCase);
        }

        float? Sample(uint selector, uint offset)
        {
            var sample = Unique(observations, item => Unsigned(item, "selector") == selector &&
                Unsigned(item, "actorValueCode") == 16);
            if (Unsigned(sample, "address") != actor + offset || Number(sample, "value") is not { } value ||
                Text(sample, "rawHex") is not { Length: 8 } raw ||
                Text(sample, "repeatRawHex") is not { Length: 8 } repeat ||
                !string.Equals(raw, repeat, StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString(raw)) ==
                    BitConverter.SingleToUInt32Bits(value) ? value : null;
            }
            catch (FormatException) { return null; }
        }
    }

    private static ActorCreatureHealthComponent? ReadCreatureHealth(JsonElement row, JsonElement baseData, JsonElement overrides)
    {
        if (Text(baseData, "rawHex") is not { Length: 392 } rawHex ||
            Unsigned(baseData, "storedHealth") is not { } stored ||
            Unsigned(overrides, "actorVtable") is not { } actorVtable)
            return null;
        byte[] raw;
        try { raw = Convert.FromHexString(rawHex); }
        catch (FormatException) { return null; }
        if (raw[4] != 0x2B || BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0xC)) != Unsigned(baseData, "formId") ||
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x34)) != Unsigned(baseData, "flags") ||
            BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x4A)) != Unsigned(baseData, "templateFlags") ||
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0xB4)) != stored)
            return null;
        return new(actorVtable, BinaryPrimitives.ReadUInt32LittleEndian(raw),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0xB0)), stored)
        {
            Scaling = ReadCreatureScaling(row, baseData, overrides, raw, rawHex)
        };
    }

    private static readonly (uint Address, string Hex)[] CreatureScalingCode =
    [
        (0x00461560, "558bec51894dfc68800000008b4dfce80c0000008be55dc3"),
        (0x00461580, "558bec51894dfc8b45fc8b4804234d080f95c08be55dc20400"),
        (0x0047D370, "558bec51894dfc8b45fc668b400c8be55dc3"),
        (0x0047D390, "558bec51894dfc8b45fc668b400e8be55dc3"),
        (0x0047D3B0, "558bec51894dfc8b45fc668b40108be55dc3"),
        (0x0047DED0, "558bec83ec2c56894dec8b4dece88ef4ffff668945fc8b4dece87236feff0fb6c085c00f84de0000000fb74dfc894de8db45e8dc35707b0101d95df8c745f4000000008b0d3cea1d01e8121533008945f0837df000740b8b55f083c2308955e4eb07c745e4000000008b45e48945f4837df40074348b4df4e823f4ffff0fb7c8894de0db45e0d84df8d97dde0fb745de0d000c00008945d8d96dd8db5dd4d96dde668b55d4668955fc8b4dece80ff4ffff0fb7c085c07e210fb775fc8b4dece8fcf3ffff0fb7c83bf17d0e8b4dece8edf3ffff668945fceb2e8b4dece8fff3ffff0fb7d085d27e1f0fb775fc8b4dece8ecf3ffff0fb7c03bf07e0c8b4dece8ddf3ffff668945fc668b45fc5e8be55dc3"),
        (0x005F8E90, "558bec83ec0c56894df433c0668945fc8b4df481e980000000e8b286e6ff0fb6c885c974398b4df481e980000000e80d50e8ff668945f80fbf55f883fa017d09b801000000668945f80fbf75f88b4df4e88bd112000faff0668975fceb0c8b4df4e87ad11200668945fc0fb745fc5e8be55dc3"),
        (0x00726070, "558bec51894dfc8b45fc8b40048be55dc3"),
        (0x007AF430, "558bec51894dfc8b45fc8b40208be55dc3"),
    ];

    private static ActorCreatureHealthScaling? ReadCreatureScaling(JsonElement row, JsonElement baseData,
        JsonElement overrides, byte[] raw, string rawHex)
    {
        var scaling = Object(row, "creatureHealthScaling");
        if (!Observed(scaling) || Unsigned(scaling, "schemaVersion") != 1 ||
            !scaling.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.Null ||
            Text(scaling, "scope") != "creature-health-scaled-inputs" ||
            Text(scaling, "readConsistency") != "repeated-owner-fields" ||
            Text(scaling, "inheritanceStatus") != "not-evaluated" ||
            Boolean(scaling, "identityStable") != true || Boolean(scaling, "noEngineCall") != true ||
            Boolean(scaling, "playerPresent") != true ||
            !string.Equals(Text(scaling, "executableSha256"), Text(row, "executableSha256"), StringComparison.OrdinalIgnoreCase) ||
            !PositiveCounter(scaling, "captureGeneration") || !PositiveCounter(scaling, "connectionGeneration") ||
            !PositiveCounter(scaling, "loadEpoch") ||
            Unsigned(scaling, "actorAddress") is not { } actor || actor == 0 || actor > uint.MaxValue - 0x24 ||
            actor != Unsigned(overrides, "actorAddress") ||
            Unsigned(scaling, "baseAddress") is not { } owner || owner == 0 || owner > uint.MaxValue - 0x104 ||
            owner != Unsigned(baseData, "address") || owner == actor ||
            Unsigned(scaling, "engineTargetFormId") != Unsigned(row, "engineTargetFormId") ||
            Unsigned(scaling, "baseFormId") != Unsigned(baseData, "formId") ||
            Unsigned(scaling, "actorFormType") != 0x3C || Unsigned(scaling, "baseFormType") != 0x2B ||
            Unsigned(scaling, "actorVtable") != 0x010870AC || Unsigned(scaling, "baseVtable") != 0x01048F5C ||
            Unsigned(scaling, "baseActorValueOwnerAddress") != owner + 0x100 ||
            Unsigned(scaling, "baseActorValueOwnerVtable") != 0x01048DC8 ||
            Unsigned(scaling, "healthComponentAddress") != owner + 0xB0 ||
            Unsigned(scaling, "healthComponentVtable") != 0x01048E6C ||
            Unsigned(scaling, "healthGetterSlot") != 0x01048E7C || Unsigned(scaling, "healthGetter") != 0x005F8E90 ||
            !HexEquals(scaling, "baseRawBeforeHex", rawHex) || !HexEquals(scaling, "baseRawAfterHex", rawHex) ||
            Unsigned(scaling, "playerSingletonAddress") != 0x011DEA3C ||
            Unsigned(scaling, "playerFormId") != 0x14 || Unsigned(scaling, "playerFormType") != 0x3B ||
            Unsigned(scaling, "playerBaseFormId") != 7 || Unsigned(scaling, "playerBaseFormType") != 0x2A ||
            Unsigned(scaling, "playerAddress") is not { } player || player == 0 || player > uint.MaxValue - 0x24 ||
            Unsigned(scaling, "playerBaseAddress") is not { } playerBase || playerBase == 0 || playerBase > uint.MaxValue - 0xC4 ||
            player == actor || player == owner || player == playerBase || playerBase == actor || playerBase == owner ||
            Text(scaling, "playerBaseRawBeforeHex") is not { Length: 392 } playerHex ||
            !HexEquals(scaling, "playerBaseRawAfterHex", playerHex) ||
            Unsigned(scaling, "levelDivisorAddress") != 0x01017B70 ||
            !HexEquals(scaling, "levelDivisorHex", "0000000000408f40"))
            return null;
        var code = Array(scaling, "codeEvidence");
        if (code.Length != CreatureScalingCode.Length || CreatureScalingCode.Any(expected =>
                code.Count(item => Unsigned(item, "address") == expected.Address && HexEquals(item, "hex", expected.Hex)) != 1))
            return null;
        byte[] playerRaw;
        try { playerRaw = Convert.FromHexString(playerHex); }
        catch (FormatException) { return null; }
        var level = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x3C));
        var minimum = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x3E));
        var maximum = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x40));
        var playerLevel = BinaryPrimitives.ReadUInt16LittleEndian(playerRaw.AsSpan(0x3C));
        if (playerRaw[4] != 0x2A || BinaryPrimitives.ReadUInt32LittleEndian(playerRaw) == 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(playerRaw.AsSpan(0xC)) != 7 ||
            Unsigned(scaling, "playerStoredLevel") != playerLevel ||
            Unsigned(scaling, "levelEncodedUnsigned") != level || Unsigned(scaling, "minimumLevel") != minimum ||
            Unsigned(scaling, "maximumLevel") != maximum ||
            Unsigned(scaling, "flags") != BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x34)) ||
            Unsigned(scaling, "templateFlags") != BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x4A)) ||
            Unsigned(scaling, "templatePointer") != BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x54)) ||
            Unsigned(scaling, "storedHealth") != BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0xB4)))
            return null;
        return new(level, minimum, maximum, playerLevel);
    }

    private static JsonElement Unique(JsonElement[] rows, Func<JsonElement, bool> predicate)
    {
        var matches = rows.Where(predicate).ToArray();
        return matches.Length == 1 && Observed(matches[0]) ? matches[0] : default;
    }

    private static bool Observed(JsonElement value) => Text(value, "status") == "observed";
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        ? RuntimeTraceDocument.Text(value, name) : null;
    private static JsonElement Object(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object ? child : default;
    private static JsonElement[] Array(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Array ? child.EnumerateArray().ToArray() : [];
    private static uint? Unsigned(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Number &&
        child.TryGetUInt32(out var number) ? number : null;
    private static bool? Boolean(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var child) && child.ValueKind is JsonValueKind.True or JsonValueKind.False ? child.GetBoolean() : null;
    private static float? Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Number &&
        child.TryGetSingle(out var number) && float.IsFinite(number) ? number : null;
}
