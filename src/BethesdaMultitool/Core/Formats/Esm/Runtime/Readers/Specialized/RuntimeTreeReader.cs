using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for <c>TESObjectTREE</c> (TREE, FormType 0x25).
///     <para>
///     TREE cannot come out of the generic PDB reader. Both of its REQUIRED subrecords live in
///     embedded structs larger than 8 bytes, and <c>RuntimeGenericReader.ReadEmbeddedStruct</c>
///     substitutes a literal placeholder string (<c>"[OBJ_TREE, 32B]"</c>) for anything that
///     big rather than walking it — so SNAM and CNAM were unrecoverable and the record could
///     never be emitted. Note the dumps contain NO carved ESM TREE bytes at all (census:
///     <c>esm_record_dumps=0</c>), so the runtime struct is the only source.
///     </para>
///     <para>
///     Layout below is read directly out of <c>Fallout_Release_MemDebug.pdb</c> — the Xbox 360
///     build's own PDB, so the offsets are 360-correct — and then confirmed against xex44.
///     Nothing here is inferred from the PC/NVSE headers.
///     </para>
///     <para>
///     <b><c>TESObjectTREE</c></b> (structSize 164): <c>SeedArray</c>@108, <c>Data</c>@124,
///     <c>BillboardSize</c>@156. All three offsets come from the PDB layout table, so they are
///     resolved by name through <see cref="PdbStructView" /> rather than hard-coded.
///     </para>
///     <para>
///     <b><c>NiTPrimitiveArray&lt;unsigned int&gt;</c></b> (16 bytes) derives from
///     <c>NiTArray&lt;unsigned int, NiTMallocInterface&lt;unsigned int&gt;&gt;</c>
///     (PDB LF_FIELDLIST 0xf92c / LF_CLASS 0xf92d):
///     </para>
///     <list type="table">
///         <item><description><c>+0</c> vfptr (LF_VFUNCTAB — the vtable IS present on the 360 build)</description></item>
///         <item><description><c>+4</c> <c>unsigned int* m_pBase</c> — the seed payload</description></item>
///         <item><description><c>+8</c> <c>uint16 m_usMaxSize</c> — allocated capacity (<c>GetAllocatedSize</c>)</description></item>
///         <item><description><c>+10</c> <c>uint16 m_usSize</c> — element count (<c>GetSize</c>) ← authoritative</description></item>
///         <item><description><c>+12</c> <c>uint16 m_usESize</c> — effective/non-null count (<c>GetEffectiveSize</c>)</description></item>
///         <item><description><c>+14</c> <c>uint16 m_usGrowBy</c></description></item>
///     </list>
///     <para>
///     Verified on xex44: <c>WhiteOak01 (0x0003C356)</c> reports maxSize/size/eSize = 5 and its
///     payload at VA 0x5105B540 reads 301409, 363767, 554776, 603335, 844198 — byte-identical
///     to the SNAM of the same record in <c>Sample/ESM/360_proto/FalloutNV.esm</c>. Every other
///     TREE in the dump reports size 1. The bytes immediately following WhiteOak01's fifth seed
///     are an unrelated heap string, so the count must be honoured exactly; over-reading yields
///     garbage seeds, not zeros.
///     </para>
/// </summary>
internal sealed class RuntimeTreeReader(RuntimeMemoryContext context)
{
    private const byte TreeFormType = 0x25;

    /// <summary>Field offsets within <c>NiTArray</c>, per the PDB field list.</summary>
    private const int ArrayBasePointer = 4;
    private const int ArraySizeField = 10;

    /// <summary>
    ///     Refuse absurd counts. <c>m_usSize</c> is a uint16, so a corrupt capture could ask for
    ///     65,535 seeds; the largest real value in the corpus is 5.
    /// </summary>
    private const int MaxSeeds = 256;

    private readonly RuntimeMemoryContext _context = context;
    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>Reads one runtime tree, or null when the struct can't be read.</summary>
    public TreeRecord? ReadRuntimeTree(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != TreeFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, TreeFormType);
        if (view is null)
        {
            return null;
        }

        return new TreeRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            Bounds = view.Bounds(),
            ModelPath = view.BsString("cModel", "TESModel"),
            // TESTexture.TextureName is the leaf/billboard texture — the ICON subrecord.
            IconPath = view.BsString("TextureName", "TESTexture"),
            Seeds = ReadSeeds(view),
            Data = ReadTreeData(view),
            BillboardSize = ReadBillboardSize(view),
            // MODT has no runtime counterpart: TESModel keeps a live TextureList, not the
            // packed on-disk blob. Left null so TreeEncoder simply omits the subrecord —
            // none of the 10 proto TREE records carries MODT either.
            ModelTextureData = null,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Walks <c>SeedArray</c>: reads the count in place, then follows <c>m_pBase</c> into
    ///     the heap for the payload. Returns null when the array can't be resolved so the
    ///     encoder omits SNAM rather than writing an invented seed.
    /// </summary>
    private uint[]? ReadSeeds(PdbStructView view)
    {
        if (view.Offset("SeedArray", "TESObjectTREE") is not { } arrayOffset
            || arrayOffset + 16 > view.Buffer.Length)
        {
            return null;
        }

        var basePointer = RuntimePdbFieldAccessor.ReadUInt32(view.Buffer, arrayOffset + ArrayBasePointer);
        var count = RuntimePdbFieldAccessor.ReadUInt16(view.Buffer, arrayOffset + ArraySizeField);

        if (count == 0 || count > MaxSeeds || !_context.IsValidPointer(basePointer))
        {
            return null;
        }

        var payload = _context.ReadBytesAtVa(basePointer, count * 4);
        if (payload is null || payload.Length < count * 4)
        {
            return null;
        }

        var seeds = new uint[count];
        for (var i = 0; i < count; i++)
        {
            seeds[i] = BinaryUtils.ReadUInt32BE(payload, i * 4);
        }

        return seeds;
    }

    /// <summary>Reads the 32-byte <c>OBJ_TREE</c> struct (CNAM).</summary>
    private static TreeData? ReadTreeData(PdbStructView view)
    {
        if (view.Offset("Data", "TESObjectTREE") is not { } offset
            || offset + 32 > view.Buffer.Length)
        {
            return null;
        }

        return new TreeData
        {
            LeafCurvature = RuntimePdbFieldAccessor.ReadFloat(view.Buffer, offset),
            MinLeafAngle = RuntimePdbFieldAccessor.ReadFloat(view.Buffer, offset + 4),
            MaxLeafAngle = RuntimePdbFieldAccessor.ReadFloat(view.Buffer, offset + 8),
            BranchDimmingValue = RuntimePdbFieldAccessor.ReadFloat(view.Buffer, offset + 12),
            LeafDimmingValue = RuntimePdbFieldAccessor.ReadFloat(view.Buffer, offset + 16),
            // int32, NOT float — see TreeData.ShadowRadius.
            ShadowRadius = RuntimePdbFieldAccessor.ReadInt32(view.Buffer, offset + 20),
            RockSpeed = RuntimePdbFieldAccessor.ReadFloat(view.Buffer, offset + 24),
            RustleSpeed = RuntimePdbFieldAccessor.ReadFloat(view.Buffer, offset + 28)
        };
    }

    /// <summary>Reads the 8-byte <c>NiPoint2</c> billboard size (BNAM).</summary>
    private static TreeBillboardSize? ReadBillboardSize(PdbStructView view)
    {
        if (view.Offset("BillboardSize", "TESObjectTREE") is not { } offset
            || offset + 8 > view.Buffer.Length)
        {
            return null;
        }

        return new TreeBillboardSize
        {
            Width = RuntimePdbFieldAccessor.ReadFloat(view.Buffer, offset),
            Height = RuntimePdbFieldAccessor.ReadFloat(view.Buffer, offset + 4)
        };
    }
}
