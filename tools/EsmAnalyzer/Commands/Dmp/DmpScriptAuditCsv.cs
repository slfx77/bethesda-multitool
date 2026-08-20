using System.Globalization;
using System.Text;

namespace EsmAnalyzer.Commands.Dmp;

internal static class DmpScriptAuditCsv
{
    private static readonly string[] Header =
    [
        "row_kind", "form_id", "copy_ordinal", "dump_offset", "editor_id",
        "runtime_copy_count", "runtime_copy_status", "content_classification",
        "source_char_length", "source_utf8_length", "source_sha256", "source_terminated_proof",
        "scda_length", "scda_sha256", "declared_scda_length", "declared_scda_length_matches",
        "header_variable_count", "effective_variable_count", "variable_list_count",
        "variable_metadata_complete", "variables_complete", "header_variable_count_matches_list",
        "effective_variable_count_matches_list", "header_reference_count", "reference_list_count",
        "references_complete", "header_reference_count_matches_list", "declaration_identity_verdict",
        "declaration_count", "declaration_identities", "slsd_identities", "declaration_identity_details",
        "source_statement_count", "decompiled_statement_count", "comparison_status",
        "comparison_match_count", "comparison_mismatch_count", "comparison_tolerated_count",
        "comparison_mismatch_categories", "comparison_tolerated_categories", "comparison_match_rate",
        "proven_trivial_scda", "structural_diagnostics", "hard_contradictions"
    ];

    internal static void Write(string path, IReadOnlyList<DmpScriptAuditRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine(string.Join(',', Header));
        foreach (var row in rows)
        {
            writer.WriteLine(Serialize(row));
        }
    }

    internal static string Serialize(DmpScriptAuditRow row)
    {
        string[] values =
        [
            row.RowKind,
            $"0x{row.FormId:X8}",
            Invariant(row.CopyOrdinal),
            $"0x{row.DumpOffset:X}",
            row.EditorId ?? string.Empty,
            Invariant(row.RuntimeCopyCount),
            row.RuntimeCopyStatus,
            row.ContentClassification,
            Invariant(row.SourceCharLength),
            Invariant(row.SourceUtf8Length),
            row.SourceSha256 ?? string.Empty,
            row.SourceTerminatedProof ?? string.Empty,
            Invariant(row.ScdaLength),
            row.ScdaSha256 ?? string.Empty,
            Invariant(row.DeclaredScdaLength),
            Bool(row.DeclaredScdaLengthMatches),
            Invariant(row.HeaderVariableCount),
            Invariant(row.EffectiveVariableCount),
            Invariant(row.VariableListCount),
            NullableBool(row.VariableMetadataComplete),
            NullableBool(row.VariablesComplete),
            Bool(row.HeaderVariableCountMatchesList),
            Bool(row.EffectiveVariableCountMatchesList),
            Invariant(row.HeaderReferenceCount),
            Invariant(row.ReferenceListCount),
            NullableBool(row.ReferencesComplete),
            Bool(row.HeaderReferenceCountMatchesList),
            row.DeclarationIdentityVerdict,
            Invariant(row.DeclarationCount),
            row.DeclarationIdentities,
            row.SlsdIdentities,
            row.DeclarationIdentityDetails,
            Invariant(row.SourceStatementCount),
            Invariant(row.DecompiledStatementCount),
            row.ComparisonStatus,
            Invariant(row.ComparisonMatchCount),
            Invariant(row.ComparisonMismatchCount),
            Invariant(row.ComparisonToleratedCount),
            row.ComparisonMismatchCategories,
            row.ComparisonToleratedCategories,
            row.ComparisonMatchRate?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty,
            Bool(row.ProvenTrivialScda),
            row.StructuralDiagnostics,
            row.HardContradictions
        ];
        return string.Join(',', values.Select(Escape));
    }

    private static string Escape(string value)
    {
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\r') && !value.Contains('\n'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string Bool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string NullableBool(bool? value)
    {
        return value.HasValue ? Bool(value.Value) : string.Empty;
    }

    private static string Invariant<T>(T value) where T : IFormattable
    {
        return value.ToString(null, CultureInfo.InvariantCulture);
    }
}