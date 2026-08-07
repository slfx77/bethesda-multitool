using System.Text;
using static BethesdaMultitool.Tests.Helpers.BinaryTestWriter;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Builds synthetic byte buffers that mimic Xbox 360 runtime struct layouts.
///     Each builder writes a TESForm-derived struct at offset 0 of a fresh
///     <see cref="byte" /> array, using the PDB-resolved runtime offsets that the
///     production reader will look up. Tests assert exact reads via
///     <see cref="RuntimeReaderTestFixture" />.
///     <para>
///         Per the test-discipline standard ([feedback_test_discipline]): no real
///         DMP data; no rate floors; tests assert exact values. Builders pin the
///         offset constants — if a production reader's offset changes, the test
///         must be updated alongside it.
///     </para>
///     <para>
///         Primitive byte writers (WriteUInt32BE, WriteInt32BE, WriteFloatBE,
///         WriteUInt16BE) live in <see cref="BinaryTestWriter" /> and are
///         re-exported here via <c>using static</c> for builder-call ergonomics.
///     </para>
/// </summary>
internal static class SyntheticStructFactory
{
    public const uint DefaultVtable = 0x82010000u;

    /// <summary>
    ///     Writes a 16-byte TESForm header at <paramref name="offset" />:
    ///     vtable @ +0, cFormType @ +4, iFormID @ +12. The intervening bytes
    ///     (formFlags @ +8) are left zero — readers don't gate on them.
    /// </summary>
    public static void WriteFormHeader(byte[] buffer, int offset, byte formType, uint formId,
        uint vtable = DefaultVtable)
    {
        WriteUInt32BE(buffer, offset, vtable);
        buffer[offset + 4] = formType;
        WriteUInt32BE(buffer, offset + 12, formId);
    }

    /// <summary>
    ///     Writes a BSStringT inline-pointer struct at <paramref name="offset" />:
    ///     [4 bytes char* BE][2 bytes length BE][2 bytes unused]. Caller is
    ///     responsible for placing the string data at <paramref name="stringDataVa" />
    ///     via <see cref="RuntimeReaderTestFixture.WithPointerTarget" />.
    /// </summary>
    public static void WriteBsString(byte[] buffer, int offset, uint stringDataVa, ushort length)
    {
        WriteUInt32BE(buffer, offset, stringDataVa);
        WriteUInt16BE(buffer, offset + 4, length);
    }

    /// <summary>
    ///     Returns a null-terminated ASCII byte array suitable for placement at
    ///     a target VA via <c>WithPointerTarget</c>.
    /// </summary>
    public static byte[] AsciiBytes(string text)
    {
        return Encoding.ASCII.GetBytes(text + '\0');
    }

    // =========================================================================
    // Per-record builders — write a TESForm-derived struct into a fresh buffer.
    // Field offsets are the RUNTIME offsets the production reader looks up
    // (post-shift where applicable). Constants are pinned here so a reader
    // change forces a test update.
    // =========================================================================

    /// <summary>
    ///     Builds a synthetic TESNPC (FormType 0x2A) at buffer offset 0.
    ///     Field offsets reflect the empirically-validated runtime layout
    ///     (per Tier 1B.10 / Phase 5.1 anchors): core-region +16 baseline
    ///     shift baked into the TESActorBaseData pointers; appearance region
    ///     uses <paramref name="appearanceShift" />.
    /// </summary>
    public static byte[] BuildNpc(
        uint formId,
        uint? racePtr = null,
        uint? voiceTypePtr = null,
        uint? combatStylePtr = null,
        int appearanceShift = 16,
        int bufferSize = 0x400)
    {
        var buf = new byte[bufferSize];
        WriteFormHeader(buf, 0, 0x2A, formId);

        // TESActorBaseData pointers (core region). Runtime offsets confirmed
        // at 100% pointer-shape across all 8 snippets in Phase 1B.10.
        if (racePtr.HasValue) WriteUInt32BE(buf, 272 + 16, racePtr.Value);
        if (voiceTypePtr.HasValue) WriteUInt32BE(buf, 80 + 16, voiceTypePtr.Value);

        // CombatStyle lives in the appearance region — Phase 5.1 anchor
        // confirms 468 + appearanceShift across snippets (Debug uses
        // appearanceShift=0; Release/MemDebug use 16).
        if (combatStylePtr.HasValue) WriteUInt32BE(buf, 468 + appearanceShift, combatStylePtr.Value);

        return buf;
    }

    /// <summary>
    ///     Builds a synthetic TESObjectREFR / ACHR / ACRE (FormType 0x3A-0x3C).
    ///     Runtime offsets per Phase 1B.11 anchors (100% across all snippets).
    ///     <paramref name="refrShift" /> is 0 on builds where TESChildCell is
    ///     8 bytes (vtable + data) and -4 on builds where TESChildCell is
    ///     4 bytes (vtable only). Production discovers this via
    ///     <c>RuntimeRefrReader.ProbeIsEarlyBuild</c>.
    /// </summary>
    public static byte[] BuildRefr(
        uint formId,
        byte formType = 0x3A,
        uint? baseObjectPtr = null,
        uint? parentCellPtr = null,
        uint? extraListPtr = null,
        int refrShift = 0,
        int bufferSize = 0x100)
    {
        var buf = new byte[bufferSize];
        WriteFormHeader(buf, 0, formType, formId);
        if (baseObjectPtr.HasValue) WriteUInt32BE(buf, 48 + refrShift, baseObjectPtr.Value);
        if (parentCellPtr.HasValue) WriteUInt32BE(buf, 80 + refrShift, parentCellPtr.Value);
        if (extraListPtr.HasValue) WriteUInt32BE(buf, 88 + refrShift, extraListPtr.Value);
        return buf;
    }

    /// <summary>
    ///     Builds a synthetic TESObjectAMMO (FormType 0x29) at buffer offset 0. Value sits at the stable
    ///     +140, while the AMMO_DATA sub-block (fSpeed / iFlags @+4 / pProjectile @+8) is placed at the
    ///     caller-supplied <paramref name="ammoDataOffset" /> — that block drifts per build (172/184/188),
    ///     so the production reader discovers it via <c>RuntimeAmmoDataProbe</c>.
    /// </summary>
    public static byte[] BuildAmmo(
        uint formId,
        int ammoDataOffset,
        float speed = 1000f,
        uint flags = 1,
        uint projectilePtr = 0,
        int value = 25,
        int bufferSize = 0x200)
    {
        var buf = new byte[bufferSize];
        WriteFormHeader(buf, 0, 0x29, formId);
        WriteInt32BE(buf, 140, value); // AmmoValueOffset (stable across builds)
        WriteFloatBE(buf, ammoDataOffset, speed);
        WriteUInt32BE(buf, ammoDataOffset + 4, flags);
        WriteUInt32BE(buf, ammoDataOffset + 8, projectilePtr);
        return buf;
    }

    /// <summary>
    ///     Builds a synthetic BGSAcousticSpace (FormType 0x0E) using explicit offsets, because
    ///     ASPC's layout differs per captured build by which trailing members exist rather than by
    ///     a uniform shift — see <c>RuntimeAcousticSpaceLayout</c>. Callers pass the offsets of the
    ///     era they are exercising so a production layout change forces a test update.
    /// </summary>
    /// <param name="soundOffsets">Sound-slot offsets for the era, in Dawn/Noon/Dusk/Night/Walla order.</param>
    /// <param name="soundVas">Parallel VAs to write; 0 leaves the slot NULL. Shorter arrays leave the rest NULL.</param>
    public static byte[] BuildAspc(
        uint formId,
        IReadOnlyList<int> soundOffsets,
        IReadOnlyList<uint> soundVas,
        int regionOffset,
        uint regionVa,
        int envTypeOffset,
        uint envType,
        int bufferSize = 0x200)
    {
        var buf = new byte[bufferSize];
        WriteFormHeader(buf, 0, 0x0E, formId);

        for (var i = 0; i < soundOffsets.Count && i < soundVas.Count; i++)
        {
            WriteUInt32BE(buf, soundOffsets[i], soundVas[i]);
        }

        WriteUInt32BE(buf, regionOffset, regionVa);
        WriteUInt32BE(buf, envTypeOffset, envType);
        return buf;
    }

    /// <summary>
    ///     Builds a synthetic TESObjectWEAP (FormType 0x28). PDB-aligned
    ///     core region (+16 build shift baked in). Per Phase 1B.11 anchors.
    /// </summary>
    public static byte[] BuildWeap(
        uint formId,
        uint? ammoPtr = null,
        uint? pickupSoundPtr = null,
        int bufferSize = 1024)
    {
        // TESObjectWEAP struct is 908 + _s(16) = 924 bytes — buffer must be at
        // least that for the reader's ReadArray call to succeed.
        var buf = new byte[bufferSize];
        WriteFormHeader(buf, 0, 0x28, formId);
        if (ammoPtr.HasValue) WriteUInt32BE(buf, 168 + 16, ammoPtr.Value);
        if (pickupSoundPtr.HasValue) WriteUInt32BE(buf, 236 + 16, pickupSoundPtr.Value);
        return buf;
    }
}