// AST node types for NifConditionExpr

#pragma warning disable S3218

namespace Xbox360MemoryCarver.Core.Formats.Nif;

// AST nodes for condition expression evaluation
public sealed partial class NifConditionExpr
{
    private interface ICondNode
    {
        bool Evaluate(IReadOnlyDictionary<string, object> fields);
        void CollectFields(HashSet<string> fields);
    }

    private interface IValueNode
    {
        long Evaluate(IReadOnlyDictionary<string, object> fields);
        void CollectFields(HashSet<string> fields);
    }

    private enum CompareOp
    {
        Gt,
        Gte,
        Lt,
        Lte,
        Eq,
        Neq
    }

    private sealed class LiteralNode(long value) : IValueNode
    {
        public long Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return value;
        }

        public void CollectFields(HashSet<string> fields)
        {
        }
    }

    private sealed class FieldNode(string fieldName) : IValueNode
    {
        public long Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            if (fields.TryGetValue(fieldName, out var val))
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
            // Field not found - default to 0 (conservative for "Has X" conditions)
            return 0;
        }

        public void CollectFields(HashSet<string> fields)
        {
            fields.Add(fieldName);
        }
    }

    private sealed class BitAndNode(IValueNode left, IValueNode right) : IValueNode
    {
        public long Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return left.Evaluate(fields) & right.Evaluate(fields);
        }

        public void CollectFields(HashSet<string> fields)
        {
            left.CollectFields(fields);
            right.CollectFields(fields);
        }
    }

    private sealed class BitOrNode(IValueNode left, IValueNode right) : IValueNode
    {
        public long Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return left.Evaluate(fields) | right.Evaluate(fields);
        }

        public void CollectFields(HashSet<string> fields)
        {
            left.CollectFields(fields);
            right.CollectFields(fields);
        }
    }

    /// <summary>
    ///     Wraps a condition node to use as a value (for parenthesized expressions that return bool).
    /// </summary>
    private sealed class WrappedNode(ICondNode inner) : IValueNode
    {
        public long Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return inner.Evaluate(fields) ? 1 : 0;
        }

        public void CollectFields(HashSet<string> fields)
        {
            inner.CollectFields(fields);
        }
    }

    private sealed class CompareCondNode(IValueNode left, CompareOp op, IValueNode right) : ICondNode
    {
        public bool Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            var l = left.Evaluate(fields);
            var r = right.Evaluate(fields);

            return op switch
            {
                CompareOp.Gt => l > r,
                CompareOp.Gte => l >= r,
                CompareOp.Lt => l < r,
                CompareOp.Lte => l <= r,
                CompareOp.Eq => l == r,
                CompareOp.Neq => l != r,
                _ => false
            };
        }

        public void CollectFields(HashSet<string> fields)
        {
            left.CollectFields(fields);
            right.CollectFields(fields);
        }
    }

    private sealed class BoolCondNode(IValueNode value) : ICondNode
    {
        public bool Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return value.Evaluate(fields) != 0;
        }

        public void CollectFields(HashSet<string> fields)
        {
            value.CollectFields(fields);
        }
    }

    private sealed class AndCondNode(ICondNode left, ICondNode right) : ICondNode
    {
        public bool Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return left.Evaluate(fields) && right.Evaluate(fields);
        }

        public void CollectFields(HashSet<string> fields)
        {
            left.CollectFields(fields);
            right.CollectFields(fields);
        }
    }

    private sealed class OrCondNode(ICondNode left, ICondNode right) : ICondNode
    {
        public bool Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return left.Evaluate(fields) || right.Evaluate(fields);
        }

        public void CollectFields(HashSet<string> fields)
        {
            left.CollectFields(fields);
            right.CollectFields(fields);
        }
    }

    private sealed class NotCondNode(ICondNode inner) : ICondNode
    {
        public bool Evaluate(IReadOnlyDictionary<string, object> fields)
        {
            return !inner.Evaluate(fields);
        }

        public void CollectFields(HashSet<string> fields)
        {
            inner.CollectFields(fields);
        }
    }
}
