using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Minidump;

/// <summary>
///     Every C++ class and vtable in a dump, derived FORWARD from the module's own RTTI tables.
///     <para>
///         <see cref="RttiReader.RunCensus" /> works backwards: it harvests candidate words from the
///         heap and asks which resolve. That derivation is inherently partial — it cannot see a
///         class with no live instance, it drops any vtable observed fewer than twice to suppress
///         false positives, and its default window misses the pools above <c>0x50000000</c>. Walking
///         the tables instead answers "what classes does this build have" completely and from
///         20 MB of module image rather than 220 MB of dump. Measured on xex44: 1,968 classes and
///         2,386 vtables here, against 857 classes for the heap census.
///     </para>
///     <para>
///         The walk is the one <c>tools/RttiScanner</c> performs offline, minus its full reverse
///         pointer index — with only ~2,400 addresses to probe, two hash-set passes over the image
///         are cheaper than indexing every word in it.
///     </para>
/// </summary>
public sealed class DumpRttiIndex
{
    /// <summary>Matches <see cref="RttiReader" />'s own guard against a runaway hierarchy walk.</summary>
    private const int MaxBaseClasses = 32;

    private const int MaxTypeNameLength = 512;

    private readonly Dictionary<uint, DumpRttiVtable> _byVtableVa;

    private readonly DumpRttiClass[] _classes;

    private DumpRttiIndex(
        Dictionary<uint, DumpRttiVtable> byVtableVa,
        DumpRttiClass[] classes,
        int typeDescriptorCount,
        int typeDescriptorsWithNoCol,
        int completeObjectLocatorCount,
        ModuleImage image)
    {
        _byVtableVa = byVtableVa;
        _classes = classes;
        TypeDescriptorCount = typeDescriptorCount;
        TypeDescriptorsWithNoColCount = typeDescriptorsWithNoCol;
        CompleteObjectLocatorCount = completeObjectLocatorCount;
        ModuleBytesCaptured = image.BytesCaptured;
        ModuleBytesDeclared = image.BytesDeclared;
        PrimaryVtableCount = byVtableVa.Values.Count(v => v.ObjectOffset == 0);

        MinVtableVa = byVtableVa.Count == 0 ? 0 : byVtableVa.Keys.Min();
        MaxVtableVa = byVtableVa.Count == 0 ? 0 : byVtableVa.Keys.Max();

        var byName = new Dictionary<string, DumpRttiClass>(StringComparer.Ordinal);
        foreach (var entry in classes)
        {
            byName.TryAdd(entry.ClassName, entry);
        }

        ClassesByName = byName;
    }

    public IReadOnlyList<DumpRttiClass> Classes => _classes;

    public IReadOnlyDictionary<string, DumpRttiClass> ClassesByName { get; }

    /// <summary>
    ///     Lowest and highest vtable address found. Every vtable in a retail dump falls inside a
    ///     band of roughly 1.5 MB, so a pair of comparisons against these rejects essentially every
    ///     word of the dump before a hash probe is needed — which is what makes sweeping the whole
    ///     dump for object headers affordable.
    /// </summary>
    public uint MinVtableVa { get; }

    public uint MaxVtableVa { get; }

    public int VtableCount => _byVtableVa.Count;

    public int PrimaryVtableCount { get; }

    public int TypeDescriptorCount { get; }

    /// <summary>
    ///     Type descriptors that no complete object locator referenced. A large value means the
    ///     walk lost coverage — most likely part of the image was not captured — so it is reported
    ///     rather than swallowed.
    /// </summary>
    public int TypeDescriptorsWithNoColCount { get; }

    public int CompleteObjectLocatorCount { get; }

    public long ModuleBytesCaptured { get; }

    public long ModuleBytesDeclared { get; }

    public bool TryGetVtable(uint vtableVa, out DumpRttiVtable entry)
    {
        return _byVtableVa.TryGetValue(vtableVa, out entry);
    }

    /// <summary>The class a vtable belongs to, or null if the id is out of range.</summary>
    public DumpRttiClass? ClassFor(DumpRttiVtable vtable)
    {
        return vtable.ClassId >= 0 && vtable.ClassId < _classes.Length ? _classes[vtable.ClassId] : null;
    }

    /// <summary>
    ///     Walk the module's RTTI tables. Returns null when the dump names no game module or none of
    ///     it was captured.
    /// </summary>
    public static DumpRttiIndex? Build(MinidumpInfo info, IMemoryAccessor accessor, long fileSize)
    {
        var image = ModuleImage.Build(info, accessor, fileSize);
        if (image is null)
        {
            return null;
        }

        var typeDescriptors = FindTypeDescriptors(image);
        if (typeDescriptors.Count == 0)
        {
            return null;
        }

        var locators = FindCompleteObjectLocators(image, typeDescriptors);
        var referencedTypeDescriptors = new HashSet<uint>(locators.Values.Select(c => c.PTypeDescriptor));

        var classIdByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var classes = new List<DumpRttiClass>();
        var vtables = FindVtables(image, locators, typeDescriptors, classIdByName, classes);

        return new DumpRttiIndex(
            vtables,
            [.. classes],
            typeDescriptors.Count,
            typeDescriptors.Count - referencedTypeDescriptors.Count,
            locators.Count,
            image);
    }

    /// <summary>
    ///     Find every <c>.?AV</c>/<c>.?AU</c> type-descriptor name in the image and map its owning
    ///     descriptor's address to it. The descriptor begins 8 bytes before the name (past
    ///     <c>pVFTable</c> and <c>spare</c>), and requiring that address to be 4-aligned is a free
    ///     falsifier — every one of the 2,030 real descriptors on xex44 satisfies it, while a
    ///     stray occurrence of the tag inside unrelated data generally does not.
    /// </summary>
    private static Dictionary<uint, string> FindTypeDescriptors(ModuleImage image)
    {
        var result = new Dictionary<uint, string>();
        var tag = ".?A"u8;

        foreach (var run in image.Runs)
        {
            var bytes = run.Bytes;
            var searchFrom = 0;

            while (searchFrom < bytes.Length)
            {
                var relative = bytes.AsSpan(searchFrom).IndexOf(tag);
                if (relative < 0)
                {
                    break;
                }

                var start = searchFrom + relative;
                searchFrom = start + 1;

                if (start + 4 > bytes.Length)
                {
                    break;
                }

                var kind = bytes[start + 3];
                if (kind != (byte)'V' && kind != (byte)'U')
                {
                    continue;
                }

                var nameVa = run.StartVa + (uint)start;
                if (nameVa < 8)
                {
                    continue;
                }

                var descriptorVa = nameVa - 8;
                if ((descriptorVa & 3) != 0)
                {
                    continue;
                }

                var name = ReadPrintableCString(bytes, start);
                if (name is null || !name.Contains("@@", StringComparison.Ordinal))
                {
                    continue;
                }

                result[descriptorVa] = name;
            }
        }

        return result;
    }

    /// <summary>
    ///     Find complete object locators by back-reference: any word equal to a known type-descriptor
    ///     address is a candidate <c>pTypeDescriptor</c> at locator+12.
    /// </summary>
    private static Dictionary<uint, LocatorInfo> FindCompleteObjectLocators(
        ModuleImage image, Dictionary<uint, string> typeDescriptors)
    {
        var result = new Dictionary<uint, LocatorInfo>();

        foreach (var (wordVa, word) in EnumerateWords(image))
        {
            if (!typeDescriptors.ContainsKey(word) || wordVa < 12)
            {
                continue;
            }

            var locatorVa = wordVa - 12;

            // +0 signature must be zero (32-bit MSVC RTTI), +16 must point at a hierarchy
            // descriptor inside this module. The +12 read is implied by how we got here.
            if (!image.TryReadUInt32(locatorVa, out var signature) || signature != 0 ||
                !image.TryReadUInt32(locatorVa + 4, out var objectOffset) ||
                !image.TryReadUInt32(locatorVa + 16, out var hierarchyVa) ||
                hierarchyVa < image.ModuleStart || hierarchyVa >= image.ModuleEnd)
            {
                continue;
            }

            result[locatorVa] = new LocatorInfo(objectOffset, word, hierarchyVa);
        }

        return result;
    }

    /// <summary>
    ///     Find vtables by back-reference to the locators: a word equal to a locator address is the
    ///     slot at vtable-4, so the vtable proper starts 4 bytes later. Validated by requiring the
    ///     first virtual function slot to point inside the module.
    /// </summary>
    private static Dictionary<uint, DumpRttiVtable> FindVtables(
        ModuleImage image,
        Dictionary<uint, LocatorInfo> locators,
        Dictionary<uint, string> typeDescriptors,
        Dictionary<string, int> classIdByName,
        List<DumpRttiClass> classes)
    {
        var result = new Dictionary<uint, DumpRttiVtable>();

        foreach (var (wordVa, word) in EnumerateWords(image))
        {
            if (!locators.TryGetValue(word, out var locator))
            {
                continue;
            }

            var vtableVa = wordVa + 4;
            if (!image.TryReadUInt32(vtableVa, out var firstSlot) ||
                firstSlot < image.ModuleStart || firstSlot >= image.ModuleEnd)
            {
                continue;
            }

            var classId = EnsureClass(image, locator, typeDescriptors, classIdByName, classes);
            if (classId < 0)
            {
                continue;
            }

            result[vtableVa] = new DumpRttiVtable(vtableVa, locator.ObjectOffset, classId);
        }

        return result;
    }

    private static int EnsureClass(
        ModuleImage image,
        LocatorInfo locator,
        Dictionary<uint, string> typeDescriptors,
        Dictionary<string, int> classIdByName,
        List<DumpRttiClass> classes)
    {
        if (!typeDescriptors.TryGetValue(locator.PTypeDescriptor, out var mangled))
        {
            return -1;
        }

        var className = MsvcNameDemangler.Demangle(mangled);
        if (string.IsNullOrEmpty(className))
        {
            return -1;
        }

        if (classIdByName.TryGetValue(className, out var existing))
        {
            return existing;
        }

        var bases = ReadBaseClasses(image, locator.HierarchyVa, typeDescriptors);
        var isTesForm = className == "TESForm" || bases.Contains("TESForm");

        classes.Add(new DumpRttiClass(className, mangled, bases, isTesForm));
        var id = classes.Count - 1;
        classIdByName[className] = id;
        return id;
    }

    /// <summary>
    ///     Walk the ClassHierarchyDescriptor's base-class array. An unreadable or implausible
    ///     hierarchy yields an empty list rather than failing the class: the identity is still
    ///     sound, only the ancestry is missing.
    /// </summary>
    private static List<string> ReadBaseClasses(
        ModuleImage image, uint hierarchyVa, Dictionary<uint, string> typeDescriptors)
    {
        var bases = new List<string>();

        if (!image.TryReadUInt32(hierarchyVa + 8, out var baseCount) ||
            baseCount == 0 || baseCount > MaxBaseClasses ||
            !image.TryReadUInt32(hierarchyVa + 12, out var arrayVa))
        {
            return bases;
        }

        for (uint i = 0; i < baseCount; i++)
        {
            if (!image.TryReadUInt32(arrayVa + i * 4, out var descriptorVa) ||
                !image.TryReadUInt32(descriptorVa, out var pTypeDescriptor) ||
                !typeDescriptors.TryGetValue(pTypeDescriptor, out var mangled))
            {
                continue;
            }

            var name = MsvcNameDemangler.Demangle(mangled);
            if (!string.IsNullOrEmpty(name) && !bases.Contains(name))
            {
                bases.Add(name);
            }
        }

        return bases;
    }

    /// <summary>Every 4-aligned big-endian word in the image, with its virtual address.</summary>
    private static IEnumerable<(uint Va, uint Word)> EnumerateWords(ModuleImage image)
    {
        foreach (var run in image.Runs)
        {
            var align = (int)((4 - (run.StartVa & 3)) & 3);
            for (var offset = align; offset + 4 <= run.Bytes.Length; offset += 4)
            {
                yield return (run.StartVa + (uint)offset, BinaryUtils.ReadUInt32BE(run.Bytes, offset));
            }
        }
    }

    private static string? ReadPrintableCString(byte[] bytes, int start)
    {
        var end = start;
        while (end < bytes.Length && end - start < MaxTypeNameLength)
        {
            var b = bytes[end];
            if (b == 0)
            {
                return end == start ? null : Encoding.ASCII.GetString(bytes, start, end - start);
            }

            if (b is < 32 or > 126)
            {
                return null;
            }

            end++;
        }

        return null;
    }

    private readonly record struct LocatorInfo(uint ObjectOffset, uint PTypeDescriptor, uint HierarchyVa);
}
