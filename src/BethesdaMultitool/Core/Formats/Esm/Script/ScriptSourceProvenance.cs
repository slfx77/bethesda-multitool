using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     The one place that decides how script source text is labelled: authored plugin SCTX, text
///     recovered from a memory dump, or a BethesdaMultitool reconstruction decompiled from SCDA. Every
///     presenter (show, GECK reports, terminal/INFO detail, script export) takes its label and token from
///     here, so original source is never presented as anything else and a reconstruction is never
///     presented as original.
///     <para>
///         <see cref="Classify(ScriptRecord, bool)" /> and its overloads take an explicit
///         <c>isMemoryDumpInput</c>: <see cref="ScriptSourceTextOrigin.None" /> is the value both for a
///         plugin's own SCTX and for dump text whose origin was never attributed, so the record alone cannot
///         tell them apart. The record's own dump flag (<see cref="ScriptRecord.FromRuntime" />,
///         <see cref="DialogueResultScript.IsDmpDerived" />, <see cref="TerminalMenuItem.IsDmpDerived" />) also
///         counts as dump input, so dump text is never labelled as authored plugin source even when a
///         caller passes <c>false</c>.
///     </para>
///     <para>
///         Tokens reuse the vocabulary of <c>ScriptEmissionProvenanceReporter</c>: plugin-record,
///         dmp-fragment, runtime-same-object, unattributed-same-dump, decompiled-from-bytecode; plus
///         <c>none</c> when there is no text.
///     </para>
/// </summary>
public static class ScriptSourceProvenance
{
    /// <summary>Token for SCTX stored in a plugin record.</summary>
    public const string PluginRecordToken = "plugin-record";

    /// <summary>Token for SCTX recovered from a memory-dump fragment.</summary>
    public const string DmpFragmentToken = "dmp-fragment";

    /// <summary>Token for SCTX recovered from the same runtime Script object.</summary>
    public const string RuntimeSameObjectToken = "runtime-same-object";

    /// <summary>Token for dump text whose origin was not attributed.</summary>
    public const string UnattributedSameDumpToken = "unattributed-same-dump";

    /// <summary>Token for text decompiled from SCDA by BethesdaMultitool.</summary>
    public const string DecompiledFromBytecodeToken = "decompiled-from-bytecode";

    /// <summary>Token when the input carries no source text.</summary>
    public const string NoneToken = "none";

    public const string WithheldCapturedToken = "withheld-captured";

    /// <summary>Correspondence token for a plugin record, where the dump correspondence gate does not apply.</summary>
    public const string CorrespondenceNotApplicable = "not-applicable";

    /// <summary>Correspondence token for dump text whose model does not record a correspondence result.</summary>
    public const string CorrespondenceNotRecorded = "not-recorded";

    /// <summary>
    ///     The label for a <c>DecompiledText</c> body. Decompiled text is always a BethesdaMultitool
    ///     reconstruction and must be shown with exactly this label, never as source.
    /// </summary>
    public const string DecompiledTextLabel =
        "Reconstruction (SCDA)";

    /// <summary>
    ///     Wording for text or code a partial memory dump does not hold. A missing capture is not historical
    ///     evidence that the build lacked it.
    /// </summary>
    public const string PartialDumpAbsenceWording =
        "not present in this capture; absence from a partial memory dump is not evidence of absence from the build";

    /// <summary>Classifies a standalone SCPT record's <see cref="ScriptRecord.SourceText" />.</summary>
    /// <param name="script">The script record.</param>
    /// <param name="isMemoryDumpInput">True when the record was loaded from a memory dump.</param>
    public static ScriptSourceClassification Classify(ScriptRecord script, bool isMemoryDumpInput)
    {
        ArgumentNullException.ThrowIfNull(script);
        return Classify(
            script.SourceText,
            script.SourceTextOrigin,
            isMemoryDumpInput || script.FromRuntime,
            script.SourceTextCorrespondenceStatus);
    }

    /// <summary>Classifies an INFO result-script block's <see cref="DialogueResultScript.SourceText" />.</summary>
    /// <param name="script">The result-script block.</param>
    /// <param name="isMemoryDumpInput">True when the owning record was loaded from a memory dump.</param>
    public static ScriptSourceClassification Classify(DialogueResultScript script, bool isMemoryDumpInput)
    {
        ArgumentNullException.ThrowIfNull(script);
        return Classify(
            script.SourceText,
            script.SourceTextOrigin,
            isMemoryDumpInput || script.IsDmpDerived,
            withheldSourceReason: script.WithheldSourceReason);
    }

    /// <summary>Classifies a terminal menu item's embedded result script (<see cref="TerminalMenuItem.SourceText" />).</summary>
    /// <param name="item">The menu item.</param>
    /// <param name="isMemoryDumpInput">True when the owning TERM was loaded from a memory dump.</param>
    public static ScriptSourceClassification Classify(TerminalMenuItem item, bool isMemoryDumpInput)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Classify(
            item.SourceText,
            item.SourceTextOrigin,
            isMemoryDumpInput || item.IsDmpDerived,
            withheldSourceReason: item.WithheldSourceReason);
    }

    /// <summary>
    ///     Classifies raw source text with its recorded origin. The typed overloads call this; use it directly
    ///     only for a model that has no overload.
    /// </summary>
    /// <param name="sourceText">The source text, or null/empty when absent.</param>
    /// <param name="origin">The recorded origin of that text.</param>
    /// <param name="isMemoryDumpInput">True when the text was read from a memory dump.</param>
    /// <param name="correspondence">
    ///     The same-dump SCTX/SCDA correspondence result, when the model records one (standalone SCPT only).
    /// </param>
    public static ScriptSourceClassification Classify(
        string? sourceText,
        ScriptSourceTextOrigin origin,
        bool isMemoryDumpInput,
        ScriptSourceCorrespondenceStatus? correspondence = null,
        string? withheldSourceReason = null)
    {
        if (string.IsNullOrEmpty(sourceText) && withheldSourceReason is not null)
        {
            return new ScriptSourceClassification(ScriptTextKind.WithheldCaptured, WithheldCapturedToken,
                $"Source withheld ({withheldSourceReason})",
                isMemoryDumpInput, "rejected");
        }

        if (string.IsNullOrEmpty(sourceText))
        {
            return new ScriptSourceClassification(
                ScriptTextKind.None,
                NoneToken,
                NoSourceLabel(isMemoryDumpInput),
                isMemoryDumpInput,
                CorrespondenceNotApplicable);
        }

        var emittedReconstruction = origin == ScriptSourceTextOrigin.None && HasDecompiledEmissionBanner(sourceText);
        var kind = emittedReconstruction ? ScriptTextKind.ReconstructedDecompiled : origin switch
        {
            ScriptSourceTextOrigin.DecompiledFromBytecode => ScriptTextKind.ReconstructedDecompiled,
            ScriptSourceTextOrigin.DmpFragment => ScriptTextKind.RecoveredDmpFragment,
            ScriptSourceTextOrigin.RuntimeSameObject => ScriptTextKind.RecoveredRuntimeObject,
            _ => isMemoryDumpInput ? ScriptTextKind.UnattributedDumpText : ScriptTextKind.AuthoredPlugin
        };

        // A reconstruction's correspondence with its own bytecode is by construction, and plugin SCTX is
        // exempt from the dump gate, so neither carries a correspondence verdict.
        var dumpText = kind is ScriptTextKind.RecoveredDmpFragment or ScriptTextKind.RecoveredRuntimeObject
            or ScriptTextKind.UnattributedDumpText;
        var correspondenceToken = !dumpText
            ? CorrespondenceNotApplicable
            : correspondence is { } status
                ? FormatCorrespondence(status)
                : CorrespondenceNotRecorded;

        return new ScriptSourceClassification(
            kind,
            GetToken(kind),
            emittedReconstruction && !isMemoryDumpInput
                ? "Reconstruction (stored SCTX)"
                : BuildLabel(kind, isMemoryDumpInput),
            isMemoryDumpInput,
            correspondenceToken);
    }

    /// <summary>The stable machine token for a kind (see the class remarks for the vocabulary).</summary>
    public static string GetToken(ScriptTextKind kind)
    {
        return kind switch
        {
            ScriptTextKind.AuthoredPlugin => PluginRecordToken,
            ScriptTextKind.RecoveredDmpFragment => DmpFragmentToken,
            ScriptTextKind.RecoveredRuntimeObject => RuntimeSameObjectToken,
            ScriptTextKind.UnattributedDumpText => UnattributedSameDumpToken,
            ScriptTextKind.ReconstructedDecompiled => DecompiledFromBytecodeToken,
            ScriptTextKind.WithheldCaptured => WithheldCapturedToken,
            _ => NoneToken
        };
    }

    /// <summary>
    ///     The heading for a missing source body. For a memory dump it states that absence from a partial
    ///     capture proves nothing about the build.
    /// </summary>
    public static string NoSourceLabel(bool isMemoryDumpInput)
    {
        return isMemoryDumpInput
            ? "No source in capture"
            : "No source (SCTX)";
    }

    /// <summary>True when the script's source text is a BethesdaMultitool decompilation, not captured text.</summary>
    public static bool IsReconstructed(ScriptRecord script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return script.HasSource && (script.SourceTextOrigin == ScriptSourceTextOrigin.DecompiledFromBytecode ||
                                    HasDecompiledEmissionBanner(script.SourceText));
    }

    private static bool HasDecompiledEmissionBanner(string? sourceText)
    {
        var text = sourceText?.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return text is not null &&
               (text.StartsWith(CapturedScriptEmissionContract.DecompiledEmissionBannerFirstLine, StringComparison.Ordinal)
                || text.StartsWith(CapturedScriptEmissionContract.LegacyDecompiledEmissionBannerFirstLine, StringComparison.Ordinal)
                || text.StartsWith("; Decompiled from SCDA by BethesdaMultitool (reconstruction, not original source)", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The script's source text when a script author wrote it (plugin SCTX, or SCTX recovered from a dump),
    ///     or null when there is none or it is a reconstruction. Use it wherever only author-written text may
    ///     be written out as source.
    /// </summary>
    public static string? AuthoredText(ScriptRecord script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return script.HasSource && !IsReconstructed(script) ? script.SourceText : null;
    }

    /// <summary>The kebab-case token for a same-dump source/bytecode correspondence verdict.</summary>
    public static string FormatCorrespondence(ScriptSourceCorrespondenceStatus status)
    {
        return status switch
        {
            ScriptSourceCorrespondenceStatus.Unverified => "unverified",
            ScriptSourceCorrespondenceStatus.Accepted => "accepted",
            ScriptSourceCorrespondenceStatus.AcceptedSourceOnly => "accepted-source-only",
            ScriptSourceCorrespondenceStatus.Rejected => "rejected",
            _ => $"unknown-{(int)status}"
        };
    }

    private static string BuildLabel(ScriptTextKind kind, bool isMemoryDumpInput)
    {
        return kind switch
        {
            ScriptTextKind.AuthoredPlugin => "Source (SCTX)",
            ScriptTextKind.RecoveredDmpFragment => "Recovered source (dump fragment)",
            ScriptTextKind.RecoveredRuntimeObject =>
                "Recovered source (runtime object)",
            ScriptTextKind.UnattributedDumpText =>
                "Recovered source (unattributed)",
            ScriptTextKind.ReconstructedDecompiled =>
                DecompiledTextLabel,
            _ => NoSourceLabel(isMemoryDumpInput)
        };
    }
}

/// <summary>
///     How one script source body is labelled. Produced by <see cref="ScriptSourceProvenance" />.
/// </summary>
/// <param name="Kind">What the text is.</param>
/// <param name="Token">
///     Stable machine token: plugin-record, dmp-fragment, runtime-same-object, unattributed-same-dump,
///     decompiled-from-bytecode, or none.
/// </param>
/// <param name="Label">Human heading for the body; plain text, no markup (callers escape).</param>
/// <param name="IsMemoryDumpInput">
///     Whether the text was treated as memory-dump input (the caller's flag or the record's own dump flag).
/// </param>
/// <param name="CorrespondenceToken">
///     The same-dump correspondence verdict for recovered dump text (unverified, accepted,
///     accepted-source-only, rejected), <c>not-recorded</c> when the model keeps none, and
///     <c>not-applicable</c> for plugin text, reconstructions and absent text.
/// </param>
public sealed record ScriptSourceClassification(
    ScriptTextKind Kind,
    string Token,
    string Label,
    bool IsMemoryDumpInput,
    string CorrespondenceToken)
{
    /// <summary>Whether any source text is present.</summary>
    public bool HasSourceText => Kind is not (ScriptTextKind.None or ScriptTextKind.WithheldCaptured);

    /// <summary>Whether this is plugin SCTX without a recognized reconstruction marker; the legacy API name does not prove authorship.</summary>
    public bool IsAuthoredOriginal => Kind == ScriptTextKind.AuthoredPlugin;

    /// <summary>Whether this is a BethesdaMultitool decompilation rather than captured text.</summary>
    public bool IsReconstructed => Kind == ScriptTextKind.ReconstructedDecompiled;

    /// <summary>Whether this text was recovered from a memory dump (attributed or not).</summary>
    public bool IsRecoveredFromDump => Kind is ScriptTextKind.RecoveredDmpFragment
        or ScriptTextKind.RecoveredRuntimeObject or ScriptTextKind.UnattributedDumpText;

    /// <summary>Whether a script author wrote this text (plugin or recovered), i.e. present and not a reconstruction.</summary>
    public bool IsAuthorWrittenText => HasSourceText && !IsReconstructed;
}
