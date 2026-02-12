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
    internal readonly RuntimeMemoryContext _context;
    private readonly RuntimeActorReader _actors;
    private readonly RuntimeItemReader _items;
    private readonly RuntimeDialogueReader _dialogue;
    private readonly RuntimeEffectReader _effects;
    private readonly RuntimeScriptReader _scripts;
    private readonly RuntimeWorldReader _world;

    public RuntimeStructReader(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo)
    {
        _context = new RuntimeMemoryContext(accessor, fileSize, minidumpInfo);
        _actors = new RuntimeActorReader(_context);
        _items = new RuntimeItemReader(_context);
        _dialogue = new RuntimeDialogueReader(_context);
        _effects = new RuntimeEffectReader(_context);
        _scripts = new RuntimeScriptReader(_context);
        _world = new RuntimeWorldReader(_context);
    }

    #region Actors

    public NpcRecord? ReadRuntimeNpc(RuntimeEditorIdEntry entry) => _actors.ReadRuntimeNpc(entry);
    public CreatureRecord? ReadRuntimeCreature(RuntimeEditorIdEntry entry) => _actors.ReadRuntimeCreature(entry);
    public FactionRecord? ReadRuntimeFaction(RuntimeEditorIdEntry entry) => _actors.ReadRuntimeFaction(entry);

    #endregion

    #region Items

    public WeaponRecord? ReadRuntimeWeapon(RuntimeEditorIdEntry entry) => _items.ReadRuntimeWeapon(entry);
    public ArmorRecord? ReadRuntimeArmor(RuntimeEditorIdEntry entry) => _items.ReadRuntimeArmor(entry);
    public AmmoRecord? ReadRuntimeAmmo(RuntimeEditorIdEntry entry) => _items.ReadRuntimeAmmo(entry);
    public ConsumableRecord? ReadRuntimeConsumable(RuntimeEditorIdEntry entry) => _items.ReadRuntimeConsumable(entry);
    public MiscItemRecord? ReadRuntimeMiscItem(RuntimeEditorIdEntry entry) => _items.ReadRuntimeMiscItem(entry);
    public KeyRecord? ReadRuntimeKey(RuntimeEditorIdEntry entry) => _items.ReadRuntimeKey(entry);
    public ContainerRecord? ReadRuntimeContainer(RuntimeEditorIdEntry entry) => _items.ReadRuntimeContainer(entry);

    #endregion

    #region Dialogue & Text

    public RuntimeDialogTopicInfo? ReadRuntimeDialogTopic(RuntimeEditorIdEntry entry) =>
        _dialogue.ReadRuntimeDialogTopic(entry);

    public RuntimeDialogueInfo? ReadRuntimeDialogueInfo(RuntimeEditorIdEntry entry) =>
        _dialogue.ReadRuntimeDialogueInfo(entry);

    public RuntimeDialogueInfo? ReadRuntimeDialogueInfoFromVA(uint va) =>
        _dialogue.ReadRuntimeDialogueInfoFromVA(va);

    public QuestRecord? ReadRuntimeQuest(RuntimeEditorIdEntry entry) => _dialogue.ReadRuntimeQuest(entry);
    public TerminalRecord? ReadRuntimeTerminal(RuntimeEditorIdEntry entry) => _dialogue.ReadRuntimeTerminal(entry);
    public NoteRecord? ReadRuntimeNote(RuntimeEditorIdEntry entry) => _dialogue.ReadRuntimeNote(entry);

    public List<TopicQuestLink> WalkTopicQuestInfoList(RuntimeEditorIdEntry entry) =>
        _dialogue.WalkTopicQuestInfoList(entry);

    #endregion

    #region Effects

    public ProjectilePhysicsData? ReadProjectilePhysics(long fileOffset, uint expectedFormId) =>
        _effects.ReadProjectilePhysics(fileOffset, expectedFormId);

    #endregion

    #region Scripts

    public RuntimeScriptData? ReadRuntimeScript(RuntimeEditorIdEntry entry) => _scripts.ReadRuntimeScript(entry);

    #endregion

    #region World

    public RuntimeLoadedLandData? ReadRuntimeLandData(RuntimeEditorIdEntry entry) =>
        _world.ReadRuntimeLandData(entry);

    public Dictionary<uint, RuntimeLoadedLandData> ReadAllRuntimeLandData(
        IEnumerable<RuntimeEditorIdEntry> entries) =>
        _world.ReadAllRuntimeLandData(entries);

    public int ProbeDialTopicLayout(RuntimeEditorIdEntry entry) => _world.ProbeDialTopicLayout(entry);

    #endregion

    #region Strings (shared utility)

    public string? ReadBSStringT(long tesFormFileOffset, int fieldOffset) =>
        _context.ReadBSStringT(tesFormFileOffset, fieldOffset);

    #endregion
}
