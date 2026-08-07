namespace BethesdaMultitool.Core.Formats.Esm.Runtime;

/// <summary>
///     One captured-build layout of <c>BGSAcousticSpace</c> (ASPC, FormType 0x0E).
///     <para>
///     This class grew by <b>appending</b> fields over the captured development window, so the
///     builds do not share a single uniform shift and a scalar shift probe cannot describe them.
///     Each era is the next one with trailing members removed, which moves every field after the
///     removal point:
///     </para>
///     <list type="table">
///         <item>
///             <term>Nov 2009 (<c>xex</c>)</term>
///             <description>
///             Still the Fallout 3 shape — one sound, no <c>bIsInterior</c>:
///             sound@64, region@68, envType@72. 76 bytes.
///             </description>
///         </item>
///         <item>
///             <term>Feb - Apr 2010 (<c>xex5</c> … <c>xex44</c>)</term>
///             <description>
///             <c>bIsInterior</c> and the Noon/Dusk/Night slots exist, <c>pWallaSound</c> and
///             <c>iWallaPop</c> do not: sounds@68-80, region@84, envType@88. 92 bytes.
///             </description>
///         </item>
///         <item>
///             <term>Jul 2010 (the MemDebug PDB)</term>
///             <description>
///             Walla added: sounds@68-84, region@88, envType@92, wallaPop@96. 100 bytes.
///             <b>No captured dump uses this layout</b> — it is the PDB's own, kept as a candidate
///             so a later build would be recognised rather than mis-fitted.
///             </description>
///         </item>
///     </list>
///     <para>
///     Mapped empirically with <c>dmp struct-layout -t ASPC</c>; slot order is proven by content,
///     not by field order — <c>xex32</c>'s <c>ExtDesertDefault</c> reads
///     <c>AMBDesertDefaultDuskLP</c> at Dusk and <c>AMBDesertDefaultNight</c> at Night.
///     </para>
///     <para>
///     <b><c>bIsInterior</c> is deliberately absent.</b> The PDB puts it at 64, immediately before
///     the sound run, and the byte there does read 0 or 1 — but it reads <c>0</c> on all 58 captured
///     acoustic spaces including 44 whose EditorID marks them interior, while retail FalloutNV.esm
///     ships <c>INAM = 1</c> on 99 of 113. Whatever occupies that slot in the captured builds, it is
///     not a populated interior flag, so this reader does not claim to have recovered one.
///     </para>
/// </summary>
internal sealed record RuntimeAcousticSpaceLayout(
    string Label,
    IReadOnlyList<int> SoundOffsets,
    int RegionOffset,
    int EnvTypeOffset,
    int? WallaPopOffset,
    int StructSize)
{
    /// <summary>Nov 2009: inherited Fallout 3 shape — a single looping sound.</summary>
    public static readonly RuntimeAcousticSpaceLayout SingleSound =
        new("SingleSound(Nov2009)", [64], 68, 72, null, 76);

    /// <summary>Feb - Apr 2010: four time-of-day sounds, no Walla. The corpus's dominant era.</summary>
    public static readonly RuntimeAcousticSpaceLayout FourSound =
        new("FourSound(2010)", [68, 72, 76, 80], 84, 88, null, 92);

    /// <summary>Jul 2010 MemDebug PDB: five sounds including Walla, plus <c>iWallaPop</c>.</summary>
    public static readonly RuntimeAcousticSpaceLayout FiveSound =
        new("FiveSound(PdbJul2010)", [68, 72, 76, 80, 84], 88, 92, 96, 100);

    /// <summary>
    ///     Probe candidates. <see cref="FourSound" /> is first so it wins the engine's
    ///     first-declared tie-break, which is the outcome we want when a dump's acoustic spaces are
    ///     entirely null and no candidate can be discriminated.
    /// </summary>
    public static readonly IReadOnlyList<RuntimeAcousticSpaceLayout> Candidates =
        [FourSound, FiveSound, SingleSound];
}
