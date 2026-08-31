using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>
///     Fills the gaps in one record model from a second capture of the same record, without ever
///     overwriting a value the first one actually had.
///     <para>
///         A record recovered from a memory dump can exist twice: once as ESM bytes embedded in the
///         dump and once as a live heap struct, or twice as two heap snapshots taken at different
///         addresses. The two sources fail in different places — a struck ESM copy may have lost its
///         EditorID while the runtime object still holds it, and vice versa — so throwing one away
///         discards evidence the other needs.
///     </para>
///     <para>
///         <b>What this can and cannot fill, and why.</b> Every model here is a C# <c>record</c>, so
///         copying is cheap and safe. But nullability is mixed. A <c>string?</c>, a <c>uint?</c> or
///         an empty collection is unambiguously "the source did not supply this". A non-nullable
///         scalar — <c>WeaponRecord.Health</c>, <c>GlobalRecord.Value</c> — has no unset sentinel:
///         zero is a legitimate value, and treating it as "missing" would let a second capture
///         overwrite a real zero. So scalars are left alone. That is exactly why the hand-written
///         mergers in the record handlers encode <c>!= 0</c> / <c>&gt; float.Epsilon</c> per field:
///         they know, per field, which zeros are real. Where such a merger exists it still runs and
///         still wins; this is the general fallback for the many types that never got one.
///     </para>
/// </summary>
internal static class RecordModelUnion
{
    private static readonly ConcurrentDictionary<Type, TypePlan> Plans = new();

    /// <summary>
    ///     Properties this must not read or fill.
    ///     <para>
    ///         <c>DecodedTree</c> is both: reading it triggers a full schema re-decode (it is lazily
    ///         produced from the record's own <c>Descriptor</c>), so scoring would decode every
    ///         candidate; and filling it would be meaningless, because it is derived from the
    ///         descriptor the copy constructor already carries rather than independent content.
    ///     </para>
    /// </summary>
    private static readonly HashSet<string> DerivedProperties = new(StringComparer.Ordinal)
    {
        "DecodedTree"
    };

    /// <summary>
    ///     How much of a capture is populated. Used to pick which of two captures leads a merge;
    ///     a resolved EditorID counts double because a record without one cannot be named, matched
    ///     to a master, or meaningfully reviewed.
    /// </summary>
    public static int Score(object model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var plan = Plans.GetOrAdd(model.GetType(), BuildPlan);
        var score = 0;
        foreach (var property in plan.Fillable)
        {
            if (!IsUnset(property.GetValue(model)))
            {
                score += string.Equals(property.Name, "EditorId", StringComparison.Ordinal) ? 2 : 1;
            }
        }

        return score;
    }

    /// <summary>
    ///     Return a copy of <paramref name="primary" /> with every unset member taken from
    ///     <paramref name="secondary" />. Returns <paramref name="primary" /> unchanged when the two
    ///     are not the same type, when the type is not a copyable record, or when nothing was
    ///     missing — so a caller can use reference equality to tell whether a merge happened.
    /// </summary>
    public static object Fill(object primary, object secondary)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);

        var type = primary.GetType();
        if (type != secondary.GetType())
        {
            return primary;
        }

        var plan = Plans.GetOrAdd(type, BuildPlan);
        if (plan.CopyConstructor == null)
        {
            return primary;
        }

        // Collect first so a type with nothing to fill costs no allocation at all — the common case
        // once a corpus is mostly clean.
        List<(PropertyInfo Property, object? Value)>? pending = null;
        foreach (var property in plan.Fillable)
        {
            var mine = property.GetValue(primary);
            if (!IsUnset(mine))
            {
                continue;
            }

            var theirs = property.GetValue(secondary);
            if (IsUnset(theirs))
            {
                continue;
            }

            (pending ??= []).Add((property, theirs));
        }

        var offsetFill = ResolveOffset(plan, primary, secondary);
        var endianFill = ResolveBigEndian(plan, primary, secondary);
        if (pending == null && !offsetFill.HasValue && !endianFill)
        {
            return primary;
        }

        // The record copy constructor shallow-copies, so reference members are shared with the
        // original. That is safe here because members are only ever replaced wholesale, never
        // mutated in place — but it is why nothing below reaches into a copied collection.
        var merged = plan.CopyConstructor.Invoke([primary]);
        if (pending != null)
        {
            foreach (var (property, value) in pending)
            {
                property.SetValue(merged, value);
            }
        }

        if (offsetFill.HasValue)
        {
            plan.Offset!.SetValue(merged, offsetFill.Value);
        }

        if (endianFill)
        {
            plan.IsBigEndian!.SetValue(merged, true);
        }

        return merged;
    }

    /// <summary>
    ///     <c>Offset</c> is a capture address, not content: whichever capture has a real one keeps
    ///     it. Every hand-written merger ends with this same rule.
    /// </summary>
    private static long? ResolveOffset(TypePlan plan, object primary, object secondary)
    {
        if (plan.Offset == null || plan.Offset.GetValue(primary) is not long mine || mine != 0)
        {
            return null;
        }

        return plan.Offset.GetValue(secondary) is long theirs && theirs != 0 ? theirs : null;
    }

    /// <summary>
    ///     <c>IsBigEndian</c> is ORed rather than filled — the other hand-written convention. A
    ///     capture that was big-endian stays big-endian even if the copy leading the merge was not.
    /// </summary>
    private static bool ResolveBigEndian(TypePlan plan, object primary, object secondary)
    {
        if (plan.IsBigEndian == null ||
            plan.IsBigEndian.GetValue(primary) is not bool mine || mine)
        {
            return false;
        }

        return plan.IsBigEndian.GetValue(secondary) is true;
    }

    /// <summary>
    ///     "The source did not supply this": null, an empty string, or an empty collection. Never a
    ///     zero scalar — see the class remarks.
    /// </summary>
    private static bool IsUnset(object? value)
    {
        return value switch
        {
            null => true,
            string s => s.Length == 0,
            ICollection c => c.Count == 0,
            _ => false
        };
    }

    private static TypePlan BuildPlan(Type type)
    {
        var fillable = new List<PropertyInfo>();
        PropertyInfo? offset = null;
        PropertyInfo? isBigEndian = null;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0 ||
                DerivedProperties.Contains(property.Name))
            {
                continue;
            }

            if (property is { Name: "Offset", PropertyType.FullName: "System.Int64" })
            {
                offset = property;
                continue;
            }

            if (property is { Name: "IsBigEndian", PropertyType.FullName: "System.Boolean" })
            {
                isBigEndian = property;
                continue;
            }

            // Non-nullable value types have no "unset" state, so a merge could only ever guess.
            if (property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) == null)
            {
                continue;
            }

            fillable.Add(property);
        }

        // Records emit `protected R(R original)`; its absence means this is not a record and there
        // is no safe way to produce a modified copy.
        //
        // The NonPublic bind is the point, not an oversight: the copy constructor the C# compiler
        // synthesises for a record is `protected`, and it is the only supported way to clone one
        // outside the type. The alternative — MemberwiseClone — is itself protected and would skip
        // any hand-written copy logic. Every type reached here is a first-party ESM record model.
#pragma warning disable S3011 // Reflection accessibility bypass — see above.
        var copyConstructor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            [type],
            null);
#pragma warning restore S3011

        return new TypePlan(fillable, copyConstructor, offset, isBigEndian);
    }

    private sealed record TypePlan(
        IReadOnlyList<PropertyInfo> Fillable,
        ConstructorInfo? CopyConstructor,
        PropertyInfo? Offset,
        PropertyInfo? IsBigEndian);
}
