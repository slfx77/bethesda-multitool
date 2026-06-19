using BethesdaMultitool.Core.Formats.Nif.Conversion;

namespace BethesdaMultitool.Core.Formats.Nif.Expressions;

internal sealed class OrNode(IExprNode left, IExprNode right) : IExprNode
{
    public bool Eval(NifVersionContext ctx)
    {
        return left.Eval(ctx) || right.Eval(ctx);
    }
}
