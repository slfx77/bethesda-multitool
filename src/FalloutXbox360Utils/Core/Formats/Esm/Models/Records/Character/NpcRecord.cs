using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

/// <summary>
///     Parsed NPC record.
///     Aggregates data from NPC_ main record header, EDID, FULL, ACBS, faction refs, etc.
/// </summary>
public record NpcRecord
{
    /// <summary>FormID of the NPC record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID (e.g., "DocMitchell").</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name (e.g., "Doc Mitchell").</summary>
    public string? FullName { get; init; }

    /// <summary>Actor base stats from ACBS subrecord.</summary>
    public ActorBaseSubrecord? Stats { get; init; }

    /// <summary>Race FormID (RNAM subrecord).</summary>
    public uint? Race { get; init; }

    /// <summary>Script FormID (SCRI subrecord).</summary>
    public uint? Script { get; init; }

    /// <summary>Class FormID (CNAM subrecord).</summary>
    public uint? Class { get; init; }

    /// <summary>Death item FormID (INAM subrecord).</summary>
    public uint? DeathItem { get; init; }

    /// <summary>Voice type FormID (VTCK subrecord).</summary>
    public uint? VoiceType { get; init; }

    /// <summary>Template FormID (TPLT subrecord).</summary>
    public uint? Template { get; init; }

    /// <summary>Faction memberships (SNAM subrecords).</summary>
    public List<FactionMembership> Factions { get; init; } = [];

    /// <summary>Spell/ability FormIDs (SPLO subrecords).</summary>
    public List<uint> Spells { get; init; } = [];

    /// <summary>Item inventory (CNTO subrecords).</summary>
    public List<InventoryItem> Inventory { get; init; } = [];

    /// <summary>AI package FormIDs (PKID subrecords).</summary>
    public List<uint> Packages { get; init; } = [];

    /// <summary>S.P.E.C.I.A.L. stats (7 bytes: ST, PE, EN, CH, IN, AG, LK), empirically at dump +204.</summary>
    public byte[]? SpecialStats { get; init; }

    /// <summary>
    ///     Skills (14 bytes at dump +211, within TESNPCData struct after SPECIAL).
    ///     Order: Barter, BigGuns, EnergyWeapons, Explosives, Lockpick, Medicine, MeleeWeapons,
    ///     Repair, Science, Guns, Sneak, Speech, Survival, Unarmed.
    /// </summary>
    public byte[]? Skills { get; init; }

    /// <summary>AI behavior data from TESAIForm (aggression, confidence, etc.), empirically at dump +164.</summary>
    public NpcAiData? AiData { get; init; }

    /// <summary>Hair style FormID (TESHair*, pointer at dump +456).</summary>
    public uint? HairFormId { get; init; }

    /// <summary>Hair length (float at dump +460).</summary>
    public float? HairLength { get; init; }

    /// <summary>Eyes type FormID (TESEyes*, pointer at dump +464).</summary>
    public uint? EyesFormId { get; init; }

    /// <summary>Combat style FormID (TESCombatStyle*, pointer at dump +484).</summary>
    public uint? CombatStyleFormId { get; init; }

    /// <summary>Hair color (packed 0x00BBGGRR, uint32 at dump +488).</summary>
    public uint? HairColor { get; init; }

    /// <summary>Head part FormIDs (BGSHeadPart*, BSSimpleList at dump +492).</summary>
    public List<uint>? HeadPartFormIds { get; init; }

    /// <summary>Original race FormID (pOriginalRace, TESRace* at PDB offset 460+s).</summary>
    public uint? OriginalRace { get; init; }

    /// <summary>Face template NPC FormID (pFaceNPC, TESNPC* at PDB offset 464+s).</summary>
    public uint? FaceNpc { get; init; }

    /// <summary>Height multiplier (fHeight, float ~0.9-1.1, default 1.0, PDB offset 468+s).</summary>
    public float? Height { get; init; }

    /// <summary>Weight value (fWeight, float 0-100 body morph slider, PDB offset 472+s).</summary>
    public float? Weight { get; init; }

    /// <summary>Blood impact material enum (eBloodImpactMaterial, PDB offset 452+s). 0=Default, 1=Metal, etc.</summary>
    public byte? BloodImpactMaterial { get; init; }

    /// <summary>Last race face preset number (sLastRaceFaceNum, uint16, PDB offset 432+s).</summary>
    public ushort? RaceFacePreset { get; init; }

    /// <summary>FaceGen geometry-symmetric morph values (FGGS, 50 floats from pointer at dump +336).</summary>
    public float[]? FaceGenGeometrySymmetric { get; init; }

    /// <summary>FaceGen geometry-asymmetric morph values (FGGA, 30 floats from pointer at dump +368).</summary>
    public float[]? FaceGenGeometryAsymmetric { get; init; }

    /// <summary>FaceGen texture-symmetric morph values (FGTS, 50 floats from pointer at dump +400).</summary>
    public float[]? FaceGenTextureSymmetric { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>
    ///     Formats a packed HCLR hair color (0x00BBGGRR) as "#RRGGBB (R, G, B)".
    ///     Returns null if the value is null or zero.
    /// </summary>
    public static string? FormatHairColor(uint? hclr)
    {
        if (hclr is not { } v || v == 0)
            return null;

        var r = (byte)(v & 0xFF);
        var g = (byte)((v >> 8) & 0xFF);
        var b = (byte)((v >> 16) & 0xFF);
        return $"#{r:X2}{g:X2}{b:X2} ({r}, {g}, {b})";
    }
}
