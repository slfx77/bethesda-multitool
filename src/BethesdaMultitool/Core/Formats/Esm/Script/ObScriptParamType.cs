namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     Oblivion (TES4) script/condition parameter types — the engine's raw ParamInfo <c>typeID</c>
///     values, extracted from retail <c>Oblivion.exe</c>'s CommandInfo array
///     (<c>tools/extract_tes4_script_functions.py</c>); member names follow the engine's own
///     per-parameter display strings. This is a DIFFERENT numbering from FNV/FO3's
///     <see cref="ScriptParamType" /> — they agree only for 0..31 and diverge after
///     (TES4 32 = Birthsign vs FNV 32 = FormType), which is why TES4 tables must never be read
///     through the FNV enum.
/// </summary>
public enum ObScriptParamType : ushort
{
    String = 0,
    Integer = 1,
    Float = 2,
    InventoryObject = 3,
    ObjectRef = 4,
    ActorValue = 5,
    Actor = 6,
    SpellItem = 7,
    Axis = 8,
    Cell = 9,
    AnimGroup = 10,
    MagicItem = 11,
    Sound = 12,
    Topic = 13,
    Quest = 14,
    Race = 15,
    Class = 16,
    Faction = 17,
    Sex = 18,
    Global = 19,
    Furniture = 20,
    Object = 21,
    VariableName = 22,
    QuestStage = 23,
    MapMarker = 24,
    ActorBase = 25,
    Container = 26,
    WorldSpace = 27,
    CrimeType = 28,
    Package = 29,
    CombatStyle = 30,
    MagicEffect = 31,
    Birthsign = 32,
    FormType = 33,
    Weather = 34,
    Npc = 35,
    Owner = 36,
    EffectShader = 37,
}
