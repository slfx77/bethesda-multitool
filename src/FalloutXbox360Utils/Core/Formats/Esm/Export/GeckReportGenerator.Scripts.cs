using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

public static partial class GeckReportGenerator
{
    #region Script Methods

    private static void AppendScriptsSection(StringBuilder sb, List<ReconstructedScript> scripts,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Scripts ({scripts.Count})");

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
            AppendRecordHeader(sb, "SCPT", script.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(script.FormId)}");
            sb.AppendLine($"Editor ID:      {script.EditorId ?? "(none)"}");
            sb.AppendLine($"Type:           {script.ScriptType}");
            sb.AppendLine($"Variables:      {script.VariableCount}");
            sb.AppendLine($"Ref Objects:    {script.RefObjectCount}");
            sb.AppendLine($"Compiled Size:  {script.CompiledSize:N0} bytes");
            sb.AppendLine($"Is Compiled:    {script.IsCompiled}");
            sb.AppendLine($"Source:         {(script.FromRuntime ? "Runtime Struct" : "ESM Record")}");
            sb.AppendLine($"Endianness:     {(script.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{script.Offset:X8}");

            if (script.OwnerQuestFormId.HasValue)
            {
                sb.AppendLine($"Owner Quest:    {FormatFormIdWithName(script.OwnerQuestFormId.Value, lookup)}");
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
                    sb.AppendLine($"  {FormatFormIdWithName(refId, lookup)}");
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

    /// <summary>
    ///     Append a hex dump of byte data with offset, hex, and ASCII columns.
    /// </summary>
    private static void AppendHexDump(StringBuilder sb, byte[] data, int bytesPerLine = 16)
    {
        for (var i = 0; i < data.Length; i += bytesPerLine)
        {
            var count = Math.Min(bytesPerLine, data.Length - i);
            sb.Append($"  {i:X4}: ");

            // Hex bytes
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

            // ASCII representation
            for (var j = 0; j < count; j++)
            {
                var b = data[i + j];
                sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
            }

            sb.AppendLine("|");
        }
    }

    /// <summary>
    ///     Generate a report for Scripts only.
    /// </summary>
    public static string GenerateScriptsReport(List<ReconstructedScript> scripts,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendScriptsSection(sb, scripts, lookup ?? []);
        return sb.ToString();
    }

    #endregion
}
