using System.CommandLine;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Spectre.Console;

namespace EgtAnalyzer.Commands;

internal static class InspectTriCommand
{
    private sealed class TriInspectionResult
    {
        public required string TriPath { get; init; }
        public required TriParser Tri { get; init; }
        public required int ByteLength { get; init; }
        public string? SiblingNifPath { get; init; }
        public TriNifGeometryInspection? SiblingNifInspection { get; init; }
    }

    internal static Command Create()
    {
        var command = new Command(
            "inspect-tri",
            "Inspect FRTRI003 TRI files and compare them to sibling NIF geometry");

        var inputArg = new Argument<string[]>("input")
        {
            Description = "TRI file(s) or directory/directories containing TRI files",
            Arity = ArgumentArity.OneOrMore
        };
        var nifOption = new Option<string?>("--nif")
        {
            Description = "Optional explicit sibling NIF path (single TRI input only)"
        };
        var reportOption = new Option<string?>("--report")
        {
            Description = "Optional CSV report output path"
        };
        var showStringsOption = new Option<bool>("--show-strings")
        {
            Description = "Print identifier-like tail strings"
        };
        var stringLimitOption = new Option<int>("--string-limit")
        {
            Description = "Maximum identifier-like strings to show per file",
            DefaultValueFactory = _ => 24
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(nifOption);
        command.Options.Add(reportOption);
        command.Options.Add(showStringsOption);
        command.Options.Add(stringLimitOption);

        command.SetAction((parseResult, _) =>
        {
            Run(
                parseResult.GetValue(inputArg)!,
                parseResult.GetValue(nifOption),
                parseResult.GetValue(reportOption),
                parseResult.GetValue(showStringsOption),
                parseResult.GetValue(stringLimitOption));
            return Task.CompletedTask;
        });

        return command;
    }

    private static void Run(
        IReadOnlyList<string> inputs,
        string? explicitNifPath,
        string? reportPath,
        bool showStrings,
        int stringLimit)
    {
        var triPaths = ResolveTriPaths(inputs);
        if (triPaths.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No TRI files found in the supplied input.");
            return;
        }

        if (explicitNifPath != null && triPaths.Count != 1)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --nif can only be used with a single TRI input.");
            return;
        }

        var results = new List<TriInspectionResult>(triPaths.Count);
        foreach (var triPath in triPaths)
        {
            var result = InspectTriFile(triPath, explicitNifPath);
            if (result != null)
            {
                results.Add(result);
            }
        }

        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No TRI files could be parsed.");
            return;
        }

        if (results.Count == 1)
        {
            PrintDetailedResult(results[0], showStrings, stringLimit);
        }
        else
        {
            PrintSummary(results);
            if (showStrings)
            {
                foreach (var result in results)
                {
                    PrintIdentifierStrings(result, stringLimit);
                }
            }
        }

        if (reportPath != null)
        {
            WriteCsvReport(results, reportPath);
            AnsiConsole.MarkupLine("Wrote TRI report to [cyan]{0}[/]", Path.GetFullPath(reportPath));
        }
    }

    private static List<string> ResolveTriPaths(IReadOnlyList<string> inputs)
    {
        var triPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            if (File.Exists(input))
            {
                if (string.Equals(Path.GetExtension(input), ".tri", StringComparison.OrdinalIgnoreCase))
                {
                    triPaths.Add(Path.GetFullPath(input));
                }

                continue;
            }

            if (Directory.Exists(input))
            {
                foreach (var path in Directory.EnumerateFiles(input, "*.tri", SearchOption.AllDirectories))
                {
                    triPaths.Add(Path.GetFullPath(path));
                }

                continue;
            }

            AnsiConsole.MarkupLine("[yellow]Warning:[/] Input not found: {0}", input);
        }

        return triPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static TriInspectionResult? InspectTriFile(string triPath, string? explicitNifPath)
    {
        var triData = File.ReadAllBytes(triPath);
        var tri = TriParser.Parse(triData);
        if (tri == null)
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] Skipping non-FRTRI003 file: {0}", triPath);
            return null;
        }

        var siblingNifPath = explicitNifPath;
        if (siblingNifPath == null)
        {
            var candidate = Path.ChangeExtension(triPath, ".nif");
            if (File.Exists(candidate))
            {
                siblingNifPath = candidate;
            }
        }

        TriNifGeometryInspection? siblingInspection = null;
        if (siblingNifPath != null && File.Exists(siblingNifPath))
        {
            siblingInspection = TriNifGeometryInspector.Inspect(File.ReadAllBytes(siblingNifPath), tri);
        }

        return new TriInspectionResult
        {
            TriPath = triPath,
            Tri = tri,
            ByteLength = triData.Length,
            SiblingNifPath = siblingNifPath != null && File.Exists(siblingNifPath) ? siblingNifPath : null,
            SiblingNifInspection = siblingInspection
        };
    }

    private static void PrintSummary(IReadOnlyList<TriInspectionResult> results)
    {
        var comparedCount = results.Count(static result => result.SiblingNifInspection != null);
        var exactMatchCount = results.Count(static result => result.SiblingNifInspection?.HasExactGeometryMatch == true);
        var declaredMatchCount = results.Count(static result =>
            result.SiblingNifInspection is { HasExactGeometryMatch: false, HasDeclaredTriangleCountMatch: true });
        var vertexOnlyMatchCount = results.Count(static result =>
            result.SiblingNifInspection is
                { HasExactGeometryMatch: false, HasDeclaredTriangleCountMatch: false, HasVertexCountMatch: true });

        AnsiConsole.MarkupLine(
            "Inspected [bold]{0}[/] TRI files. Render-exact matches: [bold]{1}[/]/{2}. Declared-topology matches: [bold]{3}[/]. Vertex-only matches: [bold]{4}[/].",
            results.Count,
            exactMatchCount,
            comparedCount,
            declaredMatchCount,
            vertexOnlyMatchCount);

        var table = new Table().Border(TableBorder.Square);
        table.AddColumn("File");
        table.AddColumn("Verts");
        table.AddColumn("TRI Tris");
        table.AddColumn("Decl");
        table.AddColumn("Rend");
        table.AddColumn("0x34");
        table.AddColumn("0x38");
        table.AddColumn("0x2C");
        table.AddColumn("IDs");
        table.AddColumn("Sibling NIF");

        foreach (var result in results)
        {
            table.AddRow(
                Path.GetFileName(result.TriPath),
                result.Tri.VertexCount.ToString(),
                result.Tri.TriangleCount.ToString(),
                FormatSummaryCount(result.SiblingNifInspection, static block => block.DeclaredTriangleCount),
                FormatSummaryCount(result.SiblingNifInspection, static block => block.TriangleCount),
                result.Tri.Record34CountHint.ToString(),
                result.Tri.Record38CountHint.ToString(),
                result.Tri.Record2CHint.ToString(),
                result.Tri.IdentifierLikeTailStrings.Count.ToString(),
                FormatSiblingMatch(result.SiblingNifInspection));
        }

        AnsiConsole.Write(table);
    }

    private static void PrintDetailedResult(TriInspectionResult result, bool showStrings, int stringLimit)
    {
        AnsiConsole.MarkupLine("[bold]{0}[/]", result.TriPath);
        AnsiConsole.MarkupLine("  Size: {0:N0} bytes", result.ByteLength);
        AnsiConsole.MarkupLine(
            "  Header: verts={0} tris={1} repeatedVerts={2}",
            result.Tri.VertexCount,
            result.Tri.TriangleCount,
            result.Tri.RepeatedVertexCountHint);
        AnsiConsole.MarkupLine(
            "  Hints: groups={0}  0x34={1}  0x38={2}  0x2C={3}",
            result.Tri.StructuredSectionGroupCountHint,
            result.Tri.Record34CountHint,
            result.Tri.Record38CountHint,
            result.Tri.Record2CHint);
        AnsiConsole.MarkupLine(
            "  Tail: {0:N0} bytes, {1} strings, {2} identifier-like",
            result.Tri.RemainingPayloadLength,
            result.Tri.TailStrings.Count,
            result.Tri.IdentifierLikeTailStrings.Count);

        if (result.SiblingNifPath != null)
        {
            AnsiConsole.MarkupLine("  Sibling NIF: [cyan]{0}[/]", result.SiblingNifPath);
        }
        else
        {
            AnsiConsole.MarkupLine("  Sibling NIF: [dim]not found[/]");
        }

        if (result.SiblingNifInspection != null)
        {
            AnsiConsole.MarkupLine(
                "  Geometry match: {0}",
                FormatSiblingMatch(result.SiblingNifInspection));

            var table = new Table().Border(TableBorder.Square);
            table.AddColumn("Block");
            table.AddColumn("Type");
            table.AddColumn("Verts");
            table.AddColumn("Decl");
            table.AddColumn("Rend");
            table.AddColumn("Degens");
            table.AddColumn("Match");

            foreach (var block in result.SiblingNifInspection.GeometryBlocks)
            {
                var isExactMatch =
                    block.VertexCount == result.Tri.VertexCount &&
                    block.TriangleCount == result.Tri.TriangleCount;
                var isDeclaredMatch =
                    block.VertexCount == result.Tri.VertexCount &&
                    block.DeclaredTriangleCount == result.Tri.TriangleCount;
                table.AddRow(
                    $"#{block.BlockIndex}",
                    block.BlockType,
                    block.VertexCount.ToString(),
                    block.DeclaredTriangleCount.ToString(),
                    block.TriangleCount.ToString(),
                    block.DegenerateTriangleCount.ToString(),
                    isExactMatch
                        ? "[green]render[/]"
                        : isDeclaredMatch
                            ? "[yellow]declared[/]"
                            : "[dim]no[/]");
            }

            AnsiConsole.Write(table);
        }

        if (showStrings)
        {
            PrintIdentifierStrings(result, stringLimit);
        }
    }

    private static void PrintIdentifierStrings(TriInspectionResult result, int stringLimit)
    {
        var strings = result.Tri.IdentifierLikeTailStrings
            .Take(Math.Max(stringLimit, 0))
            .Select(static info => info.Value)
            .ToArray();

        if (strings.Length == 0)
        {
            AnsiConsole.MarkupLine("  Identifier strings: [dim]none[/]");
            return;
        }

        AnsiConsole.MarkupLine(
            "  Identifier strings ({0}/{1}): {2}",
            strings.Length,
            result.Tri.IdentifierLikeTailStrings.Count,
            string.Join(", ", strings));
    }

    private static string FormatSiblingMatch(TriNifGeometryInspection? inspection)
    {
        if (inspection == null)
        {
            return "[dim]missing[/]";
        }

        return inspection.HasExactGeometryMatch
            ? $"[green]exact {inspection.ExactMatchingGeometryBlockCount}/{inspection.GeometryBlockCount}[/]"
            : inspection.HasDeclaredTriangleCountMatch
                ? $"[yellow]declared {inspection.DeclaredTriangleMatchingGeometryBlockCount}/{inspection.GeometryBlockCount}[/]"
            : inspection.HasVertexCountMatch
                ? $"[yellow]verts {inspection.VertexMatchingGeometryBlockCount}/{inspection.GeometryBlockCount}[/]"
                : $"[yellow]no match ({inspection.GeometryBlockCount} blocks)[/]";
    }

    private static string FormatSummaryCount(
        TriNifGeometryInspection? inspection,
        Func<NifGeometryBlockSummary, int> selector)
    {
        if (inspection == null || inspection.GeometryBlocks.Count == 0)
        {
            return "-";
        }

        return string.Join("/",
            inspection.GeometryBlocks.Select(selector));
    }

    private static void WriteCsvReport(IReadOnlyList<TriInspectionResult> results, string reportPath)
    {
        var fullPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var sb = new StringBuilder();
        sb.AppendLine(
            "tri_path,file_name,byte_length,vertex_count,triangle_count,repeated_vertex_count_hint,structured_group_count_hint,record34_count_hint,record38_count_hint,record2c_count_hint,tail_bytes,tail_strings,identifier_like_tail_strings,sibling_nif_path,sibling_nif_block_count,sibling_nif_exact_match_count,sibling_nif_declared_triangle_match_count,sibling_nif_vertex_match_count,sibling_nif_has_exact_match,sibling_nif_has_declared_triangle_match,sibling_nif_declared_triangle_counts,sibling_nif_render_triangle_counts,sibling_nif_degenerate_triangle_counts,identifier_preview");

        foreach (var result in results)
        {
            var inspection = result.SiblingNifInspection;
            var identifierPreview = string.Join("|",
                result.Tri.IdentifierLikeTailStrings.Take(8).Select(static info => info.Value));

            sb.AppendLine(string.Join(",",
                CsvEscape(result.TriPath),
                CsvEscape(Path.GetFileName(result.TriPath)),
                result.ByteLength,
                result.Tri.VertexCount,
                result.Tri.TriangleCount,
                result.Tri.RepeatedVertexCountHint,
                result.Tri.StructuredSectionGroupCountHint,
                result.Tri.Record34CountHint,
                result.Tri.Record38CountHint,
                result.Tri.Record2CHint,
                result.Tri.RemainingPayloadLength,
                result.Tri.TailStrings.Count,
                result.Tri.IdentifierLikeTailStrings.Count,
                CsvEscape(result.SiblingNifPath ?? string.Empty),
                inspection?.GeometryBlockCount ?? 0,
                inspection?.ExactMatchingGeometryBlockCount ?? 0,
                inspection?.DeclaredTriangleMatchingGeometryBlockCount ?? 0,
                inspection?.VertexMatchingGeometryBlockCount ?? 0,
                inspection?.HasExactGeometryMatch == true ? "true" : "false",
                inspection?.HasDeclaredTriangleCountMatch == true ? "true" : "false",
                CsvEscape(string.Join("|", inspection?.GeometryBlocks.Select(static block => block.DeclaredTriangleCount) ?? [])),
                CsvEscape(string.Join("|", inspection?.GeometryBlocks.Select(static block => block.TriangleCount) ?? [])),
                CsvEscape(string.Join("|", inspection?.GeometryBlocks.Select(static block => block.DegenerateTriangleCount) ?? [])),
                CsvEscape(identifierPreview)));
        }

        File.WriteAllText(fullPath, sb.ToString());
    }

    private static string CsvEscape(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
