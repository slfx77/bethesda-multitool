using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reader for runtime game structures in Xbox 360 memory dumps.
///     Provides methods to read and reconstruct game data from memory.
/// </summary>
public sealed partial class RuntimeStructReader(MemoryMappedViewAccessor accessor, long fileSize, MinidumpInfo minidumpInfo)
{
    private const int MaxListItems = 50;
    private readonly MemoryMappedViewAccessor _accessor = accessor;
    private readonly long _fileSize = fileSize;
    private readonly MinidumpInfo _minidumpInfo = minidumpInfo;

    #region NPC Struct Constants


    private const int NpcStructSize = 508;
    private const int NpcAcbsOffset = 68;
    private const int NpcDeathItemPtrOffset = 92;
    private const int NpcVoiceTypePtrOffset = 96;
    private const int NpcTemplatePtrOffset = 100;
    private const int NpcRacePtrOffset = 288;
    private const int NpcClassPtrOffset = 320;
    private const int NpcAiDataOffset = 164;
    private const int NpcMoodOffset = 168;
    private const int NpcAiFlagsOffset = 172;
    private const int NpcAiAssistanceOffset = 178;
    private const int NpcSpecialOffset = 204;
    private const int NpcSpecialSize = 7;
    private const int NpcSkillsOffset = 292;
    private const int NpcSkillsSize = 14;
    private const int NpcFggsPointerOffset = 336;
    private const int NpcFggsCountOffset = 348;
    private const int NpcFggaPointerOffset = 368;
    private const int NpcFggaCountOffset = 380;
    private const int NpcFgtsPointerOffset = 400;
    private const int NpcFgtsCountOffset = 412;
    private const int NpcHairPtrOffset = 456;
    private const int NpcHairLengthOffset = 460;
    private const int NpcEyesPtrOffset = 464;
    private const int NpcCombatStylePtrOffset = 484;
    private const int NpcContainerDataOffset = 120;
    private const int NpcContainerNextOffset = 124;
    private const int NpcFactionListHeadOffset = 112;

    #endregion

    #region Weapon Struct Constants

    private const int WeapStructSize = 924;
    private const int WeapModelPathOffset = 80;
    private const int WeapValueOffset = 152;
    private const int WeapWeightOffset = 160;
    private const int WeapHealthOffset = 168;
    private const int WeapDamageOffset = 176;
    private const int WeapAmmoPtrOffset = 184;
    private const int WeapClipRoundsOffset = 192;
    private const int WeapDataStart = 260;
    private const int WeapAnimTypeOffset = 260;
    private const int WeapSpeedOffset = 264;
    private const int WeapReachOffset = 268;
    private const int DnamMinSpreadRelOffset = 16;
    private const int DnamSpreadRelOffset = 20;
    private const int DnamProjectileRelOffset = 36;
    private const int DnamVatsChanceRelOffset = 40;
    private const int DnamMinRangeRelOffset = 44;
    private const int DnamMaxRangeRelOffset = 48;
    private const int DnamActionPointsRelOffset = 68;
    private const int DnamShotsPerSecRelOffset = 88;
    private const int WeapCritDamageOffset = 456;
    private const int WeapCritChanceOffset = 460;
    private const int WeapPickupSoundOffset = 252;
    private const int WeapPutdownSoundOffset = 256;
    private const int WeapFireSound3DOffset = 548;
    private const int WeapFireSoundDistOffset = 552;
    private const int WeapFireSound2DOffset = 556;
    private const int WeapDryFireSoundOffset = 564;
    private const int WeapIdleSoundOffset = 572;
    private const int WeapEquipSoundOffset = 576;
    private const int WeapUnequipSoundOffset = 580;
    private const int WeapImpactDataSetOffset = 584;

    #endregion

    #region Other Item Struct Constants

    private const int ArmoStructSize = 416;
    private const int ArmoValueOffset = 108;
    private const int ArmoWeightOffset = 116;
    private const int ArmoHealthOffset = 124;
    private const int ArmoRatingOffset = 392;

    private const int AmmoStructSize = 236;
    private const int AmmoValueOffset = 140;

    private const int AlchStructSize = 232;
    private const int AlchWeightOffset = 168;
    private const int AlchValueOffset = 200;

    private const int MiscStructSize = 188;
    private const int MiscValueOffset = 136;
    private const int MiscWeightOffset = 144;

    private const int ContStructSize = 172;
    private const int ContModelPathOffset = 80;
    private const int ContScriptOffset = 116;
    private const int ContContentsDataOffset = 68;
    private const int ContContentsNextOffset = 72;
    private const int ContFlagsOffset = 140;

    #endregion

    #region Note/Faction/Quest Struct Constants

    private const int NoteStructSize = 160;
    private const int NoteTypeOffset = 140;
    private const int NoteModelPathOffset = 68;
    private const int NoteFullNameOffset = 92;

    private const int FactStructSize = 108;
    private const int FactFlagsOffset = 68;
    private const int FactFullNameOffset = 44;

    private const int QustStructSize = 140;
    private const int QustFlagsOffset = 76;
    private const int QustPriorityOffset = 77;
    private const int QustFullNameOffset = 68;

    #endregion

    #region Terminal/Creature Struct Constants

    private const int TermStructSize = 184;
    private const int TermDifficultyOffset = 132;
    private const int TermFlagsOffset = 133;
    private const int TermPasswordOffset = 136;
    private const int TermMenuItemListOffset = 152;
    private const int MenuItemSize = 120;
    private const int MenuItemResponseTextOffset = 0;
    private const int MenuItemResultScriptOffset = 16;
    private const int MenuItemSubMenuOffset = 112;

    private const int CreaStructSize = 440;
    private const int CreaModelPathOffset = 188;
    private const int CreaScriptOffset = 220;
    private const int CreaCombatSkillOffset = 228;
    private const int CreaMagicSkillOffset = 229;
    private const int CreaStealthSkillOffset = 230;
    private const int CreaAttackDamageOffset = 232;
    private const int CreaTypeOffset = 236;
    private const int CreaAcbsOffset = 24;

    #endregion

    #region Projectile Struct Constants

    private const int ProjStructSize = 224;
    private const int ProjDataBase = 112;
    private const int ProjGravityOffset = ProjDataBase + 4;
    private const int ProjSpeedOffset = ProjDataBase + 8;
    private const int ProjRangeOffset = ProjDataBase + 12;
    private const int ProjExplosionOffset = ProjDataBase + 36;
    private const int ProjActiveSoundOffset = ProjDataBase + 40;
    private const int ProjMuzzleFlashDurOffset = ProjDataBase + 44;
    private const int ProjForceOffset = ProjDataBase + 52;
    private const int ProjCountdownSoundOffset = ProjDataBase + 56;
    private const int ProjDeactivateSoundOffset = ProjDataBase + 60;

    #endregion

    #region Dialogue Struct Constants

    private const int DialStructSize = 88;
    private const int DialFullNameOffset = 44;
    private const int DialDataTypeOffset = 52;
    private const int DialDataFlagsOffset = 53;
    private const int DialPriorityOffset = 56;
    private const int DialQuestInfoListOffset = 60;
    private const int DialDummyPromptOffset = 68;
    private const int DialTopicCountOffset = 84;

    private const int InfoStructSize = 80;
    private const int InfoIndexOffset = 36;
    private const int InfoDataOffset = 39;
    private const int InfoSpeakerPtrOffset = 64;
    private const int InfoDifficultyOffset = 72;
    private const int InfoQuestPtrOffset = 76;

    #endregion

    #region World/Land Struct Constants

    private const int LandStructSize = 60;
    private const int LandLoadedDataPtrOffset = 56;
    private const int LoadedDataSize = 164;
    private const int LoadedDataCellXOffset = 152;
    private const int LoadedDataCellYOffset = 156;
    private const int LoadedDataBaseHeightOffset = 160;

    #endregion

    #region Script Struct Constants

    // PDB Script class: 84 bytes, Runtime (TESForm +16): 100 bytes, FormType: 0x11
    private const int ScptStructSize = 100;
    private const int ScptVarCountOffset = 40;
    private const int ScptRefCountOffset = 44;
    private const int ScptDataSizeOffset = 48;
    private const int ScptLastVarIdOffset = 52;
    private const int ScptIsQuestOffset = 56;
    private const int ScptIsMagicEffectOffset = 57;
    private const int ScptIsCompiledOffset = 58;
    private const int ScptTextPtrOffset = 60;         // m_text: char* -> SCTX source
    private const int ScptDataPtrOffset = 64;         // m_data: char* -> SCDA bytecode
    private const int ScptQuestDelayOffset = 72;
    private const int ScptOwnerQuestOffset = 80;      // pOwnerQuest: TESQuest*
    private const int ScptRefObjectsListOffset = 84;  // BSSimpleList<SCRIPT_REFERENCED_OBJECT*>
    private const int ScptVariablesListOffset = 92;   // BSSimpleList<ScriptVariable*>

    // SCRIPT_REFERENCED_OBJECT: 16 bytes (cEditorID BSStringT + pForm TESForm* + uiVariableID)
    private const int ScroFormPtrOffset = 8;
    private const int ScroStructSize = 16;

    // ScriptVariable: 32 bytes (SCRIPT_LOCAL data 24 bytes + cName BSStringT 8 bytes)
    private const int SvarIndexOffset = 0;            // uiID within SCRIPT_LOCAL
    private const int SvarIsIntegerOffset = 12;       // bIsInteger within SCRIPT_LOCAL
    private const int SvarNameOffset = 24;            // BSStringT cName
    private const int SvarStructSize = 32;

    #endregion

    #region Core Helper Methods

    /// <summary>
    ///     Check if a 32-bit value is a valid Xbox 360 pointer within captured memory.
    /// </summary>
    private bool IsValidPointer(uint value)
    {
        if (value == 0)
        {
            return false;
        }

        return _minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(value)).HasValue;
    }

    /// <summary>
    ///     Convert a 32-bit Xbox 360 virtual address to a file offset in the dump.
    ///     Returns null if the VA is not in any captured memory region.
    /// </summary>
    private long? VaToFileOffset(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        return _minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(va));
    }

    /// <summary>
    ///     Check if a float is a normal (non-NaN, non-Infinity) value.
    /// </summary>
    private static bool IsNormalFloat(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    ///     Read a byte array from the dump file at a given file offset.
    ///     Returns null if the read fails.
    /// </summary>
    private byte[]? ReadBytes(long fileOffset, int count)
    {
        if (fileOffset + count > _fileSize)
        {
            return null;
        }

        var buf = new byte[count];
        try
        {
            _accessor.ReadArray(fileOffset, buf, 0, count);
            return buf;
        }
        catch
        {
            return null;
        }
    }

    private static int ReadInt32BE(byte[] data, int offset)
    {
        return (int)BinaryUtils.ReadUInt32BE(data, offset);
    }

    /// <summary>
    ///     Read a float and validate it's within an expected range.
    ///     Returns 0 if the value is NaN, Inf, or outside range.
    /// </summary>
    private static float ReadValidatedFloat(byte[] buffer, int offset, float min, float max)
    {
        if (offset + 4 > buffer.Length)
        {
            return 0;
        }

        var value = BinaryUtils.ReadFloatBE(buffer, offset);
        if (!IsNormalFloat(value) || value < min || value > max)
        {
            return 0;
        }

        return value;
    }

    /// <summary>
    ///     Follow a 4-byte big-endian pointer at the given buffer offset to a TESForm object,
    ///     then read and return the FormID (uint32 BE at offset 12 in TESForm header).
    ///     Returns null if the pointer is invalid or the target is not a valid TESForm.
    /// </summary>
    private uint? FollowPointerToFormId(byte[] buffer, int pointerOffset)
    {
        if (pointerOffset + 4 > buffer.Length)
        {
            return null;
        }

        var pointer = BinaryUtils.ReadUInt32BE(buffer, pointerOffset);
        if (pointer == 0)
        {
            return null;
        }

        // Validate pointer is in dump memory range
        if (!IsValidPointer(pointer))
        {
            return null;
        }

        // Convert virtual address to file offset
        var fileOffset = _minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(pointer));
        if (!fileOffset.HasValue || fileOffset.Value + 24 > _fileSize)
        {
            return null;
        }

        // Read 24-byte TESForm header at target
        var tesFormBuffer = new byte[24];
        try
        {
            _accessor.ReadArray(fileOffset.Value, tesFormBuffer, 0, 24);
        }
        catch
        {
            return null;
        }

        // Validate form type (byte at offset 4, should be < 200)
        var formType = tesFormBuffer[4];
        if (formType > 200)
        {
            return null;
        }

        // Read FormID (uint32 BE at offset 12)
        var formId = BinaryUtils.ReadUInt32BE(tesFormBuffer, 12);

        // Basic validation
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return null;
        }

        return formId;
    }

    /// <summary>
    ///     Follow a virtual address pointer to a TESForm and return its FormID.
    ///     Similar to FollowPointerToFormId but takes a VA directly (not buffer offset).
    /// </summary>
    private uint? FollowPointerVaToFormId(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null)
        {
            return null;
        }

        var formBuf = ReadBytes(fileOffset.Value, 16);
        if (formBuf == null)
        {
            return null;
        }

        var formType = formBuf[4];
        if (formType > 200)
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(formBuf, 12);
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return null;
        }

        return formId;
    }

    #endregion
}
