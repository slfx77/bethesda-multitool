// AST node types for NifConditionExpr — condition expression evaluation

namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

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
