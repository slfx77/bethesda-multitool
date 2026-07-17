using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using EsmStringUtils = BethesdaMultitool.Core.Utils.EsmStringUtils;

namespace BethesdaMultitool.Core.Formats.Esm.Reporting;

/// <summary>
///     Emits one machine-readable event for every standalone SCPT that is actually written
///     with SCTX. Source identity comes only from the ScriptRecord being emitted by the
///     current conversion; retained-master/augmentation paths never manufacture a DMP
///     source identity from an EditorID.
/// </summary>
internal static class ScriptEmissionProvenanceReporter
{
    internal const string EventCode = "script.source-provenance";
    private static readonly Dictionary<string, string> FunctionNameNormalizationMap =
        ScriptComparer.BuildFunctionNameNormalizationMap();

    internal static void ReportNewScript(
        IConversionProgressSink sink,
        ScriptRecord source,
        uint emittedFormId,
        IReadOnlyList<EncodedSubrecord> emittedSubrecords)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(emittedSubrecords);

        var subrecords = emittedSubrecords
            .Select(static subrecord => new ScriptSubrecord(subrecord.Signature, subrecord.Bytes))
            .ToArray();
        if (!TryGetSingle(subrecords, "SCTX", out var sctx))
        {
            return;
        }

        var sourceOrigin = source.SourceTextOrigin switch
        {
            ScriptSourceTextOrigin.DmpFragment => "dmp-fragment",
            ScriptSourceTextOrigin.RuntimeSameObject => "runtime-same-object",
            _ => "unattributed-same-dump",
        };
        var scda = GetOptionalSingle(subrecords, "SCDA");
        var bytecodeChanged = !ByteArraysEqual(source.CompiledData, scda);
        var tablesChanged = TablesDifferFromModel(source, subrecords);
        var capturedSource = source.SourceText ?? string.Empty;
        var expectedSctx = EncodeCapturedSource(capturedSource);
        var correspondence = BuildCapturedCorrespondenceProof(source, scda);

        Emit(
            sink,
            source.FormId == 0 ? null : source.FormId,
            emittedFormId,
            source.EditorId,
            sourceOrigin,
            sctx,
            scda,
            bytecodeChanged,
            tablesChanged,
            new SourceProof(
                "captured-exact",
                expectedSctx,
                expectedSctx,
                0,
                null,
                Encoding.UTF8.GetBytes(capturedSource),
                correspondence.Kind,
                correspondence.MatchCount,
                correspondence.ToleratedCount,
                correspondence.ToleratedCategories));
    }

    internal static void ReportAugmentedMasterScript(
        IConversionProgressSink sink,
        ParsedMainRecord master,
        IReadOnlyList<ParsedSubrecord> emittedSubrecords,
        IReadOnlyList<ScriptVariableAugmentation> augmentations)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(emittedSubrecords);
        ArgumentNullException.ThrowIfNull(augmentations);

        var final = emittedSubrecords
            .Select(static subrecord => new ScriptSubrecord(subrecord.Signature, subrecord.Data))
            .ToArray();
        if (!TryGetSingle(final, "SCTX", out var sctx))
        {
            return;
        }

        var original = master.Subrecords
            .Select(static subrecord => new ScriptSubrecord(subrecord.Signature, subrecord.Data))
            .ToArray();
        var finalScda = GetOptionalSingle(final, "SCDA");
        var originalScda = GetOptionalSingle(original, "SCDA");
        if (!TryGetSingle(original, "SCTX", out var originalSctx))
        {
            throw new InvalidOperationException(
                $"Cannot report augmented SCPT 0x{master.Header.FormId:X8}: master has no unique SCTX.");
        }

        var proof = BuildAugmentationProof(originalSctx, augmentations);
        if (!ByteArraysEqual(sctx, proof.ExpectedSctx))
        {
            throw new InvalidOperationException(
                $"Cannot report augmented SCPT 0x{master.Header.FormId:X8}: emitted SCTX does not "
                + "equal the retained master source plus its planned fresh-local declarations.");
        }

        if (!ByteArraysEqual(originalScda, finalScda))
        {
            throw new InvalidOperationException(
                $"Cannot report augmented SCPT 0x{master.Header.FormId:X8}: append-only local "
                + "augmentation changed retained master SCDA.");
        }

        // An augmentation has no standalone SCPT capture supplying its SCTX. Its text starts
        // from this exact master record and receives same-dump declarations. Keep SourceFormId
        // null rather than pretending the target master FormID identifies a captured source.
        Emit(
            sink,
            sourceFormId: null,
            master.Header.FormId,
            master.EditorId,
            "augmentation",
            sctx,
            finalScda,
            !ByteArraysEqual(originalScda, finalScda),
            !TablePayloadsEqual(original, final),
            proof);
    }

    private static void Emit(
        IConversionProgressSink sink,
        uint? sourceFormId,
        uint emittedFormId,
        string? editorId,
        string sourceOrigin,
        byte[] sctx,
        byte[]? scda,
        bool bytecodeChanged,
        bool tablesChanged,
        SourceProof proof)
    {
        var declarationBytes = proof.AugmentationDeclarations;
        sink.Info(
            "Merging top-level records",
            $"Emitted SCPT 0x{emittedFormId:X8} with attributable source text ({sourceOrigin}).",
            "SCPT",
            emittedFormId,
            EventCode,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["source-form-id"] = FormatFormId(sourceFormId),
                ["emitted-form-id"] = FormatFormId(emittedFormId),
                ["editor-id"] = editorId,
                ["source-origin"] = sourceOrigin,
                ["sctx-proof-kind"] = proof.Kind,
                ["sctx-sha256"] = Convert.ToHexString(SHA256.HashData(sctx)),
                ["expected-sctx-sha256"] = Convert.ToHexString(SHA256.HashData(proof.ExpectedSctx)),
                ["base-sctx-sha256"] = Convert.ToHexString(SHA256.HashData(proof.BaseSctx)),
                ["captured-source-utf8-sha256"] = proof.CapturedSourceUtf8 is null
                    ? null
                    : Convert.ToHexString(SHA256.HashData(proof.CapturedSourceUtf8)),
                ["augmentation-declaration-count"] = proof.AugmentationDeclarationCount
                    .ToString(CultureInfo.InvariantCulture),
                ["augmentation-declarations-base64"] = declarationBytes is null
                    ? null
                    : Convert.ToBase64String(declarationBytes),
                ["augmentation-declarations-sha256"] = declarationBytes is null
                    ? null
                    : Convert.ToHexString(SHA256.HashData(declarationBytes)),
                ["sctx-decoded-length"] = GetDecodedLength(sctx).ToString(CultureInfo.InvariantCulture),
                ["scda-sha256"] = scda is null ? null : Convert.ToHexString(SHA256.HashData(scda)),
                ["scda-length"] = (scda?.Length ?? 0).ToString(CultureInfo.InvariantCulture),
                ["bytecode-changed-from-source"] = bytecodeChanged ? "true" : "false",
                ["tables-changed-from-source"] = tablesChanged ? "true" : "false",
                ["sctx-scda-semantic-match"] = proof.SemanticMatch,
                ["sctx-scda-match-count"] = proof.ComparisonMatchCount?.ToString(CultureInfo.InvariantCulture),
                ["sctx-scda-tolerated-count"] = proof.ComparisonToleratedCount?.ToString(CultureInfo.InvariantCulture),
                ["sctx-scda-tolerated-categories"] = proof.ComparisonToleratedCategories,
            });
    }

    private static CorrespondenceProof BuildCapturedCorrespondenceProof(
        ScriptRecord source,
        byte[]? emittedScda)
    {
        if (emittedScda is null)
        {
            if (source.CompiledData is { Length: > 0 })
            {
                throw new InvalidOperationException(
                    $"Cannot report SCPT 0x{source.FormId:X8} as source-only: its model has compiled bytecode but the emitted record has no unique SCDA.");
            }

            return new CorrespondenceProof("source-only-no-scda", null, null, null);
        }

        if (source.CompiledData is not { Length: > 0 }
            || string.IsNullOrWhiteSpace(source.SourceText)
            || string.IsNullOrWhiteSpace(source.DecompiledText))
        {
            throw new InvalidOperationException(
                $"Cannot report compiled SCPT 0x{source.FormId:X8} with SCTX: source/SCDA correspondence was not evaluated.");
        }

        // Certification must not claim stronger proof than the actual emission gate.
        // Re-run the shared contract so header/table consistency, script identity, and
        // declaration storage are covered in addition to executable-line comparison.
        var contract = CapturedScriptEmissionContract.EvaluateStandalone(source);
        if (contract.BundleIssue is not null
            || contract.SourceIssue is not null
            || contract.Script.IsIncompleteExecutableBundle
            || !string.Equals(contract.Script.SourceText, source.SourceText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot report compiled SCPT 0x{source.FormId:X8} with SCTX: "
                + $"the shared emission contract rejected its proof "
                + $"({contract.BundleIssue ?? contract.SourceIssue ?? "source was not retained"}).");
        }

        var safety = ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(
            source.CompiledData,
            source.IsBigEndian,
            source.Variables,
            source.ReferencedObjects);
        if (!safety.IsSafeForEmission)
        {
            throw new InvalidOperationException(
                $"Cannot report compiled SCPT 0x{source.FormId:X8} with SCTX: "
                + $"the captured bytecode walk is unsafe ({string.Join('|', safety.Diagnostics)}).");
        }

        var comparison = ScriptComparer.CompareScripts(
            source.SourceText,
            source.DecompiledText,
            FunctionNameNormalizationMap);
        if (comparison.TotalMismatches != 0)
        {
            var categories = FormatCounts(comparison.MismatchesByCategory);
            throw new InvalidOperationException(
                $"Cannot report compiled SCPT 0x{source.FormId:X8} with SCTX: "
                + $"source/SCDA comparison has {comparison.TotalMismatches} non-tolerated mismatch(es) ({categories}).");
        }

        return new CorrespondenceProof(
            "proven-zero-nontolerated-mismatches",
            comparison.MatchCount,
            comparison.TotalTolerated,
            FormatCounts(comparison.ToleratedDifferences));
    }

    private static SourceProof BuildAugmentationProof(
        byte[] masterSctx,
        IReadOnlyList<ScriptVariableAugmentation> augmentations)
    {
        if (augmentations.Count == 0)
        {
            throw new InvalidOperationException(
                "Master-script source augmentation provenance requires at least one declaration.");
        }

        var nulIndex = Array.IndexOf(masterSctx, (byte)0);
        var textLength = nulIndex >= 0 ? nulIndex : masterSctx.Length;
        var source = EsmStringUtils.DecodeGameText(masterSctx.AsSpan(0, textLength));
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var declarations = string.Join(
            newline,
            augmentations.Select(static augmentation =>
                $"{DeclarationKeyword(ScriptVariableDeclarationIdentity.ForFreshLocal(augmentation.DeclarationKind))} "
                + augmentation.Variable.Name));
        var declarationBytes = EsmStringUtils.EncodeGameText(declarations);

        var insertionIndex = FindFirstBeginLine(source);
        string augmentedSource;
        if (insertionIndex >= 0)
        {
            augmentedSource = source.Insert(insertionIndex, declarations + newline);
        }
        else
        {
            var separator = source.Length == 0 || source.EndsWith('\n') || source.EndsWith('\r')
                ? string.Empty
                : newline;
            augmentedSource = source + separator + declarations;
        }

        var encoded = EsmStringUtils.EncodeGameText(augmentedSource);
        byte[] expected;
        if (nulIndex < 0)
        {
            expected = encoded;
        }
        else
        {
            expected = new byte[encoded.Length + masterSctx.Length - nulIndex];
            encoded.CopyTo(expected, 0);
            masterSctx.AsSpan(nulIndex).CopyTo(expected.AsSpan(encoded.Length));
        }

        return new SourceProof(
            "master-plus-declarations",
            expected,
            masterSctx,
            augmentations.Count,
            declarationBytes,
            null,
            "master-base-plus-fresh-local-declarations",
            null,
            null,
            null);
    }

    private static byte[] EncodeCapturedSource(string source)
    {
        var encoded = EsmStringUtils.EncodeGameText(source);
        var expected = new byte[encoded.Length + 1];
        encoded.CopyTo(expected, 0);
        return expected;
    }

    private static string DeclarationKeyword(ScriptVariableDeclarationKind kind) => kind switch
    {
        ScriptVariableDeclarationKind.Short => "short",
        ScriptVariableDeclarationKind.Long => "long",
        ScriptVariableDeclarationKind.Int => "int",
        ScriptVariableDeclarationKind.Float => "float",
        ScriptVariableDeclarationKind.Reference => "ref",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static int FindFirstBeginLine(string source)
    {
        var lineStart = 0;
        while (lineStart < source.Length)
        {
            var lineEnd = source.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = source.Length;
            }

            var contentStart = lineStart;
            while (contentStart < lineEnd && char.IsWhiteSpace(source[contentStart]))
            {
                contentStart++;
            }

            const string begin = "begin";
            if (lineEnd - contentStart >= begin.Length
                && source.AsSpan(contentStart, begin.Length).Equals(begin, StringComparison.OrdinalIgnoreCase)
                && (contentStart + begin.Length == lineEnd
                    || char.IsWhiteSpace(source[contentStart + begin.Length])))
            {
                return lineStart;
            }

            if (lineEnd == source.Length)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        return -1;
    }

    private static bool TablesDifferFromModel(
        ScriptRecord source,
        IReadOnlyList<ScriptSubrecord> emitted)
    {
        var variables = new List<ScriptVariableInfo>();
        ScriptVariableInfo? pending = null;
        var references = new List<uint>();
        foreach (var subrecord in emitted)
        {
            switch (subrecord.Signature)
            {
                case "SLSD" when subrecord.Data.Length >= 17:
                    if (pending is not null)
                    {
                        variables.Add(pending);
                    }

                    pending = new ScriptVariableInfo(
                        BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data),
                        null,
                        subrecord.Data[16]);
                    break;

                case "SCVR" when pending is not null:
                    variables.Add(pending with { Name = DecodeNullTerminated(subrecord.Data) });
                    pending = null;
                    break;

                case "SCRO" when subrecord.Data.Length >= 4:
                    references.Add(BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data));
                    break;

                case "SCRV" when subrecord.Data.Length >= 4:
                    references.Add(0x80000000u |
                                   BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data));
                    break;
            }
        }

        if (pending is not null)
        {
            variables.Add(pending);
        }

        if (variables.Count != source.Variables.Count
            || references.Count != source.ReferencedObjects.Count)
        {
            return true;
        }

        for (var i = 0; i < variables.Count; i++)
        {
            var emittedVariable = variables[i];
            var sourceVariable = source.Variables[i];
            if (emittedVariable.Index != sourceVariable.Index
                || emittedVariable.Type != sourceVariable.Type
                || !string.Equals(
                    NormalizeEmpty(emittedVariable.Name),
                    NormalizeEmpty(sourceVariable.Name),
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return !references.SequenceEqual(source.ReferencedObjects);
    }

    private static bool TablePayloadsEqual(
        IReadOnlyList<ScriptSubrecord> left,
        IReadOnlyList<ScriptSubrecord> right)
    {
        var leftTables = left.Where(IsTableSubrecord).ToArray();
        var rightTables = right.Where(IsTableSubrecord).ToArray();
        return leftTables.Length == rightTables.Length
               && leftTables.Zip(rightTables).All(static pair =>
                   pair.First.Signature == pair.Second.Signature
                   && pair.First.Data.AsSpan().SequenceEqual(pair.Second.Data));
    }

    private static bool IsTableSubrecord(ScriptSubrecord subrecord) =>
        subrecord.Signature is "SLSD" or "SCVR" or "SCRO" or "SCRV";

    private static bool TryGetSingle(
        IReadOnlyList<ScriptSubrecord> subrecords,
        string signature,
        out byte[] data)
    {
        var matches = subrecords.Where(subrecord => subrecord.Signature == signature).ToArray();
        if (matches.Length == 1)
        {
            data = matches[0].Data;
            return true;
        }

        data = [];
        return false;
    }

    private static byte[]? GetOptionalSingle(
        IReadOnlyList<ScriptSubrecord> subrecords,
        string signature)
    {
        var matches = subrecords.Where(subrecord => subrecord.Signature == signature).ToArray();
        return matches.Length == 1 ? matches[0].Data : null;
    }

    private static bool ByteArraysEqual(byte[]? left, byte[]? right) =>
        ReferenceEquals(left, right)
        || left is not null && right is not null && left.AsSpan().SequenceEqual(right);

    private static int GetDecodedLength(byte[] payload)
    {
        var nul = Array.IndexOf(payload, (byte)0);
        var length = nul >= 0 ? nul : payload.Length;
        return EsmStringUtils.DecodeGameText(payload.AsSpan(0, length)).Length;
    }

    private static string DecodeNullTerminated(byte[] payload)
    {
        var nul = Array.IndexOf(payload, (byte)0);
        return EsmStringUtils.DecodeGameText(payload.AsSpan(0, nul >= 0 ? nul : payload.Length));
    }

    private static string? NormalizeEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static string FormatCounts(IReadOnlyDictionary<string, int> counts) => string.Join(
        '|',
        counts.OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => $"{item.Key}={item.Value}"));

    private static string? FormatFormId(uint? formId) =>
        formId is null or 0 ? null : $"0x{formId.Value:X8}";

    private readonly record struct ScriptSubrecord(string Signature, byte[] Data);
    private readonly record struct SourceProof(
        string Kind,
        byte[] ExpectedSctx,
        byte[] BaseSctx,
        int AugmentationDeclarationCount,
        byte[]? AugmentationDeclarations,
        byte[]? CapturedSourceUtf8,
        string SemanticMatch,
        int? ComparisonMatchCount,
        int? ComparisonToleratedCount,
        string? ComparisonToleratedCategories);

    private readonly record struct CorrespondenceProof(
        string Kind,
        int? MatchCount,
        int? ToleratedCount,
        string? ToleratedCategories);
}
