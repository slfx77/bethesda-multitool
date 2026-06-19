using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using BethesdaMultitool.Core.Utils;
using BethesdaMultitool.Core.Minidump;

var repoRoot = Directory.GetCurrentDirectory();
var dumpPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(repoRoot, "Sample", "MemoryDump", "Fallout_Release_MemDebug.xex.dmp");
var globalsPath = args.Length > 1
    ? Path.GetFullPath(args[1])
    : Path.Combine(repoRoot, "Sample", "PDB", "Proto", "Fallout_Release_MemDebug", "globals.txt");
var outputPath = args.Length > 2
    ? Path.GetFullPath(args[2])
    : Path.Combine(repoRoot, "tools", "GhidraProject", "shader_probe_report.txt");
var shaderPackagePath = args.Length > 3
    ? Path.GetFullPath(args[3])
    : Path.Combine(repoRoot, "Sample", "Full_Builds", "Fallout New Vegas (360 Final)", "Data", "Shaders", "shaderpackage.sdp");

if (!File.Exists(dumpPath))
{
    Console.Error.WriteLine($"Dump not found: {dumpPath}");
    return 1;
}

if (!File.Exists(globalsPath))
{
    Console.Error.WriteLine($"PDB globals file not found: {globalsPath}");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

var minidump = MinidumpParser.Parse(dumpPath);
if (!minidump.IsValid)
{
    Console.Error.WriteLine($"Invalid minidump: {dumpPath}");
    return 1;
}

var constants = PdbConstants.Load(globalsPath);
await using var dump = new FileStream(dumpPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);

await using var reportStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
await using var writer = new StreamWriter(reportStream, new UTF8Encoding(false));

await writer.WriteLineAsync("=== Xbox 360 Shader Probe ===");
await writer.WriteLineAsync($"Date: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
await writer.WriteLineAsync($"Dump: {dumpPath}");
await writer.WriteLineAsync($"PDB globals: {globalsPath}");
await writer.WriteLineAsync($"File size: 0x{new FileInfo(dumpPath).Length:X}");
await writer.WriteLineAsync($"Minidump streams: {minidump.NumberOfStreams}");
await writer.WriteLineAsync($"Processor architecture: 0x{minidump.ProcessorArchitecture:X4} ({(minidump.IsXbox360 ? "Xbox 360/PowerPC" : "other")})");
await writer.WriteLineAsync($"Memory regions: {minidump.MemoryRegions.Count}");
await writer.WriteLineAsync($"Modules: {minidump.Modules.Count}");
await writer.WriteLineAsync();

await WriteModules(writer, minidump);
await WriteKnownMemDebugGlobals(writer, dump, minidump, constants);
await WriteShaderStrings(writer, dump, minidump);
await WriteShaderPackage(writer, shaderPackagePath, constants, outputPath);

await writer.FlushAsync();
Console.WriteLine($"Report written: {outputPath}");
return 0;

static async Task WriteModules(StreamWriter writer, MinidumpInfo minidump)
{
    await writer.WriteLineAsync("=== Modules ===");
    foreach (var module in minidump.Modules.OrderBy(m => m.BaseAddress))
    {
        await writer.WriteLineAsync($"0x{module.BaseAddress:X8}-0x{module.BaseAddress + module.Size:X8} {module.Name}");
    }

    await writer.WriteLineAsync();
}

static async Task WriteKnownMemDebugGlobals(
    StreamWriter writer,
    FileStream dump,
    MinidumpInfo minidump,
    PdbConstants constants)
{
    await writer.WriteLineAsync("=== Known MemDebug Shader Globals ===");
    await writer.WriteLineAsync("These addresses come from Ghidra decompilation of Fallout_Release_MemDebug.xex.");
    await writer.WriteLineAsync("If a non-MemDebug dump uses a different data layout, broad string scanning below is still valid but these tables may not map.");
    await writer.WriteLineAsync();

    await writer.WriteLineAsync("Ghidra-resolved runtime table candidates:");
    await WritePointerTable(
        writer,
        dump,
        minidump,
        "ShadowLightShader::pPixelShader",
        0x832B7CE0,
        0xCA,
        i => constants.NamesFor(i, "SLS_PS"),
        ShouldReportShaderName);

    await WritePointerTable(
        writer,
        dump,
        minidump,
        "ShadowLightShader::pVertexShader",
        0x832B8008,
        0x9C,
        i => constants.NamesFor(i, "SLS_VS"),
        ShouldReportShaderName);

    await WritePointerTable(
        writer,
        dump,
        minidump,
        "ShadowLightShader::spPass",
        0x832B8278,
        0x7E * 6,
        i => constants.NamesFor(i, "BSSM_"),
        ShouldReportPassName);

    await writer.WriteLineAsync("PDB-direct table candidates from section-7 base mapping:");
    await WritePointerTable(
        writer,
        dump,
        minidump,
        "PDB direct ShadowLightShader::pPixelShader",
        0x832B72E0,
        0xCA,
        i => constants.NamesFor(i, "SLS_PS"),
        ShouldReportShaderName);

    await WritePointerTable(
        writer,
        dump,
        minidump,
        "PDB direct ShadowLightShader::pVertexShader",
        0x832B7608,
        0x9C,
        i => constants.NamesFor(i, "SLS_VS"),
        ShouldReportShaderName);

    await WritePointerTable(
        writer,
        dump,
        minidump,
        "PDB direct ShadowLightShader::spPass",
        0x832B7878,
        0x7E * 6,
        i => constants.NamesFor(i, "BSSM_"),
        ShouldReportPassName);

    await WriteRawDwords(
        writer,
        dump,
        minidump,
        "fixed landscape stage resources",
        0x832B3770,
        0x20);
}

static bool ShouldReportShaderName(string name)
{
    return ContainsAny(name, "LAND", "LIGHTING", "VC", "NORMAL", "BUMP", "SPECULAR", "DIFFUSE", "AMBIENT", "ENVMAP", "TEXEFFECT");
}

static bool ShouldReportPassName(string name)
{
    return ContainsAny(name, "LAND", "LIGHTING", "VC", "NORMAL", "SPECULAR", "ENVMAP", "WATER");
}

static bool ContainsAny(string text, params string[] needles)
{
    foreach (var needle in needles)
    {
        if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

static async Task WritePointerTable(
    StreamWriter writer,
    FileStream dump,
    MinidumpInfo minidump,
    string title,
    uint baseVa,
    int count,
    Func<int, IReadOnlyList<string>> labelResolver,
    Func<string, bool> shouldReportLabel)
{
    await writer.WriteLineAsync($"--- {title} @ 0x{baseVa:X8}, count 0x{count:X} ---");
    var baseOffset = minidump.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(baseVa));
    if (!baseOffset.HasValue)
    {
        await writer.WriteLineAsync("table mapping: <not captured>");
        await writer.WriteLineAsync();
        return;
    }

    await writer.WriteLineAsync($"table mapping: file+0x{baseOffset.Value:X8}");
    var nonZeroCount = 0;
    var reported = 0;
    const int maxRows = 180;

    for (var i = 0; i < count; i++)
    {
        var entryOffset = baseOffset.Value + i * 4L;
        var pointer = await ReadBeUInt32At(dump, entryOffset);
        var labels = labelResolver(i);
        var label = labels.Count == 0 ? "" : string.Join(", ", labels);
        var interesting = pointer != 0 || labels.Any(shouldReportLabel);

        if (pointer != 0)
        {
            nonZeroCount++;
        }

        if (!interesting || reported >= maxRows)
        {
            continue;
        }

        reported++;
        var targetOffset = pointer == 0
            ? null
            : minidump.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(pointer));
        await writer.WriteLineAsync($"[{i,3}] 0x{pointer:X8} {(targetOffset.HasValue ? $"file+0x{targetOffset.Value:X8}" : "<not captured>")} {label}");

        if (targetOffset.HasValue)
        {
            await WriteObjectProbe(writer, dump, minidump, pointer, targetOffset.Value);
        }
    }

    await writer.WriteLineAsync($"non-zero entries: {nonZeroCount} / {count}");
    if (reported >= maxRows)
    {
        await writer.WriteLineAsync($"reported rows capped at {maxRows}");
    }

    await writer.WriteLineAsync();
}

static async Task WriteObjectProbe(
    StreamWriter writer,
    FileStream dump,
    MinidumpInfo minidump,
    uint objectVa,
    long objectOffset)
{
    var bytes = await ReadBytesAt(dump, objectOffset, 0x80);
    await writer.WriteLineAsync("  dwords:");
    for (var i = 0; i <= bytes.Length - 4; i += 4)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i, 4));
        if (value == 0)
        {
            continue;
        }

        var mapped = minidump.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(value));
        if (mapped.HasValue)
        {
            var inline = await TryReadAsciiStringAtVa(dump, minidump, value);
            await writer.WriteLineAsync($"    +0x{i:X2}: 0x{value:X8} -> file+0x{mapped.Value:X8}{(inline == null ? "" : $" \"{inline}\"")}");
        }
        else
        {
            await writer.WriteLineAsync($"    +0x{i:X2}: 0x{value:X8}");
        }
    }

    var strings = await ExtractNearbyStrings(dump, minidump, objectVa, objectOffset, 0x1000);
    foreach (var entry in strings.Take(8))
    {
        await writer.WriteLineAsync($"  nearby string 0x{entry.VirtualAddress:X8}: {entry.Text}");
    }
}

static async Task<string?> TryReadAsciiStringAtVa(
    FileStream dump,
    MinidumpInfo minidump,
    uint va,
    int maxLength = 256)
{
    var offset = minidump.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(va));
    if (!offset.HasValue)
    {
        return null;
    }

    var maxAvailable = Math.Min(maxLength, minidump.GetContiguousBytesFromFileOffset(offset.Value));
    if (maxAvailable <= 0)
    {
        return null;
    }

    var bytes = await ReadBytesAt(dump, offset.Value, (int)maxAvailable);
    var end = 0;
    while (end < bytes.Length && bytes[end] != 0)
    {
        if (!IsPrintableAscii(bytes[end]))
        {
            return null;
        }

        end++;
    }

    return end >= 4
        ? Encoding.ASCII.GetString(bytes, 0, end)
        : null;
}

static async Task<List<StringHit>> ExtractNearbyStrings(
    FileStream dump,
    MinidumpInfo minidump,
    uint objectVa,
    long objectOffset,
    int length)
{
    var contiguous = minidump.GetContiguousBytesFromFileOffset(objectOffset);
    if (contiguous <= 0)
    {
        return [];
    }

    length = (int)Math.Min(length, contiguous);
    var bytes = await ReadBytesAt(dump, objectOffset, length);
    var hits = new List<StringHit>();
    var runStart = -1;

    for (var i = 0; i <= bytes.Length; i++)
    {
        var printable = i < bytes.Length && IsPrintableAscii(bytes[i]);
        if (printable)
        {
            if (runStart < 0)
            {
                runStart = i;
            }

            continue;
        }

        if (runStart >= 0)
        {
            var runLength = i - runStart;
            if (runLength >= 5)
            {
                var text = Encoding.ASCII.GetString(bytes, runStart, runLength);
                if (LooksShaderRelevant(text))
                {
                    hits.Add(new StringHit(objectVa + (uint)runStart, objectOffset + runStart, CleanString(text)));
                }
            }

            runStart = -1;
        }
    }

    return hits;
}

static async Task WriteRawDwords(
    StreamWriter writer,
    FileStream dump,
    MinidumpInfo minidump,
    string title,
    uint baseVa,
    int length)
{
    await writer.WriteLineAsync($"--- {title} @ 0x{baseVa:X8}, length 0x{length:X} ---");
    var offset = minidump.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(baseVa));
    if (!offset.HasValue)
    {
        await writer.WriteLineAsync("mapping: <not captured>");
        await writer.WriteLineAsync();
        return;
    }

    await writer.WriteLineAsync($"mapping: file+0x{offset.Value:X8}");
    var bytes = await ReadBytesAt(dump, offset.Value, length);
    for (var i = 0; i <= bytes.Length - 4; i += 4)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i, 4));
        var mapped = value == 0 ? null : minidump.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(value));
        await writer.WriteLineAsync($"  +0x{i:X2}: 0x{value:X8}{(mapped.HasValue ? $" -> file+0x{mapped.Value:X8}" : "")}");
    }

    await writer.WriteLineAsync();
}

static async Task WriteShaderStrings(StreamWriter writer, FileStream dump, MinidumpInfo minidump)
{
    await writer.WriteLineAsync("=== Shader-Relevant ASCII Strings ===");
    await writer.WriteLineAsync("Region scan reports printable ASCII runs that contain shader/material tokens.");
    await writer.WriteLineAsync();

    var hits = await ScanShaderStrings(dump, minidump, maxHits: 20000);
    await WriteStringBucket(writer, "Shader source/profile/path strings", hits, IsShaderSourceString, 300);
    await WriteStringBucket(writer, "Normal/bump/specular strings", hits, IsNormalBumpSpecularString, 250);
    await WriteStringBucket(writer, "Landscape strings", hits, h => ContainsAny(h.Text, "land", "terrain", "landscape"), 180);
    await WriteStringBucket(writer, "Other shader/material strings", hits, h => true, 180);

    await writer.WriteLineAsync();
    await writer.WriteLineAsync($"matched string hits: {hits.Count}");
}

static async Task WriteStringBucket(
    StreamWriter writer,
    string title,
    IReadOnlyList<StringHit> hits,
    Func<StringHit, bool> predicate,
    int maxRows)
{
    var bucket = hits.Where(predicate).DistinctBy(h => h.Text).Take(maxRows).ToList();
    await writer.WriteLineAsync($"--- {title} ({bucket.Count} shown) ---");
    foreach (var hit in bucket)
    {
        await writer.WriteLineAsync($"0x{hit.VirtualAddress:X8} file+0x{hit.FileOffset:X8}: {hit.Text}");
    }

    await writer.WriteLineAsync();
}

static async Task<List<StringHit>> ScanShaderStrings(FileStream dump, MinidumpInfo minidump, int maxHits)
{
    var hits = new List<StringHit>();
    var buffer = new byte[1024 * 1024];

    foreach (var region in minidump.MemoryRegions.OrderBy(r => r.FileOffset))
    {
        var remaining = region.Size;
        var currentFileOffset = region.FileOffset;
        var currentVa = region.VirtualAddress;
        var run = new List<byte>(512);
        long runFileOffset = 0;
        long runVa = 0;

        while (remaining > 0)
        {
            var readLength = (int)Math.Min(buffer.Length, remaining);
            dump.Seek(currentFileOffset, SeekOrigin.Begin);
            var read = await dump.ReadAsync(buffer.AsMemory(0, readLength));
            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (IsPrintableAscii(b))
                {
                    if (run.Count == 0)
                    {
                        runFileOffset = currentFileOffset + i;
                        runVa = currentVa + i;
                    }

                    if (run.Count < 2048)
                    {
                        run.Add(b);
                    }

                    continue;
                }

                FlushRun();
                if (hits.Count >= maxHits)
                {
                    return hits;
                }
            }

            currentFileOffset += read;
            currentVa += read;
            remaining -= read;
        }

        FlushRun();
        if (hits.Count >= maxHits)
        {
            break;
        }

        void FlushRun()
        {
            if (run.Count >= 5)
            {
                var text = Encoding.ASCII.GetString(run.ToArray());
                if (LooksShaderRelevant(text))
                {
                    hits.Add(new StringHit(runVa, runFileOffset, CleanString(text)));
                }
            }

            run.Clear();
        }
    }

    return hits;
}

static bool IsPrintableAscii(byte value)
{
    return value is >= 0x20 and <= 0x7E;
}

static bool LooksShaderRelevant(string text)
{
    return ContainsAny(
        text,
        "ps_",
        "vs_",
        ".vso",
        ".pso",
        ".sdp",
        ".hlsl",
        "shader",
        "normal",
        "bump",
        "specular",
        "diffuse",
        "ambient",
        "vertexcolor",
        "vertex color",
        "land",
        "lighting",
        "basemap",
        "shadowlight",
        "xenos");
}

static bool IsShaderSourceString(StringHit hit)
{
    return ContainsAny(hit.Text, "ps_", "vs_", ".vso", ".pso", ".sdp", ".hlsl", "shader", "xenos");
}

static bool IsNormalBumpSpecularString(StringHit hit)
{
    return ContainsAny(hit.Text, "normal", "bump", "specular", "gloss", "height");
}

static string CleanString(string text)
{
    text = text.Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);
    return text.Length <= 220 ? text : text[..220] + "...";
}

static async Task<uint> ReadBeUInt32At(FileStream stream, long offset)
{
    var bytes = await ReadBytesAt(stream, offset, 4);
    return BinaryPrimitives.ReadUInt32BigEndian(bytes);
}

static async Task<byte[]> ReadBytesAt(FileStream stream, long offset, int count)
{
    var bytes = new byte[count];
    stream.Seek(offset, SeekOrigin.Begin);
    var total = 0;
    while (total < count)
    {
        var read = await stream.ReadAsync(bytes.AsMemory(total, count - total));
        if (read <= 0)
        {
            Array.Resize(ref bytes, total);
            break;
        }

        total += read;
    }

    return bytes;
}

static async Task WriteShaderPackage(
    StreamWriter writer,
    string shaderPackagePath,
    PdbConstants constants,
    string outputPath)
{
    await writer.WriteLineAsync();
    await writer.WriteLineAsync("=== Shader Package ===");

    if (!File.Exists(shaderPackagePath))
    {
        await writer.WriteLineAsync($"shaderpackage.sdp not found: {shaderPackagePath}");
        return;
    }

    var packageBytes = await File.ReadAllBytesAsync(shaderPackagePath);
    if (packageBytes.Length < 12)
    {
        await writer.WriteLineAsync($"shaderpackage.sdp is too small: {shaderPackagePath}");
        return;
    }

    var header0 = BinaryPrimitives.ReadUInt32BigEndian(packageBytes.AsSpan(0, 4));
    var declaredRecordCount = BinaryPrimitives.ReadUInt32BigEndian(packageBytes.AsSpan(4, 4));
    var declaredPayloadSize = BinaryPrimitives.ReadUInt32BigEndian(packageBytes.AsSpan(8, 4));

    await writer.WriteLineAsync($"Path: {shaderPackagePath}");
    await writer.WriteLineAsync($"Size: 0x{packageBytes.Length:X}");
    await writer.WriteLineAsync($"Header[0]: 0x{header0:X8} ({header0})");
    await writer.WriteLineAsync($"Declared records: 0x{declaredRecordCount:X} ({declaredRecordCount})");
    await writer.WriteLineAsync($"Declared payload bytes: 0x{declaredPayloadSize:X}");

    var records = ParseShaderPackage(packageBytes, constants);
    await writer.WriteLineAsync($"Parsed records: {records.Count}");
    await writer.WriteLineAsync();

    var targetRecords = records
        .Where(r => IsTargetShaderRecord(r) || r.Strings.Any(s => ContainsAny(s, "NormalMap", "Bump", "Specular", "DiffuseMap", "Vertex", "Land", "DecalMap")))
        .ToList();

    await writer.WriteLineAsync("--- Target Records ---");
    foreach (var record in targetRecords)
    {
        await writer.WriteLineAsync(
            $"[{record.Index,3}] file+0x{record.RecordOffset:X6} data+0x{record.DataOffset:X6} size=0x{record.DataSize:X4} {record.Name} {string.Join(", ", record.Labels)}");

        var strings = record.Strings
            .Where(s => s != record.Name && !s.EndsWith(".0", StringComparison.Ordinal))
            .Take(12);
        foreach (var text in strings)
        {
            await writer.WriteLineAsync($"      {text}");
        }
    }

    await writer.WriteLineAsync();
    await writer.WriteLineAsync("--- Record Counts ---");
    await writer.WriteLineAsync($"SLS1 vertex shaders: {records.Count(r => r.Name.StartsWith("SLS1", StringComparison.Ordinal) && r.Name.EndsWith(".vso", StringComparison.OrdinalIgnoreCase) && !r.Name.StartsWith("SLS1S", StringComparison.Ordinal))}");
    await writer.WriteLineAsync($"SLS1 skinned vertex shaders: {records.Count(r => r.Name.StartsWith("SLS1S", StringComparison.Ordinal) && r.Name.EndsWith(".vso", StringComparison.OrdinalIgnoreCase))}");
    await writer.WriteLineAsync($"SLS2 vertex shaders: {records.Count(r => r.Name.StartsWith("SLS2", StringComparison.Ordinal) && r.Name.EndsWith(".vso", StringComparison.OrdinalIgnoreCase))}");
    await writer.WriteLineAsync($"SLS1 pixel shaders: {records.Count(r => r.Name.StartsWith("SLS1", StringComparison.Ordinal) && r.Name.EndsWith(".pso", StringComparison.OrdinalIgnoreCase))}");
    await writer.WriteLineAsync($"SLS2 pixel shaders: {records.Count(r => r.Name.StartsWith("SLS2", StringComparison.Ordinal) && r.Name.EndsWith(".pso", StringComparison.OrdinalIgnoreCase))}");
    await writer.WriteLineAsync($"Image-space/other shaders: {records.Count(r => r.Name.StartsWith("IS", StringComparison.Ordinal))}");

    await ExtractTargetShaderRecords(packageBytes, records, outputPath);
    await writer.WriteLineAsync();
    await writer.WriteLineAsync($"Extracted target records to: {Path.Combine(Path.GetDirectoryName(outputPath)!, "shaderpackage_targets")}");
}

static List<ShaderPackageRecord> ParseShaderPackage(byte[] packageBytes, PdbConstants constants)
{
    var records = new List<ShaderPackageRecord>();
    var offset = 12;
    var index = 0;
    while (offset + 0x104 <= packageBytes.Length)
    {
        var name = ReadNullTerminatedAscii(packageBytes.AsSpan(offset, 0x100));
        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(packageBytes.AsSpan(offset + 0x100, 4));
        var dataOffset = offset + 0x104;
        if (string.IsNullOrWhiteSpace(name) || size < 0 || dataOffset + size > packageBytes.Length)
        {
            break;
        }

        var data = packageBytes.AsSpan(dataOffset, size);
        records.Add(new ShaderPackageRecord(
            index,
            offset,
            name,
            size,
            dataOffset,
            ResolveShaderRecordLabels(name, constants),
            ExtractPrintableRuns(data).Select(CleanString).ToArray()));

        offset = dataOffset + size;
        index++;
    }

    return records;
}

static string ReadNullTerminatedAscii(ReadOnlySpan<byte> bytes)
{
    var length = 0;
    while (length < bytes.Length && bytes[length] != 0)
    {
        length++;
    }

    return Encoding.ASCII.GetString(bytes[..length]);
}

static IReadOnlyList<string> ResolveShaderRecordLabels(string name, PdbConstants constants)
{
    var upper = name.ToUpperInvariant();
    var labels = new List<string>();

    var match = Regex.Match(upper, @"^SLS(?<family>[12])(?<index>\d{3})\.(?<ext>[VP]SO)$");
    if (match.Success && int.TryParse(match.Groups["index"].Value, out var index))
    {
        var family = match.Groups["family"].Value;
        var ext = match.Groups["ext"].Value;
        var prefix = ext == "PSO"
            ? $"SLS_PS{family}_"
            : $"SLS_VS{family}_";
        labels.AddRange(constants.NamesFor(index, prefix));
    }

    var skinned = Regex.Match(upper, @"^SLS1S(?<index>\d{3})\.VSO$");
    if (skinned.Success && int.TryParse(skinned.Groups["index"].Value, out var skinnedIndex))
    {
        labels.AddRange(constants.NamesFor(skinnedIndex, "SLS_VSS_"));
    }

    return labels;
}

static bool IsTargetShaderRecord(ShaderPackageRecord record)
{
    if (ContainsAny(record.Name, "NORMAL", "BUMP", "LAND", "LIGHTING", "SPECULAR", "DIFFUSE", "NOISE"))
    {
        return true;
    }

    return record.Labels.Any(label => ContainsAny(label, "LAND", "LIGHTING", "VC", "NORMAL", "BUMP", "SPECULAR", "DIFFUSE", "ENVMAP", "TEXEFFECT"));
}

static IReadOnlyList<string> ExtractPrintableRuns(ReadOnlySpan<byte> data)
{
    var strings = new List<string>();
    var runStart = -1;

    for (var i = 0; i <= data.Length; i++)
    {
        var printable = i < data.Length && IsPrintableAscii(data[i]);
        if (printable)
        {
            if (runStart < 0)
            {
                runStart = i;
            }

            continue;
        }

        if (runStart >= 0)
        {
            var length = i - runStart;
            if (length >= 4)
            {
                strings.Add(Encoding.ASCII.GetString(data.Slice(runStart, length)));
            }

            runStart = -1;
        }
    }

    return strings.Distinct(StringComparer.Ordinal).ToArray();
}

static async Task ExtractTargetShaderRecords(byte[] packageBytes, IReadOnlyList<ShaderPackageRecord> records, string outputPath)
{
    var directory = Path.Combine(Path.GetDirectoryName(outputPath)!, "shaderpackage_targets");
    Directory.CreateDirectory(directory);

    foreach (var record in records.Where(IsTargetShaderRecord))
    {
        var targetPath = Path.Combine(directory, record.Name);
        await File.WriteAllBytesAsync(targetPath, packageBytes.AsMemory((int)record.DataOffset, record.DataSize).ToArray());
    }
}

internal sealed record StringHit(long VirtualAddress, long FileOffset, string Text);

internal sealed record ShaderPackageRecord(
    int Index,
    long RecordOffset,
    string Name,
    int DataSize,
    long DataOffset,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Strings);

internal sealed class PdbConstants
{
    private static readonly Regex ConstantRegex = new(
        @"S_CONSTANT:\s+Type:\s+0x[0-9A-Fa-f]+,\s+Value:\s+(-?\d+),\s+(.+)$",
        RegexOptions.Compiled);

    private readonly Dictionary<int, List<string>> _byValue = [];

    private PdbConstants()
    {
    }

    public static PdbConstants Load(string path)
    {
        var result = new PdbConstants();
        foreach (var line in File.ReadLines(path))
        {
            var match = ConstantRegex.Match(line);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var value))
            {
                continue;
            }

            var name = match.Groups[2].Value.Trim();
            if (!result._byValue.TryGetValue(value, out var names))
            {
                names = [];
                result._byValue.Add(value, names);
            }

            names.Add(name);
        }

        foreach (var names in result._byValue.Values)
        {
            names.Sort(StringComparer.Ordinal);
        }

        return result;
    }

    public IReadOnlyList<string> NamesFor(int value, string prefix)
    {
        if (!_byValue.TryGetValue(value, out var names))
        {
            return [];
        }

        return names
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
    }
}
