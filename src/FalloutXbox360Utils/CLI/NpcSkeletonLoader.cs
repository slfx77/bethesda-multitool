using System.Numerics;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Skeleton bone loading, idle animation overrides, pose building, and skeleton visualization.
/// </summary>
internal static class NpcSkeletonLoader
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Loads skeleton NIF, applies idle animation overrides, and computes pose deltas.
    /// </summary>
    internal static void LoadSkeletonBones(
        string skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        bool bindPose,
        out Dictionary<string, Matrix4x4>? boneCache,
        out Dictionary<string, Matrix4x4>? poseDeltaCache,
        string? animOverride = null)
    {
        boneCache = null;
        poseDeltaCache = null;

        var skelRaw = NpcMeshHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
        if (skelRaw == null)
        {
            Log.Warn("Failed to load skeleton: {0}", skeletonNifPath);
            return;
        }

        var idleOverrides = bindPose
            ? null
            : LoadIdleAnimationOverrides(skeletonNifPath, meshArchives, skelRaw, animOverride);

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
    ///     Handles female-to-male KF fallback and embedded sequence fallback.
    /// </summary>
    internal static Dictionary<string, NifAnimationParser.AnimPoseOverride>? LoadIdleAnimationOverrides(
        string skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        (byte[] Data, NifInfo Info)? skelRaw,
        string? animOverride = null)
    {
        var skelDir = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);

        // Custom animation override: resolve relative to skeleton directory
        if (animOverride != null)
        {
            var customPath = skelDir + animOverride;
            var customRaw = NpcMeshHelpers.LoadNifRawFromBsa(customPath, meshArchives, true);
            if (customRaw != null)
            {
                Log.Debug("Using custom animation: {0}", customPath);
                return NifAnimationParser.ParseIdlePoseOverrides(customRaw.Value.Data, customRaw.Value.Info);
            }

            Log.Warn("Custom animation not found: {0}", customPath);
        }

        var idleKfPath = skelDir + "locomotion\\mtidle.kf";
        var idleRaw = NpcMeshHelpers.LoadNifRawFromBsa(idleKfPath, meshArchives, true);

        // Female skeletons share male locomotion animations
        if (idleRaw == null && skelDir.Contains("_female", StringComparison.OrdinalIgnoreCase))
        {
            var maleKfPath = skelDir.Replace("_female", "_male", StringComparison.OrdinalIgnoreCase)
                             + "locomotion\\mtidle.kf";
            idleRaw = NpcMeshHelpers.LoadNifRawFromBsa(maleKfPath, meshArchives, true);
        }

        Dictionary<string, NifAnimationParser.AnimPoseOverride>? overrides = null;
        if (idleRaw != null)
            overrides = NifAnimationParser.ParseIdlePoseOverrides(idleRaw.Value.Data, idleRaw.Value.Info);

        // Fallback: check skeleton NIF itself (creature skeletons may embed sequences)
        if (overrides == null && skelRaw != null)
            overrides = NifAnimationParser.ParseIdlePoseOverrides(skelRaw.Value.Data, skelRaw.Value.Info);

        return overrides;
    }

    internal static bool ShouldUseHandToHandIdleArmPose(
        NpcAppearance npc,
        NpcRenderSettings settings)
    {
        return !settings.BindPose &&
               settings.AnimOverride == null &&
               npc.WeaponVisual?.IsVisible == true &&
               npc.WeaponVisual.AttachmentMode == WeaponAttachmentMode.EquippedHandMounted &&
               npc.WeaponVisual.WeaponType == WeaponType.HandToHandMelee;
    }

    internal static Dictionary<string, Matrix4x4> BuildHandToHandEquippedArmBones(
        string skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        Dictionary<string, Matrix4x4> fallbackIdleBones,
        WeaponVisual? weaponVisual)
    {
        var skelRaw = NpcMeshHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
        if (skelRaw == null)
        {
            return fallbackIdleBones;
        }

        var baseIdleOverrides = LoadIdleAnimationOverrides(
            skeletonNifPath,
            meshArchives,
            skelRaw);
        var equippedPoseKfPath = weaponVisual?.EquippedPoseKfPath;
        if (string.IsNullOrWhiteSpace(equippedPoseKfPath))
        {
            equippedPoseKfPath = "h2hequip.kf";
        }

        var relativeEquippedPoseKfPath =
            equippedPoseKfPath?.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) == true
                ? equippedPoseKfPath["meshes\\".Length..]
                : equippedPoseKfPath;
        var armPoseOverrides = LoadNamedAnimationOverrides(
            skeletonNifPath,
            meshArchives,
            relativeEquippedPoseKfPath!);
        if (armPoseOverrides == null || armPoseOverrides.Count == 0)
        {
            return fallbackIdleBones;
        }

        var mergedOverrides = baseIdleOverrides != null
            ? new Dictionary<string, NifAnimationParser.AnimPoseOverride>(
                baseIdleOverrides,
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, NifAnimationParser.AnimPoseOverride>(StringComparer.OrdinalIgnoreCase);

        foreach (var (boneName, pose) in armPoseOverrides)
        {
            // h2hequip.kf is the equip TRANSITION animation — its Weapon bone override
            // (translation 29.3, 0, -6.5) is only active during the draw animation.
            // During idle, h2hidle.kf has NO Weapon bone override, so the Weapon bone
            // stays at its skeleton bind-pose position. Only apply arm bone overrides.
            if (ShouldUseHandToHandEquippedBone(boneName, false))
            {
                mergedOverrides[boneName] = pose;
            }
        }

        if (mergedOverrides.Count == 0)
        {
            return fallbackIdleBones;
        }

        Log.Debug(
            "Layering {0} right-arm overrides onto base idle for hand-to-hand weapon",
            equippedPoseKfPath ?? "h2hequip.kf");
        return NifGeometryExtractor.ExtractNamedBoneTransforms(
            skelRaw.Value.Data,
            skelRaw.Value.Info,
            mergedOverrides);
    }

    internal static bool ShouldUseHandToHandEquippedBone(string boneName, bool includeWeaponOverride)
    {
        return (includeWeaponOverride &&
                string.Equals(boneName, "Weapon", StringComparison.OrdinalIgnoreCase)) ||
               string.Equals(boneName, "Bip01 R Clavicle", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 R UpperArm", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 R Forearm", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 R ForeTwist", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(boneName, "Bip01 R Hand", StringComparison.OrdinalIgnoreCase) ||
               boneName.StartsWith("Bip01 R Finger", StringComparison.OrdinalIgnoreCase) ||
               boneName.StartsWith("Bip01 R Thumb", StringComparison.OrdinalIgnoreCase);
    }

    internal static Dictionary<string, NifAnimationParser.AnimPoseOverride>? LoadNamedAnimationOverrides(
        string skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        string kfRelPath,
        bool sampleLastKeyframe = false)
    {
        var kfPath = ResolveAnimationAssetPath(skeletonNifPath, kfRelPath);
        var raw = NpcMeshHelpers.LoadNifRawFromBsa(kfPath, meshArchives, true);

        if (raw == null &&
            kfPath.Contains(@"characters\_female\", StringComparison.OrdinalIgnoreCase))
        {
            var malePath = kfPath.Replace(
                @"characters\_female\",
                @"characters\_male\",
                StringComparison.OrdinalIgnoreCase);
            raw = NpcMeshHelpers.LoadNifRawFromBsa(malePath, meshArchives, true);
        }

        return raw != null
            ? NifAnimationParser.ParseIdlePoseOverrides(raw.Value.Data, raw.Value.Info, sampleLastKeyframe)
            : null;
    }

    internal static string ResolveAnimationAssetPath(string skeletonNifPath, string kfRelPath)
    {
        var normalizedPath = kfRelPath.Replace('/', '\\').TrimStart('\\');
        if (normalizedPath.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPath;
        }

        if (normalizedPath.StartsWith(@"characters\", StringComparison.OrdinalIgnoreCase))
        {
            return @"meshes\" + normalizedPath;
        }

        var skelDir = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
        return skelDir + normalizedPath;
    }

    internal static string? TryLoadSequenceParentBoneName(
        string? skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        string kfRelPath)
    {
        if (skeletonNifPath == null)
        {
            return null;
        }

        var skelDir = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
        var kfPath = skelDir + kfRelPath;
        var raw = NpcMeshHelpers.LoadNifRawFromBsa(kfPath, meshArchives, true);

        if (raw == null && skelDir.Contains("_female", StringComparison.OrdinalIgnoreCase))
        {
            var malePath = skelDir.Replace("_female", "_male", StringComparison.OrdinalIgnoreCase) + kfRelPath;
            raw = NpcMeshHelpers.LoadNifRawFromBsa(malePath, meshArchives, true);
        }

        return raw != null
            ? TryParseSequenceParentBoneName(raw.Value.Info)
            : null;
    }

    internal static string? TryParseSequenceParentBoneName(NifInfo nif)
    {
        foreach (var value in nif.Strings)
        {
            if (!value.StartsWith("prn:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parentName = value[4..].Trim();
            if (parentName.Length > 0)
            {
                return parentName;
            }
        }

        return null;
    }

    internal static Dictionary<string, NifAnimationParser.AnimPoseOverride> MergePoseOverrides(
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? baseIdle,
        Dictionary<string, NifAnimationParser.AnimPoseOverride> holsterOverrides)
    {
        var merged = new Dictionary<string, NifAnimationParser.AnimPoseOverride>(StringComparer.OrdinalIgnoreCase);
        if (baseIdle != null)
        {
            foreach (var (bone, pose) in baseIdle)
            {
                merged[bone] = pose;
            }
        }

        foreach (var (bone, pose) in holsterOverrides)
        {
            merged[bone] = pose;
        }

        return merged;
    }

    internal static bool TryReadNodeLocalTransform(
        byte[] data,
        NifInfo nif,
        string nodeName,
        out Matrix4x4 localTransform)
    {
        localTransform = Matrix4x4.Identity;
        foreach (var block in nif.Blocks)
        {
            if (!NifSceneGraphWalker.NodeTypes.Contains(block.TypeName))
            {
                continue;
            }

            var name = NifBlockParsers.ReadBlockName(data, block, nif);
            if (!string.Equals(name, nodeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            localTransform = NifBlockParsers.ParseNiAVObjectTransform(data, block, nif.BsVersion, nif.IsBigEndian);
            return true;
        }

        return false;
    }

    internal static Matrix4x4 ApplyPoseOverride(
        Matrix4x4 bindLocalTransform,
        NifAnimationParser.AnimPoseOverride anim)
    {
        var tx = anim.HasTranslation ? anim.Tx : bindLocalTransform.M41;
        var ty = anim.HasTranslation ? anim.Ty : bindLocalTransform.M42;
        var tz = anim.HasTranslation ? anim.Tz : bindLocalTransform.M43;

        var bindScale = anim.HasScale
            ? anim.Scale
            : MathF.Sqrt(bindLocalTransform.M11 * bindLocalTransform.M11 +
                         bindLocalTransform.M21 * bindLocalTransform.M21 +
                         bindLocalTransform.M31 * bindLocalTransform.M31);

        var rot = Matrix4x4.CreateFromQuaternion(anim.Rotation);
        return new Matrix4x4(
            rot.M11 * bindScale, rot.M12 * bindScale, rot.M13 * bindScale, 0,
            rot.M21 * bindScale, rot.M22 * bindScale, rot.M23 * bindScale, 0,
            rot.M31 * bindScale, rot.M32 * bindScale, rot.M33 * bindScale, 0,
            tx, ty, tz, 1);
    }

    /// <summary>
    ///     Builds a skeleton-only visualization from the skeleton NIF.
    /// </summary>
    internal static NifRenderableModel? BuildSkeletonVisualization(
        string skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        bool bindPose)
    {
        var skelRaw = NpcMeshHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
        if (skelRaw == null)
            return null;

        var overrides = bindPose
            ? null
            : LoadIdleAnimationOverrides(skeletonNifPath, meshArchives, skelRaw);

        var hierarchy = NifGeometryExtractor.ExtractSkeletonHierarchy(
            skelRaw.Value.Data, skelRaw.Value.Info, overrides);
        return hierarchy != null ? BuildSkeletonModel(hierarchy) : null;
    }

    /// <summary>
    ///     Builds a geometric visualization of the skeleton: small octahedra at joints,
    ///     tapered bone sticks between parent-child pairs, color-coded by body region.
    /// </summary>
    internal static NifRenderableModel BuildSkeletonModel(NifGeometryExtractor.SkeletonHierarchy skeleton)
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
