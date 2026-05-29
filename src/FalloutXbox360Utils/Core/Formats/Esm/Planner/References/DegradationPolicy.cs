namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.References;

/// <summary>
///     What the planner does about an outgoing FormID reference that doesn't resolve.
///     Keyed by record type + field path; consulted by <see cref="ReferenceResolver" /> after
///     it determines the target is not in the emit set.
/// </summary>
public sealed class DegradationPolicy
{
    private readonly Dictionary<(string RecordType, string FieldPath), DanglingAction> _rules = [];
    private readonly Dictionary<string, DanglingAction> _typeDefaults = new(StringComparer.Ordinal);
    private readonly DanglingAction _globalDefault;

    public DegradationPolicy(DanglingAction? globalDefault = null)
    {
        _globalDefault = globalDefault ?? DanglingAction.DropSubrecord;
    }

    /// <summary>Set the default action for a record type. Type-specific paths override this.</summary>
    public void SetDefaultForType(string recordType, DanglingAction action)
    {
        if (string.IsNullOrEmpty(recordType))
        {
            throw new ArgumentException("Record type required.", nameof(recordType));
        }

        _typeDefaults[recordType] = action;
    }

    /// <summary>Set the action for a specific (record type, field path) pair.</summary>
    public void SetRule(string recordType, string fieldPath, DanglingAction action)
    {
        if (string.IsNullOrEmpty(recordType))
        {
            throw new ArgumentException("Record type required.", nameof(recordType));
        }

        if (string.IsNullOrEmpty(fieldPath))
        {
            throw new ArgumentException("Field path required.", nameof(fieldPath));
        }

        _rules[(recordType, fieldPath)] = action;
    }

    /// <summary>
    ///     Look up the dangling-reference action for one (record type, field path). Returns
    ///     the most specific match: exact rule → type default → global default.
    /// </summary>
    public DanglingAction Lookup(string recordType, string fieldPath)
    {
        if (_rules.TryGetValue((recordType, fieldPath), out var rule))
        {
            return rule;
        }

        if (_typeDefaults.TryGetValue(recordType, out var typeDefault))
        {
            return typeDefault;
        }

        return _globalDefault;
    }
}

/// <summary>
///     Action taken when a FormID reference's target is not in the emit set. Parallels
///     <see cref="ResolvedRefAction" /> but covers only the dangling case (resolved
///     references don't consult the policy).
/// </summary>
public sealed record DanglingAction
{
    /// <summary>Most common: omit the subrecord (or repeated entry) entirely.</summary>
    public static readonly DanglingAction DropSubrecord =
        new() { Kind = DanglingActionKind.DropSubrecord };

    /// <summary>Emit the subrecord but with a null FormID. Engine treats as no-op.</summary>
    public static readonly DanglingAction NullRef =
        new() { Kind = DanglingActionKind.NullRef };

    /// <summary>Reshape the containing subrecord to a safer variant.</summary>
    public static DanglingAction DowngradeContainer(ContainerDowngrade downgrade) =>
        new() { Kind = DanglingActionKind.DowngradeContainer, Downgrade = downgrade };

    public required DanglingActionKind Kind { get; init; }
    public ContainerDowngrade? Downgrade { get; init; }
}

/// <summary>Discriminator for <see cref="DanglingAction" />.</summary>
public enum DanglingActionKind
{
    DropSubrecord,
    NullRef,
    DowngradeContainer,
}
