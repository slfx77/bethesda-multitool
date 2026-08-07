using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for <c>BGSPlaceableWater</c> (PWAT, FormType 0x23).
///     <para>
///     PWAT's parent-water reference cannot come out of the generic reader.
///     <c>BGSPlaceableWater.Data</c> is an 8-byte embedded struct, and
///     <c>RuntimeGenericReader.ReadEmbeddedStruct</c> hex-encodes structs that small
///     instead of walking them — so the pointer inside is never followed and the field
///     surfaces as a raw Xbox VA. Only a typed read can resolve it.
///     </para>
///     <para>
///     Layout of <c>BGSPlaceableWaterData</c>, established empirically with
///     <c>dmp struct-layout</c> against xex21 and not from the PDB (which records the
///     struct only as an opaque 8 bytes at offset 88):
///     </para>
///     <list type="bullet">
///         <item><description><c>+0</c> (struct 88): <c>uint32</c> flags — the DNAM flag word.</description></item>
///         <item><description><c>+4</c> (struct 92): <c>TESWaterForm*</c> — the parent WATR.</description></item>
///     </list>
///     <para>
///     This is the SAME order the on-disk DNAM uses — <c>{ uint32 Flags, FormID Water }</c>
///     per xEdit <c>wbRecord(PWAT)</c>. (An earlier revision of this comment claimed DNAM was
///     the reverse, and <c>PwatEncoder</c> was written to match, which shipped all 48 PWATs
///     with the two words transposed.) Verified on the first 10 PWATs in xex21: every one
///     resolved a type-checked WATR at +4 with a semantically matching name
///     (<c>PoolWastelandWater01</c> → <c>WastelandMuckPool01</c>,
///     <c>CreekWater2048x2048</c> → <c>CreekWater01</c>, <c>WaterDirty</c> →
///     <c>WaterTypeDirty</c>). Bit 28 ("Depth") is set on most but not all captured flag
///     words — 42 of 48 in xex21 — so do not treat it as an invariant.
///     </para>
///     <para>
///     Without this reader PWAT emits from neither pipeline — it has no typed producer, so
///     <see cref="PlaceableWaterRecord" /> was never constructed anywhere and
///     <c>PwatEncoder</c> sat registered but unreachable. Placed refs on a proto-only PWAT
///     base therefore dropped as <c>refr.dangling-base</c>.
///     </para>
/// </summary>
internal sealed class RuntimePlaceableWaterReader(RuntimeMemoryContext context)
{
    private const byte PwatFormType = 0x23;
    private const byte WatrFormType = 0x4E;

    /// <summary>Offset of the water pointer within <c>BGSPlaceableWater.Data</c>.</summary>
    private const int WaterPointerWithinData = 4;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>Reads one runtime placeable water, or null when the struct can't be read.</summary>
    public PlaceableWaterRecord? ReadRuntimePlaceableWater(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != PwatFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, PwatFormType);
        if (view?.Offset("Data", "BGSPlaceableWater") is not { } dataOffset)
        {
            return null;
        }

        // Flags occupy Data+0. A short buffer means the capture is truncated — treat the
        // whole read as failed rather than emitting a record with invented flags.
        if (dataOffset + 8 > view.Buffer.Length)
        {
            return null;
        }

        var waterFormId = _fields.ReadPointerToFormId(
            view.Buffer, dataOffset + WaterPointerWithinData, WatrFormType);

        return new PlaceableWaterRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            ModelPath = view.BsString("cModel", "TESModel"),
            Bounds = view.Bounds(),
            // Null when the pointer didn't resolve to a WATR — PwatEncoder writes DNAM's
            // water slot as 0 in that case, which is the honest "not recovered" value.
            WaterFormId = waterFormId ?? 0u,
            Flags = RuntimePdbFieldAccessor.ReadUInt32(view.Buffer, dataOffset),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
