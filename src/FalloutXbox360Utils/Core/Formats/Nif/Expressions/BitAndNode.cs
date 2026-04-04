namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

internal sealed class BitAndNode(IValueNode left, IValueNode right) : IValueNode
{
    public long Eval(IReadOnlyDictionary<string, object> fields)
    {
        return left.Eval(fields) & right.Eval(fields);
    }

    public void GatherFields(HashSet<string> fields)
    {
        left.GatherFields(fields);
        right.GatherFields(fields);
    }
}
