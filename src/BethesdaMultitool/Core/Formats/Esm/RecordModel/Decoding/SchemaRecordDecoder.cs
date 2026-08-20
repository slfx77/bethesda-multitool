using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;

/// <summary>One raw subrecord handed to the decoder: its 4-char signature and its data bytes.</summary>
public readonly record struct RawSubrecord(string Signature, byte[] Data);

/// <summary>
///     Schema-driven record decoder: turns a record's raw subrecords into an ordered, labeled
///     <see cref="DecodedNode" /> tree using a generated <see cref="RecordDef" />. One engine serves every
///     game — the per-game schema supplies the layout. Generated form-version bounds skip inactive members
///     without consuming bytes; remaining optional layout differences are handled by length-bounded
///     struct decoding. Anything the schema cannot model yet is surfaced as raw data rather than guessed,
///     so coverage gaps stay visible.
///     <para>
///         PC plugins are little-endian; pass <c>bigEndian: true</c> for an unconverted Xbox 360 record.
///     </para>
/// </summary>
public static class SchemaRecordDecoder
{
    /// <summary>Resolves a FormID to a display name (EditorID / FULL), or null. Optional.</summary>
    public delegate string? FormIdNameResolver(uint formId);

    private static readonly (string?, object?, uint?) NoValue = (null, null, null);

    public static IReadOnlyList<DecodedNode> Decode(
        RecordDef schema,
        IReadOnlyList<RawSubrecord> subrecords,
        bool bigEndian = false,
        FormIdNameResolver? resolveName = null,
        BethesdaGame game = BethesdaGame.Unknown,
        ushort? formVersion = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(subrecords);

        var ctx = new DecodeContext(bigEndian, resolveName, game, schema.Signature, formVersion);

        // Map each entry-point signature to the top-level member that consumes it. Arrays map via their
        // element's signature(s); a plain signed member maps directly.
        var bySignature = new Dictionary<string, MemberDef>(StringComparer.Ordinal);
        foreach (var member in schema.Members)
        {
            foreach (var sig in EntrySignatures(member, ctx))
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

            var iBefore = i;
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

            // Safety net: a matched member must always consume at least the current subrecord. If some
            // schema edge case leaves i unmoved, force progress so the decode can never stall.
            if (i == iBefore)
            {
                i++;
            }
        }

        return output;
    }

    /// <summary>The signatures that, when seen in the subrecord stream, begin this member.</summary>
    private static IEnumerable<string> EntrySignatures(MemberDef member, DecodeContext ctx)
    {
        if (!IsMemberActive(member, ctx))
        {
            yield break;
        }

        if (member.Signature is { Length: > 0 } sig)
        {
            // An outer-signed repeating array has no physical entry in a layout where its element is
            // version-gated out. Leave an unexpected old/new subrecord visible as a top-level raw node
            // instead of manufacturing an "array with one raw element" as if the schema consumed it.
            if (member is ArrayDef outerSignedArray && !IsMemberActive(outerSignedArray.Element, ctx))
            {
                yield break;
            }

            yield return sig;
            yield break;
        }

        if (member is ArrayDef array)
        {
            foreach (var s in EntrySignatures(array.Element, ctx))
            {
                yield return s;
            }
        }
        else if (member is StructDef structDef)
        {
            // Element-per-group struct (e.g. MAST + DATA): its children own the signatures.
            foreach (var child in structDef.Members)
            {
                if (IsMemberActive(child, ctx) && child.Signature is { Length: > 0 } cs)
                {
                    yield return cs;
                }
            }
        }
        else if (member is UnionDef union)
        {
            // A subrecord-level union (e.g. a TESConditionItem serialized as either a CTDA or a CTDT
            // subrecord): each variant contributes its own entry signature.
            foreach (var variant in union.Variants)
            {
                foreach (var s in EntrySignatures(variant, ctx))
                {
                    yield return s;
                }
            }
        }
    }

    private static DecodedNode DecodeArray(
        ArrayDef array, IReadOnlyList<RawSubrecord> subrecords, ref int i, DecodeContext ctx)
    {
        var element = array.Element;
        var children = new List<DecodedNode>();

        // The repeating subrecord's signature lives on the array itself when its element is an inline
        // field (ENAM eyes, KFFZ animations), or on the element when it owns a framed struct/field
        // (SNAM faction, CNTO item, PKID package). Either way each matching subrecord is one element.
        var elementSig = !string.IsNullOrEmpty(array.Signature) ? array.Signature : element.Signature;
        if (!string.IsNullOrEmpty(elementSig))
        {
            var index = 0;
            while (i < subrecords.Count && subrecords[i].Signature == elementSig)
            {
                var sub = subrecords[i];
                var node = IsMemberActive(element, ctx)
                    ? DecodeSignedMember(element, sub.Signature, sub.Data, ctx)
                    : RawNode(sub.Signature, sub.Signature, sub.Data);
                children.Add(node with { Label = $"{ElementLabel(element, node.Label)} [{index}]" });
                index++;
                i++;
            }
        }
        else if (element is UnionDef union)
        {
            // Each element is one of several signed-struct variants (e.g. a Condition that is a CTDA or a
            // CTDT subrecord); pick the variant whose signature matches the next subrecord.
            var index = 0;
            while (i < subrecords.Count)
            {
                var sub = subrecords[i];
                var variant =
                    union.Variants.FirstOrDefault(v => IsMemberActive(v, ctx) && v.Signature == sub.Signature);
                if (variant is null)
                {
                    break;
                }

                var node = DecodeSignedMember(variant, sub.Signature, sub.Data, ctx);
                children.Add(node with { Label = $"{ElementLabel(variant, node.Label)} [{index}]" });
                index++;
                i++;
            }
        }
        else if (element is StructDef groupStruct)
        {
            // Multi-subrecord element group (e.g. MAST filename + DATA size). Each call to DecodeOneGroup
            // consumes one element; repeat while the group's signed children keep appearing.
            var index = 0;
            while (i < subrecords.Count && GroupContainsSignature(groupStruct, subrecords[i].Signature, ctx))
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

    private static bool GroupContainsSignature(StructDef group, string signature, DecodeContext ctx)
    {
        return group.Members.Any(m => IsMemberActive(m, ctx) && m.Signature == signature);
    }

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
            if (!IsMemberActive(child, ctx) ||
                child.Signature is not { Length: > 0 } cs ||
                i >= subrecords.Count ||
                subrecords[i].Signature != cs)
            {
                continue;
            }

            var sub = subrecords[i];
            children.Add(DecodeSignedMember(child, sub.Signature, sub.Data, ctx));
            i++;
        }

        ApplyConditionStringAuthority(children);
        return children;
    }

    /// <summary>
    ///     A physical CIS1/CIS2 sibling carries the authoritative string for the corresponding CTDA
    ///     parameter. The four-byte CTDA slot remains useful forensic evidence, but it is only placeholder
    ///     storage and must not keep an inferred FormID link or a resolver-derived display name. This pass is
    ///     deliberately group-local: malformed/orphan CIS subrecords decoded into a group without CTDA are
    ///     left untouched rather than attached to an earlier condition.
    /// </summary>
    private static void ApplyConditionStringAuthority(List<DecodedNode> groupChildren)
    {
        var ctdaIndex = groupChildren.FindIndex(static node => node.Signature == "CTDA");
        if (ctdaIndex < 0)
        {
            return;
        }

        var ctda = groupChildren[ctdaIndex];
        var ctdaChildren = ctda.Children.ToList();
        var changedCtda = false;

        for (var parameterNumber = 1; parameterNumber <= 2; parameterNumber++)
        {
            var cisSignature = $"CIS{parameterNumber}";
            var cisIndex = groupChildren.FindIndex(
                ctdaIndex + 1,
                node => string.Equals(node.Signature, cisSignature, StringComparison.Ordinal));
            if (cisIndex < 0)
            {
                continue;
            }

            var parameterLabel = $"Parameter #{parameterNumber}";
            var placeholderIndex =
                ctdaChildren.FindIndex(node => string.Equals(node.Label, parameterLabel, StringComparison.Ordinal));
            if (placeholderIndex >= 0)
            {
                var placeholder = ctdaChildren[placeholderIndex];
                ctdaChildren[placeholderIndex] = placeholder with
                {
                    Label = $"{parameterLabel} (CTDA placeholder)",
                    Value = FormatConditionPlaceholderBits(placeholder.RawValue, placeholder.Value),
                    FormId = null
                };
                changedCtda = true;
            }

            var cis = groupChildren[cisIndex];
            groupChildren[cisIndex] = cis with
            {
                Label = $"{parameterLabel} ({cisSignature} authoritative string)",
                Value = cis.RawValue is string { Length: 0 } ? "(empty string)" : cis.Value
            };
        }

        if (changedCtda)
        {
            groupChildren[ctdaIndex] = ctda with { Children = ctdaChildren };
        }
    }

    /// <summary>Displays the exact scalar bits without retaining a FormID resolver's friendly name.</summary>
    private static string? FormatConditionPlaceholderBits(object? rawValue, string? fallback)
    {
        return rawValue switch
        {
            byte value => $"0x{value:X2}",
            sbyte value => $"0x{unchecked((byte)value):X2}",
            ushort value => $"0x{value:X4}",
            short value => $"0x{unchecked((ushort)value):X4}",
            uint value => $"0x{value:X8}",
            int value => $"0x{unchecked((uint)value):X8}",
            ulong value => $"0x{value:X16}",
            long value => $"0x{unchecked((ulong)value):X16}",
            byte[] value => Convert.ToHexString(value),
            _ => fallback
        };
    }

    private static string ElementLabel(MemberDef element, string fallback)
    {
        return element.Name ?? (string.IsNullOrEmpty(fallback) ? "Item" : fallback);
    }

    /// <summary>Decodes a member that owns a whole framed subrecord (its <see cref="MemberDef.Signature" />).</summary>
    private static DecodedNode DecodeSignedMember(MemberDef member, string sig, byte[] data, DecodeContext ctx)
    {
        var label = member.Name is { Length: > 0 } name ? name : sig;

        // For a big-endian (Xbox 360) record, ask the conversion schema — the single source of Xbox
        // fixed-LE truth — which byte offsets in THIS subrecord are stored little-endian even on Xbox, so
        // the reads below can skip the byte-swap for exactly those fields. Null (the common case) means no
        // override; on a PC/LE record we never even ask, so the decode is byte-identical to before.
        var leMap = ctx.BigEndian
            ? SubrecordSchemaRegistry.GetFixedLeLayout(sig, ctx.RecordSignature, data.Length)
            : null;

        switch (member)
        {
            case FieldDef field:
            {
                var (value, raw, formId) = DecodeScalar(field, data, 0, data.Length, ctx, leMap, out _);
                return new DecodedNode
                {
                    Label = label, Value = value, RawValue = raw, FormId = formId, Signature = sig
                };
            }
            case FormIdDef:
            {
                var (value, raw, formId) = DecodeFormId(data, 0, data.Length, ctx, leMap);
                return new DecodedNode
                    { Label = label, Value = value, RawValue = raw, FormId = formId, Signature = sig };
            }
            case StructDef structDef:
            {
                var children = new List<DecodedNode>();
                DecodeStructInto(structDef, data, 0, data.Length, ctx, leMap, children);
                return new DecodedNode { Label = label, Children = children, Signature = sig };
            }
            case UnionDef union when TrySelectSignedFormVersionVariant(
                union, data.Length, ctx, out var selected):
            {
                // A direct one-threshold wbFormVersionDecider is fully represented by complementary
                // gates on its two arms. Decode only when a known header version selects exactly one
                // exact-size arm; unknown/general/malformed layouts keep the historical whole-subrecord
                // raw fallback.
                var decoded = DecodeSignedMember(selected, sig, data, ctx);
                return decoded with { Label = label, Signature = sig };
            }
            case EmptyDef:
                return new DecodedNode { Label = label, Value = "(present)", Signature = sig };
            default:
                return RawNode(label, sig, data);
        }
    }

    private static bool TrySelectSignedFormVersionVariant(
        UnionDef union, int payloadLength, DecodeContext ctx, out MemberDef selected)
    {
        selected = null!;
        if (ctx.FormVersion is null ||
            !string.Equals(union.DeciderName, "wbFormVersionDecider", StringComparison.Ordinal) ||
            union.Variants.Count != 2)
        {
            return false;
        }

        var below = union.Variants[0];
        var from = union.Variants[1];
        if (below.MinFormVersion is not null ||
            below.MaxFormVersionExclusive is not { } threshold ||
            from.MinFormVersion != threshold ||
            from.MaxFormVersionExclusive is not null)
        {
            return false;
        }

        MemberDef? active = null;
        foreach (var variant in union.Variants)
        {
            if (!IsMemberActive(variant, ctx))
            {
                continue;
            }

            if (active is not null)
            {
                return false;
            }

            active = variant;
        }

        if (active is null || TryFixedSize(active, ctx) != payloadLength)
        {
            return false;
        }

        selected = active;
        return true;
    }

    /// <summary>
    ///     Length-bounded sequential struct decode within <paramref name="data" />[offset..limit].
    ///     Trailing members that run past the available bytes are simply absent (the key to absorbing
    ///     version differences). Returns the offset reached.
    /// </summary>
    private static int DecodeStructInto(
        StructDef structDef, byte[] data, int offset, int limit, DecodeContext ctx,
        IReadOnlyDictionary<int, LeFieldKind>? leMap, List<DecodedNode> output)
    {
        foreach (var member in structDef.Members)
        {
            // A form-version gate is a layout decision, not a length check: inactive members occupy
            // exactly zero bytes even when later active fields remain in this same framed subrecord.
            if (!IsMemberActive(member, ctx))
            {
                continue;
            }

            if (offset >= limit)
            {
                break; // remaining members are absent in this (shorter) framed subrecord
            }

            switch (member)
            {
                case FieldDef field:
                {
                    var (value, raw, formId) = DecodeScalar(field, data, offset, limit, ctx, leMap, out var size);
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

                    var (value, raw, formId) = DecodeFormId(data, offset, limit, ctx, leMap);
                    output.Add(new DecodedNode
                        { Label = formIdDef.Name ?? "FormID", Value = value, RawValue = raw, FormId = formId });
                    offset += 4;
                    break;
                }
                case UnusedDef unused:
                    offset += unused.Size;
                    break;
                case StructDef nested:
                {
                    var children = new List<DecodedNode>();
                    var reached = DecodeStructInto(nested, data, offset, limit, ctx, leMap, children);
                    output.Add(new DecodedNode { Label = nested.Name ?? "Struct", Children = children });
                    offset = reached;
                    break;
                }
                case ArrayDef inlineArray when inlineArray.Count > 0:
                {
                    if (!IsMemberActive(inlineArray.Element, ctx))
                    {
                        break;
                    }

                    var children = new List<DecodedNode>();
                    for (var n = 0; n < inlineArray.Count && offset < limit; n++)
                    {
                        offset = DecodeInlineElement(inlineArray.Element, data, offset, limit, ctx, leMap, n, children);
                    }

                    output.Add(new DecodedNode
                    {
                        Label = inlineArray.Name ?? "Array", Value = $"{children.Count} item(s)", Children = children
                    });
                    break;
                }
                case UnionDef union when union.Variants.Count > 0 &&
                                         !union.Variants.Any(variant => IsMemberActive(variant, ctx)):
                    // Every arm is version-gated out: the union occupies zero bytes in this layout.
                    // An actually empty CTDA parameter union is different: its physical slot is still
                    // four bytes and TryDecodeConditionUnion below preserves those bits for alignment.
                    // Emit no placeholder node here, so a later active sibling starts at this offset.
                    break;
                case UnionDef union when TryDecodeConditionUnion(union, data, offset, limit, ctx, leMap, output,
                    out var conditionNode):
                {
                    // CTDA deciders resolved from the ALREADY-DECODED siblings: the Function index
                    // picks each Parameter's numeric-vs-FormID interpretation via the game's
                    // condition-function table, and the Type flags pick float-vs-GLOB for the
                    // comparison value. Run On plus Function select whether offset 24 is a semantic
                    // Reference FormID. Every variant is 4 bytes.
                    output.Add(conditionNode);
                    offset += 4;
                    break;
                }
                case UnionDef union when TryUniformVariantSize(union, ctx, out var usize) && offset + usize <= limit:
                {
                    // An inline value union (e.g. CTDA Comparison Value / Parameter #1) — every variant is the
                    // same width, so the struct stays aligned whichever one the data is. Without a game
                    // (or for non-condition unions), decode the first variant as a representative.
                    output.Add(DecodeInlineUnion(union, data, offset, limit, ctx, leMap));
                    offset += usize;
                    break;
                }
                default:
                    // Union (non-uniform) / dynamic array / RawMemberDef / unmodeled — preserve the tail
                    // verbatim. For a trailing member this is the conditional-absent case (0 bytes);
                    // mid-struct it stops here.
                    output.Add(RawNode(member.Name ?? member.GetType().Name, null, data[offset..limit]));
                    return limit;
            }
        }

        return offset;
    }

    private static int DecodeInlineElement(
        MemberDef element, byte[] data, int offset, int limit, DecodeContext ctx,
        IReadOnlyDictionary<int, LeFieldKind>? leMap, int index, List<DecodedNode> output)
    {
        if (!IsMemberActive(element, ctx))
        {
            return offset;
        }

        switch (element)
        {
            case FieldDef field:
            {
                var (value, raw, formId) = DecodeScalar(field, data, offset, limit, ctx, leMap, out var size);
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

                var (value, raw, formId) = DecodeFormId(data, offset, limit, ctx, leMap);
                output.Add(new DecodedNode
                    { Label = $"Item [{index}]", Value = value, RawValue = raw, FormId = formId });
                return offset + 4;
            }
            case StructDef structDef:
            {
                var children = new List<DecodedNode>();
                var reached = DecodeStructInto(structDef, data, offset, limit, ctx, leMap, children);
                output.Add(new DecodedNode { Label = $"{structDef.Name ?? "Item"} [{index}]", Children = children });
                return reached;
            }
            default:
                return limit;
        }
    }

    private static (string? Value, object? Raw, uint? FormId) DecodeFormId(
        byte[] data, int offset, int limit, DecodeContext ctx,
        IReadOnlyDictionary<int, LeFieldKind>? leMap = null)
    {
        if (limit - offset < 4)
        {
            return (null, null, null);
        }

        // A FormID marked fixed-LE by the conversion schema is stored little-endian even on Xbox — read it
        // little-endian regardless of ctx.BigEndian. leMap is only populated on big-endian records.
        var forceLe = leMap is not null && leMap.TryGetValue(offset, out var kind) && kind == LeFieldKind.LittleEndian;
        var value = ctx.BigEndian && !forceLe
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

    /// <summary>
    ///     Fallback for an inline value union whose specialized decider did not select a variant: decode
    ///     the first uniform-width variant so the enclosing struct remains aligned. A purely opaque
    ///     four-byte first variant is surfaced as a u32 rather than only "&lt;4 bytes&gt;".
    /// </summary>
    private static DecodedNode DecodeInlineUnion(UnionDef union, byte[] data, int offset, int limit, DecodeContext ctx,
        IReadOnlyDictionary<int, LeFieldKind>? leMap)
    {
        var label = union.Name ?? "Value";
        var repr = union.Variants.First(v => IsMemberActive(v, ctx));
        if (repr is FieldDef { Type: PrimType.ByteArray } opaque)
        {
            repr = new FieldDef(PrimType.U32) { Name = opaque.Name };
        }

        switch (repr)
        {
            case FormIdDef:
            {
                var (value, raw, fid) = DecodeFormId(data, offset, limit, ctx, leMap);
                return new DecodedNode { Label = label, Value = value, RawValue = raw, FormId = fid };
            }
            case FieldDef field:
            {
                var (value, raw, fid) = DecodeScalar(field, data, offset, limit, ctx, leMap, out _);
                return new DecodedNode { Label = label, Value = value, RawValue = raw, FormId = fid };
            }
            default:
                return RawNode(label, null, data[offset..limit]);
        }
    }

    /// <summary>
    ///     Typed CTDA union decode (S2): resolves the condition deciders from the game's
    ///     condition-function table plus the struct's already-decoded sibling nodes.
    ///     <c>wbConditionParam1/2Decider</c> classify their 4-byte value as FormID-vs-numeric by the
    ///     sibling <c>Function</c> index; <c>wbConditionCompValueDecider</c> picks GLOB-vs-float by
    ///     the sibling <c>Type</c>'s UseGlobal flag (0x04); <c>wbConditionReferenceDecider</c> uses the
    ///     preceding <c>Function</c> and <c>Run On</c> fields with the explicit game-aware Reference
    ///     policy. <c>wbConditionRunOnDecider</c> repairs FNV's omitted sparse body-selector labels, and
    ///     <c>wbConditionParam3Decider</c> selects the generated signed variant by Run On. Parameter
    ///     metadata misses consume the physically fixed 4-byte slot as a raw u32 so
    ///     later Run On / Reference members remain aligned; non-semantic Reference storage falls back
    ///     to the schema's raw first variant without manufacturing a FormID link.
    /// </summary>
    private static bool TryDecodeConditionUnion(
        UnionDef union, byte[] data, int offset, int limit, DecodeContext ctx,
        IReadOnlyDictionary<int, LeFieldKind>? leMap, List<DecodedNode> siblings, out DecodedNode node)
    {
        node = null!;
        if (limit - offset < 4)
        {
            return false;
        }

        var label = union.Name ?? "Value";
        switch (union.DeciderName)
        {
            case "wbConditionRunOnDecider":
            {
                // FNV gives two functions a second meaning for this word: it is a sparse
                // animation-body selector, not the ordinary Subject/Target/Reference enum.
                // The generated second variant currently has no enum members, so recover the
                // function-aware labels from the same policy used by typed dialogue displays.
                if (ctx.Game != BethesdaGame.FalloutNewVegas ||
                    !TryUniformVariantSize(union, ctx, out var runOnSize) ||
                    runOnSize != 4 ||
                    FindSiblingRawValue(siblings, "Type") is not { } typeRaw ||
                    typeRaw is < byte.MinValue or > byte.MaxValue ||
                    FindSiblingRawValue(siblings, "Function") is not { } functionRaw ||
                    functionRaw is < ushort.MinValue or > ushort.MaxValue)
                {
                    return false;
                }

                var rawNode = DecodeRawConditionValue(label, data, offset, limit, ctx, leMap);
                var runOn = Convert.ToUInt32(rawNode.RawValue, CultureInfo.InvariantCulture);
                var semantic = DialogueConditionRunOnPolicy.Format(
                    (byte)typeRaw, (ushort)functionRaw, runOn, ctx.Game);
                node = rawNode with
                {
                    Value = semantic.StartsWith("Unknown (", StringComparison.Ordinal)
                        ? semantic
                        : $"{semantic} ({runOn})"
                };
                return true;
            }
            case "wbConditionParam3Decider":
            {
                if (FindSiblingRawValue(siblings, "Run On") is not { } runOnRaw ||
                    runOnRaw < 0 ||
                    runOnRaw >= union.Variants.Count ||
                    union.Variants[(int)runOnRaw] is not FieldDef selected ||
                    !IsMemberActive(selected, ctx) ||
                    FixedPrimSize(selected) != 4)
                {
                    // The uniform fallback preserves today's generic signed-int interpretation
                    // when Run On is absent, corrupt, or outside the generated schema's domain.
                    return false;
                }

                var (value, raw, fid) = DecodeScalar(selected, data, offset, limit, ctx, leMap, out _);
                var selectedLabel = selected.Name is { Length: > 0 } name &&
                                    !string.Equals(name, label, StringComparison.Ordinal)
                    ? $"{label} ({name})"
                    : label;
                node = new DecodedNode
                {
                    Label = selectedLabel,
                    Value = value,
                    RawValue = raw,
                    FormId = fid
                };
                return true;
            }
            case "wbConditionReferenceDecider":
            {
                if (FindSiblingRawValue(siblings, "Function") is not { } functionRaw ||
                    FindSiblingRawValue(siblings, "Run On") is not { } runOnRaw ||
                    !DialogueConditionReferencePolicy.IsSemanticReferenceSlot(
                        (ushort)functionRaw, (uint)runOnRaw, ctx.Game))
                {
                    // Variant 0 is the raw/unused u32. The uniform-union fallback preserves
                    // nonzero ignored storage without manufacturing a FormID link.
                    return false;
                }

                var (value, raw, fid) = DecodeFormId(data, offset, limit, ctx, leMap);
                node = new DecodedNode { Label = label, Value = value, RawValue = raw, FormId = fid };
                return true;
            }
            case "wbConditionCompValueDecider":
            {
                if (ctx.ConditionTable is null ||
                    FindSiblingRawValue(siblings, "Type") is not { } typeRaw)
                {
                    return false;
                }

                if ((typeRaw & 0x04) != 0)
                {
                    var (value, raw, fid) = DecodeFormId(data, offset, limit, ctx, leMap);
                    node = new DecodedNode { Label = label, Value = value, RawValue = raw, FormId = fid };
                }
                else
                {
                    var f = ctx.BigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(offset, 4))
                        : BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4));
                    node = new DecodedNode
                    {
                        Label = label,
                        Value = f.ToString(CultureInfo.InvariantCulture),
                        RawValue = f
                    };
                }

                return true;
            }
            case "wbConditionParam1Decider":
            case "wbConditionParam2Decider":
            {
                if (ctx.ConditionTable is not { } table ||
                    FindSiblingRawValue(siblings, "Function") is not { } functionRaw)
                {
                    node = DecodeRawConditionValue(label, data, offset, limit, ctx, leMap);
                    return true;
                }

                var paramIndex = union.DeciderName == "wbConditionParam1Decider" ? 0 : 1;
                var functionIndex = (ushort)functionRaw;
                if (FindSiblingRawValue(siblings, "Type") is not { } rawType)
                {
                    node = DecodeRawConditionValue(label, data, offset, limit, ctx, leMap);
                    return true;
                }

                var conditionType = (byte)rawType;
                uint? runOn = null;
                if (ctx.Game is BethesdaGame.Fallout4 or BethesdaGame.Fallout76)
                {
                    // FO4-family CTDA is exactly 32 bytes. At param1/param2 the later Run On field is
                    // respectively 8/4 bytes ahead. Read it only when the full modern tail remains;
                    // truncated/older layouts leave it unknown so the one Run-On-dependent exception
                    // fails closed rather than borrowing bytes from an adjacent field.
                    var runOnDelta = paramIndex == 0 ? 8 : 4;
                    var requiredRemaining = paramIndex == 0 ? 20 : 16;
                    if (limit - offset >= requiredRemaining)
                    {
                        runOn = ctx.BigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + runOnDelta, 4))
                            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + runOnDelta, 4));
                    }
                }

                uint? parameter1Value = null;
                var parameter1Raw = FindSiblingRawValue(siblings, "Parameter #1");
                if (paramIndex == 1 && parameter1Raw is >= 0 and <= uint.MaxValue)
                {
                    parameter1Value = (uint)parameter1Raw.Value;
                }

                if (!table.TryClassifyParam(
                        functionIndex,
                        paramIndex,
                        conditionType,
                        runOn,
                        parameter1Value,
                        out var paramKind))
                {
                    // xEdit's generated CTDA schemas sometimes leave this union empty (or nest an
                    // unmodeled decider) even though the on-disk parameter slot is always one u32.
                    // Stopping at that union would misalign every following field. Preserve the exact
                    // bits without attaching FormID semantics, then continue through Run On/Reference.
                    node = DecodeRawConditionValue(label, data, offset, limit, ctx, leMap);
                    return true;
                }

                if (paramKind == ConditionParamKind.FormId)
                {
                    var (value, raw, fid) = DecodeFormId(data, offset, limit, ctx, leMap);
                    node = new DecodedNode { Label = label, Value = value, RawValue = raw, FormId = fid };
                }
                else
                {
                    var v = ctx.BigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4))
                        : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                    node = new DecodedNode
                    {
                        Label = label,
                        Value = v.ToString(CultureInfo.InvariantCulture),
                        RawValue = v
                    };
                }

                return true;
            }
            default:
                return false;
        }
    }

    private static DecodedNode DecodeRawConditionValue(
        string label, byte[] data, int offset, int limit, DecodeContext ctx,
        IReadOnlyDictionary<int, LeFieldKind>? leMap)
    {
        var (value, raw, _) = DecodeScalar(
            new FieldDef(PrimType.U32), data, offset, limit, ctx, leMap, out _);
        return new DecodedNode { Label = label, Value = value, RawValue = raw };
    }

    /// <summary>
    ///     An already-decoded sibling's numeric raw value by label, searching backwards (the
    ///     decider fields precede the unions they steer in every CTDA layout).
    /// </summary>
    private static long? FindSiblingRawValue(List<DecodedNode> siblings, string label)
    {
        for (var i = siblings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(siblings[i].Label, label, StringComparison.Ordinal) &&
                siblings[i].RawValue is { } raw)
            {
                return raw switch
                {
                    byte b => b,
                    sbyte sb => sb,
                    ushort us => us,
                    short s => s,
                    uint u => u,
                    int n => n,
                    long l => l,
                    _ => null
                };
            }
        }

        return null;
    }

    /// <summary>
    ///     True when every union variant has the same known fixed width (so decoding any one keeps the
    ///     enclosing struct byte-aligned). Returns false for variable/mixed-width unions (e.g. GMST DATA).
    /// </summary>
    private static bool TryUniformVariantSize(UnionDef union, DecodeContext ctx, out int size)
    {
        size = 0;
        int? common = null;
        foreach (var variant in union.Variants)
        {
            if (!IsMemberActive(variant, ctx))
            {
                continue;
            }

            if (TryFixedSize(variant, ctx) is not { } s)
            {
                return false;
            }

            common ??= s;
            if (common != s)
            {
                return false;
            }
        }

        size = common ?? 0;
        return size > 0;
    }

    /// <summary>The fixed byte width of a member, or null when it is variable/unknown (strings, dynamic arrays).</summary>
    private static int? TryFixedSize(MemberDef member, DecodeContext ctx)
    {
        if (!IsMemberActive(member, ctx))
        {
            return 0;
        }

        return member switch
        {
            FormIdDef => 4,
            UnusedDef unused => unused.Size,
            FieldDef field => FixedPrimSize(field),
            StructDef structDef => SumFixedSizes(structDef.Members, ctx),
            ArrayDef { Count: > 0 } array when TryFixedSize(array.Element, ctx) is { } elementSize =>
                checked(array.Count * elementSize),
            _ => null
        };
    }

    private static int? SumFixedSizes(IReadOnlyList<MemberDef> members, DecodeContext ctx)
    {
        var total = 0;
        foreach (var m in members)
        {
            if (TryFixedSize(m, ctx) is not { } s)
            {
                return null;
            }

            total += s;
        }

        return total;
    }

    private static int? FixedPrimSize(FieldDef field)
    {
        return field.Type switch
        {
            PrimType.U8 or PrimType.S8 => 1,
            PrimType.U16 or PrimType.S16 or PrimType.Half => 2,
            PrimType.U24 => 3,
            PrimType.U32 or PrimType.S32 or PrimType.Float or PrimType.FormId or PrimType.LString => 4,
            PrimType.U64 or PrimType.S64 or PrimType.Double => 8,
            PrimType.ByteArray => field.FixedSize,
            _ => null
        };
    }

    /// <summary>
    ///     Reads a u32 honoring a conversion-schema fixed-LE override at this offset: <c>WordSwapped</c>
    ///     reconstructs the middle-endian value (two big-endian u16 words in LE order,
    ///     <c>(BE16@o+2 &lt;&lt; 16) | BE16@o</c>, matching <c>SubrecordSchemaReader.ReadUInt32WordSwapped</c>);
    ///     <c>LittleEndian</c> forces a plain LE read; otherwise the context's endianness applies.
    /// </summary>
    private static uint ReadU32Overridden(byte[] data, int offset, DecodeContext ctx, LeFieldKind? le)
    {
        return le switch
        {
            LeFieldKind.WordSwapped =>
                ((uint)BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2, 2)) << 16)
                | BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2)),
            LeFieldKind.LittleEndian => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)),
            _ => ctx.ReadU32(data, offset)
        };
    }

    /// <summary>Signed counterpart of <see cref="ReadU32Overridden" /> (Int32LittleEndian fields).</summary>
    private static int ReadS32Overridden(byte[] data, int offset, DecodeContext ctx, LeFieldKind? le)
    {
        return le switch
        {
            LeFieldKind.LittleEndian => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)),
            LeFieldKind.WordSwapped => unchecked((int)ReadU32Overridden(data, offset, ctx, LeFieldKind.WordSwapped)),
            _ => ctx.ReadS32(data, offset)
        };
    }

    /// <summary>
    ///     16-bit counterpart of <see cref="ReadU32Overridden" /> (UInt16LittleEndian fields; no
    ///     word-swapped u16 exists, so only the plain-LE override applies).
    /// </summary>
    private static ushort ReadU16Overridden(byte[] data, int offset, DecodeContext ctx, LeFieldKind? le)
    {
        return le == LeFieldKind.LittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2))
            : ctx.ReadU16(data, offset);
    }

    /// <summary>Signed counterpart of <see cref="ReadU16Overridden" />.</summary>
    private static short ReadS16Overridden(byte[] data, int offset, DecodeContext ctx, LeFieldKind? le)
    {
        return le == LeFieldKind.LittleEndian
            ? BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2))
            : ctx.ReadS16(data, offset);
    }

    private static (string? Value, object? Raw, uint? FormId) DecodeScalar(
        FieldDef field, byte[] data, int offset, int limit, DecodeContext ctx,
        IReadOnlyDictionary<int, LeFieldKind>? leMap, out int size)
    {
        var available = limit - offset;

        // A fixed-LE override for the field starting at this offset (word-swap or plain little-endian even
        // on Xbox). Null in the overwhelmingly common case — then the reads below use ctx's endianness.
        var le = leMap is not null && leMap.TryGetValue(offset, out var kind) ? (LeFieldKind?)kind : null;

        switch (field.Type)
        {
            case PrimType.U8:
                return Fits(1, available, out size) ? Integer(field, data[offset]) : NoValue;
            case PrimType.S8:
                return Fits(1, available, out size) ? Integer(field, (sbyte)data[offset]) : NoValue;
            case PrimType.U16:
                return Fits(2, available, out size)
                    ? Integer(field, ReadU16Overridden(data, offset, ctx, le))
                    : NoValue;
            case PrimType.S16:
                return Fits(2, available, out size)
                    ? Integer(field, ReadS16Overridden(data, offset, ctx, le))
                    : NoValue;
            case PrimType.U24:
                return Fits(3, available, out size) ? Integer(field, ctx.ReadU24(data, offset)) : NoValue;
            case PrimType.U32:
                return Fits(4, available, out size)
                    ? Integer(field, ReadU32Overridden(data, offset, ctx, le))
                    : NoValue;
            case PrimType.S32:
                return Fits(4, available, out size)
                    ? Integer(field, ReadS32Overridden(data, offset, ctx, le))
                    : NoValue;
            case PrimType.U64:
                return Fits(8, available, out size) ? Integer(field, (long)ctx.ReadU64(data, offset)) : NoValue;
            case PrimType.S64:
                return Fits(8, available, out size) ? Integer(field, ctx.ReadS64(data, offset)) : NoValue;
            case PrimType.FormId:
                return Fits(4, available, out size) ? DecodeFormId(data, offset, limit, ctx, leMap) : NoValue;
            case PrimType.Float:
                return Fits(4, available, out size) ? Scalar(ctx.ReadFloat(data, offset)) : NoValue;
            case PrimType.Double:
                return Fits(8, available, out size) ? Scalar(ctx.ReadDouble(data, offset)) : NoValue;
            case PrimType.Half:
                return Fits(2, available, out size) ? Scalar((float)ctx.ReadHalf(data, offset)) : NoValue;
            case PrimType.ZString:
            case PrimType.StringKC:
            {
                // FixedSize 0 (or null) marks a variable-length / null-terminated string — read what's
                // available (for a signed member that's the whole subrecord). Only a POSITIVE FixedSize is a
                // fixed-width field. (Treating 0 literally would read an empty string, silently dropping every
                // EDID/FULL/NNAM/DESC value.)
                size = field.FixedSize is { } fs and > 0 ? fs : available;
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
            default:
                // PrimType.ByteArray and any unmodeled type surface as raw bytes.
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

    private static (string? Value, object? Raw, uint? FormId) Scalar(object value)
    {
        return (Convert.ToString(value, CultureInfo.InvariantCulture), value, null);
    }

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

    private static DecodedNode RawNode(string label, string? sig, byte[] data)
    {
        return new DecodedNode
        {
            Label = label,
            Value = $"<{data.Length} bytes>",
            RawValue = data,
            Signature = sig,
            IsRaw = true
        };
    }

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
    private static bool IsMemberActive(MemberDef member, DecodeContext ctx)
    {
        // Unknown header versions deliberately preserve the historical length-bounded behavior. Once
        // the version is known (including zero), both bounds apply: minimum inclusive, maximum exclusive.
        if (ctx.FormVersion is not { } actual)
        {
            return true;
        }

        return (member.MinFormVersion is not { } minimum || actual >= minimum) &&
               (member.MaxFormVersionExclusive is not { } maximumExclusive || actual < maximumExclusive);
    }

    private sealed class DecodeContext(
        bool bigEndian,
        FormIdNameResolver? resolveName,
        BethesdaGame game,
        string recordSignature,
        ushort? formVersion)
    {
        private ConditionFunctionTable? _conditionTable;

        public bool BigEndian { get; } = bigEndian;
        public FormIdNameResolver? ResolveName { get; } = resolveName;
        public BethesdaGame Game { get; } = game;
        public ushort? FormVersion { get; } = formVersion;

        /// <summary>
        ///     The top-level record signature (e.g. "WEAP", "RGDL"), used to resolve the conversion
        ///     schema's per-subrecord fixed-LE layout for big-endian records.
        /// </summary>
        public string RecordSignature { get; } = recordSignature;

        /// <summary>
        ///     The game's condition-function table, or null when the game is unknown.
        ///     Unknown parameter semantics remain visible as raw four-byte values.
        /// </summary>
        public ConditionFunctionTable? ConditionTable =>
            Game == BethesdaGame.Unknown
                ? null
                : _conditionTable ??= ConditionFunctionTable.For(Game);

        public ushort ReadU16(byte[] d, int o)
        {
            return BigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o, 2));
        }

        public short ReadS16(byte[] d, int o)
        {
            return BigEndian
                ? BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(o, 2))
                : BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o, 2));
        }

        public int ReadU24(byte[] d, int o)
        {
            return BigEndian
                ? (d[o] << 16) | (d[o + 1] << 8) | d[o + 2]
                : d[o] | (d[o + 1] << 8) | (d[o + 2] << 16);
        }

        public uint ReadU32(byte[] d, int o)
        {
            return BigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o, 4));
        }

        public int ReadS32(byte[] d, int o)
        {
            return BigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(o, 4))
                : BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o, 4));
        }

        public ulong ReadU64(byte[] d, int o)
        {
            return BigEndian
                ? BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(o, 8))
                : BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o, 8));
        }

        public long ReadS64(byte[] d, int o)
        {
            return BigEndian
                ? BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(o, 8))
                : BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(o, 8));
        }

        public float ReadFloat(byte[] d, int o)
        {
            return BigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(o, 4))
                : BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o, 4));
        }

        public double ReadDouble(byte[] d, int o)
        {
            return BigEndian
                ? BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(o, 8))
                : BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(o, 8));
        }

        public Half ReadHalf(byte[] d, int o)
        {
            return BigEndian
                ? BinaryPrimitives.ReadHalfBigEndian(d.AsSpan(o, 2))
                : BinaryPrimitives.ReadHalfLittleEndian(d.AsSpan(o, 2));
        }
    }
}
