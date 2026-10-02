using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>Snapshots one Shadowkey mesh frame as a shared <see cref="ModelDocument" />.</summary>
/// <remarks>
///     <para>
///         Shadowkey differs from the NIF path in three ways that matter, each measured from the format rather
///         than assumed.
///     </para>
///     <list type="number">
///         <item>
///             <b>The basis already matches.</b> The second vertex component is the up axis, which is glTF's
///             convention, so nothing here crosses a basis. Applying the NIF path's Z-up to Y-up rotation would
///             rotate correct data into incorrect data.
///         </item>
///         <item>
///             <b>UVs are 8.8 fixed point in texels, not normalised</b>, and they legitimately exceed the texture
///             size — the retail pack has pairs reaching 7.875 times the width, which the game samples by wrapping.
///             glTF expects normalised coordinates, so they are divided by the texture dimensions. That is a
///             change of unit, not a requantisation: the authored eighths survive exactly, and values outside
///             0 to 1 are preserved rather than clamped so the wrap still reads correctly.
///         </item>
///         <item>
///             <b>A face carries separate vertex and UV index triples and no texture index.</b> Extra textures
///             are alternative whole-mesh skins chosen by the entity table, not per-face materials, so a skin is
///             selected by the caller and the rest decline.
///         </item>
///     </list>
///     <para>
///         Keyframe animation is <b>not</b> carried. The pack stores whole-frame copies and its sequence rates are
///         in unrecorded units, so a time-based clip cannot be produced without inventing a frame duration. One
///         frame is snapshotted per call and the animation declines, rather than a guessed rate being baked in.
///     </para>
/// </remarks>
internal static class ShadowkeyNeutralSceneAdapter
{
    /// <summary>Copies one frame of a Shadowkey mesh, or reports why the frame retains its native handling.</summary>
    /// <param name="mesh">The parsed mesh record.</param>
    /// <param name="frame">The frame to snapshot.</param>
    /// <param name="skin">Which alternative skin to resolve as the material's image.</param>
    /// <param name="scene">The neutral snapshot when this returns true.</param>
    /// <param name="unsupportedReason">The declining capability when this returns false.</param>
    /// <param name="cancellationToken">Cancels a large walk.</param>
    /// <param name="sourceIdentity">Optional exact source occurrence supplied by the caller, including pack and slot when known; never inferred from the mesh label.</param>
    /// <param name="magentaIsTransparent">Explicitly enables the decoder's hypothetical magenta color key and matching mask material. The default retains opaque source colors.</param>
    /// <returns>True when the frame was snapshotted; false otherwise.</returns>
    internal static bool TryAdapt(
        ShadowkeyMesh mesh,
        int frame,
        int skin,
        [NotNullWhen(true)] out ModelDocument? scene,
        out string? unsupportedReason,
        CancellationToken cancellationToken,
        string? sourceIdentity = null,
        bool magentaIsTransparent = false)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        cancellationToken.ThrowIfCancellationRequested();

        scene = null;
        unsupportedReason = UnsupportedReason(mesh, frame, skin);
        if (unsupportedReason is not null) return false;

        var triangles = mesh.ToUnrolledTriangles(frame);
        cancellationToken.ThrowIfCancellationRequested();

        // The unrolled form is already in draw order with a trivial index list, so every triangle owns its
        // three vertices and a flat normal is exact rather than averaged.
        var vertices = new SceneVertex[triangles.Positions.Length];
        for (var index = 0; index < triangles.Positions.Length; index += 3)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var normal = FaceNormal(triangles.Positions, index);
            for (var corner = 0; corner < 3; corner++)
            {
                vertices[index + corner] = new SceneVertex(
                    triangles.Positions[index + corner],
                    normal,
                    Vector4.One,
                    Normalise(triangles.TexelUvs[index + corner], mesh.Textures));
            }
        }

        var image = new SceneImage($"{mesh.Name}.skin{skin}", Png(mesh, skin, magentaIsTransparent));
        cancellationToken.ThrowIfCancellationRequested();
        var material = new SceneMaterial(
            $"{mesh.Name}.skin{skin}",
            Vector4.One,
            new SceneTextureBinding(0, 0),
            alphaMode: magentaIsTransparent ? SceneAlphaMode.Mask : SceneAlphaMode.Opaque,
            unlit: true);

        var primitive = new ScenePrimitive($"{mesh.Name}.frame{frame}", vertices, triangles.Indices, 0);
        var metadata = new JsonObject
        {
            ["sourceIdentity"] = sourceIdentity,
            ["selectedFrame"] = frame,
            ["selectedSkin"] = skin,
            ["sourceFrameCount"] = mesh.FrameCount,
            ["magentaIsTransparent"] = magentaIsTransparent
        }.ToJsonString();
        var node = new SceneNode(mesh.Name, Matrix4x4.Identity, meshIndex: 0, extrasJson: metadata);

        scene = new ModelDocument(
            "shadowkey",
            mesh.Name,
            [new SceneDefinition(mesh.Name, [0])],
            [node],
            [new SceneMesh(mesh.Name, [primitive])],
            [material],
            [image],
            // UVs legitimately exceed the 0 to 1 range and the game wraps, so the sampler must wrap too.
            [new SceneSampler(SceneTextureWrap.Repeat, SceneTextureWrap.Repeat)],
            sourceIdentity: sourceIdentity,
            extrasJson: metadata);
        SceneValidation.Validate(scene, cancellationToken);
        return true;
    }

    /// <summary>The exact triangle normal, which the unrolled form makes well defined per face.</summary>
    private static Vector3 FaceNormal(Vector3[] positions, int start)
    {
        var normal = Vector3.Cross(
            positions[start + 1] - positions[start],
            positions[start + 2] - positions[start]);
        // A degenerate face has no meaningful normal. Emitting a zero vector is what the shared validator
        // rejects, so fall back to up rather than propagate it.
        return normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
    }

    /// <summary>
    ///     Converts a texel coordinate to glTF's normalised space. Values outside 0 to 1 are kept, because the
    ///     source wraps and clamping them would move geometry onto the wrong part of the skin.
    /// </summary>
    private static Vector2 Normalise(Vector2 texels, ShadowkeyTextureSet textures)
    {
        return new Vector2(texels.X / textures.Width, texels.Y / textures.Height);
    }

    /// <summary>Resolves one alternative skin to PNG bytes through the format's own decoder.</summary>
    private static byte[] Png(ShadowkeyMesh mesh, int skin, bool magentaIsTransparent)
    {
        return NpcGlbTextureEncoder.EncodePng(mesh.DecodeSkin(skin, magentaIsTransparent).ToDecodedTexture());
    }

    /// <summary>Identifies what this snapshot cannot express before any array is copied.</summary>
    private static string? UnsupportedReason(ShadowkeyMesh mesh, int frame, int skin)
    {
        if (mesh.FrameCount == 0 || mesh.Faces.Count == 0)
            return "An empty Shadowkey mesh retains its native handling.";
        if ((uint)frame >= (uint)mesh.FrameCount)
            return "A frame outside the record's range retains its native handling.";
        if (mesh.Textures.Skins.Count == 0)
            return "A Shadowkey mesh with no skin retains its native handling.";
        if ((uint)skin >= (uint)mesh.Textures.Skins.Count)
            return "A skin outside the record's set retains its native handling.";
        if (mesh.Textures.Width <= 0 || mesh.Textures.Height <= 0)
            return "A skin with no dimensions cannot normalise its texel coordinates.";
        if (mesh.Sequences.Count > 0 && mesh.FrameCount > 1)
            return "Keyframe animation retains its native handling: the pack's sequence rates are in " +
                   "unrecorded units, so a clip cannot be timed without inventing a frame duration.";
        return null;
    }
}
