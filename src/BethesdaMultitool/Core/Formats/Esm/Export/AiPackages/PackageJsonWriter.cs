using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

namespace BethesdaMultitool.Core.Formats.Esm.Export.AiPackages;

/// <summary>
///     Writes the <c>esm packages -f json</c> document with <see cref="Utf8JsonWriter" />, never the
///     reflection-based <see cref="JsonSerializer" />: the published CLI is trimmed and ships with
///     <c>System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false</c>, under which serializing
///     anonymous or undeclared types throws. The test host does not carry that switch, so only an exe
///     smoke test can catch a regression here.
///     <para>
///         Schema <c>bethesda-multitool/esm-packages</c>, version 1: ONE object carrying the counts, the
///         filters and a <c>packages</c> array. Each package keeps the field names of the earlier
///         (never-working) array form and adds month/day names, the secondary location and target, the
///         raw flag words and the raw CTDA conditions (<c>conditionsRaw</c>). Every field is always
///         present — absent values are explicit nulls — and a non-finite float is written as null beside
///         a <c>…RawBits</c> string, because <see cref="Utf8JsonWriter" /> rejects NaN and infinity.
///     </para>
///     <para>
///         Additive within version 1: a top-level <c>conditionContext</c> (the condition table's game and
///         whether it was assumed, whether quest-variable names were available, whether the input is a
///         memory dump, and the grouping convention) and, per package, <c>conditions</c> (the same CTDA list
///         described by <see cref="ConditionDescriber" /> and written by <see cref="ConditionJsonWriter" />)
///         plus <c>conditionLogic</c> (<see cref="ConditionTextFormatter.FormatLogicSummary" />, or null).
///         <c>conditionsRaw</c> is unchanged, so a consumer can always check a description against the words.
///     </para>
/// </summary>
internal static class PackageJsonWriter
{
    /// <summary>The document's <c>schema</c> identifier.</summary>
    internal const string Schema = "bethesda-multitool/esm-packages";

    /// <summary>The document's <c>schemaVersion</c>. Bump only for a breaking change; additions keep it.</summary>
    internal const int SchemaVersion = 1;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    /// <summary>
    ///     Writes <paramref name="document" /> as one JSON object. Flushes its own writer but neither
    ///     closes nor disposes <paramref name="output" />, and appends no trailing newline.
    /// </summary>
    /// <param name="output">Writable caller-owned stream (stdout or a file).</param>
    /// <param name="document">Counts, filters and the packages to write.</param>
    /// <param name="resolver">
    ///     Name source for FormID-bearing union arms. A name is looked up only when the arm selected by the
    ///     location/target type stores a FormID, so an enum or unused arm never borrows a record's name.
    /// </param>
    /// <param name="conditionContext">
    ///     How the <c>conditions</c> arrays are described. The CLI passes
    ///     <see cref="ConditionDisplayContext.From" /> over the loaded records, so <c>GetQuestVariable</c>
    ///     names its variable. Null uses <see cref="ConditionDisplayContext.ForResolver" /> over
    ///     <paramref name="resolver" /> and the document's game: every quest variable then prints as
    ///     <c>var N</c> with a null <c>variableName</c> (never a guess).
    /// </param>
    /// <param name="isMemoryDumpInput">
    ///     True when the source is a memory dump. Recorded in <c>conditionContext</c> with the partial-capture
    ///     absence note, because a package captured with no conditions does not prove the build had none.
    /// </param>
    internal static void Write(
        Stream output,
        PackageJsonDocument document,
        FormIdResolver resolver,
        ConditionDisplayContext? conditionContext = null,
        bool isMemoryDumpInput = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(resolver);

        conditionContext ??= ConditionDisplayContext.ForResolver(resolver, document.Game);

        using var writer = new Utf8JsonWriter(output, WriterOptions);
        writer.WriteStartObject();
        writer.WriteString("schema", Schema);
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("toolVersion", document.ToolVersion);
        writer.WriteString("source", document.Source);
        writer.WriteString("game", document.Game.ToString());
        writer.WriteNumber("totalPackages", document.TotalPackages);
        writer.WriteNumber("matchedPackages", document.MatchedPackages);
        writer.WriteNumber("shownPackages", document.Packages.Count);
        writer.WriteBoolean("truncated", document.MatchedPackages > document.Packages.Count);

        writer.WriteStartObject("filters");
        WriteStringOrNull(writer, "type", document.TypeFilter);
        WriteStringOrNull(writer, "npc", document.NpcFilter);
        writer.WriteNumber("limit", document.Limit);
        writer.WriteEndObject();

        WriteConditionContext(writer, conditionContext, isMemoryDumpInput);

        writer.WriteStartArray("packages");
        foreach (var package in document.Packages)
        {
            WritePackage(writer, package, resolver, conditionContext);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    ///     Says how every package's <c>conditions</c> were described: which game's condition table named the
    ///     functions (and whether that game was assumed rather than detected), whether quest-variable names
    ///     were available at all, and whether the input is a memory dump, whose condition lists may be partial.
    /// </summary>
    private static void WriteConditionContext(
        Utf8JsonWriter writer,
        ConditionDisplayContext context,
        bool isMemoryDumpInput)
    {
        writer.WriteStartObject("conditionContext");
        writer.WriteString("game", context.Game.ToString());
        writer.WriteBoolean("gameAssumed", context.GameAssumed);
        writer.WriteBoolean("questVariableSource", context.HasQuestVariableSource);
        writer.WriteBoolean("memoryDumpInput", isMemoryDumpInput);
        WriteStringOrNull(writer, "absenceNote",
            isMemoryDumpInput ? ScriptSourceProvenance.PartialDumpAbsenceWording : null);
        writer.WriteString("grouping", ConditionTextFormatter.GroupingConventionNote);
        writer.WriteEndObject();
    }

    private static void WritePackage(
        Utf8JsonWriter writer,
        PackageRecord package,
        FormIdResolver resolver,
        ConditionDisplayContext conditionContext)
    {
        writer.WriteStartObject();
        writer.WriteString("formId", FormatHex(package.FormId));
        WriteStringOrNull(writer, "editorId", package.EditorId);
        writer.WriteString("type", package.TypeName);
        WriteNumberOrNull(writer, "typeCode", package.Data?.Type);

        WriteSchedule(writer, "schedule", package.Schedule);
        WriteLocation(writer, "location", package.Location, resolver);
        WriteLocation(writer, "location2", package.Location2, resolver);
        WriteTarget(writer, "target", package.Target, resolver);
        WriteTarget(writer, "target2", package.Target2, resolver);

        var data = package.Data;
        WriteStringOrNull(writer, "generalFlags", data != null
            ? FlagRegistry.DecodeFlagNames(data.GeneralFlags, FlagRegistry.PackageGeneralFlags)
            : null);
        WriteNumberOrNull(writer, "generalFlagsRaw", data?.GeneralFlags);
        WriteStringOrNull(writer, "foBehaviorFlags", data is { FalloutBehaviorFlags: not 0 }
            ? FlagRegistry.DecodeFlagNames(data.FalloutBehaviorFlags, FlagRegistry.PackageFOBehaviorFlags)
            : null);
        WriteNumberOrNull(writer, "foBehaviorFlagsRaw", data?.FalloutBehaviorFlags);
        WriteStringOrNull(writer, "typeSpecificFlags", data is { TypeSpecificFlags: not 0 }
            ? FlagRegistry.DecodeFlagNames(data.TypeSpecificFlags, FlagRegistry.PackageTypeSpecificFlags)
            : null);
        WriteNumberOrNull(writer, "typeSpecificFlagsRaw", data?.TypeSpecificFlags);
        writer.WriteBoolean("isRepeatable", package.IsRepeatable);
        writer.WriteBoolean("startingLocationLinkedRef", package.IsStartingLocationLinkedRef);

        WriteConditionsRaw(writer, package.Conditions);

        // Additive: the same list, described. conditionsRaw above is kept verbatim beside it.
        var described = ConditionDescriber.DescribeAll(package.Conditions, conditionContext);
        ConditionJsonWriter.Write(writer, "conditions", described);
        WriteStringOrNull(writer, "conditionLogic", ConditionTextFormatter.FormatLogicSummary(described));
        writer.WriteEndObject();
    }

    private static void WriteSchedule(Utf8JsonWriter writer, string propertyName, PackageSchedule? schedule)
    {
        if (schedule == null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteStartObject(propertyName);
        writer.WriteString("summary", schedule.Summary);
        writer.WriteNumber("month", schedule.Month);
        writer.WriteString("monthName", schedule.MonthName);
        writer.WriteNumber("dayOfWeek", schedule.DayOfWeek);
        writer.WriteString("dayOfWeekName", schedule.DayOfWeekName);
        writer.WriteNumber("date", schedule.Date);
        writer.WriteNumber("time", schedule.Time);
        writer.WriteNumber("durationHours", schedule.Duration);
        writer.WriteEndObject();
    }

    private static void WriteLocation(
        Utf8JsonWriter writer,
        string propertyName,
        PackageLocation? location,
        FormIdResolver resolver)
    {
        if (location == null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        var unionIsFormId = PackageReferenceIntegrity.LocationTypeIsFormId(location.Type);
        writer.WriteStartObject(propertyName);
        writer.WriteNumber("type", location.Type);
        writer.WriteString("union", FormatHex(location.Union));
        writer.WriteBoolean("unionIsFormId", unionIsFormId);
        WriteStringOrNull(writer, "unionEditorId",
            unionIsFormId ? resolver.GetBestNameWithRefChain(location.Union) : null);
        writer.WriteNumber("radius", location.Radius);
        writer.WriteEndObject();
    }

    private static void WriteTarget(
        Utf8JsonWriter writer,
        string propertyName,
        PackageTarget? target,
        FormIdResolver resolver)
    {
        if (target == null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        var unionIsFormId = PackageReferenceIntegrity.TargetTypeIsFormId(target.Type);
        writer.WriteStartObject(propertyName);
        writer.WriteNumber("type", target.Type);
        writer.WriteString("typeName", target.TypeName);
        writer.WriteString("formIdOrType", FormatHex(target.FormIdOrType));
        writer.WriteBoolean("unionIsFormId", unionIsFormId);
        WriteStringOrNull(writer, "editorId",
            unionIsFormId ? resolver.GetBestNameWithRefChain(target.FormIdOrType) : null);
        writer.WriteNumber("countDistance", target.CountDistance);
        WriteFloatWithRawBits(writer, "acquireRadius", target.AcquireRadius);
        writer.WriteEndObject();
    }

    /// <summary>
    ///     The CTDA conditions exactly as stored, in source order. Described conditions (function names,
    ///     operators, resolved operands) belong in a separate <c>conditions</c> array; these raw words stay
    ///     alongside it so a consumer can always check the description against the bytes.
    /// </summary>
    private static void WriteConditionsRaw(Utf8JsonWriter writer, IReadOnlyList<DialogueCondition> conditions)
    {
        writer.WriteStartArray("conditionsRaw");
        for (var i = 0; i < conditions.Count; i++)
        {
            var condition = conditions[i];
            writer.WriteStartObject();
            writer.WriteNumber("index", i + 1);
            writer.WriteNumber("functionIndex", condition.FunctionIndex);
            writer.WriteNumber("typeRaw", condition.Type);
            writer.WriteString("comparisonRawBits",
                FormatHex(BitConverter.SingleToUInt32Bits(condition.ComparisonValue)));
            writer.WriteString("parameter1", FormatHex(condition.Parameter1));
            writer.WriteString("parameter2", FormatHex(condition.Parameter2));
            writer.WriteNumber("runOn", condition.RunOn);
            writer.WriteString("reference", FormatHex(condition.Reference));
            WriteNumberOrNull(writer, "parameter3", condition.Parameter3);
            WriteStringOrNull(writer, "parameter1String", condition.Parameter1String);
            WriteStringOrNull(writer, "parameter2String", condition.Parameter2String);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    ///     Writes a float as a number, or as null when it is NaN or infinite, and ALWAYS writes its exact
    ///     IEEE-754 bits beside it as <c>&lt;name&gt;RawBits</c>, so the field set never depends on the value.
    /// </summary>
    private static void WriteFloatWithRawBits(Utf8JsonWriter writer, string propertyName, float value)
    {
        if (float.IsFinite(value))
        {
            writer.WriteNumber(propertyName, value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }

        writer.WriteString(propertyName + "RawBits", FormatHex(BitConverter.SingleToUInt32Bits(value)));
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value != null)
        {
            writer.WriteString(propertyName, value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteNumberOrNull(Utf8JsonWriter writer, string propertyName, long? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static string FormatHex(uint value)
    {
        return $"0x{value:X8}";
    }
}
