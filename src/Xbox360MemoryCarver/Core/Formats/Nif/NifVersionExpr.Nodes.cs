// AST node types for NIF version expression parser
// Supports recursive evaluation of version conditions

// S3218: Method shadowing is intentional in this expression tree visitor pattern
#pragma warning disable S3218

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     NIF version expression parser - AST node types.
/// </summary>
public sealed partial class NifVersionExpr
{
    #region AST Nodes

    private interface IExprNode
    {
        bool Evaluate(NifVersionContext ctx);
    }

    private enum VariableType
    {
        Version,
        BsVersion,
        UserVersion
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

    private sealed class CompareNode(VariableType variable, CompareOp op, long value) : IExprNode
    {
        public bool Evaluate(NifVersionContext ctx)
        {
            var varValue = variable switch
            {
                VariableType.Version => ctx.Version,
                VariableType.BsVersion => ctx.BsVersion,
                VariableType.UserVersion => (long)ctx.UserVersion,
                _ => 0
            };

            return op switch
            {
                CompareOp.Gt => varValue > value,
                CompareOp.Gte => varValue >= value,
                CompareOp.Lt => varValue < value,
                CompareOp.Lte => varValue <= value,
                CompareOp.Eq => varValue == value,
                CompareOp.Neq => varValue != value,
                _ => false
            };
        }
    }

    private sealed class AndNode(IExprNode left, IExprNode right) : IExprNode
    {
        public bool Evaluate(NifVersionContext ctx)
        {
            return left.Evaluate(ctx) && right.Evaluate(ctx);
        }
    }

    private sealed class OrNode(IExprNode left, IExprNode right) : IExprNode
    {
        public bool Evaluate(NifVersionContext ctx)
        {
            return left.Evaluate(ctx) || right.Evaluate(ctx);
        }
    }

    private sealed class NotNode(IExprNode inner) : IExprNode
    {
        public bool Evaluate(NifVersionContext ctx)
        {
            return !inner.Evaluate(ctx);
        }
    }

    #endregion
}
