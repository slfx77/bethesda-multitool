using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

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

        var projectileFormIds = item.ProjectileFormIds
            .Where(id => id != 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (projectileFormIds.Count > 1)
        {
            statsFields.Add(new ReportField("Projectiles",
                ReportValue.List(
                    projectileFormIds
                        .Select(id => (ReportValue)ReportValue.FormId(id, resolver))
                        .ToList(),
                    string.Join("; ", projectileFormIds.Select(resolver.FormatFull)))));
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
        IReadOnlyList<KeyLockedDoorInfo>? linkedDoors = null)
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
                .OrderBy(d => d.CellName ?? d.CellEditorId ?? "")
                .ThenBy(d => d.GridY ?? int.MaxValue)
                .ThenBy(d => d.GridX ?? int.MaxValue)
                .ThenBy(d => d.Ref.FormId)
                .Select(d => (ReportValue)BuildKeyLinkedDoorComposite(d, resolver))
                .ToList();

            sections.Add(new ReportSection($"Linked Doors ({linkedDoors.Count})",
            [
                new ReportField("Doors", ReportValue.List(doorItems))
            ]));
        }

        return new RecordReport("Key", key.FormId, key.EditorId, key.FullName, sections);
    }

    private static ReportValue.CompositeVal BuildKeyLinkedDoorComposite(
        KeyLockedDoorInfo info,
        FormIdResolver resolver)
    {
        var door = info.Ref;
        var baseStr = !string.IsNullOrEmpty(door.BaseEditorId)
            ? door.BaseEditorId
            : resolver.GetEditorId(door.BaseFormId)
              ?? GeckReportHelpers.FormatFormId(door.BaseFormId);
        var referenceEditorId = !string.IsNullOrEmpty(door.EditorId)
            ? door.EditorId
            : resolver.GetEditorId(door.FormId);
        var baseDisplay = resolver.GetDisplayName(door.BaseFormId);
        var displayName = !string.IsNullOrEmpty(baseDisplay) &&
                          !string.Equals(baseDisplay, baseStr, StringComparison.Ordinal)
            ? baseDisplay
            : null;

        var fields = new List<ReportField>
        {
            new("FormID", ReportValue.FormId(door.FormId, GeckReportHelpers.FormatFormId(door.FormId)),
                $"0x{door.FormId:X8}"),
            new("Base", ReportValue.String(baseStr)),
            new("Type", ReportValue.String(door.RecordType)),
            new("Position", ReportValue.String($"({door.X:F1}, {door.Y:F1}, {door.Z:F1})")),
            new("Containing Cell", ReportValue.FormId(info.CellFormId, resolver), $"0x{info.CellFormId:X8}")
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

        if (info.WorldspaceFormId is > 0)
        {
            fields.Add(new ReportField("Worldspace",
                ReportValue.FormId(info.WorldspaceFormId.Value, resolver),
                $"0x{info.WorldspaceFormId.Value:X8}"));
        }

        if (info.GridX.HasValue && info.GridY.HasValue)
        {
            fields.Add(new ReportField("Grid", ReportValue.String($"{info.GridX.Value},{info.GridY.Value}")));
        }

        var hasRotation = MathF.Abs(door.RotX) > 0.001f || MathF.Abs(door.RotY) > 0.001f ||
                          MathF.Abs(door.RotZ) > 0.001f;
        if (hasRotation)
        {
            fields.Add(new ReportField("Rotation",
                ReportValue.String($"({door.RotX:F3}, {door.RotY:F3}, {door.RotZ:F3})")));
        }

        if (Math.Abs(door.Scale - 1.0f) > 0.01f)
        {
            fields.Add(new ReportField("Scale", ReportValue.Float(door.Scale, "F2")));
        }

        if (door.LockLevel.HasValue)
        {
            fields.Add(new ReportField("Lock Level", ReportValue.Int(door.LockLevel.Value)));
        }

        if (door.IsInitiallyDisabled)
        {
            fields.Add(new ReportField("Disabled", ReportValue.Bool(true)));
        }

        AddOptionalFormIdField(fields, "Links to", door.DestinationCellFormId, resolver);
        AddOptionalFormIdField(fields, "Destination Door", door.DestinationDoorFormId, resolver);
        AddOptionalFormIdField(fields, "Enable Parent", door.EnableParentFormId, resolver);
        AddOptionalFormIdField(fields, "Linked Ref", door.LinkedRefFormId, resolver);

        if (door.ModelPath != null)
        {
            fields.Add(new ReportField("Model", ReportValue.String(door.ModelPath)));
        }

        var disabledTag = door.IsInitiallyDisabled ? " [DISABLED]" : "";
        var lockTag = door.LockLevel.HasValue ? $" (Lock {door.LockLevel.Value})" : "";
        var referenceTag = !string.IsNullOrEmpty(referenceEditorId) &&
                           !string.Equals(referenceEditorId, baseStr, StringComparison.Ordinal)
            ? $"{referenceEditorId} ({baseStr})"
            : baseStr;
        var cellLabel = info.CellName ?? info.CellEditorId ?? GeckReportHelpers.FormatFormId(info.CellFormId);
        var linksToTag = door.DestinationCellFormId is > 0
            ? $" -> Links to: {resolver.FormatFull(door.DestinationCellFormId.Value)}"
            : "";
        var summary =
            $"{referenceTag} ({door.RecordType}) [{GeckReportHelpers.FormatFormId(door.FormId)}]{disabledTag}{lockTag} in {cellLabel}{linksToTag}";

        return new ReportValue.CompositeVal(fields, summary);
    }

    private static void AddOptionalFormIdField(
        List<ReportField> fields,
        string label,
        uint? formId,
        FormIdResolver resolver)
    {
        if (formId is > 0)
        {
            fields.Add(new ReportField(label, ReportValue.FormId(formId.Value, resolver), $"0x{formId.Value:X8}"));
        }
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
