using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed NPC from memory dump.
///     Aggregates data from NPC_ main record header, EDID, FULL, ACBS, faction refs, etc.
/// </summary>
public record ReconstructedNpc
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
}
