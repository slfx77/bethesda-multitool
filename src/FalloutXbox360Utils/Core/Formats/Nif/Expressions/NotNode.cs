using FalloutXbox360Utils.Core.Formats.Nif.Conversion;

namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

internal sealed class NotNode(IExprNode inner) : IExprNode
{
    public bool Eval(NifVersionContext ctx)
    {
        return !inner.Eval(ctx);
    }
}
