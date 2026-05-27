using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Quality-check a FalloutAudioTranscriber CSV (File,FormID,VoiceType,Speaker,Quest,Source,Text)
///     against an ESM proper-noun vocabulary. Fixes whisper-source rows:
///     - Collapses double spaces after punctuation
///     - Restores canonical capitalization for case-insensitive vocab matches
///     - Auto-fixes single-edit-distance proper-noun typos (guarded)
///     and writes a sidecar report listing every change and any flagged candidates.
/// </summary>
public static class DialogueQcCommand
{
    // Common English words that would corrupt normal text if "fixed" to a proper-noun
    // capitalization just because the same letters appear in an ESM compound name
    // (e.g. "Great" in "Great Khans" must not turn every "great" into "Great").
    // Used both at vocab-build time (excluded as multi-word FULL tokens) and at
    // fix-application time (skipped on the case-fix and edit-distance paths).
    private static readonly HashSet<string> EnglishStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Articles / pronouns / determiners / conjunctions / prepositions
        "the", "a", "an", "and", "or", "but", "nor", "yet", "so", "if", "then",
        "this", "that", "these", "those", "such", "some", "any", "all", "both", "each", "every", "no", "not",
        "i", "me", "my", "mine", "you", "your", "yours", "he", "him", "his", "she", "her", "hers", "it", "its",
        "we", "us", "our", "ours", "they", "them", "their", "theirs", "who", "whom", "whose", "which", "what",
        "of", "to", "in", "on", "at", "by", "for", "with", "without", "from", "into", "onto", "upon", "over",
        "under", "above", "below", "between", "through", "across", "around", "about", "against", "along",
        "among", "before", "after", "since", "until", "during", "while", "as", "than", "though", "although",
        "because", "unless", "where", "when", "whenever", "wherever", "whether", "until",
        // Auxiliary / common verbs in low frequency forms
        "is", "am", "are", "was", "were", "be", "been", "being",
        "do", "does", "did", "done", "doing",
        "have", "has", "had", "having",
        "can", "could", "shall", "should", "will", "would", "may", "might", "must",
        "go", "goes", "going", "gone", "went",
        "come", "comes", "came", "coming",
        "get", "gets", "got", "getting",
        "say", "says", "said", "saying",
        "see", "sees", "saw", "seen", "seeing",
        "make", "makes", "made", "making",
        "take", "takes", "took", "taken", "taking",
        "give", "gives", "gave", "given", "giving",
        "know", "knows", "knew", "known", "knowing",
        "think", "thinks", "thought", "thinking",
        "look", "looks", "looked", "looking",
        "want", "wants", "wanted", "wanting",
        "use", "uses", "used", "using",
        "find", "finds", "found", "finding",
        "tell", "tells", "told", "telling",
        "ask", "asks", "asked", "asking",
        "work", "works", "worked", "working",
        "try", "tries", "tried", "trying",
        "leave", "leaves", "left", "leaving",
        "call", "calls", "called", "calling",
        "feel", "feels", "felt", "feeling",
        "keep", "keeps", "kept", "keeping",
        "let", "lets", "letting",
        "begin", "begins", "began", "begun",
        "show", "shows", "showed", "shown",
        "bring", "brings", "brought",
        "follow", "follows", "followed",
        "stand", "stands", "stood",
        "lose", "loses", "lost", "losing",
        "pay", "pays", "paid",
        "live", "lives", "lived", "living",
        "meet", "meets", "met",
        "run", "runs", "ran", "running",
        "move", "moves", "moved", "moving",
        "like", "likes", "liked",
        "believe", "believes", "believed",
        "hold", "holds", "held", "holding",
        "turn", "turns", "turned",
        "set", "sets", "setting",
        "start", "starts", "started", "starting",
        "lead", "leads", "led",
        "hear", "hears", "heard",
        "stop", "stops", "stopped",
        "play", "plays", "played",
        "speak", "speaks", "spoke", "spoken",
        "read", "reads", "reading",
        "spend", "spent",
        "grow", "grows", "grew", "grown",
        "win", "wins", "won",
        "buy", "buys", "bought",
        "wait", "waits", "waited",
        "serve", "served",
        "die", "dies", "died",
        "send", "sent",
        "build", "builds", "built",
        "stay", "stays", "stayed",
        "fall", "falls", "fell", "fallen",
        "cut", "cuts", "cutting",
        "reach", "reached",
        "kill", "kills", "killed",
        "remain", "remained",
        "carry", "carries", "carried",
        "raise", "raised",
        "drink", "drinks", "drank",
        "open", "opens", "opened",
        "walk", "walks", "walked",
        "wear", "wears", "wore", "worn",
        "fight", "fights", "fought",
        "sit", "sits", "sat",
        "lay", "lays", "laid",
        "watch", "watches", "watched",
        "rest", "rests", "rested",
        "wonder", "wondered",
        "hope", "hopes", "hoped",
        "pull", "pulls", "pulled",
        "draw", "draws", "drew", "drawn",
        "throw", "throws", "threw", "thrown",
        // Common adjectives / adverbs / quantifiers / time / place
        "good", "great", "bad", "old", "new", "young", "long", "short", "high", "low",
        "big", "small", "large", "little", "tall", "wide", "deep",
        "more", "less", "many", "much", "few", "most", "least", "enough", "lot", "lots",
        "first", "second", "third", "last", "next", "only",
        "best", "better", "worst", "worse",
        "early", "late", "later", "soon", "sooner", "now", "never", "always", "often",
        "again", "still", "ever", "ago", "today", "yesterday", "tomorrow",
        "here", "there", "everywhere", "anywhere", "nowhere",
        "out", "in", "inside", "outside", "back", "front", "side",
        "up", "down", "off", "on", "away", "near", "far",
        "left", "right", "north", "south", "east", "west",
        "home", "house", "room", "town", "city", "world",
        "way", "ways", "place", "places", "time", "times",
        "day", "days", "night", "nights", "year", "years", "week", "weeks", "month", "months", "hour", "hours",
        "thing", "things", "stuff",
        "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "zero", "hundred", "thousand", "million",
        "really", "very", "just", "even", "also", "too", "perhaps", "maybe", "actually",
        "yes", "no", "okay", "ok",
        "name", "names",
        "people", "person", "man", "men", "woman", "women", "boy", "girl", "kid", "child",
        "head", "hand", "foot", "eye", "face", "arm", "leg", "body",
        "life", "death", "war", "peace",
        "water", "fire", "earth", "air",
        "kind", "kinds", "type", "types", "sort", "sorts",
        "case", "cases", "fact", "facts", "story",
        "money", "food", "work", "job", "company", "business",
        "fine", "okay", "true", "false", "real", "fake",
        "alone", "together", "ourselves", "yourselves", "themselves", "myself", "yourself", "himself", "herself", "itself",
        // Common nouns / generic NPC titles that exist as single-word FULL strings in FNV
        // and would otherwise drive false-positive case-fixes on every dialogue use.
        "guard", "guards", "soldier", "soldiers", "trooper", "troopers", "ranger", "rangers",
        "citizen", "citizens", "settler", "settlers", "doctor", "doctors", "nurse", "nurses",
        "captain", "lieutenant", "colonel", "major", "sergeant", "general", "chief",
        "boss", "bosses", "chairman", "chairmen", "president", "secretary",
        "slave", "slaves", "stranger", "strangers", "traveler", "travelers",
        "junkie", "junkies", "drunk", "drunks", "escort", "escorts",
        "raider", "raiders", "robot", "robots", "ant", "ants", "rat", "rats",
        "dog", "dogs", "cat", "cats", "fiend", "fiends", "mutant", "mutants",
        "ghoul", "ghouls", "deathclaw", "deathclaws",
        "vault", "vaults", "lucky", "fantastic", "meat", "sweetie", "baby", "babies",
        "trash", "loyal", "gift", "gifts", "members", "member", "friend", "friends",
        "family", "families", "mountain", "mountains", "area", "supply", "security",
        "medical", "control", "hell", "power", "powder", "easy", "song", "songs",
        "mess", "miss", "missing", "service", "services", "news",
        "caravan", "caravans", "gang", "gangs", "pack", "packs", "base", "bases",
        "road", "roads", "river", "rivers", "valley", "valleys", "canyon", "canyons",
        "building", "buildings", "station", "stations", "motor", "society", "societies",
        "battle", "battles", "war", "wars", "fight", "fights", "crash", "crashed",
        "saint", "saints", "human", "humans", "creature", "creatures",
        "king", "queen", "lord", "lady", "sir", "madam",
        "north", "south", "east", "west", "central", "downtown", "uptown",
        "sun", "sunset", "sunrise", "moon", "star", "stars",
        "happy", "sad", "angry", "scared", "tired", "hungry", "thirsty",
        "blue", "red", "green", "yellow", "black", "white", "brown", "gray", "grey",
        "shop", "shopper", "shopping", "store", "stores", "market", "markets",
        // Generic occupational nouns that show up as both vocab proper-nouns and common dialogue terms.
        "local", "locals", "hunter", "hunters", "merchant", "merchants", "patient", "patients",
        "gambler", "gamblers", "prospector", "prospectors", "mercenary", "mercenaries",
        "roller", "rollers", "assistant", "assistants", "hooker", "hookers", "bartender", "bartenders",
        "thug", "thugs", "angel", "angels", "drill", "heavy", "field", "fields", "jackass",
        "bouncer", "bouncers", "dealer", "dealers", "patron", "patrons", "owner", "owners",
        "smith", "smiths", "cook", "cooks", "chef", "chefs", "engineer", "engineers",
        "scientist", "scientists", "scholar", "scholars", "teacher", "teachers",
        "leader", "leaders", "follower", "followers", "manager", "managers"
    };

    // Articles/conjunctions/prepositions that should never seed vocab tokens, even when
    // they appear capitalized in a multi-word FULL string ("The Strip", "Of The Patriots").
    private static readonly HashSet<string> StructuralWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "nor", "yet", "so", "if",
        "of", "to", "in", "on", "at", "by", "for", "with", "from", "into", "onto",
        "as", "than", "is", "are", "was", "were", "be"
    };

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
        var rows = CsvIo.Parse(rawText);

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
            sb.Append(CsvIo.SerializeRow(rows[i]));
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
                if (EnglishStopWords.Contains(token))
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
                    if (StructuralWords.Contains(token))
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
        if (EnglishStopWords.Contains(token))
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
        if (EnglishStopWords.Contains(token))
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
            CollectEditDistance1(token, sameFirst, minEditLen, bestMatches);
        }
        // Also consider vocab words with different first char (covers "Kris" → "Chris")
        // but only if we haven't already found matches in the same-first-letter bucket.
        if (bestMatches.Count == 0)
        {
            foreach (var (_, list) in vocab.ByFirstChar)
            {
                CollectEditDistance1(token, list, minEditLen, bestMatches);
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

    private static void CollectEditDistance1(
        string token, List<string> candidates, int minVocabLen, List<string> matches)
    {
        var tokenLen = token.Length;
        foreach (var c in candidates)
        {
            if (c.Length < minVocabLen)
            {
                continue;
            }
            var diff = c.Length - tokenLen;
            if (diff < -1 || diff > 1)
            {
                continue;
            }
            if (EditDistanceAtMost1(token, c))
            {
                matches.Add(c);
            }
        }
    }

    // Returns true iff Levenshtein distance between a and b is 0 or 1 (case-insensitive).
    private static bool EditDistanceAtMost1(string a, string b)
    {
        if (a.Length > b.Length)
        {
            (a, b) = (b, a);
        }
        var diff = b.Length - a.Length;
        if (diff > 1)
        {
            return false;
        }

        if (diff == 0)
        {
            // Substitution or zero differences.
            var mismatches = 0;
            for (var i = 0; i < a.Length; i++)
            {
                if (char.ToLowerInvariant(a[i]) != char.ToLowerInvariant(b[i]))
                {
                    mismatches++;
                    if (mismatches > 1)
                    {
                        return false;
                    }
                }
            }
            return mismatches <= 1;
        }

        // diff == 1: a is one char shorter than b. Allow exactly one insertion.
        int i2 = 0, j = 0;
        var inserted = false;
        while (i2 < a.Length && j < b.Length)
        {
            if (char.ToLowerInvariant(a[i2]) == char.ToLowerInvariant(b[j]))
            {
                i2++;
                j++;
            }
            else
            {
                if (inserted)
                {
                    return false;
                }
                inserted = true;
                j++;
            }
        }
        return true;
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

    // ──────────────────────────────────────────────────────────────────────────
    //  Minimal CSV reader/writer (handles quoted fields, embedded quotes/commas/newlines)
    // ──────────────────────────────────────────────────────────────────────────

    private static class CsvIo
    {
        public static List<string[]> Parse(string text)
        {
            var rows = new List<string[]>();
            var row = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = true;
                    continue;
                }
                if (c == ',')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    continue;
                }
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row.ToArray());
                    row.Clear();
                    continue;
                }
                if (c == '\n')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row.ToArray());
                    row.Clear();
                    continue;
                }

                field.Append(c);
            }

            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row.ToArray());
            }

            return rows;
        }

        public static string SerializeRow(string[] fields)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(Escape(fields[i]));
            }
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            if (value == null)
            {
                return "";
            }
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            {
                return value;
            }
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
