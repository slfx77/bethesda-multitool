using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Actors;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.RuntimeSession;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Actors;

public sealed class ActorValueCalculationTests
{
    // Inputs below are the small PC011 player/Doc controls, reshaped to Native15's typed
    // contract. PC011's raw-memory and actor-state evidence are separate captures, so this
    // fixture validates reconstruction and input admission; it is not a Native15 replay.
    [Theory]
    [InlineData(true, 80d, 9d)]
    [InlineData(false, 15d, 4d)]
    public void CallbackStagesKeepPredictedHealthSeparateFromObservedHealth(bool player, double health, double critical)
    {
        var row = Row(player);
        ((JsonArray)row["statistics"]!).Add(Statistic("Health", "base", 999999));
        var values = Evaluate(row);
        var derived = Assert.Single(values, value => value.Key == "Health.derived");
        Assert.Equal("Calculated", derived.Status);
        Assert.Equal(health, derived.Value);
        Assert.Equal(critical, Assert.Single(values, value => value.Key == "CritChance.derived").Value);
        var baseHealth = Assert.Single(values, value => value.Key == "Health.base");
        Assert.Equal("Unavailable", baseHealth.Status);
        Assert.Contains("observed Health base-override presence", baseHealth.MissingDependencies);
        Assert.DoesNotContain(derived.Inputs, input => input.Key.StartsWith("Health.", StringComparison.Ordinal) &&
            input.Key != "Health.baseCallback");
    }

    [Theory]
    [InlineData("null-flags")]
    [InlineData("string-flags")]
    [InlineData("duplicate-statistic")]
    [InlineData("unavailable-statistic")]
    [InlineData("duplicate-setting")]
    [InlineData("missing-setting")]
    [InlineData("wrong-callback")]
    [InlineData("wrong-x87")]
    [InlineData("wrong-executable")]
    [InlineData("auto-calc-npc")]
    public void MissingOrConflictingInputsRemainUnavailable(string control)
    {
        var row = Row(false);
        switch (control)
        {
            case "null-flags": row["actorValueInfo"]![0]!["flags"] = null; break;
            case "string-flags": row["actorValueInfo"]![0]!["flags"] = "32777"; break;
            case "duplicate-statistic": ((JsonArray)row["statistics"]!).Add(Statistic("Endurance", "permanent", 4)); break;
            case "unavailable-statistic": row["statistics"]![1]!["status"] = "unavailable"; break;
            case "duplicate-setting": ((JsonArray)row["gameSettings"]!).Add(Setting("fAVDNPCHealthEnduranceMult", 5)); break;
            case "missing-setting": ((JsonArray)row["gameSettings"]!).RemoveAt(3); break;
            case "wrong-callback": row["actorValueInfo"]![3]!["baseCallback"] = 1; break;
            case "wrong-x87": row["floatingPoint"]!["x87ControlWord"] = 0x027F; break;
            case "wrong-executable": row["executableSha256"] = new string('e', 64); break;
            case "auto-calc-npc": row["baseActorData"]!["flags"] = 0x10; break;
        }
        var health = Assert.Single(Evaluate(row), value => value.Key == "Health.derived");
        Assert.Equal("Unavailable", health.Status);
        Assert.Null(health.Value);
        Assert.NotEmpty(health.MissingDependencies);
    }

    [Theory]
    [InlineData("base-id")]
    [InlineData("base-type")]
    [InlineData("role")]
    public void InconsistentActorIdentityRejectsEveryStage(string control)
    {
        var row = Row(false);
        if (control == "base-id") row["baseActorData"]!["formId"] = 7;
        if (control == "base-type") row["baseActorData"]!["formType"] = 0x2B;
        if (control == "role") row["isPlayer"] = true;
        Assert.All(Evaluate(row), value => Assert.Equal("Unavailable", value.Status));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void OverrideZeroRequiresExplicitReferenceBaseAndStatisticOwnership(bool matchingOwner, bool available)
    {
        var row = Row(false);
        row["baseOverride"] = Override(false, true, 0);
        row["baseOverride"]!["engineTargetFormId"] = matchingOwner ? 0x104C0F : 0x14;
        var value = Assert.Single(Evaluate(row), value => value.Key == "Health.base");
        Assert.Equal(available ? "Calculated" : "Unavailable", value.Status);
        Assert.Equal(available ? 0d : (double?)null, value.Value);
    }

    // Control values exercise the inspected dispatcher branches; live Native16/17 calibration is separate.
    [Theory]
    [InlineData(true, false, 777f, 180d)]
    [InlineData(false, false, 777f, 65d)]
    [InlineData(true, true, 0f, 0d)]
    [InlineData(false, true, 0f, 0d)]
    [InlineData(false, true, -5f, -5d)]
    [InlineData(false, true, 67.5f, 67.5d)]
    public void BaseHealthDistinguishesAbsentOverrideFromPresentValue(bool player, bool present, float value, double expected)
    {
        var row = Row(player);
        row["baseOverride"] = Override(player, present, value);
        ((JsonArray)row["statistics"]!).Add(Statistic("Health", "base", 999999));
        if (present)
        {
            row["baseActorData"]!["storedHealth"] = null;
            row["actorValueInfo"]![3]!["baseCallback"] = 0;
        }
        var result = Assert.Single(Evaluate(row), item => item.Key == "Health.base");
        Assert.Equal("Calculated", result.Status);
        Assert.Equal(expected, result.Value);
        Assert.Equal("Reconstruction", result.CalculationBasis);
        Assert.Contains(result.Inputs, input => input.Key == "Health.overridePresent" && input.Value == (present ? 1 : 0));
        Assert.DoesNotContain(result.Inputs, input => input.Key == "Health.base");
        if (!present) Assert.DoesNotContain(result.Inputs, input => input.Key == "Health.override");
    }

    [Theory]
    [InlineData(0x10C1u, 50f, 0u, 65d)]
    [InlineData(0x1081u, null, null, 15d)]
    [InlineData(0x1841u, 50f, 0x8000u, 65d)]
    [InlineData(0x1001u, 50f, 0u, null)]
    [InlineData(0x10C1u, null, 0u, null)]
    [InlineData(0x10C1u, 50f, null, null)]
    [InlineData(0x10C1u, 50f, 2u, null)]
    public void BaseHealthUsesStoredInclusionFlagAndGatesUnknownDispatcherBranches(
        uint flags, float? stored, uint? templateFlags, double? expected)
    {
        var row = Row(false);
        row["baseOverride"] = Override(false, false, 0);
        row["actorValueInfo"]![3]!["flags"] = flags;
        row["baseActorData"]!["storedHealth"] = stored;
        row["baseActorData"]!["templateFlags"] = templateFlags;
        var result = Assert.Single(Evaluate(row), item => item.Key == "Health.base");
        Assert.Equal(expected is null ? "Unavailable" : "Calculated", result.Status);
        Assert.Equal(expected, result.Value);
        if (expected is not null)
        {
            Assert.Contains(result.Inputs, input => input.Key == "Health.includesStored" && input.Value == ((flags & 0x40) != 0 ? 1 : 0));
            Assert.Equal((flags & 0x40) != 0, result.Inputs.Any(input => input.Key == "Health.stored"));
        }
    }

    [Theory]
    [InlineData("base-id")]
    [InlineData("base-address")]
    [InlineData("getter")]
    [InlineData("getter-type")]
    [InlineData("vtable-offset")]
    [InlineData("evidence")]
    [InlineData("actor-address")]
    [InlineData("presence")]
    [InlineData("present-value")]
    [InlineData("unavailable")]
    public void OverrideRequiresVerifiedGetterAndExplicitOwnedFields(string control)
    {
        var row = Row(false);
        var observation = Override(false, true, 0);
        row["baseOverride"] = observation;
        switch (control)
        {
            case "base-id": observation["engineTargetBaseFormId"] = 7; break;
            case "base-address": observation["baseAddress"] = 0x23450000; break;
            case "getter": observation["getterAddress"] = 0x94C640; break;
            case "getter-type": observation["getterAddress"] = "0x880660"; break;
            case "vtable-offset": observation["vtableOffset"] = 0x490; break;
            case "evidence": observation["evidence"] = "unverified"; break;
            case "actor-address": observation["actorAddress"] = 0; break;
            case "presence": observation["hasOverride"] = null; break;
            case "present-value": observation["value"] = null; break;
            case "unavailable": observation["status"] = "unavailable"; break;
        }
        var result = Assert.Single(Evaluate(row), item => item.Key == "Health.base");
        Assert.Equal("Unavailable", result.Status);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("split-inputs", false)]
    [InlineData("header-hash", false)]
    [InlineData("selected-sequence", false)]
    [InlineData("source-hash", false)]
    [InlineData("record-kind", false)]
    [InlineData("weapon-stage", true)]
    public async Task ServiceUsesOneExactBoundObservationWithoutJoiningOtherRows(string control, bool calculated)
    {
        using var temp = CliExeRunner.CreateTempDirectory();
        var pluginHash = new string('a', 64);
        var headerHash = control == "header-hash" ? new string('b', 64) : ActorEngineProfile.PcRetail.ExecutableSha256;
        var weaponStage = control == "weapon-stage";
        var reference = weaponStage ? 0x14u : 0x104C0Fu;
        var first = weaponStage ? WeaponCriticalRow() : Row(false);
        first["baseOverride"] = Override(weaponStage, false, 0);
        if (weaponStage)
        {
            first["weaponCriticalStage"]!["stageValue"] = 999f;
            first["weaponCriticalStage"]!["stageValueBits"] = BitConverter.SingleToUInt32Bits(999f);
        }
        var second = (JsonObject)first.DeepClone();
        first["sequence"] = 3;
        second["sequence"] = 4;
        if (control == "split-inputs")
        {
            first["statistics"]![1]!["status"] = "unavailable";
            second["gameSettings"] = new JsonArray();
        }
        string[] lines =
        [
            new JsonObject { ["kind"] = "capture-header", ["schema"] = "bmt/runtime-trace", ["version"] = 1,
                ["identity"] = new JsonObject { ["sequence"] = 1, ["executableFileSha256"] = headerHash,
                    ["activePluginIdentityStatus"] = "complete", ["activePlugins"] = new JsonArray(new JsonObject
                    { ["index"] = 0, ["name"] = "FalloutNV.esm", ["sha256"] = pluginHash,
                        ["hashScope"] = "copied-backing-file", ["status"] = "verified" }) } }.ToJsonString(),
            """{"kind":"capture-start","protocol":1,"sequence":2,"dropped":0}""",
            first.ToJsonString(), second.ToJsonString(),
            """{"kind":"capture-end","status":"completed","protocol":1,"sequence":5,"dropped":0}""",
            """{"kind":"capture-footer","status":"completed","events":4,"dropped":0,"snapshots":0,"errors":0}"""
        ];
        await File.WriteAllLinesAsync(Path.Combine(temp.Path, "trace.ndjson"), lines, TestContext.Current.CancellationToken);
        var scenarioPath = Path.Combine(temp.Path, "scenario.json");
        await File.WriteAllTextAsync(scenarioPath, new JsonObject
        {
            ["runtimeTrace"] = "trace.ndjson", ["referenceFormId"] = reference, ["executableSha256"] = headerHash,
            ["calculationObservationSequence"] = control == "selected-sequence" ? 8 : 3
        }.ToJsonString(), TestContext.Current.CancellationToken);
        var actor = new ActorInspection(weaponStage ? 7u : 0x104C0Cu, "Actor", control == "record-kind" ? "CREA" : "NPC_", [], [], null)
        { Game = BethesdaGame.FalloutNewVegas };
        var values = await ActorStatisticsService.InspectAsync(actor, "pc-retail", scenarioPath,
            TestContext.Current.CancellationToken, new RuntimeTraceSources(
                [new(0, "FalloutNV.esm", control == "source-hash" ? new string('c', 64) : pluginHash)], true));
        var health = values.SingleOrDefault(value => value.Key == "Health.derived");
        if (!calculated) Assert.True(health is null || health.Status == "Unavailable");
        else
        {
            Assert.NotNull(health);
            Assert.Equal("Calculated", health.Status);
            Assert.Equal(weaponStage ? 80d : 15d, health.Value);
            Assert.Equal(3UL, health.Sequence);
            Assert.Equal(reference, health.ReferenceFormId);
            Assert.EndsWith("trace.ndjson#L3", health.Evidence, StringComparison.Ordinal);
            Assert.Equal(64, health.TraceSha256!.Length);
            Assert.Equal("Complete", health.TraceStatus);
            var baseHealth = Assert.Single(values, value => value.Key == "Health.base" && value.Status == "Calculated");
            Assert.Equal(weaponStage ? 180d : 65d, baseHealth.Value);
            Assert.Equal(health.Sequence, baseHealth.Sequence);
            Assert.Equal(health.TraceSha256, baseHealth.TraceSha256);
            Assert.Equal(health.ReferenceFormId, baseHealth.ReferenceFormId);
            if (weaponStage)
            {
                var predicted = Assert.Single(values, value => value.Key == "CritChance.weaponStage" && value.Status == "Calculated");
                var observed = Assert.Single(values, value => value.Key == "CritChance.weaponStage" && value.Status == "Observed");
                Assert.Equal(9d, predicted.Value);
                Assert.Equal(999d, observed.Value);
                Assert.Equal(predicted.Sequence, observed.Sequence);
                Assert.Equal(predicted.ReferenceFormId, observed.ReferenceFormId);
                Assert.Equal(predicted.TraceSha256, observed.TraceSha256);
                Assert.Equal("Unavailable", Assert.Single(values, value => value.Key == "RuntimeCriticalChance").Status);
            }
        }
    }

    // 00603FC0 reads base+BE, floors the unsigned level, and rounds each arithmetic
    // operation before EC62C0 truncates. The last two cases distinguish double math.
    [Theory]
    [InlineData(4, 7, -1f, 5f, 5f, 45d)]
    [InlineData(4, 0, -1f, 5f, 5f, 15d)]
    [InlineData(4, 1, -1f, 5f, 5f, 15d)]
    [InlineData(4, 2, -1f, 5f, 5f, 20d)]
    [InlineData(4, 65535, -1f, 5f, 5f, 327685d)]
    [InlineData(11, 1, -1f, 5f, 5f, 50d)]
    [InlineData(255, 1, -1f, 5f, 5f, 1270d)]
    [InlineData(0, 1, 1.3f, 10f, 0f, 13d)]
    [InlineData(4, 1, 1.3f, 10f, 0f, 53d)]
    public void AutoCalcUsesStoredEnduranceAndOrderedLevelArithmetic(
        int endurance, int level, float offset, float multiplier, float levelMultiplier, double expected)
    {
        var row = AutoCalcRow();
        row["baseActorData"]!["storedEndurance"] = endurance;
        row["statistics"]![0]!["value"] = level;
        row["statistics"]![1]!["value"] = 9;
        row["gameSettings"]![2]!["value"] = offset;
        row["gameSettings"]![3]!["value"] = multiplier;
        row["gameSettings"]![5]!["value"] = levelMultiplier;
        var result = Assert.Single(Evaluate(row), value => value.Key == "Health.derived");
        Assert.Equal("Calculated", result.Status);
        Assert.Equal(expected, result.Value);
        Assert.Contains(result.Inputs, input => input.Key == "Endurance.stored" && input.Value == endurance);
        Assert.DoesNotContain(result.Inputs, input => input.Key == "Endurance.permanent");
        Assert.Contains(result.Inputs, input => input.Key == "Level.floored" &&
            input.Value == Math.Max(level, 1) && input.Provenance == "Reconstruction");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void AutoCalcZeroStoredHealthBypassesUnusedInputs(int stored, bool calculated)
    {
        var row = AutoCalcRow();
        row["baseActorData"]!["storedHealth"] = stored;
        ((JsonObject)row["baseActorData"]!).Remove("storedEndurance");
        row["statistics"] = new JsonArray();
        row["gameSettings"] = new JsonArray();
        var result = Assert.Single(Evaluate(row), value => value.Key == "Health.derived");
        Assert.Equal(calculated ? "Calculated" : "Unavailable", result.Status);
        Assert.Equal(calculated ? 0d : (double?)null, result.Value);
    }

    [Theory]
    [InlineData(1.75f, 1d)]
    [InlineData(2.5f, 2d)]
    [InlineData(-1.75f, 0d)]
    [InlineData(-0.75f, 0d)]
    [InlineData(2147483520f, 2147483520d)]
    [InlineData(2147483648f, null)]
    [InlineData(-2147483648f, 0d)]
    [InlineData(-2147483904f, null)]
    public void AutoCalcTruncatesBeforeClampingAndRejectsConversionOverflow(float input, double? expected)
    {
        var row = AutoCalcRow();
        row["baseActorData"]!["storedEndurance"] = 0;
        row["statistics"]![0]!["value"] = 1;
        row["gameSettings"]![2]!["value"] = input;
        row["gameSettings"]![3]!["value"] = 1;
        var result = Assert.Single(Evaluate(row), value => value.Key == "Health.derived");
        Assert.Equal(expected is null ? "Unavailable" : "Calculated", result.Status);
        Assert.Equal(expected, result.Value);
        if (expected is null)
            Assert.Contains("auto-calc conversion within signed 32-bit range", result.MissingDependencies);
    }

    [Theory]
    [InlineData("missing-endurance")]
    [InlineData("null-endurance")]
    [InlineData("string-endurance")]
    [InlineData("negative-endurance")]
    [InlineData("large-endurance")]
    [InlineData("fractional-endurance")]
    [InlineData("missing-health")]
    [InlineData("negative-health")]
    [InlineData("fractional-health")]
    [InlineData("missing-template")]
    [InlineData("use-stats")]
    [InlineData("duplicate-setting")]
    [InlineData("wrong-callback")]
    [InlineData("wrong-x87")]
    [InlineData("wrong-owner")]
    public void AutoCalcRejectsUnavailableInputsAndPreservesOwnershipGates(string control)
    {
        var row = AutoCalcRow();
        var baseData = (JsonObject)row["baseActorData"]!;
        switch (control)
        {
            case "missing-endurance": baseData.Remove("storedEndurance"); break;
            case "null-endurance": baseData["storedEndurance"] = null; break;
            case "string-endurance": baseData["storedEndurance"] = "4"; break;
            case "negative-endurance": baseData["storedEndurance"] = -1; break;
            case "large-endurance": baseData["storedEndurance"] = 256; break;
            case "fractional-endurance": baseData["storedEndurance"] = 4.5; break;
            case "missing-health": baseData["storedHealth"] = null; break;
            case "negative-health": baseData["storedHealth"] = -1; break;
            case "fractional-health": baseData["storedHealth"] = 0.5; break;
            case "missing-template": baseData["templateFlags"] = null; break;
            case "use-stats": baseData["templateFlags"] = 2; break;
            case "duplicate-setting": ((JsonArray)row["gameSettings"]!).Add(Setting("fAVDNPCHealthLevelMult", 5)); break;
            case "wrong-callback": row["actorValueInfo"]![3]!["baseCallback"] = 1; break;
            case "wrong-x87": row["floatingPoint"]!["x87ControlWord"] = 0x027F; break;
            case "wrong-owner": baseData["formId"] = 7; break;
        }
        var result = Assert.Single(Evaluate(row), value => value.Key == "Health.derived");
        Assert.Equal("Unavailable", result.Status);
        Assert.Null(result.Value);
        Assert.NotEmpty(result.MissingDependencies);
    }

    [Theory]
    [InlineData(float.MaxValue, 2f)]
    [InlineData(float.Epsilon, 1f)]
    [InlineData(1.17549435E-38f, 0.25f)]
    [InlineData(1E-30f, 1E-30f)]
    public void AutoCalcDoesNotSubstituteSingleExponentUnderflowOrOverflow(float offset, float multiplier)
    {
        var row = AutoCalcRow();
        row["baseActorData"]!["storedEndurance"] = 0;
        row["gameSettings"]![2]!["value"] = offset;
        row["gameSettings"]![3]!["value"] = multiplier;
        var result = Assert.Single(Evaluate(row), value => value.Key == "Health.derived");
        Assert.Equal("Unavailable", result.Status);
        Assert.Contains("auto-calc arithmetic within supported single-precision exponent range", result.MissingDependencies);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void AutoCalcRejectsNonfiniteSettingValues(float setting)
    {
        using var json = JsonDocument.Parse(AutoCalcRow().ToJsonString());
        var state = ActorValueObservationReader.Read(json.RootElement);
        var settings = new Dictionary<string, float>(state.GameSettings, StringComparer.Ordinal)
        { ["fAVDNPCHealthEnduranceMult"] = setting };
        var result = Assert.Single(ActorValueCalculation.Evaluate(ActorEngineProfile.PcRetail,
            state with { GameSettings = settings }), value => value.Key == "Health.derived");
        Assert.Equal("Unavailable", result.Status);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(false, 95d)]
    [InlineData(true, 0d)]
    public void AutoCalcPc050InputsPredictBaseHealthAndPreserveExplicitOverride(bool present, double expected)
    {
        // PC050 health.ndjson line1016/sequence1011 supplies H50/E4/L7 and -1/5/5.
        // 00603FC0 independently yields45; the existing stored-inclusion branch yields95.
        var row = AutoCalcRow();
        row["baseActorData"]!["templateFlags"] = 0x8001;
        row["baseOverride"] = Override(false, present, 0);
        ((JsonArray)row["statistics"]!).Add(Statistic("Health", "base", 999999));
        var values = Evaluate(row);
        var derived = Assert.Single(values, value => value.Key == "Health.derived");
        Assert.Equal(45d, derived.Value);
        Assert.Equal("Reconstruction", derived.CalculationBasis);
        var baseHealth = Assert.Single(values, value => value.Key == "Health.base");
        Assert.Equal("Calculated", baseHealth.Status);
        Assert.Equal(expected, baseHealth.Value);
        Assert.DoesNotContain(baseHealth.Inputs, input => input.Key is "Health.base" or "Health.current" or "Health.permanent");
    }

    [Theory]
    [InlineData(0u, 0d)]
    [InlineData(137u, 137d)]
    [InlineData(65535u, 65535d)]
    [InlineData(65536u, 0d)]
    [InlineData(65537u, 1d)]
    [InlineData(uint.MaxValue, 65535d)]
    public void CreatureFixedComponentUsesExactUInt16AndIndependentStoredInput(uint stored, double expected)
    {
        var row = CreatureRow();
        row["baseActorData"]!["storedHealth"] = stored;
        SetCreatureRaw(row, 0xB4, stored);
        row["statistics"] = new JsonArray(Statistic("Health", "base", 999999));
        row["gameSettings"] = new JsonArray();
        row["actorValueInfo"]![3]!["baseCallback"] = 0;
        var values = Evaluate(row);
        var health = Assert.Single(values, value => value.Key == "Health.base");
        Assert.Equal("Calculated", health.Status);
        Assert.Equal(expected, health.Value);
        Assert.Equal("Reconstruction", health.CalculationBasis);
        Assert.Contains(health.Inputs, input => input.Key == "Health.stored" && input.Value == stored);
        Assert.DoesNotContain(health.Inputs, input => input.Key is "Health.base" or "Health.current" or
            "Health.permanent" or "Level.current" or "Endurance.permanent");
        Assert.Equal("Unavailable", Assert.Single(values, value => value.Key == "Health.derived").Status);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(67.5f)]
    public void CreatureExplicitOverrideUsesOwnedNative39Getter(float value)
    {
        var row = CreatureRow();
        row["baseOverride"]!["hasOverride"] = true;
        row["baseOverride"]!["value"] = value;
        row["baseActorData"]!["rawHex"] = null;
        row["baseActorData"]!["storedHealth"] = null;
        row["baseActorData"]!["templateFlags"] = null;
        var result = Assert.Single(Evaluate(row), item => item.Key == "Health.base");
        Assert.Equal("Calculated", result.Status);
        Assert.Equal((double)value, result.Value);
        Assert.DoesNotContain(result.Inputs, input => input.Key == "Health.stored");
    }

    [Theory]
    [InlineData("override-actor-type")]
    [InlineData("override-base-type")]
    [InlineData("override-owner")]
    [InlineData("getter")]
    [InlineData("actor-table")]
    [InlineData("base-table")]
    [InlineData("health-table")]
    [InlineData("missing-raw")]
    [InlineData("malformed-raw")]
    [InlineData("raw-id")]
    [InlineData("raw-health")]
    [InlineData("fractional-health")]
    [InlineData("raw-flags")]
    [InlineData("raw-template")]
    [InlineData("scaled")]
    [InlineData("use-stats")]
    [InlineData("missing-template")]
    [InlineData("missing-health-flags")]
    [InlineData("no-bypass")]
    [InlineData("alternate-component")]
    public void CreatureComponentRequiresOwnedUnscaledProfileRoute(string control)
    {
        var row = CreatureRow();
        var data = row["baseActorData"]!;
        var observation = row["baseOverride"]!;
        switch (control)
        {
            case "override-actor-type": observation["engineTargetFormType"] = 0x3B; break;
            case "override-base-type": observation["engineTargetBaseFormType"] = 0x2A; break;
            case "override-owner": observation["engineTargetFormId"] = 0x14; break;
            case "getter": observation["getterAddress"] = 0x94C640; break;
            case "actor-table": observation["actorVtable"] = 0x01086A6C; break;
            case "base-table": SetCreatureRaw(row, 0, 0x01047A6C); break;
            case "health-table": SetCreatureRaw(row, 0xB0, 0); break;
            case "missing-raw": data["rawHex"] = null; break;
            case "malformed-raw": data["rawHex"] = new string('z', 392); break;
            case "raw-id": SetCreatureRaw(row, 0xC, 7); break;
            case "raw-health": SetCreatureRaw(row, 0xB4, 138); break;
            case "fractional-health": data["storedHealth"] = 137.5; break;
            case "raw-flags": data["flags"] = 0; break;
            case "raw-template": data["templateFlags"] = 1; break;
            case "scaled": data["flags"] = 0xC0; SetCreatureRaw(row, 0x34, 0xC0); break;
            case "use-stats": data["templateFlags"] = 2; SetCreatureRaw(row, 0x4A, 2); break;
            case "missing-template": data["templateFlags"] = null; break;
            case "missing-health-flags": row["actorValueInfo"]![3]!["flags"] = null; break;
            case "no-bypass": row["actorValueInfo"]![3]!["flags"] = 0xC1; break;
            case "alternate-component": row["actorValueInfo"]![3]!["flags"] = 0x14C1; break;
        }
        var health = Assert.Single(Evaluate(row), value => value.Key == "Health.base");
        Assert.Equal("Unavailable", health.Status);
        Assert.Null(health.Value);
    }

    [Theory]
    [InlineData(0f, 180d)]
    [InlineData(-1f, 179d)]
    public void PlayerCurrentUsesPc028SelectorsWithoutObservedHealth(float selector2, double expected)
    {
        // PC028 health lines366/1417 and verify line505: 180 -> 179 -> 180.
        // Independent base inputs give180; raw selector2 changes0 -> -1 -> 0.
        var row = PlayerCurrentRow();
        SetPlayerSelector(row, 2, selector2);
        ((JsonArray)row["statistics"]!).Add(Statistic("Health", "current", 999999));
        ((JsonArray)row["statistics"]!).Add(Statistic("Health", "base", 999998));
        ((JsonArray)row["statistics"]!).Add(Statistic("Health", "permanent", 999997));
        var values = Evaluate(row);
        var value = Assert.Single(values, item => item.Key == "Health.current");
        Assert.Equal("Calculated", value.Status);
        Assert.Equal(expected, value.Value);
        Assert.Equal("Reconstruction", value.CalculationBasis);
        Assert.Contains(value.Inputs, input => input.Key == "Health.base" && input.Value == 180 &&
            input.Provenance == "Reconstruction");
        Assert.Contains(value.Inputs, input => input.Key == "Health.selector2" && input.Value == selector2 &&
            input.Provenance == "Observed");
        Assert.DoesNotContain(value.Inputs, input => input.Key is "Health.current" or "Health.permanent");
        var permanent = Assert.Single(values, item => item.Key == "Health.permanent");
        Assert.Equal("Calculated", permanent.Status);
        Assert.Equal(180d, permanent.Value);
        Assert.DoesNotContain(permanent.Inputs, input => input.Key is "Health.current" or "Health.permanent" or
            "Health.selector0" or "Health.selector2");
    }

    // Literal outcomes follow the three FADDs in0093ACB0, not one double sum.
    // At2^24, +1 ties to even; at1, ULP=2^-23 and half-ULP=2^-24.
    [Theory]
    [InlineData(16777216f, 1f, -16777216f, 1f, 1d)]
    [InlineData(16777216f, -16777216f, 1f, 1f, 2d)]
    [InlineData(1f, 5.96046448E-8f, 5.96046448E-8f, 0f, 1d)]
    [InlineData(1f, 1.19209290E-7f, 5.96046448E-8f, 0f, 1.0000002384185791d)]
    [InlineData(-10f, 2f, 3f, -6f, -11d)]
    [InlineData(0f, 0f, 0f, 0f, 0d)]
    public void PlayerCurrentPreservesInstructionOrderAndSinglePrecision(
        float baseHealth, float selector0, float selector1, float selector2, double expected)
    {
        var row = PlayerCurrentRow();
        row["baseOverride"] = Override(true, true, baseHealth);
        SetPlayerSelector(row, 0, selector0);
        SetPlayerSelector(row, 1, selector1);
        SetPlayerSelector(row, 2, selector2);
        var value = Assert.Single(Evaluate(row), item => item.Key == "Health.current");
        Assert.Equal("Calculated", value.Status);
        Assert.Equal(expected, value.Value);
    }

    [Theory]
    [InlineData(float.MaxValue, float.MaxValue, false)]
    [InlineData(1.17549435E-38f, -5.87747175E-39f, false)]
    [InlineData(1.17549435E-38f, -1.17549435E-38f, true)]
    [InlineData(float.MaxValue, -float.MaxValue, true)]
    public void PlayerCurrentRejectsUnsupportedExponentResultsButAllowsExactCancellation(
        float baseHealth, float selector0, bool available)
    {
        var row = PlayerCurrentRow();
        row["baseOverride"] = Override(true, true, baseHealth);
        SetPlayerSelector(row, 0, selector0);
        var value = Assert.Single(Evaluate(row), item => item.Key == "Health.current");
        Assert.Equal(available ? "Calculated" : "Unavailable", value.Status);
        Assert.Equal(available ? 0d : (double?)null, value.Value);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("status")]
    [InlineData("route")]
    [InlineData("identity")]
    [InlineData("executable")]
    [InlineData("actor")]
    [InlineData("base")]
    [InlineData("owner-address")]
    [InlineData("owner-table")]
    [InlineData("lookup-address")]
    [InlineData("lookup-code")]
    [InlineData("wrapper-code")]
    [InlineData("duplicate-wrapper")]
    [InlineData("missing-health")]
    [InlineData("duplicate-health")]
    [InlineData("sample-unavailable")]
    [InlineData("sample-address")]
    [InlineData("sample-value")]
    [InlineData("raw-malformed")]
    [InlineData("raw-nonfinite")]
    [InlineData("repeat")]
    [InlineData("address-overflow")]
    [InlineData("missing-base-calculation")]
    [InlineData("nonplayer")]
    [InlineData("x87")]
    public void PlayerCurrentRejectsIncompleteOrMisattributedSelectorRows(string control)
    {
        var row = PlayerCurrentRow();
        var selectors = row["modifierSelectors"]!;
        var observations = (JsonArray)selectors["observations"]!;
        var sample = observations[2]!;
        switch (control)
        {
            case "missing": row["modifierSelectors"] = null; break;
            case "status": selectors["status"] = "partial"; break;
            case "route": selectors["resolverRoute"] = "npc-process"; break;
            case "identity": selectors["identityStable"] = false; break;
            case "executable": selectors["executableSha256"] = new string('e', 64); break;
            case "actor": selectors["actorAddress"] = 0x12350004; break;
            case "base": selectors["baseAddress"] = 0x12340004; break;
            case "owner-address": selectors["actorValueOwnerAddress"] = 0x123500A8; break;
            case "owner-table": selectors["ownerVtable"] = 0x010869A4; break;
            case "lookup-address": selectors["lookupAddress"] = 0x0094C3D4; break;
            case "lookup-code": selectors["lookupObservedHex"] = "00"; break;
            case "wrapper-code": selectors["wrappers"]![0]!["observedHex"] = "00"; break;
            case "duplicate-wrapper": selectors["wrappers"]![2] = selectors["wrappers"]![0]!.DeepClone(); break;
            case "missing-health": observations.RemoveAt(2); break;
            case "duplicate-health": observations.Add(sample.DeepClone()); break;
            case "sample-unavailable": sample["status"] = "unavailable"; break;
            case "sample-address": sample["address"] = 0x123504F0; break;
            case "sample-value": sample["value"] = 1; break;
            case "raw-malformed": sample["rawHex"] = "zzzzzzzz"; sample["repeatRawHex"] = "zzzzzzzz"; break;
            case "raw-nonfinite": sample["rawHex"] = "0000c07f"; sample["repeatRawHex"] = "0000c07f"; break;
            case "repeat": sample["repeatRawHex"] = "000080bf"; break;
            case "address-overflow":
                selectors["actorAddress"] = uint.MaxValue - 1;
                row["baseOverride"]!["actorAddress"] = uint.MaxValue - 1;
                break;
            case "missing-base-calculation": row["baseOverride"]!["hasOverride"] = null; break;
            case "nonplayer": row["isPlayer"] = false; break;
            case "x87": row["floatingPoint"]!["x87ControlWord"] = 0x027F; break;
        }
        var value = Assert.Single(Evaluate(row), item => item.Key == "Health.current");
        Assert.Equal("Unavailable", value.Status);
        Assert.Null(value.Value);
        Assert.NotEmpty(value.MissingDependencies);
    }

    [Theory]
    [InlineData(180f, 5f, 0x10C1u, 185d)]
    [InlineData(180f, 5f, 0x10u, 100d)]
    [InlineData(-10f, 2f, 8u, 0d)]
    [InlineData(-10f, 2f, 0x8008u, 1d)]
    [InlineData(16777216f, 1f, 0u, 16777216d)]
    [InlineData(1.00000011920928955078125f, 5.96046448E-8f, 0u, 1.0000002384185791d)]
    [InlineData(float.MaxValue, float.MaxValue, 0u, null)]
    [InlineData(1.17549435E-38f, -5.87747175E-39f, 0u, null)]
    [InlineData(float.MaxValue, -float.MaxValue, 0u, 0d)]
    [InlineData(180f, 0f, null, null)]
    public void PermanentHealthAddsOnlySelectorOneThenAppliesTheObservedClamp(
        float baseHealth, float selector1, uint? flags, double? expected)
    {
        var row = PlayerCurrentRow();
        row["baseOverride"] = Override(true, true, baseHealth);
        row["actorValueInfo"]![3]!["flags"] = flags;
        SetPlayerSelector(row, 0, 1234);
        SetPlayerSelector(row, 1, selector1);
        SetPlayerSelector(row, 2, -5678);
        var value = Assert.Single(Evaluate(row), item => item.Key == "Health.permanent");
        Assert.Equal(expected is null ? "Unavailable" : "Calculated", value.Status);
        Assert.Equal(expected, value.Value);
    }

    // Expected bits execute the retained 00646D80 body under CW 007F (body SHA256 33f0555a...).
    [Theory]
    [InlineData(false, 0x41100000u, 0x40400000u, 0x3FC00000u, 0x41580000u)]
    [InlineData(true, 0x41100000u, 0x40400000u, 0x3FC00000u, 0x40900000u)]
    [InlineData(true, 0x41100000u, 0x00000000u, 0x40000000u, 0x41900000u)]
    [InlineData(true, 0x41100000u, 0x80000000u, 0x40000000u, 0x41900000u)]
    [InlineData(true, 0x41100000u, 0xC0400000u, 0x40000000u, 0xC0C00000u)]
    [InlineData(true, 0x41100000u, 0x40400000u, 0xC0000000u, 0x40400000u)]
    [InlineData(true, 0x41100000u, 0x40400000u, 0x00000000u, 0x00000000u)]
    [InlineData(true, 0x41100000u, 0x40400000u, 0x80000000u, 0x80000000u)]
    [InlineData(true, 0x3F800000u, 0x40E00000u, 0x40400000u, 0x3EDB6DB8u)]
    [InlineData(true, 0xC0E00000u, 0x40400000u, 0x3F000000u, 0xBF955555u)]
    [InlineData(true, 0x7F7FFFFFu, 0x40000000u, 0x3F800000u, 0x7EFFFFFFu)]
    [InlineData(true, 0x7F7FFFFFu, 0x3F000000u, 0x3F000000u, null)]
    [InlineData(true, 0x00800000u, 0x40000000u, 0x40000000u, null)]
    [InlineData(true, 0x00800000u, 0x7F7FFFFFu, 0x7F7FFFFFu, null)]
    public void WeaponCriticalStageMatchesRetainedInstructionVectors(bool automatic,
        uint currentBits, uint rateBits, uint multiplierBits, uint? expectedBits)
    {
        using var json = JsonDocument.Parse(Row(true).ToJsonString());
        var state = ActorValueObservationReader.Read(json.RootElement) with
        {
            WeaponCritical = new(0x12350000, 0x123500A4, BitConverter.UInt32BitsToSingle(currentBits),
                true, 0xE3778, 0x12360000, (byte)(automatic ? 2 : 0),
                BitConverter.UInt32BitsToSingle(rateBits), BitConverter.UInt32BitsToSingle(multiplierBits))
        };
        var value = Assert.Single(ActorValueCalculation.Evaluate(ActorEngineProfile.PcRetail, state),
            item => item.Key == "CritChance.weaponStage");
        Assert.Equal(expectedBits is null ? "Unavailable" : "Calculated", value.Status);
        if (expectedBits is { } expected)
        {
            Assert.NotNull(value.Value);
            Assert.Equal(expected, BitConverter.SingleToUInt32Bits((float)value.Value.Value));
            Assert.Equal("Reconstruction", value.CalculationBasis);
        }
        else Assert.Null(value.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WeaponCriticalInputsRemainSeparateFromTheObservedReturn(bool present)
    {
        var row = WeaponCriticalRow(present);
        row["weaponCriticalStage"]!["stageValue"] = 999f;
        row["weaponCriticalStage"]!["stageValueBits"] = BitConverter.SingleToUInt32Bits(999f);
        using var json = JsonDocument.Parse(row.ToJsonString());
        var state = ActorValueObservationReader.Read(json.RootElement);
        var value = Assert.Single(ActorValueCalculation.Evaluate(ActorEngineProfile.PcRetail, state),
            item => item.Key == "CritChance.weaponStage");
        Assert.Equal("Calculated", value.Status);
        Assert.Equal(9d, value.Value);
        Assert.Equal(999f, ActorValueObservationReader.ReadWeaponCriticalResult(json.RootElement));
        Assert.DoesNotContain(value.Inputs, input => input.Key.Contains("stageValue", StringComparison.Ordinal));
        if (!present) Assert.DoesNotContain(value.Inputs, input => input.Key == "Weapon.formId");
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("wrong-profile")]
    [InlineData("foreign-owner")]
    [InlineData("foreign-base")]
    [InlineData("unknown-getter")]
    [InlineData("changed-code")]
    [InlineData("duplicate-helper")]
    [InlineData("changed-identity")]
    [InlineData("torn-actor-value")]
    [InlineData("torn-weapon")]
    [InlineData("raw-mismatch")]
    [InlineData("missing-automatic-rate")]
    [InlineData("missing-multiplier")]
    [InlineData("unknown-weapon-type")]
    [InlineData("overflow-pointer")]
    [InlineData("missing-epoch")]
    [InlineData("absent-weapon-with-fields")]
    public void WeaponCriticalReaderRejectsUnavailableOrConflictingEvidence(string control)
    {
        var row = WeaponCriticalRow();
        var stage = row["weaponCriticalStage"]!;
        switch (control)
        {
            case "unavailable": stage["status"] = "unavailable"; break;
            case "wrong-profile": stage["executableSha256"] = new string('f', 64); break;
            case "foreign-owner": stage["actorAddress"] = 0x12370000; break;
            case "foreign-base": stage["baseFormId"] = 8; break;
            case "unknown-getter": stage["currentGetter"] = 0x0093ACB1; break;
            case "changed-code": stage["routineHex"] = "00"; break;
            case "duplicate-helper": stage["helperEvidence"]![4] = stage["helperEvidence"]![0]!.DeepClone(); break;
            case "changed-identity": stage["identityStable"] = false; break;
            case "torn-actor-value":
                stage["currentCriticalChanceAfter"] = 10f;
                stage["currentCriticalChanceAfterBits"] = BitConverter.SingleToUInt32Bits(10f);
                break;
            case "torn-weapon": stage["weaponFieldsAfterHex"] = "0402000000000000803f"; break;
            case "raw-mismatch": stage["criticalMultiplier"] = 2f; break;
            case "missing-automatic-rate":
                stage["weaponFlags"] = 2; stage["weaponFlagsHex"] = "02";
                stage["weaponFieldsBeforeHex"] = "0402000000000000803f";
                stage["weaponFieldsAfterHex"] = "0402000000000000803f";
                stage["fireRate"] = null;
                break;
            case "missing-multiplier": stage["criticalMultiplier"] = null; break;
            case "unknown-weapon-type": stage["weaponFormType"] = 0x29; break;
            case "overflow-pointer": stage["weaponAddress"] = uint.MaxValue; break;
            case "missing-epoch": stage["loadEpoch"] = null; break;
            case "absent-weapon-with-fields": stage["weaponPresent"] = false; break;
        }
        using var json = JsonDocument.Parse(row.ToJsonString());
        var state = ActorValueObservationReader.Read(json.RootElement);
        Assert.Null(state.WeaponCritical);
        Assert.Equal("Unavailable", Assert.Single(ActorValueCalculation.Evaluate(ActorEngineProfile.PcRetail, state),
            item => item.Key == "CritChance.weaponStage").Status);
    }

    private static JsonObject WeaponCriticalRow(bool present = true)
    {
        var row = PlayerCurrentRow();
        // Native wire example with relocated fixture addresses; code bytes are retained engine evidence.
        var stage = (JsonObject)JsonNode.Parse("""
            {
              "schemaVersion": 1,
              "status": "observed",
              "scope": "equipped-weapon-pre-modifier",
              "reason": null,
              "executableSha256": "3a87f92f011e5dc9179ddf733cf08be2b39ea6e5b7a8a9e3a9a72dafcc1b104d",
              "actorAddress": 305463296,
              "baseAddress": 305397760,
              "engineTargetFormId": 20,
              "baseFormId": 7,
              "actorFormType": 59,
              "baseFormType": 42,
              "actorValueOwnerAddress": 305463460,
              "actorValueOwnerVtable": 17344884,
              "currentGetterSlot": 17344896,
              "currentGetter": 9678000,
              "currentGetterHex": "558bec83ec20894dfc8b4508508b4dfc8b55fc8b028b5004ffd28b4508506a008b4dfc81e9a4000000dd5df4e8ef160100dc45f48b4d08516a018b4dfc81e9a4000000dd5dece8d5160100dc45ec8b5508526a028b4dfc81e9a4000000dd5de4e8bb160100dc45e4d95de0d945e08be55dc20400",
              "currentCriticalChance": 9,
              "currentCriticalChanceBeforeBits": 1091567616,
              "currentCriticalChanceAfter": 9,
              "currentCriticalChanceAfterBits": 1091567616,
              "weaponPresent": true,
              "weaponAddress": 305528832,
              "weaponFormId": 931704,
              "weaponFormType": 40,
              "weaponFlags": 0,
              "weaponFlagsHex": "00",
              "fireRate": 0,
              "fireRateHex": "00000000",
              "criticalMultiplier": 1,
              "criticalMultiplierHex": "0000803f",
              "weaponFieldsBeforeHex": "0400000000000000803f",
              "weaponFieldsAfterHex": "0400000000000000803f",
              "x87ControlWordBefore": 127,
              "x87ControlWordAfter": 127,
              "routineAddress": 6581632,
              "routineHex": "558bec83ec1c837d0c00740d8b4d0ce8fcf5dfff8945e8eb07c745e8ffffffff8b45e88945ecd9e8d95df4c645ff00837d0c00741a8b4d0ce883ddedff0fb6c885c9740b8b4d0ce874a81d00d95df4d945f4dc1d60200101dfe0f6c4447a05d9e8d95df4837d080074146a0e8b55088b028b4d088b500cffd2d95de4eb05d9eed95de4d945e4d95df0d945f0d875f4d95df8837d0c0074238b4d0ce890b02800dc1d60200101dfe0f6c401750e8b4d0ce87bb02800d84df8d95df8d945f88be55dc3",
              "helperEvidence": [
                {
                  "address": 4481936,
                  "hex": "558bec51894dfc8b45fc0fbe80f40000008be55dc3"
                },
                {
                  "address": 5393216,
                  "hex": "558bec51894dfc8b45fc0fb6880001000083e1020f95c08be55dc3"
                },
                {
                  "address": 8525376,
                  "hex": "558bec51894dfc8b45fcd980340100008be55dc3"
                },
                {
                  "address": 9248432,
                  "hex": "558bec51894dfc8b45fcd980c40100008be55dc3"
                },
                {
                  "address": 16851040,
                  "hex": "0000000000000000"
                }
              ],
              "stageValue": 9,
              "stageValueBits": 1091567616,
              "identityStable": true,
              "readConsistency": "bracketed-equal",
              "equippedWeaponRoute": "sdk-explicit-owner-GetEquippedObject-slot5",
              "captureGeneration": 1,
              "connectionGeneration": 1,
              "loadEpoch": 1
            }
            """)!;
        if (!present)
        {
            stage["weaponPresent"] = false;
            foreach (var field in new[] { "weaponAddress", "weaponFormId", "weaponFormType", "weaponFlags", "weaponFlagsHex",
                         "fireRate", "fireRateHex", "criticalMultiplier", "criticalMultiplierHex",
                         "weaponFieldsBeforeHex", "weaponFieldsAfterHex" })
                stage[field] = null;
        }
        row["weaponCriticalStage"] = stage;
        return row;
    }

    private static JsonObject PlayerCurrentRow()
    {
        // Retained PC028 lookup/wrapper bytes with relocated synthetic actor/base addresses.
        var row = Row(true);
        row["baseOverride"] = Override(true, false, 0);
        row["modifierSelectors"] = new JsonObject
        {
            ["status"] = "observed", ["resolverRoute"] = "player-direct-loaded-lookup",
            ["semanticStatus"] = "raw-selectors", ["readConsistency"] = "repeated-field-and-identity-reads",
            ["evidence"] = "pc007-player-av-wrappers-and-pc025-loaded-modifier-lookup", ["identityStable"] = true,
            ["executableSha256"] = ActorEngineProfile.PcRetail.ExecutableSha256,
            ["actorAddress"] = 0x12350000, ["baseAddress"] = 0x12340000,
            ["actorValueOwnerAddress"] = 0x123500A4, ["ownerVtable"] = 0x0108A974,
            ["lookupAddress"] = 0x0094C3D0,
            ["lookupObservedHex"] = "558bec83ec10894df8d9eed95dfc8b45088945f4837df400743c837df4017448837df4027402eb508b4d0c894df0837df0107402eb0e8b55f8d982ac040000d95dfceb108b450c8b4df8d98481b0040000d95dfceb228b550c8b45f8d9849044020000d95dfceb108b4d0c8b55f8d9848a78030000d95dfcd945fc8be55dc20800",
            ["wrappers"] = new JsonArray(
                PlayerWrapper(4, 0, 0x0094C460, "558bec51894dfc8b4508506a008b4dfc81e9a4000000e855ffffff8be55dc20400"),
                PlayerWrapper(5, 2, 0x0094C490, "558bec51894dfc8b4508506a028b4dfc81e9a4000000e825ffffff8be55dc20400"),
                PlayerWrapper(6, 1, 0x0094C4C0, "558bec51894dfc8b4508506a018b4dfc81e9a4000000e8f5feffff8be55dc20400")),
            ["observations"] = new JsonArray(new JsonObject(), new JsonObject(), new JsonObject())
        };
        for (var selector = 0; selector < 3; selector++) SetPlayerSelector(row, selector, 0);
        return row;
    }

    private static JsonObject PlayerWrapper(int slot, int selector, uint address, string code) => new()
    { ["slot"] = slot, ["selector"] = selector, ["address"] = address, ["observedHex"] = code };

    private static void SetPlayerSelector(JsonObject row, int selector, float value)
    {
        byte[] raw = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, BitConverter.SingleToUInt32Bits(value));
        var offset = selector switch { 0 => 0x284, 1 => 0x3B8, _ => 0x4AC };
        row["modifierSelectors"]!["observations"]![selector] = new JsonObject
        {
            ["selector"] = selector, ["actorValueCode"] = 16, ["status"] = "observed",
            ["value"] = value, ["address"] = 0x12350000 + offset,
            ["rawHex"] = Convert.ToHexString(raw), ["repeatRawHex"] = Convert.ToHexString(raw)
        };
    }

    [Theory]
    [InlineData(137u, 2500, 1, 0, 0, 2, 274d)]
    [InlineData(137u, 1300, 10, 0, 0, 13, 1781d)]
    [InlineData(137u, 2500, 1, 4, 0, 4, 548d)]
    [InlineData(137u, 2500, 1, 0, 1, 1, 137d)]
    [InlineData(137u, 2500, 1, 4, 3, 4, 548d)]
    [InlineData(137u, 0, 1, 0, 0, 0, 137d)]
    [InlineData(137u, 1000, 1, 32768, 0, 32768, 137d)]
    [InlineData(40000u, 2500, 1, 0, 0, 2, 14464d)]
    [InlineData(4294967295u, 65535, 65535, 0, 0, 34996, 65535d)]
    [InlineData(137u, 1000, 0, 0, 0, 0, 137d)]
    [InlineData(137u, 2000, 32769, 0, 0, 2, 274d)]
    public void ScaledCreatureHealthUsesStoredPlayerLevelSignedFloorAndLowWord(uint stored, int rawLevel,
        int playerLevel, int minimum, int maximum, int effective, double expected)
    {
        // First nine inputs/results execute the nine retained original bodies in creature-vectors.json.
        // The last two discriminate a present player with stored level0 and level truncation to low16.
        var row = ScaledCreatureRow(stored, (ushort)rawLevel, (ushort)playerLevel, (ushort)minimum, (ushort)maximum);
        row["statistics"] = new JsonArray(Statistic("Level", "current", 999),
            Statistic("Health", "base", 888), Statistic("Health", "permanent", 777));
        var health = Assert.Single(Evaluate(row), value => value.Key == "Health.base");
        Assert.Equal("Calculated", health.Status);
        Assert.Equal(expected, health.Value);
        Assert.Equal("Reconstruction", health.CalculationBasis);
        Assert.Contains(health.Inputs, item => item.Key == "Level.effectiveUInt16" && item.Value == effective);
        Assert.Contains(health.Inputs, item => item.Key == "Player.levelStored" && item.Value == playerLevel);
        Assert.Contains(health.Inputs, item => item.Key == "Health.stored" && item.Value == stored);
        Assert.DoesNotContain(health.Inputs, item => item.Key is "Level.current" or "Health.base" or "Health.permanent");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("retired")]
    [InlineData("schema")]
    [InlineData("profile")]
    [InlineData("identity")]
    [InlineData("child-owner")]
    [InlineData("child-repeat")]
    [InlineData("child-level")]
    [InlineData("child-health")]
    [InlineData("component-slot")]
    [InlineData("player-absent")]
    [InlineData("player-owner")]
    [InlineData("player-repeat")]
    [InlineData("player-malformed")]
    [InlineData("player-raw-id")]
    [InlineData("player-raw-type")]
    [InlineData("player-level")]
    [InlineData("player-overflow")]
    [InlineData("code")]
    [InlineData("duplicate-code")]
    [InlineData("divisor")]
    [InlineData("inheritance-without-proof")]
    public void ScaledCreatureRejectsUnownedChangedOrIncompleteInput(string control)
    {
        var row = ScaledCreatureRow();
        var scaling = row["creatureHealthScaling"]!;
        switch (control)
        {
            case "missing": row["creatureHealthScaling"] = null; break;
            case "retired": scaling["loadEpoch"] = 0; break;
            case "schema": scaling["schemaVersion"] = 2; break;
            case "profile": scaling["executableSha256"] = new string('0', 64); break;
            case "identity": scaling["identityStable"] = false; break;
            case "child-owner": scaling["actorAddress"] = 0x12350004; break;
            case "child-repeat": scaling["baseRawAfterHex"] = new string('0', 392); break;
            case "child-level": scaling["levelEncodedUnsigned"] = 2501; break;
            case "child-health": scaling["storedHealth"] = 138; break;
            case "component-slot": scaling["healthGetterSlot"] = 0x01048E78; break;
            case "player-absent": scaling["playerPresent"] = false; break;
            case "player-owner": scaling["playerAddress"] = 0x12350000; break;
            case "player-repeat": scaling["playerBaseRawAfterHex"] = new string('0', 392); break;
            case "player-malformed":
                scaling["playerBaseRawBeforeHex"] = new string('z', 392);
                scaling["playerBaseRawAfterHex"] = new string('z', 392);
                break;
            case "player-raw-id": ChangePlayerScalingRaw(scaling, 0xC, 8); break;
            case "player-raw-type": ChangePlayerScalingRaw(scaling, 4, 0x2B); break;
            case "player-level": scaling["playerStoredLevel"] = 2; break;
            case "player-overflow": scaling["playerBaseAddress"] = uint.MaxValue - 4; break;
            case "code": scaling["codeEvidence"]![5]!["hex"] = "90"; break;
            case "duplicate-code": scaling["codeEvidence"]![5] = scaling["codeEvidence"]![0]!.DeepClone(); break;
            case "divisor": scaling["levelDivisorHex"] = "0000000000005940"; break;
            case "inheritance-without-proof":
                row["creatureHealthScaling"] = null;
                row["baseActorData"]!["templateFlags"] = 2;
                SetCreatureRaw(row, 0x4A, 2);
                scaling["templateFlags"] = 2;
                scaling["baseRawBeforeHex"] = row["baseActorData"]!["rawHex"]!.GetValue<string>();
                scaling["baseRawAfterHex"] = row["baseActorData"]!["rawHex"]!.GetValue<string>();
                break;
        }
        var health = Assert.Single(Evaluate(row), value => value.Key == "Health.base");
        Assert.Equal("Unavailable", health.Status);
        Assert.Null(health.Value);
    }

    [Theory]
    [InlineData(false, 137d)]
    [InlineData(true, 0d)]
    public void ScalingProofIsNotRequiredByIndependentFixedOrExplicitOverrideBranches(bool presentOverride, double expected)
    {
        var row = ScaledCreatureRow();
        row["creatureHealthScaling"] = null;
        if (presentOverride)
        {
            row["baseOverride"]!["hasOverride"] = true;
            row["baseOverride"]!["value"] = 0;
        }
        else
        {
            row["baseActorData"]!["flags"] = 0x40;
            SetCreatureRaw(row, 0x34, 0x40);
        }
        var health = Assert.Single(Evaluate(row), value => value.Key == "Health.base");
        Assert.Equal("Calculated", health.Status);
        Assert.Equal(expected, health.Value);
    }

    [Theory]
    [InlineData(false, true, 137d)]
    [InlineData(false, false, null)]
    [InlineData(true, true, 274d)]
    [InlineData(true, false, null)]
    public void CreatureUseStatsUsesEffectiveLoadedComponentOnlyWithRepeatedProof(bool scaled, bool proof, double? expected)
    {
        // PC053 template-on: authored53/3/2, loaded component137/7/4, SDK base137.
        // The scaled variant applies the retained getter to loaded137 and raw2500/player1.
        var row = ScaledCreatureRow(level: scaled ? (ushort)2500 : (ushort)7);
        var data = row["baseActorData"]!;
        var context = row["creatureHealthScaling"]!;
        var flags = scaled ? 0xC0u : 0x40u;
        data["flags"] = flags;
        data["templateFlags"] = 0x8002;
        var raw = Convert.FromHexString(data["rawHex"]!.GetValue<string>());
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x34), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x4A), 0x8002);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x54), 0x12380000);
        data["rawHex"] = Convert.ToHexString(raw);
        context["flags"] = flags;
        context["templateFlags"] = 0x8002;
        context["templatePointer"] = 0x12380000;
        context["baseRawBeforeHex"] = Convert.ToHexString(raw);
        context["baseRawAfterHex"] = Convert.ToHexString(raw);
        if (!proof) row["creatureHealthScaling"] = null;
        row["statistics"] = new JsonArray(Statistic("Health", "base", 999), Statistic("Level", "current", 888));
        var result = Assert.Single(Evaluate(row), value => value.Key == "Health.base");
        Assert.Equal(expected, result.Value);
        if (expected is null)
        {
            Assert.Equal("Unavailable", result.Status);
            Assert.Contains(result.MissingDependencies, dependency => dependency.Contains("repeated effective child component inputs for UseStats", StringComparison.Ordinal));
            return;
        }
        Assert.Equal("Calculated", result.Status);
        Assert.Equal("Reconstruction", result.CalculationBasis);
        Assert.Contains("observed effective child component", result.Evidence);
        Assert.Contains(result.Inputs, item => item.Key == "Health.stored" && item.Value == 137 && item.Provenance == "Observed");
        Assert.Contains(result.Inputs, item => item.Key == "ACBS.templateFlags" && item.Value == 0x8002);
        Assert.DoesNotContain(result.Inputs, item => item.Key is "Health.base" or "Level.current");
    }

    private static JsonObject CreatureRow()
    {
        // PC051 health.ndjson line1018/sequence1013: retained raw base and owner tables.
        // Other common fields reuse the synthetic attribution fixture; this is not a full trace replay.
        var row = Row(false);
        row["engineTargetFormId"] = 0x0A000810;
        row["engineTargetBaseFormId"] = 0x0A000800;
        row["engineTargetFormType"] = 0x3C;
        row["isCreature"] = true;
        var data = row["baseActorData"]!;
        data["formId"] = 0x0A000800;
        data["formType"] = 0x2B;
        data["flags"] = 0x40;
        data["storedHealth"] = 137u;
        data["rawHex"] = "5c8f04012b000000080000000008000a1450551800000000306a000eb0340a4c00000000000000000000000000000000ec8e0401400000003200000007000000000064000000000023000000000000002459091400000000000000000000000000000000d88e04010000000000000000c48e040100000000ff000000a48e040100000000000000000000000000000000848e040100040a640000000000000000000000000000000000000000000000006c8e040189000000588e04010204040102040100";
        var observation = Override(false, false, 0);
        observation["engineTargetFormId"] = 0x0A000810;
        observation["engineTargetBaseFormId"] = 0x0A000800;
        observation["engineTargetFormType"] = 0x3C;
        observation["engineTargetBaseFormType"] = 0x2B;
        observation["actorVtable"] = 0x010870AC;
        row["baseOverride"] = observation;
        return row;
    }

    private static JsonObject ScaledCreatureRow(uint stored = 137, ushort level = 2500, ushort playerLevel = 1,
        ushort minimum = 0, ushort maximum = 0)
    {
        var row = CreatureRow();
        var data = row["baseActorData"]!;
        data["flags"] = 0xC0;
        data["storedHealth"] = stored;
        var raw = Convert.FromHexString(data["rawHex"]!.GetValue<string>());
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x34), 0xC0);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x3C), level);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x3E), minimum);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x40), maximum);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0xB4), stored);
        data["rawHex"] = Convert.ToHexString(raw);
        // Independent synthetic player identity and raw stored-level field; SDK Level is not an operand.
        var playerRaw = new byte[196];
        BinaryPrimitives.WriteUInt32LittleEndian(playerRaw, 0x01047A6C);
        playerRaw[4] = 0x2A;
        BinaryPrimitives.WriteUInt32LittleEndian(playerRaw.AsSpan(0xC), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(playerRaw.AsSpan(0x3C), playerLevel);
        row["creatureHealthScaling"] = new JsonObject
        {
            ["schemaVersion"] = 1, ["status"] = "observed", ["reason"] = null,
            ["scope"] = "creature-health-scaled-inputs", ["readConsistency"] = "repeated-owner-fields",
            ["inheritanceStatus"] = "not-evaluated", ["identityStable"] = true, ["playerPresent"] = true,
            ["noEngineCall"] = true, ["executableSha256"] = ActorEngineProfile.PcRetail.ExecutableSha256,
            ["captureGeneration"] = 1, ["connectionGeneration"] = 2, ["loadEpoch"] = 3,
            ["actorAddress"] = 0x12350000, ["engineTargetFormId"] = 0x0A000810,
            ["baseAddress"] = 0x12340000, ["baseFormId"] = 0x0A000800,
            ["actorFormType"] = 0x3C, ["baseFormType"] = 0x2B, ["actorVtable"] = 0x010870AC,
            ["baseVtable"] = 0x01048F5C, ["baseActorValueOwnerAddress"] = 0x12340100,
            ["baseActorValueOwnerVtable"] = 0x01048DC8, ["healthComponentAddress"] = 0x123400B0,
            ["healthComponentVtable"] = 0x01048E6C, ["healthGetterSlot"] = 0x01048E7C,
            ["healthGetter"] = 0x005F8E90, ["baseRawBeforeHex"] = Convert.ToHexString(raw),
            ["baseRawAfterHex"] = Convert.ToHexString(raw), ["playerSingletonAddress"] = 0x011DEA3C,
            ["playerAddress"] = 0x12360000, ["playerBaseAddress"] = 0x12370000,
            ["playerFormId"] = 0x14, ["playerFormType"] = 0x3B,
            ["playerBaseFormId"] = 7, ["playerBaseFormType"] = 0x2A,
            ["playerBaseRawBeforeHex"] = Convert.ToHexString(playerRaw),
            ["playerBaseRawAfterHex"] = Convert.ToHexString(playerRaw), ["playerStoredLevel"] = playerLevel,
            ["flags"] = 0xC0, ["levelEncodedUnsigned"] = level, ["minimumLevel"] = minimum,
            ["maximumLevel"] = maximum, ["templateFlags"] = 0,
            ["templatePointer"] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x54)),
            ["storedHealth"] = stored, ["levelDivisorAddress"] = 0x01017B70,
            ["levelDivisorHex"] = "0000000000408f40", ["codeEvidence"] = new JsonArray
            {
            new JsonObject { ["address"] = 0x00461560, ["hex"] = "558bec51894dfc68800000008b4dfce80c0000008be55dc3" },
            new JsonObject { ["address"] = 0x00461580, ["hex"] = "558bec51894dfc8b45fc8b4804234d080f95c08be55dc20400" },
            new JsonObject { ["address"] = 0x0047D370, ["hex"] = "558bec51894dfc8b45fc668b400c8be55dc3" },
            new JsonObject { ["address"] = 0x0047D390, ["hex"] = "558bec51894dfc8b45fc668b400e8be55dc3" },
            new JsonObject { ["address"] = 0x0047D3B0, ["hex"] = "558bec51894dfc8b45fc668b40108be55dc3" },
            new JsonObject { ["address"] = 0x0047DED0, ["hex"] = "558bec83ec2c56894dec8b4dece88ef4ffff668945fc8b4dece87236feff0fb6c085c00f84de0000000fb74dfc894de8db45e8dc35707b0101d95df8c745f4000000008b0d3cea1d01e8121533008945f0837df000740b8b55f083c2308955e4eb07c745e4000000008b45e48945f4837df40074348b4df4e823f4ffff0fb7c8894de0db45e0d84df8d97dde0fb745de0d000c00008945d8d96dd8db5dd4d96dde668b55d4668955fc8b4dece80ff4ffff0fb7c085c07e210fb775fc8b4dece8fcf3ffff0fb7c83bf17d0e8b4dece8edf3ffff668945fceb2e8b4dece8fff3ffff0fb7d085d27e1f0fb775fc8b4dece8ecf3ffff0fb7c03bf07e0c8b4dece8ddf3ffff668945fc668b45fc5e8be55dc3" },
            new JsonObject { ["address"] = 0x005F8E90, ["hex"] = "558bec83ec0c56894df433c0668945fc8b4df481e980000000e8b286e6ff0fb6c885c974398b4df481e980000000e80d50e8ff668945f80fbf55f883fa017d09b801000000668945f80fbf75f88b4df4e88bd112000faff0668975fceb0c8b4df4e87ad11200668945fc0fb745fc5e8be55dc3" },
            new JsonObject { ["address"] = 0x00726070, ["hex"] = "558bec51894dfc8b45fc8b40048be55dc3" },
            new JsonObject { ["address"] = 0x007AF430, ["hex"] = "558bec51894dfc8b45fc8b40208be55dc3" },
            }
        };
        return row;
    }

    private static void ChangePlayerScalingRaw(JsonNode scaling, int offset, uint value)
    {
        var raw = Convert.FromHexString(scaling["playerBaseRawBeforeHex"]!.GetValue<string>());
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset), value);
        scaling["playerBaseRawBeforeHex"] = Convert.ToHexString(raw);
        scaling["playerBaseRawAfterHex"] = Convert.ToHexString(raw);
    }

    private static void SetCreatureRaw(JsonObject row, int offset, uint value)
    {
        var raw = Convert.FromHexString(row["baseActorData"]!["rawHex"]!.GetValue<string>());
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset), value);
        row["baseActorData"]!["rawHex"] = Convert.ToHexString(raw);
    }

    private static JsonObject AutoCalcRow()
    {
        var row = Row(false);
        row["baseActorData"]!["flags"] = 0x10;
        row["baseActorData"]!["storedEndurance"] = 4;
        row["statistics"]![0]!["value"] = 7;
        return row;
    }

    private static IReadOnlyList<ActorStatisticValue> Evaluate(JsonObject row)
    {
        using var json = JsonDocument.Parse(row.ToJsonString());
        return ActorValueCalculation.Evaluate(ActorEngineProfile.PcRetail, ActorValueObservationReader.Read(json.RootElement));
    }

    private static JsonObject Row(bool player) => new()
    {
        ["kind"] = "actor-state", ["protocol"] = 1, ["sequence"] = 3, ["dropped"] = 0, ["targetKind"] = "actor",
        ["engineTargetFormId"] = player ? 0x14 : 0x104C0F, ["engineTargetBaseFormId"] = player ? 7 : 0x104C0C,
        ["engineTargetFormType"] = 0x3B, ["executableSha256"] = ActorEngineProfile.PcRetail.ExecutableSha256,
        ["isPlayer"] = player, ["isCreature"] = false,
        ["floatingPoint"] = new JsonObject { ["x87ControlWord"] = 127, ["evidence"] = "fnstcw", ["scope"] = "game-thread-before-actor-observation" },
        ["baseActorData"] = new JsonObject { ["status"] = "observed", ["formId"] = player ? 7 : 0x104C0C,
            ["formType"] = 0x2A, ["address"] = 0x12340000, ["flags"] = 0, ["templateFlags"] = 0,
            ["storedHealth"] = player ? 100 : 50 },
        ["baseOverride"] = new JsonObject { ["status"] = "unavailable", ["reason"] = "uncalibrated-virtual-getter" },
        ["statistics"] = new JsonArray(Statistic("Level", "current", 1), Statistic("Endurance", "permanent", 4),
            Statistic("Luck", "current", player ? 9 : 4)),
        ["actorValueInfo"] = new JsonArray(Info("Endurance", 7, 0x8009, 0), Info("Luck", 11, 0x8009, 0),
            Info("CritChance", 14, 0x800, 0x643B10), Info("Health", 16, 0x10C1, 0x643670)),
        ["gameSettings"] = new JsonArray(Setting("fAVDHealthEnduranceOffset", 0), Setting("fAVDHealthEnduranceMult", 20),
            Setting("fAVDNPCHealthEnduranceOffset", -1), Setting("fAVDNPCHealthEnduranceMult", 5),
            Setting("fAVDHealthLevelMult", 5), Setting("fAVDNPCHealthLevelMult", 5),
            Setting("fAVDCritLuckBase", 0), Setting("fAVDCritLuckMult", 1))
    };

    private static JsonObject Info(string name, int code, uint flags, uint callback) => new()
    { ["name"] = name, ["code"] = code, ["status"] = "observed", ["flags"] = flags, ["baseCallback"] = callback };
    private static JsonObject Statistic(string name, string component, float value) => new()
    { ["statistic"] = name, ["component"] = component, ["status"] = "observed", ["value"] = value };
    private static JsonObject Setting(string name, float value) => new()
    { ["name"] = name, ["status"] = "observed", ["value"] = value };
    private static JsonObject Override(bool player, bool present, float value) => new()
    {
        ["status"] = "observed", ["statistic"] = "Health", ["hasOverride"] = present, ["value"] = value,
        ["engineTargetFormId"] = player ? 0x14 : 0x104C0F, ["engineTargetBaseFormId"] = player ? 7 : 0x104C0C,
        ["actorAddress"] = 0x12350000, ["baseAddress"] = 0x12340000,
        ["getterAddress"] = player ? 0x94C640 : 0x880660, ["vtableOffset"] = 0x48C,
        ["evidence"] = "pc012-loaded-getter;game-thread-call;identity-rechecked"
    };
}
