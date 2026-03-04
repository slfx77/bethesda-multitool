using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class ActorRecordHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;
    private readonly NpcRecordHandler _npcs = new(context);
    private readonly CreatureRecordHandler _creatures = new(context);

    #region Actor Parsing Helpers

    internal static ActorBaseSubrecord? ParseActorBase(ReadOnlySpan<byte> data, long offset, bool bigEndian)
    {
        if (data.Length < 24)
        {
            return null;
        }

        var fields = SubrecordDataReader.ReadFields("ACBS", null, data, bigEndian);
        if (fields.Count == 0)
        {
            return null;
        }

        return new ActorBaseSubrecord(
            SubrecordDataReader.GetUInt32(fields, "Flags"),
            SubrecordDataReader.GetUInt16(fields, "Fatigue"),
            SubrecordDataReader.GetUInt16(fields, "BarterGold"),
            SubrecordDataReader.GetInt16(fields, "Level"),
            SubrecordDataReader.GetUInt16(fields, "CalcMin"),
            SubrecordDataReader.GetUInt16(fields, "CalcMax"),
            SubrecordDataReader.GetUInt16(fields, "SpeedMult"),
            SubrecordDataReader.GetFloat(fields, "KarmaAlignment"),
            SubrecordDataReader.GetInt16(fields, "Disposition"),
            SubrecordDataReader.GetUInt16(fields, "TemplateFlags"),
            offset, bigEndian);
    }

    internal static float[] ReadFloatArray(ReadOnlySpan<byte> data, bool bigEndian)
    {
        var count = data.Length / 4;
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = bigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(data[(i * 4)..])
                : BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..]);
        }

        return result;
    }

    #endregion

    #region ParseCreatures

    /// <summary>
    ///     Parse all Creature records from the scan result.
    ///     Delegates to <see cref="CreatureRecordHandler"/>.
    /// </summary>
    internal List<CreatureRecord> ParseCreatures()
    {
        return _creatures.ParseCreatures();
    }

    #endregion

    #region ParseFactions

    /// <summary>
    ///     Parse all Faction records from the scan result.
    /// </summary>
    internal List<FactionRecord> ParseFactions()
    {
        var factions = new List<FactionRecord>();
        var factionRecords = _context.GetRecordsByType("FACT").ToList();

        if (_context.Accessor == null)
        {
            foreach (var record in factionRecords)
            {
                factions.Add(new FactionRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FindFullNameInRecordBounds(record),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in factionRecords)
                {
                    var faction = ParseFactionFromAccessor(record, buffer);
                    if (faction != null)
                    {
                        factions.Add(faction);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        _context.MergeRuntimeRecords(factions, 0x08, f => f.FormId,
            (reader, entry) => reader.ReadRuntimeFaction(entry), "factions");

        return factions;
    }

    private FactionRecord? ParseFactionFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new FactionRecord
            {
                FormId = record.FormId,
                EditorId = _context.GetEditorId(record.FormId),
                FullName = _context.FindFullNameInRecordBounds(record),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        uint flags = 0;
        float crimeGoldMultiplier = 0;
        var relations = new List<FactionRelation>();
        var ranks = new List<FactionRank>();

        // Track current rank being built (RNAM groups MNAM/FNAM/INAM that follow)
        int? currentRankNumber = null;
        string? currentMaleTitle = null;
        string? currentFemaleTitle = null;
        string? currentInsignia = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 4:
                    // FACT DATA: 2 flag bytes + 2 unused
                    flags = (uint)(subData[0] | (subData[1] << 8));
                    break;
                case "XNAM" when sub.DataLength == 12:
                {
                    var fields = SubrecordDataReader.ReadFields("XNAM", "FACT", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        var factionFormId = SubrecordDataReader.GetUInt32(fields, "Faction");
                        var modifier = SubrecordDataReader.GetInt32(fields, "Modifier");
                        var combatReaction = SubrecordDataReader.GetUInt32(fields, "CombatReaction");
                        relations.Add(new FactionRelation(factionFormId, modifier, combatReaction));
                    }

                    break;
                }
                case "RNAM" when sub.DataLength == 4:
                {
                    // Flush previous rank if any
                    if (currentRankNumber.HasValue)
                    {
                        ranks.Add(new FactionRank(currentRankNumber.Value, currentMaleTitle, currentFemaleTitle,
                            currentInsignia));
                    }

                    var fields = SubrecordDataReader.ReadFields("RNAM", "FACT", subData, record.IsBigEndian);
                    currentRankNumber = fields.Count > 0
                        ? SubrecordDataReader.GetInt32(fields, "RankNumber")
                        : 0;
                    currentMaleTitle = null;
                    currentFemaleTitle = null;
                    currentInsignia = null;
                    break;
                }
                case "MNAM" when sub.DataLength > 0 && currentRankNumber.HasValue:
                    currentMaleTitle = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FNAM" when sub.DataLength > 0 && currentRankNumber.HasValue:
                    currentFemaleTitle = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "INAM" when sub.DataLength > 0 && currentRankNumber.HasValue:
                    currentInsignia = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "CRVA" when sub.DataLength >= 4:
                {
                    var fields = SubrecordDataReader.ReadFields("CRVA", "FACT", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        crimeGoldMultiplier = SubrecordDataReader.GetFloat(fields, "CrimeGoldMultiplier");
                    }

                    break;
                }
            }
        }

        // Flush last rank
        if (currentRankNumber.HasValue)
        {
            ranks.Add(new FactionRank(currentRankNumber.Value, currentMaleTitle, currentFemaleTitle, currentInsignia));
        }

        // Track FullName for display name map
        if (!string.IsNullOrEmpty(fullName))
        {
            _context.FormIdToFullName.TryAdd(record.FormId, fullName);
        }

        return new FactionRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            FullName = fullName,
            Flags = flags,
            CrimeGoldMultiplier = crimeGoldMultiplier,
            Relations = relations,
            Ranks = ranks,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region ParseNpcs

    /// <summary>
    ///     Parse all NPC records from the scan result.
    ///     Delegates to <see cref="NpcRecordHandler"/>.
    /// </summary>
    internal List<NpcRecord> ParseNpcs()
    {
        return _npcs.ParseNpcs();
    }

    #endregion

    #region ParseRaces

    /// <summary>
    ///     Parse all Race records from the scan result.
    /// </summary>
    internal List<RaceRecord> ParseRaces()
    {
        var races = new List<RaceRecord>();
        var raceRecords = _context.GetRecordsByType("RACE").ToList();

        if (_context.Accessor == null)
        {
            foreach (var record in raceRecords)
            {
                races.Add(new RaceRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in raceRecords)
                {
                    var race = ParseRaceFromAccessor(record, buffer);
                    if (race != null)
                    {
                        races.Add(race);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return races;
    }

    private RaceRecord? ParseRaceFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new RaceRecord
            {
                FormId = record.FormId,
                EditorId = _context.GetEditorId(record.FormId),
                FullName = _context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? description = null;

        // Skill Boosts from DATA (7 pairs of AV code + boost value)
        var skillBoosts = new List<(int SkillIndex, sbyte Boost)>();

        // Heights and weights (from DATA)
        var maleHeight = 1.0f;
        var femaleHeight = 1.0f;
        var maleWeight = 1.0f;
        var femaleWeight = 1.0f;
        uint dataFlags = 0;

        // Related FormIDs
        uint? olderRace = null;
        uint? youngerRace = null;
        uint? maleVoice = null;
        uint? femaleVoice = null;
        var abilityFormIds = new List<uint>();

        // Hair/Eyes
        uint? defaultHairMale = null;
        uint? defaultHairFemale = null;
        byte? defaultHairColor = null;
        var hairStyleFormIds = new List<uint>();
        var eyeColorFormIds = new List<uint>();

        // FaceGen
        float faceGenMainClamp = 0;
        float faceGenFaceClamp = 0;

        // FaceGen base morph coefficients (male after MNAM, female after FNAM)
        // Default to true: RACE records define male section first (before MNAM marker)
        var inMaleSection = true;
        float[]? maleFggs = null, maleFgga = null, maleFgts = null;
        float[]? femaleFggs = null, femaleFgga = null, femaleFgts = null;

        // Head/body part mesh and texture paths (from NAM0/NAM1 sections)
        var inHeadPartsSection = false;
        var inBodyPartsSection = false;
        var currentIndx = -1;
        string? maleHeadModel = null, femaleHeadModel = null;
        string? maleHeadTexture = null, femaleHeadTexture = null;
        string? maleUpperBody = null, femaleUpperBody = null;
        string? maleLeftHand = null, femaleLeftHand = null;
        string? maleRightHand = null, femaleRightHand = null;
        string? maleBodyTexture = null, femaleBodyTexture = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DESC":
                    description = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 36:
                {
                    // Skill Boosts: 7 pairs of (Skill AV code, Boost value)
                    for (var i = 0; i < 14; i += 2)
                    {
                        var avCode = (sbyte)subData[i];
                        var boost = (sbyte)subData[i + 1];
                        if (avCode >= 0 && boost != 0)
                        {
                            skillBoosts.Add((avCode, boost));
                        }
                    }

                    var fields = SubrecordDataReader.ReadFields("DATA", "RACE", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        maleHeight = SubrecordDataReader.GetFloat(fields, "MaleHeight");
                        femaleHeight = SubrecordDataReader.GetFloat(fields, "FemaleHeight");
                        maleWeight = SubrecordDataReader.GetFloat(fields, "MaleWeight");
                        femaleWeight = SubrecordDataReader.GetFloat(fields, "FemaleWeight");
                        dataFlags = SubrecordDataReader.GetUInt32(fields, "Flags");
                    }

                    break;
                }
                case "DNAM" when sub.DataLength == 8:
                    // Default hair styles (Male FormID + Female FormID)
                    defaultHairMale = RecordParserContext.ReadFormId(subData[..4], record.IsBigEndian);
                    defaultHairFemale = RecordParserContext.ReadFormId(subData[4..], record.IsBigEndian);
                    break;
                case "HNAM" when sub.DataLength >= 4:
                    // Hair style FormID array
                    for (var i = 0; i + 4 <= sub.DataLength; i += 4)
                    {
                        hairStyleFormIds.Add(RecordParserContext.ReadFormId(subData[i..], record.IsBigEndian));
                    }

                    break;
                case "ENAM" when sub.DataLength >= 4:
                    // Eye color FormID array
                    for (var i = 0; i + 4 <= sub.DataLength; i += 4)
                    {
                        eyeColorFormIds.Add(RecordParserContext.ReadFormId(subData[i..], record.IsBigEndian));
                    }

                    break;
                case "CNAM" when sub.DataLength >= 1:
                    defaultHairColor = subData[0];
                    break;
                case "PNAM" when sub.DataLength == 4:
                {
                    var fields = SubrecordDataReader.ReadFields("PNAM", "RACE", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        faceGenMainClamp = SubrecordDataReader.GetFloat(fields, "FaceGenMainClamp");
                    }

                    break;
                }
                case "UNAM" when sub.DataLength == 4:
                {
                    var fields = SubrecordDataReader.ReadFields("UNAM", "RACE", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        faceGenFaceClamp = SubrecordDataReader.GetFloat(fields, "FaceGenFaceClamp");
                    }

                    break;
                }
                case "ONAM" when sub.DataLength == 4:
                    olderRace = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "YNAM" when sub.DataLength == 4:
                    youngerRace = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "VTCK" when sub.DataLength >= 8:
                    maleVoice = RecordParserContext.ReadFormId(subData[..4], record.IsBigEndian);
                    femaleVoice = RecordParserContext.ReadFormId(subData[4..], record.IsBigEndian);
                    break;
                case "SPLO" when sub.DataLength == 4:
                    abilityFormIds.Add(RecordParserContext.ReadFormId(subData, record.IsBigEndian));
                    break;
                case "MNAM" when sub.DataLength == 0:
                    inMaleSection = true;
                    currentIndx = -1;
                    break;
                case "FNAM" when sub.DataLength == 0:
                    inMaleSection = false;
                    currentIndx = -1;
                    break;
                case "NAM0" when sub.DataLength == 0:
                    inHeadPartsSection = true;
                    inBodyPartsSection = false;
                    break;
                case "NAM1" when sub.DataLength == 0:
                    inHeadPartsSection = false;
                    inBodyPartsSection = true;
                    break;
                case "INDX" when sub.DataLength == 4 && (inHeadPartsSection || inBodyPartsSection):
                    currentIndx = (int)RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "MODL" when inHeadPartsSection && currentIndx == 0:
                {
                    var path = EsmStringUtils.ReadNullTermString(subData);
                    if (path != null)
                    {
                        if (inMaleSection) maleHeadModel = path;
                        else femaleHeadModel = path;
                    }

                    break;
                }
                case "MODL" when inBodyPartsSection && currentIndx >= 0 && currentIndx <= 2:
                {
                    var path = EsmStringUtils.ReadNullTermString(subData);
                    if (path != null)
                    {
                        if (currentIndx == 0) { if (inMaleSection) maleUpperBody = path; else femaleUpperBody = path; }
                        else if (currentIndx == 1) { if (inMaleSection) maleLeftHand = path; else femaleLeftHand = path; }
                        else if (currentIndx == 2) { if (inMaleSection) maleRightHand = path; else femaleRightHand = path; }
                    }

                    break;
                }
                case "ICON" when inHeadPartsSection && currentIndx == 0:
                {
                    var path = EsmStringUtils.ReadNullTermString(subData);
                    if (path != null)
                    {
                        if (inMaleSection) maleHeadTexture = path;
                        else femaleHeadTexture = path;
                    }

                    break;
                }
                case "ICON" when inBodyPartsSection && currentIndx == 0:
                {
                    var path = EsmStringUtils.ReadNullTermString(subData);
                    if (path != null)
                    {
                        if (inMaleSection) maleBodyTexture = path;
                        else femaleBodyTexture = path;
                    }

                    break;
                }
                case "FGGS" when sub.DataLength == 200:
                    if (inMaleSection)
                    {
                        maleFggs = ReadFloatArray(subData, record.IsBigEndian);
                    }
                    else
                    {
                        femaleFggs = ReadFloatArray(subData, record.IsBigEndian);
                    }

                    break;
                case "FGGA" when sub.DataLength == 120:
                    if (inMaleSection)
                    {
                        maleFgga = ReadFloatArray(subData, record.IsBigEndian);
                    }
                    else
                    {
                        femaleFgga = ReadFloatArray(subData, record.IsBigEndian);
                    }

                    break;
                case "FGTS" when sub.DataLength == 200:
                    if (inMaleSection)
                    {
                        maleFgts = ReadFloatArray(subData, record.IsBigEndian);
                    }
                    else
                    {
                        femaleFgts = ReadFloatArray(subData, record.IsBigEndian);
                    }

                    break;
            }
        }

        // Track FullName for display name map
        if (!string.IsNullOrEmpty(fullName))
        {
            _context.FormIdToFullName.TryAdd(record.FormId, fullName);
        }

        return new RaceRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            FullName = fullName,
            Description = description,
            SkillBoosts = skillBoosts,
            MaleHeight = maleHeight,
            FemaleHeight = femaleHeight,
            MaleWeight = maleWeight,
            FemaleWeight = femaleWeight,
            DataFlags = dataFlags,
            DefaultHairMaleFormId = defaultHairMale != 0 ? defaultHairMale : null,
            DefaultHairFemaleFormId = defaultHairFemale != 0 ? defaultHairFemale : null,
            DefaultHairColor = defaultHairColor,
            HairStyleFormIds = hairStyleFormIds,
            EyeColorFormIds = eyeColorFormIds,
            FaceGenMainClamp = faceGenMainClamp,
            FaceGenFaceClamp = faceGenFaceClamp,
            MaleFaceGenGeometrySymmetric = maleFggs,
            MaleFaceGenGeometryAsymmetric = maleFgga,
            MaleFaceGenTextureSymmetric = maleFgts,
            FemaleFaceGenGeometrySymmetric = femaleFggs,
            FemaleFaceGenGeometryAsymmetric = femaleFgga,
            FemaleFaceGenTextureSymmetric = femaleFgts,
            MaleHeadModelPath = maleHeadModel,
            FemaleHeadModelPath = femaleHeadModel,
            MaleHeadTexturePath = maleHeadTexture,
            FemaleHeadTexturePath = femaleHeadTexture,
            MaleUpperBodyPath = maleUpperBody,
            FemaleUpperBodyPath = femaleUpperBody,
            MaleLeftHandPath = maleLeftHand,
            FemaleLeftHandPath = femaleLeftHand,
            MaleRightHandPath = maleRightHand,
            FemaleRightHandPath = femaleRightHand,
            MaleBodyTexturePath = maleBodyTexture,
            FemaleBodyTexturePath = femaleBodyTexture,
            OlderRaceFormId = olderRace != 0 ? olderRace : null,
            YoungerRaceFormId = youngerRace != 0 ? youngerRace : null,
            MaleVoiceFormId = maleVoice != 0 ? maleVoice : null,
            FemaleVoiceFormId = femaleVoice != 0 ? femaleVoice : null,
            AbilityFormIds = abilityFormIds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
