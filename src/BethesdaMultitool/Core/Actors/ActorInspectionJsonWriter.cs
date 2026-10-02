using System.Text.Json;

namespace BethesdaMultitool.Core.Actors;

/// <summary>Writes actor inspection data without runtime reflection or graphics dependencies.</summary>
internal static class ActorInspectionJsonWriter
{
    /// <summary>Writes invariant actor fields, candidate provenance and any retained seeded preview without closing the output.</summary>
    /// <param name="output">Writable caller-owned stream; the writer flushes its JSON and leaves this stream open.</param>
    /// <param name="actor">One inspected actor with authored statistics, inventory trace and optional concrete generation.</param>
    /// <param name="source">Source label recorded verbatim in the machine-readable report.</param>
    /// <param name="playerLevel">Optional supplied preview level, retained under the legacy playerLevel JSON field for compatibility.</param>
    /// <param name="writeContext">Optional load-order metadata writer; called inside the root object.</param>
    internal static void Write(Stream output, ActorInspection actor, string source, ushort? playerLevel,
        Action<Utf8JsonWriter>? writeContext = null, Action<Utf8JsonWriter>? writeStatistics = null)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("source", source);
        writer.WriteNumber("formId", actor.FormId);
        writer.WriteString("name", actor.Name);
        writer.WriteString("kind", actor.Kind);
        writer.WriteBoolean("partialCapture", actor.IsPartialCapture);
        writer.WriteString("templateSemantics", actor.TemplateSemantics);
        if (writeContext is not null) writeContext(writer);
        else
        {
            writer.WriteStartArray("masters");
            foreach (var master in actor.Masters)
            {
                writer.WriteStartObject();
                writer.WriteString("name", master.Name);
                writer.WriteBoolean("loaded", master.Loaded);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writeStatistics?.Invoke(writer);
        writer.WriteString("inventoryMode", actor.Generation is null ? "authored-and-eligible-candidates" : "seeded-preview");
        if (playerLevel is { } level) writer.WriteNumber("playerLevel", level);
        if (actor.InventoryNotice is { } notice) writer.WriteString("inventoryNotice", notice);
        writer.WriteStartArray("statistics");
        foreach (var statistic in actor.Statistics)
        {
            writer.WriteStartObject();
            writer.WriteString("key", statistic.Key);
            writer.WriteString("value", statistic.Value);
            if (statistic.Reference is { } reference) writer.WriteNumber("reference", reference);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("inventory");
        foreach (var row in actor.Inventory)
        {
            writer.WriteStartObject();
            writer.WriteNumber("formId", row.ItemFormId);
            writer.WriteString("name", row.Name);
            writer.WriteNumber("count", row.Count);
            writer.WriteNumber("sourceActor", row.SourceActor);
            writer.WriteNumber("depth", row.Depth);
            writer.WriteString("status", row.Status);
            if (row.RequiredLevel is { } required) writer.WriteNumber("requiredLevel", required);
            if (row.ChanceNone is { } chance) writer.WriteNumber("chanceNone", chance);
            if (row.ResolvedChanceNone is { } resolvedChance) writer.WriteNumber("resolvedChanceNone", resolvedChance);
            if (row.Flags is { } flags) writer.WriteNumber("flags", flags);
            if (row.GlobalFormId is { } global) writer.WriteNumber("globalFormId", global);
            if (row.OwnerFormId is { } owner) writer.WriteNumber("ownerFormId", owner);
            if (row.ItemCondition is { } condition && float.IsFinite(condition)) writer.WriteNumber("condition", condition);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteTemplateData(writer, actor);
        if (actor.Generation is { } generation) WriteGeneration(writer, generation);
        writer.WriteEndObject();
    }

    /// <summary>Writes static inheritance evidence without implying spawn-time calculations.</summary>
    private static void WriteTemplateData(Utf8JsonWriter writer, ActorInspection actor)
    {
        writer.WriteStartArray("templateChain");
        foreach (var hop in actor.TemplateChain)
        {
            writer.WriteStartObject();
            writer.WriteNumber("formId", hop.FormId);
            writer.WriteString("kind", hop.Kind);
            if (hop.EditorId is not null) writer.WriteString("editorId", hop.EditorId);
            if (hop.TemplateFlags is { } flags) writer.WriteString("templateFlags", $"0x{flags:X4}");
            if (hop.Template is { } template) writer.WriteNumber("template", template);
            if (hop.SourcePlugin is not null) writer.WriteString("sourcePlugin", hop.SourcePlugin);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("templateGroups");
        foreach (var group in actor.TemplateGroups)
        {
            writer.WriteStartObject();
            writer.WriteString("group", group.Group.ToString());
            writer.WriteNumber("flag", (ushort)group.Group);
            writer.WriteString("status", group.Status);
            if (group.SourceActor is { } source) writer.WriteNumber("sourceActor", source);
            if (group.SourcePlugin is not null) writer.WriteString("sourcePlugin", group.SourcePlugin);
            if (group.UnresolvedFormId is { } unresolved) writer.WriteNumber("unresolvedFormId", unresolved);
            if (group.Detail is not null) writer.WriteString("detail", group.Detail);
            writer.WriteStartArray("chain");
            foreach (var hop in group.Chain) writer.WriteNumberValue(hop.FormId);
            writer.WriteEndArray();
            writer.WriteStartArray("candidates");
            foreach (var candidate in group.Candidates)
            {
                writer.WriteStartObject();
                writer.WriteNumber("formId", candidate.FormId);
                writer.WriteNumber("level", candidate.Level);
                writer.WriteNumber("count", candidate.Count);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("effectiveStatistics");
        foreach (var stat in actor.EffectiveStatistics)
        {
            writer.WriteStartObject();
            writer.WriteString("key", stat.Key);
            writer.WriteString("value", stat.Value);
            writer.WriteString("provenance", stat.Provenance);
            writer.WriteString("group", stat.Group.ToString());
            if (stat.SourceActor is { } source) writer.WriteNumber("sourceActor", source);
            if (stat.SourcePlugin is not null) writer.WriteString("sourcePlugin", stat.SourcePlugin);
            if (stat.Reference is { } reference) writer.WriteNumber("reference", reference);
            if (stat.Reason is not null) writer.WriteString("reason", stat.Reason);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("calculated");
        foreach (var value in actor.Calculated)
        {
            writer.WriteStartObject();
            writer.WriteString("key", value.Key);
            writer.WriteString("status", value.Status);
            writer.WriteString("reason", value.Reason);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    /// <summary>Writes the seed, assumptions and exact concrete output independently of trace rows.</summary>
    /// <param name="writer">The caller-owned writer positioned within the actor report.</param>
    /// <param name="generation">The same immutable result passed to selected preview/export operations.</param>
    private static void WriteGeneration(Utf8JsonWriter writer, ActorInventoryGeneration generation)
    {
        writer.WriteStartObject("generation");
        writer.WriteString("algorithm", ActorInventoryGeneration.Algorithm);
        writer.WriteNumber("seed", generation.Seed);
        writer.WriteNumber("previewLevel", generation.Level);
        writer.WriteBoolean("complete", generation.Complete);
        writer.WriteStartArray("notices");
        foreach (var notice in generation.Notices) writer.WriteStringValue(notice);
        writer.WriteEndArray();
        writer.WriteStartArray("assumptions");
        foreach (var assumption in ActorInventoryGeneration.Assumptions) writer.WriteStringValue(assumption);
        writer.WriteEndArray();
        writer.WriteStartArray("items");
        foreach (var item in generation.Items)
        {
            writer.WriteStartObject();
            writer.WriteNumber("formId", item.ItemFormId);
            writer.WriteNumber("count", item.Count);
            if (item.OwnerFormId is { } owner) writer.WriteNumber("ownerFormId", owner);
            if (item.GlobalOrRank is { } global) writer.WriteNumber("ownerGlobalOrRank", global);
            if (item.ItemCondition is { } condition && float.IsFinite(condition)) writer.WriteNumber("condition", condition);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
