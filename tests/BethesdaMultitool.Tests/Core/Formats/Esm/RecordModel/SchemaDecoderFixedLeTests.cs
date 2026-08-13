using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Generated;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     F8 — hybrid-endianness fixed-LE overlay for the generated-schema decoder. Some Fallout 3 / New
///     Vegas subrecord fields are stored little-endian even on big-endian Xbox 360 (FormIDs like WEAP DNAM
///     Projectile, QUST INDX, RGDL DATA's word-swapped bone count, the BPND count/reference fields). The
///     conversion schema (<see cref="SubrecordSchemaRegistry" />) is the single source of that truth; the
///     decoder consults it live via <see cref="SubrecordSchemaRegistry.GetFixedLeLayout" /> so a big-endian
///     decode reads exactly those fields little-endian instead of byte-swapping them. These tests pin the
///     helper, the decoder mechanism (over EVERY registered fixed-LE field), and the alignment of the
///     generated FO3/FNV schemas with the conversion schema.
/// </summary>
public class SchemaDecoderFixedLeTests
{
    // ---------------------------------------------------------------------------------------------
    // GetFixedLeLayout — the System A single-source query
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void GetFixedLeLayout_QustIndx_MarksOffset0LittleEndian()
    {
        var map = SubrecordSchemaRegistry.GetFixedLeLayout("INDX", "QUST", 2);

        Assert.NotNull(map);
        Assert.Equal(LeFieldKind.LittleEndian, map![0]);
    }

    [Fact]
    public void GetFixedLeLayout_RgdlData_MarksOffset0WordSwapped()
    {
        var map = SubrecordSchemaRegistry.GetFixedLeLayout("DATA", "RGDL", 14);

        Assert.NotNull(map);
        Assert.Equal(LeFieldKind.WordSwapped, map![0]);
    }

    [Fact]
    public void GetFixedLeLayout_RecordAgnosticBpnd_ResolvesRegardlessOfRecordType()
    {
        // BPND is registered with a null record type — it must resolve under its real parent (BPTD) via
        // the length key, and it carries several fixed-LE FormID/count fields.
        var map = SubrecordSchemaRegistry.GetFixedLeLayout("BPND", "BPTD", 84);

        Assert.NotNull(map);
        Assert.NotEmpty(map!);
    }

    [Fact]
    public void GetFixedLeLayout_NonFixedLeSubrecord_ReturnsNull()
    {
        // EDID is a plain string subrecord with no fixed-LE fields anywhere.
        Assert.Null(SubrecordSchemaRegistry.GetFixedLeLayout("EDID", "WEAP", 8));
    }

    [Fact]
    public void GetFixedLeLayout_IsCached_ReturnsSameReference()
    {
        var a = SubrecordSchemaRegistry.GetFixedLeLayout("INDX", "QUST", 2);
        var b = SubrecordSchemaRegistry.GetFixedLeLayout("INDX", "QUST", 2);

        Assert.Same(a, b);
    }

    // ---------------------------------------------------------------------------------------------
    // Decoder mechanism — over EVERY registered fixed-LE field
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Enumeration_CoversAllFourFixedLeKinds()
    {
        var fields = SubrecordSchemaRegistry.EnumerateFixedLeFields().ToList();

        Assert.NotEmpty(fields);
        Assert.Contains(fields, f => f.Type == SubrecordFieldType.FormIdLittleEndian);
        Assert.Contains(fields, f => f.Type == SubrecordFieldType.UInt16LittleEndian);
        Assert.Contains(fields, f => f.Type == SubrecordFieldType.Int32LittleEndian);
        Assert.Contains(fields, f => f.Type == SubrecordFieldType.UInt32WordSwapped);
    }

    [Fact]
    public void Decoder_ReadsEveryFixedLeField_LittleEndian_OnBigEndianRecord()
    {
        // Drive one synthetic big-endian subrecord per registered fixed-LE field. The schema layout is
        // taken from the conversion schema itself (record type + subrecord + length + offset), so the
        // decoder's walk and the overlay align by construction — this isolates the READ mechanism.
        var verified = 0;
        foreach (var f in SubrecordSchemaRegistry.EnumerateFixedLeFields().Where(f => f.DataLength is { } d && d > 0))
        {
            var recordType = f.RecordType ?? "TEST";
            var length = f.DataLength!.Value;
            var value = SampleValue(f.Type);

            // Bytes: the fixed-LE value encoded on-disk as Xbox stores it, at its offset; rest zero.
            var bytes = new byte[length];
            WriteFixedLe(bytes, f.Offset, f.Type, value);

            // Real schema (fixed-LE signature) → overlay applies → little-endian read.
            var real = DecodeField(recordType, f, bytes, out var realNode);
            Assert.True(real, $"{recordType}/{f.Signature}@{f.Offset} did not decode a node");
            AssertDecoded(f, realNode!, bytes, value, true);

            // Control (a signature with no fixed-LE registration) → no overlay → byte-swapped read. Proves
            // the overlay — not some incidental path — is what changed the value.
            var control = DecodeField("ZZZZ", f with { Signature = "ZZZZ" }, bytes, out var controlNode);
            Assert.True(control, "control decode produced no node");
            AssertDecoded(f, controlNode!, bytes, value, false);

            verified++;
        }

        Assert.True(verified >= 10, $"expected to exercise the full fixed-LE set, only ran {verified}");
    }

    // ---------------------------------------------------------------------------------------------
    // Drift guard — the generated FO3/FNV schemas must agree with the conversion schema on layout
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void GeneratedSchemas_AgreeWithConversionSchema_AtEveryFixedLeOffset()
    {
        var misaligned = new List<string>();
        var verifiedInAtLeastOneGame = 0;

        foreach (var f in SubrecordSchemaRegistry.EnumerateFixedLeFields())
        {
            var status = Classify(Fallout3Schema.Records, f) | Classify(FalloutNvSchema.Records, f);

            if ((status & Alignment.Misaligned) != 0)
            {
                misaligned.Add($"{f.RecordType ?? "*"}/{f.Signature}@{f.Offset} (w{f.Width}, {f.FieldName})");
            }

            if ((status & Alignment.Aligned) != 0)
            {
                verifiedInAtLeastOneGame++;
            }
        }

        // A field boundary of the WRONG width at a fixed-LE offset means the generated schema drifted from
        // the conversion schema — the overlay would then force-LE the wrong bytes. That must never happen.
        Assert.True(misaligned.Count == 0,
            "generated schema layout drifted from the conversion schema at:\n  " + string.Join("\n  ", misaligned));

        // Sanity: the guard actually pinned real fields (not silently skipping everything as unmodeled).
        Assert.True(verifiedInAtLeastOneGame > 0, "drift guard verified zero fields — the walker is not matching");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static uint SampleValue(SubrecordFieldType type)
    {
        return type switch
        {
            SubrecordFieldType.UInt16LittleEndian => 0x1234,
            SubrecordFieldType.FormIdLittleEndian => 0x01ABCDEF,
            SubrecordFieldType.Int32LittleEndian => 0x0055AA33,
            SubrecordFieldType.UInt32WordSwapped => 0x000A0015,
            _ => 0x0BADF00D
        };
    }

    private static void WriteFixedLe(byte[] bytes, int offset, SubrecordFieldType type, uint value)
    {
        switch (type)
        {
            case SubrecordFieldType.UInt16LittleEndian:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)value);
                break;
            case SubrecordFieldType.UInt32WordSwapped:
                // Xbox stores two big-endian u16 words in LE word order: [LO_BE][HI_BE].
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), (ushort)value);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 2), (ushort)(value >> 16));
                break;
            default: // FormIdLittleEndian / Int32LittleEndian — plain little-endian 4 bytes
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
                break;
        }
    }

    /// <summary>
    ///     Builds a one-subrecord record whose framed struct places the fixed-LE field at its
    ///     offset, decodes it big-endian, and returns the field's decoded node.
    /// </summary>
    private static bool DecodeField(
        string recordType, SubrecordSchemaRegistry.FixedLeFieldInfo f, byte[] bytes, out DecodedNode? node)
    {
        var members = new List<MemberDef>();
        if (f.Offset > 0)
        {
            members.Add(new UnusedDef(f.Offset));
        }

        members.Add(f.Type == SubrecordFieldType.FormIdLittleEndian
            ? new FormIdDef { Name = "Target" }
            : new FieldDef(GeneratedPrim(f.Type)) { Name = "Target" });

        var schema = new RecordDef(recordType,
            [new StructDef(members) { Signature = f.Signature, Name = "Sub" }]);

        var tree = SchemaRecordDecoder.Decode(schema, [new RawSubrecord(f.Signature, bytes)], true);
        node = tree.Count > 0 ? tree[0].Children.FirstOrDefault(n => n.Label == "Target") : null;
        return node is not null;
    }

    private static void AssertDecoded(
        SubrecordSchemaRegistry.FixedLeFieldInfo f, DecodedNode node, byte[] bytes, uint value, bool expectLittleEndian)
    {
        if (f.Type == SubrecordFieldType.UInt16LittleEndian)
        {
            // Little-endian read yields the intended value; a plain big-endian read yields the swapped bytes.
            long expected = expectLittleEndian ? value : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(f.Offset));
            Assert.Equal(expected, Convert.ToInt64(node.RawValue));
            return;
        }

        // 4-byte fields (FormId LE, Int32 LE, word-swapped u32). The control read is exactly what a normal
        // big-endian decode makes of the same on-disk bytes.
        var controlBe = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(f.Offset));

        if (f.Type == SubrecordFieldType.FormIdLittleEndian)
        {
            Assert.Equal(expectLittleEndian ? value : controlBe, node.FormId);
            return;
        }

        long expectedInt = expectLittleEndian ? unchecked((int)value) : unchecked((int)controlBe);
        Assert.Equal(expectedInt, Convert.ToInt64(node.RawValue));
    }

    private static PrimType GeneratedPrim(SubrecordFieldType type)
    {
        return type switch
        {
            SubrecordFieldType.UInt16LittleEndian => PrimType.U16,
            SubrecordFieldType.Int32LittleEndian => PrimType.S32,
            SubrecordFieldType.UInt32WordSwapped => PrimType.U32,
            _ => PrimType.U32
        };
    }

    private static Alignment Classify(IReadOnlyList<RecordDef> game, SubrecordSchemaRegistry.FixedLeFieldInfo f)
    {
        // Find the subrecord member. When the conversion schema is record-agnostic (null), search every record.
        foreach (var record in game)
        {
            if (f.RecordType is { } rt && record.Signature != rt)
            {
                continue;
            }

            var member = FindSubrecordMember(record.Members, f.Signature);
            if (member is null)
            {
                continue;
            }

            // Not modeled precisely enough to verify → neither aligned nor misaligned (a coverage gap, not drift).
            if (!TryFieldAt(member, f.Offset, out var field))
            {
                continue;
            }

            // The overlay only ever changes an integer/FormID read. If the generated schema surfaces this
            // offset as raw bytes / a string / a float, the fixed-LE mark is inert there — no hazard, so it
            // is not "drift" even though the widths differ (e.g. RACE ATTR modeled as an unused ByteArray).
            if (!IsOverlayAffected(field!, out var width))
            {
                continue;
            }

            // A swappable field of the WRONG width at a fixed-LE offset IS drift: the overlay would then
            // fire on the wrong field boundary (e.g. force-LE only half of a FormID split into two u16s).
            return width == f.Width ? Alignment.Aligned : Alignment.Misaligned;
        }

        return Alignment.None;
    }

    /// <summary>True when the member is a type the fixed-LE overlay actually adjusts (integer or FormID).</summary>
    private static bool IsOverlayAffected(MemberDef member, out int width)
    {
        switch (member)
        {
            case FormIdDef:
                width = 4;
                return true;
            case FieldDef { Type: PrimType.U16 or PrimType.S16 } f16:
                width = PrimWidth(f16) ?? 2;
                return true;
            case FieldDef { Type: PrimType.U32 or PrimType.S32 or PrimType.FormId } f32:
                width = PrimWidth(f32) ?? 4;
                return true;
            default:
                width = 0;
                return false;
        }
    }

    private static MemberDef? FindSubrecordMember(IReadOnlyList<MemberDef> members, string signature)
    {
        foreach (var member in members)
        {
            if (member.Signature == signature)
            {
                return member;
            }

            var nested = member switch
            {
                StructDef s => FindSubrecordMember(s.Members, signature),
                ArrayDef a => FindSubrecordMember([a.Element], signature),
                UnionDef u => FindSubrecordMember(u.Variants, signature),
                _ => null
            };
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    ///     Walks the modeled subrecord's fields in stream order, mirroring the decoder's size accounting,
    ///     and returns the field member that starts exactly at <paramref name="target" />. Returns false
    ///     (unverifiable — treated as a coverage gap, never a failure) if a variable-width or unsupported
    ///     member is reached at or before the target, or no field begins exactly there.
    /// </summary>
    private static bool TryFieldAt(MemberDef subrecord, int target, out MemberDef? field)
    {
        field = null;

        // Single-field subrecord (e.g. QUST INDX, a lone FormID).
        if (subrecord is FieldDef or FormIdDef)
        {
            if (target != 0)
            {
                return false;
            }

            field = subrecord;
            return true;
        }

        return subrecord is StructDef structDef && TryFieldAtWithin(structDef.Members, target, out field);
    }

    private static bool TryFieldAtWithin(IReadOnlyList<MemberDef> members, int target, out MemberDef? field)
    {
        field = null;
        var offset = 0;
        foreach (var member in members)
        {
            if (member is UnusedDef unused)
            {
                offset += unused.Size;
                continue;
            }

            if (member is StructDef nested)
            {
                // A nested struct may itself contain the target — recurse when the target falls inside it.
                if (FixedWidth(nested) is not { } nestedWidth)
                {
                    return false; // variable nested struct — can't verify this or later offsets
                }

                if (target >= offset && target < offset + nestedWidth)
                {
                    return TryFieldAtWithin(nested.Members, target - offset, out field);
                }

                offset += nestedWidth;
                continue;
            }

            if (offset == target)
            {
                field = member;
                return true;
            }

            if (FixedWidth(member) is not { } w)
            {
                return false; // variable/unsupported member — can't verify offsets past here
            }

            if (offset > target)
            {
                return false; // walked past the target without a field starting there
            }

            offset += w;
        }

        return false;
    }

    private static int? FixedWidth(MemberDef member)
    {
        return member switch
        {
            FormIdDef => 4,
            UnusedDef u => u.Size,
            FieldDef field => PrimWidth(field),
            StructDef s => SumFixed(s.Members),
            ArrayDef { Count: > 0 } a => FixedWidth(a.Element) is { } ew ? a.Count * ew : null,
            _ => null
        };
    }

    private static int? SumFixed(IReadOnlyList<MemberDef> members)
    {
        var total = 0;
        foreach (var m in members)
        {
            if (FixedWidth(m) is not { } w)
            {
                return null;
            }

            total += w;
        }

        return total;
    }

    private static int? PrimWidth(FieldDef field)
    {
        return field.Type switch
        {
            PrimType.U8 or PrimType.S8 => 1,
            PrimType.U16 or PrimType.S16 or PrimType.Half => 2,
            PrimType.U24 => 3,
            PrimType.U32 or PrimType.S32 or PrimType.Float or PrimType.FormId or PrimType.LString => 4,
            PrimType.U64 or PrimType.S64 or PrimType.Double => 8,
            PrimType.ByteArray => field.FixedSize is { } fs and > 0 ? fs : null,
            _ => null
        };
    }

    // ---- generated-schema layout walker (drift guard) ----

    [Flags]
    private enum Alignment
    {
        None = 0,
        Aligned = 1,
        Misaligned = 2
    }
}