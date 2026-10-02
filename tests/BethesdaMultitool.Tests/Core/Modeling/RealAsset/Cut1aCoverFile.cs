using BethesdaMultitool.Core.Modeling.Nif;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     One row of the cut-1a cover manifest (<c>cut1a-cover-manifest.json</c>, schema 2): the version key it covers,
///     its role (cover, floor or declined control), where the manifest first saw it, its pinned SHA-256 and size, and
///     every other place the census saw the same bytes.
/// </summary>
/// <param name="Key">The version key, for example <c>20.2.0.7/uv11/bs34/BE</c>.</param>
/// <param name="Role"><c>cover</c>, <c>floor</c> or <see cref="DeclinedControlRole" />.</param>
/// <param name="Source">The primary manifest source.</param>
/// <param name="Entry">The entry inside the primary source.</param>
/// <param name="Sha256">The pinned lowercase SHA-256 of the file bytes.</param>
/// <param name="Size">The pinned byte length.</param>
/// <param name="AlsoIn">Every other source the census found the same bytes in.</param>
internal sealed record Cut1aCoverFile(string Key, string Role, string Source, string Entry, string Sha256, long Size,
    IReadOnlyList<Cut1aCoverSource> AlsoIn)
{
    /// <summary>The manifest role of a file the probe declines (an out-of-scope key).</summary>
    public const string DeclinedControlRole = "declined-control";

    /// <summary>True for a big-endian (X360 or PS3) key.</summary>
    public bool IsBigEndian => Key.EndsWith("/BE", StringComparison.Ordinal);

    /// <summary>True for a <c>.kf</c> animation stream, which the reader declines until cut 1b.</summary>
    public bool IsAnimationStream => Entry.EndsWith(".kf", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a decline control.</summary>
    public bool IsDeclinedControl => string.Equals(Role, DeclinedControlRole, StringComparison.Ordinal);

    /// <summary>
    ///     The console a big-endian file was shipped on, as the <c>bmt.platform</c> option value
    ///     (<see cref="NifPackedPlatformOption.X360Value" /> or <see cref="NifPackedPlatformOption.Ps3Value" />), read
    ///     off the primary source path exactly as the probe's <c>platform_of</c> does (an <c>X360</c> or <c>PS3</c>
    ///     token in the Sample/Builds name); null for a little-endian key or a source naming neither. A big-endian FNV
    ///     file carries no byte that says which console wrote it, and the two differ only in the packed vertex-color
    ///     byte order, so the platform is the archive's, never a detection.
    /// </summary>
    public string? ConsolePlatform
    {
        get
        {
            if (!IsBigEndian)
            {
                return null;
            }

            if (Source.Contains("X360", StringComparison.OrdinalIgnoreCase))
            {
                return NifPackedPlatformOption.X360Value;
            }

            return Source.Contains("PS3", StringComparison.OrdinalIgnoreCase) ? NifPackedPlatformOption.Ps3Value : null;
        }
    }

    /// <summary>
    ///     The Data-relative virtual path of the file (<c>meshes/...</c>): the unpacked-tree sources were rooted at
    ///     <c>Data/meshes</c>, the archive sources at <c>Data</c>.
    /// </summary>
    public string DataRelativePath =>
        Entry.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ? Entry : "meshes/" + Entry;

    /// <summary>The primary source followed by every <see cref="AlsoIn" /> source, in manifest order.</summary>
    public IEnumerable<Cut1aCoverSource> Candidates => [new Cut1aCoverSource(Source, Entry), .. AlsoIn];

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Key} {Entry}";
    }
}
