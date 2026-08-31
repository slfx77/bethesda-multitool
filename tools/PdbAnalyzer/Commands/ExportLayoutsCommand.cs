using static PdbAnalyzer.PdbAnalyzerHelpers;

namespace PdbAnalyzer.Commands;

/// <summary>
///     Export flattened struct layouts for all FormType classes as JSON.
///     Recursively resolves base class fields to produce complete field maps.
///     <para>
///         Alongside the FormType-keyed <c>types</c> map, emits an <c>auxStructs</c> map holding the
///         flattened layout of every <b>non-record</b> struct those types reference. Without it a
///         consumer can see that <c>TESObjectSTAT.TextureSwapList</c> is a
///         <c>BSSimpleList&lt;TEX_SWAP *&gt;</c> but has no way to know what a <c>TEX_SWAP</c> looks
///         like, so the whole class of nested-struct payloads — MODS alternate textures, MODT
///         texture hashes, LSCR location entries, DEST destruction headers — stays unreadable no
///         matter what the reader does.
///     </para>
/// </summary>
internal static class ExportLayoutsCommand
{
    /// <summary>
    ///     Backstop on the reference walk. The walk terminates on its own — a struct is flattened
    ///     at most once, so the transitive closure is finite and cycles cannot recur — and this
    ///     exists only so a malformed dump cannot spin. It is deliberately far above the real
    ///     closure depth; if a run ever reports reaching it, the input is wrong, not the limit.
    /// </summary>
    private const int AuxStructDepthBackstop = 64;

    internal static async Task<int> ExecuteAsync(string cvdumpPath, string outputPath)
    {
        if (!File.Exists(cvdumpPath))
        {
            Console.WriteLine($"File not found: {cvdumpPath}");
            return 1;
        }

        Console.WriteLine($"Parsing {cvdumpPath}...");
        var parser = new CvdumpParser();
        await parser.ParseAsync(cvdumpPath);

        // Find ENUM_FORM_ID
        if (!parser.Enums.TryGetValue("ENUM_FORM_ID", out var formIdEnum))
        {
            Console.WriteLine("ERROR: ENUM_FORM_ID not found in PDB type dump.");
            return 1;
        }

        // Build struct lookup by name
        var structsByName = parser.Structures
            .Where(s => s.Size > 0)
            .GroupBy(s => s.Name)
            .ToDictionary(g => g.Key, g => g.First());

        Console.WriteLine();
        Console.WriteLine("=== Flattening struct layouts ===");
        Console.WriteLine();

        var types = new Dictionary<string, object>();
        var recordClassNames = new HashSet<string>(StringComparer.Ordinal);
        var referencedTypeNames = new HashSet<string>(StringComparer.Ordinal);
        var matched = 0;
        var totalFields = 0;

        foreach (var member in formIdEnum.Members.OrderBy(m => m.Value))
        {
            if (member.Name == "FORM_ID_COUNT")
                continue;

            var recordCode = member.Name.EndsWith("_ID")
                ? member.Name[..^3]
                : member.Name;

            var className = GetClassNameForRecord(recordCode);
            if (className == null || !structsByName.TryGetValue(className, out var structInfo))
                continue;

            var flatFields = parser.FlattenFields(structInfo);
            matched++;
            totalFields += flatFields.Count;
            recordClassNames.Add(className);
            CollectStructReferences(flatFields, referencedTypeNames);

            var key = $"0x{member.Value:X2}";
            types[key] = new
            {
                formType = member.Value,
                recordCode,
                className,
                structSize = structInfo.Size,
                fields = flatFields.Select(f => new
                {
                    name = f.Name,
                    offset = f.Offset,
                    size = f.Size,
                    kind = f.Kind,
                    owner = f.OwnerClass,
                    typeDetail = f.TypeDetail
                }).ToArray()
            };

            var fieldKinds = flatFields.GroupBy(f => f.Kind).OrderByDescending(g => g.Count());
            var kindSummary = string.Join(", ", fieldKinds.Select(g => $"{g.Count()} {g.Key}"));
            Console.WriteLine(
                $"  0x{member.Value:X2} {recordCode,-6} {className,-30} {structInfo.Size,5}B  {flatFields.Count,3} fields ({kindSummary})");
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {matched} types, {totalFields} fields");

        Console.WriteLine();
        Console.WriteLine("=== Flattening auxiliary (non-record) structs ===");
        Console.WriteLine();

        var auxStructs = BuildAuxiliaryStructs(
            parser, structsByName, referencedTypeNames, recordClassNames);

        Console.WriteLine();
        Console.WriteLine($"Auxiliary: {auxStructs.Count} structs");

        // Write JSON. `source` records the file this export was actually produced from — the
        // previous hard-coded "Fallout_Release_MemDebug.pdb" is ambiguous between the Proto and
        // Aug-22 dumps, whose layouts differ (WEAP is 920 bytes in one and 924 in the other), and
        // regenerating from the wrong one silently moves offsets across all 116 types.
        var json = new
        {
            source = Path.GetFileName(cvdumpPath),
            sourcePath = cvdumpPath.Replace('\\', '/'),
            tesFormSize = 40,
            generatedAt = DateTime.UtcNow.ToString("O"),
            types,
            auxStructs
        };

        var options = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var jsonText = System.Text.Json.JsonSerializer.Serialize(json, options);
        await File.WriteAllTextAsync(outputPath, jsonText);

        Console.WriteLine($"Written to {outputPath} ({jsonText.Length:N0} bytes)");
        return 0;
    }

    /// <summary>
    ///     Walk out from the record types' fields, flattening every non-record struct they name,
    ///     then the structs <i>those</i> name, to the full transitive closure.
    ///     <para>
    ///         Termination comes from the <c>visited</c> set, not from a depth limit: each struct is
    ///         considered exactly once, so a cycle (a type that reaches itself through a pointer —
    ///         <c>ExtraDataList</c>, the Ni* scene graph and the actor-process types are all
    ///         cyclic) is entered once and never re-entered. The frontier therefore shrinks to
    ///         empty and the closure is finite. <see cref="AuxStructDepthBackstop" /> guards only
    ///         against a malformed input, never against a legitimate deep graph.
    ///     </para>
    ///     <para>
    ///         Record classes are skipped — they are already in <c>types</c>, and a pointer to one
    ///         resolves to a FormID rather than needing a member layout.
    ///     </para>
    /// </summary>
    private static Dictionary<string, object> BuildAuxiliaryStructs(
        CvdumpParser parser,
        IReadOnlyDictionary<string, StructureInfo> structsByName,
        IReadOnlySet<string> seedNames,
        IReadOnlySet<string> recordClassNames)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new HashSet<string>(seedNames, StringComparer.Ordinal);

        var depth = 0;
        while (frontier.Count > 0 && depth < AuxStructDepthBackstop)
        {
            depth++;
            var next = new HashSet<string>(StringComparer.Ordinal);
            var addedThisDepth = 0;

            foreach (var name in frontier.OrderBy(n => n, StringComparer.Ordinal))
            {
                // The cycle guard. A name reached again from anywhere is dropped here, which is
                // what makes an unbounded walk terminate on a graph full of back-references.
                if (!visited.Add(name) || recordClassNames.Contains(name))
                {
                    continue;
                }

                if (!structsByName.TryGetValue(name, out var structInfo))
                {
                    continue; // A primitive, an enum, or a type cvdump never defined a body for.
                }

                var flat = parser.FlattenFields(structInfo);
                if (flat.Count == 0)
                {
                    continue; // Nothing to say about it; do not add an empty entry.
                }

                result[name] = new
                {
                    className = name,
                    structSize = structInfo.Size,
                    fields = flat.Select(f => new
                    {
                        name = f.Name,
                        offset = f.Offset,
                        size = f.Size,
                        kind = f.Kind,
                        owner = f.OwnerClass,
                        typeDetail = f.TypeDetail
                    }).ToArray()
                };
                addedThisDepth++;
                CollectStructReferences(flat, next);
            }

            // Names already visited are pruned here rather than at dequeue time so the printed
            // frontier size reflects real remaining work.
            next.ExceptWith(visited);
            Console.WriteLine(
                $"  depth {depth}: +{addedThisDepth} structs ({result.Count} total, {next.Count} queued)");
            frontier = next;
        }

        if (depth >= AuxStructDepthBackstop)
        {
            Console.WriteLine(
                $"  WARNING: stopped at the depth backstop ({AuxStructDepthBackstop}). " +
                "The closure should terminate well before this — check the input dump.");
        }

        return result;
    }

    private static void CollectStructReferences(IEnumerable<FlatField> fields, ISet<string> into)
    {
        foreach (var field in fields)
        {
            if (ExtractStructTypeName(field.TypeDetail) is { } name)
            {
                into.Add(name);
            }
        }
    }

    /// <summary>
    ///     Reduce a <c>typeDetail</c> string to the bare class name it ultimately refers to, or null
    ///     when it does not name one. Handles the three decorations the exporter writes:
    ///     <c>TESForm *[]</c> (array of pointers), <c>BSSimpleList&lt;TEX_SWAP *&gt;</c> (single
    ///     generic argument) and a plain <c>Foo *</c> pointer target.
    ///     <para>
    ///         Anything still carrying an unbalanced <c>&lt;</c> or a comma is rejected rather than
    ///         guessed at: cvdump's <c>class name = …</c> field stops at the first comma, so a
    ///         multi-argument template such as <c>BSSimpleArray&lt;Actor *,BSTArrayHeapAllocator&gt;</c>
    ///         arrives already truncated and cannot be resolved.
    ///     </para>
    /// </summary>
    internal static string? ExtractStructTypeName(string? typeDetail)
    {
        if (string.IsNullOrWhiteSpace(typeDetail))
        {
            return null;
        }

        var name = typeDetail.Trim();
        if (name.EndsWith("[]", StringComparison.Ordinal))
        {
            name = name[..^2].TrimEnd();
        }

        var open = name.IndexOf('<', StringComparison.Ordinal);
        var close = name.LastIndexOf('>');
        if (open >= 0 && close > open)
        {
            name = name[(open + 1)..close].Trim();
        }

        name = name.TrimEnd('*').TrimEnd();

        if (name.Length == 0 ||
            name.Contains('<', StringComparison.Ordinal) ||
            name.Contains(',', StringComparison.Ordinal) ||
            name.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }

        return name;
    }
}
