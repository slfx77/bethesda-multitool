using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Matches retained native QUST rows to the selected stored record. It does not reevaluate conditions.</summary>
public static class RuntimeQuestConditionSourceMap
{
    private sealed record Point(RuntimeTraceEvent Event, JsonElement Data);
    private static readonly string[] KeyFields = ["connectionGeneration", "captureGeneration", "loadEpoch", "requestId", "invocationId"];
    private static readonly string[] CommonFields = ["threadId", "ownerFormId", "ownerAddress", "headAddress",
        "subjectFormId", "subjectAddress", "subjectBaseFormId", "subjectBaseAddress", "targetFormId",
        "targetAddress", "targetBaseFormId", "targetBaseAddress", "ownerListGetterAddress"];
    private static readonly string[] RequestedFields = ["requestedOwnerPlugin", "requestedSubjectPlugin", "requestedTargetPlugin"];
    private static readonly string[] LocalFields = ["requestedOwnerLocalId", "requestedSubjectLocalId", "requestedTargetLocalId"];

    public static async Task<RuntimeQuestConditionSourceMapReport> BuildAsync(RuntimeTraceDocument trace,
        IReadOnlyList<string> sourcePaths, CancellationToken token = default)
    {
        var sources = await RuntimeTraceSources.ReadAsync(sourcePaths, true, token);
        var binding = RuntimeTraceBinding.Match(trace, sources);
        var points = new List<Point>();
        foreach (var observation in trace.Events)
        {
            token.ThrowIfCancellationRequested();
            if (observation.Kind is not ("condition-list-entry" or "condition-source-row" or "condition-list-exit")) continue;
            using var line = JsonDocument.Parse(trace.ReadSourceLine(observation));
            if (Number(line.RootElement, "schemaVersion") == 2) points.Add(new(observation, line.RootElement.Clone()));
        }
        var requested = points.Select(point => Id(point.Data, "ownerFormId")).OfType<uint>()
            .Where(id => id != 0 && (id >> 24) != 0xFF).ToHashSet();
        var records = binding.Matched ? await RuntimeQuestConditionCatalog.LoadAsync(sourcePaths, sources, requested, token) : [];
        var mappings = points.GroupBy(point => Key(point.Data) ?? "invalid:" + point.Event.Line.ToString(CultureInfo.InvariantCulture))
            .Select(group => Map(trace, sources, binding, records, group.ToArray())).ToArray();
        return new("bethesda-multitool/runtime-quest-condition-source-map", 1, trace.Summary.Sha256, binding,
            sources.Plugins, records, mappings, ["Scope: QUST top-level GetIsID subset", "Execution: preserved native evidence",
                "Winning stored rows: value and identity match; engine load origin not independently observed", "Full condition trace: Unavailable"]);
    }

    public static string Serialize(RuntimeQuestConditionSourceMapReport report) =>
        JsonSerializer.Serialize(report, RuntimeQuestConditionSourceJsonContext.Default.RuntimeQuestConditionSourceMapReport);

    private static RuntimeQuestConditionMapping Map(RuntimeTraceDocument trace, RuntimeTraceSources sources,
        RuntimeTraceBinding binding, IReadOnlyList<RuntimeQuestConditionRecord> records, Point[] points)
    {
        var first = points[0].Data;
        var owner = Id(first, "ownerFormId");
        var candidates = records.Where(record => record.LoadOrderFormId == owner).ToArray();
        var source = points.Where(point => point.Event.Kind == "condition-source-row").ToArray();
        var entries = points.Where(point => point.Event.Kind == "condition-list-entry").ToArray();
        var exits = points.Where(point => point.Event.Kind == "condition-list-exit").ToArray();
        RuntimeQuestConditionMapping Result(string status, string reason, RuntimeQuestConditionRecord? record = null,
            IReadOnlyList<RuntimeQuestConditionRowMatch>? rows = null) =>
            new(Number(first, "requestId"), Number(first, "invocationId"), owner, status, reason, record?.Identity,
                candidates.Select(candidate => candidate.Identity).ToArray(), rows ?? source.Select(point =>
                    new RuntimeQuestConditionRowMatch(point.Event.Line, point.Event.Sequence,
                        RowIndex(point.Data), null, null, "Unavailable", reason, point.Data)).ToArray(),
                exits.Length == 1 ? exits[0].Data : null);
        if (!binding.Matched) return Result("Unbound", binding.Reason);
        if (!trace.Summary.Complete || trace.Summary.Dropped != 0 || trace.Summary.MissingSequences != 0 || trace.Summary.Errors != 0)
            return Result("Unavailable", "trace-incomplete-or-loss");
        if (Key(first) is null || entries.Length != 1 || exits.Length != 1 ||
            points.Any(point => !SameCommon(first, point.Data))) return Result("Unavailable", "invocation-identity-or-pairing");
        var entry = entries[0]; var exit = exits[0];
        var count = Number(entry.Data, "sourceRowCount");
        if (count is null or 0 or > 8 || source.Length != (int)count.Value ||
            entry.Event.Sequence >= exit.Event.Sequence || source.Where((point, index) =>
                RowIndex(point.Data) != index || point.Event.Sequence <= entry.Event.Sequence || point.Event.Sequence >= exit.Event.Sequence).Any())
            return Result("Unavailable", "source-row-coverage");
        if (source.Select(point => Text(point.Data, "ownerHeaderBeforeHex")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            return Result("Unavailable", "owner-header-changed");
        if (owner is null or 0 || (owner.Value >> 24) == 0xFF ||
            Text(first, "ownerLayout") != "QUST+0x54" || Number(first, "ownerListGetterAddress") != 0x5F5760 ||
            Number(first, "ownerAddress") is not { } address || address < 0x10000 || address > uint.MaxValue - 0x54 ||
            Number(first, "headAddress") != address + 0x54 || !True(exit.Data, "ownerSourceStable"))
            return Result("Unavailable", "owner-layout-or-stability");
        var requested = sources.Plugins.Where(plugin => plugin.Name.Equals(Text(first, "requestedOwnerPlugin"), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (requested.Length != 1 || Number(first, "requestedOwnerLocalId") is not { } local || local is 0 or > 0xFFFFFF ||
            ((uint)requested[0].Index << 24 | (uint)local) != owner)
            return Result("Unavailable", "requested-owner-namespace");
        if (!RequestedActorMatches(sources, first, "Subject", "subject") ||
            !RequestedActorMatches(sources, first, "Target", "target"))
            return Result("Unavailable", "requested-actor-namespace");
        if (candidates.Length == 0) return Result("Unavailable", "owner-not-in-source");
        if (candidates.Any(record => record.Status == "source-framing-incomplete")) return Result("Unavailable", "source-framing-incomplete");
        if (candidates.Any(record => record.Status == "ambiguous-physical-winner")) return Result("Ambiguous", "ambiguous-physical-winner");
        var winners = candidates.Where(record => record.Selected).ToArray();
        if (winners.Length != 1) return Result("Ambiguous", "physical-winner-unavailable");
        var winner = winners[0];
        if (winner.Status != "supported") return Result("Unavailable", winner.Status);
        var stored = winner.Rows.Where(row => row.SourceRow.HasValue).ToArray();
        if (stored.Length != source.Length) return Result("Mismatch", "stored-runtime-row-count", winner);
        var nodes = new HashSet<uint>(); var items = new HashSet<uint>();
        var matched = new List<RuntimeQuestConditionRowMatch>();
        for (var index = 0; index < source.Length; index++)
        {
            var row = source[index]; var disk = stored[index];
            var reason = MatchRow(first, row.Data, disk, owner.Value, index, nodes, items);
            if (reason is null && index == 0 && Number(row.Data, "nodeAddress") != Number(first, "headAddress")) reason = "first-node-head-mismatch";
            if (reason is null && index + 1 < source.Length &&
                U32(Hex(row.Data, "nodeBeforeHex", 8)!, 4) != Id(source[index + 1].Data, "nodeAddress")) reason = "source-node-order-mismatch";
            matched.Add(new(row.Event.Line, row.Event.Sequence, index, disk.SubrecordIndex, disk.PayloadOffset,
                reason is null ? "Matched" : "Mismatch", reason ?? "exact-stored-row-and-resolved-identities", row.Data));
        }
        return matched.All(row => row.Status == "Matched") ? Result("Matched", "matched-winning-stored-rows", winner, matched) :
            Result("Mismatch", "stored-runtime-row-mismatch", winner, matched);
    }

    private static string? MatchRow(JsonElement common, JsonElement row, RuntimeQuestConditionStoredRow stored,
        uint owner, int ordinal, HashSet<uint> nodes, HashSet<uint> items)
    {
        var raw = Hex(row, "runtimeItemBeforeHex", 28); var after = Hex(row, "runtimeItemAfterHex", 28);
        var node = Hex(row, "nodeBeforeHex", 8); var nodeAfter = Hex(row, "nodeAfterHex", 8);
        var header = Hex(row, "ownerHeaderBeforeHex", 16);
        if (raw is null || after is null || node is null || nodeAfter is null || header is null ||
            !raw.SequenceEqual(after) || !node.SequenceEqual(nodeAfter) || !True(row, "ownerSourceStable") || !True(row, "serializedAfterEvaluation"))
            return "source-bytes-unavailable-or-changed";
        if (header[4] != 0x47 || U32(header, 12) != owner || (U32(header, 8) & 0x4020) != 0)
            return "owner-header-mismatch";
        if (!Pointer(row, "nodeAddress", out var nodeAddress) || !Pointer(row, "itemAddress", out var itemAddress) ||
            !nodes.Add(nodeAddress) || !items.Add(itemAddress) || U32(node, 0) != itemAddress) return "source-node-identity";
        var disk = Convert.FromHexString(stored.RawHex);
        if (stored.SourceRow != ordinal || disk.Length != 28 || !raw.AsSpan(0, 12).SequenceEqual(disk.AsSpan(0, 12)) ||
            U32(raw, 16) != U32(disk, 16) || U32(raw, 20) != U32(disk, 20) || Number(row, "runOn") != U32(raw, 20))
            return "stored-literal-function-or-run-on-mismatch";
        if (!Object(row, "resolvedParameter1", out var parameter) || !Form(parameter, 0x2A) ||
            Number(parameter, "address") != U32(raw, 12) || Id(parameter, "formId") != stored.Parameter1FormId)
            return "resolved-parameter-mismatch";
        if (!Object(row, "resolvedActor", out var actor) || !Form(actor, 0x3B) ||
            !Object(row, "resolvedActorBase", out var actorBase) || !Form(actorBase, 0x2A)) return "resolved-actor-unavailable";
        var runOn = U32(raw, 20);
        if (runOn < 2)
        {
            var prefix = runOn == 0 ? "subject" : "target";
            if (U32(raw, 24) != 0 || Id(actor, "formId") != Id(common, prefix + "FormId") ||
                Id(actor, "address") != Id(common, prefix + "Address") ||
                Id(actorBase, "formId") != Id(common, prefix + "BaseFormId") ||
                Id(actorBase, "address") != Id(common, prefix + "BaseAddress")) return "run-on-actor-mismatch";
        }
        else if (runOn != 2 || Id(actor, "formId") != stored.ReferenceFormId || Id(actor, "address") != U32(raw, 24))
            return "explicit-reference-mismatch";
        return null;
    }

    private static bool SameCommon(JsonElement left, JsonElement right) => Key(left) == Key(right) &&
        CommonFields.All(field => Number(left, field) is { } value && value != 0 && Number(right, field) == value) &&
        LocalFields.All(field => Number(left, field) is { } value && Number(right, field) == value) &&
        RequestedFields.All(field => Text(left, field) is { Length: > 0 } value && Text(right, field) == value) &&
        Text(left, "ownerLayout") == Text(right, "ownerLayout");
    private static bool RequestedActorMatches(RuntimeTraceSources sources, JsonElement data, string requested, string actual)
    {
        var name = Text(data, "requested" + requested + "Plugin");
        var local = Number(data, "requested" + requested + "LocalId");
        if (name is null || local is null or 0 or > 0xFFFFFF) return false;
        uint expected;
        if (name == "@player")
        {
            if (local != 0x14) return false;
            expected = 0x14;
        }
        else
        {
            var matches = sources.Plugins.Where(plugin => plugin.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) return false;
            expected = (uint)matches[0].Index << 24 | (uint)local.Value;
        }
        return Id(data, actual + "FormId") == expected &&
            (expected != 0x14 || Id(data, actual + "BaseFormId") == 7);
    }
    private static string? Key(JsonElement data)
    {
        var values = KeyFields.Select(field => Number(data, field)).ToArray();
        return values.Any(value => value is null or 0) ? null : string.Join(':', values.Select(value => value!.Value.ToString(CultureInfo.InvariantCulture)));
    }
    private static int RowIndex(JsonElement data) => Number(data, "sourceRow") is { } value && value <= int.MaxValue ? (int)value : -1;
    private static ulong? Number(JsonElement data, string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number : null;
    private static uint? Id(JsonElement data, string name) => Number(data, name) is { } value && value <= uint.MaxValue ? (uint)value : null;
    private static string? Text(JsonElement data, string name) => RuntimeTraceDocument.Text(data, name);
    private static bool True(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool Object(JsonElement data, string name, out JsonElement value) => data.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    private static bool Pointer(JsonElement data, string name, out uint address)
    { address = Id(data, name).GetValueOrDefault(); return address >= 0x10000 && address <= uint.MaxValue - 28 && (address & 3) == 0; }
    private static bool Form(JsonElement data, uint type) => Pointer(data, "address", out _) && Number(data, "formType") == type &&
        Id(data, "formId") is { } id && id != 0 && (id >> 24) != 0xFF;
    private static byte[]? Hex(JsonElement data, string field, int bytes)
    {
        var text = Text(data, field);
        if (text?.Length != bytes * 2 || !text.All(Uri.IsHexDigit)) return null;
        return Convert.FromHexString(text);
    }
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
