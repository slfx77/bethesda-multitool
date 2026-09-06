namespace BethesdaMultitool.Core.Games;

/// <summary>
///     The single game/engine identity used across the whole application. Game-specific decisions —
///     plugin framing, the TES3/TES4 parser fork, the armor Damage-Threshold field, and the default
///     landscape texture — resolve from this value via <see cref="GameProfile" /> /
///     <see cref="GameProfiles" />. Detected once (see <see cref="GameDetector" />) and threaded
///     through the pipeline rather than re-derived at each layer.
/// </summary>
public enum BethesdaGame
{
    Unknown = 0,

    /// <summary>Morrowind (TES3): flat records, 16-byte headers, no GRUPs, no FormIDs.</summary>
    Morrowind,

    /// <summary>Oblivion (TES4): 20-byte record + 20-byte GRUP headers (no VCS/version trailer).</summary>
    Oblivion,

    /// <summary>Fallout 3 (TES4): 24-byte headers.</summary>
    Fallout3,

    /// <summary>Fallout: New Vegas (TES4): 24-byte headers.</summary>
    FalloutNewVegas,

    /// <summary>Skyrim / Skyrim Special Edition (TES4): 24-byte headers, localized strings, two moons.</summary>
    Skyrim,

    /// <summary>Fallout 4 (TES4): 24-byte headers, localized strings, BA2 archives.</summary>
    Fallout4,

    /// <summary>Fallout 76 (TES4): 24-byte headers; main master <c>SeventySix.esm</c>.</summary>
    Fallout76,

    /// <summary>Starfield (TES4): 24-byte headers.</summary>
    Starfield,

    // ---- Classic (pre-plugin-era) games. No ESM/ESP record stream exists: content lives in
    // containers (BSA variants, DAT, BOS) and typed data files. Append-only from here — enum
    // values reach serialized reports, so never reorder existing members. ----

    /// <summary>TES: Arena (1994, DOS): GLOBAL.BSA container, palettized IMG/CIF/CFA art, MIF voxel maps.</summary>
    Arena,

    /// <summary>TES II: Daggerfall (1996, DOS): ARENA2 data set — XnGine BSAs (ARCH3D/BLOCKS/MAPS), TEXTURE.nnn.</summary>
    Daggerfall,

    /// <summary>An Elder Scrolls Legend: Battlespire (1997, DOS, XnGine): LZSS-capable BSAs, BSI images, BS6 levels.</summary>
    Battlespire,

    /// <summary>TES Adventures: Redguard (1998, DOS, XnGine): loose .3D/.3DC meshes, ROB archives, TEXBSI, RGM maps.</summary>
    Redguard,

    /// <summary>Fallout (1997): DAT1 archives (big-endian, LZSS), FRM sprites, MAP v19, PRO prototypes.</summary>
    Fallout1,

    /// <summary>Fallout 2 (1998): DAT2 archives (little-endian, zlib), same inner format family as Fallout 1.</summary>
    Fallout2,

    /// <summary>Fallout Tactics: Brotherhood of Steel (2001): BOS archives (plain zip), SPR/TIL/ZAR art, ENT/ESH records.</summary>
    FalloutTactics,

    // ---- The Elder Scrolls Travels (2003-2007, mobile). Still no plugin stream: a J2ME title IS
    // its JAR (a PKZIP of big-endian data tables, standard PNGs and custom sprites), Shadowkey is
    // a Symbian application directory of little-endian per-zone files, and the cancelled PSP
    // Oblivion is a set of UMD ISO trees. Registered 2026-09-05 per
    // docs/handoff_mobile_travels_2026_09_04.md; append-only from here as above. ----

    /// <summary>TES Travels: Stormhold (2003, J2ME / N-Gage, Vir2L): loose big-endian .dat tables, .cus sprites, PNG art.</summary>
    Stormhold,

    /// <summary>TES Travels: Dawnstar (2004, J2ME, Vir2L): datfiles.lmp / imgfiles.lmp lump containers over the Stormhold table family.</summary>
    Dawnstar,

    /// <summary>TES Travels: Shadowkey (2004, N-Gage / Symbian ARM): little-endian per-zone .zon/.ent/.zmp/.ztx families under system\apps\6R51.</summary>
    Shadowkey,

    /// <summary>TES Travels: Oblivion mobile (2006, J2ME, Vir2L): .jtm tile maps, .cml composites, .scr scripts, lang_N.txt strings.</summary>
    OblivionMobile,

    /// <summary>TES Travels: Oblivion PSP (cancelled, 2006-2007 UMD betas): PSP_GAME disc trees staged from the dated ISOs.</summary>
    OblivionPsp,

    // ---- Console spin-off. Like the J2ME titles this install is a single file — a PS2 disc image
    // rather than a JAR — so it resolves through the same archive-mounted path. Appended 2026-09-06.
    // ----

    /// <summary>
    ///     Fallout: Brotherhood of Steel (2004, PS2 / Xbox, Snowblind): a disc of <c>.CLP</c>
    ///     containers, <c>.DDF</c> record stores and <c>.SDB</c> string databases.
    /// </summary>
    FalloutBrotherhoodOfSteel
}
