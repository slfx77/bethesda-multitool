using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

/// <summary>
///     Extracts string ownership claims from runtime C++ struct BSStringT fields.
///     Uses pre-captured offsets from EditorID extraction (DisplayName, DialogueLine)
///     and PDB layout walks for ALL types (including specialized reader types).
/// </summary>
internal static class RuntimeStructStringClaimExtractor
{
    internal static List<RuntimeStringOwnershipClaim> ExtractClaims(
        IReadOnlyList<RuntimeEditorIdEntry> runtimeEditorIds,
        RuntimeMemoryContext memCtx)
    {
        var claims = new List<RuntimeStringOwnershipClaim>();
        var claimedOffsets = new HashSet<long>();

        foreach (var entry in runtimeEditorIds)
        {
            // Claim pre-captured DisplayName string offset
            if (entry.DisplayNameStringOffset.HasValue)
            {
                AddClaim(claims, claimedOffsets, entry, entry.DisplayNameStringOffset.Value,
                    "cFullName", memCtx.MinidumpInfo);
            }

            // Claim pre-captured DialogueLine string offset
            if (entry.DialogueLineStringOffset.HasValue)
            {
                AddClaim(claims, claimedOffsets, entry, entry.DialogueLineStringOffset.Value,
                    "cPrompt", memCtx.MinidumpInfo);
            }

            // PDB BSStringT walk for ALL types (no HasSpecializedReader exclusion)
            if (entry.TesFormOffset.HasValue)
            {
                ExtractAllBSStringTClaims(entry, memCtx, claims, claimedOffsets);
            }
        }

        return claims;
    }

    private static void ExtractAllBSStringTClaims(
        RuntimeEditorIdEntry entry,
        RuntimeMemoryContext memCtx,
        List<RuntimeStringOwnershipClaim> claims,
        HashSet<long> claimedOffsets)
    {
        var fields = PdbStructLayouts.GetBSStringTFields(entry.FormType);
        if (fields.Count == 0)
        {
            return;
        }

        var tesFormOffset = entry.TesFormOffset!.Value;

        foreach (var field in fields)
        {
            // Skip cFormEditorID — already claimed by the EditorId source
            if (field.Name is "cFormEditorID")
            {
                continue;
            }

            var info = memCtx.ReadBSStringTInfo(tesFormOffset, field.Offset);
            if (info == null)
            {
                continue;
            }

            var fieldLabel = field.Owner != null ? $"{field.Owner}.{field.Name}" : field.Name;

            AddClaim(claims, claimedOffsets, entry, info.Value.StringFileOffset,
                fieldLabel, memCtx.MinidumpInfo);
        }
    }

    private static void AddClaim(
        List<RuntimeStringOwnershipClaim> claims,
        HashSet<long> claimedOffsets,
        RuntimeEditorIdEntry entry,
        long stringFileOffset,
        string fieldName,
        MinidumpInfo minidumpInfo)
    {
        if (!claimedOffsets.Add(stringFileOffset))
        {
            return;
        }

        var formTypeName = PdbStructLayouts.Get(entry.FormType)?.RecordCode ?? $"0x{entry.FormType:X2}";

        claims.Add(new RuntimeStringOwnershipClaim(
            stringFileOffset,
            minidumpInfo.FileOffsetToVirtualAddress(stringFileOffset),
            "RuntimeStruct",
            $"{formTypeName} {entry.EditorId}",
            entry.FormId != 0 ? entry.FormId : null,
            entry.TesFormOffset,
            ClaimSource.RuntimeStructField,
            formTypeName,
            fieldName));
    }
}
