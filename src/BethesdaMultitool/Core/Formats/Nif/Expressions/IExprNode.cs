using BethesdaMultitool.Core.Formats.Nif.Conversion;

namespace BethesdaMultitool.Core.Formats.Nif.Expressions;

internal interface IExprNode
{
    bool Eval(NifVersionContext ctx);
}
