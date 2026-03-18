using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Typed runtime reader for TESRace structs (FormType 0x0C, 1260 bytes).
///     Reads RACE_DATA (skill boosts, height/weight, flags), attributes,
///     FaceGen clamp values, voice types, default hair, age races, and
///     BSSimpleList-backed hair/eye/ability lists.
/// </summary>
internal sealed class RuntimeRaceReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeRaceReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    public RaceRecord? ReadRuntimeRace(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != RaceFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + StructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[StructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, StructSize);
        }
        catch
        {
            return null;
        }

        // Validate FormID at +12
        var formId = BinaryUtils.ReadUInt32BE(buffer, FormIdOffset);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        // cFullName (BSStringT at +44)
        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, FullNameOffset);

        // RACE_DATA struct (36 bytes at +96)
        var skillBoosts = ReadSkillBoosts(buffer);
        var maleHeight = BinaryUtils.ReadFloatBE(buffer, RaceDataOffset + 16);
        var femaleHeight = BinaryUtils.ReadFloatBE(buffer, RaceDataOffset + 20);
        var maleWeight = BinaryUtils.ReadFloatBE(buffer, RaceDataOffset + 24);
        var femaleWeight = BinaryUtils.ReadFloatBE(buffer, RaceDataOffset + 28);
        var dataFlags = BinaryUtils.ReadUInt32BE(buffer, RaceDataOffset + 32);

        // FaceGen clamp values
        var faceGenMainClamp = BinaryUtils.ReadFloatBE(buffer, FaceGenClamp1Offset);
        var faceGenFaceClamp = BinaryUtils.ReadFloatBE(buffer, FaceGenClamp2Offset);

        // sFaceCoordNum (uint16 at +1224)
        var faceCoordNum = BinaryUtils.ReadUInt16BE(buffer, FaceCoordNumOffset);

        // pDefaultHair — 2×pointer at +164 (male at +164, female at +168)
        var defaultHairMale = _context.FollowPointerToFormId(buffer, DefaultHairOffset);
        var defaultHairFemale = _context.FollowPointerToFormId(buffer, DefaultHairOffset + 4);

        // cDefaultHairColor — at +172, this is a small struct. The color index is at +172 directly.
        // From ESM CNAM it's a single byte. In runtime it's 4 bytes but the meaningful value is the first byte.
        var defaultHairColor = buffer[DefaultHairColorOffset];

        // pDefaultVoiceType — 2×pointer at +1228 (male at +1228, female at +1232)
        var maleVoice = _context.FollowPointerToFormId(buffer, DefaultVoiceTypeOffset);
        var femaleVoice = _context.FollowPointerToFormId(buffer, DefaultVoiceTypeOffset + 4);

        // pOldRace / pYoungRace — pointer→TESRace at +1236, +1240
        var olderRace = _context.FollowPointerToFormId(buffer, OldRaceOffset, RaceFormType);
        var youngerRace = _context.FollowPointerToFormId(buffer, YoungRaceOffset, RaceFormType);

        // BSSimpleList walks: spellList at +64, HairList at +156, EyeList at +184
        var abilities = WalkFormIdSimpleList(buffer, offset, SpellListOffset);
        var hairStyles = WalkFormIdSimpleList(buffer, offset, HairListOffset);
        var eyeColors = WalkFormIdSimpleList(buffer, offset, EyeListOffset);

        // Validate floats — reject garbage data
        if (!RuntimeMemoryContext.IsNormalFloat(maleHeight) ||
            !RuntimeMemoryContext.IsNormalFloat(femaleHeight))
        {
            return null;
        }

        return new RaceRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            SkillBoosts = skillBoosts,
            MaleHeight = maleHeight,
            FemaleHeight = femaleHeight,
            MaleWeight = RuntimeMemoryContext.IsNormalFloat(maleWeight) ? maleWeight : 1.0f,
            FemaleWeight = RuntimeMemoryContext.IsNormalFloat(femaleWeight) ? femaleWeight : 1.0f,
            DataFlags = dataFlags,
            FaceGenMainClamp = RuntimeMemoryContext.IsNormalFloat(faceGenMainClamp) ? faceGenMainClamp : 0f,
            FaceGenFaceClamp = RuntimeMemoryContext.IsNormalFloat(faceGenFaceClamp) ? faceGenFaceClamp : 0f,
            DefaultHairMaleFormId = defaultHairMale,
            DefaultHairFemaleFormId = defaultHairFemale,
            DefaultHairColor = defaultHairColor,
            MaleVoiceFormId = maleVoice,
            FemaleVoiceFormId = femaleVoice,
            OlderRaceFormId = olderRace,
            YoungerRaceFormId = youngerRace,
            AbilityFormIds = abilities,
            HairStyleFormIds = hairStyles,
            EyeColorFormIds = eyeColors,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Parse RACE_DATA.eSkillBonus — 7 pairs of (skill AV code, boost value), each 2 bytes.
    /// </summary>
    private static List<(int SkillIndex, sbyte Boost)> ReadSkillBoosts(byte[] buffer)
    {
        var boosts = new List<(int, sbyte)>();
        for (var i = 0; i < 7; i++)
        {
            var skillIndex = buffer[RaceDataOffset + i * 2];
            var boost = unchecked((sbyte)buffer[RaceDataOffset + i * 2 + 1]);
            if (skillIndex != 0xFF && boost != 0)
            {
                boosts.Add((skillIndex, boost));
            }
        }

        return boosts;
    }

    /// <summary>
    ///     Walk a BSSimpleList of TESForm* pointers and collect their FormIDs.
    ///     BSSimpleList layout: pHead(4) + padding(4) = 8 bytes.
    ///     Each node: pItem(4) + pNext(4) = 8 bytes.
    /// </summary>
    private List<uint> WalkFormIdSimpleList(byte[] structBuffer, long structFileOffset, int listOffset)
    {
        var result = new List<uint>();

        // BSSimpleList: first 4 bytes are the head pointer
        var headVa = BinaryUtils.ReadUInt32BE(structBuffer, listOffset);
        if (headVa == 0 || !_context.IsValidPointer(headVa))
        {
            return result;
        }

        var visited = new HashSet<uint>();
        var currentVa = headVa;

        for (var i = 0; i < MaxListNodes; i++)
        {
            if (currentVa == 0 || !visited.Add(currentVa))
            {
                break;
            }

            var nodeFileOffset = _context.VaToFileOffset(currentVa);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuffer == null)
            {
                break;
            }

            // node: pItem(4) + pNext(4)
            var itemVa = BinaryUtils.ReadUInt32BE(nodeBuffer);
            var nextVa = BinaryUtils.ReadUInt32BE(nodeBuffer, 4);

            if (itemVa != 0)
            {
                var formId = _context.FollowPointerVaToFormId(itemVa);
                if (formId is > 0)
                {
                    result.Add(formId.Value);
                }
            }

            currentVa = nextVa;
        }

        return result;
    }

    #region Constants

    private const byte RaceFormType = 0x0C;
    private const int StructSize = 1260;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 44;
    private const int SpellListOffset = 64;
    private const int RaceDataOffset = 96;
    private const int HairListOffset = 156;
    private const int DefaultHairOffset = 164;
    private const int DefaultHairColorOffset = 172;
    private const int FaceGenClamp1Offset = 176;
    private const int FaceGenClamp2Offset = 180;
    private const int EyeListOffset = 184;
    private const int FaceCoordNumOffset = 1224;
    private const int DefaultVoiceTypeOffset = 1228;
    private const int OldRaceOffset = 1236;
    private const int YoungRaceOffset = 1240;
    private const int MaxListNodes = 256;

    #endregion
}
