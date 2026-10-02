namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     One place a cut-1c cover file's payload bytes can be read from: a container (an XnGine BSA, a Redguard ROB, or
///     the loose file itself) spelled as the manifest spells sources (<c>Sample/...</c> or
///     <c>&lt;SteamLibrary&gt;/&lt;game&gt;/&lt;path&gt;</c>), the entry inside it, and the DIRECTORY INDEX that is the
///     entry's identity. The index matters because ARCH3D.BSA repeats 10 ids over 24 records and 3D.BSA/3D.BS6 repeat
///     names, so a name or id alone reaches only the last copy (the plan's D6). Every candidate is verified against
///     the manifest's payload SHA-256, and a compressed candidate against its own stored SHA-256 too, before it is used.
/// </summary>
/// <param name="Source">The manifest source string, exactly as the manifest spells it.</param>
/// <param name="Entry">
///     The entry name inside the source: a numbered archive's id rendered as text, a named archive's entry name, a ROB
///     segment name, or the loose file's own name.
/// </param>
/// <param name="Index">
///     The directory or segment index inside the container; null exactly when <paramref name="Container" /> is
///     <see cref="Cut1cCoverFile.LooseContainer" />.
/// </param>
/// <param name="Container">One of <see cref="Cut1cCoverFile.Containers" />.</param>
/// <param name="StoredSize">The stored (LZSS-compressed) byte length of this candidate's entry; null when not compressed.</param>
/// <param name="StoredSha256">The lowercase SHA-256 of the stored bytes; null when not compressed.</param>
/// <param name="SegmentType">
///     The ROB segment's type word as the generator measured it (0, 256 or 512 on the cover); null exactly when
///     <paramref name="Container" /> is not <see cref="Cut1cCoverFile.RobContainer" />. The resolver rejects a
///     segment whose parsed type differs, so the pin is checked on every resolution rather than carried unread.
/// </param>
internal sealed record Cut1cCoverSource(
    string Source,
    string Entry,
    int? Index,
    string Container,
    long? StoredSize,
    string? StoredSha256,
    int? SegmentType)
{
    /// <summary>True when this candidate's entry is stored LZSS-compressed and pins the stored bytes.</summary>
    public bool IsCompressed => StoredSha256 is not null;
}
