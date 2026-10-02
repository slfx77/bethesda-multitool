namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     One row of the cut-1c cover manifest (<c>cut1c-cover-manifest.json</c>, <c>cut1c_cover_manifest.py</c> schema 1
///     written at scope cut1c with payloads): the plan's role for the file (design member, proposed member, alternate,
///     decline control or edge control; plan <c>docs/design/cut1c-xngine-reader-plan-20260925.md</c> section 9), its
///     container kind, primary source, entry and directory index, the pinned payload size and SHA-256 (for an LZSS
///     entry the DECOMPRESSED bytes, with the stored bytes pinned separately), every other verified place the same
///     payload bytes live, the named controls it carries, and for a decline control the probe's expected decline
///     reason.
/// </summary>
/// <param name="Role">One of <see cref="Roles" />.</param>
/// <param name="Name">The plan's unique name for the row (the manifest refuses a repeat).</param>
/// <param name="Game">The game the row belongs to: Daggerfall, Battlespire or Redguard.</param>
/// <param name="Population">The census population name (for example <c>daggerfall_arch3d</c>).</param>
/// <param name="Container">One of <see cref="Containers" /> (the primary source's kind).</param>
/// <param name="Source">The primary manifest source (the file's container, or the loose file itself).</param>
/// <param name="Entry">The entry name inside the primary source.</param>
/// <param name="Index">The directory or segment index; null exactly for a loose file.</param>
/// <param name="Tag">The 4-byte version tag (<c>v2.5</c>..<c>v2.7</c>, <c>v5.0</c>, <c>MZ</c>); null for an empty segment.</param>
/// <param name="Size">The pinned payload byte length (decoded length for an LZSS entry; 0 for the empty segment).</param>
/// <param name="Sha256">The pinned lowercase SHA-256 of the payload bytes.</param>
/// <param name="StoredSize">The primary entry's stored (compressed) length; null when not compressed.</param>
/// <param name="StoredSha256">The primary entry's stored SHA-256; null when not compressed.</param>
/// <param name="SegmentType">
///     A ROB segment's type word (0, 256 or 512); null outside ROB rows. It flows into the primary candidate, where
///     <see cref="Cut1cFixtureResolver" /> rejects a parsed segment whose type differs.
/// </param>
/// <param name="AlsoIn">Every other verified candidate holding the same payload bytes.</param>
/// <param name="Controls">The names of the plan's named controls this row is pinned for (empty for most rows).</param>
/// <param name="Declined">For a decline control: the expected decline reason; null otherwise.</param>
/// <param name="Note">The manifest's free-text note for the row (measurement context, never parsed).</param>
internal sealed record Cut1cCoverFile(
    string Role,
    string Name,
    string Game,
    string Population,
    string Container,
    string Source,
    string Entry,
    int? Index,
    string? Tag,
    long Size,
    string Sha256,
    long? StoredSize,
    string? StoredSha256,
    int? SegmentType,
    IReadOnlyList<Cut1cCoverSource> AlsoIn,
    IReadOnlyList<string> Controls,
    string? Declined,
    string Note)
{
    /// <summary>A file of the plan's design cover.</summary>
    public const string DesignRole = "design";

    /// <summary>A file the plan proposes beyond the design's list.</summary>
    public const string ProposedRole = "proposed";

    /// <summary>An alternate the plan names for an uncovered combination.</summary>
    public const string AlternateRole = "alternate";

    /// <summary>A file the probe must decline (NotAModel or Unsupported), with the reason pinned.</summary>
    public const string DeclinedControlRole = "declined-control";

    /// <summary>A file the reader reads that pins a named edge of the plan.</summary>
    public const string EdgeControlRole = "edge-control";

    /// <summary>Every role the manifest may give a row.</summary>
    public static IReadOnlyList<string> Roles { get; } =
        [DesignRole, ProposedRole, AlternateRole, DeclinedControlRole, EdgeControlRole];

    /// <summary>The Daggerfall numbered XnGine BSA (8-byte directory records: u32 id, i32 size).</summary>
    public const string Arch3dContainer = "arch3d-bsa";

    /// <summary>The named XnGine BSA (18-byte records; flag 0x0100 marks Battlespire per-entry LZSS).</summary>
    public const string XnGineBsaContainer = "xngine-bsa";

    /// <summary>A Redguard ROB archive; the payload excludes the 80-byte segment header.</summary>
    public const string RobContainer = "rob";

    /// <summary>A loose file; the source path is the file itself.</summary>
    public const string LooseContainer = "loose";

    /// <summary>Every container kind the manifest may name.</summary>
    public static IReadOnlyList<string> Containers { get; } =
        [Arch3dContainer, XnGineBsaContainer, RobContainer, LooseContainer];

    /// <summary>True for a decline control.</summary>
    public bool IsDeclinedControl => string.Equals(Role, DeclinedControlRole, StringComparison.Ordinal);

    /// <summary>True when the primary entry is stored LZSS-compressed.</summary>
    public bool IsCompressed => StoredSha256 is not null;

    /// <summary>The primary source as a candidate, followed by every <see cref="AlsoIn" />, in manifest order.</summary>
    public IEnumerable<Cut1cCoverSource> Candidates =>
        [new Cut1cCoverSource(Source, Entry, Index, Container, StoredSize, StoredSha256, SegmentType), .. AlsoIn];

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Name} ({Container} {Entry}" + (Index is { } i ? $" #{i})" : ")");
    }
}
