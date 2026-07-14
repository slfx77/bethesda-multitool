using System.CommandLine;
using NifAnalyzer.Models;
using NifAnalyzer.Parsers;
using Spectre.Console;
using static BethesdaMultitool.Core.Formats.Nif.Conversion.NifEndianUtils;
using static BethesdaMultitool.Core.Utils.BinaryUtils;

namespace NifAnalyzer.Commands;

/// <summary>
///     Commands for geometry analysis: geometry, geomcompare, vertices.
/// </summary>
internal static class GeometryCommands
{
    /// <summary>
    ///     Create the "geometry" command for geometry block analysis.
    /// </summary>
    public static Command CreateGeometryCommand()
    {
        var command = new Command("geometry", "Parse NiTriShapeData/NiTriStripsData geometry block details");
        var fileArg = new Argument<string>("file") { Description = "NIF file path" };
        var blockArg = new Argument<int>("block") { Description = "Block index" };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(blockArg);
        command.SetAction(parseResult => Geometry(parseResult.GetValue(fileArg), parseResult.GetValue(blockArg)));
        return command;
    }

    /// <summary>
    ///     Create the "geomcompare" command for comparing geometry between two files.
    /// </summary>
    public static Command CreateGeomCompareCommand()
    {
        var command = new Command("geomcompare", "Compare geometry data between two NIF files at the same block index");
        var file1Arg = new Argument<string>("file1") { Description = "First NIF file path" };
        var file2Arg = new Argument<string>("file2") { Description = "Second NIF file path" };
        var blockArg = new Argument<int>("block") { Description = "Block index (same for both files)" };
        command.Arguments.Add(file1Arg);
        command.Arguments.Add(file2Arg);
        command.Arguments.Add(blockArg);
        command.SetAction(parseResult => GeomCompare(
            parseResult.GetValue(file1Arg), parseResult.GetValue(file2Arg), parseResult.GetValue(blockArg)));
        return command;
    }

    /// <summary>
    ///     Create the "vertices" command for extracting vertex data.
    /// </summary>
    public static Command CreateVerticesCommand()
    {
        var command = new Command("vertices", "Extract and display vertex data from a geometry block");
        var fileArg = new Argument<string>("file") { Description = "NIF file path" };
        var blockArg = new Argument<int>("block") { Description = "Block index" };
        var countArg = new Argument<int>("count")
            { Description = "Number of vertices to display", DefaultValueFactory = _ => 10 };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(blockArg);
        command.Arguments.Add(countArg);
        command.SetAction(parseResult => Vertices(
            parseResult.GetValue(fileArg), parseResult.GetValue(blockArg), parseResult.GetValue(countArg)));
        return command;
    }

    /// <summary>
    ///     Create the "colorcompare" command for vertex color comparison.
    /// </summary>
    public static Command CreateColorCompareCommand()
    {
        var command = new Command("colorcompare", "Compare vertex colors between two geometry blocks");
        var file1Arg = new Argument<string>("file1") { Description = "First NIF file path" };
        var file2Arg = new Argument<string>("file2") { Description = "Second NIF file path" };
        var block1Arg = new Argument<int>("block1") { Description = "Block index in first file" };
        var block2Arg = new Argument<int>("block2") { Description = "Block index in second file" };
        var countOption = new Option<int>("-n", "--count")
            { Description = "Number of colors to compare", DefaultValueFactory = _ => 20 };
        command.Arguments.Add(file1Arg);
        command.Arguments.Add(file2Arg);
        command.Arguments.Add(block1Arg);
        command.Arguments.Add(block2Arg);
        command.Options.Add(countOption);
        command.SetAction(parseResult => ColorCompare(
            parseResult.GetValue(file1Arg), parseResult.GetValue(file2Arg),
            parseResult.GetValue(block1Arg), parseResult.GetValue(block2Arg), parseResult.GetValue(countOption)));
        return command;
    }

    private static void Geometry(string path, int blockIndex)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Block index {blockIndex} out of range (0-{nif.NumBlocks - 1})");
            return;
        }

        var offset = nif.GetBlockOffset(blockIndex);
        var typeName = nif.GetBlockTypeName(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];

        var infoTable = new Table().Border(TableBorder.Rounded);
        infoTable.AddColumn("Property");
        infoTable.AddColumn("Value");
        infoTable.AddRow("Block", $"{blockIndex}: [cyan]{Markup.Escape(typeName)}[/]");
        infoTable.AddRow("Offset", $"0x{offset:X4}");
        infoTable.AddRow("Size", $"{size} bytes");
        infoTable.AddRow("Endian", nif.IsBigEndian ? "[yellow]Big (Xbox 360)[/]" : "[green]Little (PC)[/]");
        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        if (!TryParseGeometryBlock(data, offset, size, nif, typeName, out var geom))
        {
            return;
        }

        AnsiConsole.Write(new Rule("[bold]Geometry Data[/]").LeftJustified());

        var geomTable = new Table().Border(TableBorder.Simple);
        geomTable.AddColumn("Field");
        geomTable.AddColumn(new TableColumn("Offset").RightAligned());
        geomTable.AddColumn("Value");
        AddGeometryFieldRow(geomTable, geom, "GroupId", "GroupId", geom.GroupId.ToString());
        AddGeometryFieldRow(geomTable, geom, "NumVertices", "NumVertices", geom.NumVertices.ToString());
        AddGeometryFieldRow(geomTable, geom, "KeepFlags", "KeepFlags", geom.KeepFlags.ToString());
        AddGeometryFieldRow(geomTable, geom, "CompressFlags", "CompressFlags", geom.CompressFlags.ToString());
        AddGeometryFieldRow(geomTable, geom, "HasVertices", "HasVertices", geom.HasVertices.ToString());
        var dataFlagsLabel = geom.UsesBsDataFlags ? "BSDataFlags" : "DataFlags";
        AddGeometryFieldRow(geomTable, geom, dataFlagsLabel, "DataFlags", $"0x{geom.DataFlags:X4}");
        AddGeometryFieldRow(geomTable, geom, "MaterialCrc", "MaterialCrc", $"0x{geom.MaterialCrc:X8}");
        AddGeometryFieldRow(geomTable, geom, "HasNormals", "HasNormals", geom.HasNormals.ToString());
        AddGeometryFieldRow(geomTable, geom, "Tangents", "Tangents", $"{geom.NumVertices} × Vector3");
        AddGeometryFieldRow(geomTable, geom, "Bitangents", "Bitangents", $"{geom.NumVertices} × Vector3");
        AddGeometryFieldRow(
            geomTable,
            geom,
            "BoundingSphere.Center",
            "BoundingSphere",
            $"({geom.BoundingCenterX:F2}, {geom.BoundingCenterY:F2}, {geom.BoundingCenterZ:F2})");
        AddGeometryFieldRow(
            geomTable, geom, "BoundingSphere.Radius", "BoundingRadius", $"{geom.BoundingRadius:F2}");
        AddGeometryFieldRow(
            geomTable, geom, "HasVertexColors", "HasVertexColors", geom.HasVertexColors.ToString());
        AddGeometryFieldRow(geomTable, geom, "HasUv", "HasUv", geom.HasUv.ToString());
        geomTable.AddRow("NumUvSets", "-", $"{geom.NumUvSets} (from {dataFlagsLabel})");
        AddGeometryFieldRow(
            geomTable, geom, "ConsistencyFlags", "ConsistencyFlags", geom.ConsistencyFlags.ToString());
        AddGeometryFieldRow(
            geomTable, geom, "AdditionalData", "AdditionalData", $"{geom.AdditionalData} (block ref)");
        AnsiConsole.Write(geomTable);
        AnsiConsole.WriteLine();

        if (typeName == "NiTriShapeData")
        {
            AnsiConsole.Write(new Rule("[bold]NiTriShapeData Specific[/]").LeftJustified());

            var triTable = new Table().Border(TableBorder.Simple);
            triTable.AddColumn("Field");
            triTable.AddColumn(new TableColumn("Offset").RightAligned());
            triTable.AddColumn("Value");
            AddGeometryFieldRow(
                triTable, geom, "NumTriangles", "NumTriangles", geom.NumTriangles.ToString());
            AddGeometryFieldRow(
                triTable, geom, "NumTrianglePoints", "NumTrianglePoints", geom.NumTrianglePoints.ToString());
            if (geom.FieldOffsets.ContainsKey("HasTriangles"))
            {
                AddGeometryFieldRow(
                    triTable, geom, "HasTriangles", "HasTriangles", geom.HasTriangles.ToString());
            }
            else
            {
                triTable.AddRow("HasTriangles", "-", $"{geom.HasTriangles} (implicit for this NIF version)");
            }

            AddGeometryFieldRow(
                triTable, geom, "NumMatchGroups", "NumMatchGroups", geom.NumMatchGroups.ToString());
            AnsiConsole.Write(triTable);
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"Parsed size: [cyan]{geom.ParsedSize}[/] bytes");
            AnsiConsole.MarkupLine($"Actual block size: [cyan]{size}[/] bytes");
            AnsiConsole.MarkupLine($"Remaining/unaccounted bytes: [cyan]{size - geom.ParsedSize}[/]");

            // Calculate expected triangle data size
            var expectedTriDataSize = geom.NumTriangles * 6; // 3 ushorts per triangle
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Triangle data analysis:[/]");
            AnsiConsole.MarkupLine($"  NumTriangles × 6 bytes = {expectedTriDataSize} bytes");
            AnsiConsole.MarkupLine($"  Unaccounted bytes = {size - geom.ParsedSize} bytes");

            if (geom.HasTriangles == 0 && size - geom.ParsedSize > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(
                    $"[yellow bold]*** WARNING: HasTriangles=0 but {size - geom.ParsedSize} bytes remain in block! ***[/]");
                AnsiConsole.MarkupLine("[yellow]*** This suggests triangles ARE present despite HasTriangles=0 ***[/]");
            }
        }
        else
        {
            AnsiConsole.Write(new Rule("[bold]NiTriStripsData Specific[/]").LeftJustified());
            AnsiConsole.MarkupLine($"NumTriangles: {geom.NumTriangles}");
            AnsiConsole.MarkupLine($"NumStrips: {geom.NumStrips}");
            AnsiConsole.MarkupLine($"HasPoints: {geom.HasPoints}");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Raw Bytes at Triangle Fields[/]").LeftJustified());

        // Show raw bytes from NumTriangles through end of block
        var triFieldOffset = geom.FieldOffsets.GetValueOrDefault("NumTriangles");
        var dumpLen = Math.Min(64, size - triFieldOffset);
        if (triFieldOffset > 0 && dumpLen > 0)
        {
            AnsiConsole.MarkupLine($"[dim]From NumTriangles field (relative offset 0x{triFieldOffset:X4}):[/]");
            HexDump(data, offset + triFieldOffset, dumpLen);
        }
    }

    private static void GeomCompare(string path1, string path2, int blockIndex)
    {
        var data1 = File.ReadAllBytes(path1);
        var data2 = File.ReadAllBytes(path2);
        var nif1 = NifParser.Parse(data1);
        var nif2 = NifParser.Parse(data2);

        if (blockIndex < 0 || blockIndex >= nif1.NumBlocks || blockIndex >= nif2.NumBlocks)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Block index {blockIndex} out of range");
            return;
        }

        var offset1 = nif1.GetBlockOffset(blockIndex);
        var offset2 = nif2.GetBlockOffset(blockIndex);

        var type1 = nif1.GetBlockTypeName(blockIndex);
        var type2 = nif2.GetBlockTypeName(blockIndex);
        var size1 = (int)nif1.BlockSizes[blockIndex];
        var size2 = (int)nif2.BlockSizes[blockIndex];

        AnsiConsole.Write(new Rule("[bold]Geometry Block Comparison[/]").LeftJustified());

        var infoTable = new Table().Border(TableBorder.Rounded);
        infoTable.AddColumn("Property");
        infoTable.AddColumn("File 1");
        infoTable.AddColumn("File 2");
        infoTable.AddRow("File", Markup.Escape(Path.GetFileName(path1)), Markup.Escape(Path.GetFileName(path2)));
        infoTable.AddRow("Endian", nif1.IsBigEndian ? "[yellow]Big (Xbox)[/]" : "[green]Little (PC)[/]",
            nif2.IsBigEndian ? "[yellow]Big (Xbox)[/]" : "[green]Little (PC)[/]");
        infoTable.AddRow("Block Type", $"[cyan]{Markup.Escape(type1)}[/]", $"[cyan]{Markup.Escape(type2)}[/]");
        infoTable.AddRow("Block Size", size1.ToString(), size2.ToString());
        infoTable.AddRow("Offset", $"0x{offset1:X4}", $"0x{offset2:X4}");
        AnsiConsole.Write(infoTable);

        if (!TryParseGeometryBlock(data1, offset1, size1, nif1, type1, out var geom1)
            || !TryParseGeometryBlock(data2, offset2, size2, nif2, type2, out var geom2))
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Geometry Fields[/]").LeftJustified());

        var compareTable = new Table().Border(TableBorder.Simple);
        compareTable.AddColumn("Field");
        compareTable.AddColumn("File 1");
        compareTable.AddColumn("File 2");
        compareTable.AddColumn("Match");

        AddOptionalCompareRow(compareTable, "GroupId", geom1, geom2, "GroupId", geom1.GroupId, geom2.GroupId);
        AddCompareRow(compareTable, "NumVertices", geom1.NumVertices, geom2.NumVertices);
        AddOptionalCompareRow(
            compareTable, "KeepFlags", geom1, geom2, "KeepFlags", geom1.KeepFlags, geom2.KeepFlags);
        AddOptionalCompareRow(
            compareTable, "CompressFlags", geom1, geom2, "CompressFlags", geom1.CompressFlags, geom2.CompressFlags);
        AddCompareRow(compareTable, "HasVertices", geom1.HasVertices, geom2.HasVertices);
        AddCompareRow(compareTable, "DataFlags kind",
            geom1.UsesBsDataFlags ? "BSDataFlags" : "DataFlags",
            geom2.UsesBsDataFlags ? "BSDataFlags" : "DataFlags");
        AddCompareRow(compareTable, "DataFlags", $"0x{geom1.DataFlags:X4}", $"0x{geom2.DataFlags:X4}");
        AddOptionalCompareRow(
            compareTable,
            "MaterialCrc",
            geom1,
            geom2,
            "MaterialCrc",
            geom1.MaterialCrc,
            geom2.MaterialCrc,
            static value => $"0x{value:X8}");
        AddCompareRow(compareTable, "HasNormals", geom1.HasNormals, geom2.HasNormals);
        AddCompareRow(compareTable, "BoundingSphere.Center.X", geom1.BoundingCenterX.ToString("F4"),
            geom2.BoundingCenterX.ToString("F4"));
        AddCompareRow(compareTable, "BoundingSphere.Center.Y", geom1.BoundingCenterY.ToString("F4"),
            geom2.BoundingCenterY.ToString("F4"));
        AddCompareRow(compareTable, "BoundingSphere.Center.Z", geom1.BoundingCenterZ.ToString("F4"),
            geom2.BoundingCenterZ.ToString("F4"));
        AddCompareRow(compareTable, "BoundingSphere.Radius", geom1.BoundingRadius.ToString("F4"),
            geom2.BoundingRadius.ToString("F4"));
        AddCompareRow(compareTable, "HasVertexColors", geom1.HasVertexColors, geom2.HasVertexColors);
        AddOptionalCompareRow(compareTable, "HasUv", geom1, geom2, "HasUv", geom1.HasUv, geom2.HasUv);
        AddCompareRow(compareTable, "NumUvSets", geom1.NumUvSets, geom2.NumUvSets);
        AddOptionalCompareRow(
            compareTable,
            "ConsistencyFlags",
            geom1,
            geom2,
            "ConsistencyFlags",
            geom1.ConsistencyFlags,
            geom2.ConsistencyFlags);
        AddOptionalCompareRow(
            compareTable,
            "AdditionalData",
            geom1,
            geom2,
            "AdditionalData",
            geom1.AdditionalData,
            geom2.AdditionalData);
        AddCompareRow(compareTable, "NumTriangles", geom1.NumTriangles, geom2.NumTriangles);
        AddCompareRow(compareTable, "NumTrianglePoints", geom1.NumTrianglePoints, geom2.NumTrianglePoints);
        AddCompareRow(compareTable, "HasTriangles", geom1.HasTriangles, geom2.HasTriangles);

        AnsiConsole.Write(compareTable);

        // If vertices are present in both, compare first few
        if (geom1.HasVertices != 0 && geom2.HasVertices != 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[bold]Vertex Data Sample (first 5)[/]").LeftJustified());

            var verts1 = ExtractVertices(data1.AsSpan(offset1, size1), nif1.IsBigEndian, geom1,
                Math.Min(5, (int)geom1.NumVertices));
            var verts2 = ExtractVertices(data2.AsSpan(offset2, size2), nif2.IsBigEndian, geom2,
                Math.Min(5, (int)geom2.NumVertices));

            var vertTable = new Table().Border(TableBorder.Simple);
            vertTable.AddColumn(new TableColumn("Idx").RightAligned());
            vertTable.AddColumn("File 1 (X, Y, Z)");
            vertTable.AddColumn("File 2 (X, Y, Z)");
            vertTable.AddColumn("Match");

            for (var i = 0; i < Math.Min(verts1.Count, verts2.Count); i++)
            {
                var v1 = verts1[i];
                var v2 = verts2[i];
                var match = Math.Abs(v1.X - v2.X) < 0.001f && Math.Abs(v1.Y - v2.Y) < 0.001f &&
                            Math.Abs(v1.Z - v2.Z) < 0.001f;
                vertTable.AddRow(
                    i.ToString(),
                    $"({v1.X,10:F4}, {v1.Y,10:F4}, {v1.Z,10:F4})",
                    $"({v2.X,10:F4}, {v2.Y,10:F4}, {v2.Z,10:F4})",
                    match ? "[green]✓[/]" : "[red]✗[/]");
            }

            AnsiConsole.Write(vertTable);
        }
    }

    private static void Vertices(string path, int blockIndex, int count)
    {
        if (count < 0)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Vertex count {count} cannot be negative");
            return;
        }

        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Block index {blockIndex} out of range");
            return;
        }

        var offset = nif.GetBlockOffset(blockIndex);
        var typeName = nif.GetBlockTypeName(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];

        var infoTable = new Table().Border(TableBorder.Rounded);
        infoTable.AddColumn("Property");
        infoTable.AddColumn("Value");
        infoTable.AddRow("Block", $"{blockIndex}: [cyan]{Markup.Escape(typeName)}[/]");
        infoTable.AddRow("Endian", nif.IsBigEndian ? "[yellow]Big (Xbox 360)[/]" : "[green]Little (PC)[/]");
        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        if (!TryParseGeometryBlock(data, offset, size, nif, typeName, out var geom))
        {
            return;
        }
        count = Math.Min(count, geom.NumVertices);

        AnsiConsole.MarkupLine($"NumVertices: [cyan]{geom.NumVertices}[/]");
        AnsiConsole.MarkupLine($"HasVertices: [cyan]{geom.HasVertices}[/]");
        AnsiConsole.MarkupLine($"HasNormals: [cyan]{geom.HasNormals}[/]");
        var dataFlagsLabel = geom.UsesBsDataFlags ? "BSDataFlags" : "DataFlags";
        AnsiConsole.MarkupLine($"{dataFlagsLabel}: [cyan]0x{geom.DataFlags:X4}[/]");
        AnsiConsole.MarkupLine($"NumUvSets: [cyan]{geom.NumUvSets}[/]");
        AnsiConsole.WriteLine();

        if (geom.HasVertices == 0)
        {
            AnsiConsole.MarkupLine("[yellow]HasVertices=0 - Vertices stored in BSPackedAdditionalGeometryData[/]");
            return;
        }

        // Extract and display vertex data
        var verts = ExtractVertices(data.AsSpan(offset, size), nif.IsBigEndian, geom, count);

        AnsiConsole.Write(new Rule($"[bold]Vertices (first {count})[/]").LeftJustified());

        var vertTable = new Table().Border(TableBorder.Simple);
        vertTable.AddColumn(new TableColumn("Idx").RightAligned());
        vertTable.AddColumn(new TableColumn("X").RightAligned());
        vertTable.AddColumn(new TableColumn("Y").RightAligned());
        vertTable.AddColumn(new TableColumn("Z").RightAligned());
        for (var i = 0; i < verts.Count; i++)
            vertTable.AddRow(i.ToString(), $"{verts[i].X:F4}", $"{verts[i].Y:F4}", $"{verts[i].Z:F4}");

        AnsiConsole.Write(vertTable);

        // Extract normals if present
        if (geom.HasNormals != 0)
        {
            var normals = ExtractNormals(data.AsSpan(offset, size), nif.IsBigEndian, geom, count);
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold]Normals (first {count})[/]").LeftJustified());

            var normalTable = new Table().Border(TableBorder.Simple);
            normalTable.AddColumn(new TableColumn("Idx").RightAligned());
            normalTable.AddColumn(new TableColumn("X").RightAligned());
            normalTable.AddColumn(new TableColumn("Y").RightAligned());
            normalTable.AddColumn(new TableColumn("Z").RightAligned());
            for (var i = 0; i < normals.Count; i++)
                normalTable.AddRow(i.ToString(), $"{normals[i].X:F4}", $"{normals[i].Y:F4}", $"{normals[i].Z:F4}");

            AnsiConsole.Write(normalTable);
        }

        // Extract UVs if present
        if (geom.NumUvSets > 0)
        {
            var uvs = ExtractUVs(data.AsSpan(offset, size), nif.IsBigEndian, geom, count);
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold]UVs (first {count})[/]").LeftJustified());

            var uvTable = new Table().Border(TableBorder.Simple);
            uvTable.AddColumn(new TableColumn("Idx").RightAligned());
            uvTable.AddColumn(new TableColumn("U").RightAligned());
            uvTable.AddColumn(new TableColumn("V").RightAligned());
            for (var i = 0; i < uvs.Count; i++) uvTable.AddRow(i.ToString(), $"{uvs[i].U:F6}", $"{uvs[i].V:F6}");

            AnsiConsole.Write(uvTable);
        }
    }

    private static void AddCompareRow<T>(Table table, string name, T val1, T val2)
    {
        var match = EqualityComparer<T>.Default.Equals(val1, val2);
        table.AddRow(name, val1?.ToString() ?? "null", val2?.ToString() ?? "null", match ? "[green]✓[/]" : "[red]✗[/]");
    }

    private static void AddOptionalCompareRow<T>(
        Table table,
        string name,
        GeometryInfo geometry1,
        GeometryInfo geometry2,
        string offsetKey,
        T val1,
        T val2,
        Func<T, string>? format = null)
    {
        var present1 = geometry1.FieldOffsets.ContainsKey(offsetKey);
        var present2 = geometry2.FieldOffsets.ContainsKey(offsetKey);
        var match = present1 == present2
                    && (!present1 || EqualityComparer<T>.Default.Equals(val1, val2));
        var display1 = present1 ? format?.Invoke(val1) ?? val1?.ToString() ?? "null" : "[dim]<absent>[/]";
        var display2 = present2 ? format?.Invoke(val2) ?? val2?.ToString() ?? "null" : "[dim]<absent>[/]";
        table.AddRow(name, display1, display2, match ? "[green]✓[/]" : "[red]✗[/]");
    }

    private static bool TryParseGeometryBlock(
        byte[] data,
        int offset,
        int size,
        NifInfo nif,
        string typeName,
        out GeometryInfo geometry)
    {
        if (offset < 0 || size < 0 || offset > data.Length - size)
        {
            geometry = new GeometryInfo();
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Geometry block range 0x{offset:X}+{size} exceeds the {data.Length}-byte file");
            return false;
        }

        if (!GeometryParser.TryParse(
                data.AsSpan(offset, size),
                nif.IsBigEndian,
                nif.Version,
                nif.BsVersion,
                typeName,
                out geometry,
                out var error))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(error)}");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(geometry.ParseWarning))
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(geometry.ParseWarning)}");
        }

        return true;
    }

    private static void AddGeometryFieldRow(
        Table table,
        GeometryInfo geometry,
        string label,
        string offsetKey,
        string value)
    {
        if (geometry.FieldOffsets.TryGetValue(offsetKey, out var offset))
        {
            table.AddRow(label, $"0x{offset:X4}", value);
        }
    }

    internal static List<(float X, float Y, float Z)> ExtractVertices(ReadOnlySpan<byte> blockData, bool bigEndian,
        GeometryInfo geom, int count)
    {
        var result = new List<(float, float, float)>();
        if (geom.HasVertices == 0 || !geom.FieldOffsets.TryGetValue("Vertices", out var vertOffset))
            return result;

        for (var i = 0; i < count; i++)
        {
            var pos = vertOffset + i * 12;
            var x = ReadFloat(blockData, pos, bigEndian);
            var y = ReadFloat(blockData, pos + 4, bigEndian);
            var z = ReadFloat(blockData, pos + 8, bigEndian);
            result.Add((x, y, z));
        }

        return result;
    }

    private static List<(float X, float Y, float Z)> ExtractNormals(ReadOnlySpan<byte> blockData, bool bigEndian,
        GeometryInfo geom, int count)
    {
        var result = new List<(float, float, float)>();
        if (geom.HasNormals == 0 || !geom.FieldOffsets.TryGetValue("Normals", out var normOffset))
            return result;

        for (var i = 0; i < count; i++)
        {
            var pos = normOffset + i * 12;
            var x = ReadFloat(blockData, pos, bigEndian);
            var y = ReadFloat(blockData, pos + 4, bigEndian);
            var z = ReadFloat(blockData, pos + 8, bigEndian);
            result.Add((x, y, z));
        }

        return result;
    }

    private static List<(float U, float V)> ExtractUVs(ReadOnlySpan<byte> blockData, bool bigEndian, GeometryInfo geom,
        int count)
    {
        var result = new List<(float, float)>();
        if (geom.NumUvSets == 0 || !geom.FieldOffsets.TryGetValue("UVSets", out var uvOffset))
            return result;

        for (var i = 0; i < count; i++)
        {
            var pos = uvOffset + i * 8;
            var u = ReadFloat(blockData, pos, bigEndian);
            var v = ReadFloat(blockData, pos + 4, bigEndian);
            result.Add((u, v));
        }

        return result;
    }

    /// <summary>
    ///     Compare vertex colors between two geometry blocks (e.g., converted vs PC reference).
    /// </summary>
    private static void ColorCompare(string path1, string path2, int block1, int block2, int count)
    {
        if (count < 0)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Color count {count} cannot be negative");
            return;
        }

        var data1 = File.ReadAllBytes(path1);
        var data2 = File.ReadAllBytes(path2);
        var nif1 = NifParser.Parse(data1);
        var nif2 = NifParser.Parse(data2);

        if (block1 < 0 || block2 < 0 || block1 >= nif1.NumBlocks || block2 >= nif2.NumBlocks)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Block index out of range");
            return;
        }

        var offset1 = nif1.GetBlockOffset(block1);
        var offset2 = nif2.GetBlockOffset(block2);

        var type1 = nif1.GetBlockTypeName(block1);
        var type2 = nif2.GetBlockTypeName(block2);
        var size1 = (int)nif1.BlockSizes[block1];
        var size2 = (int)nif2.BlockSizes[block2];

        AnsiConsole.Write(new Rule("[bold]Vertex Color Comparison[/]").LeftJustified());

        var infoTable = new Table().Border(TableBorder.Rounded);
        infoTable.AddColumn("Property");
        infoTable.AddColumn("File 1");
        infoTable.AddColumn("File 2");
        infoTable.AddRow("File", Markup.Escape(Path.GetFileName(path1)), Markup.Escape(Path.GetFileName(path2)));
        infoTable.AddRow("Block", block1.ToString(), block2.ToString());
        infoTable.AddRow("Type", $"[cyan]{Markup.Escape(type1)}[/]", $"[cyan]{Markup.Escape(type2)}[/]");
        infoTable.AddRow("Endian", nif1.IsBigEndian ? "[yellow]Big[/]" : "[green]Little[/]",
            nif2.IsBigEndian ? "[yellow]Big[/]" : "[green]Little[/]");

        if (!TryParseGeometryBlock(data1, offset1, size1, nif1, type1, out var geom1)
            || !TryParseGeometryBlock(data2, offset2, size2, nif2, type2, out var geom2))
        {
            return;
        }

        infoTable.AddRow("NumVertices", geom1.NumVertices.ToString(), geom2.NumVertices.ToString());
        infoTable.AddRow("HasVertexColors", geom1.HasVertexColors.ToString(), geom2.HasVertexColors.ToString());
        AnsiConsole.Write(infoTable);

        if (geom1.HasVertexColors == 0 && geom2.HasVertexColors == 0)
        {
            AnsiConsole.MarkupLine("\n[yellow]Neither block has vertex colors.[/]");
            return;
        }

        count = Math.Min(count, Math.Min(geom1.NumVertices, geom2.NumVertices));

        var colors1 = ExtractVertexColors(data1.AsSpan(offset1, size1), nif1.IsBigEndian, geom1, count);
        var colors2 = ExtractVertexColors(data2.AsSpan(offset2, size2), nif2.IsBigEndian, geom2, count);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold]Vertex Colors (first {count})[/]").LeftJustified());

        var colorTable = new Table().Border(TableBorder.Simple);
        colorTable.AddColumn(new TableColumn("Idx").RightAligned());
        colorTable.AddColumn("File 1 (R, G, B, A)");
        colorTable.AddColumn("File 2 (R, G, B, A)");
        colorTable.AddColumn("Match");

        var matches = 0;
        for (var i = 0; i < count; i++)
        {
            var c1 = i < colors1.Count ? colors1[i] : (R: 0f, G: 0f, B: 0f, A: 0f);
            var c2 = i < colors2.Count ? colors2[i] : (R: 0f, G: 0f, B: 0f, A: 0f);

            // Check if close enough (allowing for float precision)
            var match = Math.Abs(c1.R - c2.R) < 0.01f &&
                        Math.Abs(c1.G - c2.G) < 0.01f &&
                        Math.Abs(c1.B - c2.B) < 0.01f &&
                        Math.Abs(c1.A - c2.A) < 0.01f;

            if (match) matches++;

            colorTable.AddRow(
                i.ToString(),
                $"({c1.R,6:F3}, {c1.G,6:F3}, {c1.B,6:F3}, {c1.A,6:F3})",
                $"({c2.R,6:F3}, {c2.G,6:F3}, {c2.B,6:F3}, {c2.A,6:F3})",
                match ? "[green]✓[/]" : "[red]✗[/]");
        }

        AnsiConsole.Write(colorTable);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Match rate:[/] {matches}/{count} ({100.0 * matches / count:F1}%)");

        // Show raw bytes for first vertex color in each file
        if (geom1.HasVertexColors != 0 && geom1.FieldOffsets.TryGetValue("VertexColors", out var colorOffset1))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold]Raw Color Bytes (File 1, offset 0x{colorOffset1:X})[/]")
                .LeftJustified());
            HexDump(data1, offset1 + colorOffset1, Math.Min(64, geom1.NumVertices * 16));
        }

        if (geom2.HasVertexColors != 0 && geom2.FieldOffsets.TryGetValue("VertexColors", out var colorOffset2))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold]Raw Color Bytes (File 2, offset 0x{colorOffset2:X})[/]")
                .LeftJustified());
            HexDump(data2, offset2 + colorOffset2, Math.Min(64, geom2.NumVertices * 16));
        }
    }

    internal static List<(float R, float G, float B, float A)> ExtractVertexColors(ReadOnlySpan<byte> blockData,
        bool bigEndian, GeometryInfo geom, int count)
    {
        var result = new List<(float R, float G, float B, float A)>();
        if (geom.HasVertexColors == 0 || !geom.FieldOffsets.TryGetValue("VertexColors", out var colorOffset))
            return result;

        // NIF stores vertex colors as Color4 (4 floats: R, G, B, A)
        for (var i = 0; i < count; i++)
        {
            var pos = colorOffset + i * 16; // 4 floats * 4 bytes = 16 bytes per vertex
            var r = ReadFloat(blockData, pos, bigEndian);
            var g = ReadFloat(blockData, pos + 4, bigEndian);
            var b = ReadFloat(blockData, pos + 8, bigEndian);
            var a = ReadFloat(blockData, pos + 12, bigEndian);
            result.Add((r, g, b, a));
        }

        return result;
    }
}
