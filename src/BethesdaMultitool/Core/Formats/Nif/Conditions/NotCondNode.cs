namespace BethesdaMultitool.Core.Formats.Nif.Conditions;

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
