using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reader for runtime game structures in Xbox 360 memory dumps.
///     Facade that delegates to domain-specific reader classes.
/// </summary>
public sealed class RuntimeStructReader
{
    private readonly RuntimeActorReader _actors;
    private readonly RuntimeActorWeaponReader _actorWeapons;
    private readonly RuntimeCellReader _cells;
    private readonly RuntimeCollectionReader _collections;
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimeDialogueReader _dialogue;
    private readonly RuntimeEffectReader _effects;
    private readonly RuntimeGenericReader _generic;
    private readonly RuntimeItemReader _items;
    private readonly RuntimePackageReader _packages;
    private readonly RuntimeRefrReader _refrs;
    private readonly RuntimeScriptReader _scripts;
    private readonly RuntimeWorldReader _world;
    private readonly RuntimeWorldObjectReader _worldObjects;

    public RuntimeStructReader(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        bool useProtoOffsets = false)
        : this(accessor, fileSize, minidumpInfo, useProtoOffsets, null, null)
    {
    }

    internal RuntimeStructReader(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        bool useProtoOffsets,
        RuntimeNpcLayoutProbeResult? npcLayoutProbe,
        RuntimeWorldCellLayoutProbeResult? worldCellLayoutProbe = null)
    {
        IsEarlyBuild = useProtoOffsets;
        WorldCellLayoutProbe = worldCellLayoutProbe;
        _context = new RuntimeMemoryContext(accessor, fileSize, minidumpInfo);
        _actors = new RuntimeActorReader(_context, npcLayoutProbe);
        _generic = new RuntimeGenericReader(_context);
        _items = new RuntimeItemReader(_context);
        _dialogue = new RuntimeDialogueReader(_context);
        _effects = new RuntimeEffectReader(_context);
        _scripts = new RuntimeScriptReader(_context);
        _world = new RuntimeWorldReader(_context);
        _refrs = new RuntimeRefrReader(_context, useProtoOffsets);
        _packages = new RuntimePackageReader(_context);
        _actorWeapons = new RuntimeActorWeaponReader(_context);
        _cells = new RuntimeCellReader(_context, useProtoOffsets, worldCellLayoutProbe);
        _collections = new RuntimeCollectionReader(_context);
        _worldObjects = new RuntimeWorldObjectReader(_context);
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
        IReadOnlyList<RuntimeEditorIdEntry>? cellEntries = null)
    {
        var context = new RuntimeMemoryContext(accessor, fileSize, minidumpInfo);
        var isEarlyBuild = RuntimeRefrReader.ProbeIsEarlyBuild(context, refrEntries);
        var npcLayoutProbe = npcEntries is { Count: > 0 }
            ? RuntimeNpcLayoutProbe.Probe(context, npcEntries)
            : null;
        var worldCellLayoutProbe =
            (worldEntries is { Count: > 0 } || cellEntries is { Count: > 0 })
                ? RuntimeWorldCellLayoutProbe.Probe(context, worldEntries, cellEntries)
                : null;
        return new RuntimeStructReader(
            accessor,
            fileSize,
            minidumpInfo,
            isEarlyBuild,
            npcLayoutProbe,
            worldCellLayoutProbe);
    }

    #region Effects

    public ProjectilePhysicsData? ReadProjectilePhysics(long fileOffset, uint expectedFormId)
    {
        return _effects.ReadProjectilePhysics(fileOffset, expectedFormId);
    }

    #endregion

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

    public string? ReadBSStringT(long tesFormFileOffset, int fieldOffset)
    {
        return _context.ReadBSStringT(tesFormFileOffset, fieldOffset);
    }

    #endregion

    #region Generic (PDB-derived)

    public GenericEsmRecord? ReadGenericRecord(RuntimeEditorIdEntry entry)
    {
        return _generic.ReadGenericRecord(entry);
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
