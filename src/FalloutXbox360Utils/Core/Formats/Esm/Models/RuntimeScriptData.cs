using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Runtime script data read from a Script C++ struct in Xbox 360 memory.
///     Fields informed by PDB Script class layout (84 bytes PDB, 100 bytes runtime).
/// </summary>
public record RuntimeScriptData
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    // SCRIPT_HEADER (20 bytes at runtime offset 40)
    public uint VariableCount { get; init; }
    public uint RefObjectCount { get; init; }
    public uint DataSize { get; init; }
    public uint LastVariableId { get; init; }
    public bool IsQuestScript { get; init; }
    public bool IsMagicEffectScript { get; init; }
    public bool IsCompiled { get; init; }

    // Runtime pointers followed
    public string? SourceText { get; init; }
    public byte[]? CompiledData { get; init; }
    public uint? OwnerQuestFormId { get; init; }
    public float QuestScriptDelay { get; init; }

    // From BSSimpleList walks
    public List<(uint FormId, string? EditorId)> ReferencedObjects { get; init; } = [];
    public List<ScriptVariableInfo> Variables { get; init; } = [];

    public long DumpOffset { get; init; }
}
