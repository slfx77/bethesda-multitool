namespace Xbox360MemoryCarver.Core.Formats.Scda;

/// <summary>
///     Parsed SCDA record with bytecode and optional source text.
/// </summary>
public record ScdaRecord
{
    public required long Offset { get; init; }
    public required byte[] Bytecode { get; init; }
    public string? SourceText { get; init; }
    public long SourceOffset { get; init; }
    public List<uint> FormIdReferences { get; init; } = [];

    /// <summary>
    ///     Script/quest name extracted from source text (populated during grouping).
    /// </summary>
    public string? ScriptName { get; set; }

    // Computed properties - SonarQube S2325 incorrectly suggests these be static
#pragma warning disable S2325
    public int BytecodeSize => Bytecode.Length;
    public int BytecodeLength => Bytecode.Length;
    public bool HasAssociatedSctx => !string.IsNullOrEmpty(SourceText);
#pragma warning restore S2325
}

/// <summary>
///     Results from scanning a dump for SCDA records.
/// </summary>
public record ScdaScanResult
{
    public List<ScdaRecord> Records { get; init; } = [];
}

/// <summary>
///     Summary of script extraction results.
/// </summary>
public record ScdaExtractionResult
{
    public int TotalRecords { get; init; }
    public int GroupedQuests { get; init; }
    public int UngroupedScripts { get; init; }
    public int TotalBytecodeBytes { get; init; }
    public int RecordsWithSource { get; init; }

    /// <summary>
    ///     List of extracted scripts with their names and offsets.
    /// </summary>
    public List<ScriptInfo> Scripts { get; init; } = [];
}

/// <summary>
///     Information about an extracted script for analysis output.
/// </summary>
public record ScriptInfo
{
    public required long Offset { get; init; }
    public required int BytecodeSize { get; init; }
    public string? ScriptName { get; init; }
    public string? QuestName { get; init; }
    public bool HasSource { get; init; }
}
