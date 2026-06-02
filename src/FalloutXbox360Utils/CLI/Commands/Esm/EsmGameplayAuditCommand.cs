using System.CommandLine;
using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Esm;

public static class EsmGameplayAuditCommand
{
    private static readonly HashSet<string> LoadedPlacementBaseTypes = new(StringComparer.Ordinal)
    {
        "STAT", "SCOL", "MSTT", "TREE", "FLOR"
    };

    public static Command CreateGameplayAuditCommand()
    {
        var command = new Command("audit-gameplay", "Compare generated ESP gameplay-sensitive world/NPC data");
        var generatedArg = new Argument<string>("generated-esp") { Description = "Generated ESP to audit" };
        var sourceDmpOpt = new Option<string>("--source-dmp")
        {
            Description = "Source DMP used to build the ESP"
        };
        var pcEsmOpt = new Option<string>("--pc-esm")
        {
            Description = "PC master ESM used as conversion baseline"
        };
        var outputOpt = new Option<string>("--output")
        {
            Description = "Output directory",
            DefaultValueFactory = _ => "gameplay_audit"
        };
        var actorOpt = new Option<string[]>("--actor")
        {
            Description = "Actor label/FormID to include in appearance audit",
            AllowMultipleArgumentsPerToken = false
        };

        command.Arguments.Add(generatedArg);
        command.Options.Add(sourceDmpOpt);
        command.Options.Add(pcEsmOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(actorOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var generated = parseResult.GetValue(generatedArg)!;
            var sourceDmp = parseResult.GetValue(sourceDmpOpt)!;
            var pcEsm = parseResult.GetValue(pcEsmOpt)!;
            var output = parseResult.GetValue(outputOpt)!;
            var actors = parseResult.GetValue(actorOpt) ?? ["Ulysses"];
            return await RunAsync(generated, sourceDmp, pcEsm, output, actors, cancellationToken);
        });

        return command;
    }

    private static async Task<int> RunAsync(
        string generatedPath,
        string sourceDmpPath,
        string pcEsmPath,
        string outputDirectory,
        IReadOnlyList<string> actors,
        CancellationToken cancellationToken)
    {
        foreach (var path in new[] { generatedPath, sourceDmpPath, pcEsmPath })
        {
            if (!File.Exists(path))
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {Markup.Escape(path)}");
                return 1;
            }
        }

        Directory.CreateDirectory(outputDirectory);
        AnsiConsole.MarkupLine($"[blue]Loading generated ESP:[/] {Markup.Escape(Path.GetFileName(generatedPath))}");
        using var generated = await SemanticFileLoader.LoadAsync(generatedPath, cancellationToken: cancellationToken);
        AnsiConsole.MarkupLine($"[blue]Loading source DMP:[/] {Markup.Escape(Path.GetFileName(sourceDmpPath))}");
        using var source = await SemanticFileLoader.LoadAsync(sourceDmpPath, cancellationToken: cancellationToken);
        AnsiConsole.MarkupLine($"[blue]Loading master ESM:[/] {Markup.Escape(Path.GetFileName(pcEsmPath))}");
        using var master = await SemanticFileLoader.LoadAsync(pcEsmPath, cancellationToken: cancellationToken);

        var rawGenerated = EsmParser.EnumerateRecordsWithGrups(await File.ReadAllBytesAsync(generatedPath, cancellationToken)).Records;
        WriteCellMergeAudit(Path.Combine(outputDirectory, "cell_merge_audit.csv"),
            generated.Records, source.Records, master.Records);
        WriteLandAudit(Path.Combine(outputDirectory, "land_audit.csv"),
            generated.Records, source.Records, master.Records, rawGenerated);
        WriteMapMarkerAudit(Path.Combine(outputDirectory, "map_marker_audit.csv"),
            generated.Records, source.Records, master.Records);
        WriteAppearanceAudit(Path.Combine(outputDirectory, "appearance_audit.csv"),
            generated.Records, source.Records, master.Records, actors);

        AnsiConsole.MarkupLine($"[green]Wrote gameplay audit:[/] {Markup.Escape(outputDirectory)}");
        return 0;
    }

    private static void WriteCellMergeAudit(
        string path,
        RecordCollection generated,
        RecordCollection source,
        RecordCollection master)
    {
        var masterRefIds = master.Cells.SelectMany(c => c.PlacedObjects).Select(p => p.FormId).ToHashSet();
        var masterCells = ToFirstByFormId(master.Cells, c => c.FormId);
        var generatedCells = ToFirstByFormId(generated.Cells, c => c.FormId);
        var sourceBaseTypes = BuildBaseTypeIndex(source, master);

        var sb = new StringBuilder();
        sb.AppendLine("cell_form_id,grid,worldspace,selected_mode,loaded_evidence_count,dmp_refs,master_refs,generated_refs,preserved_master_refs,dropped_or_deleted_master_refs,preserved_master_temp_refs");
        foreach (var sourceCell in source.Cells.Where(c => !c.IsInterior).OrderBy(c => c.FormId))
        {
            if (!masterCells.TryGetValue(sourceCell.FormId, out var masterCell))
            {
                continue;
            }

            generatedCells.TryGetValue(sourceCell.FormId, out var generatedCell);
            var loadedEvidence = sourceCell.PlacedObjects.Count(p =>
                IsLoadedPlacementEvidence(p, sourceBaseTypes));
            var mode = CellMerger.Classify(sourceCell, masterRefIds);

            var sourceIds = sourceCell.PlacedObjects.Select(p => p.FormId).ToHashSet();
            var generatedIds = generatedCell?.PlacedObjects.Select(p => p.FormId).ToHashSet() ?? [];
            var masterIds = masterCell.PlacedObjects.Select(p => p.FormId).ToHashSet();
            var preservedMaster = masterIds.Intersect(generatedIds).Except(sourceIds).Count();
            var droppedMaster = masterIds.Except(generatedIds).Except(sourceIds).Count();
            var preservedMasterTemps = masterCell.PlacedObjects.Count(p =>
                !p.IsPersistent && generatedIds.Contains(p.FormId) && !sourceIds.Contains(p.FormId));

            sb.AppendLine(string.Join(',',
                Csv($"0x{sourceCell.FormId:X8}"),
                Csv(FormatGrid(sourceCell)),
                Csv(FormatFormId(sourceCell.WorldspaceFormId)),
                Csv(mode.ToString()),
                loadedEvidence,
                sourceCell.PlacedObjects.Count,
                masterCell.PlacedObjects.Count,
                generatedCell?.PlacedObjects.Count ?? 0,
                preservedMaster,
                droppedMaster,
                preservedMasterTemps));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteLandAudit(
        string path,
        RecordCollection generated,
        RecordCollection source,
        RecordCollection master,
        IReadOnlyList<ParsedMainRecord> rawGenerated)
    {
        var sourceCells = ToFirstByFormId(source.Cells, c => c.FormId);
        var masterCells = ToFirstByFormId(master.Cells, c => c.FormId);
        var rawLandRangesByCell = BuildRawLandRangesByCell(rawGenerated);

        var sb = new StringBuilder();
        sb.AppendLine("cell_form_id,grid,land_source,source_range,generated_range,master_range,generated_has_water,generated_xclw,raw_land_form_id,raw_vhgt_length,raw_min,raw_max,raw_range");
        foreach (var generatedCell in generated.Cells.Where(c => !c.IsInterior).OrderBy(c => c.FormId))
        {
            sourceCells.TryGetValue(generatedCell.FormId, out var sourceCell);
            masterCells.TryGetValue(generatedCell.FormId, out var masterCell);
            var sourceRange = CalculateRange(sourceCell?.Heightmap);
            var generatedRange = CalculateRange(generatedCell.Heightmap);
            var masterRange = CalculateRange(masterCell?.Heightmap);
            string landSource;
            if (generatedCell.Heightmap is null)
            {
                landSource = "none";
            }
            else if (sourceCell?.Heightmap is not null)
            {
                landSource = "source";
            }
            else if (masterCell?.Heightmap is not null)
            {
                landSource = "master";
            }
            else
            {
                landSource = "generated";
            }

            rawLandRangesByCell.TryGetValue(generatedCell.FormId, out var raw);
            sb.AppendLine(string.Join(',',
                Csv($"0x{generatedCell.FormId:X8}"),
                Csv(FormatGrid(generatedCell)),
                Csv(landSource),
                Csv(FormatRange(sourceRange)),
                Csv(FormatRange(generatedRange)),
                Csv(FormatRange(masterRange)),
                generatedCell.HasWater,
                Csv(generatedCell.WaterHeight?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                Csv(raw.LandFormId == 0 ? string.Empty : $"0x{raw.LandFormId:X8}"),
                raw.VhgtLength,
                Csv(FormatFloat(raw.Min)),
                Csv(FormatFloat(raw.Max)),
                Csv(FormatFloat(raw.Range))));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteMapMarkerAudit(
        string path,
        RecordCollection generated,
        RecordCollection source,
        RecordCollection master)
    {
        var generatedById = ToFirstByFormId(generated.MapMarkers, m => m.FormId);
        var masterById = ToFirstByFormId(master.MapMarkers, m => m.FormId);
        var sb = new StringBuilder();
        sb.AppendLine("classification,form_id,generated_form_id,match_strategy,source_name,generated_name,master_name,source_type,generated_type,master_type,source_pos,generated_pos,master_pos");

        foreach (var sourceMarker in source.MapMarkers.OrderBy(m => m.FormId))
        {
            var matchStrategy = "form-id";
            if (!generatedById.TryGetValue(sourceMarker.FormId, out var generatedMarker))
            {
                generatedMarker = FindGeneratedMapMarkerFallback(sourceMarker, generated.MapMarkers);
                matchStrategy = generatedMarker is null ? string.Empty : "name-type-nearby-position";
            }

            masterById.TryGetValue(sourceMarker.FormId, out var masterMarker);
            var classification = ClassifyMapMarker(sourceMarker, generatedMarker, masterMarker);
            sb.AppendLine(string.Join(',',
                Csv(classification),
                Csv($"0x{sourceMarker.FormId:X8}"),
                Csv(generatedMarker is null ? string.Empty : $"0x{generatedMarker.FormId:X8}"),
                Csv(matchStrategy),
                Csv(sourceMarker.MarkerName),
                Csv(generatedMarker?.MarkerName),
                Csv(masterMarker?.MarkerName),
                Csv(sourceMarker.MarkerType?.ToString()),
                Csv(generatedMarker?.MarkerType?.ToString()),
                Csv(masterMarker?.MarkerType?.ToString()),
                Csv(FormatPosition(sourceMarker)),
                Csv(generatedMarker is null ? string.Empty : FormatPosition(generatedMarker)),
                Csv(masterMarker is null ? string.Empty : FormatPosition(masterMarker))));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static PlacedReference? FindGeneratedMapMarkerFallback(
        PlacedReference sourceMarker,
        IEnumerable<PlacedReference> generatedMarkers)
    {
        return generatedMarkers
            .Where(marker =>
                StringEquals(marker.MarkerName, sourceMarker.MarkerName) &&
                marker.MarkerType == sourceMarker.MarkerType &&
                DistanceSquared(marker, sourceMarker) <= 4.0f)
            .OrderBy(marker => DistanceSquared(marker, sourceMarker))
            .ThenBy(marker => marker.FormId)
            .FirstOrDefault();
    }

    private static void WriteAppearanceAudit(
        string path,
        RecordCollection generated,
        RecordCollection source,
        RecordCollection master,
        IReadOnlyList<string> actors)
    {
        var labels = BuildLabelIndex(generated, source, master);
        var sb = new StringBuilder();
        sb.AppendLine("target,origin,npc_form_id,editor_id,full_name,race,race_label,original_race,template,template_flags,uses_traits,face_npc,has_facegen,inventory,matching_armor_addons");

        foreach (var target in actors.Where(a => !string.IsNullOrWhiteSpace(a)).DefaultIfEmpty("Ulysses"))
        {
            foreach (var (origin, records) in new[] { ("generated", generated), ("source", source), ("master", master) })
            {
                foreach (var npc in records.Npcs.Where(n => MatchesNpc(n, target)))
                {
                    sb.AppendLine(string.Join(',',
                        Csv(target),
                        Csv(origin),
                        Csv($"0x{npc.FormId:X8}"),
                        Csv(npc.EditorId),
                        Csv(npc.FullName),
                        Csv(FormatFormId(npc.Race)),
                        Csv(ResolveLabel(labels, npc.Race)),
                        Csv(FormatFormId(npc.OriginalRace)),
                        Csv(FormatFormId(npc.Template)),
                        Csv(npc.Stats?.TemplateFlags.ToString("X4", CultureInfo.InvariantCulture) ?? string.Empty),
                        ((npc.Stats?.TemplateFlags ?? 0) & 0x0001) != 0,
                        Csv(FormatFormId(npc.FaceNpc)),
                        npc.FaceGenGeometrySymmetric is { Length: > 0 } ||
                        npc.FaceGenGeometryAsymmetric is { Length: > 0 } ||
                        npc.FaceGenTextureSymmetric is { Length: > 0 },
                        Csv(string.Join(' ', npc.Inventory.Select(i => FormatFormId(i.ItemFormId)))),
                        Csv(string.Join(' ', records.ArmorAddons
                            .Where(a => MatchesText(a.EditorId, target) || MatchesText(a.MaleModelPath, target))
                            .Select(a => $"{FormatFormId(a.FormId)}:{a.EditorId}:{a.MaleModelPath}")))));
                }
            }
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static Dictionary<uint, string> BuildBaseTypeIndex(RecordCollection primary, RecordCollection fallback)
    {
        var result = new Dictionary<uint, string>();
        AddBaseTypes(result, fallback);
        AddBaseTypes(result, primary);
        return result;
    }

    private static void AddBaseTypes(Dictionary<uint, string> result, RecordCollection records)
    {
        foreach (var record in records.Statics) result[record.FormId] = "STAT";
        foreach (var record in records.StaticCollections) result[record.FormId] = "SCOL";
        foreach (var record in records.GenericRecords)
        {
            if (record.RecordType is "MSTT" or "TREE" or "FLOR")
            {
                result[record.FormId] = record.RecordType;
            }
        }
    }

    private static bool IsLoadedPlacementEvidence(
        PlacedReference placed,
        Dictionary<uint, string> baseTypes)
    {
        return !placed.IsPersistent
               && baseTypes.TryGetValue(placed.BaseFormId, out var baseType)
               && LoadedPlacementBaseTypes.Contains(baseType);
    }

    private static Dictionary<uint, string> BuildLabelIndex(params RecordCollection[] collections)
    {
        var labels = new Dictionary<uint, string>();
        foreach (var records in collections.Reverse())
        {
            foreach (var (formId, editorId) in records.FormIdToEditorId)
            {
                labels[formId] = editorId;
            }

            foreach (var (formId, fullName) in records.FormIdToDisplayName)
            {
                labels.TryAdd(formId, fullName);
            }
        }

        return labels;
    }

    private static string ClassifyMapMarker(
        PlacedReference source,
        PlacedReference? generated,
        PlacedReference? master)
    {
        if (generated is null)
        {
            return "MissingEmitted";
        }

        if (master is null)
        {
            return "NewMarker";
        }

        var renamed = !StringEquals(source.MarkerName, master.MarkerName);
        var typeChanged = source.MarkerType != master.MarkerType;
        var relocated = DistanceSquared(source, master) > 1.0f;
        return (renamed, relocated, typeChanged) switch
        {
            (true, true, true) => "RenameRelocateType",
            (true, true, false) => "RenameRelocate",
            (true, false, true) => "RenameType",
            (false, true, true) => "RelocateType",
            (true, false, false) => "RenameOnly",
            (false, true, false) => "RelocateOnly",
            (false, false, true) => "TypeOnly",
            _ => "Unchanged"
        };
    }

    private static (float Min, float Max, float Range)? CalculateRange(LandHeightmap? heightmap)
    {
        if (heightmap is null)
        {
            return null;
        }

        return CalculateRange(heightmap.CalculateHeights());
    }

    private static (float Min, float Max, float Range) CalculateRange(float[,] heights)
    {
        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        for (var y = 0; y < heights.GetLength(0); y++)
        {
            for (var x = 0; x < heights.GetLength(1); x++)
            {
                var value = heights[y, x];
                min = MathF.Min(min, value);
                max = MathF.Max(max, value);
            }
        }

        return (min, max, max - min);
    }

    private static Dictionary<uint, RawLandRange> BuildRawLandRangesByCell(IReadOnlyList<ParsedMainRecord> records)
    {
        var result = new Dictionary<uint, RawLandRange>();
        uint? currentCell = null;
        foreach (var record in records.OrderBy(r => r.Offset))
        {
            if (record.Header.Signature == "CELL")
            {
                currentCell = record.Header.FormId;
                continue;
            }

            if (record.Header.Signature != "LAND" || !currentCell.HasValue)
            {
                continue;
            }

            result[currentCell.Value] = ReadRawLandRange(record);
        }

        return result;
    }

    private static RawLandRange ReadRawLandRange(ParsedMainRecord land)
    {
        var vhgt = land.Subrecords.FirstOrDefault(s => s.Signature == "VHGT")?.Data;
        if (vhgt is null || vhgt.Length == 0)
        {
            return new RawLandRange(land.Header.FormId, 0, null, null, null);
        }

        var range = CalculateRange(HeightmapUtils.ParseVhgtData(vhgt, false));
        return new RawLandRange(land.Header.FormId, vhgt.Length, range.Min, range.Max, range.Range);
    }

    private static bool MatchesNpc(NpcRecord npc, string target)
    {
        return TryParseFormId(target, out var formId)
            ? npc.FormId == formId
            : MatchesText(npc.EditorId, target) || MatchesText(npc.FullName, target);
    }

    private static bool MatchesText(string? value, string target)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Contains(target, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLabel(Dictionary<uint, string> labels, uint? formId)
    {
        return formId.HasValue && labels.TryGetValue(formId.Value, out var label) ? label : string.Empty;
    }

    private static string FormatRange((float Min, float Max, float Range)? range)
    {
        return range.HasValue
            ? $"{range.Value.Min:F2}..{range.Value.Max:F2} ({range.Value.Range:F2})"
            : string.Empty;
    }

    private static string FormatFloat(float? value)
    {
        return value?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string FormatFormId(uint? formId)
    {
        return formId.HasValue && formId.Value != 0 ? $"0x{formId.Value:X8}" : string.Empty;
    }

    private static string FormatGrid(CellRecord cell)
    {
        return cell.GridX.HasValue && cell.GridY.HasValue ? $"{cell.GridX},{cell.GridY}" : string.Empty;
    }

    private static string FormatPosition(PlacedReference placed)
    {
        return $"{placed.X:F1},{placed.Y:F1},{placed.Z:F1}";
    }

    private static float DistanceSquared(PlacedReference left, PlacedReference right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static bool StringEquals(string? left, string? right)
    {
        return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
    }

    private static bool TryParseFormId(string value, out uint formId)
    {
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out formId);
    }

    private static string Csv(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        return text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    private static Dictionary<uint, T> ToFirstByFormId<T>(IEnumerable<T> records, Func<T, uint> getFormId)
    {
        var result = new Dictionary<uint, T>();
        foreach (var record in records)
        {
            result.TryAdd(getFormId(record), record);
        }

        return result;
    }

    private readonly record struct RawLandRange(uint LandFormId, int VhgtLength, float? Min, float? Max, float? Range);
}
