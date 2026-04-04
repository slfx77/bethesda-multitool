using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using Spectre.Console;
using static FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers.DiffHelpers;

namespace FalloutXbox360Utils.CLI.Shared;

/// <summary>
///     Helper methods for three-way diff formatting, schema lookup, and field decoding.
/// </summary>
internal static class ThreeWayDiffHelpers
{
    internal static ThreeWaySubrecordRow BuildThreeWaySubrecordRow(
        string recordType,
        string sig,
        AnalyzerSubrecordInfo? xbox,
        AnalyzerSubrecordInfo? converted,
        AnalyzerSubrecordInfo? pc,
        uint xboxRecordOffset,
        uint convertedRecordOffset,
        uint pcRecordOffset,
        int maxBytes,
        bool showBytes,
        bool showSemantic,
        DiffFormIdResolver? resolver = null)
    {
        // Handle missing subrecords
        if (xbox == null && converted == null && pc == null)
        {
            return new ThreeWaySubrecordRow
            {
                Signature = sig,
                SizeDisplay = "\u2014",
                XboxOffsetDisplay = "\u2014",
                ConvertedOffsetDisplay = "\u2014",
                PcOffsetDisplay = "\u2014",
                StatusMarkup = "[grey]N/A[/]",
                ShowDetails = false,
                DetailsMarkup = null
            };
        }

        // Calculate file offsets
        var xboxFileOffset = xbox != null
            ? (long)xboxRecordOffset + EsmParser.MainRecordHeaderSize + xbox.Offset
            : -1;
        var convertedFileOffset = converted != null
            ? (long)convertedRecordOffset + EsmParser.MainRecordHeaderSize + converted.Offset
            : -1;
        var pcFileOffset = pc != null
            ? (long)pcRecordOffset + EsmParser.MainRecordHeaderSize + pc.Offset
            : -1;

        // Size display - show all three if they differ
        string sizeDisplay;
        if (xbox != null && converted != null && pc != null)
        {
            if (xbox.Data.Length == converted.Data.Length && converted.Data.Length == pc.Data.Length)
            {
                sizeDisplay = xbox.Data.Length.ToString("N0");
            }
            else
            {
                sizeDisplay = $"{xbox.Data.Length}/{converted.Data.Length}/{pc.Data.Length}";
            }
        }
        else
        {
            var sizes = new List<string>();
            sizes.Add(xbox?.Data.Length.ToString("N0") ?? "\u2014");
            sizes.Add(converted?.Data.Length.ToString("N0") ?? "\u2014");
            sizes.Add(pc?.Data.Length.ToString("N0") ?? "\u2014");
            sizeDisplay = string.Join("/", sizes);
        }

        // Check presence in each file
        if (converted == null && pc == null)
        {
            return new ThreeWaySubrecordRow
            {
                Signature = sig,
                SizeDisplay = sizeDisplay,
                XboxOffsetDisplay = xboxFileOffset >= 0 ? $"0x{xboxFileOffset:X}" : "\u2014",
                ConvertedOffsetDisplay = "[red]MISSING[/]",
                PcOffsetDisplay = "[red]MISSING[/]",
                StatusMarkup = "[red]Only in Xbox[/]",
                ShowDetails = false,
                DetailsMarkup = null
            };
        }

        if (xbox == null)
        {
            return new ThreeWaySubrecordRow
            {
                Signature = sig,
                SizeDisplay = sizeDisplay,
                XboxOffsetDisplay = "[grey]\u2014[/]",
                ConvertedOffsetDisplay = convertedFileOffset >= 0 ? $"0x{convertedFileOffset:X}" : "\u2014",
                PcOffsetDisplay = pcFileOffset >= 0 ? $"0x{pcFileOffset:X}" : "\u2014",
                StatusMarkup = "[yellow]Not in Xbox[/]",
                ShowDetails = false,
                DetailsMarkup = null
            };
        }

        if (converted == null)
        {
            return new ThreeWaySubrecordRow
            {
                Signature = sig,
                SizeDisplay = sizeDisplay,
                XboxOffsetDisplay = $"0x{xboxFileOffset:X}",
                ConvertedOffsetDisplay = "[red]MISSING[/]",
                PcOffsetDisplay = pcFileOffset >= 0 ? $"0x{pcFileOffset:X}" : "\u2014",
                StatusMarkup = "[red]Missing in Conv[/]",
                ShowDetails = false,
                DetailsMarkup = null
            };
        }

        if (pc == null)
        {
            return new ThreeWaySubrecordRow
            {
                Signature = sig,
                SizeDisplay = sizeDisplay,
                XboxOffsetDisplay = $"0x{xboxFileOffset:X}",
                ConvertedOffsetDisplay = $"0x{convertedFileOffset:X}",
                PcOffsetDisplay = "[grey]\u2014[/]",
                StatusMarkup = "[yellow]Not in PC Ref[/]",
                ShowDetails = false,
                DetailsMarkup = null
            };
        }

        // All three present - compare converted vs PC
        var sizeMatch = converted.Data.Length == pc.Data.Length;
        var isIdentical = converted.Data.SequenceEqual(pc.Data);

        string status;
        if (isIdentical)
        {
            status = "[green]IDENTICAL[/]";
        }
        else if (!sizeMatch)
        {
            status = $"[red]SIZE {converted.Data.Length}/{pc.Data.Length}[/]";
        }
        else
        {
            status = "[yellow]CONTENT DIFFERS[/]";
        }

        var showDetails = !isIdentical && sizeMatch;
        string? details = null;

        if (showDetails)
        {
            var firstDiff = FindFirstDifferenceOffset(converted.Data, pc.Data);
            var schemaHint = firstDiff >= 0
                ? DescribeSchemaAtOffset(sig, recordType, converted.Data.Length, firstDiff)
                : null;

            var schemaSuffix = string.IsNullOrWhiteSpace(schemaHint)
                ? string.Empty
                : $" | schema: {Markup.Escape(schemaHint)}";

            var parts = new List<string>
            {
                $"[grey]First diff[/] +0x{firstDiff:X}{schemaSuffix}"
            };

            if (showBytes && firstDiff >= 0)
            {
                var (ctxStart, ctxLen) = converted.Data.Length <= maxBytes
                    ? (0, converted.Data.Length)
                    : GetContextWindow(firstDiff, converted.Data.Length);
                ctxLen = Math.Min(ctxLen, maxBytes);

                // Show Xbox, Converted, and PC bytes
                var xboxLine = FormatBytesHighlighted(xbox.Data, ctxStart, ctxLen);
                var convLine = FormatBytesDiffHighlighted(converted.Data, pc.Data, ctxStart, ctxLen, firstDiff, null);
                var pcLine = FormatBytesDiffHighlighted(pc.Data, converted.Data, ctxStart, ctxLen, firstDiff, null);

                parts.Add($"[yellow]Xbox[/]      +0x{ctxStart:X}: {xboxLine}");
                parts.Add($"[green]Converted[/] +0x{ctxStart:X}: {convLine}");
                parts.Add($"[cyan]PC Ref[/]    +0x{ctxStart:X}: {pcLine}");
            }

            // Add semantic field breakdown if schema exists
            if (showSemantic)
            {
                var schema = SubrecordSchemaRegistry.GetSchema(sig, recordType, converted.Data.Length);
                if (schema != null)
                {
                    parts.Add("");
                    parts.Add("[bold]Semantic field comparison:[/]");

                    var xboxFields = DecodeSchemaFieldsForThreeWay(xbox.Data, schema, true);
                    var convFields = DecodeSchemaFieldsForThreeWay(converted.Data, schema, false);
                    var pcFields = DecodeSchemaFieldsForThreeWay(pc.Data, schema, false);

                    foreach (var field in schema.Fields)
                    {
                        var xVal = xboxFields.GetValueOrDefault(field.Name, "\u2014");
                        var cVal = convFields.GetValueOrDefault(field.Name, "\u2014");
                        var pVal = pcFields.GetValueOrDefault(field.Name, "\u2014");

                        // For FormId fields, try to resolve to EDID names
                        string matchStatus;
                        string xDisplay = xVal, cDisplay = cVal, pDisplay = pVal;

                        if (resolver != null && (field.Type == SubrecordFieldType.FormId ||
                                                 field.Type == SubrecordFieldType.FormIdLittleEndian))
                        {
                            // Parse FormID values (format: 0xXXXXXXXX)
                            if (TryParseFormId(xVal, out var xFormId) &&
                                TryParseFormId(cVal, out var cFormId) &&
                                TryParseFormId(pVal, out var pFormId))
                            {
                                var xEdid = resolver.ResolveXbox(xFormId);
                                var cEdid = resolver.ResolveConverted(cFormId);
                                var pEdid = resolver.ResolvePc(pFormId);

                                // Add EDID to display if resolved
                                if (!string.IsNullOrEmpty(xEdid)) xDisplay = $"{xVal} ({xEdid})";
                                if (!string.IsNullOrEmpty(cEdid)) cDisplay = $"{cVal} ({cEdid})";
                                if (!string.IsNullOrEmpty(pEdid)) pDisplay = $"{pVal} ({pEdid})";

                                // Compare by EDID if both resolved, otherwise fall back to raw value comparison
                                if (!string.IsNullOrEmpty(cEdid) && !string.IsNullOrEmpty(pEdid))
                                {
                                    matchStatus = cEdid == pEdid
                                        ? "[green]MATCH[/]"
                                        : "[red]DIFF[/]";
                                    if (cEdid == pEdid && cVal != pVal)
                                    {
                                        matchStatus = "[green]MATCH[/] [grey](FormID differs, same EDID)[/]";
                                    }
                                }
                                else
                                {
                                    matchStatus = cVal == pVal ? "[green]MATCH[/]" : "[yellow]DIFF (unresolved)[/]";
                                }
                            }
                            else
                            {
                                matchStatus = cVal == pVal ? "[green]MATCH[/]" : "[red]DIFF[/]";
                            }
                        }
                        else
                        {
                            matchStatus = cVal == pVal ? "[green]MATCH[/]" : "[red]DIFF[/]";
                        }

                        parts.Add(
                            $"  {field.Name}: Xbox={Markup.Escape(xDisplay)}, Conv={Markup.Escape(cDisplay)}, PC={Markup.Escape(pDisplay)} {matchStatus}");
                    }
                }
            }

            details = string.Join("\n", parts);
        }

        return new ThreeWaySubrecordRow
        {
            Signature = sig,
            SizeDisplay = sizeDisplay,
            XboxOffsetDisplay = $"0x{xboxFileOffset:X}",
            ConvertedOffsetDisplay = $"0x{convertedFileOffset:X}",
            PcOffsetDisplay = $"0x{pcFileOffset:X}",
            StatusMarkup = status,
            ShowDetails = showDetails,
            DetailsMarkup = details
        };
    }

    internal static string FormatBytesHighlighted(byte[] data, int start, int length)
    {
        var sb = new StringBuilder();
        var end = Math.Min(start + length, data.Length);
        for (var i = start; i < end; i++)
        {
            if (sb.Length > 0)
            {
                _ = sb.Append(' ');
            }

            _ = sb.Append($"{data[i]:X2}");
        }

        return sb.ToString();
    }

    internal static Dictionary<string, string> DecodeSchemaFieldsForThreeWay(
        byte[] data, SubrecordSchema schema, bool bigEndian)
    {
        var fields = new Dictionary<string, string>();
        var offset = 0;

        foreach (var field in schema.Fields)
        {
            if (offset >= data.Length)
            {
                break;
            }

            var fieldSize = GetFieldSizeForThreeWay(field.Type, field.Size);
            if (offset + fieldSize > data.Length)
            {
                break;
            }

            var value = DecodeFieldValueForThreeWay(data.AsSpan(offset, fieldSize), field.Type, bigEndian);
            fields[field.Name] = value;
            offset += fieldSize;
        }

        return fields;
    }

    internal static int GetFieldSizeForThreeWay(SubrecordFieldType type, int? explicitSize)
    {
        if (explicitSize.HasValue)
        {
            return explicitSize.Value;
        }

        return type switch
        {
            SubrecordFieldType.UInt8 or SubrecordFieldType.Int8 => 1,
            SubrecordFieldType.UInt16 or SubrecordFieldType.Int16 or SubrecordFieldType.UInt16LittleEndian => 2,
            SubrecordFieldType.UInt32 or SubrecordFieldType.Int32 or SubrecordFieldType.Float
                or SubrecordFieldType.FormId or SubrecordFieldType.FormIdLittleEndian
                or SubrecordFieldType.ColorRgba or SubrecordFieldType.ColorArgb
                or SubrecordFieldType.UInt32WordSwapped => 4,
            SubrecordFieldType.UInt64 or SubrecordFieldType.Int64 or SubrecordFieldType.Double => 8,
            SubrecordFieldType.Vec3 => 12,
            SubrecordFieldType.Quaternion => 16,
            SubrecordFieldType.PosRot => 24,
            _ => 4
        };
    }

    internal static string DecodeFieldValueForThreeWay(ReadOnlySpan<byte> data, SubrecordFieldType type, bool bigEndian)
    {
        return FieldValueDecoder.Decode(data, type, bigEndian);
    }

    internal static bool TryParseFormId(string value, out uint formId)
    {
        return FieldValueDecoder.TryParseFormId(value, out formId);
    }
}
