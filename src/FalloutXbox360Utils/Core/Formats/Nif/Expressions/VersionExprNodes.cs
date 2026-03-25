// AST node types for NIF version expression parser
// Supports recursive evaluation of version conditions

using FalloutXbox360Utils.Core.Formats.Nif.Conversion;

namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

internal interface IExprNode
{
    bool Eval(NifVersionContext ctx);
}

internal enum VersionVariableType
{
    Version,
    BsVersion,
    UserVersion
}

internal enum VersionCompareOp
{
    Gt,
    Gte,
    Lt,
    Lte,
    Eq,
    Neq
}

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

internal sealed class AndNode(IExprNode left, IExprNode right) : IExprNode
{
    public bool Eval(NifVersionContext ctx)
    {
        return left.Eval(ctx) && right.Eval(ctx);
    }
}

internal sealed class OrNode(IExprNode left, IExprNode right) : IExprNode
{
    public bool Eval(NifVersionContext ctx)
    {
        return left.Eval(ctx) || right.Eval(ctx);
    }
}

internal sealed class NotNode(IExprNode inner) : IExprNode
{
    public bool Eval(NifVersionContext ctx)
    {
        return !inner.Eval(ctx);
    }
}
