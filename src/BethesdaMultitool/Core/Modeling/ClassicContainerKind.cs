namespace BethesdaMultitool.Core.Modeling;

/// <summary>
///     The classic container an XnGine mesh entry was read from (cut-1c plan section 2, <c>ClassicContainerFacts</c>;
///     section 6.2, step 2). Each kind answers the game question by itself on retail data: a numbered XnGine BSA is
///     Daggerfall's ARCH3D form, a named XnGine BSA whose directory carries LZSS entries is Battlespire's, and a ROB is
///     Redguard's per-map object archive.
/// </summary>
internal enum ClassicContainerKind
{
    /// <summary>An XnGine BSA with number records (u32 id, i32 size): Daggerfall's ARCH3D.BSA and DAGGER.SND.</summary>
    NumberedXnGineBsa,

    /// <summary>
    ///     An XnGine BSA with name records (12-byte name, u16 compression flag, i32 size): Daggerfall's BLOCKS/MAPS/MONSTER
    ///     archives (no LZSS) and Battlespire's 3D.BSA, 3D.BS6 and siblings (per-entry LZSS).
    /// </summary>
    NamedXnGineBsa,

    /// <summary>A Redguard <c>.ROB</c> archive: 80-byte segment headers, each followed by one <c>.3D</c> payload.</summary>
    RedguardRob
}
