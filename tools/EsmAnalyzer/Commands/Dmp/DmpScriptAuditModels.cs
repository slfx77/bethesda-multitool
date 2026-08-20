namespace EsmAnalyzer.Commands.Dmp;

internal sealed record DmpScriptAuditReport(
    IReadOnlyList<DmpScriptAuditRow> Rows,
    int HardContradictionCount);

internal sealed record DmpScriptAuditRow
{
    internal required string RowKind { get; init; }
    internal required uint FormId { get; init; }
    internal required int CopyOrdinal { get; init; }
    internal required long DumpOffset { get; init; }
    internal string? EditorId { get; init; }
    internal required int RuntimeCopyCount { get; init; }
    internal required string RuntimeCopyStatus { get; init; }
    internal required string ContentClassification { get; init; }
    internal required int SourceCharLength { get; init; }
    internal required int SourceUtf8Length { get; init; }
    internal string? SourceSha256 { get; init; }
    internal string? SourceTerminatedProof { get; init; }
    internal required int ScdaLength { get; init; }
    internal string? ScdaSha256 { get; init; }
    internal required uint DeclaredScdaLength { get; init; }
    internal required bool DeclaredScdaLengthMatches { get; init; }
    internal required uint HeaderVariableCount { get; init; }
    internal required uint EffectiveVariableCount { get; init; }
    internal required int VariableListCount { get; init; }
    internal bool? VariableMetadataComplete { get; init; }
    internal bool? VariablesComplete { get; init; }
    internal required bool HeaderVariableCountMatchesList { get; init; }
    internal required bool EffectiveVariableCountMatchesList { get; init; }
    internal required uint HeaderReferenceCount { get; init; }
    internal required int ReferenceListCount { get; init; }
    internal bool? ReferencesComplete { get; init; }
    internal required bool HeaderReferenceCountMatchesList { get; init; }
    internal required string DeclarationIdentityVerdict { get; init; }
    internal required int DeclarationCount { get; init; }
    internal required string DeclarationIdentities { get; init; }
    internal required string SlsdIdentities { get; init; }
    internal required string DeclarationIdentityDetails { get; init; }
    internal required int SourceStatementCount { get; init; }
    internal required int DecompiledStatementCount { get; init; }
    internal required string ComparisonStatus { get; init; }
    internal required int ComparisonMatchCount { get; init; }
    internal required int ComparisonMismatchCount { get; init; }
    internal required int ComparisonToleratedCount { get; init; }
    internal required string ComparisonMismatchCategories { get; init; }
    internal required string ComparisonToleratedCategories { get; init; }
    internal double? ComparisonMatchRate { get; init; }
    internal required bool ProvenTrivialScda { get; init; }
    internal required string StructuralDiagnostics { get; init; }
    internal required string HardContradictions { get; init; }
}