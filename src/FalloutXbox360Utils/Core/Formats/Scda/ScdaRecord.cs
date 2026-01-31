namespace FalloutXbox360Utils.Core.Formats.Scda;

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

    public int BytecodeSize => Bytecode.Length;
    public int BytecodeLength => Bytecode.Length;
    public bool HasAssociatedSctx => !string.IsNullOrEmpty(SourceText);
}
