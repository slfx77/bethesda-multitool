using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class MiscItemHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    #region Weapon Mods

    /// <summary>
    ///     Reconstruct all Weapon Mod (IMOD) records.
    /// </summary>
    internal List<WeaponModRecord> ReconstructWeaponMods()
    {
        var mods = new List<WeaponModRecord>();

        if (_context.Accessor == null)
        {
            return mods;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            foreach (var record in _context.GetRecordsByType("IMOD"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description =
                                EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
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
                            var fields = SubrecordDataReader.ReadFields("DATA", "IMOD",
                                data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                value = (int)SubrecordDataReader.GetUInt32(fields, "Value");
                                weight = SubrecordDataReader.GetFloat(fields, "Weight");
                            }

                            break;
                        }
                    }
                }

                mods.Add(new WeaponModRecord
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
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return mods;
    }

    #endregion

    #region Recipes

    /// <summary>
    ///     Reconstruct all Recipe (RCPE) records.
    /// </summary>
    internal List<RecipeRecord> ReconstructRecipes()
    {
        var recipes = new List<RecipeRecord>();

        if (_context.Accessor == null)
        {
            return recipes;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("RCPE"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null;
                int requiredSkill = -1, requiredSkillLevel = 0;
                uint categoryFormId = 0, subcategoryFormId = 0;
                var ingredients = new List<RecipeIngredient>();
                var outputs = new List<RecipeOutput>();

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 16:
                        {
                            var fields = SubrecordDataReader.ReadFields("DATA", "RCPE",
                                data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                requiredSkill = SubrecordDataReader.GetInt32(fields, "Skill");
                                requiredSkillLevel = (int)SubrecordDataReader.GetUInt32(fields, "Level");
                                categoryFormId = SubrecordDataReader.GetUInt32(fields, "Category");
                                subcategoryFormId = SubrecordDataReader.GetUInt32(fields, "SubCategory");
                            }

                            break;
                        }
                        case "RCIL" when sub.DataLength >= 8:
                        {
                            var fields = SubrecordDataReader.ReadFields("RCIL", null,
                                data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                var itemId = SubrecordDataReader.GetUInt32(fields, "Item");
                                var count = SubrecordDataReader.GetUInt32(fields, "Count");
                                ingredients.Add(new RecipeIngredient { ItemFormId = itemId, Count = count });
                            }

                            break;
                        }
                        case "RCOD" when sub.DataLength >= 8:
                        {
                            var fields = SubrecordDataReader.ReadFields("RCOD", null,
                                data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                var itemId = SubrecordDataReader.GetUInt32(fields, "Item");
                                var count = SubrecordDataReader.GetUInt32(fields, "Count");
                                outputs.Add(new RecipeOutput { ItemFormId = itemId, Count = count });
                            }

                            break;
                        }
                    }
                }

                recipes.Add(new RecipeRecord
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
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return recipes;
    }

    #endregion

    #region Armor Addons

    /// <summary>
    ///     Reconstruct all Armor Addon (ARMA) records.
    /// </summary>
    internal List<ArmaRecord> ReconstructArmorAddons()
    {
        var addons = new List<ArmaRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("ARMA"))
            {
                addons.Add(new ArmaRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FormIdToFullName.GetValueOrDefault(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return addons;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in _context.GetRecordsByType("ARMA"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    addons.Add(new ArmaRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        FullName = _context.FormIdToFullName.GetValueOrDefault(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null;
                string? maleModel = null, femaleModel = null, maleFp = null, femaleFp = null;
                ObjectBounds? bounds = null;
                uint bipedFlags = 0, generalFlags = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "OBND" when sub.DataLength == 12:
                            bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                            break;
                        case "MODL":
                            maleModel = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "MOD2":
                            femaleModel = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "MOD3":
                            maleFp = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "MOD4":
                            femaleFp = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "DATA" when sub.DataLength >= 12:
                        {
                            var fields = SubrecordDataReader.ReadFields("DATA", "ARMA", subData, record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                bipedFlags = SubrecordDataReader.GetUInt32(fields, "BipedFlags");
                                generalFlags = SubrecordDataReader.GetUInt32(fields, "GeneralFlags");
                            }

                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(fullName))
                {
                    _context.FormIdToFullName.TryAdd(record.FormId, fullName);
                }

                addons.Add(new ArmaRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    FullName = fullName,
                    Bounds = bounds,
                    MaleModelPath = maleModel,
                    FemaleModelPath = femaleModel,
                    MaleFirstPersonModelPath = maleFp,
                    FemaleFirstPersonModelPath = femaleFp,
                    BipedFlags = bipedFlags,
                    GeneralFlags = generalFlags,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return addons;
    }

    #endregion

    #region Body Part Data

    /// <summary>
    ///     Reconstruct all Body Part Data (BPTD) records.
    /// </summary>
    internal List<BodyPartDataRecord> ReconstructBodyPartData()
    {
        var parts = new List<BodyPartDataRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("BPTD"))
            {
                parts.Add(new BodyPartDataRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return parts;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in _context.GetRecordsByType("BPTD"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    parts.Add(new BodyPartDataRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
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
                            var fields = SubrecordDataReader.ReadFields("NAM5", "BPTD", subData, record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                textureCount = SubrecordDataReader.GetUInt32(fields, "TextureCount");
                            }

                            break;
                        }
                    }
                }

                parts.Add(new BodyPartDataRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    ModelPath = modelPath,
                    PartNames = partNames,
                    NodeNames = nodeNames,
                    TextureCount = textureCount,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return parts;
    }

    #endregion
}
