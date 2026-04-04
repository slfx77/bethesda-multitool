namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

internal sealed class NotCondNode(ICondNode inner) : ICondNode
{
    public bool Eval(IReadOnlyDictionary<string, object> fields)
    {
        return !inner.Eval(fields);
    }

    public void GatherFields(HashSet<string> fields)
    {
        inner.GatherFields(fields);
    }
}
