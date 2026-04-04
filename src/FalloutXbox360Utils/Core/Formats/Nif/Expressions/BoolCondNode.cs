namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

internal sealed class BoolCondNode(IValueNode value) : ICondNode
{
    public bool Eval(IReadOnlyDictionary<string, object> fields)
    {
        return value.Eval(fields) != 0;
    }

    public void GatherFields(HashSet<string> fields)
    {
        value.GatherFields(fields);
    }
}
