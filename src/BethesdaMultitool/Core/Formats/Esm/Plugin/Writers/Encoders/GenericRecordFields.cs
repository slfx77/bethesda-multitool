using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Field accessors shared by the encoders that emit from <see cref="GenericEsmRecord" />.
///     <para>
///     These records reach the writer from two different producers that key
///     <see cref="GenericEsmRecord.Fields" /> differently, so every lookup has to try both:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <c>RuntimeGenericReader</c> (the live path for every generic-only type) keys by
///                 PDB identifier — <c>"Owner.Name"</c>, e.g. <c>"BGSMovableStatic.pSoundLoop"</c>
///                 (see <c>RuntimeGenericReader.ReadFields</c>).
///             </description>
///         </item>
///         <item>
///             <description>
///                 The ESM carve path and the hand-written specialized readers key by subrecord
///                 signature — e.g. <c>"SNAM"</c> (this is why <c>FlorEncoder</c> can look up
///                 <c>"PFIG"</c> directly).
///             </description>
///         </item>
///     </list>
///     <para>
///     Value shapes also vary by producer. <c>RuntimeGenericReader.ReadPointerField</c> resolves a
///     TESForm pointer to a boxed <see cref="uint" /> FormID, while
///     <c>ReadEmbeddedStruct</c> renders a struct of ≤8 bytes as an uppercase hex
///     <see cref="string" /> and anything larger as a <c>"[TypeName, NB]"</c> descriptor that
///     carries no data — <see cref="TryBytes" /> rejects the latter rather than emitting garbage.
///     </para>
/// </summary>
internal static class GenericRecordFields
{
    /// <summary>
    ///     Look up a field by any of the supplied keys, in order. Pass the subrecord signature
    ///     first and the PDB <c>Owner.Name</c> identifiers after it.
    /// </summary>
    private static object? Find(GenericEsmRecord record, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (record.Fields.TryGetValue(key, out var value) && value is not null)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    ///     Highest load-order byte a captured FormID can legitimately carry at encode time.
    ///     Models reach the encoders before post-emit remapping, so every real reference is
    ///     either master-range (0x00xxxxxx) or allocator-issued plugin-range (0x01xxxxxx).
    /// </summary>
    private const uint MaxFormIdIndex = 0x01;

    /// <summary>
    ///     Resolve a FormID-bearing field. Returns null when absent or zero so callers can omit
    ///     the subrecord entirely rather than writing a null reference the engine would fail to
    ///     resolve.
    ///     <para>
    ///     Values that cannot be FormIDs are rejected: <c>RuntimeGenericReader.ReadPointerField</c>
    ///     falls back to returning the <b>raw Xbox virtual address</b> when a pointer target does
    ///     not resolve to a TESForm (heap ≥ 0x40000000, module space 0x82xxxxxx). Encoding one of
    ///     those as a FormID produces a reference whose load-order byte points at a nonexistent
    ///     131st master — observed as SNAM=0x82339658 on every v138 MSTT before this guard.
    ///     </para>
    /// </summary>
    public static uint? TryFormId(GenericEsmRecord record, params string[] keys)
    {
        var value = Find(record, keys);
        if (value is null)
        {
            return null;
        }

        var formId = value switch
        {
            uint u => u,
            int i and > 0 => (uint)i,
            IReadOnlyDictionary<string, object?> nested => nested.Values.OfType<uint>().FirstOrDefault(),
            _ => 0u
        };

        if (formId == 0 || formId >> 24 > MaxFormIdIndex)
        {
            return null;
        }

        return formId;
    }

    /// <summary>
    ///     True when captured object bounds look like real bounds rather than a misaligned read.
    ///     A legitimate OBND has each minimum ≤ its maximum; a struct read at the wrong offset
    ///     (the v138 MSTT/ASPC failure mode) produces inverted or wildly out-of-range extents.
    ///     Callers should omit OBND entirely when this fails — a missing bound is benign, a
    ///     garbage one is not.
    /// </summary>
    public static bool IsPlausibleBounds(ObjectBounds bounds)
    {
        return bounds.X1 <= bounds.X2
               && bounds.Y1 <= bounds.Y2
               && bounds.Z1 <= bounds.Z2;
    }

    /// <summary>
    ///     Resolve a raw byte payload. Accepts a real byte array, or the uppercase hex string that
    ///     <c>RuntimeGenericReader.ReadEmbeddedStruct</c> produces for structs of ≤8 bytes.
    ///     Descriptor placeholders for larger structs (<c>"[MOVABLE_STATIC_DATA, 16B]"</c>) carry
    ///     no recoverable data and are rejected.
    /// </summary>
    public static byte[]? TryBytes(GenericEsmRecord record, int expectedLength, params string[] keys)
    {
        var value = Find(record, keys);

        var bytes = value switch
        {
            byte[] { Length: > 0 } raw => raw,
            string s when s.Length > 0 && !s.StartsWith('[') && s.Length % 2 == 0 && IsHex(s) =>
                Convert.FromHexString(s),
            _ => null
        };

        if (bytes is null || bytes.Length != expectedLength)
        {
            return null;
        }

        return bytes;
    }

    /// <summary>Resolve an unsigned integer field (PDB uint8/uint16/uint32 all box as their CLR type).</summary>
    public static uint? TryUInt(GenericEsmRecord record, params string[] keys)
    {
        return Find(record, keys) switch
        {
            uint u => u,
            ushort us => us,
            byte b => b,
            int i and >= 0 => (uint)i,
            short s and >= 0 => (uint)s,
            sbyte sb and >= 0 => (uint)sb,
            // PDB kind "bool" boxes as System.Boolean, which matched none of the arms above — so
            // every bool-backed subrecord (ASPC's INAM among them) resolved to null and was
            // emitted as 0 no matter what the capture held.
            bool flag => flag ? 1u : 0u,
            _ => null
        };
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
