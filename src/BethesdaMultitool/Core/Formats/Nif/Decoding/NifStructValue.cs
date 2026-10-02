using System.Diagnostics.CodeAnalysis;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     A decoded struct or block: its fields in file order. Only fields that were present (passed their version,
///     type and condition filters) appear.
/// </summary>
internal sealed class NifStructValue : NifValue
{
    /// <summary>Creates a struct value over its decoded fields, in file order.</summary>
    public NifStructValue(string typeName, IReadOnlyList<NifField> fields)
        : base(typeName)
    {
        Fields = fields;
    }

    /// <inheritdoc />
    public override NifValueKind Kind => NifValueKind.Struct;

    /// <summary>The decoded fields in file order.</summary>
    public IReadOnlyList<NifField> Fields { get; }

    /// <summary>True when a field with this name (and ordinal) was decoded.</summary>
    public bool Contains(string name, int ordinal = 0)
    {
        return TryGet(name, out _, ordinal);
    }

    /// <summary>Finds a decoded field by name and ordinal.</summary>
    public bool TryGet(string name, [MaybeNullWhen(false)] out NifValue value, int ordinal = 0)
    {
        foreach (var field in Fields)
        {
            if (field.Ordinal == ordinal && string.Equals(field.Name, name, StringComparison.Ordinal))
            {
                value = field.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>Returns a decoded field's value.</summary>
    /// <exception cref="KeyNotFoundException">No field of that name and ordinal was decoded.</exception>
    public NifValue Get(string name, int ordinal = 0)
    {
        return TryGet(name, out var value, ordinal)
            ? value
            : throw new KeyNotFoundException($"{TypeName} has no decoded field '{name}' (ordinal {ordinal}).");
    }

    /// <summary>Returns a decoded field's value as a specific value type.</summary>
    /// <exception cref="KeyNotFoundException">No field of that name and ordinal was decoded.</exception>
    /// <exception cref="InvalidCastException">The field decoded as a different kind of value.</exception>
    public T Get<T>(string name, int ordinal = 0) where T : NifValue
    {
        var value = Get(name, ordinal);
        return value as T ?? throw new InvalidCastException(
            $"{TypeName}.{name} decoded as {value.GetType().Name}, not {typeof(T).Name}.");
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{TypeName} ({Fields.Count} fields)";
    }
}
