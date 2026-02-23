using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     Pure-computation helper for building property lists for cells in the World Map tab.
///     All methods are static with no UI dependencies.
/// </summary>
internal static class WorldMapCellPropertyBuilder
{
    /// <summary>
    ///     Builds a property list for a cell (identity, grid, statistics, placed objects, metadata).
    /// </summary>
    public static List<EsmPropertyEntry> BuildCellProperties(
        CellRecord cell,
        WorldViewData? worldViewData,
        FormIdResolver? resolver)
    {
        var properties = new List<EsmPropertyEntry>();

        // Identity
        properties.Add(new EsmPropertyEntry
            { Name = "Form ID", Value = $"0x{cell.FormId:X8}", Category = "Identity" });
        if (!string.IsNullOrEmpty(cell.EditorId))
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Editor ID", Value = cell.EditorId, Category = "Identity" });
        }

        if (!string.IsNullOrEmpty(cell.FullName))
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Full Name", Value = cell.FullName, Category = "Identity" });
        }

        // Grid
        if (cell.GridX.HasValue && cell.GridY.HasValue)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Grid", Value = $"({cell.GridX.Value}, {cell.GridY.Value})", Category = "Grid" });
        }

        if (cell.WorldspaceFormId is > 0)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Worldspace",
                Value = $"0x{cell.WorldspaceFormId.Value:X8}",
                Category = "Grid",
                LinkedFormId = cell.WorldspaceFormId.Value
            });
        }

        // Properties
        properties.Add(new EsmPropertyEntry
            { Name = "Interior", Value = cell.IsInterior ? "Yes" : "No", Category = "Properties" });
        if (cell.HasWater)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Has Water", Value = "Yes", Category = "Properties" });
            if (cell.WaterHeight.HasValue && cell.WaterHeight.Value < 1_000_000f)
            {
                properties.Add(new EsmPropertyEntry
                    { Name = "Water Height", Value = $"{cell.WaterHeight.Value:F1}", Category = "Properties" });
            }
        }

        // Audio/Visual
        AddFormIdProperty(properties, "Encounter Zone", cell.EncounterZoneFormId, "Audio/Visual");
        AddFormIdProperty(properties, "Music Type", cell.MusicTypeFormId, "Audio/Visual");
        AddFormIdProperty(properties, "Acoustic Space", cell.AcousticSpaceFormId, "Audio/Visual");
        AddFormIdProperty(properties, "Image Space", cell.ImageSpaceFormId, "Audio/Visual");

        // Statistics
        AddCellStatistics(properties, cell);

        // Placed Objects (expandable by category)
        AddCellPlacedObjects(properties, cell, worldViewData, resolver);

        // Metadata
        properties.Add(new EsmPropertyEntry
            { Name = "File Offset", Value = $"0x{cell.Offset:X}", Category = "Metadata" });
        properties.Add(new EsmPropertyEntry
        {
            Name = "Endianness",
            Value = cell.IsBigEndian ? "Big-endian (Xbox 360)" : "Little-endian (PC)",
            Category = "Metadata"
        });

        return properties;
    }

    private static void AddFormIdProperty(
        List<EsmPropertyEntry> properties, string name, uint? formId, string category)
    {
        if (formId is > 0)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = name,
                Value = $"0x{formId.Value:X8}",
                Category = category,
                LinkedFormId = formId.Value
            });
        }
    }

    private static void AddCellStatistics(List<EsmPropertyEntry> properties, CellRecord cell)
    {
        properties.Add(new EsmPropertyEntry
            { Name = "Placed Objects", Value = cell.PlacedObjects.Count.ToString(), Category = "Statistics" });

        int refrCount = 0, achrCount = 0, acreCount = 0, markerCount = 0;
        foreach (var p in cell.PlacedObjects)
        {
            switch (p.RecordType)
            {
                case "REFR": refrCount++; break;
                case "ACHR": achrCount++; break;
                case "ACRE": acreCount++; break;
            }

            if (p.IsMapMarker)
            {
                markerCount++;
            }
        }

        if (refrCount > 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "  REFR (Objects)", Value = refrCount.ToString(), Category = "Statistics" });
        }

        if (achrCount > 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "  ACHR (NPCs)", Value = achrCount.ToString(), Category = "Statistics" });
        }

        if (acreCount > 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "  ACRE (Creatures)", Value = acreCount.ToString(), Category = "Statistics" });
        }

        if (markerCount > 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "  Map Markers", Value = markerCount.ToString(), Category = "Statistics" });
        }

        properties.Add(new EsmPropertyEntry
            { Name = "Has Heightmap", Value = cell.Heightmap != null ? "Yes" : "No", Category = "Statistics" });
    }

    private static void AddCellPlacedObjects(
        List<EsmPropertyEntry> properties,
        CellRecord cell,
        WorldViewData? worldViewData,
        FormIdResolver? resolver)
    {
        if (cell.LinkedCellFormIds.Count > 0)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Linked Cells",
                Value = cell.LinkedCellFormIds.Count.ToString(),
                Category = "Statistics",
                IsExpandable = true,
                SubItems = cell.LinkedCellFormIds.Select(formId =>
                {
                    var cellName = $"0x{formId:X8}";
                    if (worldViewData?.CellByFormId.TryGetValue(formId, out var linked) == true)
                    {
                        cellName = linked.EditorId ?? linked.FullName ?? cellName;
                    }

                    return new EsmPropertyEntry
                    {
                        Name = cellName,
                        Value = $"0x{formId:X8}",
                        Col1 = cellName,
                        Col3 = $"0x{formId:X8}",
                        CellNavigationFormId = formId
                    };
                }).ToList()
            });
        }

        if (cell.PlacedObjects.Count == 0)
        {
            return;
        }

        var grouped = cell.PlacedObjects
            .GroupBy(obj => PlacedObjectCategoryResolver.GetCategoryName(obj, worldViewData?.CategoryIndex))
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = group.Key,
                Value = group.Count().ToString(),
                Category = "Placed Objects",
                IsExpandable = true,
                SubItems = group.Select(obj =>
                {
                    var baseName = obj.BaseEditorId
                                   ?? resolver?.GetBestName(obj.BaseFormId)
                                   ?? $"0x{obj.BaseFormId:X8}";
                    return new EsmPropertyEntry
                    {
                        Col1 = baseName,
                        Col3 = $"0x{obj.BaseFormId:X8}",
                        Col3FormId = obj.BaseFormId,
                        Name = baseName,
                        Value = $"0x{obj.BaseFormId:X8}"
                    };
                }).ToList()
            });
        }
    }
}
