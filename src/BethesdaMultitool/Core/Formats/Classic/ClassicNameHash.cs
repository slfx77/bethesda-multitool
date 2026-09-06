using System.Text;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     A stable index for a classic-game record whose only identity is a NAME — a loose file stem
///     such as Redguard's <c>maps\ISLAND.RGM</c>, which no table numbers. <see cref="ClassicFormIdScheme" />
///     wants source identity rather than enumeration order, so that two installs diff sensibly;
///     for name-keyed sources the name itself is that identity, hashed to the width the caller
///     has left in the 24-bit index. FNV-1a over the upper-cased ASCII bytes, so case differences
///     between installs do not change the id.
///     <para>
///         A hash can collide, so a record source using this must check uniqueness over the set it
///         actually produced and fail loudly — a retail test pins that no two of its records share
///         a FormID. It must never silently renumber, which would defeat the point.
///     </para>
/// </summary>
internal static class ClassicNameHash
{
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>Hashes <paramref name="name" /> (case-insensitively) to its low <paramref name="bits" /> bits.</summary>
    public static uint Of(string name, int bits)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(bits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bits, 24);

        var hash = FnvOffset;
        foreach (var b in Encoding.ASCII.GetBytes(name.ToUpperInvariant()))
        {
            hash = (hash ^ b) * FnvPrime;
        }

        // Fold the high bits down so the whole 32-bit hash contributes to a narrow index.
        hash ^= hash >> 16;
        return hash & ((1u << bits) - 1);
    }
}
