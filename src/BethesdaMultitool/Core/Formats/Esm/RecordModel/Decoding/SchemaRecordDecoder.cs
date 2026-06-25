using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;

namespace BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;

/// <summary>One raw subrecord handed to the decoder: its 4-char signature and its data bytes.</summary>
public readonly record struct RawSubrecord(string Signature, byte[] Data);

/// <summary>
///     Schema-driven record decoder: turns a record's raw subrecords into an ordered, labeled
///     <see cref="DecodedNode" /> tree using a generated <see cref="RecordDef" />. One engine serves every
///     game — the per-game schema supplies the layout, and version differences are absorbed by
///     <em>length-bounded</em> struct decoding (trailing optional/conditional fields simply consume what
///     is left in their framed subrecord, so a field present only in later games occupies zero bytes when
///     the subrecord is short). Anything the schema cannot model yet is preserved as a raw node rather
///     than guessed, so coverage gaps stay visible.
///     <para>
///         PC plugins are little-endian; pass <c>bigEndian: true</c> for an unconverted Xbox 360 record.
///     </para>
/// </summary>
public static class SchemaRecordDecoder
{
    /// <summary>Resolves a FormID to a display name (EditorID / FULL), or null. Optional.</summary>
    public delegate string? FormIdNameResolver(uint formId);

    public static IReadOnlyList<DecodedNode> Decode(
        RecordDef schema,
        IReadOnlyList<RawSubrecord> subrecords,
        bool bigEndian = false,
        FormIdNameResolver? resolveName = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(subrecords);

        var ctx = new DecodeContext(bigEndian, resolveName);

        // Map each entry-point signature to the top-level member that consumes it. Arrays map via their
        // element's signature(s); a plain signed member maps directly.
        var bySignature = new Dictionary<string, MemberDef>(StringComparer.Ordinal);
        foreach (var member in schema.Members)
        {
            foreach (var sig in EntrySignatures(member))
            {
                bySignature.TryAdd(sig, member);
            }
        }

        var output = new List<DecodedNode>();
        var i = 0;
        while (i < subrecords.Count)
        {
            var sub = subrecords[i];
            if (!bySignature.TryGetValue(sub.Signature, out var member))
            {
                output.Add(RawNode(sub.Signature, sub.Signature, sub.Data));
                i++;
                continue;
            }

            switch (member)
            {
                case ArrayDef array:
                    output.Add(DecodeArray(array, subrecords, ref i, ctx));
                    break;
                case StructDef group when string.IsNullOrEmpty(group.Signature):
                {
                    // A group struct whose children each own their own subrecord (e.g. Model =
                    // MODL + MODB + MODT). Consume one group, decoding each child from its own subrecord.
                    var before = i;
                    var children = DecodeOneGroup(group, subrecords, ref i, ctx);
                    if (i == before)
                    {
                        output.Add(RawNode(sub.Signature, sub.Signature, sub.Data));
                        i++;
                        break;
                    }

                    output.Add(new DecodedNode { Label = group.Name ?? "Group", Children = children });
                    break;
                }
                default:
                    output.Add(DecodeSignedMember(member, sub.Signature, sub.Data, ctx));
                    i++;
                    break;
            }
        }

        return output;
    }

    /// <summary>The signatures that, when seen in the subrecord stream, begin this member.</summary>
    private static IEnumerable<string> EntrySignatures(MemberDef member)
    {
        if (member.Signature is { Length: > 0 } sig)
        {
            yield return sig;
            yield break;
        }

        if (member is ArrayDef array)
        {
            foreach (var s in EntrySignatures(array.Element))
            {
                yield return s;
            }
        }
        else if (member is StructDef structDef)
        {
            // Element-per-group struct (e.g. MAST + DATA): its children own the signatures.
            foreach (var child in structDef.Members)
            {
                if (child.Signature is { Length: > 0 } cs)
                {
                    yield return cs;
                }
            }
        }
    }

    private static DecodedNode DecodeArray(
        ArrayDef array, IReadOnlyList<RawSubrecord> subrecords, ref int i, DecodeContext ctx)
    {
        var element = array.Element;
        var children = new List<DecodedNode>();

        if (element.Signature is { Length: > 0 } elementSig)
        {
            // One subrecord per element (SNAM faction, CNTO item, SPLO spell, ...).
            var index = 0;
            while (i < subrecords.Count && subrecords[i].Signature == elementSig)
            {
                var sub = subrecords[i];
                var node = DecodeSignedMember(element, sub.Signature, sub.Data, ctx);
                children.Add(node with { Label = $"{ElementLabel(element, node.Label)} [{index}]" });
                index++;
                i++;
            }
        }
        else if (element is StructDef groupStruct)
        {
            // Multi-subrecord element group (e.g. MAST filename + DATA size). Each call to DecodeOneGroup
            // consumes one element; repeat while the group's signed children keep appearing.
            var index = 0;
            while (i < subrecords.Count && GroupContainsSignature(groupStruct, subrecords[i].Signature))
            {
                var before = i;
                var groupChildren = DecodeOneGroup(groupStruct, subrecords, ref i, ctx);
                if (i == before)
                {
                    break; // no progress — avoid an infinite loop on a malformed stream
                }

                children.Add(new DecodedNode
                {
                    Label = $"{groupStruct.Name ?? "Item"} [{index}]",
                    Children = groupChildren
                });
                index++;
            }
        }

        return new DecodedNode
        {
            Label = array.Name ?? "Array",
            Value = $"{children.Count} item(s)",
            Children = children
        };
    }

    private static bool GroupContainsSignature(StructDef group, string signature) =>
        group.Members.Any(m => m.Signature == signature);

    /// <summary>
    ///     Consumes one group element: walks the struct's signed children in order, decoding each from its
    ///     own consecutive subrecord (skipping absent optional children). Advances <paramref name="i" />.
    /// </summary>
    private static List<DecodedNode> DecodeOneGroup(
        StructDef group, IReadOnlyList<RawSubrecord> subrecords, ref int i, DecodeContext ctx)
    {
        var children = new List<DecodedNode>();
        foreach (var child in group.Members)
        {
            if (child.Signature is not { Length: > 0 } cs || i >= subrecords.Count || subrecords[i].Signature != cs)
            {
                continue;
            }

            var sub = subrecords[i];
            children.Add(DecodeSignedMember(child, sub.Signature, sub.Data, ctx));
            i++;
        }

        return children;
    }

    private static string ElementLabel(MemberDef element, string fallback) =>
        element.Name ?? (string.IsNullOrEmpty(fallback) ? "Item" : fallback);

    /// <summary>Decodes a member that owns a whole framed subrecord (its <see cref="MemberDef.Signature" />).</summary>
    private static DecodedNode DecodeSignedMember(MemberDef member, string sig, byte[] data, DecodeContext ctx)
    {
        var label = member.Name is { Length: > 0 } name ? name : sig;
        switch (member)
        {
            case FieldDef field:
            {
                var (value, raw, formId) = DecodeScalar(field, data, 0, data.Length, ctx, out _);
                return new DecodedNode
                {
                    Label = label, Value = value, RawValue = raw, FormId = formId, Signature = sig
                };
            }
            case FormIdDef:
            {
                var (value, raw, formId) = DecodeFormId(data, 0, data.Length, ctx);
                return new DecodedNode { Label = label, Value = value, RawValue = raw, FormId = formId, Signature = sig };
            }
            case StructDef structDef:
            {
                var children = new List<DecodedNode>();
                DecodeStructInto(structDef, data, 0, data.Length, ctx, children);
                return new DecodedNode { Label = label, Children = children, Signature = sig };
            }
            case EmptyDef:
                return new DecodedNode { Label = label, Value = "(present)", Signature = sig };
            default:
                return RawNode(label, sig, data);
        }
    }

    /// <summary>
    ///     Length-bounded sequential struct decode within <paramref name="data" />[offset..limit].
    ///     Trailing members that run past the available bytes are simply absent (the key to absorbing
    ///     version differences). Returns the offset reached.
    /// </summary>
    private static int DecodeStructInto(
        StructDef structDef, byte[] data, int offset, int limit, DecodeContext ctx, List<DecodedNode> output)
    {
        foreach (var member in structDef.Members)
        {
            if (offset >= limit)
            {
                break; // remaining members are absent in this (shorter) framed subrecord
            }

            switch (member)
            {
                case FieldDef field:
                {
                    var (value, raw, formId) = DecodeScalar(field, data, offset, limit, ctx, out var size);
                    if (size < 0)
                    {
                        // Variable/undecodable inline field — preserve the remainder verbatim and stop.
                        output.Add(RawNode(field.Name ?? "Data", null, data[offset..limit]));
                        return limit;
                    }

                    output.Add(new DecodedNode
                    {
                        Label = field.Name ?? "Field", Value = value, RawValue = raw, FormId = formId
                    });
                    offset += size;
                    break;
                }
                case FormIdDef formIdDef:
                {
                    if (limit - offset < 4)
                    {
                        return offset;
                    }

                    var (value, raw, formId) = DecodeFormId(data, offset, limit, ctx);
                    output.Add(new DecodedNode { Label = formIdDef.Name ?? "FormID", Value = value, RawValue = raw, FormId = formId });
                    offset += 4;
                    break;
                }
                case UnusedDef unused:
                    offset += unused.Size;
                    break;
                case StructDef nested:
                {
                    var children = new List<DecodedNode>();
                    var reached = DecodeStructInto(nested, data, offset, limit, ctx, children);
                    output.Add(new DecodedNode { Label = nested.Name ?? "Struct", Children = children });
                    offset = reached;
                    break;
                }
                case ArrayDef inlineArray when inlineArray.Count > 0:
                {
                    var children = new List<DecodedNode>();
                    for (var n = 0; n < inlineArray.Count && offset < limit; n++)
                    {
                        offset = DecodeInlineElement(inlineArray.Element, data, offset, limit, ctx, n, children);
                    }

                    output.Add(new DecodedNode
                    {
                        Label = inlineArray.Name ?? "Array", Value = $"{children.Count} item(s)", Children = children
                    });
                    break;
                }
                default:
                    // Union / dynamic array / RawMemberDef / unmodeled — preserve the tail verbatim. For a
                    // trailing member this is the conditional-absent case (0 bytes); mid-struct it stops here.
                    output.Add(RawNode(member.Name ?? member.GetType().Name, null, data[offset..limit]));
                    return limit;
            }
        }

        return offset;
    }

    private static int DecodeInlineElement(
        MemberDef element, byte[] data, int offset, int limit, DecodeContext ctx, int index, List<DecodedNode> output)
    {
        switch (element)
        {
            case FieldDef field:
            {
                var (value, raw, formId) = DecodeScalar(field, data, offset, limit, ctx, out var size);
                if (size < 0)
                {
                    return limit;
                }

                output.Add(new DecodedNode
                {
                    Label = $"{field.Name ?? "Item"} [{index}]", Value = value, RawValue = raw, FormId = formId
                });
                return offset + size;
            }
            case FormIdDef:
            {
                if (limit - offset < 4)
                {
                    return limit;
                }

                var (value, raw, formId) = DecodeFormId(data, offset, limit, ctx);
                output.Add(new DecodedNode { Label = $"Item [{index}]", Value = value, RawValue = raw, FormId = formId });
                return offset + 4;
            }
            case StructDef structDef:
            {
                var children = new List<DecodedNode>();
                var reached = DecodeStructInto(structDef, data, offset, limit, ctx, children);
                output.Add(new DecodedNode { Label = $"{structDef.Name ?? "Item"} [{index}]", Children = children });
                return reached;
            }
            default:
                return limit;
        }
    }

    private static (string? Value, object? Raw, uint? FormId) DecodeFormId(
        byte[] data, int offset, int limit, DecodeContext ctx)
    {
        if (limit - offset < 4)
        {
            return (null, null, null);
        }

        var value = ctx.BigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

        if (value == 0)
        {
            return ("0x00000000 (none)", value, null);
        }

        var name = ctx.ResolveName?.Invoke(value);
        var display = name is { Length: > 0 }
            ? $"{name} (0x{value:X8})"
            : $"0x{value:X8}";
        return (display, value, value);
    }

    private static readonly (string?, object?, uint?) NoValue = (null, null, null);

    private static (string? Value, object? Raw, uint? FormId) DecodeScalar(
        FieldDef field, byte[] data, int offset, int limit, DecodeContext ctx, out int size)
    {
        var available = limit - offset;

        switch (field.Type)
        {
            case PrimType.U8:
                return Fits(1, available, out size) ? Integer(field, data[offset]) : NoValue;
            case PrimType.S8:
                return Fits(1, available, out size) ? Integer(field, (sbyte)data[offset]) : NoValue;
            case PrimType.U16:
                return Fits(2, available, out size) ? Integer(field, ctx.ReadU16(data, offset)) : NoValue;
            case PrimType.S16:
                return Fits(2, available, out size) ? Integer(field, ctx.ReadS16(data, offset)) : NoValue;
            case PrimType.U24:
                return Fits(3, available, out size) ? Integer(field, ctx.ReadU24(data, offset)) : NoValue;
            case PrimType.U32:
                return Fits(4, available, out size) ? Integer(field, ctx.ReadU32(data, offset)) : NoValue;
            case PrimType.S32:
                return Fits(4, available, out size) ? Integer(field, ctx.ReadS32(data, offset)) : NoValue;
            case PrimType.U64:
                return Fits(8, available, out size) ? Integer(field, (long)ctx.ReadU64(data, offset)) : NoValue;
            case PrimType.S64:
                return Fits(8, available, out size) ? Integer(field, ctx.ReadS64(data, offset)) : NoValue;
            case PrimType.FormId:
                return Fits(4, available, out size) ? DecodeFormId(data, offset, limit, ctx) : NoValue;
            case PrimType.Float:
                return Fits(4, available, out size) ? Scalar(ctx.ReadFloat(data, offset)) : NoValue;
            case PrimType.Double:
                return Fits(8, available, out size) ? Scalar(ctx.ReadDouble(data, offset)) : NoValue;
            case PrimType.Half:
                return Fits(2, available, out size) ? Scalar((float)ctx.ReadHalf(data, offset)) : NoValue;
            case PrimType.ZString:
            case PrimType.StringKC:
            {
                size = field.FixedSize ?? available;
                var count = Math.Min(size, Math.Max(available, 0));
                var s = DecodeString(data, offset, count);
                return (s, s, null);
            }
            case PrimType.LString:
            {
                // Localized string: a 4-byte string-table index on lstring plugins. Without the table we
                // surface the index; the .STRINGS join is a later step.
                if (!Fits(4, available, out size))
                {
                    return NoValue;
                }

                var idx = ctx.ReadU32(data, offset);
                return ($"<lstring #{idx}>", idx, null);
            }
            case PrimType.ByteArray:
            default:
                size = field.FixedSize ?? available;
                var len = Math.Min(size, Math.Max(available, 0));
                return ($"<{len} bytes>", data[offset..(offset + len)], null);
        }
    }

    /// <summary>Sets <paramref name="size" /> to the needed width when it fits, else -1 (undecodable).</summary>
    private static bool Fits(int need, int available, out int size)
    {
        if (available >= need)
        {
            size = need;
            return true;
        }

        size = -1;
        return false;
    }

    private static (string? Value, object? Raw, uint? FormId) Scalar(object value) =>
        (Convert.ToString(value, CultureInfo.InvariantCulture), value, null);

    /// <summary>Formats an integer with its enum label / flag breakdown when the field declares one.</summary>
    private static (string? Value, object? Raw, uint? FormId) Integer(FieldDef field, long value)
    {
        if (field.InlineEnum is { } e)
        {
            var label = e.Members.FirstOrDefault(m => m.Value == value)?.Label;
            return (label is { Length: > 0 } ? $"{label} ({value})" : value.ToString(CultureInfo.InvariantCulture),
                value, null);
        }

        if (field.InlineFlags is { } f)
        {
            var set = f.Bits.Where(b => (value & (1L << b.Bit)) != 0).Select(b => b.Label).ToList();
            var labels = set.Count > 0 ? string.Join(", ", set) : "(none)";
            return ($"0x{value:X} [{labels}]", value, null);
        }

        return (value.ToString(CultureInfo.InvariantCulture), value, null);
    }

    private static DecodedNode RawNode(string label, string? sig, byte[] data) => new()
    {
        Label = label,
        Value = $"<{data.Length} bytes>",
        RawValue = data,
        Signature = sig,
        IsRaw = true
    };

    private static string DecodeString(byte[] data, int offset, int count)
    {
        if (count <= 0 || offset >= data.Length)
        {
            return string.Empty;
        }

        count = Math.Min(count, data.Length - offset);
        var len = count;
        while (len > 0 && data[offset + len - 1] == 0)
        {
            len--; // trim trailing NUL padding
        }

        return Encoding.ASCII.GetString(data, offset, len);
    }

    /// <summary>Endian-aware primitive reads, captured once so the scalar switch stays terse.</summary>
    private sealed class DecodeContext(bool bigEndian, FormIdNameResolver? resolveName)
    {
        public bool BigEndian { get; } = bigEndian;
        public FormIdNameResolver? ResolveName { get; } = resolveName;

        public ushort ReadU16(byte[] d, int o) => BigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o, 2));

        public short ReadS16(byte[] d, int o) => BigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(o, 2))
            : BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o, 2));

        public int ReadU24(byte[] d, int o) => BigEndian
            ? (d[o] << 16) | (d[o + 1] << 8) | d[o + 2]
            : d[o] | (d[o + 1] << 8) | (d[o + 2] << 16);

        public uint ReadU32(byte[] d, int o) => BigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o, 4));

        public int ReadS32(byte[] d, int o) => BigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(o, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o, 4));

        public ulong ReadU64(byte[] d, int o) => BigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(o, 8))
            : BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o, 8));

        public long ReadS64(byte[] d, int o) => BigEndian
            ? BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(o, 8))
            : BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(o, 8));

        public float ReadFloat(byte[] d, int o) => BigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(o, 4))
            : BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o, 4));

        public double ReadDouble(byte[] d, int o) => BigEndian
            ? BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(o, 8))
            : BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(o, 8));

        public Half ReadHalf(byte[] d, int o) => BigEndian
            ? BinaryPrimitives.ReadHalfBigEndian(d.AsSpan(o, 2))
            : BinaryPrimitives.ReadHalfLittleEndian(d.AsSpan(o, 2));
    }
}
