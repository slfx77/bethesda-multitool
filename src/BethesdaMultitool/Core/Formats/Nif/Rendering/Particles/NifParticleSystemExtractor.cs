using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

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
    ///     Env-gated per-effect origin diagnostic (<c>FALLOUT_VIEWER_NIF_DROP_DIAG=1</c> — shared with the
    ///     shape-drop diagnostic): logs each particle system's resolved placement so an effect rendering
    ///     away from its intended origin can be attributed (wrong parent lookup, root-identity early-out,
    ///     emitter-object frame) without a debugger.
    /// </summary>
    private static readonly bool OriginDiagnosticsEnabled =
        Environment.GetEnvironmentVariable("FALLOUT_VIEWER_NIF_DROP_DIAG") == "1";

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
        bool treatRootsAsIdentity,
        IReadOnlyDictionary<int, NifMaterialAlphaController>? alphaControllersByProperty = null)
    {
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (!NifParticleSystemParser.IsParticleSystem(nif.Blocks[i].TypeName))
            {
                continue;
            }

            // Source provenance must not depend on the time-zero bake. A mixed NIF can contain ordinary
            // geometry plus a controller-delayed particle system whose static bake is empty; without this
            // marker its positive warm-cache entry looks particle-free and live mode never source-decodes it.
            model.ContainsParticleSource = true;

            var def = NifParticleSystemParser.Parse(
                data, nif, i, alphaControllersByProperty);
            if (def?.Emitter is null)
            {
                continue;
            }

            var systemWorld = ResolveSystemWorld(data, nif, i, worldTransforms, nodeChildren, treatRootsAsIdentity);

            // Mesh emitters select authored vertices/edges/faces and can launch along their normals. Preserve
            // the indexed emitter geometry in system-local space; an AABB volume changes both distribution
            // and velocity and is not an engine fallback.
            ResolveMeshEmitterGeometry(data, nif, def, systemWorld, worldTransforms);

            // Volume emitters: re-derive the emitter-object frame from the scene-graph walk. The parser
            // seeded EmitterObjectTransform with the object's raw LOCAL transform, which is only correct
            // when the object is a direct child of the system node. The engine spawns in the emitter
            // OBJECT's world frame — when the object is the system's parent node (self-reference) or lives
            // elsewhere in the graph, the local read double-applies or skips ancestor transforms and the
            // whole effect renders away from its authored origin. Same fix-up mesh emitters already use:
            // express the emitter frame relative to the system (emitterWorld · systemWorld⁻¹).
            ResolveVolumeEmitterFrame(def, systemWorld, worldTransforms, i);

            if (OriginDiagnosticsEnabled)
            {
                var name = NifBlockParsers.ReadBlockName(data, nif.Blocks[i], nif) ?? "<unnamed>";
                var spawn = Vector3.Transform(
                    def.Emitter.EmitterObjectTransform.Translation, systemWorld);
                Console.WriteLine(
                    $"[psys-origin] block {i} '{name}' shape={def.Emitter.Shape} " +
                    $"emitterObj={def.Emitter.EmitterObjectIndex} " +
                    $"systemWorldT=({systemWorld.Translation.X:F1},{systemWorld.Translation.Y:F1},{systemWorld.Translation.Z:F1}) " +
                    $"spawnT=({spawn.X:F1},{spawn.Y:F1},{spawn.Z:F1})");
            }

            var baked = NifParticleBaker.Bake(def);
            if (baked.Count == 0)
            {
                if (!ParticleLiveSettings.Enabled)
                {
                    continue;
                }

                // A controller can author a quiet time-zero frame and become active later. Keep one fully
                // transparent bootstrap quad only in opt-in mode so the decoded submesh can retain its
                // material/runtime owner; the first live tick replaces it or suppresses it with indexCount=0.
                baked =
                [
                    new BakedParticle(Vector3.Zero, 0.01f, Vector4.Zero,
                        new Vector4(0f, 0f, 1f, 1f))
                ];
            }

            var submesh = BuildSubmesh(baked, systemWorld, def);
            if (submesh is null)
            {
                continue;
            }

            submesh.ParticleRuntime = new ParticleRuntimeDefinition(def, systemWorld);
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
    ///     Re-derive a volume emitter's object frame relative to the system from the scene-graph walk
    ///     (<c>emitterWorld · systemWorld⁻¹</c>). Three cases: emitter object == the system block itself
    ///     ⇒ Identity (the parser's raw local read would double-apply the system transform); emitter object
    ///     found in the walk ⇒ world-relative frame (correct wherever the object sits in the graph); not
    ///     found (not a walked node type) ⇒ keep the parser's direct-child local read.
    /// </summary>
    private static void ResolveVolumeEmitterFrame(
        ParticleSystemDefinition def, Matrix4x4 systemWorld,
        IReadOnlyDictionary<int, Matrix4x4> worldTransforms, int psIndex)
    {
        var emitter = def.Emitter;
        if (emitter is null || emitter.EmitterObjectIndex < 0 ||
            emitter.Shape is not (ParticleEmitterShape.Box or ParticleEmitterShape.Sphere
                or ParticleEmitterShape.Cylinder))
        {
            return;
        }

        if (emitter.EmitterObjectIndex == psIndex)
        {
            emitter.EmitterObjectTransform = Matrix4x4.Identity;
            return;
        }

        if (worldTransforms.TryGetValue(emitter.EmitterObjectIndex, out var emitterWorld) &&
            Matrix4x4.Invert(systemWorld, out var invSystemWorld))
        {
            emitter.EmitterObjectTransform = emitterWorld * invSystemWorld;
        }
    }

    /// <summary>
    ///     Decode a mesh emitter's indexed surface in the particle system's LOCAL frame. Positions, normals,
    ///     and triangle topology are retained so the baker can implement every EmitFrom/VelocityType mode.
    /// </summary>
    private static void ResolveMeshEmitterGeometry(
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

        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();

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

            if (sub?.Positions is not { Length: >= 3 } positions)
            {
                continue;
            }

            var vertexBase = vertices.Count;
            for (var v = 0; v + 2 < positions.Length; v += 3)
            {
                vertices.Add(new Vector3(positions[v], positions[v + 1], positions[v + 2]));
                if (sub.Normals is { } sourceNormals && v + 2 < sourceNormals.Length)
                {
                    normals.Add(new Vector3(sourceNormals[v], sourceNormals[v + 1], sourceNormals[v + 2]));
                }
                else
                {
                    normals.Add(Vector3.Zero);
                }
            }

            foreach (var index in sub.Triangles)
            {
                triangles.Add(vertexBase + index);
            }
        }

        if (vertices.Count > 0)
        {
            emitter.MeshVertices = vertices;
            emitter.MeshNormals = normals;
            emitter.MeshTriangles = triangles;
            emitter.MeshBoundsMin = vertices.Aggregate(Vector3.Min);
            emitter.MeshBoundsMax = vertices.Aggregate(Vector3.Max);
        }
    }

    internal static RenderableSubmesh? BuildSubmesh(
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
            var halfY = MathF.Max(0.01f, p.Size * sysScale);
            var halfX = halfY * MathF.Max(0.01f, p.AspectRatio);
            var sinRotation = MathF.Sin(p.Rotation);
            var cosRotation = MathF.Cos(p.Rotation);

            var r = (byte)Math.Clamp(p.Color.X * 255f, 0f, 255f);
            var g = (byte)Math.Clamp(p.Color.Y * 255f, 0f, 255f);
            var b = (byte)Math.Clamp(p.Color.Z * 255f, 0f, 255f);
            var a = (byte)Math.Clamp(p.Color.W * 255f, 0f, 255f);

            var baseV = k * 4;
            for (var c = 0; c < 4; c++)
            {
                var (ox, oy, u, v) = corners[c];
                var vi = baseV + c;
                var unrotatedX = ox * halfX;
                var unrotatedY = oy * halfY;
                var cornerX = cosRotation * unrotatedX - sinRotation * unrotatedY;
                var cornerY = sinRotation * unrotatedX + cosRotation * unrotatedY;

                // Placeholder world-axis-aligned quad for bounds + the CPU still path; the leaf-billboard
                // VS rebuilds it camera-facing from the tangent (center) + bitangent (corner offset).
                positions[vi * 3 + 0] = center.X + cornerX;
                positions[vi * 3 + 1] = center.Y + cornerY;
                positions[vi * 3 + 2] = center.Z;

                normals[vi * 3 + 0] = 0f;
                normals[vi * 3 + 1] = 0f;
                normals[vi * 3 + 2] = 1f;

                var uvRect = p.UvRect == default ? new Vector4(0f, 0f, 1f, 1f) : p.UvRect;
                uvs[vi * 2 + 0] = uvRect.X + u * uvRect.Z;
                uvs[vi * 2 + 1] = uvRect.Y + v * uvRect.W;

                tangents[vi * 3 + 0] = center.X;
                tangents[vi * 3 + 1] = center.Y;
                tangents[vi * 3 + 2] = center.Z;

                bitangents[vi * 3 + 0] = cornerX;
                bitangents[vi * 3 + 1] = cornerY;
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

        // Lighting comes from the attached shader family, not from the blend equation. SandDust02 uses
        // ordinary SRC_ALPHA/INV_SRC_ALPHA compositing but its PCloud01 property is explicitly
        // BSShaderNoLightingProperty (the engine has a dedicated BSSM_NOLIGHTING_PSYS pass). Preserve that
        // unlit contract while leaving standard-alpha particles on lighting shader properties scene-lit.
        // Additive legacy particles without shader metadata remain full-bright as before.
        var isEmissive = def.UsesUnlitShader || def.DstBlendMode == 0;

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
            IsParticleCloud = true,
            IsEmissive = isEmissive,
            IsDoubleSided = true,
            HasAlphaBlend = true,
            SrcBlendMode = def.SrcBlendMode,
            DstBlendMode = def.DstBlendMode,
            MaterialAlpha = 1f,
            MaterialAlphaController = def.MaterialAlphaController,
        };
    }
}
