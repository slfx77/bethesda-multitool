using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESRace structs (FormType 0x0C). Reads RACE_DATA
///     (skill boosts, height/weight, flags), attributes, FaceGen clamp values,
///     voice types, default hair, age races, and BSSimpleList-backed hair/eye/
///     ability lists via <see cref="PdbStructView" /> + <see cref="PdbStructView.WithShift" />
///     for the per-build G2 offset variation that <see cref="RuntimeRaceProbe" />
///     discovers (Debug builds shift G2 by -8; Release builds by +8).
/// </summary>
internal sealed class RuntimeRaceReader
{
    private const byte RaceFormType = 0x0C;
    private const int MinProbeMargin = 3;

    // Offset bands for WithShift. The PDB-declared offsets we care about fall in
    // two disjoint ranges:
    //   G1: cFullName(44) .. EyeList(184) — early-chain race fields
    //   G2: pDefaultVoiceType(1228) .. pYoungRace(1240) — late TESRace-specific
    // Observed probe shifts across 32 sampled DMPs: G1 always 0, G2 = -8 (Debug)
    // or +8 (Release). The middle range (192..1227) holds head/body model arrays
    // we don't read, so its shift behaviour is irrelevant.
    private const int G1MinOffset = 0;
    private const int G1MaxOffset = 1000;
    private const int G2MinOffset = 1200;
    private const int G2MaxOffset = int.MaxValue;

    private readonly RuntimeMemoryContext _context;
    private readonly RuntimePdbFieldAccessor _fields;
    private readonly int _g1Shift;
    private readonly int _g2Shift;

    public RuntimeRaceReader(RuntimeMemoryContext context, RuntimeLayoutProbeResult<int[]>? probeResult = null)
    {
        _context = context;
        _fields = new RuntimePdbFieldAccessor(context);

        if (probeResult is { Margin: >= MinProbeMargin })
        {
            var shifts = probeResult.Winner.Layout;
            _g1Shift = shifts.Length > 1 ? shifts[1] : 0;
            _g2Shift = shifts.Length > 2 ? shifts[2] : 0;
        }
    }

    public RaceRecord? ReadRuntimeRace(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != RaceFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry)
            ?.WithShift(G1MinOffset, G1MaxOffset, _g1Shift)
            .WithShift(G2MinOffset, G2MaxOffset, _g2Shift);
        if (view == null)
        {
            return null;
        }

        var fullName = entry.DisplayName ?? view.BsString("cFullName", "TESFullName");

        // RACE_DATA struct (36 bytes, starts at Data field — owner TESRace).
        var dataOffset = view.Offset("Data", "TESRace");
        if (dataOffset is not { } dataOff)
        {
            return null;
        }

        var skillBoosts = ReadSkillBoosts(view.Buffer, dataOff);
        var maleHeight = BinaryUtils.ReadFloatBE(view.Buffer, dataOff + 16);
        var femaleHeight = BinaryUtils.ReadFloatBE(view.Buffer, dataOff + 20);
        var maleWeight = BinaryUtils.ReadFloatBE(view.Buffer, dataOff + 24);
        var femaleWeight = BinaryUtils.ReadFloatBE(view.Buffer, dataOff + 28);
        var dataFlags = BinaryUtils.ReadUInt32BE(view.Buffer, dataOff + 32);

        var faceGenMainClamp = view.Float("fClampFaceGeoValue", "TESRace");
        var faceGenFaceClamp = view.Float("fClampFaceGeoValue2", "TESRace");

        // pDefaultHair is a 2-pointer block (male / female). The view's
        // FormIdPointer reads one 4-byte pointer; we need to follow the second
        // pointer manually at +4 offset from the resolved field offset.
        var defaultHairOff = view.Offset("pDefaultHair", "TESRace");
        var defaultHairMale = defaultHairOff is { } hairOff
            ? _context.FollowPointerToFormId(view.Buffer, hairOff)
            : null;
        var defaultHairFemale = defaultHairOff is { } hairOff2
            ? _context.FollowPointerToFormId(view.Buffer, hairOff2 + 4)
            : null;

        var defaultHairColor = view.Byte("cDefaultHairColor", "TESRace");

        // pDefaultVoiceType is also a 2-pointer block (male / female).
        var voiceOff = view.Offset("pDefaultVoiceType", "TESRace");
        var maleVoice = voiceOff is { } vOff
            ? _context.FollowPointerToFormId(view.Buffer, vOff)
            : null;
        var femaleVoice = voiceOff is { } vOff2
            ? _context.FollowPointerToFormId(view.Buffer, vOff2 + 4)
            : null;

        var olderRace = view.FormIdPointer("pOldRace", "TESRace", RaceFormType);
        var youngerRace = view.FormIdPointer("pYoungRace", "TESRace", RaceFormType);

        var abilities = view.FormIdSimpleList("spellList", "TESSpellList");
        var hairStyles = view.FormIdSimpleList("HairList", "TESRace");
        var eyeColors = view.FormIdSimpleList("EyeList", "TESRace");

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
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Parse RACE_DATA.eSkillBonus — 7 pairs of (skill AV code, boost value), each 2 bytes.
    /// </summary>
    private static List<(int SkillIndex, sbyte Boost)> ReadSkillBoosts(byte[] buffer, int dataOffset)
    {
        var boosts = new List<(int, sbyte)>();
        for (var i = 0; i < 7; i++)
        {
            var skillIndex = buffer[dataOffset + i * 2];
            var boost = unchecked((sbyte)buffer[dataOffset + i * 2 + 1]);
            if (skillIndex != 0xFF && boost != 0)
            {
                boosts.Add((skillIndex, boost));
            }
        }

        return boosts;
    }
}
