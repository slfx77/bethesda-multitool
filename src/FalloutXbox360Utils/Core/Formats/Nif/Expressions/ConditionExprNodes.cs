// AST node types for NifConditionExpr — condition expression evaluation

namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

internal interface ICondNode
{
    bool Eval(IReadOnlyDictionary<string, object> fields);
    void GatherFields(HashSet<string> fields);
}

internal interface IValueNode
{
    long Eval(IReadOnlyDictionary<string, object> fields);
    void GatherFields(HashSet<string> fields);
}

internal enum ConditionCompareOp
{
    Gt,
    Gte,
    Lt,
    Lte,
    Eq,
    Neq
}

internal sealed class LiteralNode(long value) : IValueNode
{
    public long Eval(IReadOnlyDictionary<string, object> fields)
    {
        return value;
    }

    public void GatherFields(HashSet<string> fields)
    {
    }
}

internal sealed class FieldNode(string fieldName) : IValueNode
{
    public long Eval(IReadOnlyDictionary<string, object> fields)
    {
        if (fields.TryGetValue(fieldName, out var val))
        {
            return val switch
            {
                bool b => b ? 1 : 0,
                byte b => b,
                sbyte sb => sb,
                short s => s,
                ushort us => us,
                int i => i,
                uint ui => ui,
                long l => l,
                ulong ul => (long)ul,
                _ => 0
            };
        }

        // Field not found - default to 0 (conservative for "Has X" conditions)
        return 0;
    }

    public void GatherFields(HashSet<string> fields)
    {
        fields.Add(fieldName);
    }
}

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

internal sealed class BitOrNode(IValueNode left, IValueNode right) : IValueNode
{
    public long Eval(IReadOnlyDictionary<string, object> fields)
    {
        return left.Eval(fields) | right.Eval(fields);
    }

    public void GatherFields(HashSet<string> fields)
    {
        left.GatherFields(fields);
        right.GatherFields(fields);
    }
}

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

internal sealed class AndCondNode(ICondNode left, ICondNode right) : ICondNode
{
    public bool Eval(IReadOnlyDictionary<string, object> fields)
    {
        return left.Eval(fields) && right.Eval(fields);
    }

    public void GatherFields(HashSet<string> fields)
    {
        left.GatherFields(fields);
        right.GatherFields(fields);
    }
}

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
