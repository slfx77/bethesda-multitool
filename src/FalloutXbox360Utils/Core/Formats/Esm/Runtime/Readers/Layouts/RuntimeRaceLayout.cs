namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Layouts;

/// <summary>
///     Layout for TESRace runtime struct. Offsets organized by inheritance group:
///     Group 0: TESForm (anchored, never shifts)
///     Group 1: TESFullName through FaceGen clamps (mid-chain)
///     Group 2: Late TESRace-specific fields (voice types, age races)
/// </summary>
internal readonly record struct RuntimeRaceLayout(
    int FullNameOffset,
    int SpellListOffset,
    int RaceDataOffset,
    int HairListOffset,
    int DefaultHairOffset,
    int DefaultHairColorOffset,
    int FaceGenClamp1Offset,
    int FaceGenClamp2Offset,
    int EyeListOffset,
    int DefaultVoiceTypeOffset,
    int OldRaceOffset,
    int YoungRaceOffset,
    int StructSize)
{
    public static RuntimeRaceLayout CreateDefault()
    {
        return new RuntimeRaceLayout(
            44,
            64,
            96,
            156,
            164,
            172,
            176,
            180,
            184,
            1228,
            1236,
            1240,
            1260);
    }

    public static RuntimeRaceLayout FromShifts(int[] shifts)
    {
        var d = CreateDefault();
        var s1 = shifts.Length > 1 ? shifts[1] : 0;
        var s2 = shifts.Length > 2 ? shifts[2] : 0;
        return new RuntimeRaceLayout(
            d.FullNameOffset + s1,
            d.SpellListOffset + s1,
            d.RaceDataOffset + s1,
            d.HairListOffset + s1,
            d.DefaultHairOffset + s1,
            d.DefaultHairColorOffset + s1,
            d.FaceGenClamp1Offset + s1,
            d.FaceGenClamp2Offset + s1,
            d.EyeListOffset + s1,
            d.DefaultVoiceTypeOffset + s2,
            d.OldRaceOffset + s2,
            d.YoungRaceOffset + s2,
            d.StructSize + Math.Max(s1, s2));
    }
}