using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;
internal static class GeckWorldspaceReportBuilder
{
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
}
