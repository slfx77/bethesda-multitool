using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation;

/// <summary>
///     Shared helper methods for building RecordDetailModel entries. Extracted from RecordDetailPresenter.
/// </summary>
internal static class RecordDetailHelpers
{
    /// <summary>The PLDT/PLD2 type whose union is an object-type enum value (xEdit FNV "Object Type").</summary>
    private const byte PackageLocationObjectTypeArm = 5;

    /// <summary>The PTDT/PTD2 type whose union is an object-type enum value (xEdit FNV "Object Type").</summary>
    private const byte PackageTargetObjectTypeArm = 2;

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

    // This row is scoped to the verified FO3/FNV schemas. Unknown-game captures retain only
    // their stored bits; other games keep their existing curated detail output.
    internal static string? ActorFlags(uint? flags, BethesdaGame game, string recordType)
    {
        if (flags is null || game is not (BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas or BethesdaGame.Unknown))
        {
            return null;
        }

        var definitions = FlagRegistry.GetActorBaseFlags(game, recordType);
        return definitions.Length == 0
            ? $"0x{flags.Value:X8}"
            : FlagRegistry.DecodeFlagNamesWithHex(flags.Value, definitions);
    }

    // Authored FO3/FNV ACBS encoding, not an effective runtime level. The schema decoder
    // exposes the union as U16; recover the same signed storage used by the typed parser.
    internal static IEnumerable<RecordDetailEntry> ActorLevel(
        BethesdaGame game, uint? flags, long? level, long? minimum, long? maximum)
    {
        if (game is not (BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas)
            || flags is null || (flags.Value & 0x80) == 0)
        {
            return [Scalar("Level", level?.ToString() ?? "(unknown)")];
        }

        short? encoded = level is >= short.MinValue and <= ushort.MaxValue
            ? unchecked((short)level.Value)
            : null;
        return
        [
            Scalar("Level Multiplier (stored)", (encoded / 1000m)?.ToString(CultureInfo.InvariantCulture) ?? "(unknown)"),
            Scalar("Level (encoded)", encoded?.ToString(CultureInfo.InvariantCulture) ?? "(unknown)"),
            Scalar("Minimum Level", minimum?.ToString(CultureInfo.InvariantCulture) ?? "(unknown)"),
            Scalar("Maximum Level", maximum?.ToString(CultureInfo.InvariantCulture) ?? "(unknown)")
        ];
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

    /// <summary>
    ///     Formats a PLDT/PLD2 location as <c>Type N, value, radius R</c>. Only the arms
    ///     <see cref="PackageReferenceIntegrity.LocationTypeIsFormId" /> accepts (reference, cell, object ID)
    ///     are resolved as FormIDs, and their text is unchanged. The object-type arm prints its enum value
    ///     and every other arm its raw union, so an enum or unused value is never shown as whichever record
    ///     happens to own that low FormID.
    /// </summary>
    internal static string? FormatPackageLocation(PackageLocation? location, FormIdResolver resolver)
    {
        if (location == null)
        {
            return null;
        }

        var union = DescribePackageLocationUnion(location, resolver);
        return $"Type {location.Type}, {union}, radius {location.Radius}";
    }

    /// <summary>
    ///     Formats a PTDT/PTD2 target as <c>TypeName: value, count C, radius R</c>. Only the arms
    ///     <see cref="PackageReferenceIntegrity.TargetTypeIsFormId" /> accepts (specific reference, object ID)
    ///     are resolved as FormIDs, and their text is unchanged. The object-type arm prints its enum value
    ///     (<c>Object Type: 18</c>, which was once shown as HorseMarker, the record at FormID 0x12) and every
    ///     other arm its raw union.
    /// </summary>
    internal static string? FormatPackageTarget(PackageTarget? target, FormIdResolver resolver)
    {
        if (target == null)
        {
            return null;
        }

        var targetValue = DescribePackageTargetUnion(target, resolver);
        return $"{target.TypeName}: {targetValue}, count {target.CountDistance}, radius {target.AcquireRadius:F1}";
    }

    private static string DescribePackageLocationUnion(PackageLocation location, FormIdResolver resolver)
    {
        if (PackageReferenceIntegrity.LocationTypeIsFormId(location.Type))
        {
            return FormatPackageFormIdArm(location.Union, resolver);
        }

        return location.Type == PackageLocationObjectTypeArm
            ? $"object type {location.Union}"
            : FormatPackageRawArm(location.Union);
    }

    private static string DescribePackageTargetUnion(PackageTarget target, FormIdResolver resolver)
    {
        if (PackageReferenceIntegrity.TargetTypeIsFormId(target.Type))
        {
            return FormatPackageFormIdArm(target.FormIdOrType, resolver);
        }

        return target.Type == PackageTargetObjectTypeArm
            ? $"{target.FormIdOrType}"
            : FormatPackageRawArm(target.FormIdOrType);
    }

    private static string FormatPackageFormIdArm(uint formId, FormIdResolver resolver)
    {
        return formId != 0
            ? resolver.GetBestNameWithRefChain(formId) ?? $"0x{formId:X8}"
            : "(none)";
    }

    /// <summary>
    ///     A union arm that carries no FormID (unused in the on-disk schema, or a type this build does not
    ///     know): zero stays <c>(none)</c>, anything else prints raw and is never resolved.
    /// </summary>
    private static string FormatPackageRawArm(uint value)
    {
        return value != 0 ? $"raw 0x{value:X8}" : "(none)";
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
