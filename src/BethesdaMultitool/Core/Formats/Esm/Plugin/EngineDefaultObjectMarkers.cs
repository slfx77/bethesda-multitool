namespace BethesdaMultitool.Core.Formats.Esm.Plugin;

/// <summary>
///     FNV engine "default object" markers that give interior cells their render structure —
///     rooms, portals, occlusion planes, collision/multibound volumes. The engine references
///     these by hardcoded FormID. Some are authored as STAT records in FalloutNV.esm
///     (RoomMarker 0x1F, CollisionMarker 0x21, MultiBoundMarker 0x15), but
///     <b>PortalMarker (0x20) and OcclusionMarker (0x17) are engine-ONLY</b> — no authoring
///     record exists for them in any ESM; the engine resolves the FormID internally.
///     <para>
///     When a DMP capture contains one of the engine-only markers, the converter finds no
///     master record for it, treats it as a brand-new prototype record, and emits a
///     <i>duplicate</i> STAT under a fresh plugin FormID. Any structural REFR that referenced
///     the marker (portal <c>XPOD</c>, occlusion <c>XOCP</c>) then gets re-based onto the
///     non-functional duplicate, so the engine no longer recognises it as a portal/occlusion
///     volume — the room/portal/occlusion graph desyncs and rooms cull to black (observed in
///     Gomorrah01: five portal REFRs re-based onto a duplicate "PortalMarker" STAT).
///     </para>
///     <para>
///     This table lets the new-record path detect such a capture by editor ID and alias its
///     source FormID back to the canonical engine FormID instead of emitting a duplicate, so
///     the structural REFRs re-base onto the real engine marker. FormIDs verified against the
///     bases that master's own structural REFRs reference (XOCP→0x17, XPOD→0x20, room→0x1F,
///     collision→0x21, multibound→0x15).
///     </para>
/// </summary>
internal static class EngineDefaultObjectMarkers
{
    private static readonly Dictionary<string, uint> ByEditorId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MultiBoundMarker"] = 0x00000015,
        ["OcclusionMarker"] = 0x00000017,
        ["RoomMarker"] = 0x0000001F,
        ["PortalMarker"] = 0x00000020,
        ["CollisionMarker"] = 0x00000021,
    };

    /// <summary>
    ///     True when <paramref name="editorId" /> names an engine default-object marker, in
    ///     which case <paramref name="canonicalFormId" /> is its hardcoded engine FormID.
    /// </summary>
    public static bool TryGetCanonicalFormId(string? editorId, out uint canonicalFormId)
    {
        canonicalFormId = 0;
        return !string.IsNullOrEmpty(editorId)
               && ByEditorId.TryGetValue(editorId, out canonicalFormId);
    }
}
