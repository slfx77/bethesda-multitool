using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     Extracts string ownership claims from runtime C++ struct BSStringT fields.
///     Consumes validated DisplayName/DialogueLine payload offsets from EditorID extraction,
///     then walks top-level PDB BSStringT fields for ALL types (including specialized readers).
///     Nested allocations and list/item strings remain the separate responsibility of
///     <see cref="RuntimeNestedStringClaimExtractor" /> and are intentionally not rebased here.
/// </summary>
internal static class RuntimeStructStringClaimExtractor
{
    internal static List<RuntimeStringOwnershipClaim> ExtractClaims(
        IReadOnlyList<RuntimeEditorIdEntry> runtimeEditorIds,
        RuntimeMemoryContext memCtx)
    {
        var claims = new List<RuntimeStringOwnershipClaim>();
        var claimedOffsets = new HashSet<long>();
        var fieldAccessor = new RuntimePdbFieldAccessor(memCtx);

        foreach (var entry in runtimeEditorIds)
        {
            // The entry stores TESForm*, which may be an interior subobject (MSTT/FLOR).
            // Resolve the complete object once so pre-captured and PDB-discovered claims agree
            // on the owner even when the string payload itself was captured earlier.
            var structData = entry.TesFormOffset.HasValue
                ? fieldAccessor.ReadStruct(entry)
                : null;
            var (ownerOffsetResolved, ownerFileOffset) = ResolveOwnerFileOffset(entry, memCtx, structData);

            // Claim validated DisplayName payload offset captured during EditorID extraction.
            if (entry.DisplayNameStringOffset.HasValue)
            {
                AddClaim(claims, claimedOffsets, entry, entry.DisplayNameStringOffset.Value,
                    "cFullName", memCtx.MinidumpInfo, ownerFileOffset, ownerOffsetResolved);
            }

            // Claim validated DialogueLine payload offset captured during EditorID extraction.
            if (entry.DialogueLineStringOffset.HasValue)
            {
                AddClaim(claims, claimedOffsets, entry, entry.DialogueLineStringOffset.Value,
                    "cPrompt", memCtx.MinidumpInfo, ownerFileOffset, ownerOffsetResolved);
            }

            // PDB BSStringT walk for ALL types (no HasSpecializedReader exclusion)
            if (structData.HasValue)
            {
                ExtractAllBSStringTClaims(entry, memCtx, structData.Value, claims, claimedOffsets);
            }
        }

        return claims;
    }

    private static void ExtractAllBSStringTClaims(
        RuntimeEditorIdEntry entry,
        RuntimeMemoryContext memCtx,
        (PdbTypeLayout Layout, byte[] Buffer, long FileOffset) structData,
        List<RuntimeStringOwnershipClaim> claims,
        HashSet<long> claimedOffsets)
    {
        var fields = PdbStructLayouts.GetBSStringTFields(entry.FormType);
        if (fields.Count == 0)
        {
            return;
        }

        foreach (var field in fields)
        {
            // Skip cFormEditorID — already claimed by the EditorId source
            if (field.Name is "cFormEditorID")
            {
                continue;
            }

            var info = memCtx.ReadBSStringTInfo(structData.Buffer, field.Offset);
            if (info == null)
            {
                continue;
            }

            var fieldLabel = field.Owner != null ? $"{field.Owner}.{field.Name}" : field.Name;

            AddClaim(claims, claimedOffsets, entry, info.Value.StringFileOffset,
                fieldLabel, memCtx.MinidumpInfo, structData.FileOffset, true);
        }
    }

    private static (bool Resolved, long? FileOffset) ResolveOwnerFileOffset(
        RuntimeEditorIdEntry entry,
        RuntimeMemoryContext memCtx,
        (PdbTypeLayout Layout, byte[] Buffer, long FileOffset)? structData)
    {
        if (structData.HasValue)
        {
            return (true, structData.Value.FileOffset);
        }

        if (entry.TesFormOffset is not { } tesFormFileOffset)
        {
            return (true, null);
        }

        var layout = PdbStructLayouts.Get(entry.FormType);
        if (layout == null)
        {
            return (false, null);
        }

        var interiorOffset = PdbStructLayouts.GetTesFormInteriorOffset(layout);
        if (interiorOffset == 0)
        {
            return (true, tesFormFileOffset);
        }

        var tesFormVa = entry.TesFormPointer is { } pointer && pointer != 0
            ? pointer
            : memCtx.MinidumpInfo.FileOffsetToVirtualAddress(tesFormFileOffset);
        if (tesFormVa.HasValue)
        {
            if (tesFormVa.Value < long.MinValue + interiorOffset)
            {
                return (true, null);
            }

            return (true,
                memCtx.MinidumpInfo.VirtualAddressToFileOffset(tesFormVa.Value - interiorOffset));
        }

        // Lightweight synthetic contexts without a VA map retain the historical flat fallback,
        // but still apply the known TESForm interior offset.
        if (memCtx.MinidumpInfo.MemoryRegions.Count == 0 && tesFormFileOffset >= interiorOffset)
        {
            return (true, tesFormFileOffset - interiorOffset);
        }

        // The layout proves the entry is interior, but the complete owner is not captured.
        // Preserve that uncertainty instead of falsely attributing the string to TESForm itself.
        return (true, null);
    }

    private static void AddClaim(
        List<RuntimeStringOwnershipClaim> claims,
        HashSet<long> claimedOffsets,
        RuntimeEditorIdEntry entry,
        long stringFileOffset,
        string fieldName,
        MinidumpInfo minidumpInfo,
        long? ownerFileOffset = null,
        bool ownerOffsetResolved = false)
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
            ownerOffsetResolved ? ownerFileOffset : entry.TesFormOffset,
            ClaimSource.RuntimeStructField,
            formTypeName,
            fieldName));
    }
}
