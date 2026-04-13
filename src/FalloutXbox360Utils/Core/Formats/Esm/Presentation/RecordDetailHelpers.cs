using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Presentation;

/// <summary>
///     Shared helper methods for building RecordDetailModel entries. Extracted from RecordDetailPresenter.
/// </summary>
internal static class RecordDetailHelpers
{
    internal static RecordDetailModel Model(
        string signature,
        uint formId,
        string? editorId,
        string? displayName,
        IEnumerable<RecordDetailSection> sections)
    {
        return new RecordDetailModel
        {
            RecordSignature = signature,
            FormId = formId,
            EditorId = editorId,
            DisplayName = displayName,
            Sections = sections.Where(section => section.Entries.Count > 0).ToList()
        };
    }

    internal static RecordDetailSection Section(string title, IEnumerable<RecordDetailEntry> entries)
    {
        return new RecordDetailSection
        {
            Title = title,
            Entries = entries.Where(entry =>
                    !string.IsNullOrEmpty(entry.Value) || entry.Kind == RecordDetailEntryKind.List)
                .ToList()
        };
    }

    internal static RecordDetailSection ListSection(string title, List<RecordDetailListItem>? items)
    {
        items ??= [];
        return new RecordDetailSection
        {
            Title = title,
            Entries = items.Count == 0
                ? []
                :
                [
                    new RecordDetailEntry
                    {
                        Kind = RecordDetailEntryKind.List,
                        Label = title,
                        Items = items,
                        ExpandByDefault = items.Count <= 8
                    }
                ]
        };
    }

    internal static RecordDetailEntry Scalar(string label, string? value)
    {
        return new RecordDetailEntry
        {
            Kind = RecordDetailEntryKind.Scalar,
            Label = label,
            Value = value
        };
    }

    internal static RecordDetailEntry Link(string label, uint? formId, FormIdResolver resolver)
    {
        return new RecordDetailEntry
        {
            Kind = formId.HasValue ? RecordDetailEntryKind.Link : RecordDetailEntryKind.Scalar,
            Label = label,
            Value = formId.HasValue ? resolver.FormatWithEditorId(formId.Value) : null,
            LinkedFormId = formId
        };
    }

    internal static RecordDetailListItem ListLinkItem(uint formId, FormIdResolver resolver)
    {
        return new RecordDetailListItem
        {
            Label = resolver.GetBestNameWithRefChain(formId) ?? $"0x{formId:X8}",
            Value = $"0x{formId:X8}",
            LinkedFormId = formId
        };
    }

    internal static List<RecordDetailListItem> BuildStatItems(string[] names, string[]? values)
    {
        if (values == null)
        {
            return [];
        }

        return names.Zip(values, (name, value) => new RecordDetailListItem
        {
            Label = name,
            Value = value
        }).ToList();
    }

    internal static List<RecordDetailListItem> BuildSkillItems(byte[]? skills, FormIdResolver resolver)
    {
        if (skills == null || skills.Length == 0)
        {
            return [];
        }

        var hasBigGuns = resolver.SkillEra?.BigGunsActive ?? false;
        var items = new List<RecordDetailListItem>();
        for (var i = 0; i < skills.Length && i < 14; i++)
        {
            if (i == 1 && !hasBigGuns)
            {
                continue;
            }

            items.Add(new RecordDetailListItem
            {
                Label = resolver.GetSkillName(i) ?? $"Skill#{i}",
                Value = skills[i].ToString()
            });
        }

        return items;
    }

    internal static string? FormatBounds(ObjectBounds? bounds)
    {
        if (bounds == null)
        {
            return null;
        }

        return $"({bounds.X1}, {bounds.Y1}, {bounds.Z1}) -> ({bounds.X2}, {bounds.Y2}, {bounds.Z2})";
    }

    internal static string? FormatPackageLocation(PackageLocation? location, FormIdResolver resolver)
    {
        if (location == null)
        {
            return null;
        }

        var union = location.Union != 0
            ? resolver.GetBestNameWithRefChain(location.Union) ?? $"0x{location.Union:X8}"
            : "(none)";
        return $"Type {location.Type}, {union}, radius {location.Radius}";
    }

    internal static string? FormatPackageTarget(PackageTarget? target, FormIdResolver resolver)
    {
        if (target == null)
        {
            return null;
        }

        var targetValue = target.FormIdOrType != 0
            ? resolver.GetBestNameWithRefChain(target.FormIdOrType) ?? $"0x{target.FormIdOrType:X8}"
            : "(none)";
        return $"{target.TypeName}: {targetValue}, count {target.CountDistance}, radius {target.AcquireRadius:F1}";
    }

    internal static string? FormatVolley(PackageUseWeaponData? useWeaponData)
    {
        if (useWeaponData == null)
        {
            return null;
        }

        return $"{useWeaponData.VolleyShotsMin}-{useWeaponData.VolleyShotsMax} shots, " +
               $"{useWeaponData.VolleyWaitMin:F1}-{useWeaponData.VolleyWaitMax:F1}s";
    }

    internal static string? FormatCellCorner(short? x, short? y)
    {
        return x.HasValue && y.HasValue ? $"({x}, {y})" : null;
    }

    internal static string? FormatWorldBounds(WorldspaceRecord worldspace)
    {
        if (!worldspace.BoundsMinX.HasValue || !worldspace.BoundsMinY.HasValue ||
            !worldspace.BoundsMaxX.HasValue || !worldspace.BoundsMaxY.HasValue)
        {
            return null;
        }

        return $"({worldspace.BoundsMinX:F1}, {worldspace.BoundsMinY:F1}) -> " +
               $"({worldspace.BoundsMaxX:F1}, {worldspace.BoundsMaxY:F1})";
    }

    internal static string? FormatMapOffset(WorldspaceRecord worldspace)
    {
        if (!worldspace.MapOffsetScaleX.HasValue && !worldspace.MapOffsetScaleY.HasValue &&
            !worldspace.MapOffsetZ.HasValue)
        {
            return null;
        }

        return $"X {worldspace.MapOffsetScaleX?.ToString("F2") ?? "?"}, " +
               $"Y {worldspace.MapOffsetScaleY?.ToString("F2") ?? "?"}, " +
               $"Z {worldspace.MapOffsetZ?.ToString("F2") ?? "?"}";
    }

    internal static string? BoolText(bool? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value ? "Yes" : "No";
    }
}
