using EsmSchemaGen;
using Xunit;

namespace EsmSchemaGen.Tests;

/// <summary>
///     End-to-end vertical slice: build the TES3 schema from the real <c>wbDefinitionsTES3.pas</c>, then
///     decode real records out of a retail <c>Morrowind.esm</c> with the schema interpreter. Skipped when
///     the game file is absent (set <c>MORROWIND_ESM</c> or have it at the default Steam path), so CI
///     without the asset still passes.
/// </summary>
public class Tes3RealFileTests
{
    [Fact]
    public void Decodes_Real_Morrowind_Esm_With_Generated_Schema()
    {
        var defs = TestPaths.FindRepoFile("Sample/Reference_Code/TES5Edit/Core/wbDefinitionsTES3.pas");
        var esm = TestPaths.FindMorrowindEsm();
        Assert.SkipUnless(defs is not null, "wbDefinitionsTES3.pas not found under the repo.");
        Assert.SkipUnless(esm is not null, "Morrowind.esm not found (set MORROWIND_ESM or install at the default path).");

        // Build the TES3 record schema straight from the Pascal definitions.
        var parser = new DefinitionsFileParser();
        parser.ParseFile(File.ReadAllText(defs!));
        var schemas = parser.Records.ToDictionary(r => r.Signature, r => r, StringComparer.Ordinal);
        Assert.True(schemas.Count >= 40, $"expected ~44 TES3 record schemas, got {schemas.Count}");

        var records = Tes3FileReader.Read(esm!).Take(5000).ToList();
        Assert.NotEmpty(records);

        // 1) The first record is the TES3 file header; HEDR.Version decodes to a sane master version.
        Assert.Equal("TES3", records[0].Type);
        var header = SchemaInterpreter.Decode(schemas.GetValueOrDefault("TES3"), records[0]);
        Assert.True(header.HasSchema);
        var version = header.Fields.FirstOrDefault(f => f.Path.EndsWith("Version", StringComparison.Ordinal))?.Value;
        Assert.InRange(Assert.IsType<float>(version), 0.5f, 2.0f);

        // 2) Schema coverage: nearly every record type in a real master is modeled.
        var withSchema = records.Count(r => schemas.ContainsKey(r.Type));
        Assert.True(withSchema >= records.Count * 0.95,
            $"only {withSchema}/{records.Count} records matched a schema");

        // 3) Editor IDs (the TES3 NAME subrecord, a string) decode to real printable text.
        var editorIds = records
            .Select(r => SchemaInterpreter.Decode(schemas.GetValueOrDefault(r.Type), r))
            .SelectMany(d => d.Fields)
            .Where(f => f.Path == "NAME" && f.Value is string { Length: > 0 })
            .Select(f => (string)f.Value!)
            .Take(100)
            .ToList();
        Assert.NotEmpty(editorIds);
        Assert.All(editorIds, id => Assert.All(id, ch => Assert.InRange(ch, ' ', '~')));
    }
}

internal static class TestPaths
{
    /// <summary>Walk up from the test assembly to find a repo-relative file; null if not found.</summary>
    public static string? FindRepoFile(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, normalized);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>Locate Morrowind.esm via the MORROWIND_ESM env var or the default Steam path; null if absent.</summary>
    public static string? FindMorrowindEsm()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MORROWIND_ESM");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        const string defaultPath = @"E:\SteamLibrary\SteamApps\common\Morrowind\Data Files\Morrowind.esm";
        return File.Exists(defaultPath) ? defaultPath : null;
    }
}
