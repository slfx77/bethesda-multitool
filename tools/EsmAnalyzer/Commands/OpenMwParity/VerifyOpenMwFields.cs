using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text.RegularExpressions;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Tes3;
using Spectre.Console;

namespace EsmAnalyzer.Commands.OpenMwParity;

/// <summary>
///     Tier 2 of the OpenMW verification harness: a per-FIELD black-box diff of our TES3 parse against
///     OpenMW's <c>esmtool</c> (run as an external oracle; we diff its dump TEXT and never read its GPLv3
///     source). Covers the record types whose fields esmtool prints cleanly — GMST values, the LTEX
///     land-texture palette (index → texture, which VTEX grids resolve through), CELL coords/region/flags,
///     NPC_ core stats, and REGN weather/map-color/sleep. esmtool can't expose the LAND VTEX grid or
///     heightmap, so those stay on unit-test + visual coverage.
///     <para>
///         Only fields WE decode are compared: where our parser doesn't surface a value (e.g. an
///         auto-calc NPC has no explicit attributes, or NPC skills we intentionally skip), the comparison
///         is omitted rather than counted as a mismatch. So a non-zero result is always a real parse
///         discrepancy in something we claim to read.
///     </para>
/// </summary>
internal static class VerifyOpenMwFields
{
    private const int MaxRowsPerType = 30;

    private static readonly Regex RecordHeaderRx =
        new(@"^Record:\s+([A-Z][A-Z0-9_]{3})\s*(.*)$", RegexOptions.Compiled);

    private static readonly Regex HexInParensRx =
        new(@"\(0x([0-9A-Fa-f]+)\)", RegexOptions.Compiled);

    private static readonly Regex CoordRx =
        new(@"\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)", RegexOptions.Compiled);

    public static int Run(string file, string esmtool)
    {
        var records = LoadRecords(file);
        if (records is null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Our parser produced no records for {0}.", Markup.Escape(file));
            return 1;
        }

        AnsiConsole.MarkupLine(
            "[grey]Tier-2 field diff vs esmtool (black box). Only fields we decode are compared; " +
            "esmtool-only fields (e.g. NPC skills) are not counted.[/]");

        var total = 0;
        total += DiffGmst(records, file, esmtool);
        total += DiffLtex(records, file, esmtool);
        total += DiffCell(records, file, esmtool);
        total += DiffNpc(records, file, esmtool);
        total += DiffRegn(records, file, esmtool);

        AnsiConsole.MarkupLine(total == 0
            ? "[green]All compared fields match.[/]"
            : $"[yellow]{total} field mismatch(es) total.[/] Each is a parse discrepancy to investigate.");
        return 0;
    }

    // ---- our side: load the typed TES3 RecordCollection (same path the map/diag commands use) --------

    private static RecordCollection? LoadRecords(string file)
    {
        var result = EsmFileAnalyzer.AnalyzeAsync(file).GetAwaiter().GetResult();
        if (result.EsmRecords is null)
        {
            return null;
        }

        using var mmf = MemoryMappedFile.CreateFromFile(file, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        var parser = new RecordParser(result.EsmRecords, result.FormIdMap, accessor, result.FileSize);
        return parser.ParseAll();
    }

    // ---- esmtool side: run one type-filtered dump and parse its record blocks ------------------------

    private static List<EsmtoolRecord> FetchEsmtool(string esmtool, string file, string type)
    {
        var (dump, ok) = VerifyOpenMwCommand.RunEsmtoolDump(esmtool, file, type);
        return ok ? ParseRecords(dump) : [];
    }

    /// <summary>
    ///     Parses esmtool's dump into per-record field bags. Each record is a <c>Record: TYPE "name"</c>
    ///     header followed by 2-space-indented <c>Key: value</c> lines; 4-space-indented lines under a
    ///     value-less <c>Group:</c> header (Attributes/Skills/Weather) become <c>Group.Key</c>. The leading
    ///     banner and the non-indented <c>Record flags:</c> line are ignored.
    /// </summary>
    internal static List<EsmtoolRecord> ParseRecords(string dump)
    {
        var records = new List<EsmtoolRecord>();
        var lines = dump.Split('\n');
        EsmtoolRecord? current = null;
        string? group = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            var header = RecordHeaderRx.Match(line);
            if (header.Success)
            {
                current = new EsmtoolRecord(header.Groups[1].Value, Unquote(header.Groups[2].Value.Trim()));
                records.Add(current);
                group = null;
                continue;
            }

            if (current is null || line.Length == 0 || line[0] != ' ')
            {
                group = null; // a non-indented line (e.g. "Record flags:") ends any open group
                continue;
            }

            var indent = line.Length - line.TrimStart(' ').Length;
            var trimmed = line.Trim();
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();

            if (value.Length == 0 && indent <= 2)
            {
                group = key; // group header (Attributes:/Skills:/Weather:)
                continue;
            }

            // A quoted value that opens a quote but doesn't close it spans multiple physical lines
            // (esmtool prints multi-line GMST strings verbatim). Append the continuation lines so the
            // value isn't silently truncated to its first line (a false mismatch).
            if (value.Length >= 1 && value[0] == '"' && !ClosesQuote(value))
            {
                while (i + 1 < lines.Length)
                {
                    i++;
                    var cont = lines[i].TrimEnd('\r');
                    value += "\n" + cont;
                    if (ClosesQuote(cont))
                    {
                        break;
                    }
                }
            }

            var fieldKey = group != null && indent >= 4 ? $"{group}.{key}" : key;
            if (indent <= 2)
            {
                group = null;
            }

            current.Fields[fieldKey] = value; // last value wins (repeated keys like Sound: aren't diffed)
        }

        return records;
    }

    private static bool ClosesQuote(string s)
    {
        var t = s.TrimEnd();
        return t.Length >= 1 && t[^1] == '"';
    }

    // ---- per-type diffs -----------------------------------------------------------------------------

    private static int DiffGmst(RecordCollection records, string file, string esmtool)
    {
        var ours = OurByEditorId(records, "GMST");
        var rows = new List<Mismatch>();
        var compared = 0;

        foreach (var rec in FetchEsmtool(esmtool, file, "GMST"))
        {
            if (!ours.TryGetValue(rec.Name, out var our))
            {
                continue;
            }

            var variant = rec.Fields.FirstOrDefault(kv => kv.Key.StartsWith("variant", StringComparison.OrdinalIgnoreCase));
            if (variant.Key is null)
            {
                continue;
            }

            var kind = variant.Key.Split(' ')[^1].ToLowerInvariant();
            switch (kind)
            {
                case "string":
                {
                    var oursStr = FieldStr(our, "STRV.Value");
                    var theirs = Unquote(variant.Value);
                    if (oursStr is null)
                    {
                        continue;
                    }

                    compared++;
                    // Collapse whitespace: esmtool reproduces multi-line GMST strings with the original
                    // newlines/trailing spaces, so compare on content, not exact whitespace.
                    if (!TextEq(oursStr, theirs))
                    {
                        rows.Add(new Mismatch(rec.Name, "value(str)", oursStr, theirs));
                    }

                    break;
                }
                case "int":
                {
                    var oursStr = FieldStr(our, "INTV.Value");
                    if (oursStr is null)
                    {
                        continue;
                    }

                    compared++;
                    if (!IntEq(oursStr, variant.Value))
                    {
                        rows.Add(new Mismatch(rec.Name, "value(int)", oursStr, variant.Value));
                    }

                    break;
                }
                case "float":
                {
                    var oursStr = FieldStr(our, "FLTV.Value");
                    if (oursStr is null)
                    {
                        continue;
                    }

                    compared++;
                    if (!FloatEq(oursStr, variant.Value))
                    {
                        rows.Add(new Mismatch(rec.Name, "value(float)", oursStr, variant.Value));
                    }

                    break;
                }
            }
        }

        return Report("GMST", rows, compared);
    }

    private static int DiffLtex(RecordCollection records, string file, string esmtool)
    {
        var texById = records.TextureSets.ToDictionary(t => t.FormId);
        var ours = new Dictionary<string, (int Index, string? Texture)>(StringComparer.OrdinalIgnoreCase);
        foreach (var lt in records.LandTextures)
        {
            if (string.IsNullOrEmpty(lt.EditorId))
            {
                continue;
            }

            var index = (int)(lt.FormId - Tes3FormIdScheme.LtexFormIdBase);
            var ts = lt.TextureSetFormId is { } tsId && texById.TryGetValue(tsId, out var found) ? found : null;
            ours[lt.EditorId!] = (index, ts?.DiffuseTexture);
        }

        var rows = new List<Mismatch>();
        var compared = 0;

        foreach (var rec in FetchEsmtool(esmtool, file, "LTEX"))
        {
            if (!ours.TryGetValue(rec.Name, out var our))
            {
                continue;
            }

            if (rec.Fields.TryGetValue("Index", out var theirIdx))
            {
                compared++;
                var oursIdx = our.Index.ToString(CultureInfo.InvariantCulture);
                if (!IntEq(oursIdx, theirIdx))
                {
                    rows.Add(new Mismatch(rec.Name, "Index", oursIdx, theirIdx));
                }
            }

            if (rec.Fields.TryGetValue("Texture", out var theirTex) && our.Texture != null)
            {
                compared++;
                if (!PathEq(our.Texture, theirTex))
                {
                    rows.Add(new Mismatch(rec.Name, "Texture", our.Texture, theirTex));
                }
            }
        }

        return Report("LTEX", rows, compared);
    }

    private static int DiffCell(RecordCollection records, string file, string esmtool)
    {
        var exterior = new Dictionary<(int, int), CellRecord>();
        var interior = new Dictionary<string, CellRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in records.Cells)
        {
            if (!c.IsInterior && c.GridX is { } gx && c.GridY is { } gy)
            {
                exterior[(gx, gy)] = c;
            }
            else if (c.IsInterior && !string.IsNullOrEmpty(c.EditorId))
            {
                interior[c.EditorId!] = c;
            }
        }

        var rows = new List<Mismatch>();
        var compared = 0;

        foreach (var rec in FetchEsmtool(esmtool, file, "CELL"))
        {
            if (rec.Name.Length == 0)
            {
                // Exterior cell: esmtool prints no name; match by grid coordinates.
                if (!rec.Fields.TryGetValue("Coordinates", out var coordStr))
                {
                    continue;
                }

                var m = CoordRx.Match(coordStr);
                if (!m.Success)
                {
                    continue;
                }

                var key = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                if (!exterior.TryGetValue(key, out var our))
                {
                    continue;
                }

                var id = $"({key.Item1},{key.Item2})";
                if (rec.Fields.TryGetValue("Region", out var theirRegion) && !string.IsNullOrEmpty(our.FullName))
                {
                    compared++;
                    var theirs = Unquote(theirRegion);
                    if (!string.Equals(our.FullName, theirs, StringComparison.Ordinal))
                    {
                        rows.Add(new Mismatch(id, "Region", our.FullName!, theirs));
                    }
                }

                compared += CompareCellFlags(rec, our, id, rows);
            }
            else
            {
                // Interior cell: match by name.
                if (!interior.TryGetValue(rec.Name, out var our))
                {
                    continue;
                }

                compared += CompareCellFlags(rec, our, rec.Name, rows);
            }
        }

        return Report("CELL", rows, compared);
    }

    private static int CompareCellFlags(EsmtoolRecord rec, CellRecord our, string id, List<Mismatch> rows)
    {
        if (!rec.Fields.TryGetValue("Flags", out var flagStr))
        {
            return 0;
        }

        var m = HexInParensRx.Match(flagStr);
        if (!m.Success)
        {
            return 0;
        }

        var theirFlags = Convert.ToInt32(m.Groups[1].Value, 16) & 0xFF;
        if (theirFlags != our.Flags)
        {
            rows.Add(new Mismatch(id, "Flags", $"0x{our.Flags:X2}", $"0x{theirFlags:X2}"));
        }

        return 1;
    }

    private static int DiffNpc(RecordCollection records, string file, string esmtool)
    {
        var ours = OurByEditorId(records, "NPC_");
        var rows = new List<Mismatch>();
        var compared = 0;

        (string Esm, string Our)[] scalars =
        {
            ("Level", "NPDT.Level"), ("Reputation", "NPDT.Reputation"),
            ("Disposition", "NPDT.Disposition"), ("Rank", "NPDT.Rank"),
            ("Attributes.Strength", "NPDT.Strength"), ("Attributes.Intelligence", "NPDT.Intelligence"),
            ("Attributes.Willpower", "NPDT.Willpower"), ("Attributes.Agility", "NPDT.Agility"),
            ("Attributes.Speed", "NPDT.Speed"), ("Attributes.Endurance", "NPDT.Endurance"),
            ("Attributes.Personality", "NPDT.Personality"), ("Attributes.Luck", "NPDT.Luck")
        };
        (string Esm, string Our)[] strings = { ("Race", "RNAM.Race"), ("Class", "CNAM.Class") };

        foreach (var rec in FetchEsmtool(esmtool, file, "NPC_"))
        {
            if (!ours.TryGetValue(rec.Name, out var our))
            {
                continue;
            }

            foreach (var (esm, ourKey) in scalars)
            {
                if (!rec.Fields.TryGetValue(esm, out var their))
                {
                    continue;
                }

                var oursStr = FieldStr(our, ourKey);
                if (oursStr is null)
                {
                    continue; // not decoded our side (e.g. auto-calc NPC carries no explicit attributes)
                }

                compared++;
                if (!IntEq(oursStr, their))
                {
                    rows.Add(new Mismatch(rec.Name, esm, oursStr, their));
                }
            }

            foreach (var (esm, ourKey) in strings)
            {
                if (!rec.Fields.TryGetValue(esm, out var their))
                {
                    continue;
                }

                var oursStr = FieldStr(our, ourKey);
                if (oursStr is null)
                {
                    continue;
                }

                compared++;
                var theirs = Unquote(their);
                if (!string.Equals(oursStr, theirs, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(new Mismatch(rec.Name, esm, oursStr, theirs));
                }
            }
        }

        return Report("NPC_", rows, compared);
    }

    private static int DiffRegn(RecordCollection records, string file, string esmtool)
    {
        var ours = OurByEditorId(records, "REGN");
        var rows = new List<Mismatch>();
        var compared = 0;

        string[] weather = ["Clear", "Cloudy", "Fog", "Overcast", "Rain", "Thunder", "Ash", "Blight", "Snow", "Blizzard"];

        foreach (var rec in FetchEsmtool(esmtool, file, "REGN"))
        {
            if (!ours.TryGetValue(rec.Name, out var our))
            {
                continue;
            }

            foreach (var w in weather)
            {
                if (!rec.Fields.TryGetValue($"Weather.{w}", out var their))
                {
                    continue;
                }

                var oursStr = FieldStr(our, $"WEAT.{w}");
                if (oursStr is null)
                {
                    continue;
                }

                compared++;
                if (!IntEq(oursStr, their))
                {
                    rows.Add(new Mismatch(rec.Name, $"Weather.{w}", oursStr, their));
                }
            }

            if (rec.Fields.TryGetValue("Map Color", out var theirColor))
            {
                var oursStr = FieldStr(our, "CNAM.MapColor");
                if (oursStr != null)
                {
                    compared++;
                    if (!IntEq(oursStr, theirColor))
                    {
                        rows.Add(new Mismatch(rec.Name, "Map Color", oursStr, theirColor));
                    }
                }
            }

            if (rec.Fields.TryGetValue("Sleep List", out var theirSleep))
            {
                var oursStr = FieldStr(our, "BNAM.SleepCreature");
                if (oursStr != null)
                {
                    compared++;
                    var theirs = Unquote(theirSleep);
                    if (!string.Equals(oursStr, theirs, StringComparison.OrdinalIgnoreCase))
                    {
                        rows.Add(new Mismatch(rec.Name, "Sleep List", oursStr, theirs));
                    }
                }
            }
        }

        return Report("REGN", rows, compared);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static Dictionary<string, GenericEsmRecord> OurByEditorId(RecordCollection records, string type) =>
        records.GenericRecords
            .Where(r => r.RecordType == type && !string.IsNullOrEmpty(r.EditorId))
            .GroupBy(r => r.EditorId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static string? FieldStr(GenericEsmRecord r, string key) =>
        r.Fields.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    // Whitespace-insensitive content equality (collapses runs of spaces/newlines to one space).
    private static bool TextEq(string a, string b) =>
        string.Equals(Collapse(a), Collapse(b), StringComparison.Ordinal);

    private static string Collapse(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IntEq(string a, string b) =>
        long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
        && long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            ? x == y
            : string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);

    private static bool FloatEq(string a, string b)
    {
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return Math.Abs(x - y) <= 1e-3 * Math.Max(1.0, Math.Abs(x));
        }

        return string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);
    }

    private static bool PathEq(string a, string b) =>
        string.Equals(a.Replace('/', '\\').Trim(), b.Replace('/', '\\').Trim(), StringComparison.OrdinalIgnoreCase);

    private static int Report(string type, List<Mismatch> rows, int compared)
    {
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine(compared == 0
                ? $"[grey]{type}: no comparable records/fields.[/]"
                : $"[green]{type}: {compared:N0} field-comparison(s) match.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded).Title($"[yellow]{type} mismatches[/]");
        table.AddColumn("Id");
        table.AddColumn("Field");
        table.AddColumn("Ours");
        table.AddColumn("esmtool");
        foreach (var r in rows.Take(MaxRowsPerType))
        {
            table.AddRow(Markup.Escape(r.Id), Markup.Escape(r.Field), Markup.Escape(r.Ours), Markup.Escape(r.Theirs));
        }

        AnsiConsole.Write(table);
        if (rows.Count > MaxRowsPerType)
        {
            AnsiConsole.MarkupLine("[grey]… +{0} more {1} mismatch(es) not shown (showing first {2}).[/]",
                rows.Count - MaxRowsPerType, type, MaxRowsPerType);
        }

        AnsiConsole.MarkupLine($"[yellow]{type}: {rows.Count} mismatch(es) / {compared:N0} comparison(s).[/]");
        return rows.Count;
    }

    /// <summary>One parsed esmtool record: its type, name (unquoted), and flattened field bag.</summary>
    internal sealed class EsmtoolRecord(string type, string name)
    {
        public string Type { get; } = type;
        public string Name { get; } = name;
        public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct Mismatch(string Id, string Field, string Ours, string Theirs);
}
