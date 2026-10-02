using System.Buffers.Binary;
using System.Text.Json;
using BethesdaMultitool.Core.Actors;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Actors;

public class ActorCriticalInvocationObservationReaderTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)]
    [InlineData(15)] [InlineData(16)] [InlineData(17)]
    public void Read_RequiresCompleteAttributedDecision(int fault)
    {
        var row = Fixture();
        switch (fault)
        {
            case 1: row["captureGeneration"] = 9; break;
            case 2: row["engineTargetFormId"] = 9; break;
            case 3: row["engineTargetBaseFormId"] = 9; break;
            case 4: row["engineTargetFormType"] = 0x3C; break;
            case 5: row["selectedWeaponAddress"] = 0x6004; break;
            case 6: row["selectedWeaponFormId"] = 0x1235; break;
            case 7: row["randomRemainder"] = 43; break;
            case 8: row["thresholdSigned"] = 89; break;
            case 9: row["comparisonAccepted"] = false; break;
            case 10: row["stepCount"] = 8; break;
            case 11: row["criticalFlagAfter"] = false; break;
            case 12: row["codeVerified"] = false; break;
            case 13: row["failureCode"] = 1; break;
            case 14: row["hitInvocationId"] = 0; break;
            case 15: row["weaponStageBits"] = 0x7FC00000u; break;
            case 16: row["randomRaw"] = "42"; break;
            case 17: row["thresholdRaw"] = 91; row["thresholdSigned"] = 91; break;
        }
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(row));
        var values = ActorCriticalInvocationObservationReader.Read(document.RootElement, 0x14, 7, "NPC_", 1, 2);
        Assert.Equal(fault == 0, values.All(v => v.Status == "Observed"));
        if (fault != 0) Assert.NotEmpty(Assert.Single(values).MissingDependencies);
        else
        {
            Assert.Equal(90, values.Single(v => v.Key == "CritChance.threshold").Value);
            Assert.Equal(1, values.Single(v => v.Key == "CritChance.accepted").Value);
            Assert.All(values, v => Assert.Equal(4u, v.HitFlags));
            Assert.DoesNotContain(values, v => v.Key == "RuntimeCriticalChance");
        }
    }

    [Theory]
    [InlineData(-1f, -1, 0u, false)]
    [InlineData(1.9f, 1, 0u, true)]
    [InlineData(90f, 90, 89u, true)]
    [InlineData(90f, 90, 90u, false)]
    [InlineData(1000f, 1000, 999u, true)]
    public void Read_PreservesSignedThresholdAndEqualityBoundary(float scaled, int threshold, uint draw, bool accepted)
    {
        var row = Fixture();
        row["finalChanceScaledBits"] = BitConverter.SingleToUInt32Bits(scaled);
        row["randomRaw"] = draw; row["randomRemainder"] = draw;
        row["thresholdRaw"] = unchecked((uint)threshold); row["thresholdSigned"] = threshold;
        row["comparisonAccepted"] = accepted; row["criticalFlagAfter"] = accepted;
        var after = Convert.FromHexString((string)row["hitAfterHex"]!);
        BinaryPrimitives.WriteUInt32LittleEndian(after.AsSpan(0x58), accepted ? 4u : 0u);
        row["hitAfterHex"] = Convert.ToHexString(after);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(row));
        var values = ActorCriticalInvocationObservationReader.Read(document.RootElement, 0x14, 7, "NPC_", 1, 2);
        Assert.All(values, value => Assert.Equal("Observed", value.Status));
        Assert.Equal(threshold, values.Single(v => v.Key == "CritChance.threshold").Value);
        Assert.Equal(accepted ? 1 : 0, values.Single(v => v.Key == "CritChance.accepted").Value);
    }

    [Fact]
    public void Read_RetainsCompleteObservationAfterEarlierAbandonment()
    {
        var row = Fixture(); row["abandonedInvocations"] = 2;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(row));
        var values = ActorCriticalInvocationObservationReader.Read(document.RootElement, 0x14, 7, "NPC_", 1, 2);
        Assert.All(values, value =>
        {
            Assert.Equal("Observed", value.Status);
            Assert.Contains("Earlier critical invocations abandoned: 2", value.TraceDiagnostics);
            Assert.DoesNotContain(value.Inputs, input => input.Key == "Capture.abandonedInvocations");
        });
    }

    [Fact]
    public void Read_DistinguishesEarlyExitFromZeroRandomDraw()
    {
        var row = Fixture();row["earlyExit"] = true;row["stepCount"] = 2;
        row["randomObserved"] = false;row["thresholdObserved"] = false;row["criticalFlagAfter"] = false;
        row["hitAfterHex"] = row["hitBeforeHex"];
        foreach (var key in new[] { "randomRaw", "randomRemainder", "thresholdRaw", "thresholdSigned", "comparisonAccepted" }) row[key] = null;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(row));
        var result = Assert.Single(ActorCriticalInvocationObservationReader.Read(document.RootElement, 0x14, 7, "NPC_", 1, 2));
        Assert.Equal("CritChance.eligible", result.Key);Assert.Equal("Observed", result.Status);Assert.Equal(0, result.Value);
    }

    internal static Dictionary<string, object?> Fixture()
    {
        var before = new byte[0x64];var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(before, 0x5000);
        BinaryPrimitives.WriteUInt32LittleEndian(before.AsSpan(4), 0x7000);
        header[4] = 0x28;BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 0x1234);
        var after = (byte[])before.Clone();BinaryPrimitives.WriteUInt32LittleEndian(after.AsSpan(0x58), 4);
        return new()
        {
            ["kind"] = "critical-invocation", ["schemaVersion"] = 1, ["profile"] = "pc-retail-critical-invocation-v1",
            ["status"] = "Observed", ["complete"] = true, ["codeVerified"] = true, ["failureCode"] = 0,
            ["executableSha256"] = ActorEngineProfile.PcRetail.ExecutableSha256,
            ["codeEvidenceImageSha256"] = "e46b43cdaa32d9b79b7816fa45cb076c7ed59e335b1533118dcb6bf1d9da692d",
            ["abandonedInvocations"] = 0, ["dropped"] = 0, ["x87ControlWord"] = 0x007F,
            ["captureGeneration"] = 1, ["connectionGeneration"] = 2, ["loadEpoch"] = 3, ["threadId"] = 4,
            ["hitInvocationId"] = 5, ["leaseId"] = 6, ["targetKind"] = "actor",
            ["engineTargetFormId"] = 0x14, ["engineTargetBaseFormId"] = 7, ["targetFormId"] = 2,
            ["engineTargetFormType"] = 0x3B, ["engineTargetBaseFormType"] = 0x2A,
            ["hitBeforeHex"] = Convert.ToHexString(before), ["hitAfterHex"] = Convert.ToHexString(after),
            ["selectedWeaponHeaderHex"] = Convert.ToHexString(header), ["hitAddress"] = 0x4000,
            ["sourceAddress"] = 0x5000, ["targetAddress"] = 0x7000, ["sourceFormId"] = 0x14,
            ["sourceBaseFormId"] = 7, ["weaponAddress"] = 0, ["fallbackAddress"] = 0x6000,
            ["selectedWeaponAddress"] = 0x6000, ["selectedWeaponFormId"] = 0x1234, ["context"] = 0,
            ["criticalFlagBefore"] = false, ["criticalFlagAfter"] = true, ["earlyExit"] = false,
            ["stepCount"] = 9, ["randomObserved"] = true, ["thresholdObserved"] = true,
            ["randomRaw"] = 42, ["randomRemainder"] = 42, ["thresholdRaw"] = 90, ["thresholdSigned"] = 90,
            ["comparisonAccepted"] = true, ["weaponStageBits"] = BitConverter.SingleToUInt32Bits(9),
            ["sourceModifierStageBits"] = BitConverter.SingleToUInt32Bits(9), ["targetModifierStageBits"] = BitConverter.SingleToUInt32Bits(9),
            ["finalChanceScaledBits"] = BitConverter.SingleToUInt32Bits(90),
            ["vatsBonusBits"] = BitConverter.SingleToUInt32Bits(5), ["sneakMultiplierBits"] = BitConverter.SingleToUInt32Bits(100)
        };
    }
}
