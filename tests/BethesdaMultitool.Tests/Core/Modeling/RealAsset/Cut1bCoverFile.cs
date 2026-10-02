namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     One row of the cut-1b cover manifest (<c>cut1b-cover-manifest.json</c>, <c>nif_cover.py</c> schema 2 written at
///     <c>--scope cut1b --payloads</c>): the version key it belongs to (the cut-1a key plus the file kind, for example
///     <c>20.2.0.7/uv11/bs34/LE/.kf</c>), its role, where the census first saw it, its pinned SHA-256 and size, every
///     other place the census saw the same bytes, the named controls it carries, for a <c>.kf</c> the skeleton it
///     resolves against, and for a decline control the probe's decline reason.
/// </summary>
/// <param name="Key">The version key with the file kind as its last segment.</param>
/// <param name="Role">One of <see cref="Roles" />.</param>
/// <param name="Source">The primary manifest source (the file's container).</param>
/// <param name="Entry">The entry inside the primary source.</param>
/// <param name="Sha256">The pinned lowercase SHA-256 of the file bytes.</param>
/// <param name="Size">The pinned byte length.</param>
/// <param name="AlsoIn">Every other source the census found the same bytes in.</param>
/// <param name="Controls">The names of the plan's named controls this file is pinned for (empty for most files).</param>
/// <param name="Skeleton">The resolved skeleton of a <c>.kf</c> that needs one; null otherwise or when none was found.</param>
/// <param name="SkeletonMissing">Why a <c>.kf</c> that needs a skeleton has none; null otherwise.</param>
/// <param name="SkeletonFor">For a skeleton: the SHA-256 of every manifest <c>.kf</c> that resolves against it.</param>
/// <param name="SkeletonCandidateFor">
///     For another copy of a provisional skeleton pin's path: the SHA-256 of every manifest <c>.kf</c> whose
///     <see cref="Cut1bSkeletonCompanion.Alternatives" /> name it.
/// </param>
/// <param name="Declined">For a decline control: the probe's decline reason, verbatim; null otherwise.</param>
internal sealed record Cut1bCoverFile(
    string Key,
    string Role,
    string Source,
    string Entry,
    string Sha256,
    long Size,
    IReadOnlyList<Cut1bCoverSource> AlsoIn,
    IReadOnlyList<string> Controls,
    Cut1bSkeletonCompanion? Skeleton,
    string? SkeletonMissing,
    IReadOnlyList<string> SkeletonFor,
    IReadOnlyList<string> SkeletonCandidateFor,
    string? Declined)
{
    /// <summary>A file of the per-key minimum cover.</summary>
    public const string CoverRole = "cover";

    /// <summary>A (game, platform) floor file.</summary>
    public const string FloorRole = "floor";

    /// <summary>
    ///     A file the probe and the reader decline. The five 20.0.0.4 <c>.kf</c> carried this role until cut 2 (2026-09-28)
    ///     read them; the checked-in cut-1b manifest now has no file in this role, and the parser keeps it for a future
    ///     wholly declined key.
    /// </summary>
    public const string DeclinedControlRole = "declined-control";

    /// <summary>
    ///     The skeleton companion (or another copy of a provisional skeleton pin) of one or more manifest <c>.kf</c>.
    /// </summary>
    public const string SkeletonRole = "skeleton";

    /// <summary>A named control of the plan that no other role brought in.</summary>
    public const string ControlRole = "control";

    /// <summary>The file kind of an animation stream.</summary>
    public const string AnimationStreamKind = ".kf";

    /// <summary>Every role the manifest may give a file.</summary>
    public static IReadOnlyList<string> Roles { get; } =
        [CoverRole, FloorRole, DeclinedControlRole, SkeletonRole, ControlRole];

    /// <summary>The file kind the key carries as its last segment: <c>.kf</c> or <c>.nif</c>.</summary>
    public string FileKind => Key[(Key.LastIndexOf('/') + 1)..];

    /// <summary>True for a <c>.kf</c> animation stream.</summary>
    public bool IsAnimationStream => string.Equals(FileKind, AnimationStreamKind, StringComparison.Ordinal);

    /// <summary>True for a big-endian (X360 or PS3) key.</summary>
    public bool IsBigEndian => Key.Contains("/BE/", StringComparison.Ordinal);

    /// <summary>True for a decline control.</summary>
    public bool IsDeclinedControl => string.Equals(Role, DeclinedControlRole, StringComparison.Ordinal);

    /// <summary>True for a <c>.kf</c> the reader will read, which therefore needs a skeleton.</summary>
    public bool NeedsSkeleton => IsAnimationStream && !IsDeclinedControl;

    /// <summary>The primary source followed by every <see cref="AlsoIn" /> source, in manifest order.</summary>
    public IEnumerable<Cut1bCoverSource> Candidates => [new Cut1bCoverSource(Source, Entry), .. AlsoIn];

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Key} {Entry}";
    }
}
