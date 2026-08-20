using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Binds the optional CIS1/CIS2 string subrecords that physically follow one CTDA to that exact
///     condition. The valid sequence is CTDA, optional CIS1, optional CIS2. CIS2 may appear without
///     CIS1; any other sibling ends the association so a later orphan CIS cannot overwrite an older
///     condition.
/// </summary>
internal sealed class ConditionStringSiblingBinder
{
    private bool _acceptsCis1;
    private bool _acceptsCis2;
    private int _conditionIndex = -1;
    private List<DialogueCondition>? _conditions;

    /// <summary>Starts a new sibling sequence for the last condition in <paramref name="conditions" />.</summary>
    public void Begin(List<DialogueCondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions.Count == 0)
        {
            throw new ArgumentException("A CTDA must be added before its CIS siblings can be bound.",
                nameof(conditions));
        }

        _conditions = conditions;
        _conditionIndex = conditions.Count - 1;
        _acceptsCis1 = true;
        _acceptsCis2 = true;
    }

    /// <summary>
    ///     Consumes and binds a valid immediate CIS sibling. Every non-consumed signature terminates
    ///     the current sequence, including an out-of-order or duplicate CIS subrecord.
    /// </summary>
    public bool TryConsume(string signature, ReadOnlySpan<byte> data)
    {
        if (signature == "CIS1" && _conditions is not null && _acceptsCis1)
        {
            var condition = _conditions[_conditionIndex];
            _conditions[_conditionIndex] = condition with
            {
                Parameter1String = EsmStringUtils.ReadNullTermString(data)
            };
            _acceptsCis1 = false;
            return true;
        }

        if (signature == "CIS2" && _conditions is not null && _acceptsCis2)
        {
            var condition = _conditions[_conditionIndex];
            _conditions[_conditionIndex] = condition with
            {
                Parameter2String = EsmStringUtils.ReadNullTermString(data)
            };
            Reset();
            return true;
        }

        Reset();
        return false;
    }

    private void Reset()
    {
        _conditions = null;
        _conditionIndex = -1;
        _acceptsCis1 = false;
        _acceptsCis2 = false;
    }
}
