using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Export.Report;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Geck;

/// <summary>
///     Generates GECK-style text reports for Script records.
///     <para>
///         Source text is labelled through <see cref="ScriptSourceProvenance" />. "With Source (SCTX)" counts
///         stored SCTX without a known BMT reconstruction marker. A memory-dump
///         script whose SCTX is a BethesdaMultitool decompilation of its SCDA is counted under
///         <see cref="ReconstructedSourceCountLabel" /> and printed under <see cref="ReconstructedSourceHeading" />,
///         never as captured source. Dump-derived scripts also carry a "Source Origin:" line (the aggregate
///         report) or an Identity field "Source Origin" (<see cref="BuildScriptReport" />); plugin output is
///         unchanged, without asserting who authored plugin SCTX.
///     </para>
/// </summary>
internal static class GeckScriptWriter
{
    /// <summary>Summary label for scripts whose SCTX is a reconstruction decompiled from SCDA.</summary>
    internal const string ReconstructedSourceCountLabel = "With Reconstruction";

    /// <summary>Heading for a reconstructed body in the aggregate script report.</summary>
    internal const string ReconstructedSourceHeading =
        "Reconstruction (SCDA):";

    /// <summary>Identity field (and aggregate-report line label) naming where a dump script's text came from.</summary>
    internal const string SourceOriginFieldName = "Source Origin";

    /// <summary>
    ///     The high bit the parsers set on a reference-table entry read from SCRV (a local variable index)
    ///     rather than SCRO (a FormID).
    /// </summary>
    private const uint ScrvLocalMarker = 0x80000000;

    /// <summary>
    ///     The aggregate script section (script_report.txt and the full report). The "Endianness:",
    ///     "Bytecode Order:" and "Is Compiled:" lines are kept verbatim for downstream greps; "Source Origin:"
    ///     follows "Source:" only for dump-derived scripts.
    /// </summary>
    /// <param name="sb">The report being built.</param>
    /// <param name="scripts">The scripts to list.</param>
    /// <param name="resolver">EditorID/display-name source for FormIDs.</param>
    /// <param name="isMemoryDumpInput">
    ///     True when the records were loaded from a memory dump. The collection is also treated as dump
    ///     input when any script carries a runtime or recorded dump origin (only dump parsing sets those),
    ///     so dump text of unattributed origin is never labelled as authored plugin source.
    /// </param>
    internal static void AppendScriptsSection(StringBuilder sb, List<ScriptRecord> scripts,
        FormIdResolver resolver, bool isMemoryDumpInput = false)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Scripts ({scripts.Count})");

        var dumpInput = isMemoryDumpInput || ContainsDumpDerivedScripts(scripts);
        var byType = scripts.GroupBy(s => s.ScriptType).OrderBy(g => g.Key).ToList();
        sb.AppendLine();
        sb.AppendLine($"Total Scripts: {scripts.Count:N0}");
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key}: {group.Count():N0}");
        }

        var fromRuntime = scripts.Count(s => s.FromRuntime);
        var withSource = scripts.Count(s => ScriptSourceProvenance.AuthoredText(s) != null);
        var withReconstructed = scripts.Count(ScriptSourceProvenance.IsReconstructed);
        var withBytecode = scripts.Count(s => s.CompiledData is { Length: > 0 });
        if (fromRuntime > 0)
        {
            sb.AppendLine($"  From Runtime Structs: {fromRuntime:N0}");
        }

        sb.AppendLine($"  With Source (SCTX): {withSource:N0}");
        if (withReconstructed > 0)
        {
            sb.AppendLine($"  {ReconstructedSourceCountLabel}: {withReconstructed:N0}");
        }

        sb.AppendLine($"  With Bytecode (SCDA): {withBytecode:N0}");
        sb.AppendLine();

        foreach (var script in scripts.OrderBy(s => s.EditorId ?? "", StringComparer.OrdinalIgnoreCase))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "SCPT", script.EditorId);
            var source = ScriptSourceProvenance.Classify(script, dumpInput);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(script.FormId)}");
            sb.AppendLine($"Editor ID:      {script.EditorId ?? "(none)"}");
            sb.AppendLine($"Type:           {script.ScriptType}");
            sb.AppendLine($"Variables:      {script.Variables.Count}");
            sb.AppendLine($"Ref Objects:    {script.RefObjectCount}");
            sb.AppendLine($"Compiled Size:  {script.CompiledSize:N0} bytes");
            sb.AppendLine($"Is Compiled:    {script.IsCompiled}");
            sb.AppendLine($"Source:         {(script.FromRuntime ? "Runtime Struct" : "ESM Record")}");
            if (IsDumpDerived(source))
            {
                sb.AppendLine($"{SourceOriginFieldName}:  {FormatSourceOrigin(source)}");
            }

            sb.AppendLine($"Endianness:     {(script.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Bytecode Order: {FormatBytecodeOrder(script)}");
            sb.AppendLine($"Offset:         0x{script.Offset:X8}");

            if (script.OwnerQuestFormId.HasValue)
            {
                sb.AppendLine($"Owner Quest:    {resolver.FormatFull(script.OwnerQuestFormId.Value)}");
            }

            if (script.QuestScriptDelay > 0)
            {
                sb.AppendLine($"Quest Delay:    {script.QuestScriptDelay:F1}s");
            }

            if (script.Variables.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Variables:");
                foreach (var v in script.Variables.OrderBy(v => v.Index))
                {
                    sb.AppendLine($"  [{v.Index,3}] {ScriptVariableTypeResolver.FormatDeclaration(v, script.ReferencedObjects, alignType: true)}");
                }
            }

            if (script.ReferencedObjects.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Referenced Objects:");
                foreach (var refId in script.ReferencedObjects)
                {
                    sb.AppendLine($"  {FormatScriptReference(refId, resolver)}");
                }
            }

            if (source.HasSourceText)
            {
                sb.AppendLine();
                sb.AppendLine(source.IsReconstructed ? ReconstructedSourceHeading : "Source (SCTX):");
                foreach (var line in script.SourceText!.Split('\n'))
                {
                    sb.Append("  ").AppendLine(line.TrimEnd('\r'));
                }
            }

            foreach (var binding in script.ExternalVariableBindings)
            {
                sb.AppendLine($"External Variable: {binding.Summary}");
            }

            if (!string.IsNullOrEmpty(script.DecompiledText))
            {
                sb.AppendLine();
                sb.AppendLine("Decompiled (SCDA):");
                foreach (var line in script.DecompiledText.Split('\n'))
                {
                    sb.Append("  ").AppendLine(line.TrimEnd('\r'));
                }
            }

            if (script.CompiledData is { Length: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine($"Raw Bytecode (SCDA) - {script.CompiledData.Length} bytes:");
                AppendHexDump(sb, script.CompiledData);
            }
        }
    }

    internal static void AppendHexDump(StringBuilder sb, byte[] data, int bytesPerLine = 16)
    {
        for (var i = 0; i < data.Length; i += bytesPerLine)
        {
            var count = Math.Min(bytesPerLine, data.Length - i);
            sb.Append($"  {i:X4}: ");

            for (var j = 0; j < bytesPerLine; j++)
            {
                if (j < count)
                {
                    sb.Append($"{data[i + j]:X2} ");
                }
                else
                {
                    sb.Append("   ");
                }

                if (j == 7)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(" |");

            for (var j = 0; j < count; j++)
            {
                var b = data[i + j];
                sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
            }

            sb.AppendLine("|");
        }
    }

    /// <summary>
    ///     The typed report used by show, semdiff and the GUI. The source body stays in the section named
    ///     "Source" whatever its origin, so comparisons keep aligning; a dump-derived script adds the Identity
    ///     field <see cref="SourceOriginFieldName" /> saying what that body is. A plugin script's report is
    ///     unchanged.
    /// </summary>
    /// <param name="script">The script record.</param>
    /// <param name="resolver">EditorID/display-name source for FormIDs.</param>
    /// <param name="isMemoryDumpInput">
    ///     True when the record was loaded from a memory dump. A record with a runtime or recorded dump
    ///     origin is treated as dump-derived either way.
    /// </param>
    internal static RecordReport BuildScriptReport(ScriptRecord script, FormIdResolver resolver,
        bool isMemoryDumpInput = false)
    {
        var sections = new List<ReportSection>();

        // Identity
        var identityFields = new List<ReportField>
        {
            new("Type", ReportValue.String(script.ScriptType)),
            new("Is Compiled", ReportValue.Bool(script.IsCompiled))
        };
        var source = ScriptSourceProvenance.Classify(script, isMemoryDumpInput);
        if (IsDumpDerived(source))
        {
            identityFields.Add(new ReportField(SourceOriginFieldName,
                ReportValue.String(FormatSourceOrigin(source))));
        }

        sections.Add(new ReportSection("Identity", identityFields));

        if (script.ExternalVariableBindings.Count > 0)
        {
            sections.Add(new ReportSection("External Variables", script.ExternalVariableBindings
                .Select(binding => new ReportField($"0x{binding.OwnerFormId:X8}.var{binding.VariableIndex}",
                    ReportValue.String(binding.Summary))).ToList()));
        }

        // Stats
        sections.Add(new ReportSection("Stats",
        [
            new ReportField("Variable Count", ReportValue.Int((int)script.VariableCount)),
            new ReportField("Ref Object Count", ReportValue.Int((int)script.RefObjectCount)),
            new ReportField("Compiled Size",
                ReportValue.Int((int)script.CompiledSize, $"{script.CompiledSize:N0} bytes"))
        ]));

        // References
        var refFields = new List<ReportField>();
        if (script.OwnerQuestFormId.HasValue)
        {
            refFields.Add(new ReportField("Owner Quest",
                ReportValue.FormId(script.OwnerQuestFormId.Value, resolver),
                $"0x{script.OwnerQuestFormId.Value:X8}"));
        }

        if (script.ReferencedObjects.Count > 0)
        {
            var refItems = script.ReferencedObjects
                .Select(id => (ReportValue)ReportValue.FormId(id, FormatScriptReference(id, resolver)))
                .ToList();
            refFields.Add(new ReportField("Referenced Objects", ReportValue.List(refItems)));
        }

        if (refFields.Count > 0)
        {
            sections.Add(new ReportSection("References", refFields));
        }

        // Variables
        if (script.Variables.Count > 0)
        {
            var varItems = script.Variables
                .OrderBy(v => v.Index)
                .Select(v =>
                {
                    var type = ScriptVariableTypeResolver.Resolve(v, script.ReferencedObjects);
                    var fields = new List<ReportField>
                    {
                        new("Name", ReportValue.String(v.Name ?? "(unnamed)")),
                        new("Type", ReportValue.String(type.Name)),
                        new("Storage Type Byte", ReportValue.Int(v.Type)),
                        new("Type Evidence", ReportValue.String(type.Evidence)),
                        new("Index", ReportValue.Int((int)v.Index))
                    };
                    return (ReportValue)new ReportValue.CompositeVal(fields,
                        $"[{v.Index,3}] {ScriptVariableTypeResolver.FormatDeclaration(v, script.ReferencedObjects, alignType: true)}");
                })
                .ToList();

            sections.Add(new ReportSection("Variables",
            [
                new ReportField("Variables", ReportValue.List(varItems, $"{script.Variables.Count} variables"))
            ]));
        }

        // Source text. The section keeps the name "Source" for every origin so reports stay comparable;
        // for a dump-derived script the Identity field "Source Origin" says what the text is (a
        // reconstruction decompiled from SCDA is never presented as captured SCTX).
        if (script.HasSource)
        {
            sections.Add(new ReportSection("Source",
            [
                new ReportField("Source", ReportValue.String(script.SourceText!))
            ]));
        }

        // Decompiled (SCDA) — available in all builds
        if (!string.IsNullOrEmpty(script.DecompiledText))
        {
            sections.Add(new ReportSection("Decompiled",
            [
                new ReportField("Decompiled", ReportValue.String(script.DecompiledText))
            ]));
        }

        return new RecordReport("Script", script.FormId, script.EditorId, null, sections);
    }

    /// <summary>
    ///     The byte order the SCDA was decoded in and the rule that chose it. This is separate
    ///     from the "Endianness:" line, which describes the record container: an Xbox 360 ESM is
    ///     a big-endian container whose serialized bytecode is little-endian.
    /// </summary>
    private static string FormatBytecodeOrder(ScriptRecord script)
    {
        if (script.CompiledData is not { Length: > 0 })
        {
            return "(no SCDA)";
        }

        var order = script.IsBigEndianBytecode ? "Big-Endian" : "Little-Endian";
        var evidence = script.BytecodeByteOrderEvidence switch
        {
            ScriptBytecodeByteOrderEvidence.ScriptNameAnchor => "ScriptName anchor",
            ScriptBytecodeByteOrderEvidence.SingleCleanWalk => "only clean walk",
            ScriptBytecodeByteOrderEvidence.FewerUnknownOpcodes => "fewer unknown opcodes",
            ScriptBytecodeByteOrderEvidence.WalkedToEndInOneOrder => "only walk to reach the end",
            ScriptBytecodeByteOrderEvidence.AmbiguousSerializedDefault => "ambiguous, serialized default",
            ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback => "ambiguous, container order",
            ScriptBytecodeByteOrderEvidence.RuntimeScriptObject => "runtime Script object",
            _ => "not determined"
        };
        return $"{order} ({evidence})";
    }

    /// <summary>
    ///     The value of a "Source Origin" line or field: the provenance token, then what it means. Recovered
    ///     dump text carries its same-dump correspondence verdict; a reconstruction says it is not captured
    ///     SCTX; absent dump text says absence from a partial capture proves nothing about the build.
    /// </summary>
    internal static string FormatSourceOrigin(ScriptSourceClassification source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Kind switch
        {
            ScriptTextKind.AuthoredPlugin => $"{source.Token} (SCTX stored in this plugin)",
            ScriptTextKind.ReconstructedDecompiled =>
                $"{source.Token} (reconstruction)",
            ScriptTextKind.RecoveredDmpFragment or ScriptTextKind.RecoveredRuntimeObject
                or ScriptTextKind.UnattributedDumpText =>
                $"{source.Token} (captured SCTX; correspondence: {source.CorrespondenceToken})",
            _ => source.IsMemoryDumpInput
                ? $"{source.Token} ({ScriptSourceProvenance.PartialDumpAbsenceWording})"
                : $"{source.Token} (no SCTX in this record)"
        };
    }

    /// <summary>
    ///     True when a script's text (or its absence) comes from a memory dump: the caller said so, the record
    ///     came from a runtime struct, or its text carries a recorded dump origin. Plugin SCTX is never
    ///     dump-derived, which is what keeps plugin reports unchanged.
    /// </summary>
    private static bool IsDumpDerived(ScriptSourceClassification source)
    {
        return source.IsMemoryDumpInput || source.Kind is ScriptTextKind.RecoveredDmpFragment
            or ScriptTextKind.RecoveredRuntimeObject or ScriptTextKind.UnattributedDumpText
            or ScriptTextKind.ReconstructedDecompiled;
    }

    /// <summary>
    ///     Whether any script shows it was read from a memory dump. Only dump parsing sets
    ///     <see cref="ScriptRecord.FromRuntime" /> or a <see cref="ScriptRecord.SourceTextOrigin" /> other than
    ///     <see cref="ScriptSourceTextOrigin.None" /> (plugin SCTX keeps None), so either marks the whole
    ///     collection as dump input.
    /// </summary>
    private static bool ContainsDumpDerivedScripts(List<ScriptRecord> scripts)
    {
        return scripts.Exists(script =>
            script.FromRuntime || script.SourceTextOrigin != ScriptSourceTextOrigin.None);
    }

    /// <summary>
    ///     Formats one SCRO FormID: the two player forms by name, anything else through
    ///     <see cref="FormIdResolver.FormatFull" />. The value is taken as a FormID as-is; a caller whose
    ///     table can hold SCRV entries uses the overload that takes the script's local variables.
    /// </summary>
    internal static string FormatScriptReference(uint formId, FormIdResolver resolver)
    {
        return formId switch
        {
            0x00000007 => "Player (0x00000007)",
            0x00000014 => "PlayerRef (0x00000014)",
            _ => resolver.FormatFull(formId)
        };
    }

    /// <summary>
    ///     Formats one entry of a script's mixed SCRO/SCRV reference table. An SCRV entry (high bit set, as the
    ///     parsers store it) is a local variable of the same script and prints as <c>local #n (name)</c>, with
    ///     the name from <paramref name="localVariables" /> (SLSD/SCVR); an SCRO entry prints as the two-argument
    ///     overload does.
    /// </summary>
    /// <param name="rawReference">The table entry: a FormID, or <c>0x80000000 | index</c> for an SCRV local.</param>
    /// <param name="localVariables">The owning script's local-variable table, used to name SCRV locals.</param>
    /// <param name="resolver">EditorID/display-name source for SCRO FormIDs.</param>
    internal static string FormatScriptReference(
        uint rawReference,
        IReadOnlyList<ScriptVariableInfo> localVariables,
        FormIdResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(localVariables);
        if ((rawReference & ScrvLocalMarker) == 0)
        {
            return FormatScriptReference(rawReference, resolver);
        }

        var index = rawReference & ~ScrvLocalMarker;
        var variable = localVariables.FirstOrDefault(candidate => candidate.Index == index);
        if (variable is null)
        {
            return $"local #{index} (not declared in this script's SLSD/SCVR table)";
        }

        return string.IsNullOrWhiteSpace(variable.Name)
            ? $"local #{index} (unnamed)"
            : $"local #{index} ({variable.Name})";
    }

    /// <summary>The standalone script_report.txt body (see <see cref="AppendScriptsSection" />).</summary>
    internal static string GenerateScriptsReport(List<ScriptRecord> scripts,
        FormIdResolver? resolver = null, bool isMemoryDumpInput = false)
    {
        var sb = new StringBuilder();
        AppendScriptsSection(sb, scripts, resolver ?? FormIdResolver.Empty, isMemoryDumpInput);
        return sb.ToString();
    }
}
