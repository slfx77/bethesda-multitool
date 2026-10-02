namespace BethesdaMultitool.Core.Formats.Nif.Conditions;

/// <summary>
///     A nif.xml <c>cond</c>, <c>length</c>, <c>width</c> or <c>arg</c> expression that compiled strictly (see
///     <see cref="NifConditionExpr.TryCompileStrict" />): it parsed completely, used only field names, literals, the
///     supported operators and the <c>#ARG#</c> token, and it lists every name it references so the caller can check
///     them against the declared fields before evaluating.
/// </summary>
/// <remarks>
///     Evaluation goes through the same AST nodes as the fail-open evaluator. Those nodes treat a name the field map
///     does not contain as 0, so a strict caller must pass a map whose lookup refuses undeclared names (the NIF block
///     decoder's scope view throws for them).
/// </remarks>
internal sealed class NifStrictExpression
{
    /// <summary>The token nif.xml uses for the argument passed to a struct instance.</summary>
    public const string ArgumentToken = "#ARG#";

    private readonly ICondNode? _condition;
    private readonly IValueNode? _value;

    internal NifStrictExpression(
        string expression,
        NifStrictExpressionKind kind,
        ICondNode? condition,
        IValueNode? value,
        IReadOnlyList<string> referencedNames)
    {
        Expression = expression;
        Kind = kind;
        _condition = condition;
        _value = value;
        ReferencedNames = referencedNames;
        SingleFieldName = value is FieldNode && referencedNames.Count == 1 &&
                          !string.Equals(referencedNames[0], ArgumentToken, StringComparison.Ordinal)
            ? referencedNames[0]
            : null;
    }

    /// <summary>The expression text exactly as nif.xml wrote it.</summary>
    public string Expression { get; }

    /// <summary>The grammar the expression was compiled with.</summary>
    public NifStrictExpressionKind Kind { get; }

    /// <summary>
    ///     Every field name the expression references, plus <see cref="ArgumentToken" /> when it uses the argument,
    ///     sorted ordinally.
    /// </summary>
    public IReadOnlyList<string> ReferencedNames { get; }

    /// <summary>True when the expression reads the struct argument (<c>#ARG#</c>).</summary>
    public bool UsesArgument => ReferencedNames.Contains(ArgumentToken, StringComparer.Ordinal);

    /// <summary>
    ///     The field name when the whole value expression is one bare field reference (for example the
    ///     <c>width="Strip Lengths"</c> of a jagged array); otherwise null.
    /// </summary>
    public string? SingleFieldName { get; }

    /// <summary>Evaluates the expression as a condition (a value expression is true when non-zero).</summary>
    public bool EvaluateCondition(IReadOnlyDictionary<string, object> fields)
    {
        return _condition?.Eval(fields) ?? _value!.Eval(fields) != 0;
    }

    /// <summary>Evaluates a value expression.</summary>
    /// <exception cref="InvalidOperationException">The expression was compiled as a condition.</exception>
    public long EvaluateValue(IReadOnlyDictionary<string, object> fields)
    {
        if (_value is null)
        {
            throw new InvalidOperationException($"'{Expression}' was compiled as a condition, not a value.");
        }

        return _value.Eval(fields);
    }
}
