namespace FalloutXbox360Utils.Core.Json;

/// <summary>
///     Summary of ESM records found in the dump.
/// </summary>
public sealed class JsonEsmRecordSummary
{
    // Original subrecord counts
    public int EdidCount { get; set; }
    public int GmstCount { get; set; }
    public int SctxCount { get; set; }
    public int ScroCount { get; set; }

    // Main record detection
    public int MainRecordCount { get; set; }
    public int LittleEndianRecords { get; set; }
    public int BigEndianRecords { get; set; }
    public Dictionary<string, int> MainRecordTypes { get; set; } = [];

    // Extended subrecords
    public int NameRefCount { get; set; }
    public int PositionCount { get; set; }
    public int ActorBaseCount { get; set; }

    // Dialogue subrecords
    public int Nam1Count { get; set; }
    public int TrdtCount { get; set; }

    // Text subrecords
    public int FullNameCount { get; set; }
    public int DescriptionCount { get; set; }
    public int ModelPathCount { get; set; }
    public int IconPathCount { get; set; }
    public int TexturePathCount { get; set; }

    // FormID reference subrecords
    public int ScriptRefCount { get; set; }
    public int EffectRefCount { get; set; }
    public int SoundRefCount { get; set; }
    public int QuestRefCount { get; set; }

    // Conditions
    public int ConditionCount { get; set; }

    // Terrain/worldspace data
    public int HeightmapCount { get; set; }
    public int CellGridCount { get; set; }

    // Generic schema-defined subrecords
    public int GenericSubrecordCount { get; set; }
    public Dictionary<string, int> GenericSubrecordTypes { get; set; } = [];
}
