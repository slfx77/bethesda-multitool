using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

internal static class NpcCompositionExportAdapter
{
    internal static NpcExportScene? BuildNpc(
        NpcCompositionPlan plan,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcCompositionCaches compositionCaches)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(meshArchives);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(compositionCaches);

        return plan.Options.HeadOnly
            ? BuildHeadOnlyScene(plan, meshArchives, textureResolver, compositionCaches)
            : BuildFullBodyScene(plan, meshArchives, textureResolver, compositionCaches);
    }

    internal static NpcExportScene? BuildCreature(
        CreatureCompositionPlan plan,
        NpcMeshArchiveSet meshArchives)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(meshArchives);

        var skeletonRaw = NpcMeshHelpers.LoadNifRawFromBsa(plan.SkeletonNifPath, meshArchives);
        if (skeletonRaw == null)
        {
            return null;
        }

        var extractedSkeleton = NifExportExtractor.Extract(
            skeletonRaw.Value.Data,
            skeletonRaw.Value.Info,
            plan.AnimationOverrides);
        var scene = new NpcExportScene();
        var nodeIndicesByName = NpcExportSceneBuilder.AddNodes(
            scene,
            extractedSkeleton.Nodes,
            NpcExportNodeKind.Skeleton);

        foreach (var bodyPath in plan.BodyModelPaths)
        {
            var bodyRaw = NpcMeshHelpers.LoadNifRawFromBsa(bodyPath, meshArchives);
            if (bodyRaw == null)
            {
                continue;
            }

            var bodyExtracted = NifExportExtractor.Extract(bodyRaw.Value.Data, bodyRaw.Value.Info);
            foreach (var part in bodyExtracted.MeshParts)
            {
                if (part.Skin != null)
                {
                    NpcExportSceneBuilder.AddSkinnedPart(scene, part, nodeIndicesByName);
                    continue;
                }

                var rigidTransform = part.ShapeWorldTransform;
                if (rigidTransform.Translation.LengthSquared() < 0.01f &&
                    plan.HeadAttachmentTransform.HasValue)
                {
                    rigidTransform = plan.HeadAttachmentTransform.Value;
                }

                NpcExportSceneBuilder.AddExtractedRigidPart(scene, part, rigidTransform, bodyPath);
            }
        }

        if (plan.Options.IncludeWeapon &&
            plan.WeaponMeshPath != null &&
            plan.WeaponAttachmentTransform.HasValue)
        {
            var weaponRaw = NpcMeshHelpers.LoadNifRawFromBsa(plan.WeaponMeshPath, meshArchives);
            if (weaponRaw != null)
            {
                var weaponExtracted = NifExportExtractor.Extract(weaponRaw.Value.Data, weaponRaw.Value.Info);
                foreach (var part in weaponExtracted.MeshParts)
                {
                    var weaponSubmesh = NpcExportSceneBuilder.CloneSubmesh(part.Submesh);
                    NpcRenderHelpers.TransformSubmesh(
                        weaponSubmesh,
                        plan.WeaponAttachmentTransform.Value * part.ShapeWorldTransform);
                    NpcExportSceneBuilder.AddRigidSubmesh(scene, plan.WeaponMeshPath, weaponSubmesh);
                }
            }
        }

        return scene.MeshParts.Count > 0 ? scene : null;
    }

    private static NpcExportScene? BuildFullBodyScene(
        NpcCompositionPlan plan,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcCompositionCaches compositionCaches)
    {
        var skeletonContext = CreateSkeletonContext(plan, meshArchives);
        if (skeletonContext == null)
        {
            return null;
        }

        var scene = skeletonContext.Scene;
        foreach (var bodyPart in plan.BodyParts)
        {
            NpcExportSceneBuilder.AddSkinnedNif(
                scene,
                bodyPart.MeshPath,
                meshArchives,
                skeletonContext.NodeIndicesByBoneName,
                submesh =>
                {
                    if (bodyPart.TextureOverride != null &&
                        NpcTextureHelpers.ShouldApplyBodyTextureOverride(
                            submesh.DiffuseTexturePath,
                            bodyPart.TextureOverride))
                    {
                        submesh.DiffuseTexturePath = bodyPart.TextureOverride;
                    }
                });
        }

        NpcExportBodyAssembler.AddBodyEquipment(scene, plan, meshArchives, skeletonContext);
        NpcExportBodyAssembler.AddWeapon(scene, plan, meshArchives, textureResolver, skeletonContext);
        NpcExportHeadAssembler.AddHeadContent(
            scene,
            plan,
            meshArchives,
            textureResolver,
            compositionCaches,
            skeletonContext.NodeIndicesByBoneName);
        return scene.MeshParts.Count > 0 ? scene : null;
    }

    private static NpcExportScene? BuildHeadOnlyScene(
        NpcCompositionPlan plan,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcCompositionCaches compositionCaches)
    {
        if (plan.Head.BaseHeadNifPath == null && plan.Head.FaceGenNifPath == null)
        {
            return null;
        }

        var scene = new NpcExportScene();
        Dictionary<string, int>? nodeIndicesByBoneName = null;
        if (plan.Head.BaseHeadNifPath != null)
        {
            var headRaw = NpcMeshHelpers.LoadNifRawFromBsa(plan.Head.BaseHeadNifPath, meshArchives);
            if (headRaw != null)
            {
                var headNodes = NifExportExtractor.Extract(headRaw.Value.Data, headRaw.Value.Info);
                nodeIndicesByBoneName = NpcExportSceneBuilder.AddNodes(
                    scene,
                    headNodes.Nodes,
                    NpcExportNodeKind.Skeleton);
            }
        }

        NpcExportHeadAssembler.AddHeadContent(
            scene,
            plan,
            meshArchives,
            textureResolver,
            compositionCaches,
            nodeIndicesByBoneName);

        if (scene.MeshParts.Count == 0 && plan.Head.FaceGenNifPath != null)
        {
            var faceGenModel = NpcMeshHelpers.LoadNifFromBsa(
                plan.Head.FaceGenNifPath,
                meshArchives,
                textureResolver);
            if (faceGenModel != null)
            {
                NpcExportSceneBuilder.AddRigidModel(scene, plan.Head.FaceGenNifPath, faceGenModel);
            }
        }

        return scene.MeshParts.Count > 0 ? scene : null;
    }

    private static NpcExportSceneBuilder.SkeletonContext? CreateSkeletonContext(
        NpcCompositionPlan plan,
        NpcMeshArchiveSet meshArchives)
    {
        if (plan.Skeleton?.SkeletonNifPath == null)
        {
            return null;
        }

        if (plan.Skeleton.BodySkinningBones == null)
        {
            return null;
        }

        var skeletonRaw = NpcMeshHelpers.LoadNifRawFromBsa(plan.Skeleton.SkeletonNifPath, meshArchives);
        if (skeletonRaw == null)
        {
            return null;
        }

        var extractedSkeleton = NifExportExtractor.Extract(
            skeletonRaw.Value.Data,
            skeletonRaw.Value.Info,
            plan.Skeleton.AnimationOverrides);
        var scene = new NpcExportScene();
        var nodeIndicesByBoneName = NpcExportSceneBuilder.AddNodes(
            scene,
            extractedSkeleton.Nodes,
            NpcExportNodeKind.Skeleton);
        return new NpcExportSceneBuilder.SkeletonContext(
            scene,
            plan.Skeleton.BodySkinningBones,
            plan.Skeleton.PoseDeltas,
            nodeIndicesByBoneName);
    }
}
