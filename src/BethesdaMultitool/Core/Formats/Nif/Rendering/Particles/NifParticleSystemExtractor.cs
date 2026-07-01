using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;

/// <summary>
///     Bakes every NiParticleSystem in a NIF to a steady-state cloud (<see cref="NifParticleBaker" />) and
///     appends each as a leaf-billboard <see cref="RenderableSubmesh" /> — one camera-facing quad per
///     particle, reusing the SpeedTree leaf-card vertex encoding (tangent = quad center, bitangent = signed
///     corner offset) so the existing leaf-billboard vertex shader re-faces them to the camera each frame.
///     Particles are emissive + vertex-coloured and carry the system's blend mode (additive glow by default).
/// </summary>
internal static class NifParticleSystemExtractor
{
    /// <summary>
    ///     Find every NiParticleSystem, bake it, and append the resulting quad cloud(s) to
    ///     <paramref name="model" />. <paramref name="worldTransforms" /> + <paramref name="nodeChildren" />
    ///     come from the scene-graph walk (used to place each system within the NIF). No-op when the NIF has
    ///     no particle systems.
    /// </summary>
    internal static void Append(
        byte[] data,
        NifInfo nif,
        NifRenderableModel model,
        IReadOnlyDictionary<int, Matrix4x4> worldTransforms,
        IReadOnlyDictionary<int, List<int>> nodeChildren,
        bool treatRootsAsIdentity)
    {
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (!NifParticleSystemParser.IsParticleSystem(nif.Blocks[i].TypeName))
            {
                continue;
            }

            var def = NifParticleSystemParser.Parse(data, nif, i);
            if (def?.Emitter is null)
            {
                continue;
            }

            var systemWorld = ResolveSystemWorld(data, nif, i, worldTransforms, nodeChildren, treatRootsAsIdentity);

            // Mesh emitters (e.g. NV whirlwind columns) emit from a volume mesh, not a point — derive that
            // volume's AABB in the system's local frame so the baker fills the column instead of a blob.
            ResolveMeshEmitterBounds(data, nif, def, systemWorld, worldTransforms);

            var baked = NifParticleBaker.Bake(def);
            if (baked.Count == 0)
            {
                continue;
            }

            var submesh = BuildSubmesh(baked, systemWorld, def);
            if (submesh is null)
            {
                continue;
            }

            model.Submeshes.Add(submesh);
            model.ExpandBounds(submesh.Positions);
        }
    }

    /// <summary>
    ///     World transform of the particle system WITHIN the NIF: its parent node's world transform composed
    ///     with the system's own local transform (mirrors how the walker transforms shapes:
    ///     <c>local * parentWorld</c>). When the system is a placed-REFR scene root (treatRootsAsIdentity),
    ///     its own authored transform is discarded — the REFR placement supplies it downstream.
    /// </summary>
    private static Matrix4x4 ResolveSystemWorld(
        byte[] data, NifInfo nif, int psIndex,
        IReadOnlyDictionary<int, Matrix4x4> worldTransforms,
        IReadOnlyDictionary<int, List<int>> nodeChildren,
        bool treatRootsAsIdentity)
    {
        var parentWorld = Matrix4x4.Identity;
        var hasParent = false;
        foreach (var (parent, children) in nodeChildren)
        {
            if (children.Contains(psIndex) && worldTransforms.TryGetValue(parent, out var pw))
            {
                parentWorld = pw;
                hasParent = true;
                break;
            }
        }

        // A root-level particle system placed via a REFR has its own transform replaced by the placement.
        if (!hasParent && treatRootsAsIdentity)
        {
            return Matrix4x4.Identity;
        }

        var local = NifObjectBlockReader.ParseNiAVObjectTransform(
            data, nif.Blocks[psIndex], nif.BsVersion, nif.BinaryVersion, nif.IsBigEndian, nif.HasInlineStrings);
        return local * parentWorld;
    }

    /// <summary>
    ///     For a mesh emitter, compute the spawn-volume AABB in the particle system's LOCAL frame from the
    ///     emitter mesh geometry and store it on the emitter (<c>MeshBoundsMin/Max</c>) so the baker spawns
    ///     uniformly within the column/volume instead of from a single point. No-op for non-mesh emitters or
    ///     when the geometry can't be read (the baker then falls back to point emission).
    /// </summary>
    private static void ResolveMeshEmitterBounds(
        byte[] data, NifInfo nif, ParticleSystemDefinition def, Matrix4x4 systemWorld,
        IReadOnlyDictionary<int, Matrix4x4> worldTransforms)
    {
        var emitter = def.Emitter;
        if (emitter is null || emitter.Shape != ParticleEmitterShape.Mesh || emitter.EmitterMeshIndices.Count == 0)
        {
            return;
        }

        // Express the emitter-mesh geometry in the system's LOCAL frame: meshWorld · inverse(systemWorld).
        // systemWorld is the SAME transform BuildSubmesh re-applies, so a particle spawned at a mesh vertex
        // lands exactly on that vertex in render space (the shared parent transforms cancel).
        if (!Matrix4x4.Invert(systemWorld, out var invSystem))
        {
            invSystem = Matrix4x4.Identity;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;

        foreach (var meshIndex in emitter.EmitterMeshIndices)
        {
            if (meshIndex < 0 || meshIndex >= nif.Blocks.Count)
            {
                continue;
            }

            var shape = nif.Blocks[meshIndex];
            var dataRef = NifBlockParsers.ParseShapeDataRef(
                data, shape, nif.BsVersion, nif.BinaryVersion, nif.IsBigEndian, nif.HasInlineStrings);
            if (dataRef < 0 || dataRef >= nif.Blocks.Count)
            {
                continue;
            }

            var meshWorld = worldTransforms.TryGetValue(meshIndex, out var mw) ? mw : Matrix4x4.Identity;
            var relative = meshWorld * invSystem;

            var dataBlock = nif.Blocks[dataRef];
            var sub = dataBlock.TypeName switch
            {
                "NiTriStripsData" => NifBlockParsers.ExtractTriStripsData(
                    data, dataBlock, nif.IsBigEndian, nif.BsVersion, nif.BinaryVersion, relative),
                "NiTriShapeData" => NifBlockParsers.ExtractTriShapeData(
                    data, dataBlock, nif.IsBigEndian, nif.BsVersion, nif.BinaryVersion, relative),
                _ => null,
            };

            var positions = sub?.Positions;
            if (positions is null || positions.Length < 3)
            {
                continue;
            }

            for (var v = 0; v + 2 < positions.Length; v += 3)
            {
                var pnt = new Vector3(positions[v], positions[v + 1], positions[v + 2]);
                min = Vector3.Min(min, pnt);
                max = Vector3.Max(max, pnt);
                any = true;
            }
        }

        // Require some positive extent (a fully-degenerate point gives the baker nothing to spread over).
        if (any && (max.X > min.X || max.Y > min.Y || max.Z > min.Z))
        {
            emitter.MeshBoundsMin = min;
            emitter.MeshBoundsMax = max;
        }
    }

    private static RenderableSubmesh? BuildSubmesh(
        IReadOnlyList<BakedParticle> particles, Matrix4x4 systemWorld, ParticleSystemDefinition def)
    {
        var n = particles.Count;
        if (n == 0)
        {
            return null;
        }

        // The bitangent corner offset is a SIZE in NIF-local units (the VS multiplies it by the REFR scale),
        // so bake the system's own scale into it; the center is fully transformed into NIF-local.
        var sysScale = new Vector3(systemWorld.M11, systemWorld.M12, systemWorld.M13).Length();
        if (sysScale <= 0f)
        {
            sysScale = 1f;
        }

        var positions = new float[n * 4 * 3];
        var normals = new float[n * 4 * 3];
        var uvs = new float[n * 4 * 2];
        var tangents = new float[n * 4 * 3];
        var bitangents = new float[n * 4 * 3];
        var colors = new byte[n * 4 * 4];
        var triangles = new ushort[n * 6];

        // (offsetX, offsetY) per corner + the matching UV.
        Span<(float ox, float oy, float u, float v)> corners =
        [
            (-1f, -1f, 0f, 1f),
            (1f, -1f, 1f, 1f),
            (1f, 1f, 1f, 0f),
            (-1f, 1f, 0f, 0f),
        ];

        for (var k = 0; k < n; k++)
        {
            var p = particles[k];
            var center = Vector3.Transform(p.Position, systemWorld);
            var half = MathF.Max(0.01f, p.Size * sysScale);

            var r = (byte)Math.Clamp(p.Color.X * 255f, 0f, 255f);
            var g = (byte)Math.Clamp(p.Color.Y * 255f, 0f, 255f);
            var b = (byte)Math.Clamp(p.Color.Z * 255f, 0f, 255f);
            var a = (byte)Math.Clamp(p.Color.W * 255f, 0f, 255f);

            var baseV = k * 4;
            for (var c = 0; c < 4; c++)
            {
                var (ox, oy, u, v) = corners[c];
                var vi = baseV + c;

                // Placeholder world-axis-aligned quad for bounds + the CPU still path; the leaf-billboard
                // VS rebuilds it camera-facing from the tangent (center) + bitangent (corner offset).
                positions[vi * 3 + 0] = center.X + ox * half;
                positions[vi * 3 + 1] = center.Y + oy * half;
                positions[vi * 3 + 2] = center.Z;

                normals[vi * 3 + 0] = 0f;
                normals[vi * 3 + 1] = 0f;
                normals[vi * 3 + 2] = 1f;

                uvs[vi * 2 + 0] = u;
                uvs[vi * 2 + 1] = v;

                tangents[vi * 3 + 0] = center.X;
                tangents[vi * 3 + 1] = center.Y;
                tangents[vi * 3 + 2] = center.Z;

                bitangents[vi * 3 + 0] = ox * half;
                bitangents[vi * 3 + 1] = oy * half;
                bitangents[vi * 3 + 2] = 0f; // wind weight (particles: none for MVP)

                colors[vi * 4 + 0] = r;
                colors[vi * 4 + 1] = g;
                colors[vi * 4 + 2] = b;
                colors[vi * 4 + 3] = a;
            }

            var ti = k * 6;
            triangles[ti + 0] = (ushort)(baseV + 0);
            triangles[ti + 1] = (ushort)(baseV + 1);
            triangles[ti + 2] = (ushort)(baseV + 2);
            triangles[ti + 3] = (ushort)(baseV + 0);
            triangles[ti + 4] = (ushort)(baseV + 2);
            triangles[ti + 5] = (ushort)(baseV + 3);
        }

        // Additive particles (Dst blend = ONE → 0) are glows — fire, wisps, energy — and render full-bright
        // emissive. Alpha-blended particles (Dst = INV_SRC_ALPHA → 7: dust, smoke, water spray) are NOT
        // emissive: they're shaded by the scene so they darken at night / pick up ambient instead of glowing
        // (the SandDust "too opaque / unlit" fix).
        var isEmissive = def.DstBlendMode == 0;

        return new RenderableSubmesh
        {
            ShapeName = "ParticleCloud",
            Positions = positions,
            Triangles = triangles,
            Normals = normals,
            UVs = uvs,
            Tangents = tangents,
            Bitangents = bitangents,
            VertexColors = colors,
            UseVertexColors = true,
            DiffuseTexturePath = def.DiffuseTexturePath,
            IsLeafBillboard = true,
            IsEmissive = isEmissive,
            IsDoubleSided = true,
            HasAlphaBlend = true,
            SrcBlendMode = def.SrcBlendMode,
            DstBlendMode = def.DstBlendMode,
            MaterialAlpha = 1f,
        };
    }
}
