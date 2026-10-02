using System.Globalization;
using BethesdaMultitool.Core.Formats.Archives;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Composition;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     The zone's placements through Shared's <see cref="ModelSceneComposer" /> (cut-2 plan sections 4.1 and 4.4,
///     decision D6): the terrain document at the identity, the sky document through <see cref="ShadowkeyModelUnits.MeshToZone" />
///     alone with <see cref="SceneNodePresentation.IsSky" />, and one placement per resolved <c>.ent</c> row of the
///     Placement-shape document of its slot, built once per slot, so the composer shares meshes, materials and images by
///     reference.
/// </summary>
/// <remarks>
///     <para>
///         Placement identities: <c>terrain</c>, <c>sky</c> and <c>ent:&lt;row&gt;</c>; the composer names each placement's
///         helper root after its identity. Transforms: <see cref="ShadowkeyModelUnits.PlacementMatrix" /> (row vectors, mesh
///         units into zone units, the basis map inside), exact on quarter turns and float32-rounded otherwise. Angle0 and
///         Angle1 are not applied. A placement outside the grid or on a blocked cell is placed as stored.
///     </para>
///     <para>
///         Budgets are checked before composing: more than <see cref="ModelSceneComposer.MaximumPlacements" /> placements
///         (the two fixed ones included) is <see cref="NotSupportedException" />. Measured: the worst zone (raiders) has
///         1,072 placements; with Placement-shape documents (one node, one scene, no native rows or diagnostics) it
///         needs about 3,300 new rows of the composer's 65,536.
///     </para>
/// </remarks>
internal static class ShadowkeyZoneComposition
{
    /// <summary>The terrain placement's identity.</summary>
    public const string TerrainIdentity = "terrain";

    /// <summary>The sky placement's identity.</summary>
    public const string SkyIdentity = "sky";

    /// <summary>The placement identity of <c>.ent</c> row <paramref name="index" />.</summary>
    public static string EntityIdentity(int index)
    {
        return string.Create(CultureInfo.InvariantCulture, $"ent:{index}");
    }

    /// <summary>The source identity of a slot's Placement-shape document.</summary>
    public static string SlotIdentity(int slot)
    {
        return string.Create(CultureInfo.InvariantCulture, $"models.huge#{slot}");
    }

    /// <summary>
    ///     Composes the zone: the terrain, the sky and every resolved placement (unresolved rows are left out; their
    ///     census rows are NativeOnly).
    /// </summary>
    /// <exception cref="NotSupportedException">The zone has more placements than the composer admits.</exception>
    public static ModelCompositionResult Compose(ShadowkeyZoneSet zone, IReadOnlyList<ShadowkeyZonePlacement> placements,
        ModelDocument terrain, ModelDocument sky, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(sky);
        var resolved = placements.Count(static placement => placement.IsResolved);
        if (resolved + 2 > ModelSceneComposer.MaximumPlacements)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"'{zone.Stem}.zmp': {resolved} placements plus the terrain and the sky exceed the composer's " +
                $"{ModelSceneComposer.MaximumPlacements} placements."));
        }

        var documents = new Dictionary<int, ModelDocument>();
        var list = new List<ModelScenePlacement>(resolved + 2)
        {
            new(TerrainIdentity, terrain, System.Numerics.Matrix4x4.Identity),
            new(SkyIdentity, sky, ShadowkeyModelUnits.MeshToZone, presentation: new SceneNodePresentation(isSky: true))
        };
        foreach (var placement in placements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!placement.IsResolved || placement.Slot is not { } slot)
            {
                continue;
            }

            if (!documents.TryGetValue(slot, out var document))
            {
                document = SlotDocument(zone, slot, cancellationToken);
                documents.Add(slot, document);
            }

            list.Add(new ModelScenePlacement(EntityIdentity(placement.Index), document,
                ShadowkeyModelUnits.PlacementMatrix(placement.Entity)));
        }

        return ModelSceneComposer.Compose(zone.Map.ZoneName.Length > 0 ? zone.Map.ZoneName : zone.Stem, list,
            ShadowkeyModelUnits.Units, ShadowkeyModelUnits.ZoneBasis, zone.Stem + ".zmp", cancellationToken);
    }

    /// <summary>The Placement-shape document of one pack slot (frame 0, skin 0).</summary>
    public static ModelDocument SlotDocument(ShadowkeyZoneSet zone, int slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var entry = zone.Pack.Entries[slot];
        var mesh = zone.Pack.GetMesh(slot) ??
                   throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                       $"models.huge slot {slot} is empty."));
        var source = new ShadowkeyMeshDocumentSource(ShadowkeyPackBackend.EntryName(slot, entry.FileName),
            SlotIdentity(slot), null, zone.PackReference.SourceId, zone.PackReference, entry.Offset, string.Empty);
        return ShadowkeyMeshDocumentBuilder.Build(mesh, zone.Pack.GetEntryBytes(slot).Span,
            ShadowkeyMeshDocumentShape.Placement, source, ModelNativeDetail.Metadata, cancellationToken);
    }
}
