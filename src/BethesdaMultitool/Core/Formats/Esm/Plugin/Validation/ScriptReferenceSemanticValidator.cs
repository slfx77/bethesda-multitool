using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Validation;

/// <summary>Validates the fixed SCRO/SCRV table on every freshly-emitted SCPT.</summary>
internal static class ScriptReferenceSemanticValidator
{
    internal static ScriptReferenceSemanticValidationResult Validate(
        IReadOnlyList<ParsedMainRecord> pluginRecords,
        IReadOnlySet<uint> pluginFormIds,
        IReadOnlySet<uint>? masterFormIds,
        IReadOnlySet<uint>? additionalValidFormIds)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        var referenceCount = 0;
        foreach (var record in pluginRecords.Where(record => record.Header.Signature == "SCPT"))
        {
            var variables = record.Subrecords
                .Where(subrecord => subrecord.Signature == "SLSD" && subrecord.Data.Length >= 4)
                .Select(subrecord => BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data))
                .ToHashSet();
            var references = record.Subrecords
                .Where(subrecord => subrecord.Signature is "SCRO" or "SCRV")
                .ToList();
            referenceCount += references.Count;

            var schr = record.Subrecords.FirstOrDefault(subrecord => subrecord.Signature == "SCHR");
            if (schr is null || schr.Data.Length < 8)
            {
                errors.Add($"SCPT 0x{record.Header.FormId:X8} has no valid SCHR reference count.");
            }
            else
            {
                var declared = BinaryPrimitives.ReadUInt32LittleEndian(schr.Data.AsSpan(4, 4));
                if (declared != references.Count)
                {
                    errors.Add(
                        $"SCPT 0x{record.Header.FormId:X8} SCHR declares {declared} reference(s), " +
                        $"but {references.Count} SCRO/SCRV slot(s) were emitted.");
                }
            }

            for (var i = 0; i < references.Count; i++)
            {
                var reference = references[i];
                if (reference.Data.Length != sizeof(uint))
                {
                    errors.Add(
                        $"SCPT 0x{record.Header.FormId:X8} {reference.Signature}[{i}] has " +
                        $"{reference.Data.Length} bytes, expected 4.");
                    continue;
                }

                var value = BinaryPrimitives.ReadUInt32LittleEndian(reference.Data);
                if (reference.Signature == "SCRV")
                {
                    if (value == 0 || !variables.Contains(value))
                    {
                        errors.Add(
                            $"SCPT 0x{record.Header.FormId:X8} SCRV[{i}] variable {value} " +
                            "has no matching SLSD.");
                    }
                }
                else if (value == 0
                         || !(pluginFormIds.Contains(value)
                              || masterFormIds?.Contains(value) == true
                              || additionalValidFormIds?.Contains(value) == true
                              || RuntimeStateRecordPolicy.EngineFormIds.Contains(value)))
                {
                    errors.Add(
                        $"SCPT 0x{record.Header.FormId:X8} SCRO[{i}] FormID 0x{value:X8} " +
                        "does not resolve to an emitted, master, child, or engine-owned form.");
                }
            }
        }

        return new ScriptReferenceSemanticValidationResult(referenceCount, errors.ToImmutable());
    }
}

internal sealed record ScriptReferenceSemanticValidationResult(
    int ReferenceCount,
    ImmutableArray<string> Errors);
