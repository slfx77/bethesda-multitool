using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     Pure-computation helper for building property lists and resolving categories
///     for placed objects and cells in the World Map tab.
///     All methods are static with no UI dependencies.
/// </summary>
internal static class PlacedObjectCategoryResolver
{
    /// <summary>
    ///     Returns a display-friendly category name for a placed object, using the category
    ///     index when available and falling back to record-type heuristics.
    /// </summary>
    public static string GetCategoryName(
        PlacedReference obj, Dictionary<uint, PlacedObjectCategory>? categoryIndex)
    {
        if (obj.IsMapMarker)
        {
            return "Map Markers";
        }

        if (obj.RecordType == "ACHR")
        {
            return "NPCs";
        }

        if (obj.RecordType == "ACRE")
        {
            return "Creatures";
        }

        if (categoryIndex?.TryGetValue(obj.BaseFormId, out var category) == true)
        {
            return category switch
            {
                PlacedObjectCategory.Static => "Statics",
                PlacedObjectCategory.Architecture => "Architecture",
                PlacedObjectCategory.Landscape => "Landscape",
                PlacedObjectCategory.Plants => "Plants",
                PlacedObjectCategory.Clutter => "Clutter",
                PlacedObjectCategory.Dungeon => "Dungeon",
                PlacedObjectCategory.Effects => "Effects",
                PlacedObjectCategory.Vehicles => "Vehicles",
                PlacedObjectCategory.Traps => "Traps",
                PlacedObjectCategory.Door => "Doors",
                PlacedObjectCategory.Activator => "Activators",
                PlacedObjectCategory.Light => "Lights",
                PlacedObjectCategory.Furniture => "Furniture",
                PlacedObjectCategory.Npc => "NPCs",
                PlacedObjectCategory.Creature => "Creatures",
                PlacedObjectCategory.Container => "Containers",
                PlacedObjectCategory.Item => "Items",
                _ => "Other"
            };
        }

        return "Other";
    }

    /// <summary>
    ///     Builds a property list for a placed object reference (identity, position, references, metadata).
    /// </summary>
    public static List<EsmPropertyEntry> BuildObjectProperties(
        PlacedReference obj,
        WorldViewData? worldViewData,
        FormIdResolver? resolver)
    {
        var effectiveResolver = worldViewData?.Resolver ?? resolver;
        var properties = new List<EsmPropertyEntry>();
        var referenceEditorId = GetReferenceEditorId(obj, effectiveResolver);

        // Identity
        properties.Add(new EsmPropertyEntry
            { Name = "Form ID", Value = $"0x{obj.FormId:X8}", Category = "Identity" });
        if (!string.IsNullOrEmpty(referenceEditorId))
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Reference Editor ID", Value = referenceEditorId, Category = "Identity" });
        }

        properties.Add(new EsmPropertyEntry
        {
            Name = "Base Form ID",
            Value = $"0x{obj.BaseFormId:X8}",
            Category = "Identity",
            LinkedFormId = obj.BaseFormId
        });
        var baseEditorId = obj.BaseEditorId ?? effectiveResolver?.GetEditorId(obj.BaseFormId);
        if (!string.IsNullOrEmpty(baseEditorId))
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Base Editor ID", Value = baseEditorId, Category = "Identity" });
        }

        var baseFullName = effectiveResolver?.GetDisplayName(obj.BaseFormId);
        if (!string.IsNullOrEmpty(baseFullName))
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Base Name", Value = baseFullName, Category = "Identity" });
        }

        properties.Add(new EsmPropertyEntry
            { Name = "Record Type", Value = obj.RecordType, Category = "Identity" });

        // Category (from ObjectBoundsIndex or record type)
        if (worldViewData?.CategoryIndex.TryGetValue(obj.BaseFormId, out var objCategory) == true)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Category",
                Value = WorldMapColors.GetCategoryDisplayName(objCategory),
                Category = "Identity"
            });
        }
        else if (obj.IsMapMarker)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Category", Value = "Map Marker", Category = "Identity" });
        }
        else if (obj.RecordType == "ACHR")
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Category", Value = "NPC", Category = "Identity" });
        }
        else if (obj.RecordType == "ACRE")
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Category", Value = "Creature", Category = "Identity" });
        }

        var useCount = worldViewData?.UsageIndex?.GetUseCount(obj.FormId) ?? 0;
        if (useCount > 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Use", Value = useCount.ToString("N0"), Category = "Identity" });
        }

        if (obj.IsInitiallyDisabled)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Initial State", Value = "Initially Disabled", Category = "Identity" });
        }

        if (worldViewData?.RefrToCellIndex.TryGetValue(obj.FormId, out var parentCell) == true)
        {
            var cellName = parentCell.EditorId ?? parentCell.FullName ?? $"0x{parentCell.FormId:X8}";
            if (parentCell.GridX.HasValue && parentCell.GridY.HasValue)
            {
                cellName = $"[{parentCell.GridX.Value},{parentCell.GridY.Value}] {cellName}";
            }

            properties.Add(new EsmPropertyEntry
            {
                Name = "Parent Cell",
                Value = cellName,
                Category = "Identity",
                CellNavigationFormId = parentCell.FormId
            });
        }

        // Position
        properties.Add(new EsmPropertyEntry
            { Name = "Position", Value = $"({obj.X:F1}, {obj.Y:F1}, {obj.Z:F1})", Category = "Position" });
        properties.Add(new EsmPropertyEntry
            { Name = "Rotation", Value = $"({obj.RotX:F3}, {obj.RotY:F3}, {obj.RotZ:F3}) rad", Category = "Position" });
        if (Math.Abs(obj.Scale - 1.0f) > 0.001f)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Scale", Value = $"{obj.Scale:F3}", Category = "Position" });
        }

        if (obj.Radius is > 0)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Radius", Value = $"{obj.Radius.Value:F1}", Category = "Position" });
        }

        if (worldViewData?.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds) == true)
        {
            properties.Add(new EsmPropertyEntry
                { Name = "Object Bounds", Value = bounds.ToString(), Category = "Position" });
        }

        // References
        AddFormIdProperty(properties, "Owner", obj.OwnerFormId, "References");
        AddFormIdProperty(properties, "Encounter Zone", obj.EncounterZoneFormId, "References");
        AddFormIdProperty(properties, "Lock Key", obj.LockKeyFormId, "References");
        AddFormIdProperty(properties, "Enable Parent", obj.EnableParentFormId, "References");
        AddFormIdProperty(properties, "Persistent Cell", obj.PersistentCellFormId, "References");
        AddFormIdProperty(properties, "Starting World/Cell", obj.StartingWorldOrCellFormId, "References");
        AddFormIdProperty(properties, "Linked Ref Keyword", obj.LinkedRefKeywordFormId, "References");
        AddFormIdProperty(properties, "Linked Ref", obj.LinkedRefFormId, "References");
        AddFormIdProperty(properties, "Merchant Container", obj.MerchantContainerFormId, "References");
        AddFormIdProperty(properties, "Leveled Original Base", obj.LeveledCreatureOriginalBaseFormId, "References");
        AddFormIdProperty(properties, "Leveled Template", obj.LeveledCreatureTemplateFormId, "References");
        AddFormIdProperty(properties, "Destination Door", obj.DestinationDoorFormId, "References");
        if (obj.LinkedRefChildrenFormIds.Count > 0)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Linked Ref Children",
                Value = obj.LinkedRefChildrenFormIds.Count.ToString("N0"),
                Category = "References"
            });
        }

        if (obj.DestinationCellFormId is > 0)
        {
            var cellName = $"0x{obj.DestinationCellFormId.Value:X8}";
            if (worldViewData?.CellByFormId.TryGetValue(obj.DestinationCellFormId.Value, out var destCell) == true)
            {
                cellName = destCell.EditorId ?? destCell.FullName ?? cellName;
            }

            properties.Add(new EsmPropertyEntry
            {
                Name = "Destination Cell",
                Value = cellName,
                Category = "References",
                CellNavigationFormId = obj.DestinationCellFormId.Value
            });
        }

        if (obj.StartingPosition != null || obj.PackageStartLocation != null)
        {
            if (obj.StartingPosition != null)
            {
                properties.Add(new EsmPropertyEntry
                {
                    Name = "Starting Position",
                    Value = $"({obj.StartingPosition.X:F1}, {obj.StartingPosition.Y:F1}, {obj.StartingPosition.Z:F1})",
                    Category = "Spawn"
                });
                properties.Add(new EsmPropertyEntry
                {
                    Name = "Starting Rotation",
                    Value =
                        $"({obj.StartingPosition.RotX:F3}, {obj.StartingPosition.RotY:F3}, {obj.StartingPosition.RotZ:F3}) rad",
                    Category = "Spawn"
                });
            }

            if (obj.PackageStartLocation != null)
            {
                AddFormIdProperty(properties, "Package Start Form", obj.PackageStartLocation.LocationFormId, "Spawn");
                properties.Add(new EsmPropertyEntry
                {
                    Name = "Package Start Position",
                    Value =
                        $"({obj.PackageStartLocation.X:F1}, {obj.PackageStartLocation.Y:F1}, {obj.PackageStartLocation.Z:F1})",
                    Category = "Spawn"
                });
                properties.Add(new EsmPropertyEntry
                {
                    Name = "Package Start Z Rotation",
                    Value = $"{obj.PackageStartLocation.RotZ:F3} rad",
                    Category = "Spawn"
                });
            }
        }

        if (obj.LockLevel.HasValue || obj.LockFlags.HasValue || obj.LockNumTries.HasValue ||
            obj.LockTimesUnlocked.HasValue)
        {
            if (obj.LockLevel.HasValue)
            {
                properties.Add(new EsmPropertyEntry
                    { Name = "Level", Value = obj.LockLevel.Value.ToString(), Category = "Lock" });
            }

            if (obj.LockFlags.HasValue)
            {
                properties.Add(new EsmPropertyEntry
                    { Name = "Flags", Value = $"0x{obj.LockFlags.Value:X2}", Category = "Lock" });
            }

            if (obj.LockNumTries.HasValue)
            {
                properties.Add(new EsmPropertyEntry
                    { Name = "Failed Attempts", Value = obj.LockNumTries.Value.ToString("N0"), Category = "Lock" });
            }

            if (obj.LockTimesUnlocked.HasValue)
            {
                properties.Add(new EsmPropertyEntry
                    { Name = "Unlock Count", Value = obj.LockTimesUnlocked.Value.ToString("N0"), Category = "Lock" });
            }
        }

        // Map Marker
        if (obj.IsMapMarker)
        {
            if (!string.IsNullOrEmpty(obj.MarkerName))
            {
                properties.Add(new EsmPropertyEntry
                    { Name = "Marker Name", Value = obj.MarkerName, Category = "Map Marker" });
            }

            if (obj.MarkerType.HasValue)
            {
                properties.Add(new EsmPropertyEntry
                    { Name = "Marker Type", Value = obj.MarkerType.Value.ToString(), Category = "Map Marker" });
            }
        }

        // Spawn Info (for ACHR/ACRE placing leveled lists or direct actors)
        AddSpawnInfo(properties, obj, worldViewData, effectiveResolver);
        AddUsageInfo(properties, obj, worldViewData?.UsageIndex, effectiveResolver);

        // Metadata
        properties.Add(new EsmPropertyEntry
            { Name = "File Offset", Value = $"0x{obj.Offset:X}", Category = "Metadata" });
        properties.Add(new EsmPropertyEntry
        {
            Name = "Endianness",
            Value = obj.IsBigEndian ? "Big-endian (Xbox 360)" : "Little-endian (PC)",
            Category = "Metadata"
        });

        return properties;
    }

    /// <summary>
    ///     Computes the full inspection title for a placed object, incorporating category prefix,
    ///     display name, disabled state, and leveled-list overrides.
    /// </summary>
    public static string GetObjectInspectionTitle(
        PlacedReference obj,
        WorldViewData? worldViewData,
        FormIdResolver? resolver)
    {
        var effectiveResolver = worldViewData?.Resolver ?? resolver;
        var displayName = GetReferenceAwareName(obj, effectiveResolver);

        // Category-based prefix (not raw record type)
        string prefix;
        if (obj.IsMapMarker)
        {
            prefix = "Map Marker";
        }
        else if (obj.RecordType == "ACHR")
        {
            prefix = "NPC";
        }
        else if (obj.RecordType == "ACRE")
        {
            prefix = "Creature";
        }
        else if (worldViewData?.CategoryIndex.TryGetValue(obj.BaseFormId, out var titleCat) == true)
        {
            prefix = WorldMapColors.GetCategoryDisplayName(titleCat);
        }
        else
        {
            prefix = obj.RecordType;
        }

        var title = obj.IsInitiallyDisabled
            ? $"{prefix}: {displayName} [Disabled]"
            : $"{prefix}: {displayName}";

        // Check if spawn index has a leveled-list title override
        var leveledTitle = GetLeveledTitleOverride(obj, worldViewData?.SpawnIndex, effectiveResolver);
        return leveledTitle ?? title;
    }

    /// <summary>
    ///     If the placed reference is on a leveled list, returns the "[Leveled]" title prefix
    ///     and the resolved display name for the UI title.  Returns null when no title override is needed.
    /// </summary>
    public static string? GetLeveledTitleOverride(
        PlacedReference obj, SpawnResolutionIndex? spawnIndex, FormIdResolver? resolver)
    {
        if (spawnIndex == null) return null;

        var isLeveled = spawnIndex.LeveledListTypes.ContainsKey(obj.BaseFormId);
        var isAchr = obj.RecordType == "ACHR";
        var isAcre = obj.RecordType == "ACRE";

        if (!isAchr && !isAcre || !isLeveled) return null;

        var name = resolver?.GetDisplayName(obj.BaseFormId)
                   ?? GetReferenceEditorId(obj, resolver)
                   ?? obj.BaseEditorId
                   ?? resolver?.GetBestName(obj.BaseFormId)
                   ?? $"0x{obj.BaseFormId:X8}";
        var lvlPrefix = isAchr ? "NPC" : "Creature";
        return $"{lvlPrefix}: [Leveled] {name}";
    }

    public static string? GetReferenceEditorId(PlacedReference obj, FormIdResolver? resolver)
    {
        return resolver?.GetEditorId(obj.FormId);
    }

    public static string GetReferenceAwareName(PlacedReference obj, FormIdResolver? resolver)
    {
        if (obj.IsMapMarker && !string.IsNullOrEmpty(obj.MarkerName))
        {
            return obj.MarkerName;
        }

        return GetReferenceEditorId(obj, resolver)
               ?? resolver?.GetDisplayName(obj.FormId)
               ?? resolver?.GetDisplayName(obj.BaseFormId)
               ?? obj.BaseEditorId
               ?? resolver?.GetBestName(obj.BaseFormId)
               ?? $"0x{obj.BaseFormId:X8}";
    }

    // -- Private helpers --

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

    private static void AddSpawnInfo(
        List<EsmPropertyEntry> properties,
        PlacedReference obj,
        WorldViewData? worldViewData,
        FormIdResolver? resolver)
    {
        var spawnIndex = worldViewData?.SpawnIndex;
        if (spawnIndex == null) return;

        var isLeveled = spawnIndex.LeveledListTypes.ContainsKey(obj.BaseFormId);
        var isAchr = obj.RecordType == "ACHR";
        var isAcre = obj.RecordType == "ACRE";

        if (!isAchr && !isAcre) return;

        // Resolve actors from leveled list or direct placement
        var actorFormIds = new List<uint>();
        if (isLeveled && spawnIndex.LeveledListEntries.TryGetValue(obj.BaseFormId, out var resolved))
        {
            actorFormIds.AddRange(resolved);

            // Show possible spawns
            var label = isAchr ? "Possible NPCs" : "Possible Creatures";
            var distinct = resolved.Distinct().ToList();
            var names = distinct.Select(fid =>
            {
                var n = resolver?.GetBestName(fid);
                return n ?? $"0x{fid:X8}";
            }).ToList();

            properties.Add(new EsmPropertyEntry
            {
                Name = label,
                Value = $"{distinct.Count} entries",
                Category = "Spawn Info",
                IsExpandable = true,
                SubItems = distinct.Select((fid, i) => new EsmPropertyEntry
                {
                    Name = names[i],
                    Value = $"0x{fid:X8}",
                    Col1 = names[i],
                    Col3 = $"0x{fid:X8}",
                    LinkedFormId = fid
                }).ToList()
            });
        }
        else
        {
            // Direct placement - the actor is the base form
            actorFormIds.Add(obj.BaseFormId);
        }

        // Collect AI package cells and refs from all resolved actors
        var packageCells = new List<uint>();
        var packageRefs = new List<PackageRefLocation>();
        foreach (var actorFid in actorFormIds.Distinct())
        {
            if (spawnIndex.ActorToPackageCells.TryGetValue(actorFid, out var cells))
            {
                packageCells.AddRange(cells);
            }

            if (spawnIndex.ActorToPackageRefs.TryGetValue(actorFid, out var refs))
            {
                packageRefs.AddRange(refs);
            }
        }

        // Show AI package cells
        if (packageCells.Count > 0)
        {
            var distinctCells = packageCells.Distinct().ToList();
            properties.Add(new EsmPropertyEntry
            {
                Name = "AI Package Cells",
                Value = $"{distinctCells.Count} cells",
                Category = "Spawn Info",
                IsExpandable = true,
                SubItems = distinctCells.Select(cellFid =>
                {
                    var cellName = resolver?.GetBestName(cellFid) ?? $"0x{cellFid:X8}";
                    return new EsmPropertyEntry
                    {
                        Name = cellName,
                        Value = $"0x{cellFid:X8}",
                        Col1 = cellName,
                        Col3 = $"0x{cellFid:X8}",
                        CellNavigationFormId = cellFid
                    };
                }).ToList()
            });
        }

        // Show AI package refs
        if (packageRefs.Count > 0)
        {
            var distinctRefs = packageRefs.DistinctBy(r => r.RefFormId).ToList();
            properties.Add(new EsmPropertyEntry
            {
                Name = "AI Package Refs",
                Value = $"{distinctRefs.Count} locations",
                Category = "Spawn Info",
                IsExpandable = true,
                SubItems = distinctRefs.Select(r =>
                {
                    var refName = resolver?.GetBestName(r.RefFormId) ?? $"0x{r.RefFormId:X8}";
                    var radiusStr = r.Radius > 0 ? $" (radius: {r.Radius})" : "";
                    return new EsmPropertyEntry
                    {
                        Name = refName,
                        Value = $"0x{r.RefFormId:X8}{radiusStr}",
                        Col1 = refName,
                        Col3 = $"0x{r.RefFormId:X8}{radiusStr}",
                        LinkedFormId = r.RefFormId
                    };
                }).ToList()
            });
        }
    }

    private static void AddUsageInfo(
        List<EsmPropertyEntry> properties,
        PlacedReference obj,
        FormUsageIndex? usageIndex,
        FormIdResolver? resolver)
    {
        if (usageIndex == null)
        {
            return;
        }

        var usages = usageIndex.GetUsages(obj.FormId);
        if (usages.Count == 0)
        {
            return;
        }

        properties.Add(new EsmPropertyEntry
        {
            Name = $"Used By ({usages.Count})",
            Value = "",
            Category = "References",
            IsExpandable = true,
            SubItems = usages
                .OrderBy(u => resolver?.GetBestNameWithRefChain(u.SourceFormId) ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(u => u.Context, StringComparer.OrdinalIgnoreCase)
                .Select(u => new EsmPropertyEntry
                {
                    Name = resolver?.GetBestNameWithRefChain(u.SourceFormId) ?? $"0x{u.SourceFormId:X8}",
                    Value = $"{u.Context} [{u.SourceKind}] 0x{u.SourceFormId:X8}",
                    LinkedFormId = u.SourceFormId
                })
                .ToList()
        });
    }
}
