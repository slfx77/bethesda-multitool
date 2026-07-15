using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin;

/// <summary>
///     Conservative speaker attribution for INFO conditions. A GetIsID operand names the
///     speaking actor only when it is evaluated on the subject and its comparison is true
///     for a boolean result of one but false for zero. Negative, global-backed, swapped,
///     target, and reference conditions must not be mistaken for positive retail coverage.
/// </summary>
internal static class DialogueSpeakerBinding
{
    internal const ushort GetIsIdFunctionIndex = 72;

    public static uint? GetExactSpeaker(DialogueRecord info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var getIsIdConditions = info.Conditions
            .Where(static condition => condition.FunctionIndex == GetIsIdFunctionIndex)
            .ToList();
        if (getIsIdConditions.Count == 0)
        {
            return info.SpeakerFormId is > 0 ? info.SpeakerFormId : null;
        }

        var candidates = getIsIdConditions
            .Where(IsPositiveSubjectGetIsId)
            .Select(static condition => condition.Parameter1)
            .Distinct()
            .Take(2)
            .ToList();
        if (candidates.Count != 1)
        {
            return null;
        }

        // SpeakerFormId may be an explicit ANAM/runtime owner or an inference made by the
        // parser. A disagreement is ambiguous and must fail closed for cut-topic rehoming.
        return info.SpeakerFormId is null or 0 || info.SpeakerFormId == candidates[0]
            ? candidates[0]
            : null;
    }

    public static bool IsPositiveSubjectGetIsId(DialogueCondition condition) =>
        condition.FunctionIndex == GetIsIdFunctionIndex
        && condition.Parameter1 != 0
        && condition.RunOn == 0
        && !condition.IsSubjectTargetSwapped
        && (condition.Type & 0x04) == 0
        && PositivelyIdentifiesBooleanResult(condition.Type, condition.ComparisonValue);

    public static bool IsPositiveSubjectGetIsId(ReadOnlySpan<byte> ctda)
    {
        if (ctda.Length < 16
            || BinaryPrimitives.ReadUInt16LittleEndian(ctda[8..]) != GetIsIdFunctionIndex
            || BinaryPrimitives.ReadUInt32LittleEndian(ctda[12..]) == 0)
        {
            return false;
        }

        var type = ctda[0];
        var runOn = ctda.Length >= 24
            ? BinaryPrimitives.ReadUInt32LittleEndian(ctda[20..])
            : 0u;
        if (runOn != 0 || (type & 0x10) != 0 || (type & 0x04) != 0)
        {
            return false;
        }

        var comparisonValue = BinaryPrimitives.ReadSingleLittleEndian(ctda[4..]);
        return PositivelyIdentifiesBooleanResult(type, comparisonValue);
    }

    private static bool PositivelyIdentifiesBooleanResult(byte type, float comparisonValue)
    {
        if (!float.IsFinite(comparisonValue))
        {
            return false;
        }

        var comparisonOperator = (type >> 5) & 0x07;
        return Evaluate(comparisonOperator, 1f, comparisonValue)
               && !Evaluate(comparisonOperator, 0f, comparisonValue);
    }

    private static bool Evaluate(int comparisonOperator, float actual, float expected) =>
        comparisonOperator switch
        {
            0 => MathF.Abs(actual - expected) <= 0.0001f,
            1 => MathF.Abs(actual - expected) > 0.0001f,
            2 => actual > expected,
            3 => actual >= expected,
            4 => actual < expected,
            5 => actual <= expected,
            _ => false,
        };
}
