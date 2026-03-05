using System.Numerics;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Builds composited NPC full-body models: skeleton + body parts + equipment + head.
///     Also provides skeleton-only debug visualization.
/// </summary>
internal static class NpcBodyBuilder
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Builds the composited full-body model (body + equipment + head) without rendering.
    /// </summary>
    internal static NifRenderableModel? Build(
        NpcAppearance npc,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        ref Dictionary<string, Matrix4x4>? skeletonBoneCache,
        ref Dictionary<string, Matrix4x4>? poseDeltaCache,
        RenderNpcCommand.NpcRenderSettings s)
    {
        // Load skeleton bone transforms (cached across NPCs — same skeleton for all humans)
        if (skeletonBoneCache == null && npc.SkeletonNifPath != null)
        {
            LoadSkeletonBones(npc.SkeletonNifPath, meshesArchive, meshExtractor, s.BindPose,
                out skeletonBoneCache, out poseDeltaCache);
        }

        // Skeleton-only mode: build geometric visualization from bone positions/hierarchy.
        if (s.Skeleton && npc.SkeletonNifPath != null)
        {
            var skelModel = BuildSkeletonVisualization(npc.SkeletonNifPath, meshesArchive,
                meshExtractor, s.BindPose);
            if (skelModel != null)
                return skelModel;
        }

        var poseDeltas = poseDeltaCache;

        // Determine which body slots are covered by equipment
        var coveredSlots = 0u;
        if (!s.NoEquip && npc.EquippedItems != null)
        {
            foreach (var item in npc.EquippedItems)
                coveredSlots |= item.BipedFlags;
        }

        Log.Debug("NPC 0x{0:X8} ({1}): coveredSlots=0x{2:X}, equipment={3}, bodyTex={4}, upperBody={5}",
            npc.NpcFormId, npc.EditorId ?? "?", coveredSlots,
            npc.EquippedItems != null ? string.Join(", ", npc.EquippedItems.Select(e => e.MeshPath)) : "(none)",
            npc.BodyTexturePath ?? "(null)", npc.UpperBodyNifPath ?? "(null)");

        var bodyModel = new NifRenderableModel();

        // Pre-compute EGT-morphed body/hand textures
        var effectiveBodyTex = npc.BodyTexturePath;
        var effectiveHandTex = npc.HandTexturePath;
        if (!s.NoEgt && npc.FaceGenTextureCoeffs != null)
        {
            ApplyBodyEgtMorphs(npc, meshesArchive, meshExtractor, textureResolver, egtCache,
                ref effectiveBodyTex, ref effectiveHandTex);
        }

        // Load body parts with skeleton-driven skinning
        if ((coveredSlots & 0x04) == 0 && npc.UpperBodyNifPath != null)
        {
            LoadAndMergeBodyPart(npc.UpperBodyNifPath, effectiveBodyTex, 0,
                meshesArchive, meshExtractor, textureResolver, skeletonBoneCache, bodyModel);
        }

        if ((coveredSlots & 0x08) == 0 && npc.LeftHandNifPath != null)
        {
            LoadAndMergeBodyPart(npc.LeftHandNifPath, effectiveHandTex, 0,
                meshesArchive, meshExtractor, textureResolver, skeletonBoneCache, bodyModel);
        }

        if ((coveredSlots & 0x10) == 0 && npc.RightHandNifPath != null)
        {
            LoadAndMergeBodyPart(npc.RightHandNifPath, effectiveHandTex, 0,
                meshesArchive, meshExtractor, textureResolver, skeletonBoneCache, bodyModel);
        }

        // Head equipment transform: skeleton's idle-pose Bip01 Head world transform
        Matrix4x4? headEquipTransform = null;
        if (skeletonBoneCache != null &&
            skeletonBoneCache.TryGetValue("Bip01 Head", out var skelIdleHead))
        {
            headEquipTransform = skelIdleHead;
        }

        // Load equipment meshes
        LoadEquipment(npc, meshesArchive, meshExtractor, textureResolver, skeletonBoneCache,
            headEquipTransform, effectiveBodyTex, effectiveHandTex, bodyModel, s);

        // Build head model with hat-aware hair filtering
        var hasHat = (coveredSlots & (0x01 | 0x400 | 0x4000)) != 0;
        var hairFilter = hasHat ? "Hat" : null;

        var headModel = NpcHeadBuilder.Build(npc, meshesArchive, meshExtractor, textureResolver,
            headMeshCache, egmCache, egtCache, s, hairFilter,
            poseDeltas: poseDeltas, idlePoseBones: skeletonBoneCache);

        if (headModel != null && headModel.HasGeometry)
        {
            foreach (var sub in headModel.Submeshes)
            {
                sub.RenderOrder += 1;
                bodyModel.Submeshes.Add(sub);
                bodyModel.ExpandBounds(sub.Positions);
            }
        }

        return bodyModel.HasGeometry ? bodyModel : null;
    }

    /// <summary>
    ///     Loads skeleton NIF, applies idle animation overrides, and computes pose deltas.
    /// </summary>
    private static void LoadSkeletonBones(
        string skeletonNifPath,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        bool bindPose,
        out Dictionary<string, Matrix4x4>? boneCache,
        out Dictionary<string, Matrix4x4>? poseDeltaCache)
    {
        boneCache = null;
        poseDeltaCache = null;

        var skelRaw = NpcRenderHelpers.LoadNifRawFromBsa(skeletonNifPath, meshesArchive, meshExtractor);
        if (skelRaw == null)
        {
            Log.Warn("Failed to load skeleton: {0}", skeletonNifPath);
            return;
        }

        var idleOverrides = bindPose
            ? null
            : LoadIdleAnimationOverrides(skeletonNifPath, meshesArchive, meshExtractor, skelRaw);

        boneCache = NifGeometryExtractor.ExtractNamedBoneTransforms(
            skelRaw.Value.Data, skelRaw.Value.Info, idleOverrides);

        // Compute pose deltas: inverse(skelBind) * skelIdle for each bone
        var skelBindPose = NifGeometryExtractor.ExtractNamedBoneTransforms(
            skelRaw.Value.Data, skelRaw.Value.Info);
        poseDeltaCache = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, idleWorld) in boneCache)
        {
            if (skelBindPose.TryGetValue(name, out var bindWorld))
            {
                Matrix4x4.Invert(bindWorld, out var invBind);
                poseDeltaCache[name] = invBind * idleWorld;
            }
        }

        Log.Debug("Skeleton loaded: {0} bones, {1} pose deltas from {2}{3}",
            boneCache.Count, poseDeltaCache.Count, skeletonNifPath,
            idleOverrides != null ? $" (idle pose: {idleOverrides.Count} overrides)" : " (bind pose)");
    }

    /// <summary>
    ///     Loads idle animation overrides from KF file adjacent to the skeleton NIF.
    ///     Handles female→male KF fallback and embedded sequence fallback.
    /// </summary>
    private static Dictionary<string, NifAnimationParser.AnimPoseOverride>? LoadIdleAnimationOverrides(
        string skeletonNifPath,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        (byte[] Data, NifInfo Info)? skelRaw)
    {
        var skelDir = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
        var idleKfPath = skelDir + "locomotion\\mtidle.kf";
        var idleRaw = NpcRenderHelpers.LoadNifRawFromBsa(idleKfPath, meshesArchive, meshExtractor, true);

        // Female skeletons share male locomotion animations
        if (idleRaw == null && skelDir.Contains("_female", StringComparison.OrdinalIgnoreCase))
        {
            var maleKfPath = skelDir.Replace("_female", "_male", StringComparison.OrdinalIgnoreCase)
                             + "locomotion\\mtidle.kf";
            idleRaw = NpcRenderHelpers.LoadNifRawFromBsa(maleKfPath, meshesArchive, meshExtractor, true);
        }

        Dictionary<string, NifAnimationParser.AnimPoseOverride>? overrides = null;
        if (idleRaw != null)
            overrides = NifAnimationParser.ParseIdlePoseOverrides(idleRaw.Value.Data, idleRaw.Value.Info);

        // Fallback: check skeleton NIF itself (creature skeletons may embed sequences)
        if (overrides == null && skelRaw != null)
            overrides = NifAnimationParser.ParseIdlePoseOverrides(skelRaw.Value.Data, skelRaw.Value.Info);

        return overrides;
    }

    /// <summary>
    ///     Builds a skeleton-only visualization from the skeleton NIF.
    /// </summary>
    private static NifRenderableModel? BuildSkeletonVisualization(
        string skeletonNifPath,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        bool bindPose)
    {
        var skelRaw = NpcRenderHelpers.LoadNifRawFromBsa(skeletonNifPath, meshesArchive, meshExtractor);
        if (skelRaw == null)
            return null;

        var overrides = bindPose
            ? null
            : LoadIdleAnimationOverrides(skeletonNifPath, meshesArchive, meshExtractor, skelRaw);

        var hierarchy = NifGeometryExtractor.ExtractSkeletonHierarchy(
            skelRaw.Value.Data, skelRaw.Value.Info, overrides);
        return hierarchy != null ? BuildSkeletonModel(hierarchy) : null;
    }

    /// <summary>
    ///     Loads a body part NIF with skeleton-driven skinning and merges into target model.
    /// </summary>
    private static void LoadAndMergeBodyPart(
        string nifPath, string? textureOverride, int renderOrder,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, Matrix4x4>? skeletonIdleBones,
        NifRenderableModel targetModel)
    {
        var raw = NpcRenderHelpers.LoadNifRawFromBsa(nifPath, meshesArchive, meshExtractor);
        if (raw == null)
        {
            Log.Warn("Body part NIF failed to load: {0}", nifPath);
            return;
        }

        var partModel = NifGeometryExtractor.Extract(raw.Value.Data, raw.Value.Info, textureResolver,
            externalBoneTransforms: skeletonIdleBones);
        if (partModel == null || !partModel.HasGeometry)
        {
            Log.Warn("Body part NIF has no geometry: {0}", nifPath);
            return;
        }

        Log.Debug("Body part '{0}': {1} submeshes, bounds ({2:F2},{3:F2},{4:F2})→({5:F2},{6:F2},{7:F2})",
            nifPath, partModel.Submeshes.Count,
            partModel.MinX, partModel.MinY, partModel.MinZ,
            partModel.MaxX, partModel.MaxY, partModel.MaxZ);

        foreach (var sub in partModel.Submeshes)
        {
            if (textureOverride != null &&
                NpcRenderHelpers.ShouldApplyBodyTextureOverride(sub.DiffuseTexturePath, textureOverride))
                sub.DiffuseTexturePath = textureOverride;
            sub.RenderOrder = renderOrder;
            targetModel.Submeshes.Add(sub);
            targetModel.ExpandBounds(sub.Positions);
        }
    }

    private static void ApplyBodyEgtMorphs(
        NpcAppearance npc,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache,
        ref string? effectiveBodyTex, ref string? effectiveHandTex)
    {
        if (npc.BodyEgtPath != null && npc.BodyTexturePath != null)
        {
            var key = NpcRenderHelpers.ApplyBodyEgtMorph(npc.BodyEgtPath, npc.BodyTexturePath,
                npc.FaceGenTextureCoeffs!, npc.NpcFormId, "upperbody",
                meshesArchive, meshExtractor, textureResolver, egtCache);
            if (key != null) effectiveBodyTex = key;
        }

        if (npc.LeftHandEgtPath != null && npc.HandTexturePath != null)
        {
            var key = NpcRenderHelpers.ApplyBodyEgtMorph(npc.LeftHandEgtPath, npc.HandTexturePath,
                npc.FaceGenTextureCoeffs!, npc.NpcFormId, "lefthand",
                meshesArchive, meshExtractor, textureResolver, egtCache);
            if (key != null) effectiveHandTex = key;
        }

        if (npc.RightHandEgtPath != null && npc.HandTexturePath != null)
        {
            NpcRenderHelpers.ApplyBodyEgtMorph(npc.RightHandEgtPath, npc.HandTexturePath,
                npc.FaceGenTextureCoeffs!, npc.NpcFormId, "righthand",
                meshesArchive, meshExtractor, textureResolver, egtCache);
        }
    }

    private static void LoadEquipment(
        NpcAppearance npc,
        BsaArchive meshesArchive, BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, Matrix4x4>? skeletonBoneCache,
        Matrix4x4? headEquipTransform,
        string? effectiveBodyTex, string? effectiveHandTex,
        NifRenderableModel bodyModel, RenderNpcCommand.NpcRenderSettings s)
    {
        if (s.NoEquip || npc.EquippedItems == null)
            return;

        // Head | Hair | Headband | Hat | EyeGlasses | Mask
        const uint headEquipFlags = 0x01 | 0x02 | 0x200 | 0x400 | 0x800 | 0x4000;

        foreach (var item in npc.EquippedItems)
        {
            var isHeadEquip = (item.BipedFlags & headEquipFlags) != 0;

            var equipRaw = NpcRenderHelpers.LoadNifRawFromBsa(item.MeshPath, meshesArchive, meshExtractor);
            if (equipRaw == null)
            {
                Log.Warn("Equipment NIF failed to load: {0}", item.MeshPath);
                continue;
            }

            var equipModel = NifGeometryExtractor.Extract(
                equipRaw.Value.Data, equipRaw.Value.Info, textureResolver,
                externalBoneTransforms: skeletonBoneCache);
            if (equipModel == null || !equipModel.HasGeometry)
            {
                Log.Warn("Equipment NIF has no geometry: {0}", item.MeshPath);
                continue;
            }

            // Unskinned head equipment: vertices near origin in head-local space.
            if (isHeadEquip && equipModel.MaxZ < 30f && headEquipTransform.HasValue)
            {
                foreach (var sub in equipModel.Submeshes)
                    NpcRenderHelpers.TransformSubmesh(sub, headEquipTransform.Value);
            }

            Log.Debug("Equipment '{0}': {1} submeshes, bounds ({2:F2},{3:F2},{4:F2})→({5:F2},{6:F2},{7:F2})",
                item.MeshPath, equipModel.Submeshes.Count,
                equipModel.MinX, equipModel.MinY, equipModel.MinZ,
                equipModel.MaxX, equipModel.MaxY, equipModel.MaxZ);

            foreach (var sub in equipModel.Submeshes)
            {
                if (effectiveBodyTex != null &&
                    NpcRenderHelpers.IsEquipmentSkinSubmesh(sub.DiffuseTexturePath))
                {
                    sub.DiffuseTexturePath =
                        sub.DiffuseTexturePath!.Contains("hand", StringComparison.OrdinalIgnoreCase)
                            ? effectiveHandTex ?? effectiveBodyTex
                            : effectiveBodyTex;
                }

                sub.RenderOrder = 5;
                bodyModel.Submeshes.Add(sub);
                bodyModel.ExpandBounds(sub.Positions);
            }
        }
    }

    /// <summary>
    ///     Builds a geometric visualization of the skeleton: small octahedra at joints,
    ///     tapered bone sticks between parent-child pairs, color-coded by body region.
    /// </summary>
    private static NifRenderableModel BuildSkeletonModel(NifGeometryExtractor.SkeletonHierarchy skeleton)
    {
        var model = new NifRenderableModel();
        var positions = new List<float>();
        var indices = new List<ushort>();
        var colors = new List<byte>();

        static (byte R, byte G, byte B) GetBoneColor(string name)
        {
            var n = name.ToUpperInvariant();
            if (n.Contains("HEAD") || n.Contains("HAIR")) return (255, 255, 0);
            if (n.Contains("L CLAVICLE") || n.Contains("L UPPERARM") ||
                n.Contains("L FOREARM") || n.Contains("L HAND") ||
                n.Contains("L FINGER") || n.Contains("L FORE")) return (0, 200, 0);
            if (n.Contains("R CLAVICLE") || n.Contains("R UPPERARM") ||
                n.Contains("R FOREARM") || n.Contains("R HAND") ||
                n.Contains("R FINGER") || n.Contains("R FORE")) return (200, 50, 50);
            if (n.Contains("L THIGH") || n.Contains("L CALF") ||
                n.Contains("L FOOT") || n.Contains("L TOE")) return (0, 200, 200);
            if (n.Contains("R THIGH") || n.Contains("R CALF") ||
                n.Contains("R FOOT") || n.Contains("R TOE")) return (200, 0, 200);
            if (n.Contains("SPINE") || n.Contains("NECK")) return (80, 130, 255);
            if (n.Contains("WEAPON") || n.Contains("CAMERA")) return (255, 160, 0);
            return (220, 220, 220);
        }

        void AddJoint(Vector3 pos, float radius, (byte R, byte G, byte B) color)
        {
            var baseIdx = (ushort)(positions.Count / 3);
            float[] offsets =
            [
                radius, 0, 0, -radius, 0, 0, 0, radius, 0,
                0, -radius, 0, 0, 0, radius, 0, 0, -radius
            ];
            for (var i = 0; i < 6; i++)
            {
                positions.Add(pos.X + offsets[i * 3]);
                positions.Add(pos.Y + offsets[i * 3 + 1]);
                positions.Add(pos.Z + offsets[i * 3 + 2]);
                colors.AddRange([color.R, color.G, color.B, 255]);
            }

            ushort[] faces =
            [
                (ushort)(baseIdx + 4), (ushort)(baseIdx + 0), (ushort)(baseIdx + 2),
                (ushort)(baseIdx + 4), (ushort)(baseIdx + 2), (ushort)(baseIdx + 1),
                (ushort)(baseIdx + 4), (ushort)(baseIdx + 1), (ushort)(baseIdx + 3),
                (ushort)(baseIdx + 4), (ushort)(baseIdx + 3), (ushort)(baseIdx + 0),
                (ushort)(baseIdx + 5), (ushort)(baseIdx + 2), (ushort)(baseIdx + 0),
                (ushort)(baseIdx + 5), (ushort)(baseIdx + 1), (ushort)(baseIdx + 2),
                (ushort)(baseIdx + 5), (ushort)(baseIdx + 3), (ushort)(baseIdx + 1),
                (ushort)(baseIdx + 5), (ushort)(baseIdx + 0), (ushort)(baseIdx + 3)
            ];
            indices.AddRange(faces);
        }

        void AddBoneStick(Vector3 from, Vector3 to,
            float widthFrom, float widthTo, (byte R, byte G, byte B) color)
        {
            var baseIdx = (ushort)(positions.Count / 3);
            var dir = to - from;
            var len = dir.Length();
            if (len < 0.001f) return;

            dir = Vector3.Normalize(dir);
            var up = MathF.Abs(dir.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
            var perpA = Vector3.Normalize(Vector3.Cross(dir, up));
            var perpB = Vector3.Cross(dir, perpA);

            Vector3[] offsets =
            [
                perpA * widthFrom + perpB * widthFrom,
                perpA * widthFrom - perpB * widthFrom,
                -perpA * widthFrom - perpB * widthFrom,
                -perpA * widthFrom + perpB * widthFrom,
                perpA * widthTo + perpB * widthTo,
                perpA * widthTo - perpB * widthTo,
                -perpA * widthTo - perpB * widthTo,
                -perpA * widthTo + perpB * widthTo
            ];
            for (var i = 0; i < 4; i++)
            {
                var v = from + offsets[i];
                positions.Add(v.X);
                positions.Add(v.Y);
                positions.Add(v.Z);
                colors.AddRange([color.R, color.G, color.B, 255]);
            }

            for (var i = 4; i < 8; i++)
            {
                var v = to + offsets[i];
                positions.Add(v.X);
                positions.Add(v.Y);
                positions.Add(v.Z);
                colors.AddRange([color.R, color.G, color.B, 255]);
            }

            ushort[] tris =
            [
                baseIdx, (ushort)(baseIdx + 1), (ushort)(baseIdx + 5),
                baseIdx, (ushort)(baseIdx + 5), (ushort)(baseIdx + 4),
                (ushort)(baseIdx + 1), (ushort)(baseIdx + 2), (ushort)(baseIdx + 6),
                (ushort)(baseIdx + 1), (ushort)(baseIdx + 6), (ushort)(baseIdx + 5),
                (ushort)(baseIdx + 2), (ushort)(baseIdx + 3), (ushort)(baseIdx + 7),
                (ushort)(baseIdx + 2), (ushort)(baseIdx + 7), (ushort)(baseIdx + 6),
                (ushort)(baseIdx + 3), baseIdx, (ushort)(baseIdx + 4),
                (ushort)(baseIdx + 3), (ushort)(baseIdx + 4), (ushort)(baseIdx + 7),
                baseIdx, (ushort)(baseIdx + 2), (ushort)(baseIdx + 1),
                baseIdx, (ushort)(baseIdx + 3), (ushort)(baseIdx + 2),
                (ushort)(baseIdx + 4), (ushort)(baseIdx + 5), (ushort)(baseIdx + 6),
                (ushort)(baseIdx + 4), (ushort)(baseIdx + 6), (ushort)(baseIdx + 7)
            ];
            indices.AddRange(tris);
        }

        foreach (var (name, transform) in skeleton.BoneTransforms)
        {
            var pos = new Vector3(transform.M41, transform.M42, transform.M43);
            AddJoint(pos, 0.8f, GetBoneColor(name));
        }

        foreach (var (parentName, childName) in skeleton.BoneLinks)
        {
            if (!skeleton.BoneTransforms.TryGetValue(parentName, out var parentXform)) continue;
            if (!skeleton.BoneTransforms.TryGetValue(childName, out var childXform)) continue;

            var from = new Vector3(parentXform.M41, parentXform.M42, parentXform.M43);
            var to = new Vector3(childXform.M41, childXform.M42, childXform.M43);
            AddBoneStick(from, to, 0.5f, 0.3f, GetBoneColor(childName));
        }

        if (positions.Count == 0) return model;

        var sub = new RenderableSubmesh
        {
            Positions = positions.ToArray(),
            Triangles = indices.ToArray(),
            VertexColors = colors.ToArray(),
            UseVertexColors = true,
            IsDoubleSided = true,
            RenderOrder = 0
        };
        model.Submeshes.Add(sub);
        model.ExpandBounds(sub.Positions);
        return model;
    }
}
