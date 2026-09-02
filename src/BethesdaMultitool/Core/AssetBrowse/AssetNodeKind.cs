namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Coarse classification of an <see cref="AssetNode" />, chosen by extension in
///     <see cref="AssetTreeBuilder" /> (a magic-sniff refinement hook is reserved there). The
///     buckets are deliberately game-agnostic: the classic-game formats map into these same
///     buckets as support lands (Daggerfall FRM/CIF/CFA/DFA/TIL/RCI → <see cref="Sprite" />,
///     Arena VOC/ACM → <see cref="Audio" />, MVE/FLC/VID → <see cref="Video" />) rather than
///     growing per-game kinds.
/// </summary>
public enum AssetNodeKind
{
    /// <summary>Virtual directory synthesized from path segments; no payload of its own.</summary>
    Folder,

    /// <summary>Container archive (BSA/BA2); browsable as a nested tree once archive expansion lands.</summary>
    Archive,

    /// <summary>Plugin master/file (ESM/ESP).</summary>
    Plugin,

    /// <summary>Texture image (DDS/DDX/PNG/TGA).</summary>
    Texture,

    /// <summary>3D model (NIF/GLB/glTF).</summary>
    Model,

    /// <summary>Audio (WAV/MP3/OGG/XMA; classic VOC/ACM).</summary>
    Audio,

    /// <summary>Video (BIK; classic MVE/FLC/VID/SMK).</summary>
    Video,

    /// <summary>2D sprite/cel data (classic FRM/CIF/CFA/DFA/ZAR/TIL/SPR/RCI).</summary>
    Sprite,

    /// <summary>Map data; no extension maps here yet — reserved for the classic-game map formats.</summary>
    Map,

    /// <summary>Human-readable text/config (TXT/MSG/INI/CFG/XML/JSON/LST/GAM).</summary>
    Text,

    /// <summary>Save game (FOS/FXS).</summary>
    Save,

    /// <summary>Anything unclassified — listed and extractable, but with no preview.</summary>
    Raw
}
