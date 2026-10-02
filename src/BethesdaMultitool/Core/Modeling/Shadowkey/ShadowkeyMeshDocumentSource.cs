using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     Where one mesh record's document comes from: its name and identities, and how a byte range of the record maps to
///     a <see cref="SceneSourceLocation" /> (a slot entry, a pack slot at an offset, or an inflated <c>.zsk</c> payload).
/// </summary>
/// <param name="Name">The document name (and node 0 and mesh 0's name).</param>
/// <param name="SourceIdentity">The document's exact source identity, or null.</param>
/// <param name="Provenance">The document's source provenance, or null.</param>
/// <param name="LocationSourceId">The source id locations name, or null for no locations.</param>
/// <param name="AssetReference">The asset the record lives in, or null.</param>
/// <param name="BaseOffset">The record's byte offset inside that asset (a slot's offset in the pack; 0 for an entry).</param>
/// <param name="ElementPrefix">
///     A prefix for every location's element identity, or empty: a sky's is <c>inflated:&lt;stem&gt;.zsk:</c>, naming the
///     compressed file whose inflated payload the offsets index (<see cref="ShadowkeyZoneModelReader.SkyElementPrefix" />).
/// </param>
internal sealed record ShadowkeyMeshDocumentSource(
    string Name,
    string? SourceIdentity,
    SceneSourceProvenance? Provenance,
    string? LocationSourceId,
    AssetReference? AssetReference,
    long BaseOffset,
    string ElementPrefix)
{
    /// <summary>The location of <paramref name="length" /> bytes at record offset <paramref name="offset" />, or null.</summary>
    public SceneSourceLocation? Locate(string element, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(element);
        return LocationSourceId is null
            ? null
            : new SceneSourceLocation(LocationSourceId, ElementPrefix + element, BaseOffset + offset, length,
                AssetReference);
    }
}
