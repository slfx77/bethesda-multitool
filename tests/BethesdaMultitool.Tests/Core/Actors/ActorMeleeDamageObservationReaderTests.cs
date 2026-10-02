using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Actors;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Actors;

public sealed class ActorMeleeDamageObservationReaderTests
{
    [Theory]
    [InlineData("valid", true, true)]
    [InlineData("generated-reference", true, true)]
    [InlineData("missing-input", false, true)]
    [InlineData("scalar-bits", false, true)]
    [InlineData("scalar-address", false, true)]
    [InlineData("frame-argument", false, true)]
    [InlineData("argument-type", false, true)]
    [InlineData("caller-frame", false, true)]
    [InlineData("weapon-getter", false, true)]
    [InlineData("weapon-type", false, true)]
    [InlineData("inputs-changed", false, true)]
    [InlineData("precision", false, true)]
    [InlineData("constants", false, true)]
    [InlineData("limb-bytes", false, true)]
    [InlineData("av17-bytes", false, true)]
    [InlineData("additive-local", false, true)]
    [InlineData("route", false, false)]
    [InlineData("profile", false, false)]
    [InlineData("code-proof", false, false)]
    [InlineData("capture-generation", false, false)]
    [InlineData("connection-generation", false, false)]
    [InlineData("load-epoch", false, false)]
    [InlineData("frame", false, false)]
    [InlineData("attacker", false, false)]
    [InlineData("base-kind", false, false)]
    [InlineData("invocation", false, false)]
    [InlineData("hit-participant", true, false)]
    [InlineData("hit-flags", true, false)]
    [InlineData("null-weapon", false, true)]
    public void KeepsBoundObservedHitSeparateFromCompleteCalculationInputs(string control, bool calculated, bool hitObserved)
    {
        var row = Row();
        var stage = row["damageStage"]!;
        switch (control)
        {
            case "missing-input": stage["inputs"]!.AsObject().Remove("fDamageSkillBase"); break;
            case "generated-reference": row["engineTargetFormId"] = 0xFF000001u; break;
            case "scalar-bits": stage["inputs"]!["attackMultiplier"]!["rawUInt32"] = 0; break;
            case "scalar-address": stage["inputs"]!["fDamageSkillBase"]!["sourceAddress"] = 9; break;
            case "frame-argument": EditRaw(stage, "operandFrameRawHex", 0x88, BitConverter.SingleToUInt32Bits(2)); break;
            case "argument-type": stage["arguments"]![0] = "invalid"; break;
            case "caller-frame": stage["callerFrameAddress"] = 1; break;
            case "weapon-getter": EditRaw(stage, "weaponRawHex", 0xA0, 11); break;
            case "weapon-type": EditRaw(stage, "weaponRawHex", 0xF4, 2); break;
            case "inputs-changed": stage["inputsRepeated"] = false; break;
            case "precision": stage["x87ReturnControlWord"] = 0x027F; break;
            case "constants": stage["constantsRepeated"] = false; break;
            case "limb-bytes": stage["inputs"]!["rightArmFactor"]!["rawHex"] = "000000000000e03f"; break;
            case "av17-bytes": stage["inputs"]!["meleeDamageActorValue"]!["rawHex"] = "01000000000000c00040"; break;
            case "additive-local": EditRaw(stage, "operandFrameRawHex", 0x4C, 0); break;
            case "route": stage["route"] = "unsupported"; break;
            case "profile": stage["version"] = 2; break;
            case "code-proof": stage["codeProfileMatched"] = false; break;
            case "capture-generation": row["captureGeneration"] = 2; break;
            case "connection-generation": row["connectionGeneration"] = 2; break;
            case "load-epoch": row["loadEpoch"] = 0; break;
            case "frame": row["entryFrame"] = 9; break;
            case "attacker": row["engineTargetFormId"] = 21; break;
            case "base-kind": row["engineTargetBaseFormType"] = 0x2B; break;
            case "invocation": row["stageInvocationId"] = 2; break;
            case "hit-participant": EditRaw(stage, "hitReturnRawHex", 4, 0); break;
            case "hit-flags": stage["hitReturnFlags"]!["rawUInt32"] = 0; break;
            case "null-weapon":
                row["weaponAddress"] = 0; row["weaponFormId"] = 0; row["itemAddress"] = 0;
                EditRaw(stage, "hitReturnRawHex", 0x30, 0); stage["status"] = "partial"; stage["reason"] = "no-weapon";
                stage["inputs"] = null; stage["output"] = null; break;
        }
        using var json = JsonDocument.Parse(row.ToJsonString());
        var result = ActorMeleeDamageObservationReader.Read(json.RootElement,
            control == "generated-reference" ? 0xFF000001u : 20u, 7, "NPC_", 1, 1);
        Assert.Equal(calculated, result.Inputs is not null);
        Assert.Equal(hitObserved, result.HitHealthDamage.HasValue);
        if (calculated)
        {
            var prediction = ActorMeleeDamageCalculation.EvaluateStage(ActorEngineProfile.PcRetail, result.Inputs!);
            Assert.Equal(15d, prediction.Value);
            Assert.Equal(999f, result.StageResult);
            Assert.DoesNotContain(prediction.Inputs, input => input.Value == 999 || input.Key.Contains("return", StringComparison.OrdinalIgnoreCase));
        }
        if (hitObserved) { Assert.Equal(6f, result.HitHealthDamage); Assert.Equal(0x80u, result.HitFlags); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputRemainsIndependentAndDuplicateEvidenceIsRejected(bool duplicate)
    {
        var row = Row();
        row["damageStage"]!["output"]!["rawUInt32"] = 0;
        var text = row.ToJsonString();
        if (duplicate) text = text.Replace("\"codeProfileMatched\":true", "\"codeProfileMatched\":false,\"codeProfileMatched\":true", StringComparison.Ordinal);
        using var json = JsonDocument.Parse(text);
        var result = ActorMeleeDamageObservationReader.Read(json.RootElement, 20, 7, "NPC_", 1, 1);
        Assert.Null(result.StageResult);
        if (duplicate) { Assert.Null(result.Inputs); Assert.Null(result.HitHealthDamage); }
        else { Assert.NotNull(result.Inputs); Assert.Equal(15d, ActorMeleeDamageCalculation.EvaluateStage(ActorEngineProfile.PcRetail, result.Inputs).Value); }
    }

    internal static JsonObject Row()
    {
        const uint frame = 0x01000000, caller = 0x01000100, attacker = 0x02000000, target = 0x02001000,
            weapon = 0x03000000, item = 0x04000000, hit = 0x05000000;
        uint[] arguments = [attacker + 0xA4, weapon, Bits(1), Bits(1), 0, 0, item, 1];
        var raw = new byte[0x9C];
        U32(raw, 0, Bits(999)); U32(raw, 0x2C, 1); U32(raw, 0x38, 10); U32(raw, 0x44, 32);
        U32(raw, 0x4C, Bits(5)); U32(raw, 0x50, Bits(0)); U32(raw, 0x54, Bits(100));
        U32(raw, 0x6C, Bits(1)); U32(raw, 0x70, Bits(1)); U32(raw, 0x74, caller); U32(raw, 0x78, 0x004BDF76);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(0x24), BitConverter.DoubleToUInt64Bits(1));
        for (var i = 0; i < arguments.Length; i++) U32(raw, 0x7C + i * 4, arguments[i]);
        var weaponRaw = new byte[0x160]; weaponRaw[4] = 0x28; weaponRaw[0xF4] = 1;
        U32(weaponRaw, 12, 0x1000); U32(weaponRaw, 0xA0, 10); U32(weaponRaw, 0x15C, 32);
        var hitRaw = new byte[0x64]; U32(hitRaw, 0, attacker); U32(hitRaw, 4, target);
        U32(hitRaw, 0x14, Bits(6)); U32(hitRaw, 0x30, weapon); U32(hitRaw, 0x58, 0x80);
        var inputs = new JsonObject
        {
            ["weaponBaseDamage"] = Integer(10, weapon + 0xA0), ["governingSkill"] = Integer(32, weapon + 0x15C),
            ["skillValue"] = Scalar(100, frame - 0x20), ["rightArmFactor"] = new JsonObject
                { ["status"] = "observed", ["value"] = 1d, ["rawHex"] = "000000000000f03f" },
            ["meleeDamageActorValue"] = Scalar(3, 0), ["extraDataDamage"] = Scalar(2, frame - 0x28),
            ["conditionFraction"] = Scalar(1, frame + 0x10), ["attackMultiplier"] = Scalar(1, frame + 0x14),
            ["ammunitionDamage"] = Scalar(0, frame - 0x24), ["weaponModeMultiplier"] = Scalar(1, frame - 8),
            ["actorModeMultiplier"] = Scalar(1, frame - 4),
            ["fDamageWeaponMult"] = Scalar(1, 0x011CE2C4), ["fDamageArmConditionBase"] = Scalar(.2f, 0x011CFCB4),
            ["fDamageArmConditionMult"] = Scalar(.8f, 0x011CFAF0), ["fDamageSkillBase"] = Scalar(.5f, 0x011CECEC),
            ["fDamageSkillMult"] = Scalar(.5f, 0x011CF354)
        };
        inputs["meleeDamageActorValue"]!["actorValueCode"] = 17;
        inputs["meleeDamageActorValue"]!["rawHex"] = "00000000000000c00040";
        return new JsonObject
        {
            ["kind"] = "damage-stage", ["protocol"] = 1, ["sequence"] = 3, ["dropped"] = 0,
            ["requestId"] = 1, ["frame"] = 1, ["targetKind"] = "actor", ["engineTargetFormId"] = 20,
            ["engineTargetBaseFormId"] = 7, ["engineTargetFormType"] = 0x3B, ["engineTargetBaseFormType"] = 0x2A,
            ["engineOtherFormId"] = 21, ["engineOtherBaseFormId"] = 8,
            ["executableSha256"] = ActorEngineProfile.PcRetail.ExecutableSha256,
            ["codeEvidenceImageSha256"] = "e46b43cdaa32d9b79b7816fa45cb076c7ed59e335b1533118dcb6bf1d9da692d",
            ["hitInvocationId"] = 1, ["stageInvocationId"] = 1, ["captureGeneration"] = 1, ["connectionGeneration"] = 1,
            ["loadEpoch"] = 1, ["threadId"] = 1, ["entryFrame"] = 1, ["leaseId"] = 1,
            ["hitAddress"] = hit, ["attackerAddress"] = attacker, ["targetAddress"] = target,
            ["itemAddress"] = item, ["weaponAddress"] = weapon, ["weaponFormId"] = 0x1000,
            ["retainedEventLimit"] = 64, ["omittedBefore"] = 0,
            ["damageStage"] = new JsonObject
            {
                ["version"] = 1, ["status"] = "observed", ["reason"] = null,
                ["route"] = "009B5170/004BDF00/00644CE0", ["codeProfile"] = "pc-retail-damage-v1",
                ["returnAddress"] = 0x004BDF76, ["frameAddress"] = frame, ["callerFrameAddress"] = caller,
                ["x87ControlWord"] = 0x007F, ["x87ReturnControlWord"] = 0x007F,
                ["arguments"] = new JsonArray(arguments.Select(value => JsonValue.Create(value)).ToArray()),
                ["inputs"] = inputs, ["output"] = Scalar(999, frame - 0x74),
                ["operandFrameRawHex"] = Convert.ToHexString(raw), ["weaponRawHex"] = Convert.ToHexString(weaponRaw),
                ["hitReturnRawHex"] = Convert.ToHexString(hitRaw), ["hitReturnHealthDamage"] = Scalar(6, hit + 0x14),
                ["hitReturnFlags"] = Integer(0x80, hit + 0x58), ["hitIdentityRepeated"] = true,
                ["codeProfileMatched"] = true, ["constantsRepeated"] = true, ["meleeOperandCalls"] = 1,
                ["limbOperandCalls"] = 1, ["inputsRepeated"] = true, ["returnIsFinalHealthDamage"] = false
            }
        };
    }

    private static void EditRaw(JsonNode stage, string field, int offset, uint value)
    {
        var raw = Convert.FromHexString(stage[field]!.GetValue<string>());
        U32(raw, offset, value); stage[field] = Convert.ToHexString(raw);
    }
    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
    private static void U32(byte[] raw, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset), value);
    private static JsonObject Scalar(float value, uint address) => new()
    { ["status"] = "observed", ["value"] = (double)value, ["rawUInt32"] = Bits(value), ["sourceAddress"] = address };
    private static JsonObject Integer(uint value, uint address) => new()
    { ["status"] = "observed", ["value"] = value, ["rawUInt32"] = value, ["sourceAddress"] = address };
}
