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
    ///     Look up a field's flattened offset by (name, owner). Returns null if the field
    ///     isn't in the layout — callers should treat that as "field absent" and fall back
    ///     to a default, matching the prevailing reader idiom.
    /// </summary>
    public int? Offset(string name, string? owner = null)
    {
        return RuntimePdbFieldAccessor.FindFieldOffset(Layout, name, owner);
    }

    public int Int32(string field, string? owner = null, int def = 0)
    {
        var off = RuntimePdbFieldAccessor.FindFieldOffset(Layout, field, owner);
        return off is { } o && o + 4 <= Buffer.Length
            ? RuntimePdbFieldAccessor.ReadInt32(Buffer, o)
            : def;
    }

    public uint UInt32(string field, string? owner = null, uint def = 0)
    {
        var off = RuntimePdbFieldAccessor.FindFieldOffset(Layout, field, owner);
        return off is { } o && o + 4 <= Buffer.Length
            ? RuntimePdbFieldAccessor.ReadUInt32(Buffer, o)
            : def;
    }

    public ushort UInt16(string field, string? owner = null, ushort def = 0)
    {
        var off = RuntimePdbFieldAccessor.FindFieldOffset(Layout, field, owner);
        return off is { } o && o + 2 <= Buffer.Length
            ? RuntimePdbFieldAccessor.ReadUInt16(Buffer, o)
            : def;
    }

    public float Float(string field, string? owner = null, float def = 0f)
    {
        var off = RuntimePdbFieldAccessor.FindFieldOffset(Layout, field, owner);
        return off is { } o && o + 4 <= Buffer.Length
            ? RuntimePdbFieldAccessor.ReadFloat(Buffer, o)
            : def;
    }

    public byte Byte(string field, string? owner = null, byte def = 0)
    {
        var off = RuntimePdbFieldAccessor.FindFieldOffset(Layout, field, owner);
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
        var off = RuntimePdbFieldAccessor.FindFieldOffset(Layout, field, owner);
        return off is { } o ? RuntimeMemoryContext.ReadValidatedFloat(Buffer, o, min, max) : 0f;
    }

    /// <summary>
    ///     Reads a BSStringT at the named field. Wires through the accessor's
    ///     diagnostic-sample variant when the view was opened with a
    ///     <see cref="RuntimeEditorIdEntry" />.
    /// </summary>
    public string? BsString(string field, string? owner = null)
    {
        return _entry != null
            ? _accessor.ReadBsString(FileOffset, Layout, field, owner, _entry)
            : _accessor.ReadBsString(FileOffset, Layout, field, owner);
    }

    /// <summary>
    ///     Reads a TESForm pointer at the named field and resolves it to a FormID.
    ///     Optionally validates the pointed-to struct's FormType.
    /// </summary>
    public uint? FormIdPointer(string field, string? owner = null, byte? expectedFormType = null)
    {
        return _accessor.ReadFormIdPointer(Buffer, Layout, field, owner, expectedFormType);
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
        var off = RuntimePdbFieldAccessor.FindFieldOffset(Layout, field, owner);
        return off is { } o
            ? _accessor.ReadFormIdSimpleList(Buffer, o, expectedFormType)
            : [];
    }

    public List<T> SimpleList<T>(string field, string? owner, Func<uint, T?> itemReader)
        where T : class
    {
        var off = RuntimePdbFieldAccessor.FindFieldOffset(Layout, field, owner);
        return off is { } o
            ? _accessor.ReadSimpleList(Buffer, o, itemReader)
            : [];
    }
}
