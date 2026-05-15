using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;
internal static class GeckMapMarkerReportBuilder
{
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
}
