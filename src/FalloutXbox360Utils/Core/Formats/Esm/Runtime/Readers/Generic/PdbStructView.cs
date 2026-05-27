using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

/// <summary>
///     Typed view over a single loaded PDB-described runtime struct. Pairs with
///     <see cref="SubrecordSchemaView" /> on the ESM-byte side: callers look up fields by
///     <c>(name, owner)</c> rather than by hardcoded offsets, eliminating the drift risk
///     of <c>private const int FormIdOffset = 12;</c> style readers.
///     <para>
///         Open via <see cref="RuntimePdbFieldAccessor.OpenStructView" /> — that factory
///         performs the canonical FormID/FormType validation against the
///         <see cref="RuntimeEditorIdEntry" /> and returns null on mismatch, matching the
///         existing guard in <c>ReadStruct</c>.
///     </para>
/// </summary>
internal sealed class PdbStructView
{
    private readonly RuntimePdbFieldAccessor _accessor;
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimeEditorIdEntry? _entry;
    private Dictionary<string, int>? _ownerShifts;
    private List<(int MinOffset, int MaxOffset, int Shift)>? _shifts;

    internal PdbStructView(
        RuntimePdbFieldAccessor accessor,
        RuntimeMemoryContext context,
        PdbTypeLayout layout,
        byte[] buffer,
        long fileOffset,
        RuntimeEditorIdEntry? entry)
    {
        _accessor = accessor;
        _context = context;
        _entry = entry;
        Layout = layout;
        Buffer = buffer;
        FileOffset = fileOffset;
    }

    public byte[] Buffer { get; }
    public PdbTypeLayout Layout { get; }
    public long FileOffset { get; }

    /// <summary>
    ///     Register a build-specific offset shift for fields whose PDB-declared offset
    ///     falls in <c>[minOffset, maxOffset]</c>. Used by readers that consume live
    ///     probe results (Race / Effect / NPC / WorldCell) where the runtime struct
    ///     layout deviates from the PDB-declared one in known offset bands. Shifts
    ///     apply to every accessor (Int32/UInt32/Float/BsString/FormIdPointer/list
    ///     walkers/Offset/Bounds), so callers don't need to add the shift at each
    ///     call site.
    ///     <para>
    ///         Multiple bands may be registered; the FIRST matching range wins.
    ///         Register narrower/later-band ranges before wider ones if they overlap.
    ///     </para>
    ///     <para>Returns <c>this</c> for fluent chaining.</para>
    /// </summary>
    public PdbStructView WithShift(int minOffset, int maxOffset, int shift)
    {
        if (shift == 0)
        {
            return this; // identity, no point storing
        }

        _shifts ??= [];
        _shifts.Add((minOffset, maxOffset, shift));
        return this;
    }

    /// <summary>
    ///     Register a build-specific offset shift for ALL fields whose PDB owner matches
    ///     <paramref name="owner" />. Composes with band-based <see cref="WithShift(int,int,int)" />:
    ///     when both apply to a field, the band shift is applied first (in PDB-offset space,
    ///     first matching band wins), then the per-owner shift is added on top.
    ///     <para>
    ///         Used by readers whose runtime layout drifts uniformly within a PDB owner —
    ///         e.g. <c>RuntimeNpcFieldReader</c>'s TESNPC appearance region, which is
    ///         +32 bytes ahead of the MemDebug PDB across every observed FNV build
    ///         (FR2MatrixVTC padding delta).
    ///     </para>
    ///     <para>Registering shift=0 is a no-op (no entry stored). Returns <c>this</c> for
    ///     fluent chaining.</para>
    /// </summary>
    public PdbStructView WithShift(string owner, int shift)
    {
        if (shift == 0)
        {
            return this; // identity
        }

        _ownerShifts ??= new Dictionary<string, int>(StringComparer.Ordinal);
        _ownerShifts[owner] = shift;
        return this;
    }

    private int? ResolveOffset(string name, string? owner)
    {
        var pdbOffset = RuntimePdbFieldAccessor.FindFieldOffset(Layout, name, owner);
        if (pdbOffset is not { } off)
        {
            return null;
        }

        var resolved = off;

        // Band shifts: range matched against the original PDB offset, first match wins.
        if (_shifts is not null)
        {
            foreach (var (min, max, shift) in _shifts)
            {
                if (off >= min && off <= max)
                {
                    resolved = off + shift;
                    break;
                }
            }
        }

        // Per-owner shifts: additive on top of band shift. Owner is looked up exactly,
        // so a field declared with the matching owner picks up the shift regardless of
        // its PDB-declared offset.
        if (owner is not null && _ownerShifts is not null
            && _ownerShifts.TryGetValue(owner, out var ownerShift))
        {
            resolved += ownerShift;
        }

        return resolved;
    }

    /// <summary>
    ///     Look up a field's flattened offset by (name, owner), with any registered
    ///     band and per-owner shifts applied (see
    ///     <see cref="WithShift(int,int,int)" /> and <see cref="WithShift(string,int)" />).
    ///     Returns null if the field isn't in the layout — callers should treat that as
    ///     "field absent" and fall back to a default, matching the prevailing reader idiom.
    /// </summary>
    public int? Offset(string name, string? owner = null) => ResolveOffset(name, owner);

    public int Int32(string field, string? owner = null, int def = 0)
    {
        var off = ResolveOffset(field, owner);
        return off is { } o && o + 4 <= Buffer.Length
            ? RuntimePdbFieldAccessor.ReadInt32(Buffer, o)
            : def;
    }

    public uint UInt32(string field, string? owner = null, uint def = 0)
    {
        var off = ResolveOffset(field, owner);
        return off is { } o && o + 4 <= Buffer.Length
            ? RuntimePdbFieldAccessor.ReadUInt32(Buffer, o)
            : def;
    }

    public ushort UInt16(string field, string? owner = null, ushort def = 0)
    {
        var off = ResolveOffset(field, owner);
        return off is { } o && o + 2 <= Buffer.Length
            ? RuntimePdbFieldAccessor.ReadUInt16(Buffer, o)
            : def;
    }

    public float Float(string field, string? owner = null, float def = 0f)
    {
        var off = ResolveOffset(field, owner);
        return off is { } o && o + 4 <= Buffer.Length
            ? RuntimePdbFieldAccessor.ReadFloat(Buffer, o)
            : def;
    }

    public byte Byte(string field, string? owner = null, byte def = 0)
    {
        var off = ResolveOffset(field, owner);
        return off is { } o && o < Buffer.Length ? Buffer[o] : def;
    }

    /// <summary>
    ///     Int32 accessor with inclusive plausibility clamp: out-of-band values become 0.
    ///     Matches the pattern in older readers (e.g. <c>if (value &lt; 0 || value &gt; 1_000_000) value = 0</c>).
    /// </summary>
    public int Int32Range(string field, string? owner, int min, int max)
    {
        var v = Int32(field, owner);
        return v >= min && v <= max ? v : 0;
    }

    /// <summary>
    ///     Float accessor with plausibility clamp via
    ///     <see cref="RuntimeMemoryContext.ReadValidatedFloat" /> — finite + in-range.
    /// </summary>
    public float FloatRange(string field, string? owner, float min, float max)
    {
        var off = ResolveOffset(field, owner);
        return off is { } o ? RuntimeMemoryContext.ReadValidatedFloat(Buffer, o, min, max) : 0f;
    }

    /// <summary>
    ///     Reads a BSStringT at the named field. Wires through the accessor's
    ///     diagnostic-sample variant when the view was opened with a
    ///     <see cref="RuntimeEditorIdEntry" />.
    /// </summary>
    public string? BsString(string field, string? owner = null)
    {
        var off = ResolveOffset(field, owner);
        return _entry != null
            ? _accessor.ReadBsStringAtOffset(FileOffset, field, off, _entry)
            : _accessor.ReadBsStringAtOffset(FileOffset, field, off);
    }

    /// <summary>
    ///     Reads a TESForm pointer at the named field and resolves it to a FormID.
    ///     Optionally validates the pointed-to struct's FormType.
    /// </summary>
    public uint? FormIdPointer(string field, string? owner = null, byte? expectedFormType = null)
    {
        var off = ResolveOffset(field, owner);
        return off is { } o ? _accessor.ReadPointerToFormId(Buffer, o, expectedFormType) : null;
    }

    /// <summary>
    ///     Canonical OBND extraction via TESBoundObject.BoundData — returns null when
    ///     the layout has no BoundData field or the bounds are all-zero.
    /// </summary>
    public ObjectBounds? Bounds() => RuntimePdbFieldAccessor.ReadBounds(Buffer, Layout);

    /// <summary>
    ///     Walks a BSSimpleList&lt;TESForm*&gt; rooted at the named field and resolves each
    ///     item to a FormID.
    /// </summary>
    public List<uint> FormIdSimpleList(string field, string? owner = null, byte? expectedFormType = null)
    {
        var off = ResolveOffset(field, owner);
        return off is { } o
            ? _accessor.ReadFormIdSimpleList(Buffer, o, expectedFormType)
            : [];
    }

    public List<T> SimpleList<T>(string field, string? owner, Func<uint, T?> itemReader)
        where T : class
    {
        var off = ResolveOffset(field, owner);
        return off is { } o
            ? _accessor.ReadSimpleList(Buffer, o, itemReader)
            : [];
    }
}
