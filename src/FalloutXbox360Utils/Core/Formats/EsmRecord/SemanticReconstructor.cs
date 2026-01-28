using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Converters.Esm.Schema;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     Reconstructs semantic game objects from detected ESM records in memory dumps.
///     Links FormIDs, correlates subrecords, and builds complete objects like NPCs, Quests, Notes, etc.
/// </summary>
public sealed class SemanticReconstructor
{
    private readonly EsmRecordScanResult _scanResult;
    private readonly Dictionary<uint, DetectedMainRecord> _recordsByFormId;
    private readonly Dictionary<uint, string> _formIdToEditorId;
    private readonly Dictionary<string, uint> _editorIdToFormId;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly long _fileSize;

    /// <summary>
    ///     Creates a new SemanticReconstructor with scan results and optional memory-mapped access.
    /// </summary>
    /// <param name="scanResult">The ESM record scan results from EsmRecordFormat.</param>
    /// <param name="formIdCorrelations">FormID to Editor ID correlations.</param>
    /// <param name="accessor">Optional memory-mapped accessor for reading additional record data.</param>
    /// <param name="fileSize">Size of the memory dump file.</param>
    public SemanticReconstructor(
        EsmRecordScanResult scanResult,
        Dictionary<uint, string>? formIdCorrelations = null,
        MemoryMappedViewAccessor? accessor = null,
        long fileSize = 0)
    {
        _scanResult = scanResult;
        _accessor = accessor;
        _fileSize = fileSize;

        // Build FormID lookup from main records
        _recordsByFormId = scanResult.MainRecords
            .GroupBy(r => r.FormId)
            .ToDictionary(g => g.Key, g => g.First());

        // Build EditorID lookups
        _formIdToEditorId = formIdCorrelations ?? BuildFormIdToEditorIdMap(scanResult);
        _editorIdToFormId = _formIdToEditorId
            .GroupBy(kv => kv.Value)
            .ToDictionary(g => g.Key, g => g.First().Key);
    }

    /// <summary>
    ///     Get the Editor ID for a FormID.
    /// </summary>
    public string? GetEditorId(uint formId) => _formIdToEditorId.GetValueOrDefault(formId);

    /// <summary>
    ///     Get the FormID for an Editor ID.
    /// </summary>
    public uint? GetFormId(string editorId) =>
        _editorIdToFormId.TryGetValue(editorId, out var formId) ? formId : null;

    /// <summary>
    ///     Get a main record by FormID.
    /// </summary>
    public DetectedMainRecord? GetRecord(uint formId) => _recordsByFormId.GetValueOrDefault(formId);

    /// <summary>
    ///     Get all main records of a specific type.
    /// </summary>
    public IEnumerable<DetectedMainRecord> GetRecordsByType(string recordType) =>
        _scanResult.MainRecords.Where(r => r.RecordType == recordType);

    /// <summary>
    ///     Perform full semantic reconstruction of all supported record types.
    /// </summary>
    public SemanticReconstructionResult ReconstructAll()
    {
        return new SemanticReconstructionResult
        {
            // Characters
            Npcs = ReconstructNpcs(),
            Races = ReconstructRaces(),

            // Quests and Dialogue
            Quests = ReconstructQuests(),
            Dialogues = ReconstructDialogue(),
            Notes = ReconstructNotes(),

            // Items
            Weapons = ReconstructWeapons(),
            Armor = ReconstructArmor(),
            Ammo = ReconstructAmmo(),
            Consumables = ReconstructConsumables(),
            MiscItems = ReconstructMiscItems(),

            // Abilities
            Perks = ReconstructPerks(),
            Spells = ReconstructSpells(),

            // World
            Cells = ReconstructCells(),
            Worldspaces = ReconstructWorldspaces(),

            FormIdToEditorId = new Dictionary<uint, string>(_formIdToEditorId),
            TotalRecordsProcessed = _scanResult.MainRecords.Count
        };
    }

    /// <summary>
    ///     Reconstruct all NPC records from the scan result.
    /// </summary>
    public List<ReconstructedNpc> ReconstructNpcs()
    {
        var npcs = new List<ReconstructedNpc>();
        var npcRecords = GetRecordsByType("NPC_").ToList();

        if (_accessor == null)
        {
            // Without accessor, use already-parsed subrecords from scan result
            foreach (var record in npcRecords)
            {
                var npc = ReconstructNpcFromScanResult(record);
                if (npc != null)
                {
                    npcs.Add(npc);
                }
            }
        }
        else
        {
            // With accessor, read full record data for better reconstruction
            var buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                foreach (var record in npcRecords)
                {
                    var npc = ReconstructNpcFromAccessor(record, buffer);
                    if (npc != null)
                    {
                        npcs.Add(npc);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return npcs;
    }

    /// <summary>
    ///     Reconstruct all Quest records from the scan result.
    /// </summary>
    public List<ReconstructedQuest> ReconstructQuests()
    {
        var quests = new List<ReconstructedQuest>();
        var questRecords = GetRecordsByType("QUST").ToList();

        if (_accessor == null)
        {
            foreach (var record in questRecords)
            {
                var quest = ReconstructQuestFromScanResult(record);
                if (quest != null)
                {
                    quests.Add(quest);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(32768); // Quests can be larger
            try
            {
                foreach (var record in questRecords)
                {
                    var quest = ReconstructQuestFromAccessor(record, buffer);
                    if (quest != null)
                    {
                        quests.Add(quest);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return quests;
    }

    /// <summary>
    ///     Reconstruct all Dialogue (INFO) records from the scan result.
    /// </summary>
    public List<ReconstructedDialogue> ReconstructDialogue()
    {
        var dialogues = new List<ReconstructedDialogue>();
        var infoRecords = GetRecordsByType("INFO").ToList();

        if (_accessor == null)
        {
            foreach (var record in infoRecords)
            {
                var dialogue = ReconstructDialogueFromScanResult(record);
                if (dialogue != null)
                {
                    dialogues.Add(dialogue);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in infoRecords)
                {
                    var dialogue = ReconstructDialogueFromAccessor(record, buffer);
                    if (dialogue != null)
                    {
                        dialogues.Add(dialogue);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return dialogues;
    }

    /// <summary>
    ///     Reconstruct all Note records from the scan result.
    /// </summary>
    public List<ReconstructedNote> ReconstructNotes()
    {
        var notes = new List<ReconstructedNote>();
        var noteRecords = GetRecordsByType("NOTE").ToList();

        if (_accessor == null)
        {
            foreach (var record in noteRecords)
            {
                var note = ReconstructNoteFromScanResult(record);
                if (note != null)
                {
                    notes.Add(note);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in noteRecords)
                {
                    var note = ReconstructNoteFromAccessor(record, buffer);
                    if (note != null)
                    {
                        notes.Add(note);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return notes;
    }

    /// <summary>
    ///     Reconstruct all Cell records from the scan result.
    /// </summary>
    public List<ReconstructedCell> ReconstructCells()
    {
        var cells = new List<ReconstructedCell>();
        var cellRecords = GetRecordsByType("CELL").ToList();

        // Build a lookup of placed references by proximity to cells
        var refrRecords = _scanResult.RefrRecords;

        if (_accessor == null)
        {
            foreach (var record in cellRecords)
            {
                var cell = ReconstructCellFromScanResult(record, refrRecords);
                if (cell != null)
                {
                    cells.Add(cell);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in cellRecords)
                {
                    var cell = ReconstructCellFromAccessor(record, refrRecords, buffer);
                    if (cell != null)
                    {
                        cells.Add(cell);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return cells;
    }

    /// <summary>
    ///     Reconstruct all Worldspace records from the scan result.
    /// </summary>
    public List<ReconstructedWorldspace> ReconstructWorldspaces()
    {
        var worldspaces = new List<ReconstructedWorldspace>();
        var wrldRecords = GetRecordsByType("WRLD").ToList();

        if (_accessor == null)
        {
            foreach (var record in wrldRecords)
            {
                var worldspace = ReconstructWorldspaceFromScanResult(record);
                if (worldspace != null)
                {
                    worldspaces.Add(worldspace);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in wrldRecords)
                {
                    var worldspace = ReconstructWorldspaceFromAccessor(record, buffer);
                    if (worldspace != null)
                    {
                        worldspaces.Add(worldspace);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return worldspaces;
    }

    /// <summary>
    ///     Reconstruct all Weapon records from the scan result.
    /// </summary>
    public List<ReconstructedWeapon> ReconstructWeapons()
    {
        var weapons = new List<ReconstructedWeapon>();
        var weaponRecords = GetRecordsByType("WEAP").ToList();

        if (_accessor == null)
        {
            foreach (var record in weaponRecords)
            {
                weapons.Add(new ReconstructedWeapon
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
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
                foreach (var record in weaponRecords)
                {
                    var weapon = ReconstructWeaponFromAccessor(record, buffer);
                    if (weapon != null)
                    {
                        weapons.Add(weapon);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return weapons;
    }

    /// <summary>
    ///     Reconstruct all Armor records from the scan result.
    /// </summary>
    public List<ReconstructedArmor> ReconstructArmor()
    {
        var armor = new List<ReconstructedArmor>();
        var armorRecords = GetRecordsByType("ARMO").ToList();

        if (_accessor == null)
        {
            foreach (var record in armorRecords)
            {
                armor.Add(new ReconstructedArmor
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
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
                foreach (var record in armorRecords)
                {
                    var item = ReconstructArmorFromAccessor(record, buffer);
                    if (item != null)
                    {
                        armor.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return armor;
    }

    /// <summary>
    ///     Reconstruct all Ammo records from the scan result.
    /// </summary>
    public List<ReconstructedAmmo> ReconstructAmmo()
    {
        var ammo = new List<ReconstructedAmmo>();
        var ammoRecords = GetRecordsByType("AMMO").ToList();

        if (_accessor == null)
        {
            foreach (var record in ammoRecords)
            {
                ammo.Add(new ReconstructedAmmo
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
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
                foreach (var record in ammoRecords)
                {
                    var item = ReconstructAmmoFromAccessor(record, buffer);
                    if (item != null)
                    {
                        ammo.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return ammo;
    }

    /// <summary>
    ///     Reconstruct all Consumable (ALCH) records from the scan result.
    /// </summary>
    public List<ReconstructedConsumable> ReconstructConsumables()
    {
        var consumables = new List<ReconstructedConsumable>();
        var alchRecords = GetRecordsByType("ALCH").ToList();

        if (_accessor == null)
        {
            foreach (var record in alchRecords)
            {
                consumables.Add(new ReconstructedConsumable
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
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
                foreach (var record in alchRecords)
                {
                    var item = ReconstructConsumableFromAccessor(record, buffer);
                    if (item != null)
                    {
                        consumables.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return consumables;
    }

    /// <summary>
    ///     Reconstruct all Misc Item records from the scan result.
    /// </summary>
    public List<ReconstructedMiscItem> ReconstructMiscItems()
    {
        var miscItems = new List<ReconstructedMiscItem>();
        var miscRecords = GetRecordsByType("MISC").ToList();

        if (_accessor == null)
        {
            foreach (var record in miscRecords)
            {
                miscItems.Add(new ReconstructedMiscItem
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
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
                foreach (var record in miscRecords)
                {
                    var item = ReconstructMiscItemFromAccessor(record, buffer);
                    if (item != null)
                    {
                        miscItems.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return miscItems;
    }

    /// <summary>
    ///     Reconstruct all Perk records from the scan result.
    /// </summary>
    public List<ReconstructedPerk> ReconstructPerks()
    {
        var perks = new List<ReconstructedPerk>();
        var perkRecords = GetRecordsByType("PERK").ToList();

        if (_accessor == null)
        {
            foreach (var record in perkRecords)
            {
                perks.Add(new ReconstructedPerk
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
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
                foreach (var record in perkRecords)
                {
                    var perk = ReconstructPerkFromAccessor(record, buffer);
                    if (perk != null)
                    {
                        perks.Add(perk);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return perks;
    }

    /// <summary>
    ///     Reconstruct all Spell records from the scan result.
    /// </summary>
    public List<ReconstructedSpell> ReconstructSpells()
    {
        var spells = new List<ReconstructedSpell>();
        var spellRecords = GetRecordsByType("SPEL").ToList();

        if (_accessor == null)
        {
            foreach (var record in spellRecords)
            {
                spells.Add(new ReconstructedSpell
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
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
                foreach (var record in spellRecords)
                {
                    var spell = ReconstructSpellFromAccessor(record, buffer);
                    if (spell != null)
                    {
                        spells.Add(spell);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return spells;
    }

    /// <summary>
    ///     Reconstruct all Race records from the scan result.
    /// </summary>
    public List<ReconstructedRace> ReconstructRaces()
    {
        var races = new List<ReconstructedRace>();
        var raceRecords = GetRecordsByType("RACE").ToList();

        if (_accessor == null)
        {
            foreach (var record in raceRecords)
            {
                races.Add(new ReconstructedRace
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
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
                    var race = ReconstructRaceFromAccessor(record, buffer);
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

    #region Private Reconstruction Methods

    private ReconstructedNpc? ReconstructNpcFromScanResult(DetectedMainRecord record)
    {
        // Find matching subrecords from scan result
        var editorId = GetEditorId(record.FormId);
        var fullName = FindFullNameNear(record.Offset);
        var stats = FindActorBaseNear(record.Offset);

        return new ReconstructedNpc
        {
            FormId = record.FormId,
            EditorId = editorId,
            FullName = fullName,
            Stats = stats,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedNpc? ReconstructNpcFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructNpcFromScanResult(record);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        ActorBaseSubrecord? stats = null;
        uint? race = null;
        uint? script = null;
        uint? classFormId = null;
        uint? deathItem = null;
        uint? voiceType = null;
        uint? template = null;
        var factions = new List<FactionMembership>();
        var spells = new List<uint>();
        var inventory = new List<InventoryItem>();
        var packages = new List<uint>();

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "ACBS" when sub.DataLength == 24:
                    stats = ParseActorBase(subData, record.Offset + 24 + sub.DataOffset, record.IsBigEndian);
                    break;
                case "RNAM" when sub.DataLength == 4:
                    race = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SCRI" when sub.DataLength == 4:
                    script = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CNAM" when sub.DataLength == 4:
                    classFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "INAM" when sub.DataLength == 4:
                    deathItem = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "VTCK" when sub.DataLength == 4:
                    voiceType = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TPLT" when sub.DataLength == 4:
                    template = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength >= 5:
                    var factionFormId = ReadFormId(subData[..4], record.IsBigEndian);
                    var rank = (sbyte)subData[4];
                    factions.Add(new FactionMembership(factionFormId, rank));
                    break;
                case "SPLO" when sub.DataLength == 4:
                    spells.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
                case "CNTO" when sub.DataLength >= 8:
                    var itemFormId = ReadFormId(subData[..4], record.IsBigEndian);
                    var count = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    inventory.Add(new InventoryItem(itemFormId, count));
                    break;
                case "PKID" when sub.DataLength == 4:
                    packages.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        return new ReconstructedNpc
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Stats = stats,
            Race = race,
            Script = script,
            Class = classFormId,
            DeathItem = deathItem,
            VoiceType = voiceType,
            Template = template,
            Factions = factions,
            Spells = spells,
            Inventory = inventory,
            Packages = packages,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedQuest? ReconstructQuestFromScanResult(DetectedMainRecord record)
    {
        return new ReconstructedQuest
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedQuest? ReconstructQuestFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructQuestFromScanResult(record);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        byte flags = 0;
        byte priority = 0;
        uint? script = null;
        var stages = new List<QuestStage>();
        var objectives = new List<QuestObjective>();

        // Track current stage/objective being built
        int? currentStageIndex = null;
        string? currentLogEntry = null;
        byte currentStageFlags = 0;
        int? currentObjectiveIndex = null;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 2:
                    flags = subData[0];
                    priority = subData[1];
                    break;
                case "SCRI" when sub.DataLength == 4:
                    script = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "INDX" when sub.DataLength >= 2:
                    // Save previous stage if any
                    if (currentStageIndex.HasValue)
                    {
                        stages.Add(new QuestStage
                        {
                            Index = currentStageIndex.Value,
                            LogEntry = currentLogEntry,
                            Flags = currentStageFlags
                        });
                    }
                    // Start new stage
                    currentStageIndex = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData)
                        : BinaryPrimitives.ReadInt16LittleEndian(subData);
                    currentLogEntry = null;
                    currentStageFlags = 0;
                    break;
                case "CNAM": // Log entry text
                    currentLogEntry = ReadNullTermString(subData);
                    break;
                case "QSDT" when sub.DataLength >= 1:
                    currentStageFlags = subData[0];
                    break;
                case "QOBJ" when sub.DataLength >= 4:
                    // Save previous objective if any
                    if (currentObjectiveIndex.HasValue)
                    {
                        objectives.Add(new QuestObjective
                        {
                            Index = currentObjectiveIndex.Value
                        });
                    }
                    currentObjectiveIndex = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    break;
                case "NNAM": // Objective display text
                    if (currentObjectiveIndex.HasValue)
                    {
                        objectives.Add(new QuestObjective
                        {
                            Index = currentObjectiveIndex.Value,
                            DisplayText = ReadNullTermString(subData)
                        });
                        currentObjectiveIndex = null;
                    }
                    break;
            }
        }

        // Add final stage if any
        if (currentStageIndex.HasValue)
        {
            stages.Add(new QuestStage
            {
                Index = currentStageIndex.Value,
                LogEntry = currentLogEntry,
                Flags = currentStageFlags
            });
        }

        // Add final objective if any
        if (currentObjectiveIndex.HasValue)
        {
            objectives.Add(new QuestObjective
            {
                Index = currentObjectiveIndex.Value
            });
        }

        return new ReconstructedQuest
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Flags = flags,
            Priority = priority,
            Script = script,
            Stages = stages.OrderBy(s => s.Index).ToList(),
            Objectives = objectives.OrderBy(o => o.Index).ToList(),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedDialogue? ReconstructDialogueFromScanResult(DetectedMainRecord record)
    {
        // Find matching response texts and data near this INFO record
        var responseTexts = _scanResult.ResponseTexts
            .Where(r => Math.Abs(r.Offset - record.Offset) < record.DataSize + 100)
            .ToList();

        var responses = responseTexts.Select(rt => new DialogueResponse
        {
            Text = rt.Text
        }).ToList();

        return new ReconstructedDialogue
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            Responses = responses,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedDialogue? ReconstructDialogueFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructDialogueFromScanResult(record);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        uint? topicFormId = null;
        uint? questFormId = null;
        uint? speakerFormId = null;
        uint? previousInfo = null;
        var responses = new List<DialogueResponse>();

        // Track current response being built
        string? currentResponseText = null;
        uint currentEmotionType = 0;
        int currentEmotionValue = 0;
        byte currentResponseNumber = 0;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "QSTI" when sub.DataLength == 4:
                    questFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM1":
                    // Save previous response if any
                    if (currentResponseText != null)
                    {
                        responses.Add(new DialogueResponse
                        {
                            Text = currentResponseText,
                            EmotionType = currentEmotionType,
                            EmotionValue = currentEmotionValue,
                            ResponseNumber = currentResponseNumber
                        });
                    }
                    currentResponseText = ReadNullTermString(subData);
                    currentEmotionType = 0;
                    currentEmotionValue = 0;
                    break;
                case "TRDT" when sub.DataLength >= 20:
                    currentEmotionType = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    currentEmotionValue = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    currentResponseNumber = subData[12];
                    break;
                case "PNAM" when sub.DataLength == 4:
                    previousInfo = ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        // Add final response if any
        if (currentResponseText != null)
        {
            responses.Add(new DialogueResponse
            {
                Text = currentResponseText,
                EmotionType = currentEmotionType,
                EmotionValue = currentEmotionValue,
                ResponseNumber = currentResponseNumber
            });
        }

        return new ReconstructedDialogue
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            TopicFormId = topicFormId,
            QuestFormId = questFormId,
            SpeakerFormId = speakerFormId,
            Responses = responses,
            PreviousInfo = previousInfo,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedNote? ReconstructNoteFromScanResult(DetectedMainRecord record)
    {
        return new ReconstructedNote
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            FullName = FindFullNameNear(record.Offset),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedNote? ReconstructNoteFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructNoteFromScanResult(record);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? text = null;
        byte noteType = 0;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 1:
                    noteType = subData[0];
                    break;
                case "TNAM":
                    text = ReadNullTermString(subData);
                    break;
                case "DESC": // Fallback for text content
                    if (string.IsNullOrEmpty(text))
                    {
                        text = ReadNullTermString(subData);
                    }
                    break;
            }
        }

        return new ReconstructedNote
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            NoteType = noteType,
            Text = text,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedCell? ReconstructCellFromScanResult(DetectedMainRecord record, List<ExtractedRefrRecord> refrRecords)
    {
        // Find XCLC near this CELL record
        var cellGrid = _scanResult.CellGrids
            .FirstOrDefault(g => Math.Abs(g.Offset - record.Offset) < 200);

        // Find nearby REFRs
        var nearbyRefs = refrRecords
            .Where(r => r.Header.Offset > record.Offset && r.Header.Offset < record.Offset + 100000)
            .Take(100)
            .Select(r => new PlacedReference
            {
                FormId = r.Header.FormId,
                BaseFormId = r.BaseFormId,
                BaseEditorId = r.BaseEditorId ?? GetEditorId(r.BaseFormId),
                RecordType = r.Header.RecordType,
                X = r.Position?.X ?? 0,
                Y = r.Position?.Y ?? 0,
                Z = r.Position?.Z ?? 0,
                RotX = r.Position?.RotX ?? 0,
                RotY = r.Position?.RotY ?? 0,
                RotZ = r.Position?.RotZ ?? 0,
                Scale = r.Scale,
                OwnerFormId = r.OwnerFormId,
                Offset = r.Header.Offset,
                IsBigEndian = r.Header.IsBigEndian
            })
            .ToList();

        return new ReconstructedCell
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            GridX = cellGrid?.GridX,
            GridY = cellGrid?.GridY,
            PlacedObjects = nearbyRefs,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedCell? ReconstructCellFromAccessor(DetectedMainRecord record, List<ExtractedRefrRecord> refrRecords, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructCellFromScanResult(record, refrRecords);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        int? gridX = null;
        int? gridY = null;
        byte flags = 0;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 1:
                    flags = subData[0];
                    break;
                case "XCLC" when sub.DataLength >= 8:
                    gridX = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    gridY = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    break;
            }
        }

        // Find nearby REFRs
        var nearbyRefs = refrRecords
            .Where(r => r.Header.Offset > record.Offset && r.Header.Offset < record.Offset + 100000)
            .Take(100)
            .Select(r => new PlacedReference
            {
                FormId = r.Header.FormId,
                BaseFormId = r.BaseFormId,
                BaseEditorId = r.BaseEditorId ?? GetEditorId(r.BaseFormId),
                RecordType = r.Header.RecordType,
                X = r.Position?.X ?? 0,
                Y = r.Position?.Y ?? 0,
                Z = r.Position?.Z ?? 0,
                RotX = r.Position?.RotX ?? 0,
                RotY = r.Position?.RotY ?? 0,
                RotZ = r.Position?.RotZ ?? 0,
                Scale = r.Scale,
                OwnerFormId = r.OwnerFormId,
                Offset = r.Header.Offset,
                IsBigEndian = r.Header.IsBigEndian
            })
            .ToList();

        // Find associated heightmap
        var heightmap = _scanResult.LandRecords
            .FirstOrDefault(l => l.CellX == gridX && l.CellY == gridY)?.Heightmap;

        return new ReconstructedCell
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            GridX = gridX,
            GridY = gridY,
            Flags = flags,
            PlacedObjects = nearbyRefs,
            Heightmap = heightmap,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedWorldspace? ReconstructWorldspaceFromScanResult(DetectedMainRecord record)
    {
        return new ReconstructedWorldspace
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            FullName = FindFullNameNear(record.Offset),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedWorldspace? ReconstructWorldspaceFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructWorldspaceFromScanResult(record);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        uint? parentWorldspace = null;
        uint? climate = null;
        uint? water = null;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "WNAM" when sub.DataLength == 4:
                    parentWorldspace = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CNAM" when sub.DataLength == 4:
                    climate = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM2" when sub.DataLength == 4:
                    water = ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        return new ReconstructedWorldspace
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ParentWorldspaceFormId = parentWorldspace,
            ClimateFormId = climate,
            WaterFormId = water,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedWeapon? ReconstructWeaponFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedWeapon
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;

        // DATA subrecord (15 bytes)
        int value = 0;
        int health = 0;
        float weight = 0;
        short damage = 0;
        byte clipSize = 0;

        // DNAM subrecord (204 bytes)
        WeaponType weaponType = 0;
        uint animationType = 0;
        float speed = 1.0f;
        float reach = 0;
        byte ammoPerShot = 1;
        float minSpread = 0;
        float spread = 0;
        float drift = 0;
        uint? ammoFormId = null;
        uint? projectileFormId = null;
        byte vatsToHitChance = 0;
        byte numProjectiles = 1;
        float minRange = 0;
        float maxRange = 0;
        float shotsPerSec = 1;
        float actionPoints = 0;
        uint strengthRequirement = 0;
        uint skillRequirement = 0;

        // CRDT subrecord
        short criticalDamage = 0;
        float criticalChance = 1.0f;
        uint? criticalEffectFormId = null;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = ReadNullTermString(subData);
                    break;
                case "ENAM" when sub.DataLength == 4:
                    ammoFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength >= 15:
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    health = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    weight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    damage = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData[12..])
                        : BinaryPrimitives.ReadInt16LittleEndian(subData[12..]);
                    clipSize = subData[14];
                    break;
                case "DNAM" when sub.DataLength >= 64:
                    // Parse key DNAM fields
                    animationType = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    speed = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    reach = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    weaponType = (WeaponType)subData[44];
                    minSpread = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[20..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[20..]);
                    spread = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[24..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[24..]);
                    if (sub.DataLength >= 100)
                    {
                        shotsPerSec = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[64..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[64..]);
                        minRange = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[68..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[68..]);
                        maxRange = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[72..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[72..]);
                    }
                    break;
                case "CRDT" when sub.DataLength >= 12:
                    criticalDamage = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData)
                        : BinaryPrimitives.ReadInt16LittleEndian(subData);
                    criticalChance = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    criticalEffectFormId = ReadFormId(subData[8..], record.IsBigEndian);
                    break;
            }
        }

        return new ReconstructedWeapon
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Value = value,
            Health = health,
            Weight = weight,
            Damage = damage,
            ClipSize = clipSize,
            WeaponType = weaponType,
            AnimationType = animationType,
            Speed = speed,
            Reach = reach,
            AmmoPerShot = ammoPerShot,
            MinSpread = minSpread,
            Spread = spread,
            Drift = drift,
            AmmoFormId = ammoFormId,
            ProjectileFormId = projectileFormId,
            VatsToHitChance = vatsToHitChance,
            NumProjectiles = numProjectiles,
            MinRange = minRange,
            MaxRange = maxRange,
            ShotsPerSec = shotsPerSec,
            ActionPoints = actionPoints,
            StrengthRequirement = strengthRequirement,
            SkillRequirement = skillRequirement,
            CriticalDamage = criticalDamage,
            CriticalChance = criticalChance,
            CriticalEffectFormId = criticalEffectFormId,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedArmor? ReconstructArmorFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedArmor
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        int value = 0;
        int health = 0;
        float weight = 0;
        int armorRating = 0;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 12:
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    health = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    weight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    break;
                case "DNAM" when sub.DataLength >= 4:
                    armorRating = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    break;
            }
        }

        return new ReconstructedArmor
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Value = value,
            Health = health,
            Weight = weight,
            ArmorRating = armorRating,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedAmmo? ReconstructAmmoFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedAmmo
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        float speed = 0;
        byte flags = 0;
        uint value = 0;
        byte clipRounds = 0;
        uint? projectileFormId = null;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 13:
                    speed = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    flags = subData[4];
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[8..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[8..]);
                    clipRounds = subData[12];
                    break;
            }
        }

        return new ReconstructedAmmo
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Speed = speed,
            Flags = flags,
            Value = value,
            ClipRounds = clipRounds,
            ProjectileFormId = projectileFormId,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedConsumable? ReconstructConsumableFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedConsumable
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        float weight = 0;
        uint value = 0;
        uint? addictionFormId = null;
        float addictionChance = 0;
        var effectFormIds = new List<uint>();

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 4:
                    weight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    break;
                case "ENIT" when sub.DataLength >= 20:
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    addictionFormId = ReadFormId(subData[12..], record.IsBigEndian);
                    addictionChance = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[16..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[16..]);
                    break;
                case "EFID" when sub.DataLength == 4:
                    effectFormIds.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        return new ReconstructedConsumable
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Weight = weight,
            Value = value,
            AddictionFormId = addictionFormId != 0 ? addictionFormId : null,
            AddictionChance = addictionChance,
            EffectFormIds = effectFormIds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedMiscItem? ReconstructMiscItemFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedMiscItem
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        int value = 0;
        float weight = 0;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 8:
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    weight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    break;
            }
        }

        return new ReconstructedMiscItem
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Value = value,
            Weight = weight,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedPerk? ReconstructPerkFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedPerk
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? description = null;
        string? iconPath = null;
        byte trait = 0;
        byte minLevel = 0;
        byte ranks = 1;
        byte playable = 1;
        var entries = new List<PerkEntry>();

        // Track current entry being built
        byte currentEntryType = 0;
        byte currentEntryRank = 0;
        byte currentEntryPriority = 0;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "DESC":
                    description = ReadNullTermString(subData);
                    break;
                case "ICON":
                case "MICO":
                    iconPath = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 5:
                    trait = subData[0];
                    minLevel = subData[1];
                    ranks = subData[2];
                    playable = subData[3];
                    break;
                case "PRKE" when sub.DataLength >= 3:
                    // Start new perk entry
                    currentEntryType = subData[0];
                    currentEntryRank = subData[1];
                    currentEntryPriority = subData[2];
                    break;
                case "EPFT" when sub.DataLength >= 1:
                    // Entry point function type - finalize entry
                    entries.Add(new PerkEntry
                    {
                        Type = currentEntryType,
                        Rank = currentEntryRank,
                        Priority = currentEntryPriority
                    });
                    break;
            }
        }

        return new ReconstructedPerk
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Description = description,
            IconPath = iconPath,
            Trait = trait,
            MinLevel = minLevel,
            Ranks = ranks,
            Playable = playable,
            Entries = entries,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedSpell? ReconstructSpellFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedSpell
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        SpellType type = 0;
        uint cost = 0;
        uint level = 0;
        byte flags = 0;
        var effectFormIds = new List<uint>();

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "SPIT" when sub.DataLength >= 16:
                    type = (SpellType)(record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData));
                    cost = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]);
                    level = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[8..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[8..]);
                    flags = subData[12];
                    break;
                case "EFID" when sub.DataLength == 4:
                    effectFormIds.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        return new ReconstructedSpell
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Type = type,
            Cost = cost,
            Level = level,
            Flags = flags,
            EffectFormIds = effectFormIds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedRace? ReconstructRaceFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedRace
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? description = null;

        // S.P.E.C.I.A.L. modifiers
        sbyte strength = 0, perception = 0, endurance = 0, charisma = 0;
        sbyte intelligence = 0, agility = 0, luck = 0;

        // Heights
        float maleHeight = 1.0f;
        float femaleHeight = 1.0f;

        // Related FormIDs
        uint? olderRace = null;
        uint? youngerRace = null;
        uint? maleVoice = null;
        uint? femaleVoice = null;
        var abilityFormIds = new List<uint>();

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "DESC":
                    description = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 36:
                    // S.P.E.C.I.A.L. bonuses at offsets 0-6
                    strength = (sbyte)subData[0];
                    perception = (sbyte)subData[1];
                    endurance = (sbyte)subData[2];
                    charisma = (sbyte)subData[3];
                    intelligence = (sbyte)subData[4];
                    agility = (sbyte)subData[5];
                    luck = (sbyte)subData[6];
                    // Heights at offsets 20-27
                    maleHeight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[20..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[20..]);
                    femaleHeight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[24..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[24..]);
                    break;
                case "ONAM" when sub.DataLength == 4:
                    olderRace = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "YNAM" when sub.DataLength == 4:
                    youngerRace = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "VTCK" when sub.DataLength >= 8:
                    maleVoice = ReadFormId(subData[..4], record.IsBigEndian);
                    femaleVoice = ReadFormId(subData[4..], record.IsBigEndian);
                    break;
                case "SPLO" when sub.DataLength == 4:
                    abilityFormIds.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        return new ReconstructedRace
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Description = description,
            Strength = strength,
            Perception = perception,
            Endurance = endurance,
            Charisma = charisma,
            Intelligence = intelligence,
            Agility = agility,
            Luck = luck,
            MaleHeight = maleHeight,
            FemaleHeight = femaleHeight,
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

    #region Helper Methods

    private static Dictionary<uint, string> BuildFormIdToEditorIdMap(EsmRecordScanResult scanResult)
    {
        var map = new Dictionary<uint, string>();

        // Correlate EDID subrecords to nearby main record headers
        foreach (var edid in scanResult.EditorIds)
        {
            // Find the closest main record header before this EDID
            var nearestRecord = scanResult.MainRecords
                .Where(r => r.Offset < edid.Offset && edid.Offset < r.Offset + r.DataSize + 24)
                .OrderByDescending(r => r.Offset)
                .FirstOrDefault();

            if (nearestRecord != null && !map.ContainsKey(nearestRecord.FormId))
            {
                map[nearestRecord.FormId] = edid.Name;
            }
        }

        return map;
    }

    private string? FindFullNameNear(long recordOffset)
    {
        return _scanResult.FullNames
            .Where(f => Math.Abs(f.Offset - recordOffset) < 500)
            .OrderBy(f => Math.Abs(f.Offset - recordOffset))
            .FirstOrDefault()?.Text;
    }

    private ActorBaseSubrecord? FindActorBaseNear(long recordOffset)
    {
        return _scanResult.ActorBases
            .Where(a => Math.Abs(a.Offset - recordOffset) < 500)
            .OrderBy(a => Math.Abs(a.Offset - recordOffset))
            .FirstOrDefault();
    }

    private static ActorBaseSubrecord? ParseActorBase(ReadOnlySpan<byte> data, long offset, bool bigEndian)
    {
        if (data.Length < 24)
        {
            return null;
        }

        uint flags;
        ushort fatigueBase, barterGold, calcMin, calcMax, speedMultiplier, templateFlags;
        short level, dispositionBase;
        float karmaAlignment;

        if (bigEndian)
        {
            flags = BinaryPrimitives.ReadUInt32BigEndian(data);
            fatigueBase = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
            barterGold = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
            level = BinaryPrimitives.ReadInt16BigEndian(data[8..]);
            calcMin = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
            calcMax = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
            speedMultiplier = BinaryPrimitives.ReadUInt16BigEndian(data[14..]);
            karmaAlignment = BinaryPrimitives.ReadSingleBigEndian(data[16..]);
            dispositionBase = BinaryPrimitives.ReadInt16BigEndian(data[20..]);
            templateFlags = BinaryPrimitives.ReadUInt16BigEndian(data[22..]);
        }
        else
        {
            flags = BinaryPrimitives.ReadUInt32LittleEndian(data);
            fatigueBase = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
            barterGold = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            level = BinaryPrimitives.ReadInt16LittleEndian(data[8..]);
            calcMin = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
            calcMax = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
            speedMultiplier = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
            karmaAlignment = BinaryPrimitives.ReadSingleLittleEndian(data[16..]);
            dispositionBase = BinaryPrimitives.ReadInt16LittleEndian(data[20..]);
            templateFlags = BinaryPrimitives.ReadUInt16LittleEndian(data[22..]);
        }

        return new ActorBaseSubrecord(
            flags, fatigueBase, barterGold, level, calcMin, calcMax,
            speedMultiplier, karmaAlignment, dispositionBase, templateFlags,
            offset, bigEndian);
    }

    private static uint ReadFormId(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 4)
        {
            return 0;
        }

        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    private static string ReadNullTermString(ReadOnlySpan<byte> data)
    {
        var length = data.IndexOf((byte)0);
        if (length < 0)
        {
            length = data.Length;
        }

        return Encoding.UTF8.GetString(data[..length]);
    }

    private readonly record struct ParsedSubrecordInfo(string Signature, int DataOffset, int DataLength);

    private static IEnumerable<ParsedSubrecordInfo> IterateSubrecords(byte[] data, int dataSize, bool bigEndian)
    {
        var offset = 0;

        while (offset + 6 <= dataSize)
        {
            // Read subrecord signature (4 bytes)
            var sig = bigEndian
                ? new string([(char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset]])
                : Encoding.ASCII.GetString(data, offset, 4);

            // Read subrecord size (2 bytes)
            var subSize = bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));

            if (offset + 6 + subSize > dataSize)
            {
                yield break;
            }

            yield return new ParsedSubrecordInfo(sig, offset + 6, subSize);

            offset += 6 + subSize;
        }
    }

    #endregion
}
