using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates GECK-style text reports for Script records.
/// </summary>
internal static class GeckScriptWriter
{
    internal static void AppendScriptsSection(StringBuilder sb, List<ScriptRecord> scripts,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Scripts ({scripts.Count})");

        var byType = scripts.GroupBy(s => s.ScriptType).OrderBy(g => g.Key).ToList();
        sb.AppendLine();
        sb.AppendLine($"Total Scripts: {scripts.Count:N0}");
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key}: {group.Count():N0}");
        }

        var fromRuntime = scripts.Count(s => s.FromRuntime);
        var withSource = scripts.Count(s => s.HasSource);
        var withBytecode = scripts.Count(s => s.CompiledData is { Length: > 0 });
        if (fromRuntime > 0)
        {
            sb.AppendLine($"  From Runtime Structs: {fromRuntime:N0}");
        }

        sb.AppendLine($"  With Source (SCTX): {withSource:N0}");
        sb.AppendLine($"  With Bytecode (SCDA): {withBytecode:N0}");
        sb.AppendLine();

        foreach (var script in scripts.OrderBy(s => s.EditorId ?? "", StringComparer.OrdinalIgnoreCase))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "SCPT", script.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(script.FormId)}");
            sb.AppendLine($"Editor ID:      {script.EditorId ?? "(none)"}");
            sb.AppendLine($"Type:           {script.ScriptType}");
            sb.AppendLine($"Variables:      {script.Variables.Count}");
            sb.AppendLine($"Ref Objects:    {script.RefObjectCount}");
            sb.AppendLine($"Compiled Size:  {script.CompiledSize:N0} bytes");
            sb.AppendLine($"Is Compiled:    {script.IsCompiled}");
            sb.AppendLine($"Source:         {(script.FromRuntime ? "Runtime Struct" : "ESM Record")}");
            sb.AppendLine($"Endianness:     {(script.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
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
                    sb.AppendLine($"  [{v.Index,3}] {v.TypeName,-5} {v.Name ?? "(unnamed)"}");
                }
            }

            if (script.ReferencedObjects.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Referenced Objects:");
                foreach (var refId in script.ReferencedObjects)
                {
                    sb.AppendLine($"  {resolver.FormatFull(refId)}");
                }
            }

            if (script.HasSource)
            {
                sb.AppendLine();
                sb.AppendLine("Source (SCTX):");
                foreach (var line in script.SourceText!.Split('\n'))
                {
                    sb.Append("  ").AppendLine(line.TrimEnd('\r'));
                }
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

    internal static RecordReport BuildScriptReport(ScriptRecord script, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Identity
        sections.Add(new ReportSection("Identity",
        [
            new ReportField("Type", ReportValue.String(script.ScriptType)),
            new ReportField("Is Compiled", ReportValue.Bool(script.IsCompiled))
        ]));

        // Stats
        sections.Add(new ReportSection("Stats",
        [
            new ReportField("Variable Count", ReportValue.Int((int)script.VariableCount)),
            new ReportField("Ref Object Count", ReportValue.Int((int)script.RefObjectCount)),
            new ReportField("Compiled Size", ReportValue.Int((int)script.CompiledSize, $"{script.CompiledSize:N0} bytes"))
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
                .Select(id => (ReportValue)ReportValue.FormId(id, resolver))
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
                    var fields = new List<ReportField>
                    {
                        new("Name", ReportValue.String(v.Name ?? "(unnamed)")),
                        new("Type", ReportValue.String(v.TypeName)),
                        new("Index", ReportValue.Int((int)v.Index))
                    };
                    return (ReportValue)new ReportValue.CompositeVal(fields,
                        $"[{v.Index,3}] {v.TypeName,-5} {v.Name ?? "(unnamed)"}");
                })
                .ToList();

            sections.Add(new ReportSection($"Variables ({script.Variables.Count})",
            [
                new ReportField("Variables", ReportValue.List(varItems))
            ]));
        }

        // Source
        if (script.HasSource)
        {
            sections.Add(new ReportSection("Source",
            [
                new ReportField("SCTX", ReportValue.String(script.SourceText!))
            ]));
        }

        // Decompiled
        if (!string.IsNullOrEmpty(script.DecompiledText))
        {
            sections.Add(new ReportSection("Decompiled",
            [
                new ReportField("SCDA", ReportValue.String(script.DecompiledText))
            ]));
        }

        return new RecordReport("Script", script.FormId, script.EditorId, null, sections);
    }

    internal static string GenerateScriptsReport(List<ScriptRecord> scripts,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendScriptsSection(sb, scripts, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }
}
