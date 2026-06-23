// AST node types for NIF version expression parser
// Supports recursive evaluation of version conditions

using BethesdaMultitool.Core.Formats.Nif.Conversion;

namespace BethesdaMultitool.Core.Formats.Nif.Conditions;

internal sealed class CompareNode(VersionVariableType variable, VersionCompareOp op, long value) : IExprNode
{
    public bool Eval(NifVersionContext ctx)
    {
        var varValue = variable switch
        {
            VersionVariableType.Version => ctx.Version,
            VersionVariableType.BsVersion => ctx.BsVersion,
            VersionVariableType.UserVersion => (long)ctx.UserVersion,
            _ => 0
        };

        return op switch
        {
            VersionCompareOp.Gt => varValue > value,
            VersionCompareOp.Gte => varValue >= value,
            VersionCompareOp.Lt => varValue < value,
            VersionCompareOp.Lte => varValue <= value,
            VersionCompareOp.Eq => varValue == value,
            VersionCompareOp.Neq => varValue != value,
            _ => false
        };
    }
}
