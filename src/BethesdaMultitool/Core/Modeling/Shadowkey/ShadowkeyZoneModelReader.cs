using System.Globalization;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Catalog;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     The Shadowkey zone reader, <c>bmt.shadowkey.zone</c> (cut-2 plan <c>docs/design/cut2-shadowkey-reader-plan-20260928.md</c>,
///     section 4): one zone, read from its <c>.zmp</c> with its companions and the pack, as one composed document of the
///     terrain, the sky and every placed mesh.
/// </summary>
/// <remarks>
///     <para>
///         Reading: the game option must be absent, <c>auto</c> or Shadowkey; the <c>.zmp</c> is read under
///         <see cref="MaximumSourceBytes" /> and the zone set through the context's companion resolver
///         (<see cref="ShadowkeyZoneSet" />); the terrain (<see cref="ShadowkeyZoneTerrain" />), the sky (the <c>.zsk</c>
///         payload through <see cref="ShadowkeyMesh.Parse" /> with its detected texture-header variant, the Sky shape of
///         <see cref="ShadowkeyMeshDocumentBuilder" />) and the placements are composed by
///         <see cref="ShadowkeyZoneComposition" />.
///     </para>
///     <para>
///         The document: the composition re-stated under this reader's format id and the zone name, with the zone's
///         units and basis (<see cref="ShadowkeyModelUnits" />), the <c>.zmp</c> path and SHA-256 as its provenance, the
///         composed native rows and diagnostics followed by the zone's own (<see cref="ShadowkeyModelNativeState.Zone" />,
///         <see cref="ShadowkeyModelDiagnostics" />), and the composition's extras (each placement's source and index
///         ranges) unchanged. The per-item cache scope is not used.
///     </para>
///     <para>
///         Inspection (<see cref="ModelReadPurpose.Inspection" />) builds the same document as conversion, standard PNG
///         payloads included, and that is a recorded deviation from the plan (section 2 puts PNG encoding on conversion
///         reads only; cut-2 review finding 7). No codec is decoded: the <c>.ztx</c> indices, the palette and the 0x0RGB
///         words of the sky and of every placed record's skin 0 are read as the integers they are stored as, and each
///         PNG is an unfiltered deflate of them (for a 0x0RGB skin, after a pass that lists its distinct texels). The
///         payloads stay because Shared's GLB planner
///         (<c>ModelGltfImages.PlanCore</c> at 2e7af70) plans an image only from a PNG, DDS or TGA representation, so an
///         inspection read without them would make <c>mesh info</c>'s writer plans report every image unsupported,
///         a census that conversion contradicts.
///     </para>
/// </remarks>
public sealed class ShadowkeyZoneModelReader : IModelSourceReader, IModelSourceFormatMetadataProvider
{
    /// <summary>The largest <c>.zmp</c> the reader loads (Assumed; the largest retail file is 48,524 bytes).</summary>
    public const int MaximumSourceBytes = ShadowkeyModelFormatMetadata.MaximumZoneBytes;

    /// <inheritdoc />
    public string FormatId => ShadowkeyModelFormatMetadata.ZoneFormatId;

    /// <inheritdoc />
    public bool SupportsInspectionWithoutPixelDecoding => true;

    /// <inheritdoc />
    public ModelSourceFormatMetadata FormatMetadata => ShadowkeyModelFormatMetadata.Zone;

    /// <inheritdoc />
    public ModelProbeResult Probe(ModelSourceCandidate candidate)
    {
        return ShadowkeyZoneModelProbe.Probe(candidate);
    }

    /// <inheritdoc />
    public ModelReadResult Read(ModelSourceItem item, ModelReadContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);
        if (item.Reference != context.Item.Reference || !ReferenceEquals(item.Source, context.Item.Source))
        {
            throw new ArgumentException("The reader and context must borrow the same source occurrence.", nameof(item));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ShadowkeyModelUnits.RequireShadowkey(context.AppOptions);
        if (item.Length > MaximumSourceBytes)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The .zmp declares {item.Length} bytes, more than the reader's {MaximumSourceBytes}-byte budget."));
        }

        var zmp = ShadowkeyMeshModelReader.ReadBytes(context.Input, MaximumSourceBytes, cancellationToken);
        var zone = ShadowkeyZoneSet.Read(item, context, zmp, cancellationToken);
        var placements = zone.Resolve();
        var terrain = ShadowkeyZoneTerrain.Build(zone, cancellationToken);
        var sky = Sky(zone, context.NativeDetail, cancellationToken);
        var composed = ShadowkeyZoneComposition.Compose(zone, placements, terrain, sky.Document, cancellationToken)
            .Document;

        var name = zone.Map.ZoneName.Length > 0 ? zone.Map.ZoneName : zone.Stem;
        var diagnostics = composed.Diagnostics.Concat(Diagnostics(zone, placements, sky.Mesh)).ToList();
        var native = composed.NativeStates.Concat(ShadowkeyModelNativeState.Zone(zone, placements, context.NativeDetail));
        var document = new ModelDocument(FormatId, name, composed.Scenes, composed.Nodes, composed.Meshes,
            composed.Materials, composed.Images, composed.Samplers, composed.Animations, composed.DefaultSceneIndex,
            item.Reference.ToString(), composed.ExtrasJson, composed.Skins, diagnostics, ShadowkeyModelUnits.Units,
            ShadowkeyModelUnits.ZoneBasis, native, composed.LayerSets, composed.Palettes)
        {
            SourceProvenance = new SceneSourceProvenance(item.Reference.Path, zone.ZmpSha256),
            ReflectedFaces = composed.ReflectedFaces
        };
        var coverage = ShadowkeyModelCoverage.Zone(item.Reference, zone.TextureBytes.Count,
            placements.Select(static placement => placement.IsResolved).ToList(), zone.Sta is not null,
            ShadowkeyModelCoverage.HasUnplacedPrototypes(zone.Map, zone.Prototypes.Count), cancellationToken);
        return new ModelReadResult(document, coverage);
    }

    /// <summary>
    ///     The zone's sky: the <c>.zsk</c> payload as a mesh record in the Sky shape, with its parse. Every location it
    ///     states indexes the inflated payload, so its element names the file: <c>inflated:&lt;stem&gt;.zsk:record</c>,
    ///     <c>...:positions</c>, <c>...:sequences</c>, <c>...:skin:0</c> (cut-2 review finding 6; the terrain's are
    ///     <c>inflated:&lt;stem&gt;.&lt;ext&gt;</c>, <see cref="ShadowkeyZoneSet.InflatedLocation" />).
    /// </summary>
    internal static (ModelDocument Document, ShadowkeyMesh Mesh) Sky(ShadowkeyZoneSet zone, ModelNativeDetail detail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var payload = zone.Inflated(".zsk");
        var name = zone.Stem + ".zsk";
        var mesh = ShadowkeyMesh.Parse(payload, name, ShadowkeyMesh.DetectTextureHeader(payload, name));
        var reference = zone.Reference(name);
        var source = new ShadowkeyMeshDocumentSource(zone.Stem + " sky", name, null, reference?.SourceId, reference, 0,
            SkyElementPrefix(zone.Stem));
        return (ShadowkeyMeshDocumentBuilder.Build(mesh, payload, ShadowkeyMeshDocumentShape.Sky, source, detail,
            cancellationToken), mesh);
    }

    /// <summary>The element prefix of every sky location: <c>inflated:&lt;stem&gt;.zsk:</c>.</summary>
    internal static string SkyElementPrefix(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        return "inflated:" + stem + ".zsk:";
    }

    /// <summary>The zone's own diagnostics (plan section 4.6), each code at most once.</summary>
    internal static IReadOnlyList<SceneDiagnostic> Diagnostics(ShadowkeyZoneSet zone,
        IReadOnlyList<ShadowkeyZonePlacement> placements, ShadowkeyMesh sky)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(sky);
        var culture = CultureInfo.InvariantCulture;
        var diagnostics = new List<SceneDiagnostic>
        {
            ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.TileTexturingUnresolved,
                "which surface a floor, ceiling or wall uses is unresolved (the .zcp slots are four edge directions x " +
                "two bands, measured 0.94 on walls, floors and ceilings open), so the tiles are untextured neutral " +
                "grays per face kind; every .sur row travels as a material bound to its .ztx image")
        };

        var resolved = placements.Where(static placement => placement.IsResolved).ToList();
        if (resolved.Count > 0)
        {
            diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.ChiralityAssumed,
                string.Create(culture,
                    $"{resolved.Count} placements use the proper map (x, y, z) to (x, -z, y) and yaw -Angle2; the " +
                    $"yaw sign, unit and the up and forward axes are measured, the x mirror is not (248 against 251 " +
                    $"wins), so each placed mesh stays congruent to its own document")));
        }

        var pitchRoll = resolved.Count(static placement => placement.Entity.Angle0 != 0 || placement.Entity.Angle1 != 0);
        if (pitchRoll > 0)
        {
            diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.PitchRollUnapplied,
                string.Create(culture,
                    $"{pitchRoll} placements carry a non-zero Angle0 or Angle1; their axis and unit are not established, " +
                    $"so they are kept in bmt.shadowkey.zone.placements and not applied")));
        }

        var animated = resolved.Count(placement => zone.Pack.GetMesh(placement.Slot!.Value)!.FrameCount > 1);
        if (animated > 0)
        {
            diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.StaticPlacements,
                string.Create(culture,
                    $"{animated} placements resolve to animated records and are shown at frame 0; their clips are in " +
                    $"the records' own documents (an .ent row names an entity, not a sequence; scripts drive it)")));
        }

        if (sky.TextureHeader == ShadowkeyTextureHeader.Uncounted)
        {
            diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.SkyTextureHeader,
                "the sky payload's texture header has no skin count (width and height only, one skin); read as the " +
                "uncounted variant, as all 9 interior skies are"));
        }

        var multiSkin = resolved.Select(static placement => placement.Slot!.Value).Distinct()
            .Where(slot => zone.Pack.GetMesh(slot)!.Textures.Skins.Count > 1).Order().ToList();
        if (multiSkin.Count > 0)
        {
            diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.PlacedSkinDefault,
                "placed slots " + string.Join(", ", multiSkin.Select(slot => slot.ToString(culture))) +
                " carry several skins; each placement shows skin 0 (the entity table or a script picks the skin)"));
        }

        var magenta = resolved.Select(static placement => placement.Slot!.Value).Distinct()
            .Where(slot => ShadowkeyModelImages.HasMagenta(zone.Pack.GetMesh(slot)!.Textures.Skins[0])).Order().ToList();
        if (magenta.Count > 0)
        {
            diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.PlacedMagentaOpaque,
                "placed slots " + string.Join(", ", magenta.Select(slot => slot.ToString(culture))) +
                " hold 0x0F0F texels in skin 0, drawn opaque (the fog table does not pin the key)"));
        }

        var reversed = resolved.Select(static placement => placement.Slot!.Value).Distinct()
            .Where(slot => ShadowkeyMeshModelGeometry.Winding(zone.Pack.GetMesh(slot)!).IsReversed).Order().ToList();
        if (reversed.Count > 0)
        {
            diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.PlacedReversedWinding,
                "placed slots " + string.Join(", ", reversed.Select(slot => slot.ToString(culture))) +
                " are closed surfaces wound clockwise seen from outside; their single-sided materials (Assumed) cull " +
                "their outsides in both writers (the owner decides single or double-sided)"));
        }

        var unresolved = placements.Count - resolved.Count;
        if (unresolved > 0)
        {
            diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.PlacementUnresolved,
                string.Create(culture,
                    $"{unresolved} placements resolve to no resident mesh and are kept in bmt.shadowkey.zone.placements only")));
        }

        return diagnostics;
    }
}
