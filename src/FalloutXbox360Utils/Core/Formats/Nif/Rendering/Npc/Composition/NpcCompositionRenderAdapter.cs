using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

internal static class NpcCompositionRenderAdapter
{
    internal static NifRenderableModel? BuildNpc(
        NpcCompositionPlan plan,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        NpcCompositionCaches compositionCaches,
        NpcRenderModelCache renderModelCache)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(meshArchives);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(compositionCaches);
        ArgumentNullException.ThrowIfNull(renderModelCache);

        return plan.Options.HeadOnly
            ? NpcHeadBuilder.BuildFromPlan(plan, meshArchives, textureResolver, compositionCaches, renderModelCache)
            : NpcBodyBuilder.BuildFromPlan(plan, meshArchives, textureResolver, compositionCaches, renderModelCache);
    }

    internal static NifRenderableModel? BuildCreature(
        CreatureCompositionPlan plan,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(meshArchives);
        ArgumentNullException.ThrowIfNull(textureResolver);

        var model = new NifRenderableModel();
        foreach (var bodyPath in plan.BodyModelPaths)
        {
            var raw = NpcMeshHelpers.LoadNifRawFromBsa(bodyPath, meshArchives);
            if (raw == null)
            {
                continue;
            }

            var partModel = NifGeometryExtractor.Extract(
                raw.Value.Data,
                raw.Value.Info,
                textureResolver,
                externalBoneTransforms: plan.BoneTransforms,
                useDualQuaternionSkinning: true);
            if (partModel == null || !partModel.HasGeometry)
            {
                continue;
            }

            var attachRigidToHead = !partModel.WasSkinned && plan.HeadAttachmentTransform.HasValue;
            foreach (var submesh in partModel.Submeshes)
            {
                if (attachRigidToHead)
                {
                    NpcRenderHelpers.TransformSubmesh(submesh, plan.HeadAttachmentTransform!.Value);
                }

                model.Submeshes.Add(submesh);
                model.ExpandBounds(submesh.Positions);
            }

            if (partModel.WasSkinned)
            {
                model.WasSkinned = true;
            }
        }

        if (plan.Options.IncludeWeapon &&
            plan.WeaponMeshPath != null &&
            plan.WeaponAttachmentTransform.HasValue)
        {
            var weaponRaw = NpcMeshHelpers.LoadNifRawFromBsa(plan.WeaponMeshPath, meshArchives);
            if (weaponRaw != null)
            {
                var weaponModel = NifGeometryExtractor.Extract(
                    weaponRaw.Value.Data,
                    weaponRaw.Value.Info,
                    textureResolver);
                if (weaponModel is { HasGeometry: true })
                {
                    foreach (var submesh in weaponModel.Submeshes)
                    {
                        NpcRenderHelpers.TransformSubmesh(submesh, plan.WeaponAttachmentTransform.Value);
                        model.Submeshes.Add(submesh);
                        model.ExpandBounds(submesh.Positions);
                    }
                }
            }
        }

        return model.HasGeometry ? model : null;
    }
}
