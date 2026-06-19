using BethesdaMultitool.Core.Formats.Nif.Conversion;

namespace BethesdaMultitool.Core.Formats.Nif.Expressions;

internal sealed class NotNode(IExprNode inner) : IExprNode
{
    public bool Eval(NifVersionContext ctx)
    {
        return !inner.Eval(ctx);
    }
}
