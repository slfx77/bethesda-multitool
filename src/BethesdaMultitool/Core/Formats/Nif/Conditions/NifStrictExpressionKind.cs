namespace BethesdaMultitool.Core.Formats.Nif.Conditions;

/// <summary>
///     The grammar a strict compile (<see cref="NifConditionExpr.TryCompileStrict" />) applies: a boolean nif.xml
///     <c>cond</c> attribute, or an integer <c>length</c> / <c>width</c> / <c>arg</c> attribute.
/// </summary>
internal enum NifStrictExpressionKind
{
    /// <summary>A boolean <c>cond</c> expression (comparisons, <c>#AND#</c>, <c>#OR#</c>, <c>!</c>).</summary>
    Condition,

    /// <summary>An integer value expression (field names, literals, <c>#BITAND#</c>, <c>#BITOR#</c>, <c>#ARG#</c>).</summary>
    Value
}
