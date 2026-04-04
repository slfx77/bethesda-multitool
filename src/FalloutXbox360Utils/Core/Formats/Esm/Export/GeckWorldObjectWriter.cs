using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates GECK-style text reports for world-placeable object types:
///     Explosions, Projectiles, Sounds, Doors, Lights, Furniture, Activators, Statics.
/// </summary>
internal static class GeckWorldObjectWriter
{
    /// <summary>Build a structured explosion report from an <see cref="ExplosionRecord" />.</summary>
    internal static RecordReport BuildExplosionReport(ExplosionRecord expl, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        var statsFields = new List<ReportField>
        {
            new("Force", ReportValue.Float(expl.Force, "F1")),
            new("Damage", ReportValue.Float(expl.Damage, "F1")),
            new("Radius", ReportValue.Float(expl.Radius, "F1")),
            new("IS Radius", ReportValue.Float(expl.ISRadius, "F1"))
        };
        if (expl.Light != 0)
            statsFields.Add(new("Light", ReportValue.FormId(expl.Light, resolver), $"0x{expl.Light:X8}"));
        if (expl.Sound1 != 0)
            statsFields.Add(new("Sound 1", ReportValue.FormId(expl.Sound1, resolver), $"0x{expl.Sound1:X8}"));
        if (expl.Sound2 != 0)
            statsFields.Add(new("Sound 2", ReportValue.FormId(expl.Sound2, resolver), $"0x{expl.Sound2:X8}"));
        if (expl.ImpactDataSet != 0)
            statsFields.Add(new("Impact Data", ReportValue.FormId(expl.ImpactDataSet, resolver),
                $"0x{expl.ImpactDataSet:X8}"));
        if (expl.Enchantment != 0)
            statsFields.Add(new("Enchantment", ReportValue.FormId(expl.Enchantment, resolver),
                $"0x{expl.Enchantment:X8}"));
        if (!string.IsNullOrEmpty(expl.ModelPath))
            statsFields.Add(new("Model", ReportValue.String(expl.ModelPath)));
        if (expl.Flags != 0)
            statsFields.Add(new("Flags",
                ReportValue.String(FlagRegistry.DecodeFlagNamesWithHex(expl.Flags, FlagRegistry.ExplosionFlags))));
        sections.Add(new("Stats", statsFields));

        return new RecordReport("Explosion", expl.FormId, expl.EditorId, expl.FullName, sections);
    }

    /// <summary>Build a structured projectile report from a <see cref="ProjectileRecord" />.</summary>
    internal static RecordReport BuildProjectileReport(ProjectileRecord proj, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Identity
        sections.Add(new("Identity", [new("Type", ReportValue.String(proj.TypeName))]));

        // Physics
        var physFields = new List<ReportField>
        {
            new("Speed", ReportValue.Float(proj.Speed, "F1")),
            new("Gravity", ReportValue.Float(proj.Gravity, "F4")),
            new("Range", ReportValue.Float(proj.Range, "F1")),
            new("Impact Force", ReportValue.Float(proj.ImpactForce, "F1"))
        };
        if (proj.FadeDuration is not 0)
            physFields.Add(new("Fade Duration", ReportValue.Float(proj.FadeDuration, "F2")));
        if (proj.Timer is not 0)
            physFields.Add(new("Timer", ReportValue.Float(proj.Timer, "F2")));
        sections.Add(new("Physics", physFields));

        // Muzzle Flash
        if (proj.MuzzleFlashDuration is not 0 || proj.MuzzleFlashLight != 0)
        {
            var mfFields = new List<ReportField>();
            if (proj.MuzzleFlashDuration is not 0)
                mfFields.Add(new("Flash Duration", ReportValue.Float(proj.MuzzleFlashDuration, "F2")));
            if (proj.MuzzleFlashLight != 0)
                mfFields.Add(new("Flash Light", ReportValue.FormId(proj.MuzzleFlashLight, resolver),
                    $"0x{proj.MuzzleFlashLight:X8}"));
            sections.Add(new("Muzzle Flash", mfFields));
        }

        // References
        var refFields = new List<ReportField>();
        if (proj.Light != 0)
            refFields.Add(new("Light", ReportValue.FormId(proj.Light, resolver), $"0x{proj.Light:X8}"));
        if (proj.Explosion != 0)
            refFields.Add(new("Explosion", ReportValue.FormId(proj.Explosion, resolver),
                $"0x{proj.Explosion:X8}"));
        if (proj.Sound != 0)
            refFields.Add(new("Sound", ReportValue.FormId(proj.Sound, resolver), $"0x{proj.Sound:X8}"));
        if (!string.IsNullOrEmpty(proj.ModelPath))
            refFields.Add(new("Model", ReportValue.String(proj.ModelPath)));
        if (proj.Flags != 0)
            refFields.Add(new("Flags", ReportValue.String($"0x{proj.Flags:X4}")));
        if (refFields.Count > 0)
            sections.Add(new("References", refFields));

        return new RecordReport("Projectile", proj.FormId, proj.EditorId, proj.FullName, sections);
    }

    internal static void AppendExplosionsSection(StringBuilder sb, List<ExplosionRecord> explosions,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Explosions ({explosions.Count})");
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
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(expl.FormId)}");
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
                sb.AppendLine(
                    $"  Flags:       {FlagRegistry.DecodeFlagNamesWithHex(expl.Flags, FlagRegistry.ExplosionFlags)}");
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
        GeckReportHelpers.AppendSectionHeader(sb, $"Projectiles ({projectiles.Count})");
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
            sb.AppendLine($"  FormID:       {GeckReportHelpers.FormatFormId(proj.FormId)}");
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

    internal static void AppendSoundsSection(StringBuilder sb, List<SoundRecord> sounds)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Sounds ({sounds.Count})");
        sb.AppendLine();

        var withFile = sounds.Count(s => !string.IsNullOrEmpty(s.FileName));
        var looping = sounds.Count(s => (s.Flags & 0x0010) != 0);
        sb.AppendLine($"Total Sounds: {sounds.Count:N0}");
        sb.AppendLine($"  With File Path: {withFile:N0}");
        sb.AppendLine($"  Looping:        {looping:N0}");
        sb.AppendLine();

        foreach (var snd in sounds.OrderBy(s => s.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  SOUND: {snd.EditorId ?? "(none)"}");
            sb.AppendLine($"  FormID:         {GeckReportHelpers.FormatFormId(snd.FormId)}");

            if (!string.IsNullOrEmpty(snd.FileName))
            {
                sb.AppendLine($"  File:           {snd.FileName}");
            }

            sb.AppendLine($"  Min Atten Dist: {snd.MinAttenuationDistance * 5}");
            sb.AppendLine($"  Max Atten Dist: {snd.MaxAttenuationDistance * 5}");

            if (snd.StaticAttenuation != 0)
            {
                sb.AppendLine($"  Static Atten:   {snd.StaticAttenuation / 100.0:F2} dB");
            }

            if (snd.Flags != 0)
            {
                sb.AppendLine(
                    $"  Flags:          {FlagRegistry.DecodeFlagNamesWithHex(snd.Flags, FlagRegistry.SoundFlags)}");
            }

            if (snd.StartTime != 0 || snd.EndTime != 0)
            {
                sb.AppendLine($"  Play Hours:     {snd.StartTime}:00 \u2013 {snd.EndTime}:00");
            }

            if (snd.RandomPercentChance != 0)
            {
                sb.AppendLine($"  Random Chance:  {snd.RandomPercentChance}%");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateSoundsReport(List<SoundRecord> sounds)
    {
        var sb = new StringBuilder();
        AppendSoundsSection(sb, sounds);
        return sb.ToString();
    }

    // ── World Object Sections ────────────────────────────────────────────

    internal static void AppendDoorsSection(StringBuilder sb, List<DoorRecord> doors,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Doors ({doors.Count})");
        sb.AppendLine();

        foreach (var door in doors.OrderBy(d => d.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  DOOR: {door.EditorId ?? "(none)"} \u2014 {door.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(door.FormId)}");
            if (!string.IsNullOrEmpty(door.ModelPath))
            {
                sb.AppendLine($"  Model:       {door.ModelPath}");
            }

            if (door.Flags != 0)
            {
                sb.AppendLine($"  Flags:       0x{door.Flags:X2}");
            }

            if (door.OpenSoundFormId.HasValue)
            {
                sb.AppendLine($"  Open Sound:  {resolver.FormatFull(door.OpenSoundFormId.Value)}");
            }

            if (door.CloseSoundFormId.HasValue)
            {
                sb.AppendLine($"  Close Sound: {resolver.FormatFull(door.CloseSoundFormId.Value)}");
            }

            if (door.LoopSoundFormId.HasValue)
            {
                sb.AppendLine($"  Loop Sound:  {resolver.FormatFull(door.LoopSoundFormId.Value)}");
            }

            if (door.Script.HasValue)
            {
                sb.AppendLine($"  Script:      {resolver.FormatFull(door.Script.Value)}");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateDoorsReport(List<DoorRecord> doors, FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendDoorsSection(sb, doors, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendLightsSection(StringBuilder sb, List<LightRecord> lights)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Lights ({lights.Count})");
        sb.AppendLine();

        foreach (var light in lights.OrderBy(l => l.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            var r = (byte)(light.Color & 0xFF);
            var g = (byte)((light.Color >> 8) & 0xFF);
            var b = (byte)((light.Color >> 16) & 0xFF);

            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  LIGHT: {light.EditorId ?? "(none)"} \u2014 {light.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(light.FormId)}");
            sb.AppendLine($"  Radius:      {light.Radius}");
            sb.AppendLine($"  Color:       #{r:X2}{g:X2}{b:X2} ({r}, {g}, {b})");
            sb.AppendLine($"  Duration:    {(light.Duration == 0 ? "Infinite" : $"{light.Duration}s")}");
            if (light.FalloffExponent != 0)
            {
                sb.AppendLine($"  Falloff:     {light.FalloffExponent:F2}");
            }

            if (light.FOV != 0)
            {
                sb.AppendLine($"  FOV:         {light.FOV:F1}\u00B0");
            }

            if (light.Flags != 0)
            {
                sb.AppendLine($"  Flags:       0x{light.Flags:X8}");
            }

            if (light.Value != 0 || light.Weight != 0)
            {
                sb.AppendLine($"  Value:       {light.Value}");
                sb.AppendLine($"  Weight:      {light.Weight:F1}");
            }

            if (!string.IsNullOrEmpty(light.ModelPath))
            {
                sb.AppendLine($"  Model:       {light.ModelPath}");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateLightsReport(List<LightRecord> lights)
    {
        var sb = new StringBuilder();
        AppendLightsSection(sb, lights);
        return sb.ToString();
    }

    internal static void AppendFurnitureSection(StringBuilder sb, List<FurnitureRecord> furniture,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Furniture ({furniture.Count})");
        sb.AppendLine();

        foreach (var furn in furniture.OrderBy(f => f.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  FURN: {furn.EditorId ?? "(none)"} \u2014 {furn.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(furn.FormId)}");
            if (!string.IsNullOrEmpty(furn.ModelPath))
            {
                sb.AppendLine($"  Model:       {furn.ModelPath}");
            }

            if (furn.MarkerFlags != 0)
            {
                sb.AppendLine($"  Markers:     0x{furn.MarkerFlags:X8}");
            }

            if (furn.Script.HasValue)
            {
                sb.AppendLine($"  Script:      {resolver.FormatFull(furn.Script.Value)}");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateFurnitureReport(List<FurnitureRecord> furniture,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendFurnitureSection(sb, furniture, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendActivatorsSection(StringBuilder sb, List<ActivatorRecord> activators,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Activators ({activators.Count})");
        sb.AppendLine();

        foreach (var acti in activators.OrderBy(a => a.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  ACTI: {acti.EditorId ?? "(none)"} \u2014 {acti.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(acti.FormId)}");
            if (!string.IsNullOrEmpty(acti.ModelPath))
            {
                sb.AppendLine($"  Model:       {acti.ModelPath}");
            }

            if (acti.ActivationSoundFormId.HasValue)
            {
                sb.AppendLine($"  Activ Sound: {resolver.FormatFull(acti.ActivationSoundFormId.Value)}");
            }

            if (acti.RadioStationFormId.HasValue)
            {
                sb.AppendLine($"  Radio:       {resolver.FormatFull(acti.RadioStationFormId.Value)}");
            }

            if (acti.WaterTypeFormId.HasValue)
            {
                sb.AppendLine($"  Water Type:  {resolver.FormatFull(acti.WaterTypeFormId.Value)}");
            }

            if (acti.Script.HasValue)
            {
                sb.AppendLine($"  Script:      {resolver.FormatFull(acti.Script.Value)}");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateActivatorsReport(List<ActivatorRecord> activators,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendActivatorsSection(sb, activators, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendStaticsSection(StringBuilder sb, List<StaticRecord> statics)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Statics ({statics.Count})");
        sb.AppendLine();

        foreach (var stat in statics.OrderBy(s => s.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  STAT: {stat.EditorId ?? "(none)"}");
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(stat.FormId)}");
            if (!string.IsNullOrEmpty(stat.ModelPath))
            {
                sb.AppendLine($"  Model:       {stat.ModelPath}");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateStaticsReport(List<StaticRecord> statics)
    {
        var sb = new StringBuilder();
        AppendStaticsSection(sb, statics);
        return sb.ToString();
    }
}
