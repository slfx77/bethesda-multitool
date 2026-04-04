namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

internal sealed class CompareCondNode(IValueNode left, ConditionCompareOp op, IValueNode right) : ICondNode
{
    public bool Eval(IReadOnlyDictionary<string, object> fields)
    {
        var l = left.Eval(fields);
        var r = right.Eval(fields);

        return op switch
        {
            ConditionCompareOp.Gt => l > r,
            ConditionCompareOp.Gte => l >= r,
            ConditionCompareOp.Lt => l < r,
            ConditionCompareOp.Lte => l <= r,
            ConditionCompareOp.Eq => l == r,
            ConditionCompareOp.Neq => l != r,
            _ => false
        };
    }

    public void GatherFields(HashSet<string> fields)
    {
        left.GatherFields(fields);
        right.GatherFields(fields);
    }
}
