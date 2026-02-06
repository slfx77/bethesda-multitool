using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Export;

public static partial class GeckReportGenerator
{
    #region PlacedObjects Methods

    private static void AppendPlacedObjects(StringBuilder sb, List<PlacedReference> placedObjects)
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
                : FormatFormId(obj.BaseFormId);
            var scaleStr = Math.Abs(obj.Scale - 1.0f) > 0.01f ? $" scale={obj.Scale:F2}" : "";
            sb.AppendLine($"  - {baseStr} ({obj.RecordType})");
            sb.AppendLine($"      at ({obj.X:F1}, {obj.Y:F1}, {obj.Z:F1}){scaleStr}");
        }
    }

    #endregion

    #region Cell Methods

    private static void AppendCellsSection(StringBuilder sb, List<ReconstructedCell> cells)
    {
        AppendSectionHeader(sb, $"Cells ({cells.Count})");

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
                AppendRecordHeader(sb, $"CELL {gridStr}", cell.EditorId);

                sb.AppendLine($"FormID:         {FormatFormId(cell.FormId)}");
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

                AppendPlacedObjects(sb, cell.PlacedObjects);
            }
        }

        if (interiorCells.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Interior Cells ({interiorCells.Count}):");

            foreach (var cell in interiorCells.OrderBy(c => c.EditorId ?? ""))
            {
                AppendRecordHeader(sb, "CELL (Interior)", cell.EditorId);

                sb.AppendLine($"FormID:         {FormatFormId(cell.FormId)}");
                sb.AppendLine($"Editor ID:      {cell.EditorId ?? "(none)"}");
                sb.AppendLine($"Display Name:   {cell.FullName ?? "(none)"}");
                sb.AppendLine($"Flags:          0x{cell.Flags:X2}");
                sb.AppendLine($"Endianness:     {(cell.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
                sb.AppendLine($"Offset:         0x{cell.Offset:X8}");

                AppendPlacedObjects(sb, cell.PlacedObjects);
            }
        }
    }

    /// <summary>
    ///     Generate a report for Cells only.
    /// </summary>
    public static string GenerateCellsReport(List<ReconstructedCell> cells, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendCellsSection(sb, cells);
        return sb.ToString();
    }

    #endregion

    #region Worldspace Methods

    private static void AppendWorldspacesSection(StringBuilder sb, List<ReconstructedWorldspace> worldspaces,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Worldspaces ({worldspaces.Count})");

        foreach (var wrld in worldspaces.OrderBy(w => w.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "WRLD", wrld.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(wrld.FormId)}");
            sb.AppendLine($"Editor ID:      {wrld.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {wrld.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(wrld.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{wrld.Offset:X8}");

            if (wrld.ParentWorldspaceFormId.HasValue)
            {
                sb.AppendLine($"Parent:         {FormatFormIdWithName(wrld.ParentWorldspaceFormId.Value, lookup)}");
            }

            if (wrld.ClimateFormId.HasValue)
            {
                sb.AppendLine($"Climate:        {FormatFormIdWithName(wrld.ClimateFormId.Value, lookup)}");
            }

            if (wrld.WaterFormId.HasValue)
            {
                sb.AppendLine($"Water:          {FormatFormIdWithName(wrld.WaterFormId.Value, lookup)}");
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
    public static string GenerateWorldspacesReport(List<ReconstructedWorldspace> worldspaces,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendWorldspacesSection(sb, worldspaces, lookup ?? []);
        return sb.ToString();
    }

    #endregion

    #region MapMarker Methods

    private static void AppendMapMarkersSection(StringBuilder sb, List<PlacedReference> markers)
    {
        AppendSectionHeader(sb, $"Map Markers ({markers.Count})");
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
            var name = marker.MarkerName ?? marker.BaseEditorId ?? FormatFormId(marker.FormId);
            var typeName = marker.MarkerType?.ToString() ?? "(unknown)";
            sb.AppendLine(
                $"  {Truncate(name, 32),-32} {typeName,-18} {marker.X,10:F1} {marker.Y,10:F1} {marker.Z,8:F1}  [{FormatFormId(marker.FormId)}]");
        }

        sb.AppendLine();
    }

    public static string GenerateMapMarkersReport(List<PlacedReference> markers,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendMapMarkersSection(sb, markers);
        return sb.ToString();
    }

    #endregion

    #region Explosion Methods

    private static void AppendExplosionsSection(StringBuilder sb, List<ReconstructedExplosion> explosions,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Explosions ({explosions.Count})");
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
            sb.AppendLine($"  FormID:      {FormatFormId(expl.FormId)}");
            sb.AppendLine($"  \u2500\u2500 Stats {new string('\u2500', 70)}");
            sb.AppendLine($"  Force:       {expl.Force:F1}");
            sb.AppendLine($"  Damage:      {expl.Damage:F1}");
            sb.AppendLine($"  Radius:      {expl.Radius:F1}");
            sb.AppendLine($"  IS Radius:   {expl.ISRadius:F1}");
            if (expl.Light != 0)
            {
                sb.AppendLine($"  Light:       {FormatFormIdWithName(expl.Light, lookup)}");
            }

            if (expl.Sound1 != 0)
            {
                sb.AppendLine($"  Sound 1:     {FormatFormIdWithName(expl.Sound1, lookup)}");
            }

            if (expl.Sound2 != 0)
            {
                sb.AppendLine($"  Sound 2:     {FormatFormIdWithName(expl.Sound2, lookup)}");
            }

            if (expl.ImpactDataSet != 0)
            {
                sb.AppendLine($"  Impact Data: {FormatFormIdWithName(expl.ImpactDataSet, lookup)}");
            }

            if (expl.Enchantment != 0)
            {
                sb.AppendLine($"  Enchantment: {FormatFormIdWithName(expl.Enchantment, lookup)}");
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

    public static string GenerateExplosionsReport(List<ReconstructedExplosion> explosions,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendExplosionsSection(sb, explosions, lookup ?? []);
        return sb.ToString();
    }

    #endregion

    #region Projectile Methods

    private static void AppendProjectilesSection(StringBuilder sb, List<ReconstructedProjectile> projectiles,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Projectiles ({projectiles.Count})");
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
            sb.AppendLine($"  FormID:       {FormatFormId(proj.FormId)}");
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
                    sb.AppendLine($"  Flash Light:  {FormatFormIdWithName(proj.MuzzleFlashLight, lookup)}");
                }
            }

            if (proj.Light != 0)
            {
                sb.AppendLine($"  Light:        {FormatFormIdWithName(proj.Light, lookup)}");
            }

            if (proj.Explosion != 0)
            {
                sb.AppendLine($"  Explosion:    {FormatFormIdWithName(proj.Explosion, lookup)}");
            }

            if (proj.Sound != 0)
            {
                sb.AppendLine($"  Sound:        {FormatFormIdWithName(proj.Sound, lookup)}");
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

    public static string GenerateProjectilesReport(List<ReconstructedProjectile> projectiles,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendProjectilesSection(sb, projectiles, lookup ?? []);
        return sb.ToString();
    }

    #endregion
}
