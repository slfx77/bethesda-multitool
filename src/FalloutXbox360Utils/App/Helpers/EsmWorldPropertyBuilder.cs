using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds property entries for world/cell, AI package, and placement records.
/// </summary>
internal static class EsmWorldPropertyBuilder
{
    /// <summary>
    ///     Adds AI package-specific properties (type, flags, schedule, location, target).
    /// </summary>
    internal static void AddPackageProperties(
        List<EsmPropertyEntry> properties, object record, FormIdResolver? resolver)
    {
        if (record is not PackageRecord pkgRecord)
        {
            return;
        }

        if (pkgRecord.Data != null)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Package Type",
                Value = pkgRecord.Data.TypeName,
                Category = "General"
            });
            properties.Add(new EsmPropertyEntry
            {
                Name = "General Flags",
                Value = FlagRegistry.DecodeFlagNamesWithHex(pkgRecord.Data.GeneralFlags,
                    FlagRegistry.PackageGeneralFlags),
                Category = "General"
            });
            if (pkgRecord.Data.FalloutBehaviorFlags != 0)
            {
                properties.Add(new EsmPropertyEntry
                {
                    Name = "Fallout Behaviors",
                    Value = FlagRegistry.DecodeFlagNamesWithHex(pkgRecord.Data.FalloutBehaviorFlags,
                        FlagRegistry.PackageFOBehaviorFlags),
                    Category = "General"
                });
            }

            if (pkgRecord.Data.TypeSpecificFlags != 0)
            {
                properties.Add(new EsmPropertyEntry
                {
                    Name = "Type-Specific Flags",
                    Value = FlagRegistry.DecodeFlagNamesWithHex(pkgRecord.Data.TypeSpecificFlags,
                        FlagRegistry.PackageTypeSpecificFlags),
                    Category = "General"
                });
            }
        }

        if (pkgRecord.Schedule != null)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Schedule",
                Value = pkgRecord.Schedule.Summary,
                Category = "General"
            });
        }

        if (pkgRecord.IsRepeatable)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Repeatable", Value = "Yes", Category = "General" });
        }

        if (pkgRecord.IsStartingLocationLinkedRef)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Starting Location", Value = "At Linked Reference", Category = "General" });
        }

        AddPackageLocationProperty(properties, "Location", pkgRecord.Location, resolver);
        AddPackageLocationProperty(properties, "Location 2", pkgRecord.Location2, resolver);
        AddPackageTargetProperty(properties, "Target", pkgRecord.Target, resolver);
        AddPackageTargetProperty(properties, "Target 2", pkgRecord.Target2, resolver);
    }

    /// <summary>
    ///     Adds worldspace statistics (cell count, grid cells, placed objects).
    /// </summary>
    internal static void AddWorldspaceStats(List<EsmPropertyEntry> properties, object record)
    {
        if (record is not WorldspaceRecord wsRecord)
        {
            return;
        }

        var gridCells = wsRecord.Cells.Count(c => c.GridX.HasValue);
        var persistentCells = wsRecord.Cells.Count - gridCells;
        var totalObjects = wsRecord.Cells.Sum(c => c.PlacedObjects.Count);

        properties.Add(new EsmPropertyEntry
            { Name = "Cell Count", Value = wsRecord.Cells.Count.ToString("N0"), Category = "Statistics" });
        if (gridCells > 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "  Grid Cells", Value = gridCells.ToString("N0"), Category = "Statistics" });
        }

        if (persistentCells > 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "  Persistent Cells", Value = persistentCells.ToString("N0"), Category = "Statistics" });
        }

        properties.Add(new EsmPropertyEntry
            { Name = "Total Placed Objects", Value = totalObjects.ToString("N0"), Category = "Statistics" });
    }

    /// <summary>
    ///     Adds world placement references for a record (where it is placed in the world).
    /// </summary>
    internal static void AddWorldPlacements(
        List<EsmPropertyEntry> properties, object record, Type type,
        Dictionary<uint, List<WorldPlacement>>? placementIndex)
    {
        if (placementIndex == null)
        {
            return;
        }

        var recordFormId =
            (uint)(EsmPropertyFormatter.GetCachedProperty(type, "FormId")?.GetValue(record) ?? 0u);
        if (recordFormId == 0 ||
            !placementIndex.TryGetValue(recordFormId, out var worldPlacements) ||
            worldPlacements.Count == 0)
        {
            return;
        }

        var subItems = new List<EsmPropertyEntry>(worldPlacements.Count);
        foreach (var wp in worldPlacements)
        {
            var cellName = wp.Cell.FullName
                           ?? wp.Cell.EditorId
                           ?? $"0x{wp.Cell.FormId:X8}";
            var pos = $"({wp.Ref.X:F0}, {wp.Ref.Y:F0}, {wp.Ref.Z:F0})";
            var gridInfo = wp.Cell switch
            {
                { IsInterior: true } => "Interior",
                { GridX: not null } => $"Grid ({wp.Cell.GridX},{wp.Cell.GridY})",
                _ => "Exterior"
            };

            subItems.Add(new EsmPropertyEntry
            {
                Col1 = cellName,
                Col2 = pos,
                Col3 = $"{wp.Ref.RecordType} 0x{wp.Ref.FormId:X8}",
                Col4 = gridInfo,
                CellNavigationFormId = wp.Cell.FormId
            });
        }

        properties.Add(new EsmPropertyEntry
        {
            Name = $"World Placements ({worldPlacements.Count} instances)",
            Value = "",
            Category = "References",
            IsExpandable = true,
            SubItems = subItems
        });
    }

    private static void AddPackageLocationProperty(
        List<EsmPropertyEntry> properties,
        string label,
        PackageLocation? location,
        FormIdResolver? resolver)
    {
        if (location == null)
        {
            return;
        }

        var typeName = location.Type < EsmPropertyFormatter.LocationTypeNames.Length
            ? EsmPropertyFormatter.LocationTypeNames[location.Type]
            : $"Unknown ({location.Type})";

        var editorId = resolver?.GetEditorId(location.Union);
        var displayName = resolver?.GetDisplayName(location.Union);

        // Chain through REFR -> base object if direct lookup fails
        if (editorId == null && displayName == null && resolver != null)
        {
            var baseFormId = resolver.GetBaseFormId(location.Union);
            if (baseFormId.HasValue)
            {
                editorId = resolver.GetEditorId(baseFormId.Value);
                displayName = resolver.GetDisplayName(baseFormId.Value);
            }
        }

        var formIdStr = location.Union != 0
            ? EsmPropertyFormatter.FormatFormIdReference(location.Union, editorId, displayName)
            : "None";

        var value = location.Radius > 0
            ? $"{typeName}: {formIdStr} (radius: {location.Radius})"
            : $"{typeName}: {formIdStr}";

        // For "In Cell" (type 1), navigate to world map since the union IS a cell FormID.
        // For other types with FormID references, use data browser navigation.
        uint? cellNavFormId = null;
        uint? linkedFormId = null;
        if (location.Union != 0)
        {
            if (location.Type == 1)
            {
                cellNavFormId = location.Union;
            }
            else
            {
                linkedFormId = location.Union;
            }
        }

        properties.Add(new EsmPropertyEntry
        {
            Name = label,
            Value = value,
            Category = "General",
            LinkedFormId = linkedFormId,
            CellNavigationFormId = cellNavFormId
        });
    }

    private static void AddPackageTargetProperty(
        List<EsmPropertyEntry> properties,
        string label,
        PackageTarget? target,
        FormIdResolver? resolver)
    {
        if (target == null)
        {
            return;
        }

        string formIdStr;
        uint? linkedFormId = null;

        if (target.Type is 0 or 1 && target.FormIdOrType != 0)
        {
            // Specific Reference or Object ID - FormID reference
            var editorId = resolver?.GetEditorId(target.FormIdOrType);
            var dispName = resolver?.GetDisplayName(target.FormIdOrType);

            // Chain through REFR -> base object if direct lookup fails
            if (editorId == null && dispName == null && resolver != null)
            {
                var baseFormId = resolver.GetBaseFormId(target.FormIdOrType);
                if (baseFormId.HasValue)
                {
                    editorId = resolver.GetEditorId(baseFormId.Value);
                    dispName = resolver.GetDisplayName(baseFormId.Value);
                }
            }

            formIdStr = EsmPropertyFormatter.FormatFormIdReference(target.FormIdOrType, editorId, dispName);
            linkedFormId = target.FormIdOrType;
        }
        else if (target.Type == 2)
        {
            formIdStr = $"Type {target.FormIdOrType}";
        }
        else
        {
            formIdStr = target.FormIdOrType != 0 ? $"0x{target.FormIdOrType:X8}" : "None";
        }

        var parts = new List<string> { $"{target.TypeName}: {formIdStr}" };
        if (target.CountDistance != 0)
        {
            parts.Add($"count/dist: {target.CountDistance}");
        }

        properties.Add(new EsmPropertyEntry
        {
            Name = label,
            Value = string.Join(", ", parts),
            Category = "General",
            LinkedFormId = linkedFormId
        });
    }
}
