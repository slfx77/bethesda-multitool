using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Field accessors shared by the encoders that emit from <see cref="GenericEsmRecord" />.
///     <para>
///         These records reach the writer from two different producers that key
///         <see cref="GenericEsmRecord.Fields" /> differently, so every lookup has to try both:
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
///         Value shapes also vary by producer. <c>RuntimeGenericReader.ReadPointerField</c> resolves a
///         TESForm pointer to a boxed <see cref="uint" /> FormID, while
///         <c>ReadEmbeddedStruct</c> renders a struct of ≤8 bytes as an uppercase hex
///         <see cref="string" /> and anything larger as the raw big-endian <see cref="byte" />
///         array — <see cref="TryBytes" /> accepts both shapes.
///     </para>
/// </summary>
internal static class GenericRecordFields
{
    /// <summary>
    ///     Highest load-order byte a captured FormID can legitimately carry at encode time.
    ///     Models reach the encoders before post-emit remapping, so every real reference is
    ///     either master-range (0x00xxxxxx) or allocator-issued plugin-range (0x01xxxxxx).
    /// </summary>
    private const uint MaxFormIdIndex = 0x01;

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
    ///     Resolve a FormID-bearing field. Returns null when absent or zero so callers can omit
    ///     the subrecord entirely rather than writing a null reference the engine would fail to
    ///     resolve.
    ///     <para>
    ///         Values that cannot be FormIDs are rejected: <c>RuntimeGenericReader.ReadPointerField</c>
    ///         falls back to returning the <b>raw Xbox virtual address</b> when a pointer target does
    ///         not resolve to a TESForm (heap ≥ 0x40000000, module space 0x82xxxxxx). Encoding one of
    ///         those as a FormID produces a reference whose load-order byte points at a nonexistent
    ///         131st master — observed as SNAM=0x82339658 on every v138 MSTT before this guard.
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
    ///     Resolve a FormID list produced by <c>RuntimeContainerFieldReader</c> from a walked
    ///     <c>BSSimpleList</c> or counted pointer array. Applies the same load-order-index guard as
    ///     <see cref="TryFormId" /> to every element, and returns null rather than a partially
    ///     filtered list — a list subrecord whose length no longer matches its declared count is
    ///     worse than an omitted one.
    /// </summary>
    public static IReadOnlyList<uint>? TryFormIdList(GenericEsmRecord record, params string[] keys)
    {
        if (Find(record, keys) is not IReadOnlyList<uint> { Count: > 0 } list)
        {
            return null;
        }

        foreach (var formId in list)
        {
            if (formId == 0 || formId >> 24 > MaxFormIdIndex)
            {
                return null;
            }
        }

        return list;
    }

    /// <summary>
    ///     Resolve a <b>positional</b> FormID slot table — DOBJ's 34 default objects, IPDS's 12
    ///     material impacts — where index carries the meaning and an empty slot is a legitimate NULL
    ///     that xEdit's schema allows. Unlike <see cref="TryFormIdList" />, zeros are kept rather
    ///     than treated as corruption; a non-zero entry with an impossible load-order index still
    ///     rejects the whole table, since that means the read was misaligned.
    /// </summary>
    public static IReadOnlyList<uint>? TryFormIdSlots(
        GenericEsmRecord record, int expectedSlots, params string[] keys)
    {
        if (Find(record, keys) is not IReadOnlyList<uint> list || list.Count != expectedSlots)
        {
            return null;
        }

        foreach (var formId in list)
        {
            if (formId != 0 && formId >> 24 > MaxFormIdIndex)
            {
                return null;
            }
        }

        return list;
    }

    /// <summary>
    ///     Resolve a walked <c>MODS</c> alternate-texture list.
    ///     <para>
    ///         All-or-nothing on the texture set: every entry names a TXST, and an entry whose
    ///         pointer did not resolve carries FormID 0. Writing that would leave the engine with a
    ///         swap that names a shape but no replacement, so one unresolved entry drops the whole
    ///         subrecord rather than silently changing which shapes get swapped.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<AlternateTextureEntry>? TryAlternateTextures(
        GenericEsmRecord record, params string[] keys)
    {
        if (Find(record, keys) is not IReadOnlyList<AlternateTextureEntry> { Count: > 0 } list)
        {
            return null;
        }

        foreach (var entry in list)
        {
            if (entry.TextureSetFormId == 0 ||
                entry.TextureSetFormId >> 24 > MaxFormIdIndex ||
                string.IsNullOrEmpty(entry.ShapeName))
            {
                return null;
            }
        }

        return list;
    }

    /// <summary>
    ///     Resolve a walked LSCR <c>LNAM</c> location list, dropping entries whose FormIDs could not
    ///     have come from a real reference. Unlike the alternate textures above these are
    ///     independent of one another — LNAM repeats, one subrecord per location — so a bad entry
    ///     costs only itself.
    /// </summary>
    public static IReadOnlyList<LoadScreenLocationEntry>? TryLoadScreenLocations(
        GenericEsmRecord record, params string[] keys)
    {
        if (Find(record, keys) is not IReadOnlyList<LoadScreenLocationEntry> { Count: > 0 } list)
        {
            return null;
        }

        var kept = new List<LoadScreenLocationEntry>(list.Count);
        foreach (var entry in list)
        {
            if (entry.DirectFormId >> 24 > MaxFormIdIndex ||
                entry.IndirectWorldspaceFormId >> 24 > MaxFormIdIndex)
            {
                continue;
            }

            kept.Add(entry);
        }

        return kept.Count > 0 ? kept : null;
    }

    /// <summary>
    ///     Resolve a walked destruction block. Stages whose explosion or debris reference is
    ///     impossible are rejected wholesale rather than blanked, because a stage's index is its
    ///     position and dropping one renumbers every stage after it.
    /// </summary>
    public static DestructionData? TryDestruction(GenericEsmRecord record, params string[] keys)
    {
        if (Find(record, keys) is not DestructionData destruction)
        {
            return null;
        }

        foreach (var stage in destruction.Stages)
        {
            if (stage.ExplosionFormId >> 24 > MaxFormIdIndex ||
                stage.DebrisFormId >> 24 > MaxFormIdIndex)
            {
                return null;
            }
        }

        return destruction;
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
    ///     Resolve a raw byte payload. Accepts a real byte array — which is what
    ///     <c>RuntimeGenericReader.ReadEmbeddedStruct</c> now returns for any struct larger than
    ///     8 bytes, and what the ESM carve path stores for an unschematized subrecord — or the
    ///     uppercase hex string that same reader still produces for structs of ≤8 bytes.
    ///     <para>
    ///         Strings beginning with <c>'['</c> are rejected: that is the shape of the old
    ///         <c>"[MOVABLE_STATIC_DATA, 16B]"</c> descriptor placeholder, which carried no data.
    ///         The guard stays so a stale capture or a hand-built record cannot smuggle one in.
    ///     </para>
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
        return Unwrap(Find(record, keys)) switch
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

    /// <summary>
    ///     Resolve a text field. <c>RuntimeGenericReader.ReadEmbeddedStruct</c> resolves a
    ///     <c>BSStringT&lt;char&gt;</c> member to the real string, and the ESM carve path stores the
    ///     null-terminated text of a known string subrecord (ICON/DESC/...) the same way, so both
    ///     producers land on <see cref="string" />.
    ///     <para>
    ///         Returns null for an empty or whitespace-only value so callers omit the subrecord
    ///         rather than writing a bare null terminator, and rejects the leading-<c>'['</c>
    ///         descriptor shape for the same reason <see cref="TryBytes" /> does.
    ///     </para>
    /// </summary>
    public static string? TryString(GenericEsmRecord record, params string[] keys)
    {
        return Find(record, keys) switch
        {
            string s when !string.IsNullOrWhiteSpace(s) && !s.StartsWith('[') => s,
            _ => null
        };
    }

    /// <summary>
    ///     Resolve a floating-point field. <c>RuntimeGenericReader.ReadValidatedFloat</c> already
    ///     rejects non-finite and subnormal reads, so anything that arrives here as a
    ///     <see cref="float" /> is a plausible captured value; non-finite values are re-checked
    ///     anyway because the ESM carve path does not apply that filter.
    /// </summary>
    public static float? TryFloat(GenericEsmRecord record, params string[] keys)
    {
        var value = Unwrap(Find(record, keys)) switch
        {
            float f => f,
            double d => (float)d,
            _ => (float?)null
        };

        return value is { } v && float.IsFinite(v) ? v : null;
    }

    /// <summary>
    ///     Unwrap the single-field decode dictionary the ESM carve path stores for a scalar
    ///     subrecord. <c>MiscRecordHandler.ParseGenericRecords</c> runs every recognized subrecord
    ///     through <c>SubrecordSchemaView</c>, so a one-field schema such as IDLF's
    ///     <c>UInt8("Flags")</c> or IDLT's <c>Simple4Byte</c> arrives boxed inside a dictionary
    ///     rather than as a bare number. Only a dictionary holding exactly one entry is unwrapped:
    ///     with two or more there is no way to tell which field the caller meant, and guessing is
    ///     how a wrong value reaches the plugin.
    /// </summary>
    private static object? Unwrap(object? value)
    {
        return value is IReadOnlyDictionary<string, object?> { Count: 1 } single
            ? single.Values.First()
            : value;
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
