using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Scanning;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.World;
using BethesdaMultitool.Core.Minidump;

namespace EsmAnalyzer.Commands.SpeedTree;

/// <summary>
///     SpeedTree <c>extract-dump</c> sub-command: pulls real engine-generated tree geometry
///     (BSTreeModel/NiTriShape) out of an Xbox 360 memory dump.
/// </summary>
internal static class SpeedTreeExtractDumpCommand
{
    public static Command CreateExtractDumpCommand()
    {
        var command = new Command("extract-dump",
            "Extract real engine-generated SpeedTree geometry (BSTreeModel/NiTriShape) from a memory dump");
        var fileArg = new Argument<string>("dump") { Description = "Path to the .dmp memory dump" };
        command.Arguments.Add(fileArg);
        command.SetAction(parseResult => ExtractFromDump(parseResult.GetValue(fileArg)!));
        return command;
    }

    private static int ExtractFromDump(string dumpPath)
    {
        if (!File.Exists(dumpPath))
        {
            Console.Error.WriteLine($"File not found: {dumpPath}");
            return 1;
        }

        Console.WriteLine($"Parsing dump: {Path.GetFileName(dumpPath)} ...");
        var info = MinidumpParser.Parse(dumpPath);
        if (!info.IsValid)
        {
            Console.Error.WriteLine("Not a valid minidump.");
            return 1;
        }

        using var mmf = MemoryMappedFile.CreateFromFile(dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        var accessor = new MmfMemoryAccessor(view);
        var context = new RuntimeMemoryContext(accessor, new FileInfo(dumpPath).Length, info);

        // Find the BSTreeModel vtable via an RTTI census over ALL regions (the default heap window
        // misses the Xbox object pools where BSTreeModel lives).
        Console.WriteLine("Running RTTI census over all regions (this scans the whole dump) ...");
        using var stream = File.OpenRead(dumpPath);
        var census = new RttiReader(info, stream).RunCensus(includeAllRegions: true);
        Console.WriteLine($"Census resolved {census.Count} distinct classes.");

        PrintTreeClassDiagnostic(census);

        var bstm = census.FirstOrDefault(e =>
            e.Rtti.ClassName.Contains("BSTreeModel", StringComparison.OrdinalIgnoreCase));
        if (bstm is null)
        {
            return ScanAllMeshesFallback(context, CultureInfo.InvariantCulture);
        }

        var vtableVa = bstm.Rtti.VtableVA;
        Console.WriteLine($"BSTreeModel vtable @ 0x{vtableVa:X8}  (census instance count: {bstm.InstanceCount})");

        // Scan the heap for objects whose vtable pointer (object+0) == the BSTreeModel vtable.
        var instanceVas = new List<uint>();
        var scanner = new RuntimeObjectScanner(context);
        scanner.ScanAligned(
            (chunk, offset) => BinaryUtils.ReadUInt32BE(chunk, offset) == vtableVa,
            (_, _, fileOffset) =>
            {
                var va = info.FileOffsetToVirtualAddress(fileOffset);
                if (va.HasValue)
                {
                    lock (instanceVas)
                    {
                        instanceVas.Add((uint)va.Value);
                    }
                }
            },
            4);

        Console.WriteLine($"Located {instanceVas.Count} BSTreeModel instance(s).");

        var trees = new RuntimeTreeGeometryExtractor(context).Extract(instanceVas);
        Console.WriteLine($"Extracted geometry from {trees.Count} tree(s):");
        var ci = CultureInfo.InvariantCulture;
        if (trees.Count == 0)
        {
            PrintBstreeModelFieldDiagnostic(context, instanceVas.Take(8), ci);
        }

        var groups = trees
            .GroupBy(t => new
            {
                Seed = ReadUInt32(context, t.BSTreeModelVa + 0x40),
                RuntimeHeight = ReadFloat(context, t.BSTreeModelVa + 0x4C),
                Branches = t.Submeshes.Count(s => s.Kind == TreeGeometryKind.Branch),
                Leaves = t.Submeshes.Count(s => s.Kind == TreeGeometryKind.Leaf),
                Billboards = t.Submeshes.Count(s => s.Kind == TreeGeometryKind.Billboard),
                Vertices = t.TotalVertices,
                Triangles = t.TotalTriangles
            })
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key.Vertices)
            .ToList();

        Console.WriteLine($"Unique extracted geometry groups: {groups.Count}");
        foreach (var group in groups.Take(24))
        {
            var tree = group.First();
            var bounds = ComputeBounds(tree.Submeshes);
            Console.WriteLine(string.Create(ci,
                $"  x{group.Count(),3} seed={group.Key.Seed} runtimeH={group.Key.RuntimeHeight:F2} @0x{tree.BSTreeModelVa:X8}: {tree.Submeshes.Count} submeshes (branch {group.Key.Branches}, leaf {group.Key.Leaves}, billboard {group.Key.Billboards}), {group.Key.Vertices} verts, {group.Key.Triangles} tris, bounds size=({bounds.SizeX:F2},{bounds.SizeY:F2},{bounds.SizeZ:F2}) min=({bounds.MinX:F2},{bounds.MinY:F2},{bounds.MinZ:F2}) max=({bounds.MaxX:F2},{bounds.MaxY:F2},{bounds.MaxZ:F2})"));
        }

        if (groups.Count > 24)
        {
            Console.WriteLine($"  ... {groups.Count - 24} more group(s) omitted");
        }

        if (trees.Count > 0 && trees.All(t => t.Submeshes.All(s => s.Kind != TreeGeometryKind.Branch)))
        {
            Console.WriteLine("No branch meshes extracted; branch field diagnostic for first 3 instances:");
            PrintBstreeModelFieldDiagnostic(context, instanceVas.Take(3), ci);
        }

        return 0;
    }

    private static RuntimeBounds ComputeBounds(IEnumerable<ExtractedTreeSubmesh> submeshes)
    {
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        var maxZ = float.MinValue;

        foreach (var submesh in submeshes)
        {
            var vertices = submesh.Mesh.Vertices;
            for (var i = 0; i + 2 < vertices.Length; i += 3)
            {
                var x = vertices[i];
                var y = vertices[i + 1];
                var z = vertices[i + 2];
                if (!RuntimeMemoryContext.IsNormalFloat(x) || !RuntimeMemoryContext.IsNormalFloat(y) ||
                    !RuntimeMemoryContext.IsNormalFloat(z))
                {
                    continue;
                }

                minX = MathF.Min(minX, x);
                minY = MathF.Min(minY, y);
                minZ = MathF.Min(minZ, z);
                maxX = MathF.Max(maxX, x);
                maxY = MathF.Max(maxY, y);
                maxZ = MathF.Max(maxZ, z);
            }
        }

        return minX == float.MaxValue
            ? new RuntimeBounds()
            : new RuntimeBounds(minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static void PrintBstreeModelFieldDiagnostic(RuntimeMemoryContext context, IEnumerable<uint> modelVas,
        CultureInfo ci)
    {
        Console.WriteLine("BSTreeModel field diagnostic (first 8 instances; offsets from PDB/decompile):");
        foreach (var va in modelVas)
        {
            var speedTree = ReadPointer(context, va + 0x0C);
            var branchData = ReadPointer(context, va + 0x14);
            var leafData = ReadPointer(context, va + 0x18);
            var billboard = ReadPointer(context, va + 0x1C);
            var branchProps = ReadPointer(context, va + 0x24);
            var leafProps = ReadPointer(context, va + 0x28);
            var branchCount = ReadArrayCount(context, branchData);
            var leafCount = ReadArrayCount(context, leafData);
            var firstBranch = ReadFirstArrayPointer(context, branchData);
            var firstLeaf = ReadFirstArrayPointer(context, leafData);
            var seed = ReadUInt32(context, va + 0x40);
            var initialized = ReadByte(context, va + 0x44);
            var width = ReadFloat(context, va + 0x48);
            var height = ReadFloat(context, va + 0x4C);

            Console.WriteLine(string.Create(ci,
                $"  @0x{va:X8}: pSpeedTree=0x{speedTree:X8} branchData=0x{branchData:X8}[{branchCount}] first=0x{firstBranch:X8} leafData=0x{leafData:X8}[{leafCount}] first=0x{firstLeaf:X8} billboard=0x{billboard:X8} branchProps=0x{branchProps:X8} leafProps=0x{leafProps:X8} seed={seed} init={initialized} width={width:F2} height={height:F2}"));
            PrintGeometryDataDiagnostic(context, "branch0", firstBranch, ci);
            PrintGeometryDataDiagnostic(context, "leaf0", firstLeaf, ci);
        }
    }

    private static void PrintGeometryDataDiagnostic(RuntimeMemoryContext context, string label, uint dataVa,
        CultureInfo ci)
    {
        if (dataVa == 0 || !context.IsValidPointer(dataVa))
        {
            return;
        }

        var refCount = ReadUInt32(context, dataVa + 0x04);
        var vertices = ReadUInt16(context, dataVa + 0x08);
        var triangles = ReadUInt16(context, dataVa + 0x40);
        var radius = ReadFloat(context, dataVa + 0x1C);
        var vertexPtr = ReadPointer(context, dataVa + 0x20);
        var normalPtr = ReadPointer(context, dataVa + 0x24);
        var uvPtr = ReadPointer(context, dataVa + 0x2C);
        var buffDataPtr = ReadPointer(context, dataVa + 0x34);
        var indexCount = ReadUInt32(context, dataVa + 0x44);
        var indexPtr = ReadPointer(context, dataVa + 0x48);
        var stripCount = ReadUInt16(context, dataVa + 0x44);
        var stripLengthsPtr = ReadPointer(context, dataVa + 0x48);
        var stripListsPtr = ReadPointer(context, dataVa + 0x4C);
        var derivedStripListsPtr = ResolvePackedStripListsPointer(context, buffDataPtr);
        var firstStripLength = ReadUInt16(context, stripLengthsPtr);
        var validPointers =
            $"{context.IsValidPointer(vertexPtr)}/{context.IsValidPointer(normalPtr)}/{context.IsValidPointer(uvPtr)}/{context.IsValidPointer(indexPtr)}";
        var validStripPointers =
            $"{context.IsValidPointer(stripLengthsPtr)}/{context.IsValidPointer(stripListsPtr)}/{context.IsValidPointer(derivedStripListsPtr)}";
        var vertexBounds = ReadPointArrayBounds(context, vertexPtr, vertices);

        Console.WriteLine(string.Create(ci,
            $"      {label} @0x{dataVa:X8}: ref={refCount} verts={vertices} tris={triangles} radius={radius:F2} v/n/uv/i=0x{vertexPtr:X8}/0x{normalPtr:X8}/0x{uvPtr:X8}/0x{indexPtr:X8} valid={validPointers} idxCount={indexCount} buff=0x{buffDataPtr:X8} stripCount={stripCount} stripLen/list/derived=0x{stripLengthsPtr:X8}/0x{stripListsPtr:X8}/0x{derivedStripListsPtr:X8} validStrip={validStripPointers} firstStripLen={firstStripLength} vertexSize=({vertexBounds.SizeX:F2},{vertexBounds.SizeY:F2},{vertexBounds.SizeZ:F2})"));
    }

    private static uint ResolvePackedStripListsPointer(RuntimeMemoryContext context, uint buffDataPtr)
    {
        if (buffDataPtr == 0 || !context.IsValidPointer(buffDataPtr))
        {
            return 0;
        }

        var nestedPtr = ReadPointer(context, buffDataPtr + 0x34);
        if (nestedPtr == 0 || !context.IsValidPointer(nestedPtr))
        {
            return 0;
        }

        var packedAddress = ReadUInt32(context, nestedPtr + 0x18);
        return unchecked((packedAddress & 0x1FFF_FFFFu) +
                         (((packedAddress >> 20) + 0x200u) & 0x1000u) -
                         0x4000_0000u);
    }

    private static RuntimeBounds ReadPointArrayBounds(RuntimeMemoryContext context, uint pointPtr, int pointCount)
    {
        if (pointPtr == 0 || pointCount <= 0 || !context.IsValidPointer(pointPtr))
        {
            return new RuntimeBounds();
        }

        var data = context.ReadBytesAtVa(pointPtr, pointCount * 12);
        if (data is null || data.Length < pointCount * 12)
        {
            return new RuntimeBounds();
        }

        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        var maxZ = float.MinValue;
        for (var i = 0; i < pointCount; i++)
        {
            var offset = i * 12;
            var x = BinaryUtils.ReadFloatBE(data, offset);
            var y = BinaryUtils.ReadFloatBE(data, offset + 4);
            var z = BinaryUtils.ReadFloatBE(data, offset + 8);
            if (!RuntimeMemoryContext.IsNormalFloat(x) || !RuntimeMemoryContext.IsNormalFloat(y) ||
                !RuntimeMemoryContext.IsNormalFloat(z))
            {
                continue;
            }

            minX = MathF.Min(minX, x);
            minY = MathF.Min(minY, y);
            minZ = MathF.Min(minZ, z);
            maxX = MathF.Max(maxX, x);
            maxY = MathF.Max(maxY, y);
            maxZ = MathF.Max(maxZ, z);
        }

        return minX == float.MaxValue
            ? new RuntimeBounds()
            : new RuntimeBounds(minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static uint ReadArrayCount(RuntimeMemoryContext context, uint arrayVa)
    {
        return arrayVa != 0 && context.IsValidPointer(arrayVa) && arrayVa >= 4
            ? ReadUInt32(context, arrayVa - 4)
            : 0;
    }

    private static uint ReadFirstArrayPointer(RuntimeMemoryContext context, uint arrayVa)
    {
        return arrayVa != 0 && context.IsValidPointer(arrayVa) ? ReadPointer(context, arrayVa) : 0;
    }

    private static byte ReadByte(RuntimeMemoryContext context, long va)
    {
        var bytes = context.ReadBytesAtVa(va, 1);
        return bytes is null || bytes.Length == 0 ? (byte)0 : bytes[0];
    }

    private static float ReadFloat(RuntimeMemoryContext context, long va)
    {
        var bytes = context.ReadBytesAtVa(va, 4);
        return bytes is null || bytes.Length < 4 ? 0 : BinaryUtils.ReadFloatBE(bytes);
    }

    private static uint ReadPointer(RuntimeMemoryContext context, long va)
    {
        return ReadUInt32(context, va);
    }

    private static uint ReadUInt32(RuntimeMemoryContext context, long va)
    {
        var bytes = context.ReadBytesAtVa(va, 4);
        return bytes is null || bytes.Length < 4 ? 0 : BinaryUtils.ReadUInt32BE(bytes);
    }

    private static ushort ReadUInt16(RuntimeMemoryContext context, long va)
    {
        var bytes = context.ReadBytesAtVa(va, 2);
        return bytes is null || bytes.Length < 2 ? (ushort)0 : BinaryUtils.ReadUInt16BE(bytes);
    }

    private static void PrintTreeClassDiagnostic(IEnumerable<CensusEntry> census)
    {
        string[] keys = ["Tree", "Speed", "Billboard", "NiTriShape", "NiNode"];
        foreach (var e in census
                     .Where(e => keys.Any(k => e.Rtti.ClassName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                     .Take(20))
        {
            Console.WriteLine($"  [class] {e.Rtti.ClassName}  @vtable 0x{e.Rtti.VtableVA:X8}  x{e.InstanceCount}");
        }
    }

    // Proto/beta Xbox builds use QueuedTreeModel (a load-queue wrapper), not the retail BSTreeModel.
    // The generated geometry is still standard NiTriShape; prove it's reachable by scanning all meshes
    // and reporting the largest (tree branch/leaf meshes are among the bigger ones).
    private static int ScanAllMeshesFallback(RuntimeMemoryContext context, CultureInfo ci)
    {
        Console.WriteLine("No BSTreeModel (proto build). Scanning all NiTriShape geometry as a fallback ...");
        var meshes = new RuntimeGeometryScanner(context).ScanForMeshes();
        Console.WriteLine($"Geometry scan found {meshes.Count} meshes. Largest 12 by vertex count:");
        foreach (var m in meshes.OrderByDescending(m => m.VertexCount).Take(12))
        {
            Console.WriteLine(string.Create(ci,
                $"  @0x{m.SourceOffset:X}: {m.VertexCount} verts, {m.TriangleCount} tris, bound r={m.BoundRadius:F1} uv={m.UVs != null}"));
        }

        return 0;
    }

    private readonly record struct RuntimeBounds(
        float MinX,
        float MinY,
        float MinZ,
        float MaxX,
        float MaxY,
        float MaxZ)
    {
        public float SizeX => MaxX - MinX;
        public float SizeY => MaxY - MinY;
        public float SizeZ => MaxZ - MinZ;
    }
}