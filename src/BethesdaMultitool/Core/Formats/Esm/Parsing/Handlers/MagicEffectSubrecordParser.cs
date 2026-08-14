using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Parses one record's repeated <c>EFID + EFIT + CTDA*</c> effect groups. Optional CIS1/CIS2
///     values bind only to the immediately preceding CTDA. Callers retain ownership of every
///     non-effect subrecord.
/// </summary>
internal sealed class MagicEffectSubrecordParser(bool bigEndian)
{
    private readonly ConditionStringSiblingBinder _conditionStrings = new();
    private uint _currentEffectFormId;

    public List<EnchantmentEffect> Effects { get; } = [];

    /// <summary>Consumes one effect-owned subrecord, or returns false for the caller to handle.</summary>
    public bool TryConsume(string signature, ReadOnlySpan<byte> data)
    {
        if (_conditionStrings.TryConsume(signature, data))
        {
            return true;
        }

        switch (signature)
        {
            case "EFID" when data.Length >= 4:
                _currentEffectFormId = RecordParserContext.ReadFormId(data, bigEndian);
                return true;

            case "EFIT" when data.Length >= 12:
                if (SubrecordSchemaView.TryRead("EFIT", null, data, bigEndian) is not { } view)
                {
                    return false;
                }

                var area = view.UInt32("Area");
                var duration = view.UInt32("Duration");
                var targetType = view.UInt32("Type");
                var actorValue = view.Int32("ActorValue", -1);
                Effects.Add(new EnchantmentEffect
                {
                    EffectFormId = _currentEffectFormId,
                    Magnitude = GameStatNormalizer.EffectMagnitude(data, bigEndian),
                    Area = GameStatNormalizer.IsPlausibleEffectArea(area) ? area : 0,
                    Duration = GameStatNormalizer.IsPlausibleEffectDuration(duration) ? duration : 0,
                    Type = GameStatNormalizer.IsPlausibleEffectTarget(targetType) ? targetType : 0,
                    ActorValue = GameStatNormalizer.IsPlausibleActorValue(actorValue) ? actorValue : -1,
                });
                return true;

            case "CTDA" when Effects.Count > 0:
                if (!CtdaParser.TryDecode(data, bigEndian, out var effectCondition, out _))
                {
                    return false;
                }

                Effects[^1] = Effects[^1] with
                {
                    Conditions = [.. Effects[^1].Conditions, effectCondition],
                };
                _conditionStrings.Begin(Effects[^1].Conditions);
                return true;

            default:
                return false;
        }
    }
}
