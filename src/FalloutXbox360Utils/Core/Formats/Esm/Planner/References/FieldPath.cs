namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.References;

/// <summary>
///     Canonical path constructors for FormID-bearing fields inside a record. Encoders look
///     up resolved references by these strings — building paths ad hoc would let typos slip
///     through to runtime. Add a helper here, not at the call site.
/// </summary>
/// <remarks>
///     Grammar:
///     <list type="bullet">
///         <item><c>"XLKR"</c> — plain subrecord at single occurrence.</item>
///         <item><c>"SCRO[3]"</c> — zero-based index into a repeated subrecord (the 4th SCRO).</item>
///         <item><c>"PLDT.Union"</c> — typed-union member inside a structural subrecord.</item>
///         <item><c>"CTDA[2].Parameter1"</c> — repeated subrecord, then a named field within it.</item>
///     </list>
///     Consumers usually just key by string — the grammar is closed and stable so there's
///     no general-purpose parser; each builder method below produces one specific shape.
/// </remarks>
public static class FieldPath
{
    /// <summary>Reference at single-occurrence subrecord, e.g. <c>SCRI</c>, <c>XLKR</c>, <c>QSTI</c>.</summary>
    public static string Subrecord(string signature)
    {
        EnsureSignature(signature);
        return signature;
    }

    /// <summary>Reference at a specific repeated-subrecord index, e.g. <c>SCRO[3]</c>.</summary>
    public static string Indexed(string signature, int index)
    {
        EnsureSignature(signature);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Subrecord index must be non-negative.");
        }

        return $"{signature}[{index}]";
    }

    /// <summary>Reference to a named union member inside a structural subrecord (PLDT.Union, etc.).</summary>
    public static string Member(string signature, string memberName)
    {
        EnsureSignature(signature);
        if (string.IsNullOrWhiteSpace(memberName))
        {
            throw new ArgumentException("Member name required.", nameof(memberName));
        }

        return $"{signature}.{memberName}";
    }

    /// <summary>Named field within a specific repeated subrecord entry, e.g. <c>CTDA[2].Parameter1</c>.</summary>
    public static string IndexedMember(string signature, int index, string memberName)
    {
        EnsureSignature(signature);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (string.IsNullOrWhiteSpace(memberName))
        {
            throw new ArgumentException("Member name required.", nameof(memberName));
        }

        return $"{signature}[{index}].{memberName}";
    }

    private static void EnsureSignature(string signature)
    {
        if (string.IsNullOrEmpty(signature) || signature.Length != 4)
        {
            throw new ArgumentException(
                "Subrecord signatures must be exactly 4 characters.",
                nameof(signature));
        }
    }
}
