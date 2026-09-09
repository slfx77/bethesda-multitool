namespace BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;

/// <summary>
///     Narrow runtime correction for two unnamed wbFloat leaves in the generated Skyrim WATR DNAM.
///     Their raw placeholders otherwise stop decoding at +28, hiding the existing Depth Properties
///     schema. Retain the generated schema unchanged on disk, and leave every other record untouched.
///     TESV's fixed 228-byte load and the surrounding declared fields prove both four-byte widths.
/// </summary>
internal static class SkyrimWaterSchema
{
    internal static IReadOnlyList<RecordDef> CompleteKnownFloatWidths(IReadOnlyList<RecordDef> records)
    {
        return records.Select(record => record.Signature == "WATR"
            ? record with
            {
                Members = record.Members.Select(member => member is StructDef { Signature: "DNAM" } dnam
                    ? CompleteDnam(dnam)
                    : member).ToArray()
            }
            : record).ToArray();
    }

    private static StructDef CompleteDnam(StructDef dnam)
    {
        // Exact known inline structure, not a global interpretation of arbitrary raw builders.
        // A regenerated layout that no longer matches keeps its own authoritative definition.
        if (dnam.Members.Count != 37 ||
            dnam.Members[7] is not RawMemberDef
            {
                Builder: "wbFloat", Signature: null, MinFormVersion: null, MaxFormVersionExclusive: null
            } ||
            dnam.Members[22] is not RawMemberDef
            {
                Builder: "wbFloat", Signature: null, MinFormVersion: null, MaxFormVersionExclusive: null
            } ||
            !HasKnownFraming(dnam)) return dnam;

        var members = dnam.Members.ToArray();
        members[7] = new FieldDef(PrimType.Float) { Name = "Unknown float +28" };
        members[22] = new FieldDef(PrimType.Float) { Name = "Unknown float +136" };
        return dnam with { Members = members };
    }

    private static bool HasKnownFraming(StructDef dnam)
    {
        var members = dnam.Members;
        if (!IsNamedFloat(members[6], "Water Properties - Fresnel Amount") ||
            !IsNamedFloat(members[8], "Fog Properties - Above Water - Fog Distance - Near Plane") ||
            !IsNamedFloat(members[9], "Fog Properties - Above Water - Fog Distance - Far Plane") ||
            !IsNamedFloat(members[21], "Fog Properties - Above Water - Fog Amount") ||
            members[23] is not StructDef { Name: "Fog Properties - Under Water", Members.Count: 3 } underwater ||
            !IsNamedFloat(underwater.Members[0], "Fog Amount") ||
            !IsNamedFloat(underwater.Members[1], "Fog Distance - Near Plane") ||
            !IsNamedFloat(underwater.Members[2], "Fog Distance - Far Plane") ||
            members[34] is not StructDef { Name: "Depth Properties", Members.Count: 4 } depth ||
            !IsNamedFloat(depth.Members[0], "Reflections") ||
            !IsNamedFloat(depth.Members[1], "Refraction") ||
            !IsNamedFloat(depth.Members[2], "Normals") ||
            !IsNamedFloat(depth.Members[3], "Specular Lighting") ||
            members[36] is not RawMemberDef { Builder: "IsSSE" }) return false;

        // Revalidate byte framing, including nested color/padding widths, so a same-count
        // regenerated schema with moved fields cannot receive the historical replacement.
        var offset = 0;
        for (var index = 0; index < 36; index++)
        {
            var expected = index switch
            {
                7 => 28, 8 => 32, 9 => 36, 21 => 132, 22 => 136,
                23 => 140, 34 => 208, 35 => 224, _ => -1
            };
            if (expected >= 0 && offset != expected) return false;
            var width = index is 7 or 22 ? 4 : KnownInlineWidth(members[index]);
            if (width <= 0 || width > 228 - offset) return false;
            offset += width;
        }

        return offset == 228;
    }

    private static bool IsNamedFloat(MemberDef member, string name)
    {
        return member is FieldDef { Type: PrimType.Float } field && field.Name == name;
    }

    private static int KnownInlineWidth(MemberDef member)
    {
        if (member.Signature is not null || member.MinFormVersion is not null ||
            member.MaxFormVersionExclusive is not null) return -1;
        if (member is FieldDef { FixedSize: null } field)
            return field.Type switch { PrimType.Float => 4, PrimType.U8 => 1, _ => -1 };
        if (member is UnusedDef { Size: > 0 and <= 3 } unused) return unused.Size;
        if (member is not StructDef structure) return -1;
        var total = 0;
        foreach (var child in structure.Members)
        {
            var width = KnownInlineWidth(child);
            if (width <= 0 || width > 228 - total) return -1;
            total += width;
        }

        return total;
    }
}
