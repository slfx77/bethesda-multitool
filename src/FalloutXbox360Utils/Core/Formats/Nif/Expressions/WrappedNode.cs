namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

/// <summary>
///     Wraps a condition node to use as a value (for parenthesized expressions that return bool).
/// </summary>
internal sealed class WrappedNode(ICondNode inner) : IValueNode
{
    public long Eval(IReadOnlyDictionary<string, object> fields)
    {
        return inner.Eval(fields) ? 1 : 0;
    }

    public void GatherFields(HashSet<string> fields)
    {
        inner.GatherFields(fields);
    }
}
