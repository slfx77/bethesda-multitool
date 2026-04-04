namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

internal sealed class OrCondNode(ICondNode left, ICondNode right) : ICondNode
{
    public bool Eval(IReadOnlyDictionary<string, object> fields)
    {
        return left.Eval(fields) || right.Eval(fields);
    }

    public void GatherFields(HashSet<string> fields)
    {
        left.GatherFields(fields);
        right.GatherFields(fields);
    }
}
