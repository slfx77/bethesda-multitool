using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     Presents a <see cref="NifDecodeScope" /> as the field map the nif.xml expression nodes evaluate against.
///     Strict by design: the expression nodes read a missing name as 0, so this view never reports a name as
///     missing. <see cref="TryGetValue" /> returns the integer (0 for a declared-but-absent field) or raises a
///     <see cref="NifDecodeFault" /> for a name that is undeclared, not yet read, or not an integer.
/// </summary>
internal sealed class NifScopeFieldView : IReadOnlyDictionary<string, object>
{
    private readonly NifDecodeScope _scope;

    /// <summary>Creates a view over a scope.</summary>
    public NifScopeFieldView(NifDecodeScope scope)
    {
        _scope = scope;
    }

    /// <inheritdoc />
    public object this[string key] => _scope.ResolveInteger(key);

    /// <inheritdoc />
    public IEnumerable<string> Keys => _scope.VisibleIntegers().Select(pair => pair.Key);

    /// <inheritdoc />
    public IEnumerable<object> Values => _scope.VisibleIntegers().Select(pair => (object)pair.Value);

    /// <inheritdoc />
    public int Count => _scope.VisibleIntegers().Count();

    /// <summary>
    ///     True when the name resolves to an integer (a declared-but-absent field counts, as 0); false instead of a
    ///     fault otherwise.
    /// </summary>
    public bool ContainsKey(string key)
    {
        try
        {
            _scope.ResolveInteger(key);
            return true;
        }
        catch (NifDecodeFault)
        {
            return false;
        }
    }

    /// <summary>Resolves a name strictly (see the class remarks); never returns false.</summary>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out object value)
    {
        value = _scope.ResolveInteger(key);
        return true;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        return _scope.VisibleIntegers()
            .Select(pair => new KeyValuePair<string, object>(pair.Key, pair.Value))
            .GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
