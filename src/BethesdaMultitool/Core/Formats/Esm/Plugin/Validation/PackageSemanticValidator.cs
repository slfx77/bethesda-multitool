using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Validation;

/// <summary>
///     Type-aware post-emit closure check for PACK PLDT/PTDT unions and actor PKID lists.
/// </summary>
internal static class PackageSemanticValidator
{
    internal static PackageSemanticValidationResult Validate(
        IReadOnlyList<ParsedMainRecord> pluginRecords,
        IReadOnlyDictionary<string, HashSet<uint>> pluginFormIdsByType,
        IReadOnlyDictionary<string, HashSet<uint>>? masterFormIdsByType)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        var packageReferenceCount = 0;
        var actorPackageCount = 0;

        foreach (var record in pluginRecords)
        {
            if (record.Header.Signature == "PACK")
            {
                foreach (var subrecord in record.Subrecords)
                {
                    if (subrecord.Signature is "PLDT" or "PLD2" && subrecord.Data.Length >= 8)
                    {
                        var type = subrecord.Data[0];
                        if (!PackageReferenceIntegrity.LocationTypeIsFormId(type))
                        {
                            continue;
                        }

                        packageReferenceCount++;
                        var target = BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data.AsSpan(4, 4));
                        ValidatePackageUnion(
                            record, subrecord.Signature, type, target,
                            static (unionType, recordType) =>
                                PackageReferenceIntegrity.IsLocationTargetTypeAllowed(unionType, recordType),
                            pluginFormIdsByType, masterFormIdsByType, errors);
                    }
                    else if (subrecord.Signature is "PTDT" or "PTD2" && subrecord.Data.Length >= 8)
                    {
                        var type = subrecord.Data[0];
                        if (!PackageReferenceIntegrity.TargetTypeIsFormId(type))
                        {
                            continue;
                        }

                        packageReferenceCount++;
                        var target = BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data.AsSpan(4, 4));
                        ValidatePackageUnion(
                            record, subrecord.Signature, type, target,
                            static (unionType, recordType) =>
                                PackageReferenceIntegrity.IsPackageTargetTypeAllowed(unionType, recordType),
                            pluginFormIdsByType, masterFormIdsByType, errors);
                    }
                }
            }

            if (record.Header.Signature is not ("NPC_" or "CREA"))
            {
                continue;
            }

            foreach (var subrecord in record.Subrecords)
            {
                if (subrecord.Signature != "PKID" || subrecord.Data.Length < 4)
                {
                    continue;
                }

                actorPackageCount++;
                var packageId = BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data);
                if (!TryResolveType(packageId, pluginFormIdsByType, masterFormIdsByType, out var targetType))
                {
                    errors.Add(
                        $"{record.Header.Signature} 0x{record.Header.FormId:X8} PKID " +
                        $"0x{packageId:X8} does not resolve to an emitted/master PACK.");
                }
                else if (targetType != "PACK")
                {
                    errors.Add(
                        $"{record.Header.Signature} 0x{record.Header.FormId:X8} PKID " +
                        $"0x{packageId:X8} resolves to {targetType}, expected PACK.");
                }
            }
        }

        return new PackageSemanticValidationResult(
            errors.ToImmutable(), packageReferenceCount, actorPackageCount);
    }

    private static void ValidatePackageUnion(
        ParsedMainRecord package,
        string signature,
        byte unionType,
        uint target,
        Func<byte, string, bool> typeIsAllowed,
        IReadOnlyDictionary<string, HashSet<uint>> pluginFormIdsByType,
        IReadOnlyDictionary<string, HashSet<uint>>? masterFormIdsByType,
        ImmutableArray<string>.Builder errors)
    {
        if (!TryResolveType(target, pluginFormIdsByType, masterFormIdsByType, out var targetType))
        {
            errors.Add(
                $"PACK 0x{package.Header.FormId:X8} {signature} Type {unionType} FormID " +
                $"0x{target:X8} does not resolve to an emitted/master record.");
        }
        else if (!typeIsAllowed(unionType, targetType))
        {
            errors.Add(
                $"PACK 0x{package.Header.FormId:X8} {signature} Type {unionType} FormID " +
                $"0x{target:X8} resolves to {targetType}, invalid for that union arm.");
        }
    }

    private static bool TryResolveType(
        uint formId,
        IReadOnlyDictionary<string, HashSet<uint>> pluginFormIdsByType,
        IReadOnlyDictionary<string, HashSet<uint>>? masterFormIdsByType,
        out string recordType)
    {
        if (formId == 0)
        {
            recordType = string.Empty;
            return false;
        }

        if (formId == 0x00000014u)
        {
            recordType = "PLYR";
            return true;
        }

        foreach (var (type, ids) in pluginFormIdsByType)
        {
            if (ids.Contains(formId))
            {
                recordType = type;
                return true;
            }
        }

        if (masterFormIdsByType is not null)
        {
            foreach (var (type, ids) in masterFormIdsByType)
            {
                if (ids.Contains(formId))
                {
                    recordType = type;
                    return true;
                }
            }
        }

        recordType = string.Empty;
        return false;
    }
}

internal sealed record PackageSemanticValidationResult(
    ImmutableArray<string> Errors,
    int PackageReferenceCount,
    int ActorPackageCount);
