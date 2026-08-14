using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

internal sealed class MiscItemHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    #region Weapon Mods

    /// <summary>
    ///     Parse all Weapon Mod (IMOD) records.
    /// </summary>
    internal List<WeaponModRecord> ParseWeaponMods()
    {
        var mods = ParseAccessorOnly("IMOD", 1024, ParseWeaponModFromAccessor);

        Context.MergeRuntimeRecords(mods, 0x67, m => m.FormId,
            (reader, entry) => reader.ReadRuntimeWeaponMod(entry), "weapon mods");

        return mods;
    }

    private WeaponModRecord? ParseWeaponModFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, fullName = null, description = null;
        string? modelPath = null, icon = null;
        var value = 0;
        float weight = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "FULL":
                    fullName = Context.ReadFullName(data.AsSpan(sub.DataOffset, sub.DataLength));
                    break;
                case "DESC":
                    description =
                        Context.ReadDescription(data.AsSpan(sub.DataOffset, sub.DataLength));
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset,
                        sub.DataLength));
                    break;
                case "ICON":
                    icon = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    break;
                case "DATA" when sub.DataLength >= 8:
                {
                    if (SubrecordSchemaView.TryRead("DATA", "IMOD",
                            data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian) is { } v)
                    {
                        value = (int)v.UInt32("Value");
                        weight = v.Float("Weight");
                    }

                    break;
                }
            }
        }

        return new WeaponModRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            FullName = fullName,
            Description = description,
            ModelPath = modelPath,
            Icon = icon,
            Value = value,
            Weight = weight,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Recipes

    /// <summary>
    ///     Parse all Recipe (RCPE) records.
    /// </summary>
    internal List<RecipeRecord> ParseRecipes()
    {
        var recipes = ParseAccessorOnly("RCPE", 2048, ParseRecipeFromAccessor);

        Context.MergeRuntimeRecords(recipes, 0x6A, r => r.FormId,
            (reader, entry) => reader.ReadRuntimeRecipe(entry), "recipes");

        return recipes;
    }

    private RecipeRecord? ParseRecipeFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, fullName = null;
        int requiredSkill = -1, requiredSkillLevel = 0;
        uint categoryFormId = 0, subcategoryFormId = 0;
        var ingredients = new List<RecipeIngredient>();
        var outputs = new List<RecipeOutput>();
        uint? pendingIngredientItemId = null;
        uint? pendingOutputItemId = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "FULL":
                    fullName = Context.ReadFullName(data.AsSpan(sub.DataOffset, sub.DataLength));
                    break;
                case "DATA" when sub.DataLength >= 16:
                {
                    if (SubrecordSchemaView.TryRead("DATA", "RCPE",
                            data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian) is { } v)
                    {
                        requiredSkill = v.Int32("Skill");
                        requiredSkillLevel = (int)v.UInt32("Level");
                        categoryFormId = v.UInt32("Category");
                        subcategoryFormId = v.UInt32("SubCategory");
                    }

                    break;
                }
                case "RCIL" when sub.DataLength >= 4:
                {
                    FlushPendingRecipeComponent(
                        ingredients,
                        outputs,
                        ref pendingIngredientItemId,
                        ref pendingOutputItemId,
                        1);
                    var itemId = ReadSubrecordUInt32(data, sub.DataOffset, record.IsBigEndian);
                    if (sub.DataLength >= 8)
                    {
                        var count = ReadSubrecordUInt32(data, sub.DataOffset + 4, record.IsBigEndian);
                        ingredients.Add(new RecipeIngredient { ItemFormId = itemId, Count = count });
                    }
                    else
                    {
                        pendingIngredientItemId = itemId;
                    }

                    break;
                }
                case "RCOD" when sub.DataLength >= 4:
                {
                    FlushPendingRecipeComponent(
                        ingredients,
                        outputs,
                        ref pendingIngredientItemId,
                        ref pendingOutputItemId,
                        1);
                    var itemId = ReadSubrecordUInt32(data, sub.DataOffset, record.IsBigEndian);
                    if (sub.DataLength >= 8)
                    {
                        var count = ReadSubrecordUInt32(data, sub.DataOffset + 4, record.IsBigEndian);
                        outputs.Add(new RecipeOutput { ItemFormId = itemId, Count = count });
                    }
                    else
                    {
                        pendingOutputItemId = itemId;
                    }

                    break;
                }
                case "RCQY" when sub.DataLength >= 4:
                {
                    var count = ReadSubrecordUInt32(data, sub.DataOffset, record.IsBigEndian);
                    FlushPendingRecipeComponent(
                        ingredients,
                        outputs,
                        ref pendingIngredientItemId,
                        ref pendingOutputItemId,
                        count);
                    break;
                }
            }
        }

        FlushPendingRecipeComponent(
            ingredients,
            outputs,
            ref pendingIngredientItemId,
            ref pendingOutputItemId,
            1);

        return new RecipeRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            FullName = fullName,
            RequiredSkill = requiredSkill,
            RequiredSkillLevel = requiredSkillLevel,
            CategoryFormId = categoryFormId,
            SubcategoryFormId = subcategoryFormId,
            Ingredients = ingredients,
            Outputs = outputs,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static uint ReadSubrecordUInt32(byte[] data, int offset, bool bigEndian)
    {
        return BinaryUtils.ReadUInt32(data, offset, bigEndian);
    }

    private static void FlushPendingRecipeComponent(
        List<RecipeIngredient> ingredients,
        List<RecipeOutput> outputs,
        ref uint? pendingIngredientItemId,
        ref uint? pendingOutputItemId,
        uint count)
    {
        if (pendingIngredientItemId is > 0)
        {
            ingredients.Add(new RecipeIngredient { ItemFormId = pendingIngredientItemId.Value, Count = count });
            pendingIngredientItemId = null;
        }

        if (pendingOutputItemId is > 0)
        {
            outputs.Add(new RecipeOutput { ItemFormId = pendingOutputItemId.Value, Count = count });
            pendingOutputItemId = null;
        }
    }

    #endregion

    #region Armor Addons

    /// <summary>
    ///     Parse all Armor Addon (ARMA) records.
    /// </summary>
    internal List<ArmaRecord> ParseArmorAddons()
    {
        return ParseRecordList("ARMA", 4096,
            ParseArmorAddonFromAccessor,
            record => new ArmaRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FormIdToFullName.GetValueOrDefault(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
    }

    private ArmaRecord? ParseArmorAddonFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ArmaRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FormIdToFullName.GetValueOrDefault(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, fullName = null;
        string? maleModel = null, femaleModel = null, maleFp = null, femaleFp = null;
        string? maleIcon = null, femaleIcon = null;
        byte[]? maleTextureHash = null, femaleTextureHash = null;
        byte[]? maleFpTextureHash = null, femaleFpTextureHash = null;
        ObjectBounds? bounds = null;
        uint bipedFlags = 0;
        byte generalFlags = 0;
        var damageResistance = 0;
        var damageThreshold = 0f;
        var value = 0;
        var maxCondition = 0;
        var weight = 0f;
        var equipmentType = EquipmentType.None;
        uint? repairItemListFormId = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "FULL":
                    fullName = Context.ReadFullName(subData);
                    break;
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "BMDT" when sub.DataLength >= 8:
                {
                    if (SubrecordSchemaView.TryRead("BMDT", null, subData, record.IsBigEndian) is { } v)
                    {
                        bipedFlags = v.UInt32("BipedFlags");
                        generalFlags = v.Byte("GeneralFlags");
                    }

                    break;
                }
                case "MODL":
                    maleModel = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODT":
                    maleTextureHash = subData.ToArray();
                    break;
                case "MOD2":
                    femaleModel = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MO2T":
                    femaleTextureHash = subData.ToArray();
                    break;
                case "MOD3":
                    maleFp = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MO3T":
                    maleFpTextureHash = subData.ToArray();
                    break;
                case "MOD4":
                    femaleFp = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MO4T":
                    femaleFpTextureHash = subData.ToArray();
                    break;
                case "ICON":
                    maleIcon = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MIC2":
                    femaleIcon = EsmStringUtils.ReadNullTermString(subData);
                    break;
                // ARMA DNAM is the same DR/DT armor-defense struct as ARMO (DR Int16 @+0, DT float @+4);
                // FO3's is a short 4-byte DR-only block. DT presence keys on length, not version.
                case "DNAM" when sub.DataLength >= 2:
                    (damageResistance, damageThreshold) = ArmorDefenseData.Read(subData, record.IsBigEndian);
                    break;
                case "ETYP" when sub.DataLength >= 4:
                {
                    var raw = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    equipmentType = (EquipmentType)raw;
                    break;
                }
                case "REPL" when sub.DataLength == 4:
                    repairItemListFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength >= 12:
                {
                    if (SubrecordSchemaView.TryRead("DATA", "ARMA", subData, record.IsBigEndian) is { } v)
                    {
                        value = v.Int32("Value");
                        maxCondition = v.Int32("MaxCondition");
                        weight = v.Float("Weight");
                    }

                    break;
                }
            }
        }

        return new ArmaRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            Bounds = bounds,
            MaleModelPath = maleModel,
            FemaleModelPath = femaleModel,
            MaleFirstPersonModelPath = maleFp,
            FemaleFirstPersonModelPath = femaleFp,
            MaleTextureHashData = maleTextureHash,
            FemaleTextureHashData = femaleTextureHash,
            MaleFirstPersonTextureHashData = maleFpTextureHash,
            FemaleFirstPersonTextureHashData = femaleFpTextureHash,
            MaleIconPath = maleIcon,
            FemaleIconPath = femaleIcon,
            DamageResistance = damageResistance,
            DamageThreshold = damageThreshold,
            EquipmentType = equipmentType,
            RepairItemListFormId = repairItemListFormId,
            BipedFlags = bipedFlags,
            GeneralFlags = generalFlags,
            Value = value,
            MaxCondition = maxCondition,
            Weight = weight,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Constructible Objects

    /// <summary>
    ///     Parse COBJ records into the retained forensic hybrid model. This accepts the
    ///     historical recipe-shaped probe fields in addition to FNV base-object fields;
    ///     it is not authority for production FNV serialization.
    /// </summary>
    internal List<ConstructibleObjectRecord> ParseConstructibleObjects()
    {
        var objects = ParseAccessorOnly("COBJ", 2048, ParseConstructibleObjectFromAccessor);

        Context.MergeRuntimeRecords(objects, 0x32, c => c.FormId,
            (reader, entry) => reader.ReadRuntimeConstructibleObject(entry), "constructible objects");

        return objects;
    }

    private ConstructibleObjectRecord? ParseConstructibleObjectFromAccessor(
        DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, fullName = null, modelPath = null;
        byte[]? textureHash = null;
        ObjectBounds? bounds = null;
        var ingredients = new List<InventoryItem>();
        var conditions = new List<DialogueCondition>();
        var conditionStrings = new ConditionStringSiblingBinder();
        uint? createdItem = null;
        uint? workbenchKeyword = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);
            if (conditionStrings.TryConsume(sub.Signature, subData))
            {
                continue;
            }

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "FULL":
                    fullName = Context.ReadFullName(subData);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODT":
                    textureHash = subData.ToArray();
                    break;
                case "COCT":
                    // Ingredient count — we count from the actual CNTOs that follow, so ignore.
                    break;
                case "CNTO" when sub.DataLength >= 8:
                {
                    var itemId = RecordParserContext.ReadFormId(subData[..4], record.IsBigEndian);
                    var count = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..8])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..8]);
                    ingredients.Add(new InventoryItem(itemId, count));
                    break;
                }
                case "CTDA":
                    if (CtdaParser.TryDecode(subData, record.IsBigEndian, out var recipeCondition, out _))
                    {
                        conditions.Add(recipeCondition);
                        conditionStrings.Begin(conditions);
                    }

                    break;
                case "CNAM" when sub.DataLength == 4:
                    createdItem = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "BNAM" when sub.DataLength == 4:
                    workbenchKeyword = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        return new ConstructibleObjectRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            Bounds = bounds,
            ModelPath = modelPath,
            TextureHashData = textureHash,
            Ingredients = ingredients,
            Conditions = conditions,
            CreatedItemFormId = createdItem,
            WorkbenchKeywordFormId = workbenchKeyword,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Body Part Data

    /// <summary>
    ///     Parse all Body Part Data (BPTD) records.
    /// </summary>
    internal List<BodyPartDataRecord> ParseBodyPartData()
    {
        return ParseRecordList("BPTD", 4096,
            ParseBodyPartDataFromAccessor,
            record => new BodyPartDataRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
    }

    private BodyPartDataRecord? ParseBodyPartDataFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new BodyPartDataRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, modelPath = null;
        var partNames = new List<string>();
        var nodeNames = new List<string>();
        uint textureCount = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "BPTN":
                {
                    var name = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(name))
                    {
                        partNames.Add(name);
                    }

                    break;
                }
                case "BPNN":
                {
                    var name = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(name))
                    {
                        nodeNames.Add(name);
                    }

                    break;
                }
                case "NAM5" when sub.DataLength >= 4:
                {
                    if (SubrecordSchemaView.TryRead("NAM5", "BPTD", subData, record.IsBigEndian) is { } v)
                    {
                        textureCount = v.UInt32("TextureCount");
                    }

                    break;
                }
            }
        }

        return new BodyPartDataRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            ModelPath = modelPath,
            PartNames = partNames,
            NodeNames = nodeNames,
            TextureCount = textureCount,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Ingredients

    /// <summary>
    ///     Parse all Ingredient (INGR) records.
    /// </summary>
    internal List<IngredientRecord> ParseIngredients()
    {
        var ingredients = ParseAccessorOnly("INGR", 512, ParseIngredientFromAccessor);

        Context.MergeRuntimeRecords(ingredients, 0x1D, i => i.FormId,
            (reader, entry) => reader.ReadRuntimeIngredient(entry), "ingredients");

        return ingredients;
    }

    private IngredientRecord? ParseIngredientFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, fullName = null, modelPath = null;
        float weight = 0;
        uint equipType = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "FULL":
                    fullName =
                        Context.ReadFullName(data.AsSpan(sub.DataOffset, sub.DataLength));
                    break;
                case "MODL":
                    modelPath =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    break;
                // FNV INGR DATA is one float. Value/flags are stored separately in ENIT.
                case "DATA" when sub.DataLength >= 4:
                    weight = BinaryUtils.ReadFloat(data, sub.DataOffset, record.IsBigEndian);
                    break;
                case "ETYP" when sub.DataLength >= 4:
                    equipType = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
            }
        }

        return new IngredientRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Weight = weight,
            EquipType = equipType,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
