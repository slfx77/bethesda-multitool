using FalloutXbox360Utils.Core.Formats.Nif.Conversion;

namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

internal sealed class AndNode(IExprNode left, IExprNode right) : IExprNode
{
    public bool Eval(NifVersionContext ctx)
    {
        return left.Eval(ctx) && right.Eval(ctx);
    }
}
