using BethesdaMultitool.Core.Formats.Nif.Conversion;

namespace BethesdaMultitool.Core.Formats.Nif.Conditions;

internal interface IExprNode
{
    bool Eval(NifVersionContext ctx);
}
