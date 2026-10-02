using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;

/// <summary>Preserves identities at the point where typed record fields become component paths.</summary>
internal static class NpcAssetUseFactory
{
    internal static AssetUseGraph Build(NpcAppearanceIndex index, NpcAppearance npc, string plugin,
        uint? raceId, uint? hairId, uint? eyesId, List<uint>? headParts, bool runtimeNpc = false)
    {
        var graph = new AssetUseGraphBuilder();
        AssetRecordOwner Owner(uint? id, string signature)
        {
            var owner = index.Owner(id, signature);
            return owner.Plugin is null && owner.Status is "StoredBytes" or "MappedRecord"
                ? owner with { Plugin = plugin } : owner;
        }
        var npcOwner = runtimeNpc ? AssetRecordOwner.Unavailable("NPC_", npc.NpcFormId) : Owner(npc.NpcFormId, "NPC_");
        var actor = graph.Add(npcOwner, "actor", "NPC_", null, runtimeNpc ? "runtime-record-identity-unavailable" : "selected-record");
        var race = Owner(raceId, "RACE");
        var raceLink = graph.Add(npcOwner, "race", "RaceFormId", null, "record-reference", [actor]);
        var gender = npc.IsFemale ? "Female" : "Male";
        string? Add(AssetRecordOwner? owner, string component, string field, string? path,
            string relation = "record-field", params string[] parents) => path is null ? null :
            graph.Add(owner, component, field, path, relation, parents.Length == 0 ? [actor] : parents);
        var head = Add(race, "head", gender + "HeadModelPath", npc.BaseHeadNifPath, "record-field", raceLink);
        if (head is not null)
        {
            Add(null, "head-morph", "BaseHeadTriPath", npc.BaseHeadTriPath, "bmt-path-derivation", head);
            Add(null, "head-morph", "EGM", Path.ChangeExtension(npc.BaseHeadNifPath, ".egm"), "bmt-path-derivation", head);
            Add(null, "head-texture-morph", "EGT", Path.ChangeExtension(npc.BaseHeadNifPath, ".egt"), "bmt-path-derivation", head);
        }
        Add(race, "head-texture", gender + "HeadTexturePath", npc.HeadDiffuseOverride, "record-field", raceLink);
        Add(npcOwner, "facegen", "FormId/Plugin", npc.FaceGenNifPath, "facegen-path-convention");
        Add(npcOwner, "facegen-map", "FormId/Plugin", npc.AuthoredFaceGenMap0Path, "facegen-path-convention");
        var hairLink = graph.Add(npcOwner, "hair", "HairFormId", null, "record-reference", [actor]);
        Add(Owner(hairId, "HAIR"), "hair-mesh", "ModelPath", npc.HairNifPath, "record-field", hairLink);
        Add(Owner(hairId, "HAIR"), "hair-texture", "TexturePath", npc.HairTexturePath, "record-field", hairLink);
        var eyesLink = graph.Add(npcOwner, "eyes", "EyesFormId / resolved race default", null, "appearance-selection", [actor, raceLink]);
        Add(Owner(eyesId, "EYES"), "eye-texture", "TexturePath", npc.EyeTexturePath, "record-field", eyesLink);
        foreach (var (component, field, path) in new (string, string, string?)[]
        {
            ("eye-left", "EyeLeftModelPath", npc.LeftEyeNifPath), ("eye-right", "EyeRightModelPath", npc.RightEyeNifPath),
            ("ear", "EarModelPath", npc.EarNifPath), ("mouth", "MouthModelPath", npc.MouthNifPath),
            ("teeth-lower", "LowerTeethModelPath", npc.LowerTeethNifPath), ("teeth-upper", "UpperTeethModelPath", npc.UpperTeethNifPath),
            ("tongue", "TongueModelPath", npc.TongueNifPath), ("upper-body", "UpperBodyPath", npc.UpperBodyNifPath),
            ("lower-body", "LowerBodyPath", npc.LowerBodyNifPath), ("hands", "HandPath", npc.HandNifPath),
            ("feet", "FootPath", npc.FootNifPath), ("tail", "TailPath", npc.TailNifPath),
            ("hand-left", "LeftHandPath", npc.LeftHandNifPath), ("hand-right", "RightHandPath", npc.RightHandNifPath),
            ("body-texture", "BodyTexturePath", npc.BodyTexturePath), ("lower-body-texture", "LowerBodyTexturePath", npc.LowerBodyTexturePath),
            ("foot-texture", "FootTexturePath", npc.FootTexturePath), ("tail-texture", "TailTexturePath", npc.TailTexturePath)
        }) Add(race, component, gender + field, path, "record-field", raceLink);
        index.Races.TryGetValue(raceId ?? 0, out var raceRecord);
        var authoredEar = npc.IsFemale ? raceRecord?.FemaleEarTexturePath : raceRecord?.MaleEarTexturePath;
        Add(race, "ear-texture", gender + (authoredEar is null ? "HeadTexturePath" : "EarTexturePath"),
            npc.EarTexturePath, authoredEar is null ? "appearance-fallback" : "record-field", raceLink);
        var authoredHands = npc.IsFemale ? raceRecord?.FemaleHandTexturePath : raceRecord?.MaleHandTexturePath;
        Add(race, "hand-texture", gender + (authoredHands is null ? "BodyTexturePath" : "HandTexturePath"),
            npc.HandTexturePath, authoredHands is null ? "bmt-path-derivation" : "record-field", raceLink);
        Add(null, "skeleton", "SkeletonNifPath", npc.SkeletonNifPath, "renderer-default", actor);
        foreach (var (component, path) in new[] { ("body-morph", npc.BodyEgtPath), ("hand-left-morph", npc.LeftHandEgtPath), ("hand-right-morph", npc.RightHandEgtPath) })
            Add(null, component, "EGT", path, "bmt-path-derivation", head ?? raceLink);
        var headLink = graph.Add(npcOwner, "head-parts", "HeadPartFormIds", null, "record-reference", [actor]);
        foreach (var id in headParts ?? [])
        {
            index.HeadParts.TryGetValue(id, out var part);
            graph.Add(Owner(id, "HDPT"), "head-part", "ModelPath", NpcAppearancePathDeriver.AsMeshPath(part?.ModelPath), "record-field", [headLink]);
        }
        foreach (var item in npc.EquippedItems ?? [])
        foreach (var source in item.AssetOwners)
        {
            var inventory = source.InventoryFormId is { } inventoryId && inventoryId != source.ArmorFormId
                ? graph.Add(Owner(inventoryId, "LVLI"), "inventory-selection", "Items", null, "preview-leveled-selection", [actor]) : actor;
            var armor = graph.Add(Owner(source.ArmorFormId, "ARMO"), "equipment", "selected-inventory-item", null, "appearance-selection", [inventory]);
            if (source.AddonFormId is { } addon)
            {
                var list = graph.Add(Owner(source.ArmorFormId, "ARMO"), "equipment-addon", "BipedModelListFormId", null, "record-reference", [armor]);
                var entries = source.FormListId is { } listId
                    ? graph.Add(Owner(listId, "FLST"), "equipment-addon-list", "FormIds", null, "record-reference", [list]) : list;
                graph.Add(Owner(addon, "ARMA"), "equipment-mesh", source.Field, item.MeshPath, "record-field", [entries]);
            }
            else graph.Add(Owner(source.ArmorFormId, "ARMO"), "equipment-mesh", source.Field, item.MeshPath, "record-field", [armor]);
        }
        if (npc.WeaponVisual is { } weapon)
        {
            Add(Owner(weapon.WeaponFormId, "WEAP"), "weapon", "ModelPath", weapon.MeshPath, "appearance-selection", actor);
            // Existing hand-to-hand addon matching is a preview heuristic, not an authored WEAP→ARMA link.
            foreach (var addon in weapon.AddonMeshes ?? [])
            {
                if (addon.AssetOwners.Count == 0)
                    Add(null, "weapon-addon", "MeshPath", addon.MeshPath, "preview-heuristic-owner-unavailable", actor);
                foreach (var source in addon.AssetOwners)
                    Add(Owner(source.FormId, "ARMA"), "weapon-addon", source.Field, addon.MeshPath, "preview-heuristic-selection", actor);
            }
        }
        if (npc.NpcFaceGenTextureCoeffs is not null)
            graph.Add(npcOwner, "facegen-texture-coefficients", "FaceGenTexture", null, "record-data", [actor]);
        if (npc.RaceFaceGenTextureCoeffs is not null)
            graph.Add(race, "facegen-texture-coefficients", gender + "FaceGenTexture", null, "record-data", [raceLink]);
        return graph.Build();
    }
}
