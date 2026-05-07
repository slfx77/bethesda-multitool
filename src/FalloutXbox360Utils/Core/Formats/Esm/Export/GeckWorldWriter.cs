using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Generates GECK-style text reports for Cell, Worldspace, and Map Marker records.</summary>
internal static class GeckWorldWriter
{
    internal static RecordReport BuildCellReport(
        CellRecord cell,
        FormIdResolver resolver,
        IReadOnlyDictionary<uint, PlacedReferenceLocation>? placedReferenceLocations = null)
    {
        var sections = new List<ReportSection>();

        // Identity
        var identityFields = new List<ReportField>
        {
            new("Type", ReportValue.String(cell.IsInterior ? "Interior" : "Exterior")),
            new("Flags", ReportValue.String($"0x{cell.Flags:X2}")),
            new("Has Water", ReportValue.Bool(cell.HasWater)),
            new("Endianness", ReportValue.String(cell.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")),
            new("Offset", ReportValue.String($"0x{cell.Offset:X8}"))
        };

        if (cell.GridX.HasValue)
        {
            identityFields.Add(new ReportField("Grid", ReportValue.String($"{cell.GridX}, {cell.GridY}")));
        }

        if (cell.WorldspaceFormId.HasValue)
        {
            identityFields.Add(new ReportField("Worldspace",
                ReportValue.FormId(cell.WorldspaceFormId.Value, resolver),
                $"0x{cell.WorldspaceFormId.Value:X8}"));
        }

        sections.Add(new ReportSection("Identity", identityFields));

        // Environment
        var envFields = new List<ReportField>();

        if (cell.WaterHeight.HasValue)
        {
            envFields.Add(new ReportField("Water Height",
                ReportValue.Float(WorldHeightNormalizer.NormalizeReportableHeight(cell.WaterHeight.Value))));
        }

        if (cell.EncounterZoneFormId.HasValue)
        {
            envFields.Add(new ReportField("Encounter Zone",
                ReportValue.FormId(cell.EncounterZoneFormId.Value, resolver),
                $"0x{cell.EncounterZoneFormId.Value:X8}"));
        }

        if (cell.MusicTypeFormId.HasValue)
        {
            envFields.Add(new ReportField("Music Type",
                ReportValue.FormId(cell.MusicTypeFormId.Value, resolver),
                $"0x{cell.MusicTypeFormId.Value:X8}"));
        }

        if (cell.AcousticSpaceFormId.HasValue)
        {
            envFields.Add(new ReportField("Acoustic Space",
                ReportValue.FormId(cell.AcousticSpaceFormId.Value, resolver),
                $"0x{cell.AcousticSpaceFormId.Value:X8}"));
        }

        if (cell.ImageSpaceFormId.HasValue)
        {
            envFields.Add(new ReportField("Image Space",
                ReportValue.FormId(cell.ImageSpaceFormId.Value, resolver),
                $"0x{cell.ImageSpaceFormId.Value:X8}"));
        }

        if (cell.LightingTemplateFormId.HasValue)
        {
            envFields.Add(new ReportField("Lighting Template",
                ReportValue.FormId(cell.LightingTemplateFormId.Value, resolver),
                $"0x{cell.LightingTemplateFormId.Value:X8}"));
        }

        if (cell.LightingTemplateInheritanceFlags.HasValue)
        {
            envFields.Add(new ReportField("Lighting Inheritance Flags",
                ReportValue.String($"0x{cell.LightingTemplateInheritanceFlags.Value:X8}")));
        }

        if (envFields.Count > 0)
        {
            sections.Add(new ReportSection("Environment", envFields));
        }

        // Heightmap
        if (cell.Heightmap != null)
        {
            sections.Add(new ReportSection("Heightmap",
            [
                new ReportField("Height Offset", ReportValue.Float(cell.Heightmap.HeightOffset))
            ]));
        }

        // Door Links
        var doorLinkItems = BuildDoorLinkList(cell.PlacedObjects, resolver, placedReferenceLocations);
        if (doorLinkItems.Count > 0)
        {
            sections.Add(new ReportSection("Door Links",
            [
                new ReportField("Linked Doors", ReportValue.List(doorLinkItems, $"{doorLinkItems.Count} doors"))
            ]));
        }

        // Placed Objects
        if (cell.PlacedObjects.Count > 0)
        {
            var objectItems = BuildPlacedObjectList(cell.PlacedObjects, resolver);
            sections.Add(new ReportSection("Placed Objects",
            [
                new ReportField("Objects", ReportValue.List(objectItems, $"{cell.PlacedObjects.Count} objects"))
            ]));
        }

        return new RecordReport("Cell", cell.FormId, cell.EditorId, cell.FullName, sections);
    }

    internal static RecordReport BuildWorldspaceReport(WorldspaceRecord wrld, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Identity
        var identityFields = new List<ReportField>
        {
            new("Endianness", ReportValue.String(wrld.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")),
            new("Offset", ReportValue.String($"0x{wrld.Offset:X8}"))
        };

        if (wrld.Flags.HasValue)
        {
            identityFields.Add(new ReportField("Flags", ReportValue.String($"0x{wrld.Flags.Value:X2}")));
        }

        if (wrld.ParentWorldspaceFormId.HasValue)
        {
            identityFields.Add(new ReportField("Parent",
                ReportValue.FormId(wrld.ParentWorldspaceFormId.Value, resolver),
                $"0x{wrld.ParentWorldspaceFormId.Value:X8}"));
        }

        if (wrld.ParentUseFlags.HasValue)
        {
            identityFields.Add(new ReportField("Parent Use Flags",
                ReportValue.String($"0x{wrld.ParentUseFlags.Value:X4}")));
        }

        sections.Add(new ReportSection("Identity", identityFields));

        // Environment
        var envFields = new List<ReportField>();

        if (wrld.ClimateFormId.HasValue)
        {
            envFields.Add(new ReportField("Climate",
                ReportValue.FormId(wrld.ClimateFormId.Value, resolver),
                $"0x{wrld.ClimateFormId.Value:X8}"));
        }

        if (wrld.WaterFormId.HasValue)
        {
            envFields.Add(new ReportField("Water",
                ReportValue.FormId(wrld.WaterFormId.Value, resolver),
                $"0x{wrld.WaterFormId.Value:X8}"));
        }

        if (wrld.EncounterZoneFormId.HasValue)
        {
            envFields.Add(new ReportField("Encounter Zone",
                ReportValue.FormId(wrld.EncounterZoneFormId.Value, resolver),
                $"0x{wrld.EncounterZoneFormId.Value:X8}"));
        }

        if (wrld.ImageSpaceFormId.HasValue)
        {
            envFields.Add(new ReportField("Image Space",
                ReportValue.FormId(wrld.ImageSpaceFormId.Value, resolver),
                $"0x{wrld.ImageSpaceFormId.Value:X8}"));
        }

        if (wrld.MusicTypeFormId.HasValue)
        {
            envFields.Add(new ReportField("Music Type",
                ReportValue.FormId(wrld.MusicTypeFormId.Value, resolver),
                $"0x{wrld.MusicTypeFormId.Value:X8}"));
        }

        if (envFields.Count > 0)
        {
            sections.Add(new ReportSection("Environment", envFields));
        }

        // Heights
        if (wrld.DefaultLandHeight.HasValue || wrld.DefaultWaterHeight.HasValue)
        {
            var heightFields = new List<ReportField>();
            if (wrld.DefaultLandHeight.HasValue)
            {
                heightFields.Add(
                    new ReportField("Default Land Height", ReportValue.Float(wrld.DefaultLandHeight.Value)));
            }

            if (wrld.DefaultWaterHeight.HasValue)
            {
                heightFields.Add(new ReportField("Default Water Height",
                    ReportValue.Float(WorldHeightNormalizer.NormalizeReportableHeight(wrld.DefaultWaterHeight.Value))));
            }

            sections.Add(new ReportSection("Heights", heightFields));
        }

        // Bounds
        if (wrld.BoundsMinX.HasValue)
        {
            sections.Add(new ReportSection("World Bounds",
            [
                new ReportField("Min", ReportValue.String($"({wrld.BoundsMinX:F0}, {wrld.BoundsMinY:F0})")),
                new ReportField("Max", ReportValue.String($"({wrld.BoundsMaxX:F0}, {wrld.BoundsMaxY:F0})"))
            ]));
        }

        // Map Data
        if (wrld.MapUsableWidth.HasValue)
        {
            var mapFields = new List<ReportField>
            {
                new("Usable Size", ReportValue.String($"{wrld.MapUsableWidth}x{wrld.MapUsableHeight}")),
                new("Cell Range", ReportValue.String(
                    $"[{wrld.MapNWCellX},{wrld.MapNWCellY}]-[{wrld.MapSECellX},{wrld.MapSECellY}]"))
            };

            if (wrld.MapOffsetScaleX.HasValue)
            {
                mapFields.Add(new ReportField("Offset Scale",
                    ReportValue.String($"({wrld.MapOffsetScaleX:F2}, {wrld.MapOffsetScaleY:F2})")));
            }

            if (wrld.MapOffsetZ.HasValue)
            {
                mapFields.Add(new ReportField("Offset Z", ReportValue.Float(wrld.MapOffsetZ.Value)));
            }

            sections.Add(new ReportSection("Map Data", mapFields));
        }

        // Cells summary
        if (wrld.Cells.Count > 0)
        {
            sections.Add(new ReportSection("Cells",
            [
                new ReportField("Count", ReportValue.Int(wrld.Cells.Count))
            ]));
        }

        return new RecordReport("Worldspace", wrld.FormId, wrld.EditorId, wrld.FullName, sections);
    }

    private static List<ReportValue> BuildPlacedObjectList(
        List<PlacedReference> placedObjects, FormIdResolver resolver)
    {
        var items = new List<ReportValue>();

        foreach (var obj in placedObjects.OrderBy(o => o.FormId))
        {
            var baseStr = !string.IsNullOrEmpty(obj.BaseEditorId)
                ? obj.BaseEditorId
                : resolver.GetEditorId(obj.BaseFormId)
                  ?? GeckReportHelpers.FormatFormId(obj.BaseFormId);
            var referenceEditorId = !string.IsNullOrEmpty(obj.EditorId)
                ? obj.EditorId
                : resolver.GetEditorId(obj.FormId);

            // Resolve a human-readable name distinct from the base editor ID:
            // - Map markers carry their map name on the placement itself.
            // - Other objects: prefer the base record's display (FULL) name when it
            //   actually adds information (i.e. differs from the editor ID).
            string? displayName = null;
            if (obj.IsMapMarker && !string.IsNullOrEmpty(obj.MarkerName))
            {
                displayName = obj.MarkerName;
            }
            else
            {
                var baseDisplay = resolver.GetDisplayName(obj.BaseFormId);
                if (!string.IsNullOrEmpty(baseDisplay)
                    && !string.Equals(baseDisplay, baseStr, StringComparison.Ordinal))
                {
                    displayName = baseDisplay;
                }
            }

            var fields = BuildPlacedObjectFields(obj, resolver, baseStr, displayName, referenceEditorId);

            items.Add(BuildPlacedObjectComposite(obj, fields, baseStr, displayName, referenceEditorId, resolver));
        }

        return items;
    }

    private static List<ReportValue> BuildDoorLinkList(
        IEnumerable<PlacedReference> placedObjects,
        FormIdResolver resolver,
        IReadOnlyDictionary<uint, PlacedReferenceLocation>? placedReferenceLocations)
    {
        var items = new List<ReportValue>();
        foreach (var sourceDoor in placedObjects
                     .Where(obj => obj.DestinationDoorFormId is > 0)
                     .OrderBy(obj => obj.FormId))
        {
            var destinationDoorFormId = sourceDoor.DestinationDoorFormId!.Value;
            if (placedReferenceLocations == null ||
                !placedReferenceLocations.TryGetValue(destinationDoorFormId, out var destination))
            {
                items.Add(ReportValue.FormId(destinationDoorFormId, resolver));
                continue;
            }

            var linkedDoor = destination.Ref;
            var baseStr = !string.IsNullOrEmpty(linkedDoor.BaseEditorId)
                ? linkedDoor.BaseEditorId
                : resolver.GetEditorId(linkedDoor.BaseFormId)
                  ?? GeckReportHelpers.FormatFormId(linkedDoor.BaseFormId);
            var referenceEditorId = !string.IsNullOrEmpty(linkedDoor.EditorId)
                ? linkedDoor.EditorId
                : resolver.GetEditorId(linkedDoor.FormId);
            string? displayName = null;
            var baseDisplay = resolver.GetDisplayName(linkedDoor.BaseFormId);
            if (!string.IsNullOrEmpty(baseDisplay)
                && !string.Equals(baseDisplay, baseStr, StringComparison.Ordinal))
            {
                displayName = baseDisplay;
            }

            var fields = BuildPlacedObjectFields(linkedDoor, resolver, baseStr, displayName, referenceEditorId);
            fields.Add(new ReportField("Containing Cell",
                ReportValue.FormId(destination.CellFormId, resolver),
                $"0x{destination.CellFormId:X8}"));
            fields.Add(new ReportField("Linked From",
                ReportValue.FormId(sourceDoor.FormId, resolver),
                $"0x{sourceDoor.FormId:X8}"));

            items.Add(BuildPlacedObjectComposite(linkedDoor, fields, baseStr, displayName, referenceEditorId, resolver));
        }

        return items;
    }

    private static List<ReportField> BuildPlacedObjectFields(
        PlacedReference obj,
        FormIdResolver resolver,
        string baseStr,
        string? displayName,
        string? referenceEditorId)
    {
        var fields = new List<ReportField>
        {
            new("FormID", ReportValue.FormId(obj.FormId, GeckReportHelpers.FormatFormId(obj.FormId)),
                $"0x{obj.FormId:X8}"),
            new("Base", ReportValue.String(baseStr)),
            new("Type", ReportValue.String(obj.RecordType)),
            new("Position", ReportValue.String($"({obj.X:F1}, {obj.Y:F1}, {obj.Z:F1})"))
        };

        if (!string.IsNullOrEmpty(referenceEditorId) &&
            !string.Equals(referenceEditorId, baseStr, StringComparison.Ordinal))
        {
            fields.Add(new ReportField("Reference Editor ID", ReportValue.String(referenceEditorId)));
        }

        if (displayName != null)
        {
            fields.Add(new ReportField("Name", ReportValue.String(displayName)));
        }

        var hasRotation = MathF.Abs(obj.RotX) > 0.001f || MathF.Abs(obj.RotY) > 0.001f ||
                          MathF.Abs(obj.RotZ) > 0.001f;
        if (hasRotation)
        {
            fields.Add(new ReportField("Rotation",
                ReportValue.String($"({obj.RotX:F3}, {obj.RotY:F3}, {obj.RotZ:F3})")));
        }

        if (Math.Abs(obj.Scale - 1.0f) > 0.01f)
        {
            fields.Add(new ReportField("Scale", ReportValue.Float(obj.Scale, "F2")));
        }

        if (obj.IsInitiallyDisabled)
        {
            fields.Add(new ReportField("Disabled", ReportValue.Bool(true)));
        }

        AddOptionalFormIdField(fields, "Links to", obj.DestinationCellFormId, resolver);
        AddOptionalFormIdField(fields, "Destination Door", obj.DestinationDoorFormId, resolver);
        AddOptionalFormIdField(fields, "Enable Parent", obj.EnableParentFormId, resolver);
        AddOptionalFormIdField(fields, "Linked Ref", obj.LinkedRefFormId, resolver);
        AddOptionalFormIdField(fields, "Linked Ref Keyword", obj.LinkedRefKeywordFormId, resolver);
        AddOptionalFormIdField(fields, "Origin Cell", obj.OriginCellFormId, resolver);

        if (obj.ModelPath != null)
        {
            fields.Add(new ReportField("Model", ReportValue.String(obj.ModelPath)));
        }

        if (obj.Bounds != null)
        {
            fields.Add(new ReportField("Bounds", ReportValue.String(
                $"[{obj.Bounds.X1},{obj.Bounds.Y1},{obj.Bounds.Z1}]-[{obj.Bounds.X2},{obj.Bounds.Y2},{obj.Bounds.Z2}]")));
        }

        return fields;
    }

    private static ReportValue.CompositeVal BuildPlacedObjectComposite(
        PlacedReference obj,
        List<ReportField> fields,
        string baseStr,
        string? displayName,
        string? referenceEditorId,
        FormIdResolver resolver)
    {
        var disabledTag = obj.IsInitiallyDisabled ? " [DISABLED]" : "";
        var nameTag = displayName != null ? $" \"{displayName}\"" : "";
        var referenceTag = !string.IsNullOrEmpty(referenceEditorId) &&
                           !string.Equals(referenceEditorId, baseStr, StringComparison.Ordinal)
            ? $"{referenceEditorId} ({baseStr})"
            : baseStr;
        var linksToTag = obj.DestinationCellFormId is > 0
            ? $" -> Links to: {resolver.FormatFull(obj.DestinationCellFormId.Value)}"
            : "";
        var display =
            $"{referenceTag}{nameTag} ({obj.RecordType}) [{GeckReportHelpers.FormatFormId(obj.FormId)}]{disabledTag}{linksToTag}";
        return new ReportValue.CompositeVal(fields, display);
    }

    private static void AddOptionalFormIdField(
        List<ReportField> fields,
        string label,
        uint? formId,
        FormIdResolver resolver)
    {
        if (formId is not > 0)
        {
            return;
        }

        fields.Add(new ReportField(label, ReportValue.FormId(formId.Value, resolver), $"0x{formId.Value:X8}"));
    }

    internal static void AppendPlacedObjects(
        StringBuilder sb, List<PlacedReference> placedObjects, FormIdResolver resolver)
    {
        if (placedObjects.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"Placed Objects ({placedObjects.Count}):");

        foreach (var obj in placedObjects.OrderBy(o => o.FormId))
        {
            var baseStr = !string.IsNullOrEmpty(obj.BaseEditorId)
                ? obj.BaseEditorId
                : resolver.GetBestName(obj.BaseFormId)
                  ?? GeckReportHelpers.FormatFormId(obj.BaseFormId);
            var referenceEditorId = !string.IsNullOrEmpty(obj.EditorId)
                ? obj.EditorId
                : resolver.GetEditorId(obj.FormId);
            var objectLabel = !string.IsNullOrEmpty(referenceEditorId) &&
                              !string.Equals(referenceEditorId, baseStr, StringComparison.Ordinal)
                ? $"{referenceEditorId} ({baseStr})"
                : baseStr;
            var disabledStr = obj.IsInitiallyDisabled ? " [DISABLED]" : "";
            sb.AppendLine(
                $"  - {objectLabel} ({obj.RecordType}) [{GeckReportHelpers.FormatFormId(obj.FormId)}]{disabledStr}");

            var scaleStr = Math.Abs(obj.Scale - 1.0f) > 0.01f ? $" scale={obj.Scale:F2}" : "";
            var hasRotation = MathF.Abs(obj.RotX) > 0.001f || MathF.Abs(obj.RotY) > 0.001f ||
                              MathF.Abs(obj.RotZ) > 0.001f;
            var rotStr = hasRotation ? $"  rot=({obj.RotX:F3}, {obj.RotY:F3}, {obj.RotZ:F3})" : "";
            sb.Append($"      at ({obj.X:F1}, {obj.Y:F1}, {obj.Z:F1}){rotStr}{scaleStr}");

            if (obj.Bounds != null)
            {
                sb.Append(
                    $" bounds=[{obj.Bounds.X1},{obj.Bounds.Y1},{obj.Bounds.Z1}]-[{obj.Bounds.X2},{obj.Bounds.Y2},{obj.Bounds.Z2}]");
            }

            if (obj.DestinationCellFormId is > 0)
            {
                sb.Append($" links to: {resolver.FormatFull(obj.DestinationCellFormId.Value)}");
            }

            sb.AppendLine();

            if (obj.DestinationDoorFormId is > 0)
            {
                sb.AppendLine($"      destination door: {resolver.FormatFull(obj.DestinationDoorFormId.Value)}");
            }

            if (obj.LinkedRefFormId is > 0)
            {
                sb.AppendLine($"      linked ref: {resolver.FormatFull(obj.LinkedRefFormId.Value)}");
            }

            if (obj.ModelPath != null)
            {
                sb.AppendLine($"      model: {obj.ModelPath}");
            }
        }
    }

    internal static void AppendCellsSection(StringBuilder sb, List<CellRecord> cells, FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Cells ({cells.Count})");

        // Separate interior and exterior cells
        var exteriorCells = cells.Where(c => !c.IsInterior && c.GridX.HasValue).ToList();
        var interiorCells = cells.Where(c => c.IsInterior || !c.GridX.HasValue).ToList();

        if (exteriorCells.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Exterior Cells ({exteriorCells.Count}):");

            // Group exterior cells by worldspace for clearer organization
            var byWorldspace = exteriorCells
                .GroupBy(c => c.WorldspaceFormId ?? 0)
                .OrderByDescending(g => g.Count())
                .ToList();

            foreach (var wsGroup in byWorldspace)
            {
                if (byWorldspace.Count > 1 || wsGroup.Key != 0)
                {
                    var wsName = wsGroup.Key != 0
                        ? resolver.GetBestName(wsGroup.Key) ?? GeckReportHelpers.FormatFormId(wsGroup.Key)
                        : "(Unlinked)";
                    var wsTitle = $"Worldspace: {wsName} ({wsGroup.Count()} cells)";
                    sb.AppendLine();
                    sb.AppendLine(new string('=', 80));
                    sb.AppendLine($"  {wsTitle}");
                    sb.AppendLine(new string('=', 80));
                }

                foreach (var cell in wsGroup.OrderBy(c => c.GridX).ThenBy(c => c.GridY))
                {
                    AppendExteriorCellDetail(sb, cell, resolver);
                }
            }
        }

        if (interiorCells.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Interior Cells ({interiorCells.Count}):");

            foreach (var cell in interiorCells.OrderBy(c => c.EditorId ?? ""))
            {
                AppendCellHeader(sb, "CELL (Interior)", cell.EditorId);

                sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(cell.FormId)}");
                sb.AppendLine($"Editor ID:      {cell.EditorId ?? "(none)"}");
                sb.AppendLine($"Display Name:   {cell.FullName ?? "(none)"}");
                sb.AppendLine($"Flags:          0x{cell.Flags:X2}");
                sb.AppendLine($"Endianness:     {(cell.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
                sb.AppendLine($"Offset:         0x{cell.Offset:X8}");

                AppendPlacedObjects(sb, cell.PlacedObjects, resolver);
            }
        }
    }

    private static void AppendCellHeader(StringBuilder sb, string recordType, string? editorId)
    {
        sb.AppendLine();
        sb.AppendLine(new string('-', GeckReportHelpers.SeparatorWidth));
        var title = string.IsNullOrEmpty(editorId) ? recordType : $"{recordType}: {editorId}";
        var padding = (GeckReportHelpers.SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string('-', GeckReportHelpers.SeparatorWidth));
    }

    private static void AppendExteriorCellDetail(StringBuilder sb, CellRecord cell, FormIdResolver resolver)
    {
        var gridStr = $"({cell.GridX}, {cell.GridY})";
        AppendCellHeader(sb, $"CELL {gridStr}", cell.EditorId);

        sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(cell.FormId)}");
        sb.AppendLine($"Editor ID:      {cell.EditorId ?? "(none)"}");
        sb.AppendLine($"Display Name:   {cell.FullName ?? "(none)"}");
        sb.AppendLine($"Grid:           {cell.GridX}, {cell.GridY}");
        sb.AppendLine($"Flags:          0x{cell.Flags:X2}");
        sb.AppendLine($"Has Water:      {cell.HasWater}");
        sb.AppendLine($"Endianness:     {(cell.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
        sb.AppendLine($"Offset:         0x{cell.Offset:X8}");

        if (cell.Heightmap != null)
        {
            sb.AppendLine();
            sb.AppendLine($"Heightmap:      Found (offset: {cell.Heightmap.HeightOffset:F1})");
        }

        AppendPlacedObjects(sb, cell.PlacedObjects, resolver);
    }

    /// <summary>
    ///     Generate a report for Cells only.
    /// </summary>
    public static string GenerateCellsReport(List<CellRecord> cells, FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendCellsSection(sb, cells, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendWorldspacesSection(StringBuilder sb, List<WorldspaceRecord> worldspaces,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Worldspaces ({worldspaces.Count})");

        foreach (var wrld in worldspaces.OrderBy(w => w.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "WRLD", wrld.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(wrld.FormId)}");
            sb.AppendLine($"Editor ID:      {wrld.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {wrld.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(wrld.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{wrld.Offset:X8}");

            if (wrld.ParentWorldspaceFormId.HasValue)
            {
                sb.AppendLine($"Parent:         {resolver.FormatFull(wrld.ParentWorldspaceFormId.Value)}");
            }

            if (wrld.ClimateFormId.HasValue)
            {
                sb.AppendLine($"Climate:        {resolver.FormatFull(wrld.ClimateFormId.Value)}");
            }

            if (wrld.WaterFormId.HasValue)
            {
                sb.AppendLine($"Water:          {resolver.FormatFull(wrld.WaterFormId.Value)}");
            }

            if (wrld.DefaultLandHeight.HasValue || wrld.DefaultWaterHeight.HasValue)
            {
                sb.AppendLine(
                    $"Default Heights: land={wrld.DefaultLandHeight?.ToString("F1") ?? "?"} water={wrld.DefaultWaterHeight?.ToString("F1") ?? "?"}");
            }

            if (wrld.BoundsMinX.HasValue)
            {
                sb.AppendLine(
                    $"World Bounds:   ({wrld.BoundsMinX:F0}, {wrld.BoundsMinY:F0}) to ({wrld.BoundsMaxX:F0}, {wrld.BoundsMaxY:F0})");
            }

            if (wrld.MapUsableWidth.HasValue)
            {
                sb.AppendLine(
                    $"Map Data:       {wrld.MapUsableWidth}x{wrld.MapUsableHeight} cells=[{wrld.MapNWCellX},{wrld.MapNWCellY}]-[{wrld.MapSECellX},{wrld.MapSECellY}]");
            }

            if (wrld.EncounterZoneFormId.HasValue)
            {
                sb.AppendLine(
                    $"Encounter Zone: {resolver.FormatFull(wrld.EncounterZoneFormId.Value)}");
            }

            if (wrld.Cells.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Cells:          {wrld.Cells.Count}");
            }
        }
    }

    /// <summary>
    ///     Generate a report for Worldspaces only.
    /// </summary>
    public static string GenerateWorldspacesReport(List<WorldspaceRecord> worldspaces,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendWorldspacesSection(sb, worldspaces, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendMapMarkersSection(StringBuilder sb, List<PlacedReference> markers,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Map Markers ({markers.Count})");
        sb.AppendLine();

        var byType = markers.Where(m => m.MarkerType != null)
            .GroupBy(m => m.MarkerType!.Value)
            .OrderByDescending(g => g.Count())
            .ToList();
        sb.AppendLine($"Total Map Markers: {markers.Count:N0}");
        if (byType.Count > 0)
        {
            sb.AppendLine("By Type:");
            foreach (var group in byType)
            {
                sb.AppendLine($"  {group.Key,-20} {group.Count(),5:N0}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(
            $"  {"Name",-32} {"Type",-18} {"X",10} {"Y",10} {"Z",8}  {"FormID"}");
        sb.AppendLine($"  {new string('\u2500', 76)}");

        foreach (var marker in markers
                     .OrderBy(m => m.MarkerType?.ToString() ?? "")
                     .ThenBy(m => m.MarkerName, StringComparer.OrdinalIgnoreCase))
        {
            var name = marker.MarkerName
                       ?? marker.BaseEditorId
                       ?? resolver.GetBestName(marker.BaseFormId)
                       ?? GeckReportHelpers.FormatFormId(marker.FormId);
            var typeName = marker.MarkerType?.ToString() ?? "(unknown)";
            sb.AppendLine(
                $"  {GeckReportHelpers.Truncate(name, 32),-32} {typeName,-18} {marker.X,10:F1} {marker.Y,10:F1} {marker.Z,8:F1}  [{GeckReportHelpers.FormatFormId(marker.FormId)}]");
        }

        sb.AppendLine();
    }

    public static string GenerateMapMarkersReport(List<PlacedReference> markers,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendMapMarkersSection(sb, markers, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    /// <summary>
    ///     Build a structured report for a single map marker placed reference, used by
    ///     <see cref="RecordTextFormatter.BuildReport" /> to include map markers in the
    ///     cross-dump comparison (<c>compare_mapmarker.html</c>).
    /// </summary>
    internal static RecordReport BuildMapMarkerReport(
        PlacedReference marker,
        FormIdResolver resolver,
        IReadOnlyDictionary<uint, PlacedReferenceLocation>? placedReferenceLocations = null)
    {
        var sections = new List<ReportSection>();

        var identity = new List<ReportField>
        {
            new("Endianness",
                ReportValue.String(marker.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")),
            new("Offset", ReportValue.String($"0x{marker.Offset:X8}"))
        };

        if (marker.MarkerType.HasValue)
        {
            identity.Add(new ReportField("Type",
                ReportValue.Int((int)marker.MarkerType.Value, marker.MarkerType.Value.ToString())));
        }

        identity.Add(new ReportField("Base",
            ReportValue.FormId(marker.BaseFormId, resolver),
            $"0x{marker.BaseFormId:X8}"));

        if (!string.IsNullOrEmpty(marker.BaseEditorId))
        {
            identity.Add(new ReportField("Base Editor ID", ReportValue.String(marker.BaseEditorId)));
        }

        sections.Add(new ReportSection("Identity", identity));

        sections.Add(new ReportSection("Position",
        [
            new ReportField("X", ReportValue.Float(marker.X, "F2")),
            new ReportField("Y", ReportValue.Float(marker.Y, "F2")),
            new ReportField("Z", ReportValue.Float(marker.Z, "F2"))
        ]));

        if (placedReferenceLocations != null &&
            placedReferenceLocations.TryGetValue(marker.FormId, out var location))
        {
            sections.Add(new ReportSection("Location",
            [
                new ReportField("Cell", ReportValue.FormId(location.CellFormId, resolver),
                    $"0x{location.CellFormId:X8}")
            ]));
        }

        var refFields = new List<ReportField>();
        if (marker.OwnerFormId.HasValue)
        {
            refFields.Add(new ReportField("Owner",
                ReportValue.FormId(marker.OwnerFormId.Value, resolver),
                $"0x{marker.OwnerFormId.Value:X8}"));
        }

        if (marker.EnableParentFormId.HasValue)
        {
            refFields.Add(new ReportField("Enable Parent",
                ReportValue.FormId(marker.EnableParentFormId.Value, resolver),
                $"0x{marker.EnableParentFormId.Value:X8}"));
        }

        if (refFields.Count > 0)
        {
            sections.Add(new ReportSection("References", refFields));
        }

        return new RecordReport(
            "MapMarker",
            marker.FormId,
            marker.EditorId,
            marker.MarkerName,
            sections);
    }

    public static string GeneratePersistentObjectsReport(List<CellRecord> cells,
        FormIdResolver? resolver = null)
    {
        return GeneratePlacedObjectsReport(cells, resolver ?? FormIdResolver.Empty,
            "Persistent Objects", static o => o.IsPersistent);
    }

    /// <summary>
    ///     Non-persistent placed-object report — mirror of
    ///     <see cref="GeneratePersistentObjectsReport" /> with the filter flipped.
    ///     Rows cover XESP-gated placements in the ESM and runtime refs observed in DMPs.
    /// </summary>
    public static string GenerateNonPersistentObjectsReport(List<CellRecord> cells,
        FormIdResolver? resolver = null)
    {
        return GeneratePlacedObjectsReport(cells, resolver ?? FormIdResolver.Empty,
            "Non-Persistent Objects", static o => !o.IsPersistent);
    }

    private static string GeneratePlacedObjectsReport(
        List<CellRecord> cells,
        FormIdResolver res,
        string sectionTitle,
        Func<PlacedReference, bool> filter)
    {
        var placed = cells
            .SelectMany(c => c.PlacedObjects.Where(filter)
                .Select(o => (Cell: c, Obj: o)))
            .ToList();

        var sb = new StringBuilder();
        GeckReportHelpers.AppendSectionHeader(sb, $"{sectionTitle} ({placed.Count})");
        sb.AppendLine();

        var grouped = placed
            .GroupBy(p => p.Obj.RecordType)
            .OrderBy(g => g.Key switch { "ACHR" => 0, "ACRE" => 1, _ => 2 });

        foreach (var group in grouped)
        {
            var typeName = group.Key switch
            {
                "ACHR" => "NPCs (ACHR)",
                "ACRE" => "Creatures (ACRE)",
                _ => $"Objects ({group.Key})"
            };

            sb.AppendLine($"  {typeName} ({group.Count()}):");
            sb.AppendLine();

            foreach (var (cell, obj) in group.OrderBy(p => p.Obj.BaseEditorId ?? ""))
            {
                var baseName = obj.BaseEditorId
                               ?? res.GetBestName(obj.BaseFormId)
                               ?? GeckReportHelpers.FormatFormId(obj.BaseFormId);
                var cellName = cell.EditorId ?? GeckReportHelpers.FormatFormId(cell.FormId);
                var scaleStr = Math.Abs(obj.Scale - 1.0f) > 0.01f ? $" scale={obj.Scale:F2}" : "";
                var disabledStr = obj.IsInitiallyDisabled ? " [DISABLED]" : "";

                sb.AppendLine($"    {GeckReportHelpers.FormatFormId(obj.FormId)}  {baseName}{disabledStr}");
                sb.AppendLine(
                    $"      pos=({obj.X:F1}, {obj.Y:F1}, {obj.Z:F1})  rot=({obj.RotX:F3}, {obj.RotY:F3}, {obj.RotZ:F3}){scaleStr}");
                sb.AppendLine($"      cell={cellName}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
