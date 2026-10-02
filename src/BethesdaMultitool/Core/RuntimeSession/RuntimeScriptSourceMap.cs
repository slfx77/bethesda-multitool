using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Script;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Joins exact captured script identities to physical source blocks, with independent location admission.</summary>
public static class RuntimeScriptSourceMap
{
    public static async Task<RuntimeScriptSourceMapReport> BuildAsync(RuntimeTraceDocument trace,
        IReadOnlyList<string> sourcePaths, CancellationToken token = default)
    {
        var sources = await RuntimeTraceSources.ReadAsync(sourcePaths, true, token);
        var binding = RuntimeTraceBinding.Match(trace, sources);
        var requested = new HashSet<uint>();
        foreach (var observation in trace.Events)
        {
            token.ThrowIfCancellationRequested();
            using var line = JsonDocument.Parse(trace.ReadSourceLine(observation));
            var id = ScriptId(observation, line.RootElement);
            if (id is > 0 && (id.Value >> 24) != 0xFF) requested.Add(id.Value);
        }
        var catalog = binding.Matched
            ? await RuntimeScriptBlockCatalog.LoadAsync(sourcePaths, sources, requested, token)
            : new RuntimeScriptCatalog([], new Dictionary<uint, string>());
        return Build(trace, sources, catalog, token);
    }

    public static string Serialize(RuntimeScriptSourceMapReport report) =>
        JsonSerializer.Serialize(report, RuntimeScriptSourceMapJsonContext.Default.RuntimeScriptSourceMapReport);

    internal static RuntimeScriptSourceMapReport Build(RuntimeTraceDocument trace, RuntimeTraceSources sources,
        RuntimeScriptCatalog catalog, CancellationToken token = default)
    {
        var binding = RuntimeTraceBinding.Match(trace, sources);
        var calibration = RuntimeScriptOffsetCalibration.Observe(trace);
        var mappings = new List<RuntimeScriptMapping>();
        foreach (var observation in trace.Events)
        {
            token.ThrowIfCancellationRequested();
            using var line = JsonDocument.Parse(trace.ReadSourceLine(observation));
            var data = line.RootElement;
            if (observation.ScriptFormId is null && !Object(data, "commandLocation", out _) &&
                observation.Kind is not ("script-entry" or "script-exit")) continue;
            mappings.Add(Map(trace, observation, data, binding, catalog, calibration));
        }
        var offsetScope = "Offset calibration: Unavailable (paired same-trace dispatcher proof required)";
        if (calibration is not null)
            offsetScope = $"Offset calibration: Scoped to PC expression-X caller 005ACBB8; matched commands={mappings.Count(item => item.LocationStatus == "Matched")}; all other callers remain unavailable";
        return new("bethesda-multitool/runtime-script-source-map", 1, trace.Summary.Sha256, binding,
            sources.Plugins, catalog.Blocks, mappings,
            ["Source lines: Reconstruction", "Runtime reference/local tables: Unavailable",
             "Temporary/embedded owner mapping: Unavailable",
             offsetScope,
             "Script::Execute block/instruction coverage: Unavailable"]);
    }

    private static RuntimeScriptMapping Map(RuntimeTraceDocument trace, RuntimeTraceEvent observation, JsonElement data,
        RuntimeTraceBinding binding, RuntimeScriptCatalog catalog, RuntimeScriptOffsetCalibration? calibration)
    {
        var id = ScriptId(observation, data);
        var hasLocation = Object(data, "commandLocation", out var location);
        var raw = hasLocation ? location.Clone() : (JsonElement?)null;
        var blocks = id.HasValue ? catalog.Blocks.Where(block => block.LoadOrderFormId == id.Value).ToArray() : [];
        var keys = blocks.Select(block => block.Identity).ToArray();
        RuntimeScriptMapping Result(string owner, string ownerReason, string bytecode = "Unavailable",
            string bytecodeReason = "runtime-bytecode-not-observed", string position = "Unavailable",
            string positionReason = "offset-basis-unverified", RuntimeScriptBlock? block = null,
            int? offset = null, ScriptInstructionSpan? instruction = null) =>
            new(observation.Line, observation.Offset, observation.Length, observation.Sequence, observation.Kind, id,
                owner, ownerReason, bytecode, bytecodeReason, position, positionReason, keys, block?.Identity,
                offset, block?.ScdaFileOffset is { } file && offset.HasValue ? file + offset.Value : null, instruction, raw);

        if (!binding.Matched) return Result("Unbound", binding.Reason);
        var beforeScript = Child(Child(location, "before"), "script");
        var flags = Number(beforeScript, "flags");
        var topFlags = Number(data, "scriptFlags");
        var temporary = data.TryGetProperty("temporaryScript", out var temporaryValue) && temporaryValue.ValueKind == JsonValueKind.True;
        if (id is null or 0 || (id.Value >> 24) == 0xFF || temporary ||
            ((flags.GetValueOrDefault() | topFlags.GetValueOrDefault()) & 0x4000) != 0)
            return Result("Unavailable", "temporary-or-anonymous-script");
        if (hasLocation && topFlags.HasValue && flags != topFlags)
            return Result("Unavailable", "runtime-script-flags-inconsistent");
        if (catalog.OwnerExclusions.TryGetValue(id.Value, out var excluded))
            return Result(excluded == "ambiguous-physical-winner" ? "Ambiguous" : "Unavailable", excluded);
        if (blocks.Length == 0) return Result("Unavailable", "owner-not-in-source");
        if (blocks.Any(block => block.RecordType != "SCPT"))
            return Result("Unavailable", "embedded-owner-not-observed");
        if (hasLocation && !StableOwner(location, id.Value))
            return Result("Unavailable", "runtime-script-identity-incomplete-or-changed");
        if (!hasLocation) return Result("Matched", "exact-SCPT-owner");
        if (!StableFingerprint(location, id.Value, out var hash, out var length, out var byteOrder))
            return Result("Matched", "exact-SCPT-owner", "Unavailable", "runtime-bytecode-incomplete-or-changed");
        var matching = blocks.Where(block => block.ScdaLength == length && block.ScdaSha256.Equals(hash,
            StringComparison.OrdinalIgnoreCase) && block.ByteOrder == byteOrder).ToArray();
        if (matching.Length == 0) return Result("Matched", "exact-SCPT-owner", "Mismatch", "SCDA-hash-length-or-order-differs");
        if (matching.Length != 1) return Result("Matched", "exact-SCPT-owner", "Ambiguous", "multiple-physical-blocks-match");
        var selected = matching[0];
        RuntimeScriptMapping At(string status, string reason, int? offset = null, ScriptInstructionSpan? instruction = null) =>
            Result("Matched", "exact-SCPT-owner", "Matched", "exact-complete-runtime-bytecode", status, reason, selected, offset, instruction);
        if (calibration is null || !calibration.Allows(trace, observation, data, location, byteOrder))
            return At("Unavailable", "offset-basis-unverified");
        var before = Child(location, "before");
        if (Text(before, "opcodeOffsetStatus") != "observed" || Number(before, "opcodeOffset") is not { } rawOffset ||
            Number(before, "scriptDataAddress") != Number(beforeScript, "dataAddress") ||
            Text(before, "pointerRangeStatus") != "data-start" ||
            Number(Child(location, "after"), "scriptDataAddress") != Number(beforeScript, "dataAddress") ||
            Text(Child(location, "after"), "pointerRangeStatus") != "data-start" ||
            Number(before, "opcodeOffsetPointer") is not > 0 ||
            Number(before, "opcodeOffsetPointer") != Number(Child(location, "after"), "opcodeOffsetPointer"))
            return At("Unavailable", "offset-buffer-not-calibrated");
        var normalized = (long)rawOffset + RuntimeScriptOffsetCalibration.BeforeOffsetAdjustment;
        if (normalized < 0 || normalized >= selected.ScdaLength) return At("Unavailable", "offset-outside-bytecode");
        var offsetValue = (int)normalized;
        var instruction = selected.Reconstruction.Instructions.Where(item => item.Offset <= offsetValue &&
            offsetValue < item.Offset + item.Length).OrderBy(item => item.Length).FirstOrDefault();
        if (instruction is null) return At("Unavailable", "offset-has-no-decoded-span", offsetValue);
        if (instruction.Status != "Decoded") return At("Unavailable", "instruction-" + instruction.Status.ToLowerInvariant(), offsetValue, instruction);
        if (instruction.Opcode == ScriptOpcodes.SetRef) return At("ReferencePrefix", "SetRef-prefix", offsetValue, instruction);
        if (instruction.Offset != offsetValue) return At("Operand", "inside-instruction-not-opcode-entry", offsetValue, instruction);
        if (Number(data, "opcode") is not { } runtimeOpcode || instruction.Opcode != runtimeOpcode)
            return At("Mismatch", "observed-opcode-differs", offsetValue, instruction);
        if (instruction.Kind != "ExpressionCall") return At("Unavailable", "instruction-route-not-calibrated", offsetValue, instruction);
        return At("Matched", calibration.Evidence, offsetValue, instruction);
    }

    private static bool StableFingerprint(JsonElement location, uint id, out string? hash, out uint? length, out string? byteOrder)
    {
        hash = null; length = null; byteOrder = null;
        if (Text(location, "status") != "observed" || !location.TryGetProperty("bytecodeStableAcrossCall", out var stable) ||
            stable.ValueKind != JsonValueKind.True) return false;
        var before = Child(location, "before"); var after = Child(location, "after");
        var a = Child(before, "script");
        if (!StableOwner(location, id) || Number(a, "dataAddress") is not > 0) return false;
        var codeA = Child(before, "bytecode"); var codeB = Child(after, "bytecode");
        hash = Text(codeA, "sha256"); length = Number(codeA, "length"); byteOrder = Text(codeA, "byteOrder");
        return Text(codeA, "status") == "observed" && Text(codeB, "status") == "observed" &&
            Text(codeA, "scope") == "entire-Script-data" && Text(codeB, "scope") == "entire-Script-data" &&
            hash is { Length: 64 } && hash.All(Uri.IsHexDigit) && hash.Equals(Text(codeB, "sha256"), StringComparison.OrdinalIgnoreCase) &&
            length is > 0 and <= 65536 && length == Number(codeB, "length") && length == Number(a, "dataLength") &&
            byteOrder is "little" or "big" && byteOrder == Text(codeB, "byteOrder");
    }

    private static bool StableOwner(JsonElement location, uint id)
    {
        var before = Child(Child(location, "before"), "script");
        var after = Child(Child(location, "after"), "script");
        if (Text(before, "status") != "observed" || Text(after, "status") != "observed" ||
            Number(before, "formId") != id || Number(before, "formType") != 0x11 || Number(before, "address") is not > 0)
            return false;
        foreach (var field in new[] { "address", "formId", "formType", "flags", "dataAddress", "dataLength" })
            if (Number(before, field) is not { } value || Number(after, field) != value) return false;
        return true;
    }

    private static uint? ScriptId(RuntimeTraceEvent observation, JsonElement data) => observation.ScriptFormId ??
        Number(Child(Child(Child(data, "commandLocation"), "before"), "script"), "formId");
    private static uint? Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Number && child.TryGetUInt32(out var number) ? number : null;
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object ? RuntimeTraceDocument.Text(value, name) : null;
    private static JsonElement Child(JsonElement value, string name) => Object(value, name, out var child) ? child : default;
    private static bool Object(JsonElement value, string name, out JsonElement child)
    {
        child = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out child) && child.ValueKind == JsonValueKind.Object;
    }
}
