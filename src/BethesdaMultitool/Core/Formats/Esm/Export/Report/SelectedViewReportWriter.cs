using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using BethesdaMultitool.Core.WorldData;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Report;

/// <summary>Per-edge and per-component evidence for derived static views, without engine inheritance claims.</summary>
internal static class SelectedViewReportWriter
{
    internal static Dictionary<string, string> Generate(LoadOrderSelectionView view)
    {
        var links = new StringBuilder("SourceINFO,TargetFormID,Kind,Evidence,Status,SourcePlugin,SourcePath,SourceOffset,EvidenceINFO,EvidenceSourcePlugin,EvidenceSourcePath,EvidenceSourceOffset\n");
        foreach (var edge in view.Records.DialogueTree?.Edges ?? [])
        {
            var source = view.Index.Records.GetValueOrDefault(edge.SourceInfoFormId)?.Winner;
            // One row per contributing INFO keeps competing reverse-TCLF owners inspectable.
            // SourceINFO remains the graph origin; the appended fields identify the stored evidence.
            IEnumerable<uint?> evidenceIds = edge.EvidenceInfoFormIds.Count == 0
                ? [null] : edge.EvidenceInfoFormIds.Select(id => (uint?)id);
            foreach (var evidenceId in evidenceIds)
            {
                var evidence = evidenceId is { } id ? view.Index.Records.GetValueOrDefault(id)?.Winner : null;
                Row(links, Id(edge.SourceInfoFormId), Id(edge.TargetFormId), edge.Kind, edge.Evidence, edge.Status,
                    source?.Plugin, source?.FilePath, source?.Offset, Id(evidenceId),
                    evidence?.Plugin, evidence?.FilePath, evidence?.Offset);
            }
        }
        var ordering = new StringBuilder("TopicFormID,Issue,INFOs\n");
        foreach (var issue in view.Records.DialogueTree?.OrderingIssues ?? [])
        {
            Row(ordering, Id(issue.TopicFormId), issue.Code, string.Join(";", issue.InfoFormIds.Select(id => Id(id))));
        }
        var components = new StringBuilder("ParentFormID,SourceFormID,Component,TargetFormID,Value,Status,SourcePlugin,SourcePath,SourceOffset\n");
        var cells = view.Records.Cells.Select(c => c.FormId).ToHashSet();
        foreach (var cell in view.Records.Cells)
        {
            Component(cell.FormId, cell.FormId, "cell-fields", null, null, "selected");
            Component(cell.FormId, cell.FormId, "water-height", null, cell.WaterHeight, cell.WaterHeight.HasValue ? "stored-value" : "unset");
            Link(cell.FormId, cell.FormId, "water-type", cell.WaterFormId);
            Link(cell.FormId, cell.FormId, "image-space", cell.ImageSpaceFormId);
            Link(cell.FormId, cell.FormId, "lighting-template", cell.LightingTemplateFormId);
            Component(cell.FormId, cell.FormId, "lighting-data", null, cell.LightingTemplateInheritanceFlags,
                cell.LightingData is null ? "unset" : "stored-values");
        }
        foreach (var world in view.Records.Worldspaces)
        {
            Link(world.FormId, world.FormId, "parent-worldspace", world.ParentWorldspaceFormId);
            Component(world.FormId, world.FormId, "world-water-height", null, world.DefaultWaterHeight,
                world.WaterFromParentWorldspace ? "parser-derived;inheritance-not-recomputed" : "stored-value-or-unset");
            Link(world.FormId, world.FormId, "world-water-type", world.WaterFormId,
                world.WaterFromParentWorldspace ? "parser-derived;inheritance-not-recomputed" : null);
        }
        var counts = view.TerrainComponents.Where(t => t.ParentCellFormId.HasValue).GroupBy(t => t.ParentCellFormId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var land in view.TerrainComponents)
        {
            var status = land.ParentCellFormId is not { } parent || !cells.Contains(parent) ? "parent-unresolved" :
                counts[parent] > 1 ? "ambiguous-cell-land" : "selected";
            Component(land.ParentCellFormId, land.FormId, "land-height", null, null,
                land.Heightmap is null ? status + ";height-unavailable" : status);
            Component(land.ParentCellFormId, land.FormId, "land-visual", null, null,
                land.VisualData is null ? status + ";visual-unavailable" : status);
            foreach (var layer in land.VisualData?.TextureLayers ?? [])
            {
                Link(land.ParentCellFormId, land.FormId, "land-texture", layer.TextureFormId);
            }
        }
        foreach (var texture in view.Records.LandTextures)
        {
            Link(null, texture.FormId, "texture-set", texture.TextureSetFormId);
            Asset(texture.FormId, "texture-icon", texture.IconPath);
        }
        foreach (var texture in view.Records.TextureSets)
        {
            Asset(texture.FormId, "diffuse-texture", texture.DiffuseTexture);
            Asset(texture.FormId, "normal-texture", texture.NormalTexture);
            Asset(texture.FormId, "glow-texture", texture.GlowTexture);
            Asset(texture.FormId, "environment-texture", texture.EnvironmentTexture);
            Asset(texture.FormId, "environment-map", texture.EnvironmentMapTexture);
            Asset(texture.FormId, "parallax-texture", texture.ParallaxTexture);
        }
        var placements = new StringBuilder("ParentCellFormID,FormID,BaseFormID,RecordType,X,Y,Z,RotX,RotY,RotZ,Scale,Status,SourcePlugin,SourcePath,SourceOffset\n");
        foreach (var (parent, reference) in view.PlacementComponents)
        {
            var source = view.Index.Records[reference.FormId].Winner;
            Row(placements, Id(parent), Id(reference.FormId), Id(reference.BaseFormId), reference.RecordType,
                reference.X, reference.Y, reference.Z, reference.RotX, reference.RotY, reference.RotZ, reference.Scale,
                cells.Contains(parent) ? "selected" : "parent-unresolved", source.Plugin, source.FilePath, source.Offset);
            Link(parent, reference.FormId, "placed-base", reference.BaseFormId);
        }
        foreach (var (id, path) in view.Records.ModelPathIndex) { Asset(id, "model", path); }
        return new()
        {
            ["dialogue_links.csv"] = links.ToString(), ["dialogue_ordering.csv"] = ordering.ToString(),
            ["world_components.csv"] = components.ToString(), ["selected_placements.csv"] = placements.ToString(),
            ["world_inheritance.csv"] = WriteInheritance(view)
        };

        void Asset(uint source, string name, string? path)
        {
            if (!string.IsNullOrEmpty(path)) { Component(null, source, name, null, path, "declared-path;asset-not-verified"); }
        }
        void Link(uint? parent, uint source, string name, uint? target, string? status = null) =>
            Component(parent, source, name, target, null, status ?? (target is not > 0 ? "unset" :
                view.IncludedFormIds.Contains(target.Value) ? "resolved" : "unresolved"));
        void Component(uint? parent, uint sourceId, string name, uint? target, object? value, string status)
        {
            var source = view.Index.Records.GetValueOrDefault(sourceId)?.Winner;
            Row(components, Id(parent), Id(sourceId), name, Id(target), value, status, source?.Plugin, source?.FilePath, source?.Offset);
        }
    }

    private static string WriteInheritance(LoadOrderSelectionView view)
    {
        var csv = new StringBuilder("ContextFormID,Component,SourceFormID,TargetFormID,Value,Status,Selection,Path,SourcePlugin,SourcePath,SourceOffset,TargetStatus,RequestedSourceFormID,RequestedSourceStatus\n");
        var templates = view.Records.LightingTemplates.ToDictionary(t => t.FormId);
        var waterIds = view.Records.Water.Select(record => record.FormId).ToHashSet();
        var climateIds = view.Records.Climate.Select(record => record.FormId).ToHashSet();
        var imageSpaceIds = view.Records.ImageSpaces.Select(record => record.FormId).ToHashSet();
        foreach (var cell in view.Records.Cells)
        {
            var template = cell.LightingTemplateFormId is { } id ? templates.GetValueOrDefault(id) : null;
            foreach (var field in InteriorLightingFieldResolver.Fields)
            {
                var selected = InteriorLightingFieldResolver.Resolve(field, cell.LightingData,
                    template?.LightingData, cell.LightingTemplateInheritanceFlags.GetValueOrDefault());
                var sourceId = selected.Source switch
                {
                    InteriorLightingSource.Cell => (uint?)cell.FormId,
                    InteriorLightingSource.Template => template?.FormId,
                    _ => null
                };
                var selection = selected.Source.ToString().ToLowerInvariant();
                if (selected.InheritanceRequested) selection += ";inherit-requested";
                if (selected.UsedFallback) selection += ";fallback";
                var path = sourceId.HasValue && sourceId != cell.FormId
                    ? $"{Id(cell.FormId)};{Id(sourceId)}" : Id(cell.FormId);
                Write(cell.FormId, "lighting-" + field, sourceId, null, selected.Value, selection,
                    path, selected.InheritanceRequested ? cell.LightingTemplateFormId : cell.FormId);
            }
        }
        // These PNAM bits are defined by the FO3/FNV schema. Other families retain
        // their own existing atmosphere/water policies.
        if (view.Records.Game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas)
        {
            var waterCatalog = WorldWaterCatalog.Create(view.Records.Worldspaces, view.Records.Game);
            foreach (var world in view.Records.Worldspaces)
            {
                foreach (var component in new[] { WorldspaceComponent.Water, WorldspaceComponent.Climate, WorldspaceComponent.ImageSpace })
                {
                    var route = WorldspaceInheritanceResolver.Resolve(world, view.Records.Worldspaces, component);
                    var source = route.Source;
                    var path = string.Join(";", route.Path.Select(id => Id(id)));
                    if (component == WorldspaceComponent.Water)
                    {
                        var water = waterCatalog.Get(world.FormId);
                        var waterPath = string.Join(";", water.Path.Select(id => Id(id)));
                        Write(world.FormId, "world-water-height", water.SourceFormId, null, water.Height, water.Status, waterPath, water.Path.LastOrDefault());
                        Write(world.FormId, "world-water-type", water.SourceFormId, water.WaterFormId, null, water.Status, waterPath, water.Path.LastOrDefault());
                    }
                    else
                    {
                        Write(world.FormId, component == WorldspaceComponent.Climate ? "world-climate" : "world-image-space",
                            source?.FormId, component == WorldspaceComponent.Climate ? source?.ClimateFormId : source?.ImageSpaceFormId,
                            null, route.Status, path, route.Path.LastOrDefault());
                    }
                }
            }
        }
        return csv.ToString();

        void Write(uint context, string component, uint? sourceId, uint? target, object? value, string selection, string path,
            uint? requestedSource)
        {
            var source = sourceId.HasValue ? view.Index.Records.GetValueOrDefault(sourceId.Value)?.Winner : null;
            Row(csv, Id(context), component, Id(sourceId), Id(target), value,
                sourceId.HasValue && (value is not null || target is > 0) ? "Reconstruction" : "Unavailable",
                selection, path, source?.Plugin, source?.FilePath, source?.Offset,
                TargetStatus(component, target),
                Id(requestedSource), requestedSource is not > 0 ? "unset" :
                view.Index.Records.TryGetValue(requestedSource.Value, out var identity) ? view.Status(identity) : "unavailable");
        }

        string TargetStatus(string component, uint? target)
        {
            if (target is not > 0) return "unset";
            var expected = component switch
            {
                "world-water-type" => waterIds,
                "world-climate" => climateIds,
                "world-image-space" => imageSpaceIds,
                _ => throw new ArgumentOutOfRangeException(nameof(component))
            };
            if (expected.Contains(target.Value)) return "resolved";
            if (view.IncludedFormIds.Contains(target.Value)) return "wrong-type";
            return view.Index.Records.TryGetValue(target.Value, out var identity) ? view.Status(identity) : "unresolved";
        }
    }

    private static string Id(uint? id) => id.HasValue ? $"0x{id.Value:X8}" : "";
    private static void Row(StringBuilder target, params object?[] fields) => target.AppendLine(string.Join(",", fields.Select(value =>
    {
        var text = value switch { float f => f.ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value?.ToString() ?? "" };
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? '"' + text.Replace("\"", "\"\"") + '"' : text;
    })));
}
