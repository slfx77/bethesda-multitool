using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers;
using BethesdaMultitool.Core.Minidump;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime;

/// <summary>
///     Reader for runtime game structures in Xbox 360 memory dumps.
///     Facade that delegates to domain-specific reader classes.
/// </summary>
public sealed class RuntimeStructReader
{
    private readonly RuntimeActorReader _actors;
    private readonly RuntimeActorWeaponReader _actorWeapons;
    private readonly RuntimeCharacterAppearanceReader _appearance;
    private readonly RuntimeAudioLocationControllerReader _audioLocationControllers;
    private readonly RuntimeBookReader _books;
    private readonly RuntimeCameraPathReader _cameraPaths;
    private readonly RuntimeCaravanCardReader _caravanCards;
    private readonly RuntimeCaravanDeckReader _caravanDecks;
    private readonly RuntimeCaravanMoneyReader _caravanMoney;
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
    private readonly RuntimeFlorReader _flora;
    private readonly RuntimeGenericReader _generic;
    private readonly RuntimeGlobalReader _globals;
    private readonly RuntimeHeadPartReader _headParts;
    private readonly RuntimeIdleAnimationReader _idleAnimations;
    private readonly RuntimeImageSpaceModifierReader _imageSpaceModifiers;
    private readonly RuntimeImpactDataReader _impactData;
    private readonly RuntimeIngredientReader _ingredients;
    private readonly RuntimeItemReader _items;
    private readonly RuntimeLightingTemplateReader _lightingTemplates;
    private readonly RuntimeLoadScreenTypeReader _loadScreenTypes;
    private readonly RuntimeMagicReader _magic;
    private readonly RuntimeMenuIconReader _menuIcons;
    private readonly RuntimeMessageReader _messages;
    private readonly RuntimeMsttReader _movableStatics;
    private readonly RuntimeMusicTypeReader _musicTypes;
    private readonly RuntimeNavMeshReader _navMeshes;
    private readonly RuntimeNavMeshInfoMapReader _navMeshInfoMaps;
    private readonly RuntimeNavMeshDiscovery _navMeshDiscovery;
    private readonly RuntimePackageReader _packages;
    private readonly RuntimeRaceReader _races;
    private readonly RuntimeRecipeCategoryReader _recipeCategories;
    private readonly RuntimeRecipeReader _recipes;
    private readonly RuntimeRefrReader _refrs;
    private readonly RuntimeRegionReader _regions;
    private readonly RuntimeReputationReader _reputations;
    private readonly RuntimeScriptReader _scripts;
    private readonly RuntimeSkyWeatherReader _skyWeather;
    private readonly RuntimeSoundReader _sounds;
    private readonly RuntimeSurvivalStageReader _survivalStages;
    private readonly RuntimeVoiceTypeReader _voiceTypes;
    private readonly RuntimeWaterReader _water;
    private readonly RuntimeWeaponModReader _weaponMods;
    private readonly RuntimeWorldReader _world;
    private readonly RuntimeWorldObjectReader _worldObjects;

    /// <summary>
    ///     Creates a runtime reader over a memory-mapped DMP view, using the struct offsets for
    ///     the detected build (pass <paramref name="useProtoOffsets" /> for prototype-build layouts).
    /// </summary>
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
        RuntimeProbeResults? probeResults = null,
        int bipedPtrShift = 0)
    {
        IsEarlyBuild = useProtoOffsets;
        WorldCellLayoutProbe = worldCellLayoutProbe;
        ProbeResults = probeResults;
        _context = new RuntimeMemoryContext(accessor, fileSize, minidumpInfo)
        {
            EditorIdsByFormId = probeResults?.EditorIdsByFormId
        };
        _actors = new RuntimeActorReader(_context, npcLayoutProbe);
        _generic = new RuntimeGenericReader(_context, probeResults?.GenericTypeShifts);
        _items = new RuntimeItemReader(_context, probeResults?.WeaponSoundLayout, probeResults?.AmmoDataLayout);
        _dialogue = new RuntimeDialogueReader(_context);
        _effects = new RuntimeEffectReader(_context, probeResults?.EffectLayout);
        _scripts = new RuntimeScriptReader(_context);
        _skyWeather = new RuntimeSkyWeatherReader(_context);
        _world = new RuntimeWorldReader(_context);
        _refrs = new RuntimeRefrReader(_context, useProtoOffsets);
        _packages = new RuntimePackageReader(_context);
        _actorWeapons = new RuntimeActorWeaponReader(_context, bipedPtrShift);
        _cells = new RuntimeCellReader(_context, useProtoOffsets, worldCellLayoutProbe);
        _collections = new RuntimeCollectionReader(_context);
        _worldObjects = new RuntimeWorldObjectReader(_context);
        _races = new RuntimeRaceReader(_context, probeResults?.RaceLayout);
        _magic = new RuntimeMagicReader(_context);
        _globals = new RuntimeGlobalReader(_context);
        _classes = new RuntimeClassReader(_context);
        _appearance = new RuntimeCharacterAppearanceReader(_context);
        _reputations = new RuntimeReputationReader(_context);
        _musicTypes = new RuntimeMusicTypeReader(_context);
        _sounds = new RuntimeSoundReader(_context);
        _books = new RuntimeBookReader(_context);
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
        _imageSpaceModifiers = new RuntimeImageSpaceModifierReader(_context, useProtoOffsets);
        _cameraPaths = new RuntimeCameraPathReader(_context);
        _impactData = new RuntimeImpactDataReader(_context);
        _audioLocationControllers = new RuntimeAudioLocationControllerReader(_context);
        _movableStatics = new RuntimeMsttReader(_context);
        _flora = new RuntimeFlorReader(_context);
        _regions = new RuntimeRegionReader(_context);
        _caravanCards = new RuntimeCaravanCardReader(_context);
        _caravanMoney = new RuntimeCaravanMoneyReader(_context);
        _debris = new RuntimeDebrisReader(_context);
        _ingredients = new RuntimeIngredientReader(_context);
        _navMeshInfoMaps = new RuntimeNavMeshInfoMapReader(_context);
        _navMeshDiscovery = new RuntimeNavMeshDiscovery(_context);
        _caravanDecks = new RuntimeCaravanDeckReader(_context);
        _survivalStages = new RuntimeSurvivalStageReader(_context);
    }

    public bool IsEarlyBuild { get; }
    internal RuntimeWorldCellLayoutProbeResult? WorldCellLayoutProbe { get; }

    /// <summary>
    ///     Aggregated probe results captured by <see cref="CreateWithAutoDetect(IMemoryAccessor,long,MinidumpInfo,IReadOnlyList{RuntimeEditorIdEntry},IReadOnlyList{RuntimeEditorIdEntry}?,IReadOnlyList{RuntimeEditorIdEntry}?,IReadOnlyList{RuntimeEditorIdEntry}?,IReadOnlyList{RuntimeEditorIdEntry}?,IReadOnlyList{RuntimeEditorIdEntry}?)" />.
    ///     Surfaced so research/diagnostic commands (e.g. <c>dmp probe-shifts</c>) can
    ///     inspect probe-discovered layout drift across builds without re-running the probes.
    ///     Null when constructed without an entry set (e.g. test fixtures).
    /// </summary>
    internal RuntimeProbeResults? ProbeResults { get; }

    /// <summary>
    ///     Locates the runtime Sky singleton and returns its current/outgoing weather blend.
    ///     Currently supported only for the symbol- and DMP-validated FNV Xbox 360 Debug,
    ///     Release Beta, and Release MemDebug families; unsupported games return null.
    /// </summary>
    public WeatherTransitionSnapshot? ReadWeatherTransitionSnapshot()
    {
        return _skyWeather.Read();
    }

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
        IReadOnlyList<RuntimeEditorIdEntry>? allEntries = null,
        IReadOnlyList<RuntimeEditorIdEntry>? landEntries = null)
    {
        return CreateWithAutoDetect(new MmfMemoryAccessor(accessor), fileSize, minidumpInfo,
            refrEntries, npcEntries, worldEntries, cellEntries, allEntries, landEntries);
    }

    /// <summary>
    ///     Creates a reader that auto-detects the build's struct layout by probing the supplied
    ///     runtime entries (REFR/NPC/world/cell/LAND) before reading.
    /// </summary>
    public static RuntimeStructReader CreateWithAutoDetect(
        IMemoryAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        IReadOnlyList<RuntimeEditorIdEntry> refrEntries,
        IReadOnlyList<RuntimeEditorIdEntry>? npcEntries = null,
        IReadOnlyList<RuntimeEditorIdEntry>? worldEntries = null,
        IReadOnlyList<RuntimeEditorIdEntry>? cellEntries = null,
        IReadOnlyList<RuntimeEditorIdEntry>? allEntries = null,
        IReadOnlyList<RuntimeEditorIdEntry>? landEntries = null)
    {
        var context = new RuntimeMemoryContext(accessor, fileSize, minidumpInfo);
        var refrLayoutProbe = RuntimeRefrReader.ProbeRefrLayout(context, refrEntries);
        var isEarlyBuild = refrLayoutProbe.Winner.Layout;
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
            // (used by the weapon sound probe to disambiguate adjacent SOUN pointers, and by
            // the QUST script brute-force scan to validate candidates via the
            // Script.pOwnerQuest backpointer). The NPC-side brute-force scan was removed
            // because prefix-based validation was unsafe — see RuntimeActorReader.
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
                RefrLayout = refrLayoutProbe,
                NpcLayout = npcLayoutProbe,
                WorldCellLayout = worldCellLayoutProbe,
                RaceLayout = RuntimeRaceProbe.Probe(context, allEntries),
                EffectLayout = RuntimeEffectProbe.Probe(context, allEntries),
                WeaponSoundLayout = RuntimeWeaponSoundProbe.Probe(context, allEntries,
                    msg => Logger.Instance.Info(msg),
                    editorIdsByFormId),
                AmmoDataLayout = RuntimeAmmoDataProbe.Probe(context, allEntries),
                GenericTypeShifts = RuntimeGenericReader.ProbeAllTypeShifts(context, allEntries),
                // Surface the dict on the context (via the reader constructor) so specialized
                // readers can resolve candidate Script* pointers to their EditorIds. The
                // QUST reader uses this when validating via Script.pOwnerQuest. Without it,
                // the QUST brute-force scan would pick the first arbitrary Script* pointer
                // it finds in struct memory — for a TESQuest captured mid-tutorial-execution
                // that's a runtime-cached unrelated script (CGTutorialSCRIPT bound to VMS01).
                EditorIdsByFormId = editorIdsByFormId
            };
        }
        else if (npcLayoutProbe != null || worldCellLayoutProbe != null)
        {
            probeResults = new RuntimeProbeResults
            {
                RefrLayout = refrLayoutProbe,
                NpcLayout = npcLayoutProbe,
                WorldCellLayout = worldCellLayoutProbe
            };
        }

        var bipedPtrShift = RuntimeBipedOffsetProbe.Probe(
            context, refrEntries, msg => Logger.Instance.Info(msg)) ?? 0;

        return new RuntimeStructReader(
            accessor,
            fileSize,
            minidumpInfo,
            isEarlyBuild,
            npcLayoutProbe,
            worldCellLayoutProbe,
            probeResults,
            bipedPtrShift);
    }

    #region Scripts

    /// <summary>Reads the runtime script for the given DMP entry, or null if it can't be read.</summary>
    public RuntimeScriptData? ReadRuntimeScript(RuntimeEditorIdEntry entry)
    {
        return _scripts.ReadRuntimeScript(entry);
    }

    #endregion

    #region AI Packages

    /// <summary>Reads the runtime AI-package record for the given DMP entry, or null if it can't be read.</summary>
    public PackageRecord? ReadRuntimePackage(RuntimeEditorIdEntry entry)
    {
        return _packages.ReadRuntimePackage(entry);
    }

    #endregion

    #region Strings (shared utility)

    /// <summary>
    ///     Reads a BSString&lt;T&gt; (length-prefixed engine string) at a field offset within a
    ///     TESForm struct located at the given file offset.
    /// </summary>
    public string? ReadBsStringT(long tesFormFileOffset, int fieldOffset)
    {
        return _context.ReadBsStringT(tesFormFileOffset, fieldOffset);
    }

    #endregion

    #region Generic (PDB-derived)

    /// <summary>
    ///     Reads a record with no specialized reader as a generic, PDB-layout-driven record
    ///     (identity and schema fields only) for the given DMP entry.
    /// </summary>
    public GenericEsmRecord? ReadGenericRecord(RuntimeEditorIdEntry entry)
    {
        return _generic.ReadGenericRecord(entry);
    }

    /// <summary>
    ///     Read a runtime MSTT (BGSMovableStatic) base form. Activated by the
    ///     multi-inheritance probe in <see cref="Records.TesFormHeaderProbe" />.
    /// </summary>
    public GenericEsmRecord? ReadRuntimeMstt(RuntimeEditorIdEntry entry)
    {
        return _movableStatics.ReadRuntimeMstt(entry);
    }

    /// <summary>
    ///     Read a runtime FLOR (TESFlora) base form. Activated by the
    ///     multi-inheritance probe in <see cref="Records.TesFormHeaderProbe" />.
    /// </summary>
    public GenericEsmRecord? ReadRuntimeFlor(RuntimeEditorIdEntry entry)
    {
        return _flora.ReadRuntimeFlor(entry);
    }

    #endregion

    #region Globals

    /// <summary>Reads the runtime global-variable record for the given DMP entry, or null if it can't be read.</summary>
    public GlobalRecord? ReadRuntimeGlobal(RuntimeEditorIdEntry entry)
    {
        return _globals.ReadRuntimeGlobal(entry);
    }

    #endregion

    #region Classes

    /// <summary>Reads the runtime class record for the given DMP entry, or null if it can't be read.</summary>
    public ClassRecord? ReadRuntimeClass(RuntimeEditorIdEntry entry)
    {
        return _classes.ReadRuntimeClass(entry);
    }

    #endregion

    #region Reputations

    /// <summary>Reads the runtime reputation record for the given DMP entry, or null if it can't be read.</summary>
    public ReputationRecord? ReadRuntimeReputation(RuntimeEditorIdEntry entry)
    {
        return _reputations.ReadRuntimeReputation(entry);
    }

    #endregion

    #region Encounter Zones

    /// <summary>Reads the runtime encounter-zone record for the given DMP entry, or null if it can't be read.</summary>
    public EncounterZoneRecord? ReadRuntimeEncounterZone(RuntimeEditorIdEntry entry)
    {
        return _encounterZones.ReadRuntimeEncounterZone(entry);
    }

    #endregion

    #region Navmeshes

    /// <summary>Reads the runtime navmesh record for the given DMP entry, or null if it can't be read.</summary>
    public NavMeshRecord? ReadRuntimeNavMesh(RuntimeEditorIdEntry entry)
    {
        return _navMeshes.ReadRuntimeNavMesh(entry);
    }

    #endregion

    #region Lighting Templates

    /// <summary>Reads the runtime lighting-template record for the given DMP entry, or null if it can't be read.</summary>
    public LightingTemplateRecord? ReadRuntimeLightingTemplate(RuntimeEditorIdEntry entry)
    {
        return _lightingTemplates.ReadRuntimeLightingTemplate(entry);
    }

    #endregion

    #region Recipe Categories

    /// <summary>Reads the runtime recipe-category record for the given DMP entry, or null if it can't be read.</summary>
    public RecipeCategoryRecord? ReadRuntimeRecipeCategory(RuntimeEditorIdEntry entry)
    {
        return _recipeCategories.ReadRuntimeRecipeCategory(entry);
    }

    #endregion

    #region Constructible Objects

    /// <summary>Reads the runtime constructible-object (recipe) record for the given DMP entry, or null if it can't be read.</summary>
    public ConstructibleObjectRecord? ReadRuntimeConstructibleObject(RuntimeEditorIdEntry entry)
    {
        return _constructibleObjects.ReadRuntimeConstructibleObject(entry);
    }

    #endregion

    #region Water

    /// <summary>Reads the runtime water-type record for the given DMP entry, or null if it can't be read.</summary>
    public WaterRecord? ReadRuntimeWater(RuntimeEditorIdEntry entry)
    {
        return _water.ReadRuntimeWater(entry);
    }

    #endregion

    #region Image Space Modifiers

    /// <summary>
    ///     Reconstructs a complete runtime IMAD ordered stream, or returns null when either
    ///     known TESImageSpaceModifier layout cannot be validated without missing data.
    /// </summary>
    public ImageSpaceModifierRecord? ReadRuntimeImageSpaceModifier(RuntimeEditorIdEntry entry)
    {
        return _imageSpaceModifiers.ReadRuntimeImageSpaceModifier(entry);
    }

    #endregion

    #region Effects

    /// <summary>Reads a runtime projectile's physics block at the given file offset, validating its FormID.</summary>
    public ProjectilePhysicsData? ReadProjectilePhysics(long fileOffset, uint expectedFormId)
    {
        return _effects.ReadProjectilePhysics(fileOffset, expectedFormId);
    }

    /// <summary>Reads the runtime projectile record for the given DMP entry, or null if it can't be read.</summary>
    public ProjectileRecord? ReadRuntimeProjectile(RuntimeEditorIdEntry entry)
    {
        return _effects.ReadRuntimeProjectile(entry);
    }

    #endregion

    #region Sounds

    /// <summary>Reads the runtime music-type record for the given DMP entry, or null if it can't be read.</summary>
    public MusicTypeRecord? ReadRuntimeMusicType(RuntimeEditorIdEntry entry)
    {
        return _musicTypes.ReadRuntimeMusicType(entry);
    }

    /// <summary>Reads the runtime sound record for the given DMP entry, or null if it can't be read.</summary>
    public SoundRecord? ReadRuntimeSound(RuntimeEditorIdEntry entry)
    {
        return _sounds.ReadRuntimeSound(entry);
    }

    #endregion

    #region Actors

    /// <summary>Reads the runtime NPC record for the given DMP entry, or null if it can't be read.</summary>
    public NpcRecord? ReadRuntimeNpc(RuntimeEditorIdEntry entry)
    {
        return _actors.ReadRuntimeNpc(entry);
    }

    internal RuntimeActorWeaponReader.RuntimeActorWeaponState? ReadRuntimeActorWeaponState(RuntimeEditorIdEntry entry)
    {
        return _actorWeapons.ReadRuntimeActorWeaponState(entry);
    }

    internal RuntimeActorWeaponReader.RuntimeActorWornArmorState? ReadRuntimeActorWornArmor(RuntimeEditorIdEntry entry)
    {
        return _actorWeapons.ReadRuntimeActorWornArmor(entry);
    }

    /// <summary>Reads the runtime creature record for the given DMP entry, or null if it can't be read.</summary>
    public CreatureRecord? ReadRuntimeCreature(RuntimeEditorIdEntry entry)
    {
        return _actors.ReadRuntimeCreature(entry);
    }

    /// <summary>Reads the runtime faction record for the given DMP entry, or null if it can't be read.</summary>
    public FactionRecord? ReadRuntimeFaction(RuntimeEditorIdEntry entry)
    {
        return _actors.ReadRuntimeFaction(entry);
    }

    /// <summary>Reads the runtime actor-value-info record for the given DMP entry, or null if it can't be read.</summary>
    public ActorValueInfoRecord? ReadRuntimeAvif(RuntimeEditorIdEntry entry)
    {
        return _actors.ReadRuntimeAvif(entry);
    }

    /// <summary>Reads the runtime race record for the given DMP entry, or null if it can't be read.</summary>
    public RaceRecord? ReadRuntimeRace(RuntimeEditorIdEntry entry)
    {
        return _races.ReadRuntimeRace(entry);
    }

    #endregion

    #region Magic / Effects

    /// <summary>Reads the runtime base magic-effect record for the given DMP entry, or null if it can't be read.</summary>
    public BaseEffectRecord? ReadRuntimeBaseEffect(RuntimeEditorIdEntry entry)
    {
        return _magic.ReadRuntimeBaseEffect(entry);
    }

    /// <summary>Reads the runtime spell record for the given DMP entry, or null if it can't be read.</summary>
    public SpellRecord? ReadRuntimeSpell(RuntimeEditorIdEntry entry)
    {
        return _magic.ReadRuntimeSpell(entry);
    }

    /// <summary>Reads the runtime enchantment record for the given DMP entry, or null if it can't be read.</summary>
    public EnchantmentRecord? ReadRuntimeEnchantment(RuntimeEditorIdEntry entry)
    {
        return _magic.ReadRuntimeEnchantment(entry);
    }

    /// <summary>Reads the runtime perk record for the given DMP entry, or null if it can't be read.</summary>
    public PerkRecord? ReadRuntimePerk(RuntimeEditorIdEntry entry)
    {
        return _magic.ReadRuntimePerk(entry);
    }

    #endregion

    #region Character Appearance

    /// <summary>Reads the runtime eyes record for the given DMP entry, or null if it can't be read.</summary>
    public EyesRecord? ReadRuntimeEyes(RuntimeEditorIdEntry entry)
    {
        return _appearance.ReadRuntimeEyes(entry);
    }

    /// <summary>Reads the runtime hair record for the given DMP entry, or null if it can't be read.</summary>
    public HairRecord? ReadRuntimeHair(RuntimeEditorIdEntry entry)
    {
        return _appearance.ReadRuntimeHair(entry);
    }

    #endregion

    #region Items

    /// <summary>Reads the runtime weapon record for the given DMP entry, or null if it can't be read.</summary>
    public WeaponRecord? ReadRuntimeWeapon(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeWeapon(entry);
    }

    /// <summary>Reads the runtime armor record for the given DMP entry, or null if it can't be read.</summary>
    public ArmorRecord? ReadRuntimeArmor(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeArmor(entry);
    }

    /// <summary>Reads the runtime ammunition record for the given DMP entry, or null if it can't be read.</summary>
    public AmmoRecord? ReadRuntimeAmmo(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeAmmo(entry);
    }

    /// <summary>Reads the runtime consumable (ALCH) record for the given DMP entry, or null if it can't be read.</summary>
    public ConsumableRecord? ReadRuntimeConsumable(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeConsumable(entry);
    }

    /// <summary>Reads the runtime miscellaneous-item record for the given DMP entry, or null if it can't be read.</summary>
    public MiscItemRecord? ReadRuntimeMiscItem(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeMiscItem(entry);
    }

    /// <summary>Reads the runtime key record for the given DMP entry, or null if it can't be read.</summary>
    public KeyRecord? ReadRuntimeKey(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeKey(entry);
    }

    /// <summary>Reads the runtime container record for the given DMP entry, or null if it can't be read.</summary>
    public ContainerRecord? ReadRuntimeContainer(RuntimeEditorIdEntry entry)
    {
        return _items.ReadRuntimeContainer(entry);
    }

    /// <summary>Reads the runtime book record for the given DMP entry, or null if it can't be read.</summary>
    public BookRecord? ReadRuntimeBook(RuntimeEditorIdEntry entry)
    {
        return _books.ReadRuntimeBook(entry);
    }

    /// <summary>Reads the runtime weapon-mod record for the given DMP entry, or null if it can't be read.</summary>
    public WeaponModRecord? ReadRuntimeWeaponMod(RuntimeEditorIdEntry entry)
    {
        return _weaponMods.ReadRuntimeWeaponMod(entry);
    }

    /// <summary>Reads the runtime recipe record for the given DMP entry, or null if it can't be read.</summary>
    public RecipeRecord? ReadRuntimeRecipe(RuntimeEditorIdEntry entry)
    {
        return _recipes.ReadRuntimeRecipe(entry);
    }

    /// <summary>Reads the runtime challenge record for the given DMP entry, or null if it can't be read.</summary>
    public ChallengeRecord? ReadRuntimeChallenge(RuntimeEditorIdEntry entry)
    {
        return _challenges.ReadRuntimeChallenge(entry);
    }

    /// <summary>Reads the runtime explosion record for the given DMP entry, or null if it can't be read.</summary>
    public ExplosionRecord? ReadRuntimeExplosion(RuntimeEditorIdEntry entry)
    {
        return _explosions.ReadRuntimeExplosion(entry);
    }

    /// <summary>Reads the runtime message record for the given DMP entry, or null if it can't be read.</summary>
    public MessageRecord? ReadRuntimeMessage(RuntimeEditorIdEntry entry)
    {
        return _messages.ReadRuntimeMessage(entry);
    }

    #endregion

    #region Dialogue & Text

    /// <summary>Reads the runtime dialogue-topic (DIAL) info for the given DMP entry.</summary>
    public RuntimeDialogTopicInfo? ReadRuntimeDialogTopic(RuntimeEditorIdEntry entry)
    {
        return _dialogue.ReadRuntimeDialogTopic(entry);
    }

    /// <summary>Reads the runtime dialogue-response (INFO) data for the given DMP entry.</summary>
    public RuntimeDialogueInfo? ReadRuntimeDialogueInfo(RuntimeEditorIdEntry entry)
    {
        return _dialogue.ReadRuntimeDialogueInfo(entry);
    }

    /// <summary>Reads the runtime dialogue-response (INFO) data at the given virtual address.</summary>
    public RuntimeDialogueInfo? ReadRuntimeDialogueInfoFromVA(uint va)
    {
        return _dialogue.ReadRuntimeDialogueInfoFromVA(va);
    }

    /// <summary>Reads the runtime quest record for the given DMP entry, or null if it can't be read.</summary>
    public QuestRecord? ReadRuntimeQuest(RuntimeEditorIdEntry entry)
    {
        return _dialogue.ReadRuntimeQuest(entry);
    }

    /// <summary>Reads the runtime terminal record for the given DMP entry, or null if it can't be read.</summary>
    public TerminalRecord? ReadRuntimeTerminal(RuntimeEditorIdEntry entry)
    {
        return _dialogue.ReadRuntimeTerminal(entry);
    }

    /// <summary>Reads the runtime note record for the given DMP entry, or null if it can't be read.</summary>
    public NoteRecord? ReadRuntimeNote(RuntimeEditorIdEntry entry)
    {
        return _dialogue.ReadRuntimeNote(entry);
    }

    /// <summary>Walks a dialogue topic's runtime quest/info list, returning the topic-to-quest links.</summary>
    public List<TopicQuestLink> WalkTopicQuestInfoList(RuntimeEditorIdEntry entry)
    {
        return _dialogue.WalkTopicQuestInfoList(entry);
    }

    /// <summary>Accumulated diagnostics for TESConversationData link list population.</summary>
    internal RuntimeDialogueReader.ConversationDataDiagnostics DialogueConversationDiagnostics =>
        _dialogue.ConversationDiagnostics;

    #endregion

    #region Head Parts / Voice Types

    /// <summary>Reads the runtime head-part record for the given DMP entry, or null if it can't be read.</summary>
    public HeadPartRecord? ReadRuntimeHeadPart(RuntimeEditorIdEntry entry)
    {
        return _headParts.ReadRuntimeHeadPart(entry);
    }

    /// <summary>Reads the runtime voice-type record for the given DMP entry, or null if it can't be read.</summary>
    public VoiceTypeRecord? ReadRuntimeVoiceType(RuntimeEditorIdEntry entry)
    {
        return _voiceTypes.ReadRuntimeVoiceType(entry);
    }

    /// <summary>Reads the runtime menu-icon record for the given DMP entry, or null if it can't be read.</summary>
    public MenuIconRecord? ReadRuntimeMenuIcon(RuntimeEditorIdEntry entry)
    {
        return _menuIcons.ReadRuntimeMenuIcon(entry);
    }

    /// <summary>Reads the runtime load-screen-type record for the given DMP entry, or null if it can't be read.</summary>
    public LoadScreenTypeRecord? ReadRuntimeLoadScreenType(RuntimeEditorIdEntry entry)
    {
        return _loadScreenTypes.ReadRuntimeLoadScreenType(entry);
    }

    /// <summary>Reads the runtime idle-animation record for the given DMP entry, or null if it can't be read.</summary>
    public IdleAnimationRecord? ReadRuntimeIdleAnimation(RuntimeEditorIdEntry entry)
    {
        return _idleAnimations.ReadRuntimeIdleAnimation(entry);
    }

    /// <summary>Reads the runtime camera-path record for the given DMP entry, or null if it can't be read.</summary>
    public CameraPathRecord? ReadRuntimeCameraPath(RuntimeEditorIdEntry entry)
    {
        return _cameraPaths.ReadRuntimeCameraPath(entry);
    }

    /// <summary>Reads the runtime impact-data record for the given DMP entry, or null if it can't be read.</summary>
    public ImpactDataRecord? ReadRuntimeImpactData(RuntimeEditorIdEntry entry)
    {
        return _impactData.ReadRuntimeImpactData(entry);
    }

    /// <summary>Reads the runtime audio-location-controller record for the given DMP entry, or null if it can't be read.</summary>
    public AudioLocationControllerRecord? ReadRuntimeAudioLocationController(RuntimeEditorIdEntry entry)
    {
        return _audioLocationControllers.ReadRuntimeAudioLocationController(entry);
    }

    /// <summary>Reads the runtime region record for the given DMP entry, or null if it can't be read.</summary>
    public RegionRecord? ReadRuntimeRegion(RuntimeEditorIdEntry entry)
    {
        return _regions.ReadRuntimeRegion(entry);
    }

    /// <summary>Reads the runtime caravan-card record for the given DMP entry, or null if it can't be read.</summary>
    public CaravanCardRecord? ReadRuntimeCaravanCard(RuntimeEditorIdEntry entry)
    {
        return _caravanCards.ReadRuntimeCaravanCard(entry);
    }

    /// <summary>Reads the runtime caravan-money record for the given DMP entry, or null if it can't be read.</summary>
    public CaravanMoneyRecord? ReadRuntimeCaravanMoney(RuntimeEditorIdEntry entry)
    {
        return _caravanMoney.ReadRuntimeCaravanMoney(entry);
    }

    /// <summary>Reads the runtime debris record for the given DMP entry, or null if it can't be read.</summary>
    public DebrisRecord? ReadRuntimeDebris(RuntimeEditorIdEntry entry)
    {
        return _debris.ReadRuntimeDebris(entry);
    }

    /// <summary>Reads the runtime ingredient record for the given DMP entry, or null if it can't be read.</summary>
    public IngredientRecord? ReadRuntimeIngredient(RuntimeEditorIdEntry entry)
    {
        return _ingredients.ReadRuntimeIngredient(entry);
    }

    /// <summary>Reads the runtime navmesh-info-map record for the given DMP entry, or null if it can't be read.</summary>
    public NavMeshInfoMapRecord? ReadRuntimeNavMeshInfoMap(RuntimeEditorIdEntry entry)
    {
        return _navMeshInfoMaps.ReadRuntimeNavMeshInfoMap(entry);
    }

    /// <summary>
    ///     Walk the NavMeshInfoMap's InfoMap NiTPointerMap (FormType 0x38 entry) and surface
    ///     every BSNavMesh the engine has loaded in heap memory. Without this, all NAVMs not
    ///     present in the in-DMP ESM byte stream stay invisible because vanilla navmeshes have
    ///     no editor IDs and therefore never land in the editor-id-hash-driven editor-id list
    ///     that <see cref="ReadRuntimeNavMesh" /> walks.
    /// </summary>
    public List<NavMeshRecord> DiscoverNavMeshesFromInfoMap(RuntimeEditorIdEntry naviEntry)
    {
        return _navMeshDiscovery.Discover(naviEntry);
    }

    /// <summary>
    ///     Walk a TESObjectCELL's per-cell NavMeshArray (the BSSimpleArray inlined at +0x74)
    ///     and surface every BSNavMesh the engine has loaded for that cell. This is the actual
    ///     reachable entry point for runtime NAVM discovery — the NAVI singleton has no editor
    ///     ID so the InfoMap walk via <see cref="DiscoverNavMeshesFromInfoMap" /> currently
    ///     can't fire.
    /// </summary>
    public List<NavMeshRecord> DiscoverNavMeshesForCell(RuntimeEditorIdEntry cellEntry)
    {
        return _navMeshDiscovery.DiscoverForCell(cellEntry);
    }

    /// <summary>
    ///     VA-driven companion to <see cref="DiscoverNavMeshesForCell" />: walks the cell's
    ///     <c>NavMeshArray</c> without requiring a <see cref="RuntimeEditorIdEntry" />. Used by
    ///     <c>MiscGameSystemHandler</c> when handing off cells discovered via paths
    ///     other than the editor-id hash (pAllForms walk, worldspace grid, heap-scan).
    /// </summary>
    public List<NavMeshRecord> DiscoverNavMeshesForCellVa(uint cellVa, uint cellFormId)
    {
        return _navMeshDiscovery.DiscoverForCellVa(cellVa, cellFormId);
    }

    /// <summary>
    ///     Direct BSNavMesh projection: reads a single BSNavMesh struct at the given VA and
    ///     returns its synthetic <see cref="NavMeshRecord" /> without going through a cell
    ///     parent. Used by Path 4 in <c>MiscGameSystemHandler</c> to surface
    ///     runtime-only NAVMs from pAllForms (FormType 0x43) when the cell graph has detached
    ///     NavMeshArrays.
    /// </summary>
    public NavMeshRecord? DiscoverNavMeshAtVa(uint navMeshVa, uint fallbackParentCellFormId)
    {
        return _navMeshDiscovery.DiscoverForNavMeshVa(navMeshVa, fallbackParentCellFormId);
    }

    /// <summary>
    ///     Factory for the multi-source <see cref="RuntimeCellEnumerator" />. Returns null when
    ///     the upstream pointer-triple scan failed to locate <c>pAllForms</c> (no enumeration is
    ///     possible without it). Consumers should fall back to the editor-id-only path in that
    ///     case.
    /// </summary>
    internal RuntimeCellEnumerator? CreateRuntimeCellEnumerator(
        uint pAllFormsVa,
        IReadOnlyDictionary<byte, byte>? driftRemap = null)
    {
        if (pAllFormsVa == 0)
        {
            return null;
        }

        return new RuntimeCellEnumerator(_context, _context.MinidumpInfo, pAllFormsVa, driftRemap);
    }

    /// <summary>
    ///     Factory for the BSNavMesh structural validator used by Phase 2d to filter Path 4
    ///     candidates. Holds a snapshot of the cell VAs surfaced by
    ///     <see cref="RuntimeCellEnumerator" /> so Strict mode can cross-reference each
    ///     candidate's <c>pParentCell</c> field against a trusted runtime cell set.
    /// </summary>
    internal BsNavMeshStructuralValidator CreateBsNavMeshValidator(
        IReadOnlySet<uint> knownCellVas,
        BsNavMeshValidationMode mode)
    {
        return new BsNavMeshStructuralValidator(_context, knownCellVas, mode);
    }

    /// <summary>Reads the runtime caravan-deck record for the given DMP entry, or null if it can't be read.</summary>
    public CaravanDeckRecord? ReadRuntimeCaravanDeck(RuntimeEditorIdEntry entry)
    {
        return _caravanDecks.ReadRuntimeCaravanDeck(entry);
    }

    /// <summary>Reads the runtime survival-stage record for the given DMP entry, or null if it can't be read.</summary>
    public SurvivalStageRecord? ReadRuntimeSurvivalStage(RuntimeEditorIdEntry entry, byte expectedFormType)
    {
        return _survivalStages.ReadRuntimeSurvivalStage(entry, expectedFormType);
    }

    #endregion

    #region World

    /// <summary>Reads the runtime loaded landscape (LAND) terrain data for the given cell entry.</summary>
    public RuntimeLoadedLandData? ReadRuntimeLandData(RuntimeEditorIdEntry entry)
    {
        return _world.ReadRuntimeLandData(entry);
    }

    /// <summary>Reads runtime landscape data for many entries, keyed by FormID.</summary>
    public Dictionary<uint, RuntimeLoadedLandData> ReadAllRuntimeLandData(
        IEnumerable<RuntimeEditorIdEntry> entries)
    {
        return _world.ReadAllRuntimeLandData(entries);
    }

    /// <summary>Probes the runtime dialogue-topic struct layout, returning the detected variant.</summary>
    public int ProbeDialTopicLayout(RuntimeEditorIdEntry entry)
    {
        return _world.ProbeDialTopicLayout(entry);
    }

    /// <summary>Reads the runtime placed-object reference (REFR) for the given DMP entry.</summary>
    public ExtractedRefrRecord? ReadRuntimeRefr(RuntimeEditorIdEntry entry)
    {
        return _refrs.ReadRuntimeRefr(entry);
    }

    /// <summary>Reads runtime placed-object references for many entries, keyed by FormID.</summary>
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

    /// <summary>Reads each worldspace's runtime cell map (grid coordinate to cell), keyed by worldspace FormID.</summary>
    public Dictionary<uint, RuntimeWorldspaceData> ReadAllWorldspaceCellMaps(
        IEnumerable<RuntimeEditorIdEntry> entries)
    {
        return _cells.ReadAllWorldspaceCellMaps(entries);
    }

    /// <summary>Reads the runtime worldspace (WRLD) record for the given DMP entry.</summary>
    public WorldspaceRecord? ReadRuntimeWorldspace(RuntimeEditorIdEntry entry)
    {
        return _cells.ReadRuntimeWorldspace(entry);
    }

    /// <summary>Reads the runtime cell (CELL) record for the given DMP entry.</summary>
    public CellRecord? ReadRuntimeCell(RuntimeEditorIdEntry entry)
    {
        return _cells.ReadRuntimeCell(entry);
    }

    /// <summary>Reads the runtime cell (CELL) record for a cell-map entry, with optional editor ID and display name.</summary>
    public CellRecord? ReadRuntimeCell(RuntimeCellMapEntry entry, string? editorId = null, string? displayName = null)
    {
        return _cells.ReadRuntimeCell(entry, editorId, displayName);
    }

    #endregion

    #region Collections

    /// <summary>Reads the runtime form-list record for the given DMP entry, or null if it can't be read.</summary>
    public FormListRecord? ReadRuntimeFormList(RuntimeEditorIdEntry entry)
    {
        return _collections.ReadRuntimeFormList(entry);
    }

    /// <summary>Reads the runtime leveled-list record for the given DMP entry, or null if it can't be read.</summary>
    public LeveledListRecord? ReadRuntimeLeveledList(RuntimeEditorIdEntry entry)
    {
        return _collections.ReadRuntimeLeveledList(entry);
    }

    #endregion

    #region World Objects

    /// <summary>Reads the runtime activator record for the given DMP entry, or null if it can't be read.</summary>
    public ActivatorRecord? ReadRuntimeActivator(RuntimeEditorIdEntry entry)
    {
        return _worldObjects.ReadRuntimeActivator(entry);
    }

    /// <summary>Reads the runtime light record for the given DMP entry, or null if it can't be read.</summary>
    public LightRecord? ReadRuntimeLight(RuntimeEditorIdEntry entry)
    {
        return _worldObjects.ReadRuntimeLight(entry);
    }

    /// <summary>Reads the runtime door record for the given DMP entry, or null if it can't be read.</summary>
    public DoorRecord? ReadRuntimeDoor(RuntimeEditorIdEntry entry)
    {
        return _worldObjects.ReadRuntimeDoor(entry);
    }

    /// <summary>Reads the runtime static-object record for the given DMP entry, or null if it can't be read.</summary>
    public StaticRecord? ReadRuntimeStatic(RuntimeEditorIdEntry entry)
    {
        return _worldObjects.ReadRuntimeStatic(entry);
    }

    /// <summary>Reads the runtime furniture record for the given DMP entry, or null if it can't be read.</summary>
    public FurnitureRecord? ReadRuntimeFurniture(RuntimeEditorIdEntry entry)
    {
        return _worldObjects.ReadRuntimeFurniture(entry);
    }

    #endregion
}
