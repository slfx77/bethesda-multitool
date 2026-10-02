using BethesdaMultitool.Core.Formats.Nif.Conditions;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     The name scope of one block or struct instance during a decode, chained to the enclosing instance's scope.
///     Expressions resolve names here, never in a flattened map, so a struct's "Num Vertices" cannot be confused
///     with its parent's.
/// </summary>
/// <remarks>
///     Resolution walks the chain from the innermost scope. In each scope a decoded field wins. A name the
///     definition declares but whose fields were all excluded (by version, type or condition) reads as 0, which
///     nif.xml relies on: <c>(Data Flags #BITOR# BS Data Flags)</c> in NiGeometryData always has one side absent.
///     A name declared but not yet reached, a name no scope in the chain declares, and a decoded field that is not
///     an integer are schema faults.
/// </remarks>
internal sealed class NifDecodeScope
{
    private readonly IReadOnlySet<string> _declared;
    private readonly Dictionary<string, NifValue> _decoded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);

    /// <summary>Creates a scope for one block or struct instance.</summary>
    /// <param name="parent">The enclosing instance's scope, or null for a block.</param>
    /// <param name="definitionName">The block type or struct name.</param>
    /// <param name="declared">Every field name the definition declares (all versions).</param>
    /// <param name="argument">The value passed through the field's <c>arg</c> attribute, or null when none was.</param>
    /// <param name="template">The <c>#T#</c> type passed through the field's <c>template</c>, or null.</param>
    public NifDecodeScope(
        NifDecodeScope? parent,
        string definitionName,
        IReadOnlySet<string> declared,
        long? argument,
        string? template)
    {
        Parent = parent;
        DefinitionName = definitionName;
        _declared = declared;
        Argument = argument;
        Template = template;
    }

    /// <summary>The enclosing instance's scope, or null for a block.</summary>
    public NifDecodeScope? Parent { get; }

    /// <summary>The block type or struct name this scope belongs to.</summary>
    public string DefinitionName { get; }

    /// <summary>The <c>#ARG#</c> value of this instance, or null when no argument was passed.</summary>
    public long? Argument { get; }

    /// <summary>The <c>#T#</c> type of this instance, or null.</summary>
    public string? Template { get; }

    /// <summary>Records a decoded field (a later field of the same name replaces an earlier one).</summary>
    public void RecordDecoded(string name, NifValue value)
    {
        _decoded[name] = value;
    }

    /// <summary>Records a field that was excluded by its version, type or condition filters.</summary>
    public void RecordSkipped(string name)
    {
        _skipped.Add(name);
    }

    /// <summary>
    ///     Resolves a name to a decoded value, or null when the name is declared but absent. Raises a schema fault for
    ///     a name that is declared but not yet read, or that no scope in the chain declares.
    /// </summary>
    public NifValue? Resolve(string name)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope._decoded.TryGetValue(name, out var value))
            {
                return value;
            }

            if (!scope._declared.Contains(name))
            {
                continue;
            }

            if (scope._skipped.Contains(name))
            {
                return null;
            }

            throw new NifDecodeFault(NifDecodeFailureKind.Schema,
                $"'{name}' is referenced before {scope.DefinitionName} reads it");
        }

        throw new NifDecodeFault(NifDecodeFailureKind.Schema,
            $"'{name}' is not declared by {DescribeChain()}");
    }

    /// <summary>
    ///     Resolves a name to an integer: the decoded integer, or 0 when the name is declared but absent. Raises a
    ///     schema fault for a decoded value that is not an integer (see <see cref="Resolve" /> for the other faults).
    /// </summary>
    public long ResolveInteger(string name)
    {
        if (string.Equals(name, NifStrictExpression.ArgumentToken, StringComparison.Ordinal))
        {
            return Argument ?? throw new NifDecodeFault(NifDecodeFailureKind.Schema,
                $"#ARG# is used but no argument was passed to {DefinitionName}");
        }

        return Resolve(name) switch
        {
            null => 0,
            NifIntegerValue integer => integer.Value,
            var other => throw new NifDecodeFault(NifDecodeFailureKind.Schema,
                $"'{name}' decoded as {other.Kind} ({other.TypeName}), which an expression cannot use as an integer")
        };
    }

    /// <summary>The decoded integers visible from this scope (innermost first), for enumeration.</summary>
    public IEnumerable<KeyValuePair<string, long>> VisibleIntegers()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (Argument is { } argument)
        {
            seen.Add(NifStrictExpression.ArgumentToken);
            yield return new KeyValuePair<string, long>(NifStrictExpression.ArgumentToken, argument);
        }

        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            foreach (var (name, value) in scope._decoded)
            {
                if (value is NifIntegerValue integer && seen.Add(name))
                {
                    yield return new KeyValuePair<string, long>(name, integer.Value);
                }
            }
        }
    }

    private string DescribeChain()
    {
        var names = new List<string>();
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            names.Add(scope.DefinitionName);
        }

        return string.Join(" < ", names);
    }
}
