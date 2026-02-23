using Spectre.Console;
using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Record comparison display: side-by-side subrecord diff rendering and helpers.
/// </summary>
internal static class DumpCommandsRecordCompare
{
    internal static void DisplayRecordComparison(
        List<AnalyzerRecordInfo> primaryMatches, EsmFileLoadResult primaryEsm,
        List<AnalyzerRecordInfo> compareMatches, EsmFileLoadResult compareEsm,
        string primaryPath, string comparePath, bool showHex)
    {
        var primaryFileName = Path.GetFileName(primaryPath);
        var compareFileName = Path.GetFileName(comparePath);

        // If both files have the same name, use parent directory to distinguish
        string primaryName, compareName;
        if (primaryFileName.Equals(compareFileName, StringComparison.OrdinalIgnoreCase))
        {
            var primaryDir = Path.GetFileName(Path.GetDirectoryName(primaryPath)) ?? "primary";
            var compareDir = Path.GetFileName(Path.GetDirectoryName(comparePath)) ?? "compare";
            primaryName = $"{primaryDir}/{primaryFileName}";
            compareName = $"{compareDir}/{compareFileName}";
        }
        else
        {
            primaryName = primaryFileName;
            compareName = compareFileName;
        }

        // Get the first match from each (typically there's only one record per FormID per file)
        var primary = primaryMatches.FirstOrDefault();
        var compare = compareMatches.FirstOrDefault();

        if (primary == null && compare == null)
        {
            AnsiConsole.MarkupLine("[yellow]No records found in either file.[/]");
            return;
        }

        // Parse subrecords for both
        List<AnalyzerSubrecordInfo> primarySubs = [];
        List<AnalyzerSubrecordInfo> compareSubs = [];

        if (primary != null)
        {
            try
            {
                var data = EsmHelpers.GetRecordData(primaryEsm.Data, primary, primaryEsm.IsBigEndian);
                primarySubs = EsmRecordParser.ParseSubrecords(data, primaryEsm.IsBigEndian);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to parse primary record:[/] {ex.Message}");
            }
        }

        if (compare != null)
        {
            try
            {
                var data = EsmHelpers.GetRecordData(compareEsm.Data, compare, compareEsm.IsBigEndian);
                compareSubs = EsmRecordParser.ParseSubrecords(data, compareEsm.IsBigEndian);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to parse compare record:[/] {ex.Message}");
            }
        }

        // Display header comparison
        var headerTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("Property").Centered())
            .AddColumn(new TableColumn(primaryName).Centered())
            .AddColumn(new TableColumn(compareName).Centered())
            .AddColumn(new TableColumn("Match").Centered());

        _ = headerTable.AddRow(
            "Signature",
            primary?.Signature ?? "[dim]N/A[/]",
            compare?.Signature ?? "[dim]N/A[/]",
            primary?.Signature == compare?.Signature ? "[green]✓[/]" : "[red]✗[/]");

        _ = headerTable.AddRow(
            "Data Size",
            primary != null ? $"{primary.DataSize:N0}" : "[dim]N/A[/]",
            compare != null ? $"{compare.DataSize:N0}" : "[dim]N/A[/]",
            primary?.DataSize == compare?.DataSize ? "[green]✓[/]" : "[yellow]≠[/]");

        _ = headerTable.AddRow(
            "Flags",
            primary != null ? $"0x{primary.Flags:X8}" : "[dim]N/A[/]",
            compare != null ? $"0x{compare.Flags:X8}" : "[dim]N/A[/]",
            primary?.Flags == compare?.Flags ? "[green]✓[/]" : "[yellow]≠[/]");

        _ = headerTable.AddRow(
            "Subrecord Count",
            primarySubs.Count.ToString(),
            compareSubs.Count.ToString(),
            primarySubs.Count == compareSubs.Count ? "[green]✓[/]" : "[yellow]≠[/]");

        AnsiConsole.MarkupLine("[bold]Record Header Comparison:[/]");
        AnsiConsole.Write(headerTable);
        AnsiConsole.WriteLine();

        // Display subrecord sequence comparison
        AnsiConsole.MarkupLine("[bold]Subrecord Sequence Comparison:[/]");

        var subTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("#").RightAligned())
            .AddColumn(new TableColumn("Group").Centered())
            .AddColumn(new TableColumn($"{primaryName}").Centered())
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn(new TableColumn($"{compareName}").Centered())
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn(new TableColumn("Diff").Centered())
            .AddColumn(new TableColumn("Match").Centered());

        var maxCount = Math.Max(primarySubs.Count, compareSubs.Count);
        var mismatchCount = 0;
        var contentMismatchCount = 0;
        var primaryBlockNum = 0;
        var compareBlockNum = 0;
        var primaryScriptDepth = 0;
        var compareScriptDepth = 0;
        var detailMismatches = new List<(int Index, string Signature, int Size, string PrimaryDetails,
            string CompareDetails, string? DiffSummary)>();

        for (var i = 0; i < maxCount; i++)
        {
            var pSub = i < primarySubs.Count ? primarySubs[i] : null;
            var cSub = i < compareSubs.Count ? compareSubs[i] : null;

            // Track script blocks: SCHR starts a block, NEXT separates blocks
            var pGroup = GetSubrecordGroup(pSub?.Signature, ref primaryBlockNum, ref primaryScriptDepth);
            var cGroup = GetSubrecordGroup(cSub?.Signature, ref compareBlockNum, ref compareScriptDepth);

            // Display group indicator (use primary, or compare if primary missing)
            var groupDisplay = pGroup ?? cGroup ?? "";
            if (pGroup != null && cGroup != null && pGroup != cGroup)
            {
                groupDisplay = $"[yellow]{pGroup}|{cGroup}[/]";
            }
            else if (!string.IsNullOrEmpty(groupDisplay))
            {
                groupDisplay = $"[dim]{groupDisplay}[/]";
            }

            var sigMatch = pSub?.Signature == cSub?.Signature;
            var sizeMatch = pSub?.Data.Length == cSub?.Data.Length;
            var contentMatch = sigMatch && sizeMatch && pSub != null && cSub != null &&
                               pSub.Data.SequenceEqual(cSub.Data);
            var fullMatch = contentMatch;

            if (!sigMatch || !sizeMatch)
            {
                mismatchCount++;
            }
            else if (!contentMatch)
            {
                contentMismatchCount++;
            }

            var matchIcon = fullMatch ? "[green]✓[/]" : GetPartialMatchIcon(sigMatch, sizeMatch);

            var diffDisplay = "-";
            string? diffSummary = null;
            string? previewRow = null;
            if (sigMatch && sizeMatch && pSub != null && cSub != null && !contentMatch)
            {
                diffDisplay = BuildDiffDisplay(pSub.Signature,
                    pSub.Data, cSub.Data, primaryEsm.IsBigEndian, compareEsm.IsBigEndian, out diffSummary);
                previewRow = BuildPreviewRowText(pSub.Data, cSub.Data, 16);

                var hasPrimaryDetails = EsmDisplayHelpers.TryFormatSubrecordDetails(pSub.Signature, pSub.Data,
                    primaryEsm.IsBigEndian, out var primaryDetails);
                var hasCompareDetails = EsmDisplayHelpers.TryFormatSubrecordDetails(cSub.Signature, cSub.Data,
                    compareEsm.IsBigEndian, out var compareDetails);

                if (hasPrimaryDetails || hasCompareDetails)
                {
                    detailMismatches.Add((
                        i + 1,
                        pSub.Signature,
                        pSub.Data.Length,
                        hasPrimaryDetails ? primaryDetails : "(unparsed)",
                        hasCompareDetails ? compareDetails : "(unparsed)",
                        diffSummary));
                }
            }

            // Color code the signatures
            var pSigDisplay = pSub != null ? $"[cyan]{pSub.Signature}[/]" : "[dim]---[/]";
            var cSigDisplay = cSub != null ? $"[cyan]{cSub.Signature}[/]" : "[dim]---[/]";

            if (!sigMatch)
            {
                pSigDisplay = pSub != null ? $"[red]{pSub.Signature}[/]" : "[dim]---[/]";
                cSigDisplay = cSub != null ? $"[red]{cSub.Signature}[/]" : "[dim]---[/]";
            }

            _ = subTable.AddRow(
                (i + 1).ToString(),
                groupDisplay,
                pSigDisplay,
                pSub != null ? pSub.Data.Length.ToString() : "-",
                cSigDisplay,
                cSub != null ? cSub.Data.Length.ToString() : "-",
                diffDisplay,
                matchIcon);

            if (previewRow != null)
            {
                _ = subTable.AddRow(
                    "",
                    "",
                    "[dim]preview[/]",
                    "",
                    "",
                    "",
                    previewRow,
                    "");
            }
        }

        AnsiConsole.Write(subTable);

        // Summary
        AnsiConsole.WriteLine();
        if (mismatchCount == 0 && contentMismatchCount == 0)
        {
            AnsiConsole.MarkupLine($"[green]✓ All {maxCount} subrecords match in sequence, size, and content[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[yellow]⚠ {mismatchCount} position mismatches, {contentMismatchCount} content mismatches[/]");
        }

        // Show unique subrecords in each file
        var primarySigCounts = primarySubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.Count());
        var compareSigCounts = compareSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.Count());

        var onlyInPrimary = primarySigCounts.Keys.Except(compareSigCounts.Keys).ToList();
        var onlyInCompare = compareSigCounts.Keys.Except(primarySigCounts.Keys).ToList();

        if (onlyInPrimary.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]Subrecords only in {primaryName}:[/] {string.Join(", ", onlyInPrimary)}");
        }

        if (onlyInCompare.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]Subrecords only in {compareName}:[/] {string.Join(", ", onlyInCompare)}");
        }

        // Count differences for shared subrecord types
        var sharedTypes = primarySigCounts.Keys.Intersect(compareSigCounts.Keys).ToList();
        var countDiffs = sharedTypes.Where(t => primarySigCounts[t] != compareSigCounts[t]).ToList();
        if (countDiffs.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Count differences for shared subrecord types:[/]");
            foreach (var sig in countDiffs)
            {
                AnsiConsole.MarkupLine($"  [cyan]{sig}[/]: {primarySigCounts[sig]} vs {compareSigCounts[sig]}");
            }
        }

        if (detailMismatches.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Content mismatch details (interpreted):[/]");

            foreach (var mismatch in detailMismatches)
            {
                var diffText = mismatch.DiffSummary != null ? $" (diff {mismatch.DiffSummary})" : string.Empty;
                AnsiConsole.MarkupLine(
                    $"[cyan]#{mismatch.Index} {mismatch.Signature}[/] ({mismatch.Size} bytes){diffText}");
                AnsiConsole.MarkupLine($"  {primaryName}: {EscapeMarkup(mismatch.PrimaryDetails)}");
                AnsiConsole.MarkupLine($"  {compareName}: {EscapeMarkup(mismatch.CompareDetails)}");
            }
        }

        if (showHex && primary != null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]Hex Dump ({primaryName}, first 256 bytes):[/]");
            var primaryData = EsmHelpers.GetRecordData(primaryEsm.Data, primary, primaryEsm.IsBigEndian);
            EsmDisplayHelpers.RenderHexDumpPanel(primaryData, 256);
        }

        if (showHex && compare != null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]Hex Dump ({compareName}, first 256 bytes):[/]");
            var compareData = EsmHelpers.GetRecordData(compareEsm.Data, compare, compareEsm.IsBigEndian);
            EsmDisplayHelpers.RenderHexDumpPanel(compareData, 256);
        }

        AnsiConsole.WriteLine();
    }

    /// <summary>
    ///     Determines the grouping/context of a subrecord for display purposes.
    ///     Tracks script blocks, response groups, etc.
    /// </summary>
    private static string? GetSubrecordGroup(string? signature, ref int blockNum, ref int scriptDepth)
    {
        return signature == null
            ? null
            : signature switch
            {
                // TRDT starts a response block (dialogue response)
                "TRDT" => $"Resp{++blockNum}",
                "NAM1" or "NAM2" => $"Resp{blockNum}",

                // Script blocks: SCHR starts, NEXT separates Begin/End
                "SCHR" when scriptDepth == 0 => BeginNewScriptBlock(ref scriptDepth, "Begin"),
                "SCHR" => $"Script{scriptDepth}",
                "NEXT" => BeginNewScriptBlock(ref scriptDepth, "End"),
                "SCDA" or "SCTX" or "SCRO" or "SCRV" or "SLSD" or "SCVR" => $"Script{scriptDepth}",

                // Condition groups
                "CTDA" => "Cond",

                // Topic choice links
                "TCLT" or "TCLF" => "Choice",

                _ => null
            };
    }

    private static string BeginNewScriptBlock(ref int scriptDepth, string name)
    {
        scriptDepth++;
        return $"{name}";
    }

    private static FirstDiff? FindFirstDiff(byte[] left, byte[] right)
    {
        var len = Math.Min(left.Length, right.Length);
        for (var i = 0; i < len; i++)
        {
            if (left[i] != right[i])
            {
                return new FirstDiff(i, left[i], right[i]);
            }
        }

        return left.Length != right.Length
            ? new FirstDiff(len, len < left.Length ? left[len] : (byte)0,
                len < right.Length ? right[len] : (byte)0)
            : null;
    }

    private static string BuildDiffDisplay(string subSignature, byte[] left, byte[] right,
        bool leftBigEndian, bool rightBigEndian, out string? diffSummary)
    {
        diffSummary = null;

        var first = FindFirstDiff(left, right);
        if (first == null)
        {
            return "-";
        }

        var diffCount = CountDiffBytes(left, right);

        // Simple heuristic: common FormID subrecord names
        var isFormId = IsLikelyFormIdSubrecord(subSignature);

        if (left.Length == 4 && right.Length == 4)
        {
            var leftValue = ReadUInt32Value(left, 0, leftBigEndian);
            var rightValue = ReadUInt32Value(right, 0, rightBigEndian);
            diffSummary = isFormId
                ? $"fid 0x{leftValue:X8}->0x{rightValue:X8}"
                : $"u32 {leftValue:X8}->{rightValue:X8}";
            return
                $"[yellow]{(isFormId ? "FormID" : "UInt32")}[/] [dim]first {(isFormId ? "0x" : string.Empty)}{leftValue:X8}, second {(isFormId ? "0x" : string.Empty)}{rightValue:X8}[/] [dim](delta {diffCount})[/]";
        }

        diffSummary =
            $"offset 0x{first.Value.Offset:X}, first {first.Value.Left:X2}, second {first.Value.Right:X2} (delta {diffCount})";
        return
            $"[yellow]offset 0x{first.Value.Offset:X}[/] [dim]first {first.Value.Left:X2}, second {first.Value.Right:X2}[/] [dim](delta {diffCount})[/]";
    }

    private static int CountDiffBytes(byte[] left, byte[] right)
    {
        var len = Math.Min(left.Length, right.Length);
        var count = 0;
        for (var i = 0; i < len; i++)
        {
            if (left[i] != right[i])
            {
                count++;
            }
        }

        count += Math.Abs(left.Length - right.Length);
        return count;
    }

    private static uint ReadUInt32Value(byte[] data, int offset, bool bigEndian)
    {
        return offset + 4 > data.Length
            ? 0
            : bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static string GetPartialMatchIcon(bool sigMatch, bool sizeMatch)
    {
        return sigMatch && sizeMatch ? "[yellow]≈[/]" : "[red]✗[/]";
    }

    private static string BuildPreviewRowText(byte[] left, byte[] right, int byteCount)
    {
        var first = FindFirstDiff(left, right);
        var leftPreview = FormatHexBytesWithHighlight(left, byteCount, first?.Offset, true);
        var rightPreview = FormatHexBytesWithHighlight(right, byteCount, first?.Offset, false);
        return $"[dim]first {byteCount} bytes[/]  first: {leftPreview}   second: {rightPreview}";
    }

    private static string FormatHexBytesWithHighlight(byte[] data, int count, int? diffOffset, bool isPrimary)
    {
        if (data.Length == 0)
        {
            return "-";
        }

        var max = Math.Min(count, data.Length);
        var builder = new StringBuilder((max * 3) - 1);
        for (var i = 0; i < max; i++)
        {
            if (i > 0)
            {
                _ = builder.Append(' ');
            }

            var hex = data[i].ToString("X2");
            _ = diffOffset.HasValue && i == diffOffset.Value
                ? builder.Append(isPrimary ? "[yellow]" : "[red]").Append(hex).Append("[/]")
                : builder.Append(hex);
        }

        return builder.ToString();
    }

    private static string EscapeMarkup(string value)
    {
        return Markup.Escape(value);
    }

    /// <summary>
    ///     Simple heuristic to detect common FormID subrecord signatures.
    /// </summary>
    private static bool IsLikelyFormIdSubrecord(string signature)
    {
        return signature is "NAME" or "INAM" or "TPLT" or "VTCK" or "LNAM" or "LTMP" or "REPL" or "ZNAM"
            or "XOWN" or "XEZN" or "XCAS" or "XCIM" or "XCMO" or "XCWT" or "PKID" or "ENAM" or "HNAM"
            or "NAM6" or "NAM7" or "NAM8" or "NAM9" or "SCRI" or "GNAM" or "BNAM" or "SNAM" or "KNAM"
            or "PNAM" or "CNAM" or "DNAM" or "ONAM" or "XLKR" or "XCLP" or "XLCN" or "XMRK" or "XPOD";
    }

    private readonly record struct FirstDiff(int Offset, byte Left, byte Right);
}
