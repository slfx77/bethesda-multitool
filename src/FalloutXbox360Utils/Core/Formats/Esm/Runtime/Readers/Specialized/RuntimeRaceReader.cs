using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESRace structs (FormType 0x0C, ~1260 bytes).
///     Reads RACE_DATA (skill boosts, height/weight, flags), attributes,
///     FaceGen clamp values, voice types, default hair, age races, and
///     BSSimpleList-backed hair/eye/ability lists.
///     Supports auto-detected layouts via <see cref="RuntimeRaceProbe" />.
/// </summary>
internal sealed class RuntimeRaceReader
{
    private const byte RaceFormType = 0x0C;
    private const int FormIdOffset = 12;
    private const int MaxListNodes = 256;
    private const int MinProbeMargin = 3;
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimeRaceLayout _layout;

    public RuntimeRaceReader(RuntimeMemoryContext context, RuntimeLayoutProbeResult<int[]>? probeResult = null)
    {
        _context = context;
        _layout = probeResult is { Margin: >= MinProbeMargin }
            ? RuntimeRaceLayout.FromShifts(probeResult.Winner.Layout)
            : RuntimeRaceLayout.CreateDefault();
    }

    public RaceRecord? ReadRuntimeRace(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != RaceFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + _layout.StructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[_layout.StructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, _layout.StructSize);
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, _layout.FullNameOffset);

        // RACE_DATA struct (36 bytes)
        var skillBoosts = ReadSkillBoosts(buffer);
        var maleHeight = BinaryUtils.ReadFloatBE(buffer, _layout.RaceDataOffset + 16);
        var femaleHeight = BinaryUtils.ReadFloatBE(buffer, _layout.RaceDataOffset + 20);
        var maleWeight = BinaryUtils.ReadFloatBE(buffer, _layout.RaceDataOffset + 24);
        var femaleWeight = BinaryUtils.ReadFloatBE(buffer, _layout.RaceDataOffset + 28);
        var dataFlags = BinaryUtils.ReadUInt32BE(buffer, _layout.RaceDataOffset + 32);

        // FaceGen clamp values
        var faceGenMainClamp = BinaryUtils.ReadFloatBE(buffer, _layout.FaceGenClamp1Offset);
        var faceGenFaceClamp = BinaryUtils.ReadFloatBE(buffer, _layout.FaceGenClamp2Offset);

        // pDefaultHair — 2×pointer (male, female)
        var defaultHairMale = _context.FollowPointerToFormId(buffer, _layout.DefaultHairOffset);
        var defaultHairFemale = _context.FollowPointerToFormId(buffer, _layout.DefaultHairOffset + 4);

        // cDefaultHairColor — single byte color index
        var defaultHairColor = buffer[_layout.DefaultHairColorOffset];

        // pDefaultVoiceType — 2×pointer (male, female)
        var maleVoice = _context.FollowPointerToFormId(buffer, _layout.DefaultVoiceTypeOffset);
        var femaleVoice = _context.FollowPointerToFormId(buffer, _layout.DefaultVoiceTypeOffset + 4);

        // pOldRace / pYoungRace — pointer→TESRace
        var olderRace = _context.FollowPointerToFormId(buffer, _layout.OldRaceOffset, RaceFormType);
        var youngerRace = _context.FollowPointerToFormId(buffer, _layout.YoungRaceOffset, RaceFormType);

        // BSSimpleList walks
        var abilities = WalkFormIdSimpleList(buffer, _layout.SpellListOffset);
        var hairStyles = WalkFormIdSimpleList(buffer, _layout.HairListOffset);
        var eyeColors = WalkFormIdSimpleList(buffer, _layout.EyeListOffset);

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
    private List<(int SkillIndex, sbyte Boost)> ReadSkillBoosts(byte[] buffer)
    {
        var boosts = new List<(int, sbyte)>();
        for (var i = 0; i < 7; i++)
        {
            var skillIndex = buffer[_layout.RaceDataOffset + i * 2];
            var boost = unchecked((sbyte)buffer[_layout.RaceDataOffset + i * 2 + 1]);
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
    private List<uint> WalkFormIdSimpleList(byte[] structBuffer, int listOffset)
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
}
