using FalloutXbox360Utils.Core.Formats.Nif.Conversion;

namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

internal interface IExprNode
{
    bool Eval(NifVersionContext ctx);
}
