using System.CommandLine;
using NifAnalyzer.Models;
using NifAnalyzer.Parsers;
using Spectre.Console;
using static FalloutXbox360Utils.Core.Formats.Nif.Conversion.NifEndianUtils;

namespace NifAnalyzer.Commands;

/// <summary>
///     Compare normals from Xbox 360 packed data against PC reference normals.
/// </summary>
internal static class PackedNormalComparer
{
    private static void NormalCompare(string xboxPath, string pcPath, int packedBlockIndex, int pcGeomBlockIndex,
        int count)
    {
        var xboxData = File.ReadAllBytes(xboxPath);
        var pcData = File.ReadAllBytes(pcPath);
        var xboxNif = NifParser.Parse(xboxData);
        var pcNif = NifParser.Parse(pcData);

        AnsiConsole.WriteLine("=== Normal Comparison: Xbox 360 Packed vs PC Reference ===");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine($"Xbox file: {Path.GetFileName(xboxPath)} (Block {packedBlockIndex})");
        AnsiConsole.WriteLine($"PC file:   {Path.GetFileName(pcPath)} (Block {pcGeomBlockIndex})");
        AnsiConsole.WriteLine();

        // Validate blocks
        if (packedBlockIndex >= xboxNif.NumBlocks)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Xbox block {packedBlockIndex} out of range");
            return;
        }

        if (pcGeomBlockIndex >= pcNif.NumBlocks)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] PC block {pcGeomBlockIndex} out of range");
            return;
        }

        var xboxTypeName = xboxNif.GetBlockTypeName(packedBlockIndex);
        var pcTypeName = pcNif.GetBlockTypeName(pcGeomBlockIndex);

        if (!xboxTypeName.Contains("PackedAdditionalGeometryData"))
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Xbox block {packedBlockIndex} is {Markup.Escape(xboxTypeName)}, not BSPackedAdditionalGeometryData");
            return;
        }

        // Parse Xbox packed data
        var xboxOffset = xboxNif.GetBlockOffset(packedBlockIndex);
        var xboxSize = (int)xboxNif.BlockSizes[packedBlockIndex];
        var packedResult = PackedCommands.ParsePackedGeometry(xboxData, xboxOffset, xboxSize, xboxNif.IsBigEndian);

        if (packedResult.RawDataOffset < 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to parse Xbox packed data");
            return;
        }

        // Parse PC geometry data
        var pcOffset = pcNif.GetBlockOffset(pcGeomBlockIndex);
        var pcSize = (int)pcNif.BlockSizes[pcGeomBlockIndex];
        var pcGeom = GeometryParser.Parse(pcData.AsSpan(pcOffset, pcSize), pcNif.IsBigEndian, pcNif.BsVersion,
            pcTypeName);

        AnsiConsole.WriteLine($"Xbox: {packedResult.NumVertices} vertices, stride {packedResult.Stride}");
        AnsiConsole.WriteLine($"PC:   {pcGeom.NumVertices} vertices, HasNormals={pcGeom.HasNormals}");
        AnsiConsole.WriteLine();

        // Find normal stream in packed data - VERIFIED: unit-length stream at offset 20 matches PC normals
        // NOTE: Stream headers may label offset 8 as "Normal" but that data has avg length ~0.82, not unit-length.
        // Actual normals are at offset 20 (unit-length ~1.0).
        var half4Streams = packedResult.Streams.Where(s => s.Type == 16 && s.UnitSize == 8)
            .OrderBy(s => s.BlockOffset).ToList();

        // Find the stream at offset 20 (actual normals)
        var normalStream = half4Streams.FirstOrDefault(s => s.BlockOffset == 20);
        if (normalStream == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Could not find normal stream at offset 20");
            return;
        }

        AnsiConsole.WriteLine(
            $"Xbox normal stream: type={normalStream.Type}, offset={normalStream.BlockOffset}, unitSize={normalStream.UnitSize}");
        AnsiConsole.WriteLine();

        // Extract Xbox normals
        var xboxNormals = new List<(float X, float Y, float Z)>();
        for (var v = 0; v < packedResult.NumVertices && v < count; v++)
        {
            var vertexBase = packedResult.RawDataOffset + v * packedResult.Stride;
            var nOff = vertexBase + (int)normalStream.BlockOffset;

            var nx = HalfToFloat(ReadUInt16(xboxData, nOff, true));
            var ny = HalfToFloat(ReadUInt16(xboxData, nOff + 2, true));
            var nz = HalfToFloat(ReadUInt16(xboxData, nOff + 4, true));
            xboxNormals.Add((nx, ny, nz));
        }

        // Extract PC normals
        var pcNormals = ExtractPcNormals(pcData.AsSpan(pcOffset, pcSize), pcNif.IsBigEndian, pcGeom, count);

        // Show raw bytes for first few normals
        AnsiConsole.WriteLine("=== Raw Normal Bytes (first 5 vertices) ===");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine($"{"Vtx",-4} {"Xbox Bytes",-26} {"Xbox (Nx, Ny, Nz)",-35} {"PC (Nx, Ny, Nz)",-35}");
        AnsiConsole.WriteLine(new string('-', 110));

        var displayCount = Math.Min(5, Math.Min(xboxNormals.Count, pcNormals.Count));
        for (var v = 0; v < displayCount; v++)
        {
            var vertexBase = packedResult.RawDataOffset + v * packedResult.Stride;
            var nOff = vertexBase + (int)normalStream.BlockOffset;

            // Raw bytes
            var hexBytes =
                $"{xboxData[nOff]:X2} {xboxData[nOff + 1]:X2} {xboxData[nOff + 2]:X2} {xboxData[nOff + 3]:X2} " +
                $"{xboxData[nOff + 4]:X2} {xboxData[nOff + 5]:X2}";

            var xn = xboxNormals[v];
            var pn = pcNormals[v];

            Console.WriteLine(
                $"{v,-4} {hexBytes,-26} ({xn.X,8:F4}, {xn.Y,8:F4}, {xn.Z,8:F4})   ({pn.X,8:F4}, {pn.Y,8:F4}, {pn.Z,8:F4})");
        }

        AnsiConsole.WriteLine();

        // Full comparison with statistics
        AnsiConsole.WriteLine($"=== Normal Comparison (first {count}) ===");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine($"{"Vtx",-5} {"Xbox (Nx, Ny, Nz)",-35} {"PC (Nx, Ny, Nz)",-35} {"Delta",-10} {"Match"}");
        AnsiConsole.WriteLine(new string('-', 100));

        var matchCount = 0;
        float maxDelta = 0;
        var maxDeltaVert = 0;

        for (var i = 0; i < Math.Min(xboxNormals.Count, pcNormals.Count); i++)
        {
            var xn = xboxNormals[i];
            var pn = pcNormals[i];

            var delta = MathF.Sqrt((xn.X - pn.X) * (xn.X - pn.X) +
                                   (xn.Y - pn.Y) * (xn.Y - pn.Y) +
                                   (xn.Z - pn.Z) * (xn.Z - pn.Z));
            var match = delta < 0.02f;
            if (match) matchCount++;
            if (delta > maxDelta)
            {
                maxDelta = delta;
                maxDeltaVert = i;
            }

            if (i < 20 || !match) // Show first 20 or any mismatches
                Console.WriteLine(
                    $"{i,-5} ({xn.X,8:F4}, {xn.Y,8:F4}, {xn.Z,8:F4})   ({pn.X,8:F4}, {pn.Y,8:F4}, {pn.Z,8:F4})   {delta,8:F4}   {(match ? "\u2713" : "\u2717")}");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("=== Summary ===");
        AnsiConsole.WriteLine($"Compared: {Math.Min(xboxNormals.Count, pcNormals.Count)} normals");
        Console.WriteLine(
            $"Matches (delta < 0.02): {matchCount} ({100.0 * matchCount / Math.Min(xboxNormals.Count, pcNormals.Count):F1}%)");
        AnsiConsole.WriteLine($"Max delta: {maxDelta:F4} at vertex {maxDeltaVert}");

        // Check if normals look like they need remapping
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("=== Checking for potential issues ===");

        // Check if Xbox normals are normalized
        float avgLen = 0;
        for (var i = 0; i < xboxNormals.Count; i++)
        {
            var n = xboxNormals[i];
            avgLen += MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        }

        avgLen /= xboxNormals.Count;
        AnsiConsole.WriteLine($"Xbox normal avg length: {avgLen:F4} (should be ~1.0 for normalized)");

        // Check if values look like valid normals (-1 to 1 range)
        var outOfRange = 0;
        for (var i = 0; i < xboxNormals.Count; i++)
        {
            var n = xboxNormals[i];
            if (MathF.Abs(n.X) > 1.1f || MathF.Abs(n.Y) > 1.1f || MathF.Abs(n.Z) > 1.1f)
                outOfRange++;
        }

        AnsiConsole.WriteLine($"Xbox normals out of [-1,1] range: {outOfRange}");
    }

    private static List<(float X, float Y, float Z)> ExtractPcNormals(ReadOnlySpan<byte> blockData, bool bigEndian,
        GeometryInfo geom, int count)
    {
        var normals = new List<(float X, float Y, float Z)>();
        if (geom.HasNormals == 0) return normals;

        // Use the actual parsed normals offset from GeometryParser
        if (!geom.FieldOffsets.TryGetValue("Normals", out var normalsOffset))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] " +
                                   "  Warning: Normals offset not found in PC geometry, trying fallback...");
            // Fallback: after vertices
            normalsOffset = geom.FieldOffsets.GetValueOrDefault("Vertices") + geom.NumVertices * 12;
        }

        count = Math.Min(count, geom.NumVertices);

        for (var i = 0; i < count; i++)
        {
            var off = normalsOffset + i * 12;
            var x = ReadFloat(blockData.ToArray(), off, bigEndian);
            var y = ReadFloat(blockData.ToArray(), off + 4, bigEndian);
            var z = ReadFloat(blockData.ToArray(), off + 8, bigEndian);
            normals.Add((x, y, z));
        }

        return normals;
    }

    public static Command CreateNormalCompareCommand()
    {
        var command = new Command("normalcompare", "Compare normals between Xbox 360 packed and PC geometry");
        var xboxArg = new Argument<string>("xbox") { Description = "Xbox NIF file path" };
        var pcArg = new Argument<string>("pc") { Description = "PC NIF file path" };
        var packedBlockArg = new Argument<int>("packed-block")
        { Description = "BSPackedAdditionalGeometryData block index" };
        var pcBlockArg = new Argument<int>("pc-block") { Description = "PC geometry block index" };
        var countOpt = new Option<int>("-c", "--count")
        { Description = "Number of normals to compare", DefaultValueFactory = _ => 100 };
        command.Arguments.Add(xboxArg);
        command.Arguments.Add(pcArg);
        command.Arguments.Add(packedBlockArg);
        command.Arguments.Add(pcBlockArg);
        command.Options.Add(countOpt);
        command.SetAction(parseResult => NormalCompare(
            parseResult.GetValue(xboxArg), parseResult.GetValue(pcArg),
            parseResult.GetValue(packedBlockArg), parseResult.GetValue(pcBlockArg), parseResult.GetValue(countOpt)));
        return command;
    }
}
