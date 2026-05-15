using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Compatibility facade for GECK-style Cell, Worldspace, and Map Marker reports.</summary>
internal static class GeckWorldWriter
{
    internal static RecordReport BuildCellReport(
        CellRecord cell,
        FormIdResolver resolver,
        IReadOnlyDictionary<uint, PlacedReferenceLocation>? placedReferenceLocations = null)
    {
        return GeckCellReportBuilder.BuildCellReport(cell, resolver, placedReferenceLocations);
    }

    internal static RecordReport BuildWorldspaceReport(WorldspaceRecord wrld, FormIdResolver resolver)
    {
        return GeckWorldspaceReportBuilder.BuildWorldspaceReport(wrld, resolver);
    }

    internal static void AppendPlacedObjects(
        StringBuilder sb,
        List<PlacedReference> placedObjects,
        FormIdResolver resolver)
    {
        GeckCellReportBuilder.AppendPlacedObjects(sb, placedObjects, resolver);
    }

    internal static void AppendCellsSection(StringBuilder sb, List<CellRecord> cells, FormIdResolver resolver)
    {
        GeckCellReportBuilder.AppendCellsSection(sb, cells, resolver);
    }

    public static string GenerateCellsReport(List<CellRecord> cells, FormIdResolver? resolver = null)
    {
        return GeckCellReportBuilder.GenerateCellsReport(cells, resolver);
    }

    public static Dictionary<string, string> GenerateCellsReportsByWorldspace(
        List<CellRecord> cells,
        FormIdResolver? resolver = null)
    {
        return GeckCellReportBuilder.GenerateCellsReportsByWorldspace(cells, resolver);
    }

    internal static void AppendWorldspacesSection(
        StringBuilder sb,
        List<WorldspaceRecord> worldspaces,
        FormIdResolver resolver)
    {
        GeckWorldspaceReportBuilder.AppendWorldspacesSection(sb, worldspaces, resolver);
    }

    public static string GenerateWorldspacesReport(
        List<WorldspaceRecord> worldspaces,
        FormIdResolver? resolver = null)
    {
        return GeckWorldspaceReportBuilder.GenerateWorldspacesReport(worldspaces, resolver);
    }

    internal static void AppendMapMarkersSection(
        StringBuilder sb,
        List<PlacedReference> markers,
        FormIdResolver resolver)
    {
        GeckMapMarkerReportBuilder.AppendMapMarkersSection(sb, markers, resolver);
    }

    public static string GenerateMapMarkersReport(List<PlacedReference> markers, FormIdResolver? resolver = null)
    {
        return GeckMapMarkerReportBuilder.GenerateMapMarkersReport(markers, resolver);
    }

    internal static RecordReport BuildMapMarkerReport(
        PlacedReference marker,
        FormIdResolver resolver,
        IReadOnlyDictionary<uint, PlacedReferenceLocation>? placedReferenceLocations = null)
    {
        return GeckMapMarkerReportBuilder.BuildMapMarkerReport(marker, resolver, placedReferenceLocations);
    }

    public static string GeneratePersistentObjectsReport(List<CellRecord> cells, FormIdResolver? resolver = null)
    {
        return GeckCellReportBuilder.GeneratePersistentObjectsReport(cells, resolver);
    }

    public static string GenerateNonPersistentObjectsReport(List<CellRecord> cells, FormIdResolver? resolver = null)
    {
        return GeckCellReportBuilder.GenerateNonPersistentObjectsReport(cells, resolver);
    }
}
