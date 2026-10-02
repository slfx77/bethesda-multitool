using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     Builds the document of one Shadowkey mesh record in one of its three shapes (cut-2 plan section 3;
///     <see cref="ShadowkeyMeshDocumentShape" />). The record is parsed by the one decoder the viewer uses
///     (<see cref="ShadowkeyMesh.Parse" />); this builder only maps it.
/// </summary>
/// <remarks>
///     <para>
///         The document: one scene whose roots are the skin nodes; the UV-domain geometry of
///         <see cref="ShadowkeyMeshModelGeometry" />; one image, one material and one mesh per skin
///         (<see cref="ShadowkeyModelImages" />, <see cref="ShadowkeyMeshModelLayers" />); one repeating sampler (UVs reach
///         7.875 x the texture); in the Standalone shape the frame targets and one clip per sequence
///         (<see cref="ShadowkeyMeshModelAnimation" />), driving the default skin's node; the units and basis of
///         <see cref="ShadowkeyModelUnits" />; the native rows (<see cref="ShadowkeyModelNativeState" />) and the
///         diagnostics.
///     </para>
///     <para>
///         Materials are white, opaque, single-sided and unlit: no normals are stored and the engine lights meshes through
///         the zone light table (inferred), so a lit material would invent a lighting model; opaque because the magenta
///         key is a hypothesis the fog table does not support (diagnostic <see cref="ShadowkeyModelDiagnostics.MagentaOpaque" />).
///         Single-sided is Assumed: no field of the record states culling, and faces are emitted as stored. 75 of the 79
///         closed retail records wind counter-clockwise seen from outside under the right-handed basis; the other 4 carry
///         <see cref="ShadowkeyModelDiagnostics.ReversedWinding" /> because a single-sided material culls their outside
///         (cut-2 review finding 5; single against double-sided for them is an owner decision).
///     </para>
///     <para>
///         Before anything is built the record must be documentable (<see cref="RequireDocumentable" />): at least one
///         skin, both texture sides at least 1 and every sequence rate non-zero. <see cref="ShadowkeyMesh.Parse" /> accepts
///         all three and the probe refuses them only inside its 64 KiB prefix, so a larger record reaches the read.
///     </para>
/// </remarks>
internal static class ShadowkeyMeshDocumentBuilder
{
    /// <summary>The sidedness statement the mesh header row carries (its provenance).</summary>
    public const string SidednessEvidence =
        "single-sided, Assumed: no field of the record states culling and faces are emitted as stored; 75 of 79 " +
        "closed retail records wind counter-clockwise seen from outside under the right-handed basis";

    /// <summary>Builds the document.</summary>
    /// <param name="mesh">The parsed record.</param>
    /// <param name="record">The record's bytes (the texel blocks become the images' originals).</param>
    /// <param name="shape">The document shape.</param>
    /// <param name="source">The name, identities and location mapping.</param>
    /// <param name="detail">Whether native rows retain raw bytes.</param>
    /// <param name="cancellationToken">Observed per skin, per frame and every 1,024 vertices.</param>
    /// <exception cref="InvalidDataException">
    ///     A UV index is owned by two vertices or used by no face, or the record is not documentable
    ///     (<see cref="RequireDocumentable" />).
    /// </exception>
    public static ModelDocument Build(ShadowkeyMesh mesh, ReadOnlySpan<byte> record, ShadowkeyMeshDocumentShape shape,
        ShadowkeyMeshDocumentSource source, ModelNativeDetail detail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(source);
        RequireDocumentable(mesh);
        var standalone = shape == ShadowkeyMeshDocumentShape.Standalone;
        var animated = standalone && mesh.FrameCount > 1;
        var geometry = ShadowkeyMeshModelGeometry.Build(mesh, animated, cancellationToken);
        var skins = shape == ShadowkeyMeshDocumentShape.Placement ? 1 : mesh.Textures.Skins.Count;
        var layers = ShadowkeyMeshModelLayers.Build(source.Name, skins, cancellationToken);

        var images = new List<SceneImage>(skins);
        var materials = new List<SceneMaterial>(skins);
        var meshes = new List<SceneMesh>(skins);
        for (var skin = 0; skin < skins; skin++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (offset, length) = mesh.SkinTexelBlock(skin);
            var skinName = ShadowkeyMeshModelLayers.SkinName(source.Name, skin);
            images.Add(ShadowkeyModelImages.Skin(skinName, mesh.Textures.Skins[skin], record.Slice(offset, length),
                mesh.Textures.Width, mesh.Textures.Height,
                source.Locate(string.Create(CultureInfo.InvariantCulture, $"skin:{skin}"), offset, length)));
            materials.Add(new SceneMaterial(skinName, Vector4.One, new SceneTextureBinding(skin, 0),
                SceneAlphaMode.Opaque, 0.5f, doubleSided: false, unlit: true));
            var meshName = skins == 1 ? source.Name : skinName;
            meshes.Add(new SceneMesh(meshName, [ShadowkeyMeshModelGeometry.Primitive(meshName, geometry, skin)]));
        }

        var animations = new List<SceneAnimation>();
        if (animated)
        {
            for (var index = 0; index < mesh.Sequences.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                animations.Add(ShadowkeyMeshModelAnimation.Create(index, mesh.Sequences[index], mesh.FrameCount,
                    layers.ClipNodes));
            }
        }

        IReadOnlyList<SceneNativeState> native = [];
        if (shape != ShadowkeyMeshDocumentShape.Placement)
        {
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(record));
            native = ShadowkeyModelNativeState.Mesh(source, record, sha256, mesh,
                ShadowkeyMeshModelGeometry.UnusedVertices(mesh), ShadowkeyMeshModelGeometry.Winding(mesh), detail);
        }

        var placement = shape == ShadowkeyMeshDocumentShape.Placement;
        return new ModelDocument(ShadowkeyModelFormatMetadata.MeshFormatId, source.Name,
            [new SceneDefinition(source.Name, layers.Roots)], layers.Nodes, meshes, materials, images,
            [new SceneSampler(SceneTextureWrap.Repeat, SceneTextureWrap.Repeat)], animations,
            sourceIdentity: source.SourceIdentity, diagnostics: Diagnostics(mesh, shape),
            units: placement ? ShadowkeyModelUnits.PlacementUnits : ShadowkeyModelUnits.Units,
            sourceBasis: placement ? ShadowkeyModelUnits.PlacementBasis : ShadowkeyModelUnits.MeshBasis,
            nativeStates: native, layerSets: layers.LayerSets)
        {
            SourceProvenance = source.Provenance
        };
    }

    /// <summary>
    ///     Refuses, as invalid data naming the offset, a record no document can be built from: no skin, a texture side of
    ///     0, or a sequence rate of 0 (cut-2 review finding 14; the format's corrupt-input admission rule).
    /// </summary>
    /// <exception cref="InvalidDataException">The record declares one of the three.</exception>
    public static void RequireDocumentable(ShadowkeyMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var header = mesh.Section("texture-header");
        if (mesh.Textures.Skins.Count == 0)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"'{mesh.Name}': the texture header at byte {header.Offset} declares 0 skins; a mesh document needs " +
                $"at least one."));
        }

        if (mesh.Textures.Width == 0 || mesh.Textures.Height == 0)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"'{mesh.Name}': the texture header at byte {header.Offset} declares a {mesh.Textures.Width}x" +
                $"{mesh.Textures.Height} texture; both sides must be at least 1."));
        }

        var table = mesh.Section("sequences");
        for (var index = 0; index < mesh.Sequences.Count; index++)
        {
            if (mesh.Sequences[index].Rate == 0)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"'{mesh.Name}': sequence {index} at byte {table.Offset + 6 * index} has rate 0, which cannot " +
                    $"be timed."));
            }
        }
    }

    /// <summary>The document diagnostics of one shape (plan section 3.7), each code at most once.</summary>
    public static IReadOnlyList<SceneDiagnostic> Diagnostics(ShadowkeyMesh mesh, ShadowkeyMeshDocumentShape shape)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var diagnostics = new List<SceneDiagnostic>();
        if (shape == ShadowkeyMeshDocumentShape.Placement)
        {
            return diagnostics;
        }

        var magenta = new List<int>();
        for (var skin = 0; skin < mesh.Textures.Skins.Count; skin++)
        {
            if (ShadowkeyModelImages.HasMagenta(mesh.Textures.Skins[skin]))
            {
                magenta.Add(skin);
            }
        }

        if (magenta.Count > 0)
        {
            diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.MagentaOpaque,
                "skin(s) " + string.Join(", ", magenta.Select(static k => k.ToString(CultureInfo.InvariantCulture))) +
                " hold 0x0F0F texels, drawn opaque; that the engine keys them is a hypothesis: the fog table does not " +
                "pin 0x0F0F"));
        }

        if (shape == ShadowkeyMeshDocumentShape.Sky)
        {
            return diagnostics;
        }

        if (mesh.Textures.Skins.Count > 1)
        {
            diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.SkinSelection,
                string.Create(CultureInfo.InvariantCulture,
                    $"{mesh.Textures.Skins.Count} whole-mesh skins, each a scene root in the exclusive layer group " +
                    $"{ShadowkeyMeshModelLayers.ExclusiveGroup}; the entity table or a script picks one, and skin 0 is " +
                    $"shown by default. The clips drive skin 0 only: the other skins share its frame targets but no " +
                    $"track drives them, so they hold frame 0 (a GLB layer selection refuses a morph track on a node " +
                    $"it leaves undrawn; the choice awaits the owner's ruling on D4)")));
        }

        if (mesh.FrameCount > 1)
        {
            diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.StepInterpolation,
                "the frames are whole keyframes and the clips hold each one (Step, Assumed); whether the engine blends " +
                "between them is not established (RE-6); no loop policy is declared"));
            if (ShadowkeyMeshModelAnimation.HasSuspectRate(mesh.Sequences))
            {
                var suspect = mesh.Sequences.Where(static s => s.Length > 1 && s.Rate == 1).Select(static s =>
                    string.Create(CultureInfo.InvariantCulture, $"[{s.Start}, {s.EndExclusive})"));
                diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.RateSuspect,
                    "sequence(s) " + string.Join(", ", suspect) + " run at rate 1, as many seconds as they have frames " +
                    "under the frames-per-second reading; carried as stored"));
            }
        }

        if (ShadowkeyMeshModelAnimation.IsHumanoid(mesh.Sequences))
        {
            diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.ActorAssembly,
                "the record carries the humanoid sequence table the tunic bodies, delfran, orc_ice_warrior and the six " +
                "weapon records share; bodies and weapons play it in lockstep, and which weapon rides which body is " +
                "script data, so no composition is made"));
        }

        var winding = ShadowkeyMeshModelGeometry.Winding(mesh);
        if (winding.IsReversed)
        {
            diagnostics.Add(ShadowkeyModelDiagnostics.Create(ShadowkeyModelDiagnostics.ReversedWinding,
                string.Create(CultureInfo.InvariantCulture,
                    $"the record is a closed surface whose frame-0 signed volume is negative (6V = " +
                    $"{winding.SignedVolumeX6}), so its faces wind clockwise seen from outside under the right-handed " +
                    $"basis; the single-sided material (Assumed) culls its outside in GLB and Blender. 4 of 79 closed " +
                    $"retail records do this (door_5units_right, door_7units_right, woodenbucket, ax); that the mirrored " +
                    $"_right doors shipped reversed suggests the engine does not cull them. Single or double-sided is an " +
                    $"owner decision")));
        }

        return diagnostics;
    }
}
