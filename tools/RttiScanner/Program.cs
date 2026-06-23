using System.Buffers.Binary;
using System.Text;

/// <summary>
///     Fast RTTI struct size extractor for Xbox 360 raw module binaries.
///     Scans for MSVC RTTI TypeDescriptor strings, traces COL→vftable chains,
///     then finds operator new allocation sizes in PPC code near vftable stores.
/// </summary>

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: RttiScanner <module.bin> [--base 0x82000000]");
    return 1;
}

var modulePath = args[0];
var moduleBase = 0x82000000u;

for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--base" && i + 1 < args.Length)
        moduleBase = Convert.ToUInt32(args[++i], 16);
}

if (!File.Exists(modulePath))
{
    Console.Error.WriteLine($"File not found: {modulePath}");
    return 1;
}

var data = File.ReadAllBytes(modulePath);
var moduleEnd = moduleBase + (uint)data.Length;
Console.Error.WriteLine($"Loaded {data.Length:N0} bytes, base=0x{moduleBase:X8}, end=0x{moduleEnd:X8}");

// Phase 1: Find all RTTI TypeDescriptor strings
Console.Error.WriteLine("\n--- Phase 1: Finding TypeDescriptor strings ---");
var typeDescriptors = new Dictionary<uint, string>(); // VA → demangled class name
var avPattern = ".?AV"u8;
var auPattern = ".?AU"u8;

FindTypeDescriptors(data, moduleBase, avPattern, typeDescriptors);
FindTypeDescriptors(data, moduleBase, auPattern, typeDescriptors);
Console.Error.WriteLine($"Found {typeDescriptors.Count} TypeDescriptors");

// Phase 2: Build reverse lookup — for any uint32 value in module range, where does it appear?
Console.Error.WriteLine("\n--- Phase 2: Building pointer index ---");
var pointerIndex = new Dictionary<uint, List<uint>>(); // value → list of VAs where that value appears

for (var i = 0; i <= data.Length - 4; i += 4)
{
    var val = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
    if (val >= moduleBase && val < moduleEnd)
    {
        if (!pointerIndex.TryGetValue(val, out var list))
        {
            list = [];
            pointerIndex[val] = list;
        }
        list.Add(moduleBase + (uint)i);
    }
}
Console.Error.WriteLine($"Indexed {pointerIndex.Count} unique module-range pointer values");

// Phase 3: Find COLs for each TypeDescriptor
Console.Error.WriteLine("\n--- Phase 3: Finding CompleteObjectLocators ---");
var cols = new Dictionary<uint, (uint offset, uint cdOffset, uint pTD, uint pCHD, string className)>();

foreach (var (tdVA, className) in typeDescriptors)
{
    // COL has pTypeDescriptor at +12, so COL is at (ref_location - 12)
    if (!pointerIndex.TryGetValue(tdVA, out var refs)) continue;

    foreach (var refVA in refs)
    {
        var candidateCOL = refVA - 12;
        var colOff = (int)(candidateCOL - moduleBase);
        if (colOff < 0 || colOff + 20 > data.Length) continue;

        var signature = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(colOff));
        if (signature != 0) continue;

        var offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(colOff + 4));
        var cdOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(colOff + 8));
        var pTD = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(colOff + 12));
        var pCHD = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(colOff + 16));

        if (pTD != tdVA) continue;
        if (pCHD < moduleBase || pCHD >= moduleEnd) continue;

        cols[candidateCOL] = (offset, cdOffset, pTD, pCHD, className);
    }
}
Console.Error.WriteLine($"Found {cols.Count} CompleteObjectLocators");

// Phase 4: Find vftables (COL address at vftable[-1])
Console.Error.WriteLine("\n--- Phase 4: Finding vftables ---");
var vftables = new Dictionary<uint, (string className, uint colVA, uint pCHD)>();

foreach (var (colVA, col) in cols)
{
    if (!pointerIndex.TryGetValue(colVA, out var refs)) continue;

    foreach (var refVA in refs)
    {
        var vtableVA = refVA + 4;
        var vtOff = (int)(vtableVA - moduleBase);
        if (vtOff < 0 || vtOff + 4 > data.Length) continue;

        var firstVfunc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(vtOff));
        if (firstVfunc >= moduleBase && firstVfunc < moduleEnd)
        {
            vftables[vtableVA] = (col.className, colVA, col.pCHD);
        }
    }
}
Console.Error.WriteLine($"Found {vftables.Count} vftables");

// Phase 5: Walk class hierarchies
Console.Error.WriteLine("\n--- Phase 5: Walking class hierarchies ---");
var classInfos = new List<ClassInfo>();

foreach (var (vtableVA, vt) in vftables)
{
    var bases = ReadHierarchy(data, moduleBase, moduleEnd, vt.pCHD, typeDescriptors);
    var isTesForm = bases.Any(b =>
        b is "TESForm" or "TESObject" or "TESBoundObject" or "TESBoundAnimObject"
            or "MagicItem" or "TESActorBase");

    classInfos.Add(new ClassInfo(vt.className, vtableVA, bases, isTesForm, 0));
}

var tesFormCount = classInfos.Count(c => c.IsTesForm);
Console.Error.WriteLine($"TESForm-derived classes: {tesFormCount}");

// Phase 6: Find allocation sizes
Console.Error.WriteLine("\n--- Phase 6: Finding allocation sizes ---");

// Build lis+ori vtable store index
// For each vftable VA, find code locations that load it via lis rX,hi; ori rX,rX,lo
for (var ci = 0; ci < classInfos.Count; ci++)
{
    var info = classInfos[ci];
    if (!info.IsTesForm) continue;

    var size = FindAllocSize(data, moduleBase, info.VtableVA, pointerIndex);
    if (size > 0)
    {
        classInfos[ci] = info with { AllocSize = size };
    }
}

// Phase 7: Output results
Console.WriteLine("ClassName\tAllocSize\tIsTESForm\tVtableVA\tBaseClasses");

foreach (var info in classInfos.OrderBy(c => c.ClassName))
{
    var sizeStr = info.AllocSize > 0 ? info.AllocSize.ToString() : "";
    var basesStr = string.Join(", ", info.Bases);
    Console.WriteLine($"{info.ClassName}\t{sizeStr}\t{info.IsTesForm}\t0x{info.VtableVA:X8}\t{basesStr}");
}

// Summary to stderr
Console.Error.WriteLine("\n=== TESForm-Derived Summary ===");
var withSize = 0;
foreach (var info in classInfos.Where(c => c.IsTesForm).OrderBy(c => c.ClassName))
{
    var sizeStr = info.AllocSize > 0 ? info.AllocSize.ToString() : "?";
    Console.Error.WriteLine($"  {info.ClassName,-35} size={sizeStr,-6} vtable=0x{info.VtableVA:X8}");
    if (info.AllocSize > 0) withSize++;
}
Console.Error.WriteLine($"\nTotal classes: {classInfos.Count}, TESForm-derived: {tesFormCount}, with size: {withSize}");

return 0;

// --- Helper methods ---

static void FindTypeDescriptors(byte[] data, uint baseAddr, ReadOnlySpan<byte> prefix,
    Dictionary<uint, string> result)
{
    var span = data.AsSpan();
    var searchStart = 0;

    while (searchStart < span.Length - prefix.Length)
    {
        var idx = span[searchStart..].IndexOf(prefix);
        if (idx < 0) break;

        var absIdx = searchStart + idx;
        // Read null-terminated string
        var end = absIdx;
        while (end < data.Length && data[end] != 0 && end - absIdx < 256) end++;

        if (end > absIdx)
        {
            var mangledName = Encoding.ASCII.GetString(data, absIdx, end - absIdx);
            if (mangledName.Contains("@@"))
            {
                var demangled = Demangle(mangledName);
                if (demangled != null)
                {
                    // TypeDescriptor starts 8 bytes before the name
                    var tdVA = baseAddr + (uint)(absIdx - 8);
                    result.TryAdd(tdVA, demangled);
                }
            }
        }

        searchStart = absIdx + 1;
    }
}

static string? Demangle(string mangledName)
{
    if (!mangledName.StartsWith(".?AV") && !mangledName.StartsWith(".?AU"))
        return null;

    var name = mangledName[4..];
    var atIdx = name.IndexOf("@@", StringComparison.Ordinal);
    return atIdx > 0 ? name[..atIdx] : null;
}

static List<string> ReadHierarchy(byte[] data, uint baseAddr, uint endAddr, uint chdVA,
    Dictionary<uint, string> typeDescriptors)
{
    var result = new List<string>();
    var chdOff = (int)(chdVA - baseAddr);
    if (chdOff < 0 || chdOff + 16 > data.Length) return result;

    var numBases = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(chdOff + 8));
    var pBCA = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(chdOff + 12));

    if (numBases <= 0 || numBases > 32 || pBCA < baseAddr || pBCA >= endAddr)
        return result;

    for (var i = 0; i < numBases; i++)
    {
        var bcaEntryOff = (int)(pBCA - baseAddr) + i * 4;
        if (bcaEntryOff < 0 || bcaEntryOff + 4 > data.Length) break;

        var bcdPtr = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bcaEntryOff));
        if (bcdPtr < baseAddr || bcdPtr >= endAddr) break;

        var bcdOff = (int)(bcdPtr - baseAddr);
        if (bcdOff < 0 || bcdOff + 4 > data.Length) break;

        var pTD = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bcdOff));
        if (pTD < baseAddr || pTD >= endAddr) break;

        if (typeDescriptors.TryGetValue(pTD, out var className))
        {
            result.Add(className);
        }
        else
        {
            // Read name from TypeDescriptor
            var nameOff = (int)(pTD - baseAddr) + 8;
            if (nameOff >= 0 && nameOff < data.Length)
            {
                var end = nameOff;
                while (end < data.Length && data[end] != 0 && end - nameOff < 256) end++;
                if (end > nameOff)
                {
                    var mangledName = Encoding.ASCII.GetString(data, nameOff, end - nameOff);
                    var demangled = Demangle(mangledName);
                    if (demangled != null) result.Add(demangled);
                }
            }
        }
    }

    return result;
}

static int FindAllocSize(byte[] data, uint baseAddr, uint vtableVA,
    Dictionary<uint, List<uint>> pointerIndex)
{
    var hi16 = (ushort)(vtableVA >> 16);
    var lo16 = (ushort)(vtableVA & 0xFFFF);

    // Scan code for lis rX, hi16(vtableVA) + ori rX, rX, lo16(vtableVA)
    // lis rD, imm: 001111 DDDDD 00000 IIIIIIIIIIIIIIII = 0x3C00_0000 | (rD<<21) | imm
    // ori rS, rA, imm: 011000 SSSSS AAAAA IIIIIIIIIIIIIIII = 0x6000_0000 | (rS<<21) | (rA<<16) | imm

    for (var off = 0; off <= data.Length - 8; off += 4)
    {
        var instr = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(off));
        var opcode = (instr >> 26) & 0x3F;

        // lis: opcode 15, rA=0
        if (opcode != 15 || ((instr >> 16) & 0x1F) != 0) continue;

        var rd = (int)((instr >> 21) & 0x1F);
        var immHi = (ushort)(instr & 0xFFFF);
        if (immHi != hi16) continue;

        // Check next instruction for ori rD, rD, lo16
        if (off + 4 >= data.Length) continue;
        var nextInstr = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(off + 4));
        var nextOp = (nextInstr >> 26) & 0x3F;
        if (nextOp != 24) continue; // ori

        var oriRs = (int)((nextInstr >> 21) & 0x1F);
        var oriRa = (int)((nextInstr >> 16) & 0x1F);
        var oriImm = (ushort)(nextInstr & 0xFFFF);

        if (oriRs != rd || oriRa != rd || oriImm != lo16) continue;

        // Found a vftable load! Search backward for li r3, <size>; bl <new>
        var size = SearchBackwardForAlloc(data, off);
        if (size > 0) return size;
    }

    return 0;
}

static int SearchBackwardForAlloc(byte[] data, int vtStoreOffset)
{
    var searchStart = Math.Max(0, vtStoreOffset - 256);

    for (var addr = vtStoreOffset - 4; addr >= searchStart; addr -= 4)
    {
        var instr = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(addr));

        // li r3, <imm> = addi r3, r0, <imm> = 0x3860_XXXX
        if ((instr & 0xFFFF0000) != 0x38600000) continue;

        var size = (int)(instr & 0xFFFF);
        if ((size & 0x8000) != 0) continue; // sign bit = negative
        if (size < 12 || size > 8192) continue;

        // Check next instruction is bl (opcode 18, LK=1)
        if (addr + 4 >= data.Length) continue;
        var nextInstr = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(addr + 4));
        var nextOp = (nextInstr >> 26) & 0x3F;
        if (nextOp == 18 && (nextInstr & 1) == 1)
        {
            return size;
        }
    }

    return 0;
}

record ClassInfo(string ClassName, uint VtableVA, List<string> Bases, bool IsTesForm, int AllocSize);
