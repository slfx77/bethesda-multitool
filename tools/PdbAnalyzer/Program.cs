using System.Text.RegularExpressions;
using PdbAnalyzer.Commands;

namespace PdbAnalyzer;

/// <summary>
///     Parses cvdump output to extract structure information for ESM conversion.
///     Finds all structures with Endian() methods - these are the critical ones for Xbox 360 -> PC conversion.
/// </summary>
internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("PdbAnalyzer - Extract structure info from cvdump output");
            Console.WriteLine();
            Console.WriteLine("Usage: PdbAnalyzer <command> [options]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  endian <cvdump-file>              Find all structures with Endian() methods");
            Console.WriteLine("  struct <cvdump-file> <name>       Get detailed field layout for a structure");
            Console.WriteLine("  search <cvdump-file> <pattern>    Search for structures matching pattern");
            Console.WriteLine(
                "  esm <cvdump-file>                 Find all ESM-related structures (FORM, CHUNK, OBJ_*, etc.)");
            Console.WriteLine("  export <cvdump-file> <output.cs>  Export Endian structures as C# code for converter");
            Console.WriteLine(
                "  formtypes <cvdump-file>           Extract ENUM_FORM_ID and cross-ref with struct sizes");
            Console.WriteLine(
                "  layouts <cvdump-file> <out.json>   Export flattened struct layouts for all FormTypes as JSON");
            return 1;
        }

        var command = args[0].ToLower();

        return command switch
        {
            "endian" when args.Length >= 2 => await FindEndianStructuresCommand.ExecuteAsync(args[1]),
            "struct" when args.Length >= 3 => await GetStructureDetailsCommand.ExecuteAsync(args[1], args[2]),
            "search" when args.Length >= 3 => await SearchStructuresCommand.ExecuteAsync(args[1], args[2]),
            "esm" when args.Length >= 2 => await FindEsmStructuresCommand.ExecuteAsync(args[1]),
            "export" when args.Length >= 3 => await ExportStructuresCommand.ExecuteAsync(args[1], args[2]),
            "formtypes" when args.Length >= 2 => await ExtractFormTypesCommand.ExecuteAsync(args[1]),
            "layouts" when args.Length >= 3 => await ExportLayoutsCommand.ExecuteAsync(args[1], args[2]),
            _ => ShowUsage()
        };
    }

    private static int ShowUsage()
    {
        Console.WriteLine("Invalid arguments. Run without arguments for help.");
        return 1;
    }
}

/// <summary>
///     Parses cvdump output format to extract structure definitions.
/// </summary>
internal partial class CvdumpParser
{
    private readonly Dictionary<uint, FieldListInfo> _fieldLists = [];
    private readonly Dictionary<uint, PointerTypeInfo> _pointerTypes = [];
    private readonly Dictionary<uint, ArrayTypeInfo> _arrayTypes = [];
    private readonly Dictionary<uint, uint> _forwardRefs = []; // forwardRefIndex → UDT actual index
    public List<StructureInfo> Structures { get; } = [];
    public Dictionary<uint, StructureInfo> StructuresByIndex { get; } = [];
    public Dictionary<string, EnumInfo> Enums { get; } = [];

    public async Task ParseAsync(string path)
    {
        var lines = await File.ReadAllLinesAsync(path);

        Console.WriteLine($"Read {lines.Length:N0} lines");

        // First pass: collect all field lists (including enum field lists) and pointer types
        Console.WriteLine("Pass 1: Collecting field lists and pointer types...");
        CollectFieldLists(lines);
        CollectPointerTypes(lines);
        CollectArrayTypes(lines);
        Console.WriteLine(
            $"  Found {_fieldLists.Count:N0} field lists, {_pointerTypes.Count:N0} pointer types, " +
            $"{_arrayTypes.Count:N0} array types");

        // Second pass: collect structures and enums
        Console.WriteLine("Pass 2: Collecting structures and enums...");
        CollectStructures(lines);
        CollectEnums(lines);
        Console.WriteLine($"  Found {Structures.Count:N0} structures, {Enums.Count:N0} enums");

        // Third pass: link structures to field lists, build index
        Console.WriteLine("Pass 3: Linking field data...");
        LinkFieldData();
        LinkEnumData();
        BuildStructureIndex();

        // Debug: count field lists with Endian method
        var endianFieldLists = _fieldLists.Values.Count(f => f.HasEndianMethod);
        Console.WriteLine($"  Field lists with Endian(): {endianFieldLists}");
    }

    private void CollectFieldLists(string[] lines)
    {
        uint currentTypeIndex = 0;
        FieldListInfo? currentFieldList = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Match type index line: "0x0001739b : Length = 1606, Leaf = 0x1203 LF_FIELDLIST"
            var typeMatch = TypeIndexRegex().Match(line);
            if (typeMatch.Success && line.Contains("LF_FIELDLIST"))
            {
                currentTypeIndex = Convert.ToUInt32(typeMatch.Groups[1].Value, 16);
                currentFieldList = new FieldListInfo { TypeIndex = currentTypeIndex };
                _fieldLists[currentTypeIndex] = currentFieldList;
                continue;
            }

            // If we're in a field list, look for members and Endian method
            if (currentFieldList != null)
            {
                // Check for end of field list (next type definition)
                if (line.StartsWith("0x") && line.Contains(" : Length = "))
                {
                    currentFieldList = null;
                    continue;
                }

                // Match base class: "list[0] = LF_BCLASS, public, type = 0xC803, offset = 0"
                var bclassMatch = BClassRegex().Match(line);
                if (bclassMatch.Success)
                {
                    var baseTypeIndex = Convert.ToUInt32(bclassMatch.Groups[1].Value, 16);
                    var baseOffset = int.Parse(bclassMatch.Groups[2].Value);
                    currentFieldList.BaseClasses.Add(new BaseClassRef(baseTypeIndex, baseOffset));
                }

                // Match member: "list[0] = LF_MEMBER, public, type = T_REAL32(0040), offset = 4"
                //               "        member name = 'fSpeed'"
                var memberMatch = MemberRegex().Match(line);
                if (memberMatch.Success)
                {
                    var typeName = memberMatch.Groups[1].Value;
                    var offset = int.Parse(memberMatch.Groups[2].Value);

                    // Next line should have member name
                    if (i + 1 < lines.Length)
                    {
                        var nameMatch = MemberNameRegex().Match(lines[i + 1]);
                        if (nameMatch.Success)
                            currentFieldList.Fields.Add(new FieldInfo
                            {
                                TypeName = typeName,
                                Offset = offset,
                                Name = nameMatch.Groups[1].Value
                            });
                    }
                }

                // Match LF_ENUMERATE: "list[0] = LF_ENUMERATE, public, value = 42, name = 'NPC__ID'"
                var enumMatch = EnumerateRegex().Match(line);
                if (enumMatch.Success)
                {
                    var value = int.Parse(enumMatch.Groups[1].Value);
                    var name = enumMatch.Groups[2].Value;
                    currentFieldList.Fields.Add(new FieldInfo
                    {
                        TypeName = "LF_ENUMERATE",
                        Offset = value,
                        Name = name
                    });
                }

                // Check for Endian method - LF_ONEMETHOD...name = 'Endian'
                if (line.Contains("LF_ONEMETHOD") && line.Contains("name = 'Endian'"))
                    currentFieldList.HasEndianMethod = true;
            }
        }
    }

    private void CollectStructures(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Match: "0x0001739c : Length = 42, Leaf = 0x1505 LF_STRUCTURE"
            // Or:    "0x0001ff61 : Length = 118, Leaf = 0x1504 LF_CLASS"
            if (!line.Contains("LF_STRUCTURE") && !line.Contains("LF_CLASS"))
                continue;

            var typeMatch = TypeIndexRegex().Match(line);
            if (!typeMatch.Success)
                continue;

            var typeIndex = Convert.ToUInt32(typeMatch.Groups[1].Value, 16);

            // Look for structure details in next few lines
            for (var j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
            {
                // Match: "Size = 204, class name = OBJ_WEAP, unique name = UOBJ_WEAP@@"
                var detailMatch = StructDetailRegex().Match(lines[j]);
                if (detailMatch.Success)
                {
                    var size = int.Parse(detailMatch.Groups[1].Value);
                    var name = detailMatch.Groups[2].Value;

                    // Capture forward references → UDT target for later resolution
                    if (size == 0)
                    {
                        var udtMatch = UdtRegex().Match(lines[j]);
                        if (udtMatch.Success)
                        {
                            var udtTarget = Convert.ToUInt32(udtMatch.Groups[1].Value, 16);
                            if (udtTarget != typeIndex)
                                _forwardRefs[typeIndex] = udtTarget;
                        }

                        break;
                    }

                    // Check for field list reference in previous lines: "field list type 0x1739b"
                    // It's typically 2 lines before the Size line
                    uint fieldListIndex = 0;
                    for (var k = j - 1; k >= Math.Max(i, j - 3); k--)
                    {
                        var fieldListMatch = FieldListRefRegex().Match(lines[k]);
                        if (fieldListMatch.Success)
                        {
                            fieldListIndex = Convert.ToUInt32(fieldListMatch.Groups[1].Value, 16);
                            break;
                        }
                    }

                    Structures.Add(new StructureInfo
                    {
                        TypeIndex = typeIndex,
                        Name = name,
                        Size = size,
                        FieldListIndex = fieldListIndex
                    });
                    break;
                }
            }
        }
    }

    private void CollectEnums(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (!line.Contains("LF_ENUM"))
                continue;

            var typeMatch = TypeIndexRegex().Match(line);
            if (!typeMatch.Success)
                continue;

            var typeIndex = Convert.ToUInt32(typeMatch.Groups[1].Value, 16);

            // Look for enum details in next few lines
            // Format: "# members = 122, type = T_INT4(0074) field list type 0xc5c3"
            //         "enum name = ENUM_FORM_ID, UDT(0x0000c5c4)"
            uint fieldListIndex = 0;
            string? enumName = null;

            for (var j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
            {
                var fieldListMatch = FieldListRefRegex().Match(lines[j]);
                if (fieldListMatch.Success)
                    fieldListIndex = Convert.ToUInt32(fieldListMatch.Groups[1].Value, 16);

                var enumNameMatch = EnumNameRegex().Match(lines[j]);
                if (enumNameMatch.Success)
                {
                    enumName = enumNameMatch.Groups[1].Value;
                    break;
                }
            }

            if (enumName != null)
                Enums[enumName] = new EnumInfo
                {
                    TypeIndex = typeIndex,
                    Name = enumName,
                    FieldListIndex = fieldListIndex
                };
        }
    }

    private void LinkFieldData()
    {
        foreach (var s in Structures)
            if (s.FieldListIndex != 0 && _fieldLists.TryGetValue(s.FieldListIndex, out var fieldList))
            {
                s.Fields.AddRange(fieldList.Fields);
                s.BaseClasses.AddRange(fieldList.BaseClasses);
                s.HasEndianMethod = fieldList.HasEndianMethod;
            }
    }

    private void LinkEnumData()
    {
        foreach (var e in Enums.Values)
            if (e.FieldListIndex != 0 && _fieldLists.TryGetValue(e.FieldListIndex, out var fieldList))
                // LF_ENUMERATE entries are stored as fields with Name and Offset (= value)
                foreach (var f in fieldList.Fields)
                    e.Members.Add(new EnumMember { Name = f.Name, Value = f.Offset });
    }

    private void CollectPointerTypes(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("LF_POINTER"))
                continue;

            var typeMatch = TypeIndexRegex().Match(line);
            if (!typeMatch.Success)
                continue;

            var typeIndex = Convert.ToUInt32(typeMatch.Groups[1].Value, 16);

            // Next 2 lines contain pointer details and element type
            for (var j = i + 1; j < Math.Min(i + 3, lines.Length); j++)
            {
                var elemMatch = PointerElementTypeRegex().Match(lines[j]);
                if (elemMatch.Success)
                {
                    var elemTypeIndex = Convert.ToUInt32(elemMatch.Groups[1].Value, 16);
                    _pointerTypes[typeIndex] = new PointerTypeInfo(typeIndex, elemTypeIndex, null);
                    break;
                }
            }
        }
    }

    /// <summary>
    ///     Collect <c>LF_ARRAY</c> leaves. Without these, every fixed-size array member resolves to
    ///     <c>("unknown", 0, …)</c> and is then dropped by the reader's readable-field filter — which
    ///     is why <c>BGSDefaultObjectManager.pObjectArray</c> (the 136-byte default-object table),
    ///     <c>BGSImpactDataSet.ppImpactData</c> and both of <c>TESCasino</c>'s model/texture arrays
    ///     were invisible despite being present in every dump.
    ///     <para>
    ///         A record's shape in the dump is:
    ///         <code>
    ///     0xNNNN : Length = 14, Leaf = 0x1503 LF_ARRAY
    ///         Element type = 0xMMMM
    ///         Index type = T_ULONG(0022)
    ///         length = 136
    ///         </code>
    ///         where <c>length</c> is the array's TOTAL size in bytes, not its element count.
    ///     </para>
    /// </summary>
    private void CollectArrayTypes(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("LF_ARRAY"))
                continue;

            var typeMatch = TypeIndexRegex().Match(lines[i]);
            if (!typeMatch.Success)
                continue;

            var typeIndex = Convert.ToUInt32(typeMatch.Groups[1].Value, 16);
            uint elementTypeIndex = 0;
            var totalSize = -1;

            for (var j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
            {
                var elemMatch = ArrayElementTypeRegex().Match(lines[j]);
                if (elemMatch.Success && elemMatch.Groups[1].Success)
                {
                    elementTypeIndex = Convert.ToUInt32(elemMatch.Groups[1].Value, 16);
                    continue;
                }

                var lengthMatch = ArrayLengthRegex().Match(lines[j]);
                if (lengthMatch.Success)
                {
                    totalSize = int.Parse(lengthMatch.Groups[1].Value);
                    break;
                }
            }

            if (totalSize >= 0)
                _arrayTypes[typeIndex] = new ArrayTypeInfo(elementTypeIndex, totalSize, null);
        }
    }

    private void BuildStructureIndex()
    {
        // Index actual (non-forward-ref) structures
        foreach (var s in Structures)
            StructuresByIndex.TryAdd(s.TypeIndex, s);

        // Register forward refs as aliases to the actual definitions
        foreach (var (fwdIndex, udtTarget) in _forwardRefs)
        {
            if (!StructuresByIndex.ContainsKey(fwdIndex) &&
                StructuresByIndex.TryGetValue(udtTarget, out var actual))
                StructuresByIndex[fwdIndex] = actual;
        }

        Console.WriteLine(
            $"  Structure index: {StructuresByIndex.Count:N0} entries ({_forwardRefs.Count:N0} forward refs resolved)");

        // Resolve pointer element type names. Iterated to a fixed point because a pointer's target
        // can itself be a pointer — TESTextureList::pTextureOffsetArray is a BSFileEntry**, and
        // naming it just "unknown" leaves a consumer unable to tell an array of file entries from
        // an array of bytes. Two passes cover T**; the loop stops as soon as nothing new resolves.
        for (var pass = 0; pass < 4; pass++)
        {
            var resolvedThisPass = 0;

            foreach (var (idx, ptr) in _pointerTypes)
            {
                if (ptr.ElementTypeName != null)
                    continue;

                var elemIdx = ptr.ElementTypeIndex;

                // Follow forward ref if needed
                if (_forwardRefs.TryGetValue(elemIdx, out var resolvedIdx))
                    elemIdx = resolvedIdx;

                string? elemName = null;
                if (StructuresByIndex.TryGetValue(elemIdx, out var pointedStruct))
                    elemName = pointedStruct.Name;
                else if (_pointerTypes.TryGetValue(elemIdx, out var innerPtr) && innerPtr.ElementTypeName != null)
                    elemName = $"{innerPtr.ElementTypeName} *";

                if (elemName == null)
                    continue;

                _pointerTypes[idx] = ptr with { ElementTypeName = elemName };
                resolvedThisPass++;
            }

            if (resolvedThisPass == 0)
                break;
        }

        // Same for arrays: name the element so a consumer can tell an array of TESForm pointers
        // from an array of bytes.
        foreach (var (idx, arr) in _arrayTypes)
        {
            var elemIdx = arr.ElementTypeIndex;
            if (_forwardRefs.TryGetValue(elemIdx, out var resolvedElemIdx))
                elemIdx = resolvedElemIdx;

            string? elemName = null;
            if (_pointerTypes.TryGetValue(elemIdx, out var elemPtr))
                elemName = elemPtr.ElementTypeName != null ? $"{elemPtr.ElementTypeName} *" : "void *";
            else if (StructuresByIndex.TryGetValue(elemIdx, out var elemStruct))
                elemName = elemStruct.Name;

            if (elemName != null)
                _arrayTypes[idx] = arr with { ElementTypeName = elemName };
        }

        Console.WriteLine($"  Array index: {_arrayTypes.Count:N0} entries");
    }

    /// <summary>
    ///     Resolves a PDB type reference to a field kind and size.
    ///     Returns (kind, size, typeDetail) for use in FlatField construction.
    /// </summary>
    public (string Kind, int Size, string? TypeDetail) ResolveFieldType(string typeName)
    {
        // Built-in primitive types
        if (typeName.StartsWith("T_"))
        {
            return typeName switch
            {
                "T_UCHAR(0020)" => ("uint8", 1, null),
                "T_RCHAR(0070)" => ("int8", 1, null),
                "T_CHAR(0010)" => ("uint8", 1, null),
                "T_BOOL08(0030)" => ("bool", 1, null),
                "T_INT1(0068)" => ("int8", 1, null),
                "T_UINT1(0069)" => ("uint8", 1, null),
                "T_SHORT(0011)" => ("int16", 2, null),
                "T_USHORT(0021)" => ("uint16", 2, null),
                "T_INT2(0072)" => ("int16", 2, null),
                "T_UINT2(0073)" => ("uint16", 2, null),
                "T_LONG(0012)" => ("int32", 4, null),
                "T_ULONG(0022)" => ("uint32", 4, null),
                "T_INT4(0074)" => ("int32", 4, null),
                "T_UINT4(0075)" => ("uint32", 4, null),
                "T_REAL32(0040)" => ("float32", 4, null),
                "T_REAL64(0041)" => ("float64", 8, null),
                "T_32PVOID(0403)" => ("pointer", 4, "void"),
                "T_32PRCHAR(0470)" => ("pointer", 4, "char"),
                "T_NOTYPE(0000)" => ("unknown", 0, null),
                _ => ("unknown", 0, typeName)
            };
        }

        // Custom type reference (0xXXXX)
        if (typeName.StartsWith("0x") &&
            uint.TryParse(typeName[2..], System.Globalization.NumberStyles.HexNumber, null, out var typeIdx))
        {
            // Follow forward ref if needed
            if (_forwardRefs.TryGetValue(typeIdx, out var resolvedIdx))
                typeIdx = resolvedIdx;

            // Check if it's a pointer type
            if (_pointerTypes.TryGetValue(typeIdx, out var ptrInfo))
                return ("pointer", 4, ptrInfo.ElementTypeName);

            // Check if it's a known structure (embedded struct)
            if (StructuresByIndex.TryGetValue(typeIdx, out var structInfo))
                return ("struct", structInfo.Size, structInfo.Name);

            // Check if it's a fixed-size array (LF_ARRAY carries the TOTAL byte length)
            if (_arrayTypes.TryGetValue(typeIdx, out var arrayInfo))
                return ("array", arrayInfo.TotalSize,
                    arrayInfo.ElementTypeName != null ? $"{arrayInfo.ElementTypeName}[]" : "[]");

            // Check if it's an enum (treat as its underlying int size)
            foreach (var e in Enums.Values)
                if (e.TypeIndex == typeIdx)
                    return ("enum", 4, e.Name);

            // Unknown custom type — bitfield, modifier, etc.
            return ("unknown", 0, typeName);
        }

        return ("unknown", 0, typeName);
    }

    /// <summary>
    ///     Recursively flattens all fields from a structure including inherited base class fields.
    ///     Returns fields sorted by offset.
    /// </summary>
    public List<FlatField> FlattenFields(StructureInfo structInfo, HashSet<uint>? visited = null)
    {
        visited ??= [];
        if (!visited.Add(structInfo.TypeIndex))
            return []; // Prevent infinite recursion

        var result = new List<FlatField>();

        // Recursively flatten base classes
        foreach (var baseRef in structInfo.BaseClasses)
        {
            if (!StructuresByIndex.TryGetValue(baseRef.TypeIndex, out var baseStruct))
                continue;

            var baseFields = FlattenFields(baseStruct, visited);
            foreach (var bf in baseFields)
                result.Add(bf with { Offset = baseRef.Offset + bf.Offset });
        }

        // Add this class's own members
        foreach (var field in structInfo.Fields)
        {
            if (field.TypeName == "LF_ENUMERATE")
                continue; // Skip enum values that ended up in field list

            var (kind, size, typeDetail) = ResolveFieldType(field.TypeName);
            result.Add(new FlatField(field.Name, field.Offset, size, kind, structInfo.Name, typeDetail));
        }

        // Deduplicate by offset (base class fields shouldn't overlap with own fields)
        return result
            .GroupBy(f => f.Offset)
            .Select(g => g.First())
            .OrderBy(f => f.Offset)
            .ToList();
    }

    [GeneratedRegex(@"^(0x[0-9a-fA-F]+) : Length")]
    private static partial Regex TypeIndexRegex();

    [GeneratedRegex(@"Size = (\d+), class name = ([^,]+)")]
    private static partial Regex StructDetailRegex();

    [GeneratedRegex(@"field list type (0x[0-9a-fA-F]+)")]
    private static partial Regex FieldListRefRegex();

    [GeneratedRegex(@"LF_MEMBER.*type = ([^,]+), offset = (\d+)")]
    private static partial Regex MemberRegex();

    [GeneratedRegex(@"member name = '([^']+)'")]
    private static partial Regex MemberNameRegex();

    [GeneratedRegex(@"LF_ENUMERATE.*value = (\d+), name = '([^']+)'")]
    private static partial Regex EnumerateRegex();

    [GeneratedRegex(@"enum name = ([^,]+),")]
    private static partial Regex EnumNameRegex();

    [GeneratedRegex(@"LF_BCLASS.*type = (0x[0-9a-fA-F]+), offset = (\d+)")]
    private static partial Regex BClassRegex();

    [GeneratedRegex(@"Element type : (0x[0-9a-fA-F]+)")]
    private static partial Regex PointerElementTypeRegex();

    // LF_ARRAY writes "Element type = 0xNNNN" (with '=', not ':' as LF_POINTER does) and the
    // element may be a primitive, in which case there is no index to capture.
    [GeneratedRegex(@"Element type = (?:(0x[0-9a-fA-F]+)|T_)")]
    private static partial Regex ArrayElementTypeRegex();

    [GeneratedRegex(@"^\s*length = (\d+)")]
    private static partial Regex ArrayLengthRegex();

    [GeneratedRegex(@"UDT\((0x[0-9a-fA-F]+)\)")]
    private static partial Regex UdtRegex();
}

internal class StructureInfo
{
    public uint TypeIndex { get; set; }
    public string Name { get; set; } = "";
    public int Size { get; set; }
    public uint FieldListIndex { get; set; }
    public bool HasEndianMethod { get; set; }
    public List<FieldInfo> Fields { get; } = [];
    public List<BaseClassRef> BaseClasses { get; } = [];
}

internal record BaseClassRef(uint TypeIndex, int Offset);

internal class FieldListInfo
{
    public uint TypeIndex { get; set; }
    public bool HasEndianMethod { get; set; }
    public List<FieldInfo> Fields { get; } = [];
    public List<BaseClassRef> BaseClasses { get; } = [];
}

internal class FieldInfo
{
    public string TypeName { get; set; } = "";
    public int Offset { get; set; }
    public string Name { get; set; } = "";
}

internal record PointerTypeInfo(uint TypeIndex, uint ElementTypeIndex, string? ElementTypeName);

/// <summary>
///     A fixed-size array leaf. <paramref name="TotalSize" /> is the whole array's byte length as
///     the PDB records it, not an element count.
/// </summary>
internal record ArrayTypeInfo(uint ElementTypeIndex, int TotalSize, string? ElementTypeName);

internal record FlatField(
    string Name,
    int Offset,
    int Size,
    string Kind,
    string? OwnerClass,
    string? TypeDetail);

internal class EnumInfo
{
    public uint TypeIndex { get; set; }
    public string Name { get; set; } = "";
    public uint FieldListIndex { get; set; }
    public List<EnumMember> Members { get; } = [];
}

internal class EnumMember
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}
