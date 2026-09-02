namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthetic FormID namespacing for classic-game records, mirroring the role
///     <c>Tes3FormIdScheme</c> plays for Morrowind: the classics have no FormIDs, but the whole
///     downstream pipeline (stats/list/show/diff, the resolver, the GUI Records tab) keys on them.
///     <para>
///         Layout: <c>(domain &lt;&lt; 24) | stableIndex</c>. The domain byte is a per-content-category
///         namespace each game's record source declares beside its synthesizer (e.g. Fallout PRO
///         items vs critters vs MSG files); the 24-bit index derives from SOURCE IDENTITY — a PRO
///         id, an LST line number, a MAPS location index — NEVER enumeration order, so <c>diff</c>
///         between two installs/patches of the same game compares like with like.
///     </para>
///     <para>
///         Domain 0x00 is rejected (its ids collide with genuine low FormIDs in mixed displays) and
///         0xFF is reserved (the TES3 shared-namespace convention — nothing classic merges across
///         plugins, but never contradict it).
///     </para>
/// </summary>
public static class ClassicFormIdScheme
{
    /// <summary>Largest encodable stable index (24 bits).</summary>
    public const uint MaxIndex = 0x00FF_FFFF;

    /// <summary>Composes a synthetic FormID from a domain byte and a stable 24-bit index.</summary>
    public static uint Compose(byte domain, uint stableIndex)
    {
        if (domain is 0x00 or 0xFF)
        {
            throw new ArgumentOutOfRangeException(nameof(domain), domain,
                "Classic FormID domains 0x00 (low-FormID collision) and 0xFF (TES3 shared namespace) are reserved.");
        }

        if (stableIndex > MaxIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(stableIndex), stableIndex,
                $"Classic FormID stable index exceeds 24 bits (max 0x{MaxIndex:X6}).");
        }

        return ((uint)domain << 24) | stableIndex;
    }

    /// <summary>The domain byte of a synthetic FormID.</summary>
    public static byte DomainOf(uint formId)
    {
        return (byte)(formId >> 24);
    }

    /// <summary>The 24-bit stable index of a synthetic FormID.</summary>
    public static uint IndexOf(uint formId)
    {
        return formId & MaxIndex;
    }
}
