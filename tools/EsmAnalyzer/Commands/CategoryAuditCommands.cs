using System.CommandLine;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Audits placed objects categorized as "Unknown" by the world map viewer,
///     determines correct categories, and outputs actionable fix suggestions.
/// </summary>
public static class CategoryAuditCommands
{
    /// <summary>
    ///     Proposed category for record types that ObjectBoundsIndex does not currently handle.
    /// </summary>
    private static readonly Dictionary<string, PlacedObjectCategory> ProposedRecordTypeCategories = new()
    {
        ["EXPL"] = PlacedObjectCategory.Effects,
        ["PROJ"] = PlacedObjectCategory.Effects,
        ["ARMA"] = PlacedObjectCategory.Item,
        ["WATR"] = PlacedObjectCategory.Landscape,
        ["NAVM"] = PlacedObjectCategory.Effects,
        ["NAVI"] = PlacedObjectCategory.Effects,
        ["LGTM"] = PlacedObjectCategory.Effects,
        ["IMGS"] = PlacedObjectCategory.Effects,
        ["WTHR"] = PlacedObjectCategory.Effects,
        ["CLMT"] = PlacedObjectCategory.Effects,
        ["REGN"] = PlacedObjectCategory.Landscape,
        ["BPTD"] = PlacedObjectCategory.Npc,
        ["IDLE"] = PlacedObjectCategory.Effects,
        ["FLST"] = PlacedObjectCategory.Effects,
        ["AVIF"] = PlacedObjectCategory.Effects,
        ["CSTY"] = PlacedObjectCategory.Effects
    };

    /// <summary>
    ///     Record types that have dedicated <see cref="RecordCollection" /> lists
    ///     (i.e., are reconstructed) but ObjectBoundsIndex does not iterate them.
    /// </summary>
    private static readonly HashSet<string> ReconstructedTypes =
    [
        "EXPL", "PROJ", "ARMA", "WATR", "NAVM", "LGTM", "WTHR", "BPTD", "AVIF", "CSTY", "FLST"
    ];

    public static Command CreateCategoryAuditCommand()
    {
        var command = new Command("category-audit",
            "Audit Unknown map categories and suggest ObjectBoundsIndex fixes");

        var fileArg = new Argument<string>("file") { Description = "Path to ESM or DMP file" };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum entries to show per table",
            DefaultValueFactory = _ => 50
        };
        var formatOption = new Option<string>("--format")
        {
            Description = "Output format: table, csv, or code",
            DefaultValueFactory = _ => "table"
        };

        command.Arguments.Add(fileArg);
        command.Options.Add(limitOption);
        command.Options.Add(formatOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var limit = parseResult.GetValue(limitOption);
            var format = parseResult.GetValue(formatOption)!;
            await RunAuditAsync(file, limit, format, cancellationToken);
        });

        return command;
    }

    private static async Task RunAuditAsync(
        string filePath, int limit, string format, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {filePath}");
            return;
        }

        // Phase 1: Analyze ESM
        AnsiConsole.MarkupLine($"[blue]Analyzing:[/] {Path.GetFileName(filePath)}");
        var result = await EsmFileAnalyzer.AnalyzeAsync(filePath, cancellationToken: cancellationToken);

        if (result.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to parse ESM records");
            return;
        }

        // Phase 2: Semantic reconstruction
        RecordCollection records;
        AnsiConsole.MarkupLine("[blue]Reconstructing records...[/]");
        using (var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            var parser = new RecordParser(result.EsmRecords, result.FormIdMap, accessor, result.FileSize);
            records = parser.ParseAll();
        }

        // Phase 3: Build category index using the authoritative logic
        var (_, categoryIndex) = ObjectBoundsIndex.BuildCombined(records);
        AnsiConsole.MarkupLine($"[green]Categorized {categoryIndex.Count:N0} base object FormIDs[/]");

        // Phase 3b: Build raw FormID → record type from scan data (covers ALL records)
        var rawFormIdToType = result.EsmRecords.MainRecords
            .GroupBy(r => r.FormId)
            .Where(g => g.Key != 0)
            .ToDictionary(g => g.Key, g => g.First().RecordType);
        AnsiConsole.MarkupLine($"[green]Indexed {rawFormIdToType.Count:N0} raw FormIDs from scan[/]");

        // Phase 4: Collect all placed references
        var allRefs = new List<PlacedReference>();
        foreach (var cell in records.Cells)
        {
            allRefs.AddRange(cell.PlacedObjects);
        }

        foreach (var ws in records.Worldspaces)
        {
            foreach (var cell in ws.Cells)
            {
                allRefs.AddRange(cell.PlacedObjects);
            }
        }

        AnsiConsole.MarkupLine($"[green]Found {allRefs.Count:N0} placed references[/]");

        // Phase 5: Identify unknowns and diagnose root causes
        var categoryCounts = new Dictionary<PlacedObjectCategory, int>();
        var unknowns = new List<UnknownEntry>();

        foreach (var refr in allRefs)
        {
            var category = GetObjectCategory(refr, categoryIndex);

            categoryCounts.TryGetValue(category, out var count);
            categoryCounts[category] = count + 1;

            if (category == PlacedObjectCategory.Unknown)
            {
                var baseType = rawFormIdToType.GetValueOrDefault(refr.BaseFormId, "???");
                var editorId = records.FormIdToEditorId.GetValueOrDefault(refr.BaseFormId);

                unknowns.Add(new UnknownEntry
                {
                    BaseFormId = refr.BaseFormId,
                    RecordType = baseType,
                    EditorId = editorId,
                    ModelPath = refr.ModelPath,
                    RootCause = ClassifyRootCause(baseType, refr.BaseFormId, refr.ModelPath, records),
                    ProposedCategory = ProposeCategory(baseType, refr.ModelPath)
                });
            }
        }

        AnsiConsole.WriteLine();

        // Phase 6: Output results
        switch (format.ToLowerInvariant())
        {
            case "csv":
                OutputCsv(unknowns);
                break;
            case "code":
                OutputCode(unknowns, limit);
                break;
            default:
                OutputTables(unknowns, categoryCounts, allRefs.Count, limit);
                break;
        }
    }

    private static PlacedObjectCategory GetObjectCategory(
        PlacedReference refr,
        Dictionary<uint, PlacedObjectCategory> categoryIndex)
    {
        if (refr.IsMapMarker)
        {
            return PlacedObjectCategory.MapMarker;
        }

        return refr.RecordType switch
        {
            "ACHR" => PlacedObjectCategory.Npc,
            "ACRE" => PlacedObjectCategory.Creature,
            _ => categoryIndex.GetValueOrDefault(refr.BaseFormId, PlacedObjectCategory.Unknown)
        };
    }

    private static RootCause ClassifyRootCause(
        string recordType, uint formId, string? modelPath, RecordCollection records)
    {
        if (recordType == "???")
        {
            return formId < 0x100 ? RootCause.EngineFormId : RootCause.Uncategorizable;
        }

        if (ReconstructedTypes.Contains(recordType))
        {
            return RootCause.MissingRecordTypeHandler;
        }

        // Check if STAT/MSTT with unrecognized model folder
        if (recordType is "STAT" or "MSTT" && modelPath != null)
        {
            return RootCause.UnrecognizedModelFolder;
        }

        // Check if the type is in the generic records list (reconstructed as GenericEsmRecord)
        if (records.GenericRecords.Exists(g => g.RecordType == recordType))
        {
            return RootCause.MissingRecordTypeHandler;
        }

        return RootCause.UnreconstructedType;
    }

    private static PlacedObjectCategory? ProposeCategory(string recordType, string? modelPath)
    {
        // Try record type mapping first
        if (ProposedRecordTypeCategories.TryGetValue(recordType, out var proposed))
        {
            return proposed;
        }

        // For STAT/MSTT with model paths, try the folder heuristic
        if (recordType is "STAT" or "MSTT" && modelPath != null)
        {
            var folderCategory = ObjectBoundsIndex.GetStaticCategoryFromModelPath(modelPath);
            if (folderCategory.HasValue)
            {
                return folderCategory.Value;
            }
        }

        return null;
    }

    #region Output: Tables

    private static void OutputTables(
        List<UnknownEntry> unknowns,
        Dictionary<PlacedObjectCategory, int> categoryCounts,
        int totalRefs,
        int limit)
    {
        // Category distribution
        var catTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Category Distribution[/]")
            .AddColumn(new TableColumn("[bold]Category[/]"))
            .AddColumn(new TableColumn("[bold]Count[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]%[/]").RightAligned());

        foreach (var (cat, cnt) in categoryCounts.OrderByDescending(kv => kv.Value))
        {
            var pct = 100.0 * cnt / totalRefs;
            var color = cat == PlacedObjectCategory.Unknown ? "red" : "cyan";
            _ = catTable.AddRow($"[{color}]{cat}[/]", cnt.ToString("N0"), $"{pct:F1}%");
        }

        AnsiConsole.Write(catTable);
        AnsiConsole.WriteLine();

        if (unknowns.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No Unknown references found![/]");
            return;
        }

        // Group by root cause
        var byRootCause = unknowns
            .GroupBy(u => u.RootCause)
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var group in byRootCause)
        {
            var title = group.Key switch
            {
                RootCause.MissingRecordTypeHandler => "[bold yellow]Missing Record Type Handlers[/]",
                RootCause.UnrecognizedModelFolder => "[bold yellow]Unrecognized Model Path Folders[/]",
                RootCause.UnreconstructedType => "[bold red]Unreconstructed Record Types[/]",
                RootCause.EngineFormId => "[bold blue]Engine FormIDs[/]",
                _ => "[bold grey]Uncategorizable[/]"
            };

            // Sub-group by record type
            var byType = group
                .GroupBy(u => u.RecordType)
                .OrderByDescending(g => g.Count())
                .Take(limit)
                .ToList();

            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title(title)
                .AddColumn(new TableColumn("[bold]Record Type[/]"))
                .AddColumn(new TableColumn("[bold]Count[/]").RightAligned())
                .AddColumn(new TableColumn("[bold]Proposed[/]"))
                .AddColumn(new TableColumn("[bold]Samples[/]"));

            foreach (var typeGroup in byType)
            {
                var proposed = typeGroup.First().ProposedCategory?.ToString() ?? "[grey]?[/]";
                var samples = typeGroup
                    .DistinctBy(u => u.BaseFormId)
                    .Take(3)
                    .Select(u =>
                    {
                        var label = $"0x{u.BaseFormId:X8}";
                        if (u.EditorId != null)
                        {
                            label += $" ({u.EditorId})";
                        }

                        return label;
                    });

                _ = table.AddRow(
                    $"[yellow]{Markup.Escape(typeGroup.Key)}[/]",
                    typeGroup.Count().ToString("N0"),
                    proposed,
                    Markup.Escape(string.Join(", ", samples)));
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }

        // Unrecognized model folder detail
        var folderUnknowns = unknowns
            .Where(u => u.RootCause == RootCause.UnrecognizedModelFolder && u.ModelPath != null)
            .GroupBy(u => ExtractTopFolder(u.ModelPath!) ?? "(no folder)")
            .OrderByDescending(g => g.Count())
            .Take(limit)
            .ToList();

        if (folderUnknowns.Count > 0)
        {
            var folderTable = new Table()
                .Border(TableBorder.Rounded)
                .Title("[bold yellow]Unrecognized Folders Detail[/]")
                .AddColumn(new TableColumn("[bold]Folder[/]"))
                .AddColumn(new TableColumn("[bold]Count[/]").RightAligned())
                .AddColumn(new TableColumn("[bold]Sample Path[/]"));

            foreach (var g in folderUnknowns)
            {
                var sample = g.First().ModelPath ?? "";
                _ = folderTable.AddRow(
                    $"[yellow]{Markup.Escape(g.Key)}[/]",
                    g.Count().ToString("N0"),
                    Markup.Escape(sample.Length > 60 ? sample[..57] + "..." : sample));
            }

            AnsiConsole.Write(folderTable);
            AnsiConsole.WriteLine();
        }

        // Summary
        var fixableCount = unknowns.Count(u => u.ProposedCategory.HasValue);
        var totalUnknown = unknowns.Count;
        var unknownPct = totalRefs > 0 ? 100.0 * totalUnknown / totalRefs : 0;

        AnsiConsole.MarkupLine("[bold underline]Summary[/]");
        AnsiConsole.MarkupLine($"  Total placed references: {totalRefs:N0}");
        AnsiConsole.MarkupLine($"  Unknown: [red]{totalUnknown:N0}[/] ({unknownPct:F1}%)");
        AnsiConsole.MarkupLine($"  Fixable (proposed category): [green]{fixableCount:N0}[/]");
        AnsiConsole.MarkupLine($"  Remaining unfixable: [grey]{totalUnknown - fixableCount:N0}[/]");
    }

    #endregion

    #region Output: CSV

    private static void OutputCsv(List<UnknownEntry> unknowns)
    {
        Console.WriteLine("BaseFormId,RecordType,EditorId,ModelPath,ProposedCategory,RootCause");
        foreach (var u in unknowns.DistinctBy(u => u.BaseFormId).OrderBy(u => u.RecordType).ThenBy(u => u.BaseFormId))
        {
            var proposed = u.ProposedCategory?.ToString() ?? "";
            var editorId = u.EditorId ?? "";
            var modelPath = u.ModelPath ?? "";
            Console.WriteLine(
                $"0x{u.BaseFormId:X8},{u.RecordType},{CsvEscape(editorId)},{CsvEscape(modelPath)},{proposed},{u.RootCause}");
        }
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    #endregion

    #region Output: Code

    private static void OutputCode(List<UnknownEntry> unknowns, int limit)
    {
        var sb = new StringBuilder();

        // Group fixable unknowns by record type
        var fixableByType = unknowns
            .Where(u => u.ProposedCategory.HasValue && u.RootCause == RootCause.MissingRecordTypeHandler)
            .DistinctBy(u => u.RecordType)
            .OrderBy(u => u.RecordType)
            .Take(limit)
            .ToList();

        if (fixableByType.Count > 0)
        {
            sb.AppendLine("// === Paste into ObjectBoundsIndex.BuildCombined() ===");
            sb.AppendLine("// Add these blocks to handle record types currently producing Unknown:");
            sb.AppendLine();

            foreach (var entry in fixableByType)
            {
                var collectionName = GetCollectionName(entry.RecordType);
                var category = entry.ProposedCategory!.Value;

                if (collectionName != null)
                {
                    sb.AppendLine($"// {entry.RecordType} → {category}");
                    sb.AppendLine($"foreach (var r in records.{collectionName})");
                    sb.AppendLine("{");
                    sb.AppendLine("    if (r.FormId != 0)");
                    sb.AppendLine("    {");
                    sb.AppendLine($"        categories.TryAdd(r.FormId, PlacedObjectCategory.{category});");
                    sb.AppendLine("    }");
                    sb.AppendLine("}");
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine($"// {entry.RecordType} → {category} (add to generic records switch)");
                    sb.AppendLine($"// \"{entry.RecordType}\" => PlacedObjectCategory.{category},");
                    sb.AppendLine();
                }
            }
        }

        // Unrecognized model path folders
        var folderEntries = unknowns
            .Where(u => u.RootCause == RootCause.UnrecognizedModelFolder && u.ModelPath != null)
            .Select(u => ExtractTopFolder(u.ModelPath!))
            .Where(f => f != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        if (folderEntries.Count > 0)
        {
            sb.AppendLine("// === Paste into ObjectBoundsIndex.GetStaticCategoryFromModelPath() ===");
            sb.AppendLine("// Add these folder mappings to reduce Unknown STAT/MSTT objects:");
            sb.AppendLine();

            foreach (var folder in folderEntries)
            {
                sb.AppendLine($"if (folder.Equals(\"{folder}\", StringComparison.OrdinalIgnoreCase))");
                sb.AppendLine("{");
                sb.AppendLine($"    return PlacedObjectCategory.Static; // TODO: assign correct category for '{folder}'");
                sb.AppendLine("}");
                sb.AppendLine();
            }
        }

        // Engine FormIDs
        var engineFormIds = unknowns
            .Where(u => u.RootCause == RootCause.EngineFormId)
            .DistinctBy(u => u.BaseFormId)
            .OrderBy(u => u.BaseFormId)
            .Take(limit)
            .ToList();

        if (engineFormIds.Count > 0)
        {
            sb.AppendLine("// === Paste into ObjectBoundsIndex.BuildCombined() (engine FormIDs section) ===");
            sb.AppendLine();

            foreach (var entry in engineFormIds)
            {
                var cat = entry.ProposedCategory?.ToString() ?? "Effects";
                sb.AppendLine(
                    $"categories.TryAdd(0x{entry.BaseFormId:X8}, PlacedObjectCategory.{cat}); // {entry.EditorId ?? "engine marker"}");
            }

            sb.AppendLine();
        }

        if (sb.Length == 0)
        {
            sb.AppendLine("// No actionable fixes found — all unknowns are unreconstructed types.");
        }

        Console.Write(sb);
    }

    /// <summary>
    ///     Maps record type signatures to their RecordCollection property names.
    /// </summary>
    private static string? GetCollectionName(string recordType)
    {
        return recordType switch
        {
            "EXPL" => "Explosions",
            "PROJ" => "Projectiles",
            "ARMA" => "ArmorAddons",
            "WATR" => "Water",
            "NAVM" => "NavMeshes",
            "LGTM" => "LightingTemplates",
            "WTHR" => "Weather",
            "BPTD" => "BodyPartData",
            "AVIF" => "ActorValueInfos",
            "CSTY" => "CombatStyles",
            "FLST" => "FormLists",
            _ => null
        };
    }

    #endregion

    #region Helpers

    /// <summary>
    ///     Extracts the top-level folder from a model path, stripping meshes\ and DLC prefixes.
    /// </summary>
    private static string? ExtractTopFolder(string modelPath)
    {
        var path = modelPath.AsSpan();

        if (path.Length > 7 &&
            (path[..7].Equals("meshes\\", StringComparison.OrdinalIgnoreCase) ||
             path[..7].Equals("meshes/", StringComparison.OrdinalIgnoreCase)))
        {
            path = path[7..];
        }

        // Strip DLC directory prefix (numeric: DLC01\, DLC02\, etc.)
        if (path.Length > 6 &&
            path[..3].Equals("dlc", StringComparison.OrdinalIgnoreCase) &&
            path[3] >= '0' && path[3] <= '9' &&
            path[4] >= '0' && path[4] <= '9' &&
            (path[5] == '\\' || path[5] == '/'))
        {
            path = path[6..];
        }

        // Strip named DLC folder prefixes (dlcanch\, DLCPitt\, etc.)
        if (path.Length >= 5 && path[..3].Equals("dlc", StringComparison.OrdinalIgnoreCase))
        {
            var dlcSep = path.IndexOfAny('\\', '/');
            if (dlcSep > 3)
            {
                path = path[(dlcSep + 1)..];
            }
        }

        var sepIndex = path.IndexOfAny('\\', '/');
        return sepIndex <= 0 ? null : path[..sepIndex].ToString();
    }

    #endregion

    private sealed class UnknownEntry
    {
        public required uint BaseFormId { get; init; }
        public required string RecordType { get; init; }
        public string? EditorId { get; init; }
        public string? ModelPath { get; init; }
        public required RootCause RootCause { get; init; }
        public PlacedObjectCategory? ProposedCategory { get; init; }
    }

    private enum RootCause
    {
        MissingRecordTypeHandler,
        UnrecognizedModelFolder,
        UnreconstructedType,
        EngineFormId,
        Uncategorizable
    }
}
