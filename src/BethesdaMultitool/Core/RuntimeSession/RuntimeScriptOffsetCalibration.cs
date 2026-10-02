using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

// One independently inspected dispatcher, not a generic executable-to-offset table.
// Only this trace's paired native observations can construct an admission.
internal sealed class RuntimeScriptOffsetCalibration
{
    internal const string ExecutableHash = "3a87f92f011e5dc9179ddf733cf08be2b39ea6e5b7a8a9e3a9a72dafcc1b104d";
    internal const string WindowHash = "ab2406854fc4e897fbdbc1e9b63f4ae68d7f47c124bd47c24a5fcf832049348f";
    internal const string Route = "pc-expression-X-command";
    internal const int BeforeOffsetAdjustment = -4;
    private readonly int _startLine, _endLine;
    private readonly ulong _startSequence, _endSequence;
    private readonly string _traceHash;

    private RuntimeScriptOffsetCalibration(RuntimeTraceDocument trace, RuntimeTraceEvent start, RuntimeTraceEvent end)
    {
        _traceHash = trace.Summary.Sha256;
        _startLine = start.Line; _endLine = end.Line;
        _startSequence = start.Sequence; _endSequence = end.Sequence;
    }

    internal string Evidence => $"same-trace-dispatch-window:{Route}:start-line={_startLine}:end-line={_endLine}:opcode-header-minus-4";

    internal static RuntimeScriptOffsetCalibration? Observe(RuntimeTraceDocument trace)
    {
        if (!trace.Summary.Complete || trace.Summary.Status != "completed" || trace.Summary.Dropped != 0 ||
            trace.Summary.MissingSequences != 0 || trace.Summary.Errors != 0 || trace.Summary.ControllerErrors != 0 ||
            Text(trace.Identity, "backend") != "xnvse" || Text(trace.Identity, "executableFileSha256") != ExecutableHash ||
            Number(trace.Identity, "processId") is not > 0 ||
            !DateTimeOffset.TryParse(Text(trace.Identity, "processStartedUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var started) || started.UtcDateTime < DateTime.FromFileTimeUtc(0)) return null;
        var starts = trace.Events.Where(item => item.Kind == "capture-start").ToArray();
        var ends = trace.Events.Where(item => item.Kind == "capture-end").ToArray();
        if (starts.Length != 1 || ends.Length != 1 || starts[0].Line >= ends[0].Line || starts[0].Sequence >= ends[0].Sequence) return null;
        using var startLine = JsonDocument.Parse(trace.ReadSourceLine(starts[0]));
        using var endLine = JsonDocument.Parse(trace.ReadSourceLine(ends[0]));
        var start = Child(startLine.RootElement, "dispatchWindow");
        var end = Child(endLine.RootElement, "dispatchWindow");
        if (Text(endLine.RootElement, "status") != "completed" ||
            !Proof(start, "start", trace.Identity, started) || !Proof(end, "end", trace.Identity, started) ||
            !Proof(start, "start", trace.PluginIdentity, started)) return null;
        foreach (var name in new[] { "captureGeneration", "connectionGeneration", "nativeImageBase" })
            if (Number(start, name) is not > 0 || Number(start, name) != Number(end, name)) return null;
        if (string.IsNullOrWhiteSpace(Text(start, "session")) || Text(start, "session") != Text(end, "session") ||
            Text(start, "session") != Text(startLine.RootElement, "session") || Text(start, "nativeSha256") != Text(end, "nativeSha256")) return null;
        return new(trace, starts[0], ends[0]);
    }

    internal bool Allows(RuntimeTraceDocument trace, RuntimeTraceEvent observation, JsonElement data, JsonElement location, string? byteOrder) =>
        trace.Summary.Sha256 == _traceHash && observation.Line > _startLine && observation.Line < _endLine &&
        observation.Sequence > _startSequence && observation.Sequence < _endSequence &&
        Text(location, "basis") == "raw-sdk-command-arguments" && byteOrder == "little" &&
        Number(data, "callerAddress") == 0x005ACBB8 && Number(data, "callerImageBase") == 0x00400000 &&
        Number(data, "callerRva") == 0x001ACBB8;

    private static bool Proof(JsonElement proof, string boundary, JsonElement identity, DateTimeOffset started)
    {
        if (Number(proof, "schemaVersion") != 1 || Text(proof, "route") != Route || Text(proof, "boundary") != boundary ||
            Text(proof, "status") != "observed" || Text(proof, "reason") != "repeated-main-image-read" ||
            Number(proof, "processId") != Number(identity, "processId") ||
            Number(proof, "processCreationFileTime") != (ulong)started.ToFileTime() ||
            Text(proof, "executableFileSha256") != ExecutableHash || Number(proof, "imageBase") != 0x00400000 ||
            Number(proof, "address") != 0x005AC7A0 || Number(proof, "length") != 1161 ||
            Text(proof, "sha256") != WindowHash || Text(proof, "bytesHex") is not { Length: 2322 } hex ||
            !hex.All(Uri.IsHexDigit)) return false;
        if (Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(hex))) != WindowHash) return false;
        var nativeHash = Text(proof, "nativeSha256");
        if (nativeHash is not { Length: 64 } || !nativeHash.All(Uri.IsHexDigit) || Number(proof, "nativeImageBase") is not > 0 ||
            !identity.TryGetProperty("loadedRuntimeModules", out var modules) || modules.ValueKind != JsonValueKind.Array) return false;
        var matched = 0;
        foreach (var module in modules.EnumerateArray())
        {
            var mapped = Text(module, "mappedFile");
            if (mapped is null || !mapped.EndsWith("\\NvseRuntimeBridge.dll", StringComparison.OrdinalIgnoreCase)) continue;
            ++matched;
            if (Text(module, "sha256") != nativeHash || Number(module, "imageBase") != Number(proof, "nativeImageBase") ||
                Text(module, "hashScope") != "mapped-module-backing-file-on-disk") return false;
        }
        return matched == 1;
    }

    private static ulong? Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Number && child.TryGetUInt64(out var number) ? number : null;
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object ? RuntimeTraceDocument.Text(value, name) : null;
    private static JsonElement Child(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object ? child : default;
}
