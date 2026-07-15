using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

/// <summary>
///     Shared FNV PACK reference policy. A location/target whose selected union arm is a
///     FormID is structural package input: replacing a missing destination with
///     NearCurrentLocation or ObjectType=None changes when and where the package runs.
///     Such packages therefore fail closed when the reference cannot be remapped to a
///     live, type-compatible record.
/// </summary>
internal static class PackageReferenceIntegrity
{
    private static readonly ImmutableHashSet<string> ReferenceTargetTypes =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "REFR", "ACHR", "ACRE", "PGRE", "PMIS", "PBEA", "PLYR");

    private static readonly ImmutableHashSet<string> LocationObjectTargetTypes =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "ACTI", "DOOR", "STAT", "FURN", "CREA", "SPEL", "NPC_", "CONT",
            "ARMO", "AMMO", "MISC", "WEAP", "BOOK", "KEYM", "ALCH", "LIGH",
            "CHIP", "CMNY", "CCRD", "IMOD");

    private static readonly ImmutableHashSet<string> PackageTargetObjectTypes =
        LocationObjectTargetTypes.Union(
            ["LVLN", "LVLC", "FACT", "FLST", "IDLM"]);

    /// <summary>
    ///     FNV PLDT/PLD2 union arms that store FormIDs. Types 2, 3, 5, 6 and 7 carry
    ///     no FormID in the on-disk schema (current/editor/package/linked locations or
    ///     an object-type enum) and must not be remapped as one.
    /// </summary>
    internal static bool LocationTypeIsFormId(byte type) => type is 0 or 1 or 4;

    /// <summary>
    ///     FNV PTDT/PTD2 union arms that store FormIDs. Linked Reference (type 3) has an
    ///     unused four-byte union arm in FNV; only Specific Reference and Object ID carry
    ///     a FormID.
    /// </summary>
    internal static bool TargetTypeIsFormId(byte type) => type is 0 or 1;

    internal static bool IsLocationTargetTypeAllowed(byte locationType, string targetRecordType) =>
        locationType switch
        {
            0 => ReferenceTargetTypes.Contains(targetRecordType),
            1 => string.Equals(targetRecordType, "CELL", StringComparison.Ordinal),
            4 => LocationObjectTargetTypes.Contains(targetRecordType),
            _ => true,
        };

    internal static bool IsPackageTargetTypeAllowed(byte targetType, string targetRecordType) =>
        targetType switch
        {
            0 => ReferenceTargetTypes.Contains(targetRecordType),
            1 => PackageTargetObjectTypes.Contains(targetRecordType),
            _ => true,
        };

    /// <summary>
    ///     Remap every FormID-bearing package union and require the final target to be in
    ///     <paramref name="liveFormIds" />. A zero selected union is invalid even without
    ///     a live set: FNV logs it as a missing package target/location, not as an absent
    ///     optional subrecord.
    /// </summary>
    internal static PackageReferenceSanitization Sanitize(
        PackageRecord package,
        IReadOnlySet<uint>? liveFormIds,
        IReadOnlyDictionary<uint, uint>? sourceToEmitted)
    {
        ArgumentNullException.ThrowIfNull(package);

        var remaps = ImmutableArray.CreateBuilder<PackageReferenceRemap>();

        if (!TrySanitizeLocation(package.Location, "PLDT", liveFormIds, sourceToEmitted,
                remaps, out var location, out var issue)
            || !TrySanitizeTarget(package.Target, "PTDT", liveFormIds, sourceToEmitted,
                remaps, out var target, out issue)
            || !TrySanitizeLocation(package.Location2, "PLD2", liveFormIds, sourceToEmitted,
                remaps, out var location2, out issue)
            || !TrySanitizeTarget(package.Target2, "PTD2", liveFormIds, sourceToEmitted,
                remaps, out var target2, out issue))
        {
            return new PackageReferenceSanitization(package, false, issue, remaps.ToImmutable());
        }

        return new PackageReferenceSanitization(
            package with
            {
                Location = location,
                Target = target,
                Location2 = location2,
                Target2 = target2,
            },
            true,
            null,
            remaps.ToImmutable());
    }

    private static bool TrySanitizeLocation(
        PackageLocation? location,
        string field,
        IReadOnlySet<uint>? liveFormIds,
        IReadOnlyDictionary<uint, uint>? sourceToEmitted,
        ImmutableArray<PackageReferenceRemap>.Builder remaps,
        out PackageLocation? sanitized,
        out PackageReferenceIssue? issue)
    {
        sanitized = location;
        issue = null;
        if (location is null || !LocationTypeIsFormId(location.Type))
        {
            return true;
        }

        if (!TryResolve(location.Union, liveFormIds, sourceToEmitted, out var resolved))
        {
            issue = new PackageReferenceIssue(
                $"{field}.Union", location.Union, location.Type,
                location.Union == 0 ? "selected FormID union is zero" : "target is not live after remapping");
            return false;
        }

        if (resolved != location.Union)
        {
            remaps.Add(new PackageReferenceRemap($"{field}.Union", location.Union, resolved));
            sanitized = location with { Union = resolved };
        }

        return true;
    }

    private static bool TrySanitizeTarget(
        PackageTarget? target,
        string field,
        IReadOnlySet<uint>? liveFormIds,
        IReadOnlyDictionary<uint, uint>? sourceToEmitted,
        ImmutableArray<PackageReferenceRemap>.Builder remaps,
        out PackageTarget? sanitized,
        out PackageReferenceIssue? issue)
    {
        sanitized = target;
        issue = null;
        if (target is null || !TargetTypeIsFormId(target.Type))
        {
            return true;
        }

        if (!TryResolve(target.FormIdOrType, liveFormIds, sourceToEmitted, out var resolved))
        {
            issue = new PackageReferenceIssue(
                $"{field}.FormIdOrType", target.FormIdOrType, target.Type,
                target.FormIdOrType == 0 ? "selected FormID union is zero" : "target is not live after remapping");
            return false;
        }

        if (resolved != target.FormIdOrType)
        {
            remaps.Add(new PackageReferenceRemap($"{field}.FormIdOrType", target.FormIdOrType, resolved));
            sanitized = target with { FormIdOrType = resolved };
        }

        return true;
    }

    private static bool TryResolve(
        uint source,
        IReadOnlySet<uint>? liveFormIds,
        IReadOnlyDictionary<uint, uint>? sourceToEmitted,
        out uint resolved)
    {
        resolved = source;
        if (source == 0)
        {
            return false;
        }

        if (sourceToEmitted is not null && sourceToEmitted.TryGetValue(source, out var remapped))
        {
            resolved = remapped;
            return remapped != 0 && (liveFormIds is null || liveFormIds.Contains(remapped));
        }

        return liveFormIds is null || liveFormIds.Contains(source);
    }
}

internal sealed record PackageReferenceSanitization(
    PackageRecord Package,
    bool IsValid,
    PackageReferenceIssue? Issue,
    ImmutableArray<PackageReferenceRemap> Remaps);

internal sealed record PackageReferenceIssue(
    string FieldPath,
    uint FormId,
    byte UnionType,
    string Reason);

internal sealed record PackageReferenceRemap(
    string FieldPath,
    uint SourceFormId,
    uint EmittedFormId);
