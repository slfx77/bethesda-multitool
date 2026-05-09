using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Strings;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

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
    ///     Set of all PDB class names (for cFormEditorID fallback validation).
    /// </summary>
    private readonly HashSet<string> _pdbClassNames;

    public OwnershipTextMatcher(BufferAnalysisContext ctx)
    {
        _ctx = ctx;
        _editorIdTextLookup = BuildEditorIdTextLookup();
        _gmstTextLookup = BuildGmstTextLookup();
        _dialogueTextLookup = BuildDialogueTextLookup();
        _pdbClassNames = new HashSet<string>(
            PdbStructLayouts.Layouts.Values.Select(l => l.ClassName));
    }

    /// <summary>
    ///     Match ReferencedOwnerUnknown EditorId strings by text content
    ///     against the known EditorID inventory.
    /// </summary>
    internal RuntimeStringOwnershipClaim? TryEditorIdTextMatch(RuntimeStringHit hit)
    {
        if (_editorIdTextLookup == null || hit.Category != StringCategory.EditorId)
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
        if (hit.Category != StringCategory.GameSetting)
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
        if (_dialogueTextLookup == null || hit.Category != StringCategory.DialogueLine)
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
            foreach (var (fileOffset, va, _) in allReferrers)
            {
                if (va < 0 || va > uint.MaxValue)
                {
                    continue;
                }

                var claim = TryCFormEditorIdAtReferrer(hit, fileOffset, (uint)va);
                if (claim != null)
                {
                    return claim;
                }
            }

            return null;
        }

        if (hit.OwnerResolution.ReferrerFileOffset == null || hit.OwnerResolution.ReferrerVa == null)
        {
            return null;
        }

        return TryCFormEditorIdAtReferrer(hit,
            hit.OwnerResolution.ReferrerFileOffset.Value,
            (uint)hit.OwnerResolution.ReferrerVa.Value);
    }

    /// <summary>
    ///     Check if the referrer is at offset +16 (cFormEditorID) from a TESForm vtable.
    /// </summary>
    private RuntimeStringOwnershipClaim? TryCFormEditorIdAtReferrer(
        RuntimeStringHit hit, long referrerFileOffset, uint referrerVa)
    {
        // cFormEditorID is at offset +16 from TESForm base.
        // TESForm base has vtable at +0. So vtable is at referrer - 16.
        if (referrerVa < 16)
        {
            return null;
        }

        var vtableFileOffset = referrerFileOffset - 16;
        if (vtableFileOffset < 0)
        {
            return null;
        }

        var vtableBytes = new byte[4];
        _ctx.Accessor.ReadArray(vtableFileOffset, vtableBytes, 0, 4);
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
            "SecondPassVtable",
            $"{recordCode} ({rtti.Value.ClassName})",
            null,
            vtableFileOffset,
            ClaimSource.SecondPassVtable,
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
