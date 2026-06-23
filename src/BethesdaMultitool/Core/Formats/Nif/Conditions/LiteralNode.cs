// AST node types for NifConditionExpr — condition expression evaluation

namespace BethesdaMultitool.Core.Formats.Nif.Conditions;

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
