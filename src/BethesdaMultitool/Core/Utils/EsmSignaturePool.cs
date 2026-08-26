using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace BethesdaMultitool.Core.Utils;

/// <summary>
///     One shared instance per distinct 4-byte record/subrecord signature.
///     <para>
///         A plugin holds a couple of hundred distinct signatures and millions of records that carry
///         them. Fallout 76's <c>SeventySix.esm</c> alone has 5.1M REFR records, and every one was
///         allocating its own 4-character <c>"REFR"</c> — a 32-byte string each, retained for the
///         lifetime of the load and often two or three times over, because a decoded node stores the
///         signature as both its Label and its Signature.
///     </para>
///     <para>
///         The <em>sub</em>record iterator already pooled its own signatures for exactly this
///         reason; record headers, GRUP labels and the descriptor scanner each kept allocating
///         fresh ones. This is that pool, lifted out so all of them share a single table.
///     </para>
///     <para>
///         <b>Deliberately limited to bytes 0x00–0x7F.</b> Over that range
///         <c>Encoding.ASCII.GetString</c> and a plain <c>(char)b</c> cast agree exactly, which is
///         what makes interning provably behaviour-neutral for every existing caller — and the two
///         callers did NOT agree above it (ASCII substitutes <c>'?'</c>, the cast yields the
///         Latin-1 character). Corrupt or misaligned data therefore falls through to whatever each
///         caller did before, and cannot fill the table either.
///     </para>
/// </summary>
internal static class EsmSignaturePool
{
    /// <summary>
    ///     Distinct signatures a well-formed plugin uses is ~200. The cap exists only so that
    ///     malformed data — which can present arbitrary 4-byte runs as signatures — cannot grow the
    ///     table into a leak; past it, callers simply get an un-pooled string.
    /// </summary>
    public const int MaxPooledSignatures = 4096;

    private static readonly ConcurrentDictionary<uint, string> Pool = new();

    /// <summary>Distinct signatures currently pooled. Diagnostics and tests.</summary>
    public static int Count => Pool.Count;

    /// <summary>
    ///     Returns the shared instance for <paramref name="signature" />'s first four bytes, or
    ///     false when they are not all plain ASCII (see the class remarks) or there are fewer than
    ///     four. A false return is not an error — the caller falls back to its own conversion.
    /// </summary>
    public static bool TryIntern(ReadOnlySpan<byte> signature, [NotNullWhen(true)] out string? interned)
    {
        interned = null;
        if (signature.Length < 4)
        {
            return false;
        }

        return TryIntern(signature[0], signature[1], signature[2], signature[3], out interned);
    }

    /// <summary>
    ///     Byte-wise overload for callers that have already un-swapped a big-endian signature and
    ///     would otherwise have to build a span to ask.
    /// </summary>
    public static bool TryIntern(byte b0, byte b1, byte b2, byte b3, [NotNullWhen(true)] out string? interned)
    {
        interned = null;
        if (((b0 | b1 | b2 | b3) & 0x80) != 0)
        {
            return false; // not plain ASCII — the two conversions disagree here, so do not pool
        }

        var key = (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
        if (Pool.TryGetValue(key, out var pooled))
        {
            interned = pooled;
            return true;
        }

        var created = new string([(char)b0, (char)b1, (char)b2, (char)b3]);
        if (Pool.Count < MaxPooledSignatures)
        {
            Pool.TryAdd(key, created);
        }

        // Returned pooled-or-not: a caller past the cap still gets a correct string, just not a
        // shared one. Returning false here instead would make the caller build a SECOND copy.
        interned = created;
        return true;
    }
}
