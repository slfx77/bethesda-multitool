using EsmSchemaGen;
using EsmSchemaGen.Emit;
using EsmSchemaGen.Ir;

// Bootstrap entrypoint. The full System.CommandLine surface (--xedit-root / --out / --game) and the
// C# schema emitters arrive in a later stage. For now this points the DefinitionsFileParser at a single
// wbDefinitions*.pas file and reports how completely it lowers into IR (records, resolved symbols, and
// the unmapped-builder coverage tally that drives the next increment).
if (args.Length == 0)
{
    Console.WriteLine("EsmSchemaGen (bootstrap)");
    Console.WriteLine("  usage: EsmSchemaGen <wbDefinitions*.pas>                  # parse + coverage report");
    Console.WriteLine("         EsmSchemaGen read <wbDefinitionsTES3.pas> <esm>    # decode a real TES3 file");
    return 0;
}

if (args is ["read", var pasPath, var esmPath, ..])
{
    return RunRead(pasPath, esmPath);
}

if (args is ["emit", var emitPas, var emitOut, ..])
{
    return RunEmit(emitPas, emitOut, args.Length > 3 ? args[3] : null);
}

if (args is ["emit-all", var coreDir, var outDir, ..])
{
    return RunEmitAll(coreDir, outDir);
}

var path = args[0];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"File not found: {path}");
    return 1;
}

var parser = DefinitionsFileParser.LoadWithCommon(path);

Console.WriteLine(
    $"{Path.GetFileName(path)}: parsed {parser.Records.Count} record(s), {parser.ParseFailures} failure(s), " +
    $"{parser.Builder.Symbols.Count} symbol(s), {parser.Builder.EnumSymbols.Count} enum(s), " +
    $"{parser.Builder.FlagsSymbols.Count} flags(s).");

var totalMembers = parser.Records.Sum(r => r.Members.Count);
var unknownMembers = parser.Records.Sum(r => r.Members.Count(m => m is UnknownMemberDef));
Console.WriteLine($"  top-level members: {totalMembers}, unknown: {unknownMembers}");

if (parser.Builder.UnknownCalls.Count > 0)
{
    Console.WriteLine("  top unmapped builders:");
    foreach (var (name, count) in parser.Builder.UnknownCalls.OrderByDescending(kv => kv.Value).Take(20))
    {
        Console.WriteLine($"    {count,6}  {name}");
    }
}

return 0;

// Decode a real TES3 file with the schema built from its Pascal definitions — the end-to-end proof.
static int RunRead(string pasPath, string esmPath)
{
    if (!File.Exists(pasPath))
    {
        Console.Error.WriteLine($"File not found: {pasPath}");
        return 1;
    }

    if (!File.Exists(esmPath))
    {
        Console.Error.WriteLine($"File not found: {esmPath}");
        return 1;
    }

    var parser = DefinitionsFileParser.LoadWithCommon(pasPath);
    var schemas = parser.Records.ToDictionary(r => r.Signature, r => r, StringComparer.Ordinal);

    var total = 0;
    var withSchema = 0;
    var byType = new Dictionary<string, int>(StringComparer.Ordinal);
    Tes3RawRecord? first = null;

    foreach (var rec in Tes3FileReader.Read(esmPath))
    {
        first ??= rec;
        total++;
        if (schemas.ContainsKey(rec.Type))
        {
            withSchema++;
        }

        byType[rec.Type] = byType.GetValueOrDefault(rec.Type) + 1;
    }

    var pct = total == 0 ? 0 : 100.0 * withSchema / total;
    Console.WriteLine($"{Path.GetFileName(esmPath)}: {total:N0} records, {withSchema:N0} with schema " +
                      $"({pct:F1}%), {byType.Count} record types, {schemas.Count} schemas.");

    if (first is not null)
    {
        Console.WriteLine();
        Console.WriteLine($"First record [{first.Type}] decoded fields:");
        foreach (var f in SchemaInterpreter.Decode(schemas.GetValueOrDefault(first.Type), first).Fields.Take(12))
        {
            var rendered = f.Value switch { null => "(null)", string s => $"\"{s}\"", _ => f.Value.ToString() ?? "" };
            Console.WriteLine($"    {f.Path,-28} = {rendered}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Sample editor IDs (NAME):");
    var shown = 0;
    foreach (var rec in Tes3FileReader.Read(esmPath))
    {
        if (shown >= 10)
        {
            break;
        }

        var name = SchemaInterpreter.Decode(schemas.GetValueOrDefault(rec.Type), rec)
            .Fields.FirstOrDefault(f => f.Path == "NAME")?.Value as string;
        if (!string.IsNullOrEmpty(name))
        {
            Console.WriteLine($"    [{rec.Type,-4}] {name}");
            shown++;
        }
    }

    return 0;
}

// Emit one game's schema as a C# file for the main library's RecordModel/Generated folder.
static int RunEmit(string pasPath, string outPath, string? className)
{
    if (!File.Exists(pasPath))
    {
        Console.Error.WriteLine($"File not found: {pasPath}");
        return 1;
    }

    var parser = DefinitionsFileParser.LoadWithCommon(pasPath);
    var name = className ?? ClassNameFor(pasPath);
    var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
    if (dir is not null)
    {
        Directory.CreateDirectory(dir);
    }

    File.WriteAllText(outPath, SchemaEmitter.EmitGame(name, parser.Records));
    Console.WriteLine($"Emitted {parser.Records.Count} record(s) as {name} -> {outPath}");
    return 0;
}

// Emit every game's schema into an output directory (drives the committed RecordModel/Generated set).
static int RunEmitAll(string coreDir, string outDir)
{
    Directory.CreateDirectory(outDir);
    (string Suffix, string Class)[] games =
    [
        ("TES3", "Tes3Schema"), ("TES4", "OblivionSchema"), ("FO3", "Fallout3Schema"),
        ("FNV", "FalloutNvSchema"), ("TES5", "SkyrimSchema"), ("FO4", "Fallout4Schema"),
        ("FO76", "Fallout76Schema")
    ];

    foreach (var (suffix, cls) in games)
    {
        var pas = Path.Combine(coreDir, $"wbDefinitions{suffix}.pas");
        if (!File.Exists(pas))
        {
            Console.WriteLine($"  skip {suffix} (not found)");
            continue;
        }

        var parser = DefinitionsFileParser.LoadWithCommon(pas);
        File.WriteAllText(Path.Combine(outDir, $"{cls}.g.cs"), SchemaEmitter.EmitGame(cls, parser.Records));
        Console.WriteLine($"  {suffix}: {parser.Records.Count} records -> {cls}.g.cs");
    }

    return 0;
}

static string ClassNameFor(string pasPath)
{
    var suffix = Path.GetFileNameWithoutExtension(pasPath)
        .Replace("wbDefinitions", "", StringComparison.OrdinalIgnoreCase);
    return suffix switch
    {
        "TES3" => "Tes3Schema",
        "TES4" => "OblivionSchema",
        "FO3" => "Fallout3Schema",
        "FNV" => "FalloutNvSchema",
        "TES5" => "SkyrimSchema",
        "FO4" => "Fallout4Schema",
        "FO76" => "Fallout76Schema",
        _ => suffix + "Schema"
    };
}
