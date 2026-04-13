using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates GECK-style text reports for Weapon, Armor, Ammo, Consumable, Misc Item, Key, and Container records.
///     Structured Build*Report methods live here; text-format Append*Section methods are in
///     <see cref="GeckItemTextWriter" />.
///     Detailed weapon/container reports, recipes, and weapon mods are in GeckItemDetailWriter.
/// </summary>
internal static class GeckItemWriter
{
    // ── Structured Build*Report methods ──

    internal static RecordReport BuildArmorReport(ArmorRecord item, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Identity
        sections.Add(new ReportSection("Identity",
        [
            new ReportField("Equip", ReportValue.String(item.EquipmentTypeName))
        ]));

        // Stats
        var statsFields = new List<ReportField>
        {
            new("DT", ReportValue.FloatDisplay(item.DamageThreshold, $"{item.DamageThreshold:F1}")),
            new("DR", ReportValue.Int(item.DamageResistance)),
            new("Value", ReportValue.Int(item.Value, $"{item.Value} caps")),
            new("Weight", ReportValue.Float(item.Weight)),
            new("Health", ReportValue.Int(item.Health))
        };

        if (item.BipedFlags != 0)
        {
            statsFields.Add(new ReportField("Biped Slots", ReportValue.String($"0x{item.BipedFlags:X8}")));
        }

        if (!string.IsNullOrEmpty(item.ModelPath))
        {
            statsFields.Add(new ReportField("Model", ReportValue.String(item.ModelPath)));
        }

        sections.Add(new ReportSection("Stats", statsFields));

        return new RecordReport("Armor", item.FormId, item.EditorId, item.FullName, sections);
    }

    internal static RecordReport BuildAmmoReport(AmmoRecord item, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Stats
        var statsFields = new List<ReportField>
        {
            new("Speed", ReportValue.FloatDisplay(item.Speed, $"{item.Speed:F1}")),
            new("Value", ReportValue.Int((int)item.Value, $"{item.Value} caps")),
            new("Clip Rounds", ReportValue.Int(item.ClipRounds)),
            new("Flags", ReportValue.String($"0x{item.Flags:X2}"))
        };

        if (item.ProjectileFormId.HasValue)
        {
            statsFields.Add(new ReportField("Projectile",
                ReportValue.FormId(item.ProjectileFormId.Value, resolver),
                $"0x{item.ProjectileFormId.Value:X8}"));
        }

        if (!string.IsNullOrEmpty(item.ModelPath))
        {
            statsFields.Add(new ReportField("Model", ReportValue.String(item.ModelPath)));
        }

        sections.Add(new ReportSection("Stats", statsFields));

        return new RecordReport("Ammo", item.FormId, item.EditorId, item.FullName, sections);
    }

    internal static RecordReport BuildConsumableReport(ConsumableRecord item, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Stats
        var statsFields = new List<ReportField>
        {
            new("Value", ReportValue.Int((int)item.Value, $"{item.Value} caps")),
            new("Weight", ReportValue.Float(item.Weight))
        };

        if (item.AddictionFormId.HasValue)
        {
            statsFields.Add(new ReportField("Addiction",
                ReportValue.FormId(item.AddictionFormId.Value, resolver),
                $"0x{item.AddictionFormId.Value:X8}"));
            statsFields.Add(new ReportField("Addict. Chance",
                ReportValue.FloatDisplay(item.AddictionChance, $"{item.AddictionChance * 100:F0}%")));
        }

        if (!string.IsNullOrEmpty(item.ModelPath))
        {
            statsFields.Add(new ReportField("Model", ReportValue.String(item.ModelPath)));
        }

        sections.Add(new ReportSection("Stats", statsFields));

        // Effects (conditional)
        if (item.Effects.Count > 0)
        {
            var effectItems = new List<ReportValue>();
            foreach (var effect in item.Effects)
            {
                var effectName = effect.EffectFormId != 0
                    ? resolver.FormatFull(effect.EffectFormId)
                    : "(none)";

                var typeName = effect.Type switch
                {
                    0 => "Self",
                    1 => "Touch",
                    2 => "Target",
                    _ => $"#{effect.Type}"
                };

                effectItems.Add(new ReportValue.CompositeVal(
                    [
                        new ReportField("Effect", effect.EffectFormId != 0
                                ? ReportValue.FormId(effect.EffectFormId, resolver)
                                : ReportValue.String("(none)"),
                            effect.EffectFormId != 0 ? $"0x{effect.EffectFormId:X8}" : null),
                        new ReportField("Magnitude", ReportValue.Float(effect.Magnitude)),
                        new ReportField("Area", ReportValue.Int((int)effect.Area)),
                        new ReportField("Duration", ReportValue.Int((int)effect.Duration)),
                        new ReportField("Target", ReportValue.String(typeName))
                    ],
                    $"{effectName}\nMagnitude: {effect.Magnitude:F1}\tArea: {effect.Area}u\tDuration: {effect.Duration}s\tTarget: {typeName}"));
            }

            sections.Add(new ReportSection("Effects", [new ReportField("Effects", ReportValue.List(effectItems))]));
        }

        return new RecordReport("Consumable", item.FormId, item.EditorId, item.FullName, sections);
    }

    internal static RecordReport BuildMiscItemReport(MiscItemRecord item, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Stats
        var statsFields = new List<ReportField>
        {
            new("Value", ReportValue.Int(item.Value, $"{item.Value} caps")),
            new("Weight", ReportValue.Float(item.Weight))
        };

        if (!string.IsNullOrEmpty(item.ModelPath))
        {
            statsFields.Add(new ReportField("Model", ReportValue.String(item.ModelPath)));
        }

        sections.Add(new ReportSection("Stats", statsFields));

        return new RecordReport("Misc Item", item.FormId, item.EditorId, item.FullName, sections);
    }

    internal static RecordReport BuildKeyReport(
        KeyRecord key, FormIdResolver resolver,
        IReadOnlyList<(PlacedReference Ref, CellRecord Cell)>? linkedDoors = null)
    {
        var sections = new List<ReportSection>();

        // Stats
        var statsFields = new List<ReportField>
        {
            new("Value", ReportValue.Int(key.Value, $"{key.Value} caps")),
            new("Weight", ReportValue.Float(key.Weight))
        };
        if (!string.IsNullOrEmpty(key.ModelPath))
            statsFields.Add(new ReportField("Model", ReportValue.String(key.ModelPath)));
        sections.Add(new ReportSection("Stats", statsFields));

        // Linked Doors (reverse index: which doors/containers use this key)
        if (linkedDoors is { Count: > 0 })
        {
            var doorItems = linkedDoors
                .OrderBy(d => d.Cell.FullName ?? d.Cell.EditorId ?? "")
                .Select(d =>
                {
                    var fields = new List<ReportField>();

                    // Door/container base object
                    if (d.Ref.BaseFormId != 0)
                        fields.Add(new ReportField("Object",
                            ReportValue.FormId(d.Ref.BaseFormId, resolver),
                            $"0x{d.Ref.BaseFormId:X8}"));

                    // Lock level
                    if (d.Ref.LockLevel.HasValue)
                        fields.Add(new ReportField("Lock Level",
                            ReportValue.Int(d.Ref.LockLevel.Value)));

                    // Cell location
                    var cellName = d.Cell.FullName ?? d.Cell.EditorId ?? $"0x{d.Cell.FormId:X8}";
                    fields.Add(new ReportField("Cell",
                        ReportValue.FormId(d.Cell.FormId, resolver),
                        $"0x{d.Cell.FormId:X8}"));

                    // Summary
                    var objName = resolver.FormatFull(d.Ref.BaseFormId);
                    var lockStr = d.Ref.LockLevel.HasValue ? $" (Lock {d.Ref.LockLevel})" : "";
                    return (ReportValue)new ReportValue.CompositeVal(fields,
                        $"{objName}{lockStr} in {cellName}");
                })
                .ToList();

            sections.Add(new ReportSection($"Linked Doors ({linkedDoors.Count})",
            [
                new ReportField("Doors", ReportValue.List(doorItems))
            ]));
        }

        return new RecordReport("Key", key.FormId, key.EditorId, key.FullName, sections);
    }

    // ── Text-format section writers (delegate to GeckItemTextWriter) ──

    internal static void AppendWeaponsSection(StringBuilder sb, List<WeaponRecord> weapons,
        FormIdResolver resolver)
    {
        GeckItemTextWriter.AppendWeaponsSection(sb, weapons, resolver);
    }

    /// <summary>
    ///     Generate a report for Weapons only.
    /// </summary>
    public static string GenerateWeaponsReport(List<WeaponRecord> weapons,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        GeckItemTextWriter.AppendWeaponsSection(sb, weapons, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendArmorSection(StringBuilder sb, List<ArmorRecord> armor,
        FormIdResolver? resolver = null)
    {
        GeckItemTextWriter.AppendArmorSection(sb, armor, resolver);
    }

    /// <summary>
    ///     Generate a report for Armor only.
    /// </summary>
    public static string GenerateArmorReport(List<ArmorRecord> armor, Dictionary<uint, string>? _lookup = null)
    {
        var sb = new StringBuilder();
        GeckItemTextWriter.AppendArmorSection(sb, armor);
        return sb.ToString();
    }

    internal static void AppendAmmoSection(StringBuilder sb, List<AmmoRecord> ammo,
        FormIdResolver resolver)
    {
        GeckItemTextWriter.AppendAmmoSection(sb, ammo, resolver);
    }

    /// <summary>
    ///     Generate a report for Ammo only.
    /// </summary>
    public static string GenerateAmmoReport(List<AmmoRecord> ammo, FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        GeckItemTextWriter.AppendAmmoSection(sb, ammo, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendConsumablesSection(StringBuilder sb, List<ConsumableRecord> consumables,
        FormIdResolver resolver)
    {
        GeckItemTextWriter.AppendConsumablesSection(sb, consumables, resolver);
    }

    /// <summary>
    ///     Generate a report for Consumables only.
    /// </summary>
    public static string GenerateConsumablesReport(List<ConsumableRecord> consumables,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        GeckItemTextWriter.AppendConsumablesSection(sb, consumables, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendMiscItemsSection(StringBuilder sb, List<MiscItemRecord> miscItems)
    {
        GeckItemTextWriter.AppendMiscItemsSection(sb, miscItems);
    }

    /// <summary>
    ///     Generate a report for Misc Items only.
    /// </summary>
    public static string GenerateMiscItemsReport(List<MiscItemRecord> miscItems,
        Dictionary<uint, string>? _lookup = null)
    {
        var sb = new StringBuilder();
        GeckItemTextWriter.AppendMiscItemsSection(sb, miscItems);
        return sb.ToString();
    }

    internal static void AppendKeysSection(StringBuilder sb, List<KeyRecord> keys)
    {
        GeckItemTextWriter.AppendKeysSection(sb, keys);
    }

    /// <summary>
    ///     Generate a report for Keys only.
    /// </summary>
    public static string GenerateKeysReport(List<KeyRecord> keys, Dictionary<uint, string>? _lookup = null)
    {
        var sb = new StringBuilder();
        GeckItemTextWriter.AppendKeysSection(sb, keys);
        return sb.ToString();
    }

    internal static void AppendContainersSection(StringBuilder sb, List<ContainerRecord> containers,
        FormIdResolver resolver)
    {
        GeckItemTextWriter.AppendContainersSection(sb, containers, resolver);
    }

    /// <summary>
    ///     Generate a report for Containers only.
    /// </summary>
    public static string GenerateContainersReport(List<ContainerRecord> containers,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        GeckItemTextWriter.AppendContainersSection(sb, containers, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }
}
