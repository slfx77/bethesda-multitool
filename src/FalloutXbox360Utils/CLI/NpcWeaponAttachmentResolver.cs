using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;

/// <summary>
///     Weapon and equipment attachment node resolution, holster pose loading,
///     and attachment transform computation.
/// </summary>
internal static class NpcWeaponAttachmentResolver
{
    private static readonly Logger Log = Logger.Instance;

    internal static bool TryResolveWeaponAttachmentNode(
        WeaponVisual weaponVisual,
        out string attachmentNodeName,
        out string? omitReason)
    {
        if (!string.IsNullOrWhiteSpace(weaponVisual.EmbeddedWeaponNode))
        {
            attachmentNodeName = weaponVisual.EmbeddedWeaponNode.Trim();
            omitReason = null;
            return true;
        }

        if (weaponVisual.IsEmbeddedWeapon)
        {
            attachmentNodeName = "";
            omitReason =
                $"embedded weapon '{weaponVisual.EditorId ?? weaponVisual.MeshPath ?? "?"}' has no attachment node";
            return false;
        }

        attachmentNodeName = "Weapon";
        omitReason = null;
        return true;
    }

    internal static bool TryResolveEquippedWeaponAttachmentTransform(
        WeaponVisual weaponVisual,
        Dictionary<string, Matrix4x4> idleBoneTransforms,
        string? skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        out string attachmentNodeName,
        out Matrix4x4 attachmentTransform,
        out string? omitReason)
    {
        if (weaponVisual.WeaponType == WeaponType.HandToHandMelee &&
            TryResolveHandToHandProcessWeaponAttachmentTransform(
                idleBoneTransforms,
                skeletonNifPath,
                meshArchives,
                !string.IsNullOrWhiteSpace(weaponVisual.EquippedPoseKfPath),
                weaponVisual.PreferEquippedForearmMount,
                out attachmentNodeName,
                out attachmentTransform,
                out omitReason))
        {
            return true;
        }

        return TryResolveEquippedWeaponAttachmentTransform(
            weaponVisual,
            idleBoneTransforms,
            out attachmentNodeName,
            out attachmentTransform,
            out omitReason);
    }

    internal static bool TryResolveEquippedWeaponAttachmentTransform(
        WeaponVisual weaponVisual,
        Dictionary<string, Matrix4x4> idleBoneTransforms,
        out string attachmentNodeName,
        out Matrix4x4 attachmentTransform,
        out string? omitReason)
    {
        attachmentNodeName = string.Empty;
        attachmentTransform = default;

        if (weaponVisual.IsEmbeddedWeapon && string.IsNullOrWhiteSpace(weaponVisual.EmbeddedWeaponNode))
        {
            omitReason =
                $"embedded weapon '{weaponVisual.EditorId ?? weaponVisual.MeshPath ?? "?"}' has no attachment node";
            return false;
        }

        var candidates = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            var trimmed = candidate.Trim();
            if (seen.Add(trimmed))
            {
                candidates.Add(trimmed);
            }
        }

        AddCandidate(weaponVisual.EmbeddedWeaponNode);
        AddCandidate("Weapon");
        AddCandidate("Bip01 R Hand");
        AddCandidate("Bip01 R ForeTwist");

        foreach (var candidate in candidates)
        {
            if (idleBoneTransforms.TryGetValue(candidate, out attachmentTransform))
            {
                attachmentNodeName = candidate;
                omitReason = null;
                return true;
            }
        }

        omitReason =
            $"no equipped attachment node in base pose for {weaponVisual.MeshPath ?? weaponVisual.EditorId ?? "?"}";
        return false;
    }

    internal static bool TryResolveEquipmentAttachmentTransform(
        EquippedItem item,
        IReadOnlyDictionary<string, Matrix4x4> idleBoneTransforms,
        out string attachmentNodeName,
        out Matrix4x4 attachmentTransform,
        out string? omitReason)
    {
        attachmentNodeName = string.Empty;
        attachmentTransform = default;

        string[] candidates = item.AttachmentMode switch
        {
            EquipmentAttachmentMode.LeftWristRigid => ["Bip01 L ForeTwist", "Bip01 L Forearm", "Bip01 L Hand"],
            EquipmentAttachmentMode.RightWristRigid => ["Bip01 R ForeTwist", "Bip01 R Forearm", "Bip01 R Hand"],
            _ => []
        };

        foreach (var candidate in candidates)
        {
            if (!idleBoneTransforms.TryGetValue(candidate, out attachmentTransform))
            {
                continue;
            }

            attachmentNodeName = candidate;
            omitReason = null;
            return true;
        }

        omitReason = $"no wrist attachment node in base pose for {item.MeshPath}";
        return false;
    }

    internal static bool TryResolveHandToHandProcessWeaponAttachmentTransform(
        IReadOnlyDictionary<string, Matrix4x4> idleBoneTransforms,
        string? skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        bool preferProcessStyleRebuild,
        bool preferEquippedForearmMount,
        out string attachmentNodeName,
        out Matrix4x4 attachmentTransform,
        out string? omitReason)
    {
        attachmentNodeName = string.Empty;
        attachmentTransform = default;

        if (skeletonNifPath == null)
        {
            omitReason = "missing skeleton for hand-to-hand attachment";
            return false;
        }

        var skelRaw = NpcRenderHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
        if (skelRaw == null)
        {
            omitReason = $"failed to load skeleton '{skeletonNifPath}' for hand-to-hand attachment";
            return false;
        }

        if (preferProcessStyleRebuild &&
            idleBoneTransforms.TryGetValue("Weapon", out attachmentTransform))
        {
            Log.Debug(
                "Weapon bone matrix:\n  [{0:F3},{1:F3},{2:F3},{3:F3}]\n  [{4:F3},{5:F3},{6:F3},{7:F3}]\n  [{8:F3},{9:F3},{10:F3},{11:F3}]\n  [{12:F3},{13:F3},{14:F3},{15:F3}]",
                attachmentTransform.M11, attachmentTransform.M12, attachmentTransform.M13, attachmentTransform.M14,
                attachmentTransform.M21, attachmentTransform.M22, attachmentTransform.M23, attachmentTransform.M24,
                attachmentTransform.M31, attachmentTransform.M32, attachmentTransform.M33, attachmentTransform.M34,
                attachmentTransform.M41, attachmentTransform.M42, attachmentTransform.M43, attachmentTransform.M44);
            if (idleBoneTransforms.TryGetValue("Bip01 R Hand", out var handTransform))
            {
                Log.Debug(
                    "Bip01 R Hand matrix:\n  [{0:F3},{1:F3},{2:F3},{3:F3}]\n  [{4:F3},{5:F3},{6:F3},{7:F3}]\n  [{8:F3},{9:F3},{10:F3},{11:F3}]\n  [{12:F3},{13:F3},{14:F3},{15:F3}]",
                    handTransform.M11, handTransform.M12, handTransform.M13, handTransform.M14,
                    handTransform.M21, handTransform.M22, handTransform.M23, handTransform.M24,
                    handTransform.M31, handTransform.M32, handTransform.M33, handTransform.M34,
                    handTransform.M41, handTransform.M42, handTransform.M43, handTransform.M44);
            }

            attachmentNodeName = "Weapon (posed scene graph)";
            omitReason = null;
            return true;
        }

        if (!NpcSkeletonLoader.TryReadNodeLocalTransform(skelRaw.Value.Data, skelRaw.Value.Info, "Weapon",
                out var bindLocalWeaponTransform))
        {
            omitReason = "missing Weapon node in skeleton for hand-to-hand attachment";
            return false;
        }

        var equipOverrides = NpcSkeletonLoader.LoadNamedAnimationOverrides(
            skeletonNifPath,
            meshArchives,
            "h2hequip.kf",
            true);
        var equipParentOverrideBone = NpcSkeletonLoader.TryLoadSequenceParentBoneName(
            skeletonNifPath,
            meshArchives,
            "h2hequip.kf");
        var parentBoneCandidates = ResolveHandToHandWeaponParentBoneCandidates(
            skeletonNifPath,
            meshArchives,
            preferEquippedForearmMount);

        if (TryResolveHandToHandProcessStyleWeaponAttachmentTransform(
                bindLocalWeaponTransform,
                idleBoneTransforms,
                parentBoneCandidates,
                equipOverrides,
                equipParentOverrideBone,
                skelRaw.Value.Data,
                skelRaw.Value.Info,
                out attachmentNodeName,
                out attachmentTransform))
        {
            omitReason = null;
            return true;
        }

        if (idleBoneTransforms.TryGetValue("Weapon", out attachmentTransform))
        {
            attachmentNodeName = "Weapon";
            omitReason = null;
            return true;
        }

        omitReason = "no process-style hand-to-hand attachment parent in equipped arm pose";
        return false;
    }

    internal static bool TryResolveHandToHandProcessStyleWeaponAttachmentTransform(
        Matrix4x4 bindLocalWeaponTransform,
        IReadOnlyDictionary<string, Matrix4x4> idleBoneTransforms,
        IReadOnlyList<string> parentBoneCandidates,
        IReadOnlyDictionary<string, NifAnimationParser.AnimPoseOverride>? equipOverrides,
        string? equipParentOverrideBone,
        byte[] skeletonData,
        NifInfo skeletonInfo,
        out string attachmentNodeName,
        out Matrix4x4 attachmentTransform)
    {
        attachmentNodeName = string.Empty;
        attachmentTransform = default;

        foreach (var candidate in parentBoneCandidates)
        {
            if (equipOverrides is { Count: > 0 } &&
                !string.IsNullOrWhiteSpace(equipParentOverrideBone) &&
                string.Equals(candidate, equipParentOverrideBone, StringComparison.OrdinalIgnoreCase))
            {
                var animatedAttachmentTransform = ResolveWeaponHolsterAttachmentTransform(
                    idleBoneTransforms,
                    equipOverrides,
                    skeletonData,
                    skeletonInfo,
                    "Weapon",
                    candidate,
                    idleBoneTransforms);
                if (animatedAttachmentTransform.HasValue)
                {
                    attachmentNodeName = $"Weapon via {candidate} (equip local)";
                    attachmentTransform = animatedAttachmentTransform.Value;
                    return true;
                }
            }

            if (!idleBoneTransforms.TryGetValue(candidate, out var parentWorldTransform))
            {
                continue;
            }

            attachmentNodeName = $"Weapon via {candidate}";
            attachmentTransform = bindLocalWeaponTransform * parentWorldTransform;
            return true;
        }

        return false;
    }

    internal static bool TryResolveProcessStyleWeaponAttachmentTransform(
        Matrix4x4 bindLocalWeaponTransform,
        IReadOnlyDictionary<string, Matrix4x4> idleBoneTransforms,
        IReadOnlyList<string> parentBoneCandidates,
        out string attachmentNodeName,
        out Matrix4x4 attachmentTransform)
    {
        attachmentNodeName = string.Empty;
        attachmentTransform = default;

        foreach (var candidate in parentBoneCandidates)
        {
            if (!idleBoneTransforms.TryGetValue(candidate, out var parentWorldTransform))
            {
                continue;
            }

            attachmentNodeName = $"Weapon via {candidate}";
            attachmentTransform = bindLocalWeaponTransform * parentWorldTransform;
            return true;
        }

        return false;
    }

    internal static IReadOnlyList<string> ResolveHandToHandWeaponParentBoneCandidates(
        string? skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        bool preferEquippedForearmMount = false)
    {
        var candidates = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
            {
                candidates.Add(trimmed);
            }
        }

        var idleParentBone = NpcSkeletonLoader.TryLoadSequenceParentBoneName(
            skeletonNifPath,
            meshArchives,
            "h2hidle.kf");
        var equipParentBone = NpcSkeletonLoader.TryLoadSequenceParentBoneName(
            skeletonNifPath,
            meshArchives,
            "h2hequip.kf");

        if (preferEquippedForearmMount)
        {
            AddCandidate(equipParentBone);
            AddCandidate(idleParentBone);
            AddCandidate("Bip01 R ForeTwist");
            AddCandidate("Bip01 R Hand");
        }
        else
        {
            AddCandidate(idleParentBone);
            AddCandidate(equipParentBone);
            AddCandidate("Bip01 R Hand");
            AddCandidate("Bip01 R ForeTwist");
        }

        return candidates;
    }

    /// <summary>
    ///     Loads the weapon holster animation KF and computes skeleton bone transforms with it
    ///     layered over the base idle animation.
    /// </summary>
    internal static WeaponHolsterPose? LoadWeaponHolsterPose(
        string? skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        string holsterProfileKey,
        bool usePowerArmorHolster)
    {
        return LoadWeaponAttachmentPose(
            skeletonNifPath,
            meshArchives,
            BuildHolsterKfRelPath(holsterProfileKey, usePowerArmorHolster),
            false,
            "holster");
    }

    internal static WeaponHolsterPose? LoadWeaponAttachmentPose(
        string? skeletonNifPath,
        NpcMeshArchiveSet meshArchives,
        string kfRelPath,
        bool sampleLastKeyframe,
        string poseLabel)
    {
        if (skeletonNifPath == null)
            return null;

        var skelDir = skeletonNifPath.Replace("skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
        var kfPath = skelDir + kfRelPath;

        var kfRaw = NpcRenderHelpers.LoadNifRawFromBsa(kfPath, meshArchives, true);

        // Female-to-male fallback (same pattern as base idle loading)
        if (kfRaw == null && skelDir.Contains("_female", StringComparison.OrdinalIgnoreCase))
        {
            var malePath = skelDir.Replace("_female", "_male", StringComparison.OrdinalIgnoreCase) + kfRelPath;
            kfRaw = NpcRenderHelpers.LoadNifRawFromBsa(malePath, meshArchives, true);
        }

        if (kfRaw == null)
            return null;

        var attachmentOverrides = NifAnimationParser.ParseIdlePoseOverrides(
            kfRaw.Value.Data,
            kfRaw.Value.Info,
            sampleLastKeyframe);
        if (attachmentOverrides == null || attachmentOverrides.Count == 0)
            return null;

        // Load skeleton and base idle overrides, then layer holster overrides on top.
        var skelRaw = NpcRenderHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
        if (skelRaw == null)
            return null;

        var baseIdle = NpcSkeletonLoader.LoadIdleAnimationOverrides(skeletonNifPath, meshArchives, skelRaw);
        var merged = NpcSkeletonLoader.MergePoseOverrides(baseIdle, attachmentOverrides);

        Log.Debug("Weapon {0} KF '{1}': {2} overrides ({3} base + {4} anim)",
            poseLabel, kfRelPath, merged.Count, baseIdle?.Count ?? 0, attachmentOverrides.Count);
        var parentOverrideBone = NpcSkeletonLoader.TryParseSequenceParentBoneName(kfRaw.Value.Info);
        if (parentOverrideBone != null)
        {
            Log.Debug("Weapon {0} KF '{1}': parent override '{2}'", poseLabel, kfRelPath, parentOverrideBone);
        }

        return new WeaponHolsterPose(
            NifGeometryExtractor.ExtractNamedBoneTransforms(
                skelRaw.Value.Data,
                skelRaw.Value.Info,
                merged),
            attachmentOverrides,
            skelRaw.Value.Data,
            skelRaw.Value.Info,
            parentOverrideBone);
    }

    internal static Matrix4x4? ResolveWeaponHolsterAttachmentTransform(
        IReadOnlyDictionary<string, Matrix4x4> worldTransforms,
        IReadOnlyDictionary<string, NifAnimationParser.AnimPoseOverride> holsterOverrides,
        byte[] skeletonData,
        NifInfo skeletonInfo,
        string attachmentNodeName,
        string? parentOverrideBone,
        IReadOnlyDictionary<string, Matrix4x4>? parentWorldTransforms = null)
    {
        worldTransforms.TryGetValue(attachmentNodeName, out var defaultWorldTransform);

        if (string.IsNullOrWhiteSpace(parentOverrideBone) ||
            !holsterOverrides.TryGetValue(attachmentNodeName, out var attachmentOverride) ||
            !NpcSkeletonLoader.TryReadNodeLocalTransform(skeletonData, skeletonInfo, attachmentNodeName, out var bindLocalTransform))
        {
            return worldTransforms.ContainsKey(attachmentNodeName)
                ? defaultWorldTransform
                : null;
        }

        var parentTransforms = parentWorldTransforms ?? worldTransforms;
        if (!parentTransforms.TryGetValue(parentOverrideBone, out var parentWorldTransform))
        {
            return worldTransforms.ContainsKey(attachmentNodeName)
                ? defaultWorldTransform
                : null;
        }

        return NpcSkeletonLoader.ApplyPoseOverride(bindLocalTransform, attachmentOverride) * parentWorldTransform;
    }

    internal static Matrix4x4? ResolveWeaponHolsterAttachmentTransform(
        WeaponHolsterPose holsterPose,
        string attachmentNodeName)
    {
        return ResolveWeaponHolsterAttachmentTransform(
            holsterPose.WorldTransforms,
            holsterPose.HolsterOverrides,
            holsterPose.SkeletonData,
            holsterPose.SkeletonInfo,
            attachmentNodeName,
            holsterPose.ParentOverrideBone);
    }

    internal static string BuildHolsterKfRelPath(string holsterProfileKey, bool usePowerArmorHolster)
    {
        return usePowerArmorHolster
            ? $"PA{holsterProfileKey}Holster.kf"
            : $"{holsterProfileKey}Holster.kf";
    }

    internal static bool HasPowerArmorTorso(IEnumerable<EquippedItem>? equippedItems)
    {
        if (equippedItems == null)
        {
            return false;
        }

        foreach (var item in equippedItems)
        {
            if ((item.BipedFlags & 0x04) != 0 && item.IsPowerArmor)
            {
                return true;
            }
        }

        return false;
    }

    internal sealed record WeaponHolsterPose(
        Dictionary<string, Matrix4x4> WorldTransforms,
        Dictionary<string, NifAnimationParser.AnimPoseOverride> HolsterOverrides,
        byte[] SkeletonData,
        NifInfo SkeletonInfo,
        string? ParentOverrideBone);
}
