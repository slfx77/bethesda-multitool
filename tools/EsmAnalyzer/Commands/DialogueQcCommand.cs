using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Quality-check a BethesdaAudioTranscriber CSV (File,FormID,VoiceType,Speaker,Quest,Source,Text)
///     against an ESM proper-noun vocabulary. Fixes whisper-source rows:
///     - Collapses double spaces after punctuation
///     - Restores canonical capitalization for case-insensitive vocab matches
///     - Auto-fixes single-edit-distance proper-noun typos (guarded)
///     and writes a sidecar report listing every change and any flagged candidates.
/// </summary>
public static class DialogueQcCommand
{
    public static Command CreateDialogueQcCommand()
    {
        var command = new Command(
            "dialogue-qc",
            "Quality-check a transcriber CSV against an ESM proper-noun vocabulary");

        var csvArg = new Argument<string>("csv") { Description = "Path to the transcriber CSV" };
        var esmArg = new Argument<string>("esm") { Description = "Path to the ESM whose proper nouns form the vocabulary" };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Show what would change but don't write" };
        var noBackupOption = new Option<bool>("--no-backup") { Description = "Skip writing the .csv.bak backup" };
        var reportOption = new Option<string?>("--report")
        {
            Description = "Path for the QC report (default: <csv>.qc-report.txt)"
        };
        var minEditLenOption = new Option<int>("--min-edit-len")
        {
            Description = "Minimum token length for edit-distance-1 auto-fix (default: 5)",
            DefaultValueFactory = _ => 5
        };

        command.Arguments.Add(csvArg);
        command.Arguments.Add(esmArg);
        command.Options.Add(dryRunOption);
        command.Options.Add(noBackupOption);
        command.Options.Add(reportOption);
        command.Options.Add(minEditLenOption);

        command.SetAction(parseResult =>
        {
            var csvPath = parseResult.GetValue(csvArg)!;
            var esmPath = parseResult.GetValue(esmArg)!;
            var dryRun = parseResult.GetValue(dryRunOption);
            var noBackup = parseResult.GetValue(noBackupOption);
            var reportPath = parseResult.GetValue(reportOption);
            var minEditLen = parseResult.GetValue(minEditLenOption);

            return Run(csvPath, esmPath, dryRun, noBackup, reportPath, minEditLen);
        });

        return command;
    }

    private static int Run(string csvPath, string esmPath, bool dryRun, bool noBackup, string? reportPath, int minEditLen)
    {
        AnsiConsole.MarkupLine("[bold cyan]Dialogue CSV QC[/]");
        AnsiConsole.MarkupLine($"[grey]CSV:[/] {csvPath}");
        AnsiConsole.MarkupLine($"[grey]ESM:[/] {esmPath}");
        AnsiConsole.WriteLine();

        if (!File.Exists(csvPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] CSV not found: {csvPath}");
            return 1;
        }

        // ── 1. Build vocabulary from ESM ───────────────────────────────────────
        var esm = EsmFileLoader.Load(esmPath, printStatus: true);
        if (esm == null)
        {
            return 1;
        }

        AnsiConsole.MarkupLine("[grey]Extracting proper-noun vocabulary...[/]");
        var vocab = BuildVocabulary(esm.Data);
        AnsiConsole.MarkupLine(
            $"[grey]Vocabulary: {vocab.CanonicalByLower.Count:N0} unique tokens from {vocab.FullStringsScanned:N0} FULL strings " +
            $"(NPC/CREA: {vocab.NpcCount:N0}, CELL: {vocab.CellCount:N0}, WRLD: {vocab.WrldCount:N0}, " +
            $"REGN: {vocab.RegnCount:N0}, FACT: {vocab.FactCount:N0})[/]");
        AnsiConsole.WriteLine();

        // ── 2. Load and process CSV ────────────────────────────────────────────
        AnsiConsole.MarkupLine("[grey]Reading CSV...[/]");
        var rawText = File.ReadAllText(csvPath);
        var newlineStyle = rawText.Contains("\r\n") ? "\r\n" : "\n";
        var rows = DialogueQcCsvIo.Parse(rawText);

        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] CSV is empty");
            return 1;
        }

        var header = rows[0];
        var textIdx = Array.IndexOf(header, "Text");
        var sourceIdx = Array.IndexOf(header, "Source");
        var formIdIdx = Array.IndexOf(header, "FormID");
        var voiceTypeIdx = Array.IndexOf(header, "VoiceType");
        if (textIdx < 0 || sourceIdx < 0)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] CSV header missing 'Text' or 'Source' column: {string.Join(", ", header)}");
            return 1;
        }

        var report = new QcReport();
        var changedRows = 0;

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Length <= Math.Max(textIdx, sourceIdx))
            {
                continue;
            }

            var source = row[sourceIdx];
            // Only modify rows the user has not authored or accepted as final.
            if (!source.Equals("whisper", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var originalText = row[textIdx];
            if (string.IsNullOrEmpty(originalText))
            {
                continue;
            }

            var ctx = new RowContext
            {
                LineNumber = i + 1,
                FormId = formIdIdx >= 0 && formIdIdx < row.Length ? row[formIdIdx] : "",
                VoiceType = voiceTypeIdx >= 0 && voiceTypeIdx < row.Length ? row[voiceTypeIdx] : ""
            };

            var fixedText = ApplyFixes(originalText, vocab, ctx, report, minEditLen);
            if (!ReferenceEquals(fixedText, originalText) && fixedText != originalText)
            {
                row[textIdx] = fixedText;
                changedRows++;
                report.ChangedRowSamples.Add((ctx.LineNumber, ctx.FormId, originalText, fixedText));
            }
        }

        // ── 3. Write report ────────────────────────────────────────────────────
        reportPath ??= csvPath + ".qc-report.txt";
        WriteReport(reportPath, csvPath, esmPath, vocab, report, changedRows, rows.Count - 1);
        AnsiConsole.MarkupLine($"[green]Report:[/] {reportPath}");

        // ── 4. Print summary + sample ──────────────────────────────────────────
        var summary = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Count[/]");
        _ = summary.AddRow("CSV rows", $"{rows.Count - 1:N0}");
        _ = summary.AddRow("Whisper rows scanned", $"{report.WhisperRowsScanned:N0}");
        _ = summary.AddRow("Rows changed", $"{changedRows:N0}");
        _ = summary.AddRow("Double-space fixes", $"{report.DoubleSpaceFixes:N0}");
        _ = summary.AddRow("Case-only fixes (exact vocab)", $"{report.CaseOnlyFixes:N0}");
        _ = summary.AddRow("Edit-distance-1 fixes", $"{report.EditDistance1Fixes:N0}");
        _ = summary.AddRow("Flagged (ambiguous)", $"{report.AmbiguousFlags:N0}");
        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();

        if (report.ChangedRowSamples.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]Sample fixes (first 10):[/]");
            var sampleTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Line")
                .AddColumn("FormID")
                .AddColumn("Before")
                .AddColumn("After");
            foreach (var (line, formId, before, after) in report.ChangedRowSamples.Take(10))
            {
                _ = sampleTable.AddRow(
                    line.ToString(CultureInfo.InvariantCulture),
                    formId,
                    Markup.Escape(Truncate(before, 80)),
                    Markup.Escape(Truncate(after, 80)));
            }
            AnsiConsole.Write(sampleTable);
        }

        // ── 5. Write CSV (unless dry-run) ──────────────────────────────────────
        if (dryRun)
        {
            AnsiConsole.MarkupLine("[yellow]Dry-run:[/] CSV not modified.");
            return 0;
        }

        if (changedRows == 0)
        {
            AnsiConsole.MarkupLine("[grey]No changes needed; CSV not rewritten.[/]");
            return 0;
        }

        if (!noBackup)
        {
            var backupPath = csvPath + ".bak";
            File.Copy(csvPath, backupPath, overwrite: true);
            AnsiConsole.MarkupLine($"[green]Backup:[/] {backupPath}");
        }

        var sb = new StringBuilder(rawText.Length + (changedRows * 16));
        for (var i = 0; i < rows.Count; i++)
        {
            sb.Append(DialogueQcCsvIo.SerializeRow(rows[i]));
            sb.Append(newlineStyle);
        }
        File.WriteAllText(csvPath, sb.ToString());
        AnsiConsole.MarkupLine($"[green]Wrote:[/] {csvPath}");

        return 0;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Vocabulary extraction
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class Vocabulary
    {
        // Lowercase token → canonical capitalization. Indexes EVERY proper-noun token,
        // including components of multi-word FULL strings — used for edit-distance fuzzy match.
        public Dictionary<string, string> CanonicalByLower { get; } = new(StringComparer.Ordinal);
        // Lowercase tokens that originated from a STANDALONE single-word FULL ("Marcus",
        // "Jacobstown"). Only these are eligible for case-only auto-fixes — case-fixing on
        // a multi-word-component like "strip" (from "The Strip") would corrupt every
        // normal use of "strip" in dialogue.
        public HashSet<string> StandaloneLower { get; } = new(StringComparer.Ordinal);
        // First-letter bucketed list of canonical tokens for fast edit-distance pre-filter.
        public Dictionary<char, List<string>> ByFirstChar { get; } = new();
        // Full-string set of FULL display strings for context in reports.
        public HashSet<string> FullStrings { get; } = new(StringComparer.Ordinal);

        public int FullStringsScanned { get; set; }
        public int NpcCount { get; set; }
        public int CellCount { get; set; }
        public int WrldCount { get; set; }
        public int RegnCount { get; set; }
        public int FactCount { get; set; }

        public void AddToken(string token, bool isStandalone)
        {
            if (token.Length < 3)
            {
                return;
            }
            var lower = token.ToLowerInvariant();
            if (isStandalone)
            {
                StandaloneLower.Add(lower);
            }
            if (CanonicalByLower.TryGetValue(lower, out var existing))
            {
                // Prefer the capitalization that begins with uppercase.
                if (!char.IsUpper(existing[0]) && char.IsUpper(token[0]))
                {
                    CanonicalByLower[lower] = token;
                }
                return;
            }
            CanonicalByLower[lower] = token;
            var firstLower = char.ToLowerInvariant(token[0]);
            if (!ByFirstChar.TryGetValue(firstLower, out var list))
            {
                list = new List<string>();
                ByFirstChar[firstLower] = list;
            }
            list.Add(token);
        }
    }

    private static Vocabulary BuildVocabulary(byte[] data)
    {
        var vocab = new Vocabulary();
        var records = EsmParser.EnumerateRecords(data);
        foreach (var rec in records)
        {
            var sig = rec.Header.Signature;
            bool include = sig switch
            {
                "NPC_" or "CREA" or "CELL" or "WRLD" or "REGN" or "FACT" => true,
                _ => false
            };
            if (!include)
            {
                continue;
            }

            var full = rec.Subrecords.FirstOrDefault(s => s.Signature == "FULL")?.DataAsString;
            if (string.IsNullOrWhiteSpace(full))
            {
                continue;
            }
            vocab.FullStringsScanned++;
            vocab.FullStrings.Add(full);

            switch (sig)
            {
                case "NPC_": case "CREA": vocab.NpcCount++; break;
                case "CELL": vocab.CellCount++; break;
                case "WRLD": vocab.WrldCount++; break;
                case "REGN": vocab.RegnCount++; break;
                case "FACT": vocab.FactCount++; break;
            }

            var tokens = Tokenize(full).ToList();
            var isSingleWord = tokens.Count == 1;
            foreach (var token in tokens)
            {
                // ANY token (even from a single-word FULL) that collides with a
                // common English word is unsafe to seed vocab — case-fixing every
                // "guard" or "vault" in dialogue would be noise.
                if (DialogueQcStopWords.EnglishStopWords.Contains(token))
                {
                    continue;
                }
                // Multi-word FULLs add even more guards: ignore structural words
                // and tokens too short to be distinctive.
                if (!isSingleWord)
                {
                    if (token.Length < 4)
                    {
                        continue;
                    }
                    if (DialogueQcStopWords.StructuralWords.Contains(token))
                    {
                        continue;
                    }
                }
                vocab.AddToken(token, isStandalone: isSingleWord);
            }
        }
        return vocab;
    }

    private static readonly Regex TokenSplit = new(@"[A-Za-z]{3,}", RegexOptions.Compiled);

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match m in TokenSplit.Matches(text))
        {
            yield return m.Value;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Text fixes
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class RowContext
    {
        public int LineNumber;
        public string FormId = "";
        public string VoiceType = "";
    }

    private sealed class QcReport
    {
        public int WhisperRowsScanned;
        public int DoubleSpaceFixes;
        public int CaseOnlyFixes;
        public int EditDistance1Fixes;
        public int AmbiguousFlags;
        public List<(int Line, string FormId, string Before, string After)> ChangedRowSamples = new();
        public List<string> ChangeLog = new();
        public List<string> AmbiguousLog = new();
    }

    // Collapse 2+ whitespace after sentence punctuation into a single space.
    private static readonly Regex DoubleSpaceAfterPunct = new(
        @"([.!?,;:])[ \t]{2,}",
        RegexOptions.Compiled);

    // Word token in text: contiguous letters/apostrophes. We match each position and
    // decide whether to rewrite it.
    private static readonly Regex WordToken = new(
        @"[A-Za-z][A-Za-z']*",
        RegexOptions.Compiled);

    private static string ApplyFixes(string text, Vocabulary vocab, RowContext ctx, QcReport report, int minEditLen)
    {
        report.WhisperRowsScanned++;
        var original = text;

        // Step 1: double-space-after-punctuation collapse.
        var dsCount = 0;
        var afterDoubleSpace = DoubleSpaceAfterPunct.Replace(text, m =>
        {
            dsCount++;
            return m.Groups[1].Value + " ";
        });
        if (dsCount > 0)
        {
            report.DoubleSpaceFixes += dsCount;
            text = afterDoubleSpace;
        }

        // Step 2: scan word tokens and apply vocab fixes.
        var result = new StringBuilder(text.Length);
        var lastEnd = 0;
        var matches = WordToken.Matches(text);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            result.Append(text, lastEnd, m.Index - lastEnd);

            var token = m.Value;
            // Skip contractions/possessives like "Benny's", "don't", "we're". The
            // apostrophe-restore round trip is fragile (drops/duplicates trailing
            // chars when canonical length differs), and these are rarely the
            // proper-noun typos we're trying to fix.
            if (token.Contains('\''))
            {
                result.Append(token);
                lastEnd = m.Index + m.Length;
                continue;
            }
            var letters = token;
            var sentenceInitial = IsSentenceInitial(text, m.Index);

            var replacement = TryFixToken(letters, sentenceInitial, vocab, ctx, report, minEditLen);
            if (replacement != null && replacement != letters)
            {
                result.Append(replacement);
            }
            else
            {
                result.Append(token);
            }

            lastEnd = m.Index + m.Length;
        }
        result.Append(text, lastEnd, text.Length - lastEnd);

        var finalText = result.ToString();
        if (finalText != original)
        {
            return finalText;
        }
        return original;
    }

    private static bool IsSentenceInitial(string text, int index)
    {
        // Walk back skipping whitespace and quote/bracket marks; if we hit . ! ? or start of string, we're sentence-initial.
        for (var i = index - 1; i >= 0; i--)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == '(' || c == '[' || c == '{' || c == '-')
            {
                continue;
            }
            return c is '.' or '!' or '?';
        }
        return true;
    }

    private static string? TryFixToken(
        string token, bool sentenceInitial, Vocabulary vocab, RowContext ctx, QcReport report, int minEditLen)
    {
        // Case-only fix needs ≥ 4 chars to avoid clobbering short common words
        // like "the", "for", "all". Fuzzy fix bar is enforced separately below.
        if (token.Length < 4)
        {
            return null;
        }

        // Hard skip on common English words even if they show up in vocab somehow.
        if (DialogueQcStopWords.EnglishStopWords.Contains(token))
        {
            return null;
        }

        var lower = token.ToLowerInvariant();

        // (a) Token's lowercase form IS in vocab.
        if (vocab.CanonicalByLower.TryGetValue(lower, out var canonical))
        {
            // Already correctly capitalized — no work to do, and crucially: do NOT fall
            // through to the fuzzy branch (which would re-discover this same token at
            // distance 0 and log a no-op fix).
            if (canonical == token)
            {
                return null;
            }
            // Components of multi-word FULLs ("Strip" from "The Strip") collide with
            // normal English use — we can't safely case-fix them either way.
            if (!vocab.StandaloneLower.Contains(lower))
            {
                return null;
            }
            // Sentence-initial special cases.
            if (sentenceInitial && !char.IsUpper(canonical[0]))
            {
                return null;
            }
            if (sentenceInitial && string.Equals(canonical, char.ToUpperInvariant(token[0]) + token[1..],
                StringComparison.Ordinal))
            {
                return null;
            }
            report.CaseOnlyFixes++;
            report.ChangeLog.Add(
                $"L{ctx.LineNumber} [{ctx.FormId}/{ctx.VoiceType}] case:  '{token}' → '{canonical}'");
            return canonical;
        }

        // (b) Edit-distance-1 against vocabulary. Guarded.
        if (token.Length < minEditLen || sentenceInitial || !char.IsUpper(token[0]))
        {
            return null;
        }
        if (DialogueQcStopWords.EnglishStopWords.Contains(token))
        {
            return null;
        }
        // Possessive/plural guard: if removing a trailing 's' yields a vocab match,
        // the writer probably meant the possessive/plural form (e.g. "Bennys" for
        // "Benny's", "Tabithas" for "Tabitha's", "Legions" for the plural). Leave it.
        if (lower.Length > 2 && lower[^1] == 's' &&
            vocab.CanonicalByLower.ContainsKey(lower[..^1]))
        {
            return null;
        }

        // First-character bucket primary search, then ±1 bucket for substitution at position 0.
        var firstLetter = char.ToLowerInvariant(token[0]);
        var bestMatches = new List<string>();
        if (vocab.ByFirstChar.TryGetValue(firstLetter, out var sameFirst))
        {
            DialogueQcEditDistance.CollectEditDistance1(token, sameFirst, minEditLen, bestMatches);
        }
        // Also consider vocab words with different first char (covers "Kris" → "Chris")
        // but only if we haven't already found matches in the same-first-letter bucket.
        if (bestMatches.Count == 0)
        {
            foreach (var (_, list) in vocab.ByFirstChar)
            {
                DialogueQcEditDistance.CollectEditDistance1(token, list, minEditLen, bestMatches);
            }
        }

        if (bestMatches.Count == 0)
        {
            return null;
        }

        // Dedupe by lowercase (CanonicalByLower already gives us unique tokens, but be paranoid).
        var distinct = bestMatches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (distinct.Count > 1)
        {
            report.AmbiguousFlags++;
            report.AmbiguousLog.Add(
                $"L{ctx.LineNumber} [{ctx.FormId}/{ctx.VoiceType}] ambiguous: '{token}' → {{{string.Join(", ", distinct)}}}");
            return null;
        }

        var pick = distinct[0];
        // Only apply if pick starts with uppercase — proper-noun convention.
        if (!char.IsUpper(pick[0]))
        {
            return null;
        }
        // Skip no-op fixes (the existing token already matches canonical, just routed
        // here because case-fix branch was disabled by the standalone-only gate).
        if (string.Equals(pick, token, StringComparison.Ordinal))
        {
            return null;
        }

        report.EditDistance1Fixes++;
        report.ChangeLog.Add(
            $"L{ctx.LineNumber} [{ctx.FormId}/{ctx.VoiceType}] fuzzy: '{token}' → '{pick}'");
        return pick;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Report I/O
    // ──────────────────────────────────────────────────────────────────────────

    private static void WriteReport(
        string path, string csvPath, string esmPath, Vocabulary vocab, QcReport report,
        int changedRows, int totalRows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Dialogue CSV QC Report");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"CSV: {csvPath}");
        sb.AppendLine($"ESM: {esmPath}");
        sb.AppendLine($"Vocabulary tokens: {vocab.CanonicalByLower.Count:N0} from {vocab.FullStringsScanned:N0} FULL strings");
        sb.AppendLine($"  NPC/CREA: {vocab.NpcCount:N0}   CELL: {vocab.CellCount:N0}   WRLD: {vocab.WrldCount:N0}   REGN: {vocab.RegnCount:N0}   FACT: {vocab.FactCount:N0}");
        sb.AppendLine();
        sb.AppendLine($"Total CSV rows (excl. header): {totalRows:N0}");
        sb.AppendLine($"Whisper rows scanned: {report.WhisperRowsScanned:N0}");
        sb.AppendLine($"Rows changed: {changedRows:N0}");
        sb.AppendLine($"  Double-space-after-punctuation fixes: {report.DoubleSpaceFixes:N0}");
        sb.AppendLine($"  Case-only fixes (exact vocab match):   {report.CaseOnlyFixes:N0}");
        sb.AppendLine($"  Edit-distance-1 fuzzy fixes:           {report.EditDistance1Fixes:N0}");
        sb.AppendLine($"  Ambiguous candidates (not applied):    {report.AmbiguousFlags:N0}");
        sb.AppendLine();

        sb.AppendLine("=== CHANGES APPLIED ===");
        if (report.ChangeLog.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var line in report.ChangeLog)
            {
                sb.AppendLine(line);
            }
        }
        sb.AppendLine();

        sb.AppendLine("=== AMBIGUOUS CANDIDATES (review manually) ===");
        if (report.AmbiguousLog.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var line in report.AmbiguousLog)
            {
                sb.AppendLine(line);
            }
        }
        sb.AppendLine();

        sb.AppendLine("=== ROW-LEVEL BEFORE/AFTER (sample, first 200) ===");
        foreach (var (line, formId, before, after) in report.ChangedRowSamples.Take(200))
        {
            sb.AppendLine($"L{line} [{formId}]");
            sb.AppendLine($"  before: {before}");
            sb.AppendLine($"  after:  {after}");
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
