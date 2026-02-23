using System.CommandLine;
using NifAnalyzer.Parsers;
using Spectre.Console;

namespace NifAnalyzer.Commands;

/// <summary>
///     Commands for NiSkinPartition analysis and comparison: skinpart, skinpartcompare.
/// </summary>
internal static class SkinPartitionCommands
{
    private static void SkinPart(string path, int blockIndex)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] " +
                                   $"Block index {blockIndex} out of range (0-{nif.NumBlocks - 1})");
            return;
        }

        var offset = nif.GetBlockOffset(blockIndex);
        var typeName = nif.GetBlockTypeName(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];

        if (!typeName.Contains("SkinPartition"))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] " + $"Block {blockIndex} is {typeName}, not NiSkinPartition");
            return;
        }

        AnsiConsole.WriteLine($"Block {blockIndex}: {typeName}");
        AnsiConsole.WriteLine($"Offset: 0x{offset:X4}");
        AnsiConsole.WriteLine($"Size: {size} bytes");
        AnsiConsole.WriteLine($"Endian: {(nif.IsBigEndian ? "Big (Xbox 360)" : "Little (PC)")}");
        AnsiConsole.WriteLine();

        var skinPart = SkinPartitionParser.Parse(data.AsSpan(offset, size), nif.IsBigEndian);

        AnsiConsole.WriteLine("=== NiSkinPartition ===");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine($"NumPartitions: {skinPart.NumPartitions}");
        AnsiConsole.WriteLine();

        for (var p = 0; p < skinPart.NumPartitions; p++)
        {
            var part = skinPart.Partitions[p];
            AnsiConsole.WriteLine($"--- Partition {p} ---");
            AnsiConsole.WriteLine($"  NumVertices: {part.NumVertices}");
            AnsiConsole.WriteLine($"  NumTriangles: {part.NumTriangles}");
            AnsiConsole.WriteLine($"  NumBones: {part.NumBones}");
            AnsiConsole.WriteLine($"  NumStrips: {part.NumStrips}");
            AnsiConsole.WriteLine($"  NumWeightsPerVertex: {part.NumWeightsPerVertex}");
            Console.WriteLine(
                $"  Bones: [{string.Join(", ", part.Bones.Take(Math.Min(10, part.Bones.Length)))}{(part.Bones.Length > 10 ? "..." : "")}]");
            AnsiConsole.WriteLine($"  HasVertexMap: {part.HasVertexMap}");
            Console.WriteLine(
                $"  VertexMap: [{string.Join(", ", part.VertexMap.Take(Math.Min(10, part.VertexMap.Length)))}{(part.VertexMap.Length > 10 ? "..." : "")}]");
            AnsiConsole.WriteLine($"  HasVertexWeights: {part.HasVertexWeights}");
            AnsiConsole.WriteLine($"  HasFaces: {part.HasFaces}");

            if (part.NumStrips > 0)
            {
                AnsiConsole.WriteLine($"  NumStripsLengths: {part.StripLengths.Length}");
                AnsiConsole.WriteLine($"  StripLengths: [{string.Join(", ", part.StripLengths)}]");
                AnsiConsole.WriteLine($"  Total strip indices: {part.StripLengths.Sum(l => l)}");

                if (part.Strips.Length > 0 && part.Strips[0].Length > 0)
                    Console.WriteLine(
                        $"  Strip[0] first 20 indices: [{string.Join(", ", part.Strips[0].Take(Math.Min(20, part.Strips[0].Length)))}...]");
            }

            AnsiConsole.WriteLine($"  HasBoneIndices: {part.HasBoneIndices}");
            if (part.Triangles.Length > 0)
                Console.WriteLine(
                    $"  Triangles[0-4]: {string.Join(", ", part.Triangles.Take(5).Select(t => $"({t.V1},{t.V2},{t.V3})"))}...");

            AnsiConsole.WriteLine();
        }

        // Summary for skinned mesh reconstruction
        AnsiConsole.WriteLine("=== Reconstruction Info ===");
        if (skinPart.NumPartitions > 0)
        {
            var part = skinPart.Partitions[0];
            if (part.NumStrips > 0 && part.Strips.Length > 0)
            {
                // Count triangles that can be generated from strips
                var stripTris = 0;
                foreach (var strip in part.Strips)
                    if (strip.Length >= 3)
                        stripTris += strip.Length - 2;
                AnsiConsole.WriteLine($"Triangles reconstructable from strips: {stripTris}");
                AnsiConsole.WriteLine($"Declared NumTriangles: {part.NumTriangles}");
            }
            else if (part.Triangles.Length > 0)
            {
                AnsiConsole.WriteLine($"Direct triangles available: {part.Triangles.Length}");
            }
        }
    }

    /// <summary>
    ///     Compare NiSkinPartition blocks from two NIF files (converted vs PC reference).
    /// </summary>
    private static void SkinPartCompare(string file1Path, string file2Path, int block1Index, int block2Index, int count)
    {
        var data1 = File.ReadAllBytes(file1Path);
        var data2 = File.ReadAllBytes(file2Path);
        var nif1 = NifParser.Parse(data1);
        var nif2 = NifParser.Parse(data2);

        AnsiConsole.WriteLine("\u2554\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2557");
        AnsiConsole.WriteLine("\u2551              NiSkinPartition Comparison                                \u2551");
        AnsiConsole.WriteLine("\u255a\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u255d");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine($"File 1: {Path.GetFileName(file1Path)} (Block {block1Index})");
        AnsiConsole.WriteLine($"File 2: {Path.GetFileName(file2Path)} (Block {block2Index})");
        AnsiConsole.WriteLine();

        // Validate block indices
        if (block1Index >= nif1.NumBlocks)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] " + $"Block {block1Index} out of range for file 1");
            return;
        }

        if (block2Index >= nif2.NumBlocks)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] " + $"Block {block2Index} out of range for file 2");
            return;
        }

        var type1 = nif1.GetBlockTypeName(block1Index);
        var type2 = nif2.GetBlockTypeName(block2Index);

        if (!type1.Contains("SkinPartition"))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] " +
                                   $"Block {block1Index} in file 1 is {type1}, not NiSkinPartition");
            return;
        }

        if (!type2.Contains("SkinPartition"))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] " +
                                   $"Block {block2Index} in file 2 is {type2}, not NiSkinPartition");
            return;
        }

        // Parse both skin partitions
        var offset1 = nif1.GetBlockOffset(block1Index);
        var size1 = (int)nif1.BlockSizes[block1Index];
        var offset2 = nif2.GetBlockOffset(block2Index);
        var size2 = (int)nif2.BlockSizes[block2Index];

        AnsiConsole.WriteLine($"File 1: Size={size1} bytes, Endian={(nif1.IsBigEndian ? "Big" : "Little")}");
        AnsiConsole.WriteLine($"File 2: Size={size2} bytes, Endian={(nif2.IsBigEndian ? "Big" : "Little")}");
        AnsiConsole.WriteLine();

        var skin1 = SkinPartitionParser.Parse(data1.AsSpan(offset1, size1), nif1.IsBigEndian);
        var skin2 = SkinPartitionParser.Parse(data2.AsSpan(offset2, size2), nif2.IsBigEndian);

        AnsiConsole.WriteLine($"{"Property",-25} {"File 1",-20} {"File 2",-20} {"Match"}");
        AnsiConsole.WriteLine(new string('\u2500', 75));
        Console.WriteLine(
            $"{"NumPartitions",-25} {skin1.NumPartitions,-20} {skin2.NumPartitions,-20} {(skin1.NumPartitions == skin2.NumPartitions ? "\u2713" : "\u2717")}");
        AnsiConsole.WriteLine();

        var numPartitions = Math.Min(skin1.NumPartitions, skin2.NumPartitions);

        for (var p = 0; p < numPartitions; p++)
        {
            var part1 = skin1.Partitions[p];
            var part2 = skin2.Partitions[p];

            AnsiConsole.WriteLine($"\u2550\u2550\u2550 Partition {p} \u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine($"{"Property",-25} {"File 1",-20} {"File 2",-20} {"Match"}");
            AnsiConsole.WriteLine(new string('\u2500', 75));
            Console.WriteLine(
                $"{"NumVertices",-25} {part1.NumVertices,-20} {part2.NumVertices,-20} {(part1.NumVertices == part2.NumVertices ? "\u2713" : "\u2717")}");
            Console.WriteLine(
                $"{"NumTriangles",-25} {part1.NumTriangles,-20} {part2.NumTriangles,-20} {(part1.NumTriangles == part2.NumTriangles ? "\u2713" : "\u2717")}");
            Console.WriteLine(
                $"{"NumBones",-25} {part1.NumBones,-20} {part2.NumBones,-20} {(part1.NumBones == part2.NumBones ? "\u2713" : "\u2717")}");
            Console.WriteLine(
                $"{"NumStrips",-25} {part1.NumStrips,-20} {part2.NumStrips,-20} {(part1.NumStrips == part2.NumStrips ? "\u2713" : "\u2717")}");
            Console.WriteLine(
                $"{"NumWeightsPerVertex",-25} {part1.NumWeightsPerVertex,-20} {part2.NumWeightsPerVertex,-20} {(part1.NumWeightsPerVertex == part2.NumWeightsPerVertex ? "\u2713" : "\u2717")}");
            Console.WriteLine(
                $"{"HasVertexMap",-25} {part1.HasVertexMap,-20} {part2.HasVertexMap,-20} {(part1.HasVertexMap == part2.HasVertexMap ? "\u2713" : "\u2717")}");
            Console.WriteLine(
                $"{"HasVertexWeights",-25} {part1.HasVertexWeights,-20} {part2.HasVertexWeights,-20} {(part1.HasVertexWeights == part2.HasVertexWeights ? "\u2713" : "\u2717")}");
            Console.WriteLine(
                $"{"HasFaces",-25} {part1.HasFaces,-20} {part2.HasFaces,-20} {(part1.HasFaces == part2.HasFaces ? "\u2713" : "\u2717")}");
            Console.WriteLine(
                $"{"HasBoneIndices",-25} {part1.HasBoneIndices,-20} {part2.HasBoneIndices,-20} {(part1.HasBoneIndices == part2.HasBoneIndices ? "\u2713" : "\u2717")}");
            AnsiConsole.WriteLine();

            // Compare bones array
            var bonesMatch = part1.Bones.Length == part2.Bones.Length && part1.Bones.SequenceEqual(part2.Bones);
            AnsiConsole.WriteLine($"Bones array: {(bonesMatch ? "\u2713 Match" : "\u2717 Mismatch")}");
            if (!bonesMatch && part1.Bones.Length > 0 && part2.Bones.Length > 0)
            {
                Console.WriteLine(
                    $"  File 1: [{string.Join(", ", part1.Bones.Take(10))}{(part1.Bones.Length > 10 ? "..." : "")}]");
                Console.WriteLine(
                    $"  File 2: [{string.Join(", ", part2.Bones.Take(10))}{(part2.Bones.Length > 10 ? "..." : "")}]");
            }

            // Compare vertex map
            var vmapMatch = part1.VertexMap.Length == part2.VertexMap.Length &&
                            part1.VertexMap.SequenceEqual(part2.VertexMap);
            Console.WriteLine(
                $"VertexMap: {(vmapMatch ? "\u2713 Match" : "\u2717 Mismatch")} ({part1.VertexMap.Length} vs {part2.VertexMap.Length} entries)");
            if (!vmapMatch && part1.VertexMap.Length > 0 && part2.VertexMap.Length > 0)
            {
                // Show first few mismatches
                var mismatches = 0;
                for (var i = 0; i < Math.Min(part1.VertexMap.Length, part2.VertexMap.Length) && mismatches < 5; i++)
                    if (part1.VertexMap[i] != part2.VertexMap[i])
                    {
                        AnsiConsole.WriteLine($"  [VM {i}] File 1: {part1.VertexMap[i]}, File 2: {part2.VertexMap[i]}");
                        mismatches++;
                    }
            }

            // Compare weights if both have them
            if (part1.HasVertexWeights != 0 && part2.HasVertexWeights != 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.WriteLine($"=== Vertex Weights (first {Math.Min(count, part1.NumVertices)} vertices) ===");

                var weightMatches = 0;
                var weightMismatches = 0;
                var displayedMismatches = 0;

                var vertsToCompare = Math.Min(count, Math.Min(part1.NumVertices, part2.NumVertices));

                for (var v = 0; v < vertsToCompare; v++)
                {
                    var allMatch = true;
                    for (var w = 0; w < Math.Min(part1.NumWeightsPerVertex, part2.NumWeightsPerVertex); w++)
                    {
                        var w1 = part1.VertexWeights[v][w];
                        var w2 = part2.VertexWeights[v][w];
                        if (Math.Abs(w1 - w2) > 0.001f)
                        {
                            allMatch = false;
                            break;
                        }
                    }

                    if (allMatch)
                    {
                        weightMatches++;
                    }
                    else
                    {
                        weightMismatches++;
                        if (displayedMismatches < 5)
                        {
                            Console.WriteLine(
                                $"  [V{v}] File 1: [{string.Join(", ", part1.VertexWeights[v].Select(f => f.ToString("F4")))}]");
                            Console.WriteLine(
                                $"       File 2: [{string.Join(", ", part2.VertexWeights[v].Select(f => f.ToString("F4")))}]");
                            displayedMismatches++;
                        }
                    }
                }

                Console.WriteLine(
                    $"  Weight matches: {weightMatches}/{vertsToCompare}, mismatches: {weightMismatches}");
            }

            // Compare bone indices if both have them
            if (part1.HasBoneIndices != 0 && part2.HasBoneIndices != 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.WriteLine($"=== Bone Indices (first {Math.Min(count, part1.NumVertices)} vertices) ===");

                var idxMatches = 0;
                var idxMismatches = 0;
                var displayedMismatches = 0;

                var vertsToCompare = Math.Min(count, Math.Min(part1.NumVertices, part2.NumVertices));

                for (var v = 0; v < vertsToCompare; v++)
                {
                    var allMatch = part1.BoneIndices[v].SequenceEqual(part2.BoneIndices[v]);

                    if (allMatch)
                    {
                        idxMatches++;
                    }
                    else
                    {
                        idxMismatches++;
                        if (displayedMismatches < 5)
                        {
                            AnsiConsole.WriteLine($"  [V{v}] File 1: [{string.Join(", ", part1.BoneIndices[v])}]");
                            AnsiConsole.WriteLine($"       File 2: [{string.Join(", ", part2.BoneIndices[v])}]");
                            displayedMismatches++;
                        }
                    }
                }

                AnsiConsole.WriteLine(
                    $"  Bone index matches: {idxMatches}/{vertsToCompare}, mismatches: {idxMismatches}");
            }

            // Compare triangles or strips
            if (part1.NumStrips > 0 || part2.NumStrips > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.WriteLine("=== Strips ===");
                var stripsMatch = part1.StripLengths.Length == part2.StripLengths.Length &&
                                  part1.StripLengths.SequenceEqual(part2.StripLengths);
                AnsiConsole.WriteLine($"  StripLengths: {(stripsMatch ? "\u2713 Match" : "\u2717 Mismatch")}");
                if (!stripsMatch)
                {
                    AnsiConsole.WriteLine($"    File 1: [{string.Join(", ", part1.StripLengths)}]");
                    AnsiConsole.WriteLine($"    File 2: [{string.Join(", ", part2.StripLengths)}]");
                }
            }
            else if (part1.Triangles.Length > 0 || part2.Triangles.Length > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.WriteLine("=== Triangles ===");
                var triMatch = part1.Triangles.Length == part2.Triangles.Length;
                if (triMatch)
                {
                    var mismatches = 0;
                    for (var t = 0; t < part1.Triangles.Length; t++)
                        if (part1.Triangles[t] != part2.Triangles[t])
                            mismatches++;
                    AnsiConsole.WriteLine($"  Triangles: {part1.Triangles.Length} total, {mismatches} mismatches");
                }
                else
                {
                    Console.WriteLine(
                        $"  Triangles count mismatch: {part1.Triangles.Length} vs {part2.Triangles.Length}");
                }
            }

            AnsiConsole.WriteLine();
        }
    }

    public static Command CreateSkinPartCommand()
    {
        var command = new Command("skinpart", "Parse NiSkinPartition block");
        var fileArg = new Argument<string>("file") { Description = "NIF file path" };
        var blockArg = new Argument<int>("block") { Description = "Block index" };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(blockArg);
        command.SetAction(parseResult => SkinPart(parseResult.GetValue(fileArg), parseResult.GetValue(blockArg)));
        return command;
    }

    public static Command CreateSkinPartCompareCommand()
    {
        var command = new Command("skinpartcompare", "Compare NiSkinPartition blocks between two files");
        var file1Arg = new Argument<string>("file1") { Description = "First NIF file path" };
        var file2Arg = new Argument<string>("file2") { Description = "Second NIF file path" };
        var block1Arg = new Argument<int>("block1") { Description = "Block index in first file" };
        var block2Arg = new Argument<int>("block2") { Description = "Block index in second file" };
        var countOpt = new Option<int>("-c", "--count")
        { Description = "Max partitions to compare", DefaultValueFactory = _ => 50 };
        command.Arguments.Add(file1Arg);
        command.Arguments.Add(file2Arg);
        command.Arguments.Add(block1Arg);
        command.Arguments.Add(block2Arg);
        command.Options.Add(countOpt);
        command.SetAction(parseResult => SkinPartCompare(
            parseResult.GetValue(file1Arg), parseResult.GetValue(file2Arg),
            parseResult.GetValue(block1Arg), parseResult.GetValue(block2Arg), parseResult.GetValue(countOpt)));
        return command;
    }
}
