using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Generates GECK-style text reports for Cell, Worldspace, Map Marker, Explosion, and Projectile records.</summary>
internal static class GeckWorldWriter
{
    internal static void AppendPlacedObjects(
        StringBuilder sb, List<PlacedReference> placedObjects, FormIdResolver resolver)
    {
        if (placedObjects.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"Placed Objects ({placedObjects.Count}):");

        foreach (var obj in placedObjects)
        {
            var baseStr = !string.IsNullOrEmpty(obj.BaseEditorId)
                ? obj.BaseEditorId
                : resolver.GetBestName(obj.BaseFormId)
                  ?? GeckReportGenerator.FormatFormId(obj.BaseFormId);
            var scaleStr = Math.Abs(obj.Scale - 1.0f) > 0.01f ? $" scale={obj.Scale:F2}" : "";
            sb.AppendLine($"  - {baseStr} ({obj.RecordType})");
            sb.Append($"      at ({obj.X:F1}, {obj.Y:F1}, {obj.Z:F1}){scaleStr}");

            if (obj.Bounds != null)
            {
                sb.Append($" bounds=[{obj.Bounds.X1},{obj.Bounds.Y1},{obj.Bounds.Z1}]-[{obj.Bounds.X2},{obj.Bounds.Y2},{obj.Bounds.Z2}]");
            }

            sb.AppendLine();

            if (obj.ModelPath != null)
            {
                sb.AppendLine($"      model: {obj.ModelPath}");
            }
        }
    }

    internal static void AppendCellsSection(StringBuilder sb, List<CellRecord> cells, FormIdResolver resolver)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Cells ({cells.Count})");

        // Separate interior and exterior cells
        var exteriorCells = cells.Where(c => !c.IsInterior && c.GridX.HasValue).ToList();
        var interiorCells = cells.Where(c => c.IsInterior || !c.GridX.HasValue).ToList();

        if (exteriorCells.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Exterior Cells ({exteriorCells.Count}):");

            foreach (var cell in exteriorCells.OrderBy(c => c.GridX).ThenBy(c => c.GridY))
            {
                var gridStr = $"({cell.GridX}, {cell.GridY})";
                GeckReportGenerator.AppendRecordHeader(sb, $"CELL {gridStr}", cell.EditorId);

                sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(cell.FormId)}");
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
        }

        if (interiorCells.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Interior Cells ({interiorCells.Count}):");

            foreach (var cell in interiorCells.OrderBy(c => c.EditorId ?? ""))
            {
                GeckReportGenerator.AppendRecordHeader(sb, "CELL (Interior)", cell.EditorId);

                sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(cell.FormId)}");
                sb.AppendLine($"Editor ID:      {cell.EditorId ?? "(none)"}");
                sb.AppendLine($"Display Name:   {cell.FullName ?? "(none)"}");
                sb.AppendLine($"Flags:          0x{cell.Flags:X2}");
                sb.AppendLine($"Endianness:     {(cell.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
                sb.AppendLine($"Offset:         0x{cell.Offset:X8}");

                AppendPlacedObjects(sb, cell.PlacedObjects, resolver);
            }
        }
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
        GeckReportGenerator.AppendSectionHeader(sb, $"Worldspaces ({worldspaces.Count})");

        foreach (var wrld in worldspaces.OrderBy(w => w.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "WRLD", wrld.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(wrld.FormId)}");
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
        GeckReportGenerator.AppendSectionHeader(sb, $"Map Markers ({markers.Count})");
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
                       ?? GeckReportGenerator.FormatFormId(marker.FormId);
            var typeName = marker.MarkerType?.ToString() ?? "(unknown)";
            sb.AppendLine(
                $"  {GeckReportGenerator.Truncate(name, 32),-32} {typeName,-18} {marker.X,10:F1} {marker.Y,10:F1} {marker.Z,8:F1}  [{GeckReportGenerator.FormatFormId(marker.FormId)}]");
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

    internal static void AppendExplosionsSection(StringBuilder sb, List<ExplosionRecord> explosions,
        FormIdResolver resolver)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Explosions ({explosions.Count})");
        sb.AppendLine();

        sb.AppendLine($"Total Explosions: {explosions.Count:N0}");
        var withEnchantment = explosions.Count(e => e.Enchantment != 0);
        sb.AppendLine($"  With Enchantment: {withEnchantment:N0}");
        if (explosions.Count > 0)
        {
            sb.AppendLine(
                $"  Damage Range: {explosions.Min(e => e.Damage):F0} \u2013 {explosions.Max(e => e.Damage):F0}");
            sb.AppendLine(
                $"  Radius Range: {explosions.Min(e => e.Radius):F0} \u2013 {explosions.Max(e => e.Radius):F0}");
        }

        sb.AppendLine();

        foreach (var expl in explosions.OrderBy(e => e.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  EXPLOSION: {expl.EditorId ?? "(none)"} \u2014 {expl.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {GeckReportGenerator.FormatFormId(expl.FormId)}");
            sb.AppendLine($"  \u2500\u2500 Stats {new string('\u2500', 70)}");
            sb.AppendLine($"  Force:       {expl.Force:F1}");
            sb.AppendLine($"  Damage:      {expl.Damage:F1}");
            sb.AppendLine($"  Radius:      {expl.Radius:F1}");
            sb.AppendLine($"  IS Radius:   {expl.ISRadius:F1}");
            if (expl.Light != 0)
            {
                sb.AppendLine($"  Light:       {resolver.FormatFull(expl.Light)}");
            }

            if (expl.Sound1 != 0)
            {
                sb.AppendLine($"  Sound 1:     {resolver.FormatFull(expl.Sound1)}");
            }

            if (expl.Sound2 != 0)
            {
                sb.AppendLine($"  Sound 2:     {resolver.FormatFull(expl.Sound2)}");
            }

            if (expl.ImpactDataSet != 0)
            {
                sb.AppendLine($"  Impact Data: {resolver.FormatFull(expl.ImpactDataSet)}");
            }

            if (expl.Enchantment != 0)
            {
                sb.AppendLine($"  Enchantment: {resolver.FormatFull(expl.Enchantment)}");
            }

            if (!string.IsNullOrEmpty(expl.ModelPath))
            {
                sb.AppendLine($"  Model:       {expl.ModelPath}");
            }

            if (expl.Flags != 0)
            {
                sb.AppendLine($"  Flags:       0x{expl.Flags:X8}");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateExplosionsReport(List<ExplosionRecord> explosions,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendExplosionsSection(sb, explosions, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendProjectilesSection(StringBuilder sb, List<ProjectileRecord> projectiles,
        FormIdResolver resolver)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Projectiles ({projectiles.Count})");
        sb.AppendLine();

        var byType = projectiles.GroupBy(p => p.TypeName).OrderByDescending(g => g.Count()).ToList();
        sb.AppendLine($"Total Projectiles: {projectiles.Count:N0}");
        sb.AppendLine("By Type:");
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key,-20} {group.Count(),5:N0}");
        }

        if (projectiles.Count > 0)
        {
            sb.AppendLine(
                $"  Speed Range: {projectiles.Min(p => p.Speed):F0} \u2013 {projectiles.Max(p => p.Speed):F0}");
        }

        sb.AppendLine();

        foreach (var proj in projectiles.OrderBy(p => p.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  PROJECTILE: {proj.EditorId ?? "(none)"} \u2014 {proj.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:       {GeckReportGenerator.FormatFormId(proj.FormId)}");
            sb.AppendLine($"  Type:         {proj.TypeName}");
            sb.AppendLine($"  \u2500\u2500 Physics {new string('\u2500', 68)}");
            sb.AppendLine($"  Speed:        {proj.Speed:F1}");
            sb.AppendLine($"  Gravity:      {proj.Gravity:F4}");
            sb.AppendLine($"  Range:        {proj.Range:F1}");
            sb.AppendLine($"  Impact Force: {proj.ImpactForce:F1}");
            if (proj.FadeDuration is not 0)
            {
                sb.AppendLine($"  Fade Duration: {proj.FadeDuration:F2}");
            }

            if (proj.Timer is not 0)
            {
                sb.AppendLine($"  Timer:        {proj.Timer:F2}");
            }

            if (proj.MuzzleFlashDuration is not 0 || proj.MuzzleFlashLight != 0)
            {
                sb.AppendLine($"  \u2500\u2500 Muzzle Flash {new string('\u2500', 63)}");
                if (proj.MuzzleFlashDuration is not 0)
                {
                    sb.AppendLine($"  Flash Duration: {proj.MuzzleFlashDuration:F2}");
                }

                if (proj.MuzzleFlashLight != 0)
                {
                    sb.AppendLine($"  Flash Light:  {resolver.FormatFull(proj.MuzzleFlashLight)}");
                }
            }

            if (proj.Light != 0)
            {
                sb.AppendLine($"  Light:        {resolver.FormatFull(proj.Light)}");
            }

            if (proj.Explosion != 0)
            {
                sb.AppendLine($"  Explosion:    {resolver.FormatFull(proj.Explosion)}");
            }

            if (proj.Sound != 0)
            {
                sb.AppendLine($"  Sound:        {resolver.FormatFull(proj.Sound)}");
            }

            if (!string.IsNullOrEmpty(proj.ModelPath))
            {
                sb.AppendLine($"  Model:        {proj.ModelPath}");
            }

            if (proj.Flags != 0)
            {
                sb.AppendLine($"  Flags:        0x{proj.Flags:X4}");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateProjectilesReport(List<ProjectileRecord> projectiles,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendProjectilesSection(sb, projectiles, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }
}
