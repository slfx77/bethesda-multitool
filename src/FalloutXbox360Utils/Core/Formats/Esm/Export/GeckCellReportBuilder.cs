using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;
internal static class GeckCellReportBuilder
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
            var objectItems = BuildPlacedObjectList(cell.PlacedObjects, resolver, placedReferenceLocations);
            sections.Add(new ReportSection("Placed Objects",
            [
                new ReportField("Objects", ReportValue.List(objectItems, $"{cell.PlacedObjects.Count} objects"))
            ]));
        }

        return new RecordReport("Cell", cell.FormId, cell.EditorId, cell.FullName, sections);
    }

    private static List<ReportValue> BuildPlacedObjectList(
        List<PlacedReference> placedObjects,
        FormIdResolver resolver,
        IReadOnlyDictionary<uint, PlacedReferenceLocation>? placedReferenceLocations)
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

            var fields = BuildPlacedObjectFields(
                obj,
                resolver,
                baseStr,
                displayName,
                referenceEditorId,
                placedReferenceLocations: placedReferenceLocations);

            items.Add(BuildPlacedObjectComposite(
                obj,
                fields,
                baseStr,
                displayName,
                referenceEditorId,
                resolver,
                placedReferenceLocations));
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
            var baseStr = !string.IsNullOrEmpty(sourceDoor.BaseEditorId)
                ? sourceDoor.BaseEditorId
                : resolver.GetEditorId(sourceDoor.BaseFormId)
                  ?? GeckReportHelpers.FormatFormId(sourceDoor.BaseFormId);
            var referenceEditorId = !string.IsNullOrEmpty(sourceDoor.EditorId)
                ? sourceDoor.EditorId
                : resolver.GetEditorId(sourceDoor.FormId);
            string? displayName = null;
            var baseDisplay = resolver.GetDisplayName(sourceDoor.BaseFormId);
            if (!string.IsNullOrEmpty(baseDisplay)
                && !string.Equals(baseDisplay, baseStr, StringComparison.Ordinal))
            {
                displayName = baseDisplay;
            }

            var fields = BuildPlacedObjectFields(
                sourceDoor,
                resolver,
                baseStr,
                displayName,
                referenceEditorId,
                true,
                placedReferenceLocations);

            items.Add(BuildPlacedObjectComposite(
                sourceDoor,
                fields,
                baseStr,
                displayName,
                referenceEditorId,
                resolver,
                placedReferenceLocations));
        }

        return items;
    }

    private static List<ReportField> BuildPlacedObjectFields(
        PlacedReference obj,
        FormIdResolver resolver,
        string baseStr,
        string? displayName,
        string? referenceEditorId,
        bool includeRotation = false,
        IReadOnlyDictionary<uint, PlacedReferenceLocation>? placedReferenceLocations = null)
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
        if (hasRotation || includeRotation)
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

        if (TryResolveDestinationDoor(obj, resolver, placedReferenceLocations, out var destinationDoor))
        {
            fields.Add(new ReportField("Links to",
                ReportValue.FormId(destinationDoor.CellFormId, resolver),
                $"0x{destinationDoor.CellFormId:X8}"));
            fields.Add(new ReportField("Destination Door",
                ReportValue.FormId(destinationDoor.Ref.FormId, destinationDoor.Label),
                $"0x{destinationDoor.Ref.FormId:X8}"));
            fields.Add(new ReportField("Destination Door Cell",
                ReportValue.FormId(destinationDoor.CellFormId, resolver),
                $"0x{destinationDoor.CellFormId:X8}"));
        }
        else
        {
            AddOptionalConcreteFormIdField(fields, "Links to", obj.DestinationCellFormId, resolver);
            AddOptionalFormIdField(fields, "Destination Door", obj.DestinationDoorFormId, resolver);
        }

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
        FormIdResolver resolver,
        IReadOnlyDictionary<uint, PlacedReferenceLocation>? placedReferenceLocations = null)
    {
        var disabledTag = obj.IsInitiallyDisabled ? " [DISABLED]" : "";
        var label = BuildPlacedReferenceLabel(obj, baseStr, displayName, referenceEditorId);
        var linksToTag = "";
        if (TryResolveDestinationDoor(obj, resolver, placedReferenceLocations, out var destinationDoor))
        {
            linksToTag =
                $" -> Links to: {resolver.FormatFull(destinationDoor.CellFormId)} destination door: {destinationDoor.Label}";
        }
        else if (obj.DestinationCellFormId is > 0 && IsConcreteReportFormId(obj.DestinationCellFormId.Value))
        {
            linksToTag = $" -> Links to: {resolver.FormatFull(obj.DestinationCellFormId.Value)}";
        }

        var display =
            $"{label}{disabledTag}{linksToTag}";
        return new ReportValue.CompositeVal(fields, display);
    }

    private static bool TryResolveDestinationDoor(
        PlacedReference obj,
        FormIdResolver resolver,
        IReadOnlyDictionary<uint, PlacedReferenceLocation>? placedReferenceLocations,
        out DestinationDoorInfo destinationDoor)
    {
        destinationDoor = default;
        if (obj.DestinationDoorFormId is not > 0 ||
            placedReferenceLocations == null ||
            !placedReferenceLocations.TryGetValue(obj.DestinationDoorFormId.Value, out var destination))
        {
            return false;
        }

        var destinationBase = !string.IsNullOrEmpty(destination.Ref.BaseEditorId)
            ? destination.Ref.BaseEditorId
            : resolver.GetEditorId(destination.Ref.BaseFormId)
              ?? GeckReportHelpers.FormatFormId(destination.Ref.BaseFormId);
        var destinationReferenceEditorId = !string.IsNullOrEmpty(destination.Ref.EditorId)
            ? destination.Ref.EditorId
            : resolver.GetEditorId(destination.Ref.FormId);
        string? destinationDisplayName = null;
        var destinationBaseDisplay = resolver.GetDisplayName(destination.Ref.BaseFormId);
        if (!string.IsNullOrEmpty(destinationBaseDisplay)
            && !string.Equals(destinationBaseDisplay, destinationBase, StringComparison.Ordinal))
        {
            destinationDisplayName = destinationBaseDisplay;
        }

        destinationDoor = new DestinationDoorInfo(
            destination.Ref,
            destination.CellFormId,
            BuildPlacedReferenceLabel(
                destination.Ref,
                destinationBase,
                destinationDisplayName,
                destinationReferenceEditorId));
        return true;
    }

    private static string BuildPlacedReferenceLabel(
        PlacedReference obj,
        string baseStr,
        string? displayName,
        string? referenceEditorId)
    {
        var nameTag = displayName != null ? $" \"{displayName}\"" : "";
        var referenceTag = !string.IsNullOrEmpty(referenceEditorId) &&
                           !string.Equals(referenceEditorId, baseStr, StringComparison.Ordinal)
            ? $"{referenceEditorId} ({baseStr})"
            : baseStr;
        return $"{referenceTag}{nameTag} ({obj.RecordType}) [{GeckReportHelpers.FormatFormId(obj.FormId)}]";
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

    private static void AddOptionalConcreteFormIdField(
        List<ReportField> fields,
        string label,
        uint? formId,
        FormIdResolver resolver)
    {
        if (formId is > 0 && IsConcreteReportFormId(formId.Value))
        {
            fields.Add(new ReportField(label, ReportValue.FormId(formId.Value, resolver), $"0x{formId.Value:X8}"));
        }
    }

    private static bool IsConcreteReportFormId(uint formId)
    {
        return (formId & 0xFF000000) != 0xFE000000 &&
               (formId & 0xFF000000) != 0xFF000000;
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
                : resolver.GetEditorId(obj.BaseFormId)
                  ?? resolver.GetDisplayName(obj.BaseFormId)
                  ?? GeckReportHelpers.FormatFormId(obj.BaseFormId);
            var referenceEditorId = !string.IsNullOrEmpty(obj.EditorId)
                ? obj.EditorId
                : resolver.GetEditorId(obj.FormId);
            var displayName = !string.IsNullOrEmpty(obj.MarkerName)
                ? obj.MarkerName
                : resolver.GetDisplayName(obj.BaseFormId);
            var objectLabel = !string.IsNullOrEmpty(referenceEditorId) &&
                              !string.Equals(referenceEditorId, baseStr, StringComparison.Ordinal)
                ? $"{referenceEditorId} ({baseStr})"
                : baseStr;
            if (!string.IsNullOrEmpty(displayName) &&
                !string.Equals(displayName, baseStr, StringComparison.Ordinal) &&
                !string.Equals(displayName, referenceEditorId, StringComparison.Ordinal))
            {
                objectLabel = $"{objectLabel} \"{displayName}\"";
            }

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
                    var wsFormIdStr = wsGroup.Key != 0
                        ? $" [{GeckReportHelpers.FormatFormId(wsGroup.Key)}]"
                        : "";
                    var wsTitle = $"Worldspace: {wsName}{wsFormIdStr} ({wsGroup.Count()} cells)";
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

    /// <summary>
    ///     Generate one cell report per worldspace, plus a single "Interior" report for cells
    ///     that lack a worldspace link. Returned dictionary is keyed by a worldspace label
    ///     (EditorID / display name / formatted FormID, or "Interior" / "Unlinked").
    /// </summary>
    public static Dictionary<string, string> GenerateCellsReportsByWorldspace(
        List<CellRecord> cells,
        FormIdResolver? resolver = null)
    {
        resolver ??= FormIdResolver.Empty;
        var results = new Dictionary<string, string>(StringComparer.Ordinal);

        var exteriorByWs = cells
            .Where(c => !c.IsInterior && c.GridX.HasValue)
            .GroupBy(c => c.WorldspaceFormId ?? 0);

        foreach (var wsGroup in exteriorByWs)
        {
            var wsLabel = wsGroup.Key != 0
                ? resolver.GetBestName(wsGroup.Key) ?? GeckReportHelpers.FormatFormId(wsGroup.Key)
                : "Unlinked";
            results[wsLabel] = GenerateCellsReport([.. wsGroup], resolver);
        }

        var interiorCells = cells.Where(c => c.IsInterior || !c.GridX.HasValue).ToList();
        if (interiorCells.Count > 0)
        {
            results["Interior"] = GenerateCellsReport(interiorCells, resolver);
        }

        return results;
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

    private readonly record struct DestinationDoorInfo(
        PlacedReference Ref,
        uint CellFormId,
        string Label);
}
