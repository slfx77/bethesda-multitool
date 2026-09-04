using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Strings;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     Text-content and cFormEditorID fallback matching strategies for second-pass
///     ownership resolution. Matches ReferencedOwnerUnknown strings by their text
///     content against known EditorIDs, game settings, dialogue lines, and asset paths.
/// </summary>
internal sealed class OwnershipTextMatcher
{
    private readonly BufferAnalysisContext _ctx;

    /// <summary>
    ///     Case-insensitive lookup from dialogue line text to RuntimeEditorIdEntry.
    /// </summary>
    private readonly Dictionary<string, RuntimeEditorIdEntry>? _dialogueTextLookup;

    /// <summary>
    ///     Case-insensitive lookup from EditorID text to RuntimeEditorIdEntry.
    /// </summary>
    private readonly Dictionary<string, RuntimeEditorIdEntry>? _editorIdTextLookup;

    /// <summary>
    ///     Case-insensitive lookup from GMST setting name to GmstRecord.
    /// </summary>
    private readonly Dictionary<string, GmstRecord>? _gmstTextLookup;

    /// <summary>
    ///     VA-space reader for the cFormEditorID step-back. Fails closed at region boundaries,
    ///     which a flat file-offset read cannot do.
    /// </summary>
    private readonly RuntimeMemoryContext _memory;

    /// <summary>
    ///     Set of all PDB class names (for cFormEditorID fallback validation).
    /// </summary>
    private readonly HashSet<string> _pdbClassNames;

    /// <summary>
    ///     Shortest unclassified string allowed to claim an owner by exact text match.
    ///     <para>
    ///         The text matchers below are exact dictionary lookups against inventories we already
    ///         recovered, so an <c>Other</c> string that matches one is just as much evidence as a
    ///         classified one — see <see cref="CanTryTextMatch" />. Very short text is the one place
    ///         that reasoning weakens: "Yes" or "Doc" can equal a real EditorID or dialogue line by
    ///         coincidence rather than identity, so a cross-category promotion needs a few
    ///         characters behind it. Classified hits are unaffected.
    ///     </para>
    /// </summary>
    private const int MinUnclassifiedTextMatchLength = 6;

    public OwnershipTextMatcher(BufferAnalysisContext ctx)
    {
        _ctx = ctx;
        _memory = new RuntimeMemoryContext(new MmfMemoryAccessor(ctx.Accessor), ctx.FileSize, ctx.MinidumpInfo);
        _editorIdTextLookup = BuildEditorIdTextLookup();
        _gmstTextLookup = BuildGmstTextLookup();
        _dialogueTextLookup = BuildDialogueTextLookup();
        _pdbClassNames = new HashSet<string>(
            PdbStructLayouts.Layouts.Values.Select(l => l.ClassName));
    }

    /// <summary>
    ///     Whether a hit may attempt an exact-text claim against the inventory for
    ///     <paramref name="preferred" />.
    ///     <para>
    ///         Until 2026-09-03 each matcher required the hit to already carry its own category,
    ///         which made the shape classifier a gatekeeper on evidence it has nothing to do with:
    ///         these are exact dictionary lookups, so a string that equals a known EditorID *is*
    ///         that EditorID whether or not it looked like one. The classifier is deliberately
    ///         strict — a dialogue line needs 25+ characters with spaces, an EditorID 6+ starting
    ///         uppercase — so short lines and lowercase IDs were classified <c>Other</c> and then
    ///         denied the one test that would have named them. Unclassified text is now allowed to
    ///         try, subject to <see cref="MinUnclassifiedTextMatchLength" />.
    ///     </para>
    /// </summary>
    private static bool CanTryTextMatch(RuntimeStringHit hit, StringCategory preferred)
    {
        if (hit.Category == preferred)
        {
            return true;
        }

        return hit.Category == StringCategory.Other
               && hit.Text.Length >= MinUnclassifiedTextMatchLength;
    }

    /// <summary>
    ///     Match ReferencedOwnerUnknown EditorId strings by text content
    ///     against the known EditorID inventory.
    /// </summary>
    internal RuntimeStringOwnershipClaim? TryEditorIdTextMatch(RuntimeStringHit hit)
    {
        if (_editorIdTextLookup == null || !CanTryTextMatch(hit, StringCategory.EditorId))
        {
            return null;
        }

        if (!_editorIdTextLookup.TryGetValue(hit.Text, out var entry))
        {
            return null;
        }

        return new RuntimeStringOwnershipClaim(
            hit.FileOffset,
            hit.VirtualAddress,
            "TextContentMatch",
            entry.EditorId,
            entry.FormId != 0 ? entry.FormId : null,
            entry.TesFormOffset,
            ClaimSource.TextContentMatch);
    }

    /// <summary>
    ///     Match ReferencedOwnerUnknown GameSetting strings by text content
    ///     against the GMST record inventory and EditorID inventory.
    /// </summary>
    internal RuntimeStringOwnershipClaim? TryGameSettingTextMatch(RuntimeStringHit hit)
    {
        if (!CanTryTextMatch(hit, StringCategory.GameSetting))
        {
            return null;
        }

        // Try GMST record inventory first
        if (_gmstTextLookup != null && _gmstTextLookup.TryGetValue(hit.Text, out var gmst))
        {
            return new RuntimeStringOwnershipClaim(
                hit.FileOffset,
                hit.VirtualAddress,
                "TextContentMatch",
                $"GMST [{gmst.Name}]",
                null,
                gmst.Offset,
                ClaimSource.TextContentMatch,
                "GMST",
                gmst.Name);
        }

        // Fall back to EditorID inventory (GMST records have EditorIDs too)
        if (_editorIdTextLookup != null && _editorIdTextLookup.TryGetValue(hit.Text, out var entry))
        {
            return new RuntimeStringOwnershipClaim(
                hit.FileOffset,
                hit.VirtualAddress,
                "TextContentMatch",
                entry.EditorId,
                entry.FormId != 0 ? entry.FormId : null,
                entry.TesFormOffset,
                ClaimSource.TextContentMatch,
                "GMST",
                entry.EditorId);
        }

        return null;
    }

    /// <summary>
    ///     Match ReferencedOwnerUnknown DialogueLine strings by text content
    ///     against dialogue lines extracted from RuntimeEditorIdEntry inventory.
    /// </summary>
    internal RuntimeStringOwnershipClaim? TryDialogueTextMatch(RuntimeStringHit hit)
    {
        if (_dialogueTextLookup == null || !CanTryTextMatch(hit, StringCategory.DialogueLine))
        {
            return null;
        }

        if (!_dialogueTextLookup.TryGetValue(hit.Text, out var entry))
        {
            return null;
        }

        return new RuntimeStringOwnershipClaim(
            hit.FileOffset,
            hit.VirtualAddress,
            "TextContentMatch",
            $"INFO [{entry.EditorId}]",
            entry.FormId != 0 ? entry.FormId : null,
            entry.TesFormOffset,
            ClaimSource.TextContentMatch,
            "INFO",
            "DialogueLine");
    }

    /// <summary>
    ///     Content-based claiming for file path strings with known game asset extensions.
    ///     These strings have inbound pointers (they're in ReferencedOwnerUnknown) and their
    ///     content pattern strongly identifies them as game asset paths.
    /// </summary>
    internal static RuntimeStringOwnershipClaim? TryAssetPathContentMatch(RuntimeStringHit hit)
    {
        if (hit.Category != StringCategory.FilePath)
        {
            return null;
        }

        // Must contain a path separator or look like a filename with an extension
        var text = hit.Text;
        if (text.Length < 5)
        {
            return null;
        }

        // Check for known game asset extensions (case-insensitive)
        var dotIdx = text.LastIndexOf('.');
        if (dotIdx < 1 || dotIdx >= text.Length - 2)
        {
            return null;
        }

        var ext = text[dotIdx..].ToLowerInvariant();
        var isKnownAssetExt = ext is ".nif" or ".kf" or ".dds" or ".psa" or ".egt" or ".egm"
            or ".bsa" or ".esm" or ".esp" or ".lip" or ".fuz" or ".wav" or ".ogg" or ".mp3"
            or ".spt" or ".tre" or ".tri" or ".tga" or ".bmp" or ".xml" or ".ctl"
            or ".ddx" or ".xdo" or ".psd" or ".txt" or ".ini" or ".lst";

        if (!isKnownAssetExt)
        {
            return null;
        }

        return new RuntimeStringOwnershipClaim(
            hit.FileOffset,
            hit.VirtualAddress,
            "TextContentMatch",
            "AssetPath",
            null,
            null,
            ClaimSource.TextContentMatch,
            "AssetPath",
            ext[1..].ToUpperInvariant());
    }

    /// <summary>
    ///     Low-priority fallback: check if an EditorId string is at cFormEditorID (+16)
    ///     relative to any TESForm vtable among its referrers. Only matches EditorId-category
    ///     strings and runs after all higher-confidence strategies.
    ///     <para>
    ///         Deliberately still category-gated, unlike the exact-text matchers above. This one is
    ///         POSITIONAL — it infers ownership from a string sitting at a plausible field offset
    ///         near a vtable, not from the string equalling anything known — and its own comment
    ///         notes TESForms are densely packed, so it is already the weakest strategy here.
    ///         Opening it to unclassified text would let arbitrary bytes near a form claim an
    ///         owner on position alone.
    ///     </para>
    /// </summary>
    internal RuntimeStringOwnershipClaim? TryCFormEditorIdFallback(RuntimeStringHit hit)
    {
        if (hit.Category != StringCategory.EditorId || hit.OwnerResolution == null)
        {
            return null;
        }

        var allReferrers = hit.OwnerResolution.AllReferrers;
        if (allReferrers is { Count: > 0 })
        {
            foreach (var (_, va, _) in allReferrers)
            {
                if (va < 0 || va > uint.MaxValue)
                {
                    continue;
                }

                var claim = TryCFormEditorIdAtReferrer(hit, (uint)va);
                if (claim != null)
                {
                    return claim;
                }
            }

            return null;
        }

        if (hit.OwnerResolution.ReferrerVa is not (>= 0 and <= uint.MaxValue))
        {
            return null;
        }

        return TryCFormEditorIdAtReferrer(hit, (uint)hit.OwnerResolution.ReferrerVa.Value);
    }

    /// <summary>
    ///     Check if the referrer is at offset +16 (cFormEditorID) from a TESForm vtable.
    /// </summary>
    private RuntimeStringOwnershipClaim? TryCFormEditorIdAtReferrer(RuntimeStringHit hit, uint referrerVa)
    {
        // cFormEditorID is at offset +16 from TESForm base.
        // TESForm base has vtable at +0. So vtable is at referrer - 16.
        if (referrerVa < 16)
        {
            return null;
        }

        // Step back in VA space, not file-offset space. Until 2026-09-04 this read at
        // referrerFileOffset - 16: minidump regions are laid out contiguously by FILE OFFSET while
        // their VAs are arbitrary, so a referrer within 16 bytes of a region start read the
        // PREVIOUS region's trailing bytes and then attributed that unrelated allocation's vtable
        // to this string. ReadBytesAtVaInto fails closed instead of splicing across the boundary.
        var vtableVa = referrerVa - 16u;
        var vtableBytes = new byte[4];
        if (!_memory.ReadBytesAtVaInto(Xbox360MemoryUtils.VaToLong(vtableVa), vtableBytes, 0, 4))
        {
            return null;
        }

        var vtableFileOffset = _ctx.VaToFileOffset(vtableVa);
        var vtablePtr = BinaryPrimitives.ReadUInt32BigEndian(vtableBytes);

        if (!Xbox360MemoryUtils.IsModulePointer(vtablePtr))
        {
            return null;
        }

        var rtti = OwnershipVtableResolver.ResolveVtableMinimal(_ctx, vtablePtr);
        if (rtti == null || !_pdbClassNames.Contains(rtti.Value.ClassName))
        {
            return null;
        }

        // Validate: ObjectOffset should be 0 (primary vtable = TESForm base)
        if (rtti.Value.ObjectOffset != 0)
        {
            return null;
        }

        var layout = PdbStructLayouts.Layouts.Values
            .FirstOrDefault(l => l.ClassName == rtti.Value.ClassName);
        var recordCode = layout?.RecordCode ?? rtti.Value.ClassName;

        return new RuntimeStringOwnershipClaim(
            hit.FileOffset,
            hit.VirtualAddress,
            "SecondPassCFormEditorIdPosition",
            $"{recordCode} ({rtti.Value.ClassName})",
            null,
            vtableFileOffset,
            ClaimSource.SecondPassCFormEditorIdPosition,
            recordCode,
            "TESForm.cFormEditorID");
    }

    private Dictionary<string, RuntimeEditorIdEntry>? BuildEditorIdTextLookup()
    {
        if (_ctx.RuntimeEditorIds is not { Count: > 0 })
        {
            return null;
        }

        var lookup = new Dictionary<string, RuntimeEditorIdEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _ctx.RuntimeEditorIds)
        {
            lookup.TryAdd(entry.EditorId, entry);
        }

        return lookup;
    }

    private Dictionary<string, GmstRecord>? BuildGmstTextLookup()
    {
        if (_ctx.GameSettings is not { Count: > 0 })
        {
            return null;
        }

        var lookup = new Dictionary<string, GmstRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var gmst in _ctx.GameSettings)
        {
            lookup.TryAdd(gmst.Name, gmst);
        }

        return lookup;
    }

    private Dictionary<string, RuntimeEditorIdEntry>? BuildDialogueTextLookup()
    {
        if (_ctx.RuntimeEditorIds is not { Count: > 0 })
        {
            return null;
        }

        var lookup = new Dictionary<string, RuntimeEditorIdEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _ctx.RuntimeEditorIds)
        {
            if (!string.IsNullOrEmpty(entry.DialogueLine))
            {
                lookup.TryAdd(entry.DialogueLine, entry);
            }
        }

        return lookup.Count > 0 ? lookup : null;
    }
}
