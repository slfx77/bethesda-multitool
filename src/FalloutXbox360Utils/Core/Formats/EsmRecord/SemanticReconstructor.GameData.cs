using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     Partial class containing game data reconstruction methods:
///     Globals, Enchantments, Base Effects, Weapon Mods, Recipes,
///     Challenges, Reputations, Projectiles, Explosions, Messages, Classes.
/// </summary>
public sealed partial class SemanticReconstructor
{
    /// <summary>
    ///     Reconstruct all Global Variable (GLOB) records.
    /// </summary>
    public List<ReconstructedGlobal> ReconstructGlobals()
    {
        var globals = new List<ReconstructedGlobal>();

        if (_accessor == null)
        {
            return globals;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            foreach (var record in GetRecordsByType("GLOB"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null;
                var valueType = 'f';
                float value = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FNAM" when sub.DataLength >= 1:
                            valueType = (char)buffer[sub.DataOffset];
                            break;
                        case "FLTV" when sub.DataLength >= 4:
                            value = record.IsBigEndian
                                ? BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                    }
                }

                globals.Add(new ReconstructedGlobal
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    ValueType = valueType,
                    Value = value,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return globals;
    }

    /// <summary>
    ///     Reconstruct all Enchantment (ENCH) records.
    /// </summary>
    public List<ReconstructedEnchantment> ReconstructEnchantments()
    {
        var enchantments = new List<ReconstructedEnchantment>();

        if (_accessor == null)
        {
            return enchantments;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("ENCH"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null;
                string? fullName = null;
                uint enchantType = 0, chargeAmount = 0, enchantCost = 0;
                byte flags = 0;
                var effects = new List<EnchantmentEffect>();
                uint currentEffectId = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ENIT" when sub.DataLength >= 12:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                enchantType = BinaryPrimitives.ReadUInt32BigEndian(span);
                                chargeAmount = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
                                enchantCost = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                            }
                            else
                            {
                                enchantType = BinaryPrimitives.ReadUInt32LittleEndian(span);
                                chargeAmount = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                                enchantCost = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                            }

                            if (sub.DataLength >= 13)
                            {
                                flags = buffer[sub.DataOffset + 12];
                            }

                            break;
                        }
                        case "EFID" when sub.DataLength >= 4:
                            currentEffectId = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                        case "EFIT" when sub.DataLength >= 12:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            float magnitude;
                            uint area, duration, type;
                            int actorValue;
                            if (record.IsBigEndian)
                            {
                                magnitude = BinaryPrimitives.ReadSingleBigEndian(span);
                                area = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
                                duration = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                                type = sub.DataLength >= 16 ? BinaryPrimitives.ReadUInt32BigEndian(span[12..]) : 0;
                                actorValue = sub.DataLength >= 20
                                    ? BinaryPrimitives.ReadInt32BigEndian(span[16..])
                                    : -1;
                            }
                            else
                            {
                                magnitude = BinaryPrimitives.ReadSingleLittleEndian(span);
                                area = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                                duration = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                                type = sub.DataLength >= 16 ? BinaryPrimitives.ReadUInt32LittleEndian(span[12..]) : 0;
                                actorValue = sub.DataLength >= 20
                                    ? BinaryPrimitives.ReadInt32LittleEndian(span[16..])
                                    : -1;
                            }

                            effects.Add(new EnchantmentEffect
                            {
                                EffectFormId = currentEffectId,
                                Magnitude = magnitude,
                                Area = area,
                                Duration = duration,
                                Type = type,
                                ActorValue = actorValue
                            });
                            break;
                        }
                    }
                }

                enchantments.Add(new ReconstructedEnchantment
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    EnchantType = enchantType,
                    ChargeAmount = chargeAmount,
                    EnchantCost = enchantCost,
                    Flags = flags,
                    Effects = effects,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return enchantments;
    }

    /// <summary>
    ///     Reconstruct all Base Effect (MGEF) records.
    /// </summary>
    public List<ReconstructedBaseEffect> ReconstructBaseEffects()
    {
        var effects = new List<ReconstructedBaseEffect>();

        if (_accessor == null)
        {
            return effects;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in GetRecordsByType("MGEF"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, description = null;
                string? icon = null, modelPath = null;
                uint flags = 0, associatedItem = 0, archetype = 0, projectile = 0, explosion = 0;
                float baseCost = 0;
                int magicSchool = -1, resistValue = -1, actorValue = -1;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ICON":
                            icon = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 36:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                flags = BinaryPrimitives.ReadUInt32BigEndian(span);
                                baseCost = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                                associatedItem = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                                magicSchool = BinaryPrimitives.ReadInt32BigEndian(span[12..]);
                                resistValue = BinaryPrimitives.ReadInt32BigEndian(span[16..]);
                                archetype = BinaryPrimitives.ReadUInt32BigEndian(span[24..]);
                                actorValue = BinaryPrimitives.ReadInt32BigEndian(span[28..]);
                                if (sub.DataLength >= 44)
                                {
                                    projectile = BinaryPrimitives.ReadUInt32BigEndian(span[36..]);
                                }

                                if (sub.DataLength >= 48)
                                {
                                    explosion = BinaryPrimitives.ReadUInt32BigEndian(span[40..]);
                                }
                            }
                            else
                            {
                                flags = BinaryPrimitives.ReadUInt32LittleEndian(span);
                                baseCost = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                                associatedItem = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                                magicSchool = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
                                resistValue = BinaryPrimitives.ReadInt32LittleEndian(span[16..]);
                                archetype = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);
                                actorValue = BinaryPrimitives.ReadInt32LittleEndian(span[28..]);
                                if (sub.DataLength >= 44)
                                {
                                    projectile = BinaryPrimitives.ReadUInt32LittleEndian(span[36..]);
                                }

                                if (sub.DataLength >= 48)
                                {
                                    explosion = BinaryPrimitives.ReadUInt32LittleEndian(span[40..]);
                                }
                            }

                            break;
                        }
                    }
                }

                effects.Add(new ReconstructedBaseEffect
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    Description = description,
                    Flags = flags,
                    BaseCost = baseCost,
                    AssociatedItem = associatedItem,
                    MagicSchool = magicSchool,
                    ResistValue = resistValue,
                    Archetype = archetype,
                    ActorValue = actorValue,
                    Projectile = projectile,
                    Explosion = explosion,
                    Icon = icon,
                    ModelPath = modelPath,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return effects;
    }

    /// <summary>
    ///     Reconstruct all Weapon Mod (IMOD) records.
    /// </summary>
    public List<ReconstructedWeaponMod> ReconstructWeaponMods()
    {
        var mods = new List<ReconstructedWeaponMod>();

        if (_accessor == null)
        {
            return mods;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            foreach (var record in GetRecordsByType("IMOD"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, description = null;
                string? modelPath = null, icon = null;
                var value = 0;
                float weight = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ICON":
                            icon = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 8:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                value = BinaryPrimitives.ReadInt32BigEndian(span);
                                weight = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                            }
                            else
                            {
                                value = BinaryPrimitives.ReadInt32LittleEndian(span);
                                weight = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                            }

                            break;
                        }
                    }
                }

                mods.Add(new ReconstructedWeaponMod
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

    /// <summary>
    ///     Reconstruct all Recipe (RCPE) records.
    /// </summary>
    public List<ReconstructedRecipe> ReconstructRecipes()
    {
        var recipes = new List<ReconstructedRecipe>();

        if (_accessor == null)
        {
            return recipes;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("RCPE"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null;
                int requiredSkill = -1, requiredSkillLevel = 0;
                uint categoryFormId = 0, subcategoryFormId = 0;
                var ingredients = new List<RecipeIngredient>();
                var outputs = new List<RecipeOutput>();

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 16:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                requiredSkill = BinaryPrimitives.ReadInt32BigEndian(span);
                                requiredSkillLevel = BinaryPrimitives.ReadInt32BigEndian(span[4..]);
                                categoryFormId = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                                subcategoryFormId = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
                            }
                            else
                            {
                                requiredSkill = BinaryPrimitives.ReadInt32LittleEndian(span);
                                requiredSkillLevel = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
                                categoryFormId = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                                subcategoryFormId = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                            }

                            break;
                        }
                        case "RCIL" when sub.DataLength >= 8:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            var itemId = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(span)
                                : BinaryPrimitives.ReadUInt32LittleEndian(span);
                            var count = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(span[4..])
                                : BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                            ingredients.Add(new RecipeIngredient { ItemFormId = itemId, Count = count });
                            break;
                        }
                        case "RCOD" when sub.DataLength >= 8:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            var itemId = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(span)
                                : BinaryPrimitives.ReadUInt32LittleEndian(span);
                            var count = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(span[4..])
                                : BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                            outputs.Add(new RecipeOutput { ItemFormId = itemId, Count = count });
                            break;
                        }
                    }
                }

                recipes.Add(new ReconstructedRecipe
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

    /// <summary>
    ///     Reconstruct all Challenge (CHAL) records.
    /// </summary>
    public List<ReconstructedChallenge> ReconstructChallenges()
    {
        var challenges = new List<ReconstructedChallenge>();

        if (_accessor == null)
        {
            return challenges;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("CHAL"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, description = null, icon = null;
                uint challengeType = 0, threshold = 0, flags = 0, interval = 0;
                uint value1 = 0, value2 = 0, value3 = 0, script = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ICON":
                            icon = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "SCRI" when sub.DataLength >= 4:
                            script = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                        case "DATA" when sub.DataLength >= 20:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                challengeType = BinaryPrimitives.ReadUInt32BigEndian(span);
                                threshold = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
                                flags = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                                interval = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
                                value1 = sub.DataLength >= 24 ? BinaryPrimitives.ReadUInt32BigEndian(span[16..]) : 0;
                                value2 = sub.DataLength >= 28 ? BinaryPrimitives.ReadUInt32BigEndian(span[20..]) : 0;
                                value3 = sub.DataLength >= 32 ? BinaryPrimitives.ReadUInt32BigEndian(span[24..]) : 0;
                            }
                            else
                            {
                                challengeType = BinaryPrimitives.ReadUInt32LittleEndian(span);
                                threshold = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                                flags = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                                interval = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                                value1 = sub.DataLength >= 24 ? BinaryPrimitives.ReadUInt32LittleEndian(span[16..]) : 0;
                                value2 = sub.DataLength >= 28 ? BinaryPrimitives.ReadUInt32LittleEndian(span[20..]) : 0;
                                value3 = sub.DataLength >= 32 ? BinaryPrimitives.ReadUInt32LittleEndian(span[24..]) : 0;
                            }

                            break;
                        }
                    }
                }

                challenges.Add(new ReconstructedChallenge
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    Description = description,
                    Icon = icon,
                    ChallengeType = challengeType,
                    Threshold = threshold,
                    Flags = flags,
                    Interval = interval,
                    Value1 = value1,
                    Value2 = value2,
                    Value3 = value3,
                    Script = script,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return challenges;
    }

    /// <summary>
    ///     Reconstruct all Reputation (REPU) records.
    /// </summary>
    public List<ReconstructedReputation> ReconstructReputations()
    {
        var reputations = new List<ReconstructedReputation>();

        if (_accessor == null)
        {
            return reputations;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            foreach (var record in GetRecordsByType("REPU"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null;
                float positiveValue = 0, negativeValue = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 8:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                positiveValue = BinaryPrimitives.ReadSingleBigEndian(span);
                                negativeValue = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                            }
                            else
                            {
                                positiveValue = BinaryPrimitives.ReadSingleLittleEndian(span);
                                negativeValue = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                            }

                            break;
                        }
                    }
                }

                reputations.Add(new ReconstructedReputation
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    PositiveValue = positiveValue,
                    NegativeValue = negativeValue,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return reputations;
    }

    /// <summary>
    ///     Reconstruct all Projectile (PROJ) records.
    /// </summary>
    public List<ReconstructedProjectile> ReconstructProjectiles()
    {
        var projectiles = new List<ReconstructedProjectile>();

        if (_accessor == null)
        {
            return projectiles;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("PROJ"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, modelPath = null;
                ushort projFlags = 0, projType = 0;
                float gravity = 0, speed = 0, range = 0;
                float muzzleFlashDuration = 0, fadeDuration = 0, impactForce = 0, timer = 0;
                uint light = 0, muzzleFlashLight = 0, explosion = 0, sound = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 52:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                projFlags = BinaryPrimitives.ReadUInt16BigEndian(span);
                                projType = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
                                gravity = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                                speed = BinaryPrimitives.ReadSingleBigEndian(span[8..]);
                                range = BinaryPrimitives.ReadSingleBigEndian(span[12..]);
                                light = BinaryPrimitives.ReadUInt32BigEndian(span[16..]);
                                muzzleFlashLight = BinaryPrimitives.ReadUInt32BigEndian(span[20..]);
                                muzzleFlashDuration = BinaryPrimitives.ReadSingleBigEndian(span[28..]);
                                fadeDuration = BinaryPrimitives.ReadSingleBigEndian(span[32..]);
                                impactForce = BinaryPrimitives.ReadSingleBigEndian(span[36..]);
                                sound = BinaryPrimitives.ReadUInt32BigEndian(span[40..]);
                                timer = BinaryPrimitives.ReadSingleBigEndian(span[48..]);
                                explosion = sub.DataLength >= 56 ? BinaryPrimitives.ReadUInt32BigEndian(span[52..]) : 0;
                            }
                            else
                            {
                                projFlags = BinaryPrimitives.ReadUInt16LittleEndian(span);
                                projType = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
                                gravity = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                                speed = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
                                range = BinaryPrimitives.ReadSingleLittleEndian(span[12..]);
                                light = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
                                muzzleFlashLight = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
                                muzzleFlashDuration = BinaryPrimitives.ReadSingleLittleEndian(span[28..]);
                                fadeDuration = BinaryPrimitives.ReadSingleLittleEndian(span[32..]);
                                impactForce = BinaryPrimitives.ReadSingleLittleEndian(span[36..]);
                                sound = BinaryPrimitives.ReadUInt32LittleEndian(span[40..]);
                                timer = BinaryPrimitives.ReadSingleLittleEndian(span[48..]);
                                explosion = sub.DataLength >= 56
                                    ? BinaryPrimitives.ReadUInt32LittleEndian(span[52..])
                                    : 0;
                            }

                            break;
                        }
                    }
                }

                projectiles.Add(new ReconstructedProjectile
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    ModelPath = modelPath,
                    Flags = projFlags,
                    ProjectileType = projType,
                    Gravity = gravity,
                    Speed = speed,
                    Range = range,
                    Light = light,
                    MuzzleFlashLight = muzzleFlashLight,
                    Explosion = explosion,
                    Sound = sound,
                    MuzzleFlashDuration = muzzleFlashDuration,
                    FadeDuration = fadeDuration,
                    ImpactForce = impactForce,
                    Timer = timer,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return projectiles;
    }

    /// <summary>
    ///     Reconstruct all Explosion (EXPL) records.
    /// </summary>
    public List<ReconstructedExplosion> ReconstructExplosions()
    {
        var explosions = new List<ReconstructedExplosion>();

        if (_accessor == null)
        {
            return explosions;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("EXPL"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, modelPath = null;
                float force = 0, damage = 0, radius = 0, isRadius = 0;
                uint light = 0, sound1 = 0, flags = 0, impactDataSet = 0, sound2 = 0, enchantment = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "EITM" when sub.DataLength >= 4:
                            enchantment = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                        case "DATA" when sub.DataLength >= 36:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                force = BinaryPrimitives.ReadSingleBigEndian(span);
                                damage = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                                radius = BinaryPrimitives.ReadSingleBigEndian(span[8..]);
                                light = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
                                sound1 = BinaryPrimitives.ReadUInt32BigEndian(span[16..]);
                                flags = BinaryPrimitives.ReadUInt32BigEndian(span[20..]);
                                isRadius = BinaryPrimitives.ReadSingleBigEndian(span[24..]);
                                impactDataSet = BinaryPrimitives.ReadUInt32BigEndian(span[28..]);
                                sound2 = BinaryPrimitives.ReadUInt32BigEndian(span[32..]);
                            }
                            else
                            {
                                force = BinaryPrimitives.ReadSingleLittleEndian(span);
                                damage = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                                radius = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
                                light = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                                sound1 = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
                                flags = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
                                isRadius = BinaryPrimitives.ReadSingleLittleEndian(span[24..]);
                                impactDataSet = BinaryPrimitives.ReadUInt32LittleEndian(span[28..]);
                                sound2 = BinaryPrimitives.ReadUInt32LittleEndian(span[32..]);
                            }

                            break;
                        }
                    }
                }

                explosions.Add(new ReconstructedExplosion
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    ModelPath = modelPath,
                    Force = force,
                    Damage = damage,
                    Radius = radius,
                    Light = light,
                    Sound1 = sound1,
                    Flags = flags,
                    ISRadius = isRadius,
                    ImpactDataSet = impactDataSet,
                    Sound2 = sound2,
                    Enchantment = enchantment,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return explosions;
    }

    /// <summary>
    ///     Reconstruct all Message (MESG) records.
    /// </summary>
    public List<ReconstructedMessage> ReconstructMessages()
    {
        var messages = new List<ReconstructedMessage>();

        if (_accessor == null)
        {
            return messages;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("MESG"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, description = null, icon = null;
                uint questFormId = 0, flags = 0, displayTime = 0;
                var buttons = new List<string>();

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ICON":
                            icon = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "QNAM" when sub.DataLength >= 4:
                            questFormId = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                        case "DNAM" when sub.DataLength >= 4:
                            flags = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                        case "TNAM" when sub.DataLength >= 4:
                            displayTime = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                        case "ITXT":
                        {
                            var btnText = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(btnText))
                            {
                                buttons.Add(btnText);
                            }

                            break;
                        }
                    }
                }

                messages.Add(new ReconstructedMessage
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    Description = description,
                    Icon = icon,
                    QuestFormId = questFormId,
                    Flags = flags,
                    DisplayTime = displayTime,
                    Buttons = buttons,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return messages;
    }

    /// <summary>
    ///     Reconstruct all Class (CLAS) records.
    /// </summary>
    public List<ReconstructedClass> ReconstructClasses()
    {
        var classes = new List<ReconstructedClass>();

        if (_accessor == null)
        {
            return classes;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            foreach (var record in GetRecordsByType("CLAS"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, description = null, icon = null;
                var tagSkills = new List<int>();
                uint classFlags = 0, barterFlags = 0;
                byte trainingSkill = 0, trainingLevel = 0;
                var attributeWeights = Array.Empty<byte>();

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ICON":
                            icon = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 20:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            // DATA: 4 tag skill indices (int32 each) + flags (uint32) + barter flags (uint32)
                            for (var i = 0; i < 4 && i * 4 + 4 <= sub.DataLength - 8; i++)
                            {
                                var skill = record.IsBigEndian
                                    ? BinaryPrimitives.ReadInt32BigEndian(span[(i * 4)..])
                                    : BinaryPrimitives.ReadInt32LittleEndian(span[(i * 4)..]);
                                if (skill >= 0)
                                {
                                    tagSkills.Add(skill);
                                }
                            }

                            var flagsOffset = sub.DataLength - 8;
                            if (record.IsBigEndian)
                            {
                                classFlags = BinaryPrimitives.ReadUInt32BigEndian(span[flagsOffset..]);
                                barterFlags = BinaryPrimitives.ReadUInt32BigEndian(span[(flagsOffset + 4)..]);
                            }
                            else
                            {
                                classFlags = BinaryPrimitives.ReadUInt32LittleEndian(span[flagsOffset..]);
                                barterFlags = BinaryPrimitives.ReadUInt32LittleEndian(span[(flagsOffset + 4)..]);
                            }

                            break;
                        }
                        case "ATTR" when sub.DataLength >= 2:
                        {
                            trainingSkill = buffer[sub.DataOffset];
                            trainingLevel = buffer[sub.DataOffset + 1];
                            if (sub.DataLength >= 9)
                            {
                                attributeWeights = new byte[7];
                                Array.Copy(buffer, sub.DataOffset + 2, attributeWeights, 0, 7);
                            }

                            break;
                        }
                    }
                }

                classes.Add(new ReconstructedClass
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    Description = description,
                    Icon = icon,
                    TagSkills = tagSkills.ToArray(),
                    Flags = classFlags,
                    BarterFlags = barterFlags,
                    TrainingSkill = trainingSkill,
                    TrainingLevel = trainingLevel,
                    AttributeWeights = attributeWeights,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return classes;
    }
}
