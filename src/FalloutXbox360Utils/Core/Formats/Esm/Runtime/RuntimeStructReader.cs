using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     Reader for runtime game structures in Xbox 360 memory dumps.
///     Facade that delegates to domain-specific reader classes.
/// </summary>
public sealed class RuntimeStructReader
{
    private readonly RuntimeActorReader _actors;
    private readonly RuntimeActorWeaponReader _actorWeapons;
    private readonly RuntimeAudioLocationControllerReader _audioLocationControllers;
    private readonly RuntimeCharacterAppearanceReader _appearance;
    private readonly RuntimeBookReader _books;
    private readonly RuntimeCameraPathReader _cameraPaths;
    private readonly RuntimeCaravanCardReader _caravanCards;
    private readonly RuntimeCaravanDeckReader _caravanDecks;
    private readonly RuntimeCellReader _cells;
    private readonly RuntimeChallengeReader _challenges;
    private readonly RuntimeClassReader _classes;
    private readonly RuntimeCollectionReader _collections;
    private readonly RuntimeConstructibleObjectReader _constructibleObjects;
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimeDebrisReader _debris;
    private readonly RuntimeDialogueReader _dialogue;
    private readonly RuntimeEffectReader _effects;
    private readonly RuntimeEncounterZoneReader _encounterZones;
    private readonly RuntimeExplosionReader _explosions;
    private readonly RuntimeGenericReader _generic;
    private readonly RuntimeGlobalReader _globals;
    private readonly RuntimeHeadPartReader _headParts;
    private readonly RuntimeIdleAnimationReader _idleAnimations;
    private readonly RuntimeImpactDataReader _impactData;
    private readonly RuntimeIngredientReader _ingredients;
    private readonly RuntimeNavMeshInfoMapReader _navMeshInfoMaps;
    private readonly RuntimeSurvivalStageReader _survivalStages;
    private readonly RuntimeItemReader _items;
    private readonly RuntimeLightingTemplateReader _lightingTemplates;
    private readonly RuntimeLoadScreenTypeReader _loadScreenTypes;
    private readonly RuntimeMagicReader _magic;
    private readonly RuntimeMenuIconReader _menuIcons;
    private readonly RuntimeMessageReader _messages;
    private readonly RuntimeMusicTypeReader _musicTypes;
    private readonly RuntimeNavMeshReader _navMeshes;
    private readonly RuntimePackageReader _packages;
    private readonly RuntimeRaceReader _races;
    private readonly RuntimeRecipeCategoryReader _recipeCategories;
    private readonly RuntimeRegionReader _regions;
    private readonly RuntimeRecipeReader _recipes;
    private readonly RuntimeVoiceTypeReader _voiceTypes;
    private readonly RuntimeRefrReader _refrs;
    private readonly RuntimeReputationReader _reputations;
    private readonly RuntimeScriptReader _scripts;
    private readonly RuntimeSoundReader _sounds;
    private readonly RuntimeWaterReader _water;
    private readonly RuntimeWeaponModReader _weaponMods;
    private readonly RuntimeWorldReader _world;
    private readonly RuntimeWorldObjectReader _worldObjects;

    public RuntimeStructReader(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        bool useProtoOffsets = false)
        : this(new MmfMemoryAccessor(accessor), fileSize, minidumpInfo, useProtoOffsets, null)
    {
    }

    internal RuntimeStructReader(
        IMemoryAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        bool useProtoOffsets,
        RuntimeNpcLayoutProbeResult? npcLayoutProbe,
        RuntimeWorldCellLayoutProbeResult? worldCellLayoutProbe = null,
        RuntimeProbeResults? probeResults = null)
    {
        IsEarlyBuild = useProtoOffsets;
        WorldCellLayoutProbe = worldCellLayoutProbe;
        _context = new RuntimeMemoryContext(accessor, fileSize, minidumpInfo);
        _actors = new RuntimeActorReader(_context, npcLayoutProbe);
        _generic = new RuntimeGenericReader(_context, probeResults?.GenericTypeShifts);
        _items = new RuntimeItemReader(_context, probeResults?.WeaponSoundLayout, probeResults?.WeaponCritLayout);
        _dialogue = new RuntimeDialogueReader(_context, probeResults?.TerminalLayout);
        _effects = new RuntimeEffectReader(_context, probeResults?.EffectLayout);
        _scripts = new RuntimeScriptReader(_context);
        _world = new RuntimeWorldReader(_context);
        _refrs = new RuntimeRefrReader(_context, useProtoOffsets);
        _packages = new RuntimePackageReader(_context);
        _actorWeapons = new RuntimeActorWeaponReader(_context);
        _cells = new RuntimeCellReader(_context, useProtoOffsets, worldCellLayoutProbe);
        _collections = new RuntimeCollectionReader(_context);
        _worldObjects = new RuntimeWorldObjectReader(_context);
        _races = new RuntimeRaceReader(_context, probeResults?.RaceLayout);
        _magic = new RuntimeMagicReader(_context, probeResults?.MagicLayout);
        _globals = new RuntimeGlobalReader(_context);
        _classes = new RuntimeClassReader(_context);
        _appearance = new RuntimeCharacterAppearanceReader(_context);
        _reputations = new RuntimeReputationReader(_context);
        _musicTypes = new RuntimeMusicTypeReader(_context);
        _sounds = new RuntimeSoundReader(_context);
        _books = new RuntimeBookReader(_context, probeResults?.BookLayout);
        _weaponMods = new RuntimeWeaponModReader(_context);
        _recipes = new RuntimeRecipeReader(_context);
        _challenges = new RuntimeChallengeReader(_context);
        _explosions = new RuntimeExplosionReader(_context);
        _messages = new RuntimeMessageReader(_context);
        _encounterZones = new RuntimeEncounterZoneReader(_context);
        _navMeshes = new RuntimeNavMeshReader(_context);
        _lightingTemplates = new RuntimeLightingTemplateReader(_context);
        _recipeCategories = new RuntimeRecipeCategoryReader(_context);
        _constructibleObjects = new RuntimeConstructibleObjectReader(_context);
        _water = new RuntimeWaterReader(_context);
        _headParts = new RuntimeHeadPartReader(_context);
        _voiceTypes = new RuntimeVoiceTypeReader(_context);
        _menuIcons = new RuntimeMenuIconReader(_context);
        _loadScreenTypes = new RuntimeLoadScreenTypeReader(_context);
        _idleAnimations = new RuntimeIdleAnimationReader(_context);
        _cameraPaths = new RuntimeCameraPathReader(_context);
        _impactData = new RuntimeImpactDataReader(_context);
        _audioLocationControllers = new RuntimeAudioLocationControllerReader(_context);
        _regions = new RuntimeRegionReader(_context);
        _caravanCards = new RuntimeCaravanCardReader(_context);
        _debris = new RuntimeDebrisReader(_context);
        _ingredients = new RuntimeIngredientReader(_context);
        _navMeshInfoMaps = new RuntimeNavMeshInfoMapReader(_context);
        _caravanDecks = new RuntimeCaravanDeckReader(_context);
        _survivalStages = new RuntimeSurvivalStageReader(_context);
    }

    public bool IsEarlyBuild { get; }
    internal RuntimeWorldCellLayoutProbeResult? WorldCellLayoutProbe { get; }

    /// <summary>
    ///     Factory that probes the DMP memory to auto-detect early vs final build layout.
    ///     Samples REFR entries and tries both offset layouts; the one producing more valid
    ///     reads wins. Falls back to final layout if no REFR entries are available.
    /// </summary>
    public static RuntimeStructReader CreateWithAutoDetect(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        IReadOnlyList<RuntimeEditorIdEntry> refrEntries,
        IReadOnlyList<RuntimeEditorIdEntry>? npcEntries = null,
        IReadOnlyList<RuntimeEditorIdEntry>? worldEntries = null,
        IReadOnlyList<RuntimeEditorIdEntry>? cellEntries = null,
        IReadOnlyList<RuntimeEditorIdEntry>? allEntries = null)
    {
        return CreateWithAutoDetect(new MmfMemoryAccessor(accessor), fileSize, minidumpInfo,
            refrEntries, npcEntries, worldEntries, cellEntries, allEntries);
    }

    public static RuntimeStructReader CreateWithAutoDetect(
        IMemoryAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        IReadOnlyList<RuntimeEditorIdEntry> refrEntries,
        IReadOnlyList<RuntimeEditorIdEntry>? npcEntries = null,
        IReadOnlyList<RuntimeEditorIdEntry>? worldEntries = null,
        IReadOnlyList<RuntimeEditorIdEntry>? cellEntries = null,
        IReadOnlyList<RuntimeEditorIdEntry>? allEntries = null)
    {
        var context = new RuntimeMemoryContext(accessor, fileSize, minidumpInfo);
        var isEarlyBuild = RuntimeRefrReader.ProbeIsEarlyBuild(context, refrEntries);
        var npcLayoutProbe = npcEntries is { Count: > 0 }
            ? RuntimeNpcLayoutProbe.Probe(context, npcEntries)
            : null;
        var worldCellLayoutProbe =
            worldEntries is { Count: > 0 } || cellEntries is { Count: > 0 }
                ? RuntimeWorldCellLayoutProbe.Probe(context, worldEntries, cellEntries)
                : null;

        // Run field probes for specialized readers using allEntries
        RuntimeProbeResults? probeResults = null;
        if (allEntries is { Count: > 0 })
        {
            // Build a FormID → entry lookup once for content-based positional validation
            // (used by the weapon sound probe to disambiguate adjacent SOUN pointers).
            var editorIdsByFormId = new Dictionary<uint, RuntimeEditorIdEntry>(allEntries.Count);
            foreach (var entry in allEntries)
            {
                if (entry.FormId != 0)
                {
                    editorIdsByFormId[entry.FormId] = entry;
                }
            }

            probeResults = new RuntimeProbeResults
            {
                NpcLayout = npcLayoutProbe,
                WorldCellLayout = worldCellLayoutProbe,
                BookLayout = RuntimeBookProbe.Probe(context, allEntries),
                RaceLayout = RuntimeRaceProbe.Probe(context, allEntries),
                EffectLayout = RuntimeEffectProbe.Probe(context, allEntries),
                MagicLayout = RuntimeMagicProbe.Probe(context, allEntries),
                WeaponSoundLayout = RuntimeWeaponSoundProbe.Probe(context, allEntries,
                    msg => Logger.Instance.Info(msg),
                    editorIdsByFormId),
                WeaponCritLayout = RuntimeWeaponCritProbe.Probe(context, allEntries,
                    msg => Logger.Instance.Info(msg),
                    editorIdsByFormId),
                TerminalLayout = RuntimeTerminalLayoutProbe.Probe(context, allEntries,
                    msg => Logger.Instance.Info(msg),
                    editorIdsByFormId),
                GenericTypeShifts = RuntimeGenericReader.ProbeAllTypeShifts(context, allEntries)
            };
        }

        return new RuntimeStructReader(
            accessor,
            fileSize,
            minidumpInfo,
            isEarlyBuild,
            npcLayoutProbe,
            worldCellLayoutProbe,
            probeResults);
    }

    #region Scripts

    public RuntimeScriptData? ReadRuntimeScript(RuntimeEditorIdEntry entry)
    {
        return _scripts.ReadRuntimeScript(entry);
    }

    #endregion

    #region AI Packages

    public PackageRecord? ReadRuntimePackage(RuntimeEditorIdEntry entry)
    {
        return _packages.ReadRuntimePackage(entry);
    }

    #endregion

    #region Strings (shared utility)

    public string? ReadBsStringT(long tesFormFileOffset, int fieldOffset)
    {
        return _context.ReadBsStringT(tesFormFileOffset, fieldOffset);
    }

    #endregion

    #region Generic (PDB-derived)

    public GenericEsmRecord? ReadGenericRecord(RuntimeEditorIdEntry entry)
    {
        return _generic.ReadGenericRecord(entry);
    }

    #endregion

    #region Globals

    public GlobalRecord? ReadRuntimeGlobal(RuntimeEditorIdEntry entry)
    {
        return _globals.ReadRuntimeGlobal(entry);
    }

    #endregion

    #region Classes

    public ClassRecord? ReadRuntimeClass(RuntimeEditorIdEntry entry)
    {
        return _classes.ReadRuntimeClass(entry);
    }

    #endregion

    #region Reputations

    public ReputationRecord? ReadRuntimeReputation(RuntimeEditorIdEntry entry)
    {
        return _reputations.ReadRuntimeReputation(entry);
    }

    #endregion

    #region Effects

    public ProjectilePhysicsData? ReadProjectilePhysics(long fileOffset, uint expectedFormId)
    {
        return _effects.ReadProjectilePhysics(fileOffset, expectedFormId);
    }

    public ProjectileRecord? ReadRuntimeProjectile(RuntimeEditorIdEntry entry)
    {
        return _effects.ReadRuntimeProjectile(entry);
    }

    #endregion

    #region Sounds

    public MusicTypeRecord? ReadRuntimeMusicType(RuntimeEditorIdEntry entry)
    {
        return _musicTypes.ReadRuntimeMusicType(entry);
    }

    public SoundRecord? ReadRuntimeSound(RuntimeEditorIdEntry entry)
    {
        return _sounds.ReadRuntimeSound(entry);
    }

    #endregion

    #region Actors

    public NpcRecord? ReadRuntimeNpc(RuntimeEditorIdEntry entry)
    {
        return _actors.ReadRuntimeNpc(entry);
    }

    internal RuntimeActorWeaponReader.RuntimeActorWeaponState? ReadRuntimeActorWeaponState(RuntimeEditorIdEntry entry)
    {
        return _actorWeapons.ReadRuntimeActorWeaponState(entry);
    }

    public CreatureRecord? ReadRuntimeCreature(RuntimeEditorIdEntry entry)
    {
        return _actors.ReadRuntimeCreature(entry);
    }

    public FactionRecord? ReadRuntimeFaction(RuntimeEditorIdEntry entry)
    {
        return _actors.ReadRuntimeFaction(entry);
    }

    public ActorValueInfoRecord? ReadRuntimeAvif(RuntimeEditorIdEntry entry)
    {
        return _actors.ReadRuntimeAvif(entry);
    }

    public RaceRecord? ReadRuntimeRace(RuntimeEditorIdEntry entry)
    {
        return _races.ReadRuntimeRace(entry);
    }

    #endregion

    #region Magic / Effects

    public BaseEffectRecord? ReadRuntimeBaseEffect(RuntimeEditorIdEntry entry)
    {
        return _magic.ReadRuntimeBaseEffect(entry);
    }

    public SpellRecord? ReadRuntimeSpell(RuntimeEditorIdEntry entry)
    {
        return _magic.ReadRuntimeSpell(entry);
    }

    public EnchantmentRecord? ReadRuntimeEnchantment(RuntimeEditorIdEntry entry)
    {
        return _magic.ReadRuntimeEnchantment(entry);
    }

    public PerkRecord? ReadRuntimePerk(RuntimeEditorIdEntry entry)
    {
        return _magic.ReadRuntimePerk(entry);
    }

    #endregion

    #region Character Appearance

    public EyesRecord? ReadRuntimeEyes(RuntimeEditorIdEntry entry)
    {
        return _appearance.ReadRuntimeEyes(entry);
    }

    public HairRecord? ReadRuntimeHair(RuntimeEditorIdEntry entry)
    {
        return _appearance.ReadRuntimeHair(entry);
    }

    #endregion

    #region Items

    public WeaponRecord? ReadRuntimeWeapon(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeWeapon(entry);
    }

    public ArmorRecord? ReadRuntimeArmor(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeArmor(entry);
    }

    public AmmoRecord? ReadRuntimeAmmo(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeAmmo(entry);
    }

    public ConsumableRecord? ReadRuntimeConsumable(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeConsumable(entry);
    }

    public MiscItemRecord? ReadRuntimeMiscItem(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeMiscItem(entry);
    }

    public KeyRecord? ReadRuntimeKey(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeKey(entry);
    }

    public ContainerRecord? ReadRuntimeContainer(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeContainer(entry);
    }

    public BookRecord? ReadRuntimeBook(RuntimeEditorIdEntry entry)
    {
        return _books.ReadRuntimeBook(entry);
    }

    public WeaponModRecord? ReadRuntimeWeaponMod(RuntimeEditorIdEntry entry)
    {
        return _weaponMods.ReadRuntimeWeaponMod(entry);
    }

    public RecipeRecord? ReadRuntimeRecipe(RuntimeEditorIdEntry entry)
    {
        return _recipes.ReadRuntimeRecipe(entry);
    }

    public ChallengeRecord? ReadRuntimeChallenge(RuntimeEditorIdEntry entry)
    {
        return _challenges.ReadRuntimeChallenge(entry);
    }

    public ExplosionRecord? ReadRuntimeExplosion(RuntimeEditorIdEntry entry)
    {
        return _explosions.ReadRuntimeExplosion(entry);
    }

    public MessageRecord? ReadRuntimeMessage(RuntimeEditorIdEntry entry)
    {
        return _messages.ReadRuntimeMessage(entry);
    }

    #endregion

    #region Dialogue & Text

    public RuntimeDialogTopicInfo? ReadRuntimeDialogTopic(RuntimeEditorIdEntry entry)
    {
        return _dialogue.ReadRuntimeDialogTopic(entry);
    }

    public RuntimeDialogueInfo? ReadRuntimeDialogueInfo(RuntimeEditorIdEntry entry)
    {
        return _dialogue.ReadRuntimeDialogueInfo(entry);
    }

    public RuntimeDialogueInfo? ReadRuntimeDialogueInfoFromVA(uint va)
    {
        return _dialogue.ReadRuntimeDialogueInfoFromVA(va);
    }

    public QuestRecord? ReadRuntimeQuest(RuntimeEditorIdEntry entry)
    {
        return _dialogue.ReadRuntimeQuest(entry);
    }

    public TerminalRecord? ReadRuntimeTerminal(RuntimeEditorIdEntry entry)
    {
        return _dialogue.ReadRuntimeTerminal(entry);
    }

    public NoteRecord? ReadRuntimeNote(RuntimeEditorIdEntry entry)
    {
        return _dialogue.ReadRuntimeNote(entry);
    }

    public List<TopicQuestLink> WalkTopicQuestInfoList(RuntimeEditorIdEntry entry)
    {
        return _dialogue.WalkTopicQuestInfoList(entry);
    }

    /// <summary>Accumulated diagnostics for TESConversationData link list population.</summary>
    internal RuntimeDialogueReader.ConversationDataDiagnostics DialogueConversationDiagnostics =>
        _dialogue.ConversationDiagnostics;

    #endregion

    #region Encounter Zones

    public EncounterZoneRecord? ReadRuntimeEncounterZone(RuntimeEditorIdEntry entry)
    {
        return _encounterZones.ReadRuntimeEncounterZone(entry);
    }

    #endregion

    #region Navmeshes

    public NavMeshRecord? ReadRuntimeNavMesh(RuntimeEditorIdEntry entry)
    {
        return _navMeshes.ReadRuntimeNavMesh(entry);
    }

    #endregion

    #region Lighting Templates

    public LightingTemplateRecord? ReadRuntimeLightingTemplate(RuntimeEditorIdEntry entry)
    {
        return _lightingTemplates.ReadRuntimeLightingTemplate(entry);
    }

    #endregion

    #region Recipe Categories

    public RecipeCategoryRecord? ReadRuntimeRecipeCategory(RuntimeEditorIdEntry entry)
    {
        return _recipeCategories.ReadRuntimeRecipeCategory(entry);
    }

    #endregion

    #region Constructible Objects

    public ConstructibleObjectRecord? ReadRuntimeConstructibleObject(RuntimeEditorIdEntry entry)
    {
        return _constructibleObjects.ReadRuntimeConstructibleObject(entry);
    }

    #endregion

    #region Water

    public WaterRecord? ReadRuntimeWater(RuntimeEditorIdEntry entry)
    {
        return _water.ReadRuntimeWater(entry);
    }

    #endregion

    #region Head Parts / Voice Types

    public HeadPartRecord? ReadRuntimeHeadPart(RuntimeEditorIdEntry entry)
    {
        return _headParts.ReadRuntimeHeadPart(entry);
    }

    public VoiceTypeRecord? ReadRuntimeVoiceType(RuntimeEditorIdEntry entry)
    {
        return _voiceTypes.ReadRuntimeVoiceType(entry);
    }

    public MenuIconRecord? ReadRuntimeMenuIcon(RuntimeEditorIdEntry entry)
    {
        return _menuIcons.ReadRuntimeMenuIcon(entry);
    }

    public LoadScreenTypeRecord? ReadRuntimeLoadScreenType(RuntimeEditorIdEntry entry)
    {
        return _loadScreenTypes.ReadRuntimeLoadScreenType(entry);
    }

    public IdleAnimationRecord? ReadRuntimeIdleAnimation(RuntimeEditorIdEntry entry)
    {
        return _idleAnimations.ReadRuntimeIdleAnimation(entry);
    }

    public CameraPathRecord? ReadRuntimeCameraPath(RuntimeEditorIdEntry entry)
    {
        return _cameraPaths.ReadRuntimeCameraPath(entry);
    }

    public ImpactDataRecord? ReadRuntimeImpactData(RuntimeEditorIdEntry entry)
    {
        return _impactData.ReadRuntimeImpactData(entry);
    }

    public AudioLocationControllerRecord? ReadRuntimeAudioLocationController(RuntimeEditorIdEntry entry)
    {
        return _audioLocationControllers.ReadRuntimeAudioLocationController(entry);
    }

    public RegionRecord? ReadRuntimeRegion(RuntimeEditorIdEntry entry)
    {
        return _regions.ReadRuntimeRegion(entry);
    }

    public CaravanCardRecord? ReadRuntimeCaravanCard(RuntimeEditorIdEntry entry)
    {
        return _caravanCards.ReadRuntimeCaravanCard(entry);
    }

    public DebrisRecord? ReadRuntimeDebris(RuntimeEditorIdEntry entry)
    {
        return _debris.ReadRuntimeDebris(entry);
    }

    public IngredientRecord? ReadRuntimeIngredient(RuntimeEditorIdEntry entry)
    {
        return _ingredients.ReadRuntimeIngredient(entry);
    }

    public NavMeshInfoMapRecord? ReadRuntimeNavMeshInfoMap(RuntimeEditorIdEntry entry)
    {
        return _navMeshInfoMaps.ReadRuntimeNavMeshInfoMap(entry);
    }

    public CaravanDeckRecord? ReadRuntimeCaravanDeck(RuntimeEditorIdEntry entry)
    {
        return _caravanDecks.ReadRuntimeCaravanDeck(entry);
    }

    public SurvivalStageRecord? ReadRuntimeSurvivalStage(RuntimeEditorIdEntry entry, byte expectedFormType)
    {
        return _survivalStages.ReadRuntimeSurvivalStage(entry, expectedFormType);
    }

    #endregion

    #region World

    public RuntimeLoadedLandData? ReadRuntimeLandData(RuntimeEditorIdEntry entry)
    {
        return _world.ReadRuntimeLandData(entry);
    }

    public Dictionary<uint, RuntimeLoadedLandData> ReadAllRuntimeLandData(
        IEnumerable<RuntimeEditorIdEntry> entries)
    {
        return _world.ReadAllRuntimeLandData(entries);
    }

    public int ProbeDialTopicLayout(RuntimeEditorIdEntry entry)
    {
        return _world.ProbeDialTopicLayout(entry);
    }

    public ExtractedRefrRecord? ReadRuntimeRefr(RuntimeEditorIdEntry entry)
    {
        return _refrs.ReadRuntimeRefr(entry);
    }

    public Dictionary<uint, ExtractedRefrRecord> ReadAllRuntimeRefrs(
        IEnumerable<RuntimeEditorIdEntry> entries)
    {
        return _refrs.ReadAllRuntimeRefrs(entries);
    }

    internal RuntimeRefrExtraDataCensus BuildRuntimeRefrExtraDataCensus(
        IEnumerable<RuntimeEditorIdEntry> entries,
        int maxEntries = 256)
    {
        return _refrs.BuildExtraDataCensus(entries, maxEntries);
    }

    public Dictionary<uint, RuntimeWorldspaceData> ReadAllWorldspaceCellMaps(
        IEnumerable<RuntimeEditorIdEntry> entries)
    {
        return _cells.ReadAllWorldspaceCellMaps(entries);
    }

    public WorldspaceRecord? ReadRuntimeWorldspace(RuntimeEditorIdEntry entry)
    {
        return _cells.ReadRuntimeWorldspace(entry);
    }

    public CellRecord? ReadRuntimeCell(RuntimeEditorIdEntry entry)
    {
        return _cells.ReadRuntimeCell(entry);
    }

    public CellRecord? ReadRuntimeCell(RuntimeCellMapEntry entry, string? editorId = null, string? displayName = null)
    {
        return _cells.ReadRuntimeCell(entry, editorId, displayName);
    }

    #endregion

    #region Collections

    public FormListRecord? ReadRuntimeFormList(RuntimeEditorIdEntry entry)
    {
        return _collections.ReadRuntimeFormList(entry);
    }

    public LeveledListRecord? ReadRuntimeLeveledList(RuntimeEditorIdEntry entry)
    {
        return _collections.ReadRuntimeLeveledList(entry);
    }

    #endregion

    #region World Objects

    public ActivatorRecord? ReadRuntimeActivator(RuntimeEditorIdEntry entry)
    {
        return _worldObjects.ReadRuntimeActivator(entry);
    }

    public LightRecord? ReadRuntimeLight(RuntimeEditorIdEntry entry)
    {
        return _worldObjects.ReadRuntimeLight(entry);
    }

    public DoorRecord? ReadRuntimeDoor(RuntimeEditorIdEntry entry)
    {
        return _worldObjects.ReadRuntimeDoor(entry);
    }

    public StaticRecord? ReadRuntimeStatic(RuntimeEditorIdEntry entry)
    {
        return _worldObjects.ReadRuntimeStatic(entry);
    }

    public FurnitureRecord? ReadRuntimeFurniture(RuntimeEditorIdEntry entry)
    {
        return _worldObjects.ReadRuntimeFurniture(entry);
    }

    #endregion
}
