using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     The two plain-text tables the Shadowkey zone binaries index — <c>entities.txt</c> and the
///     <c>models.txt</c> family — plus the pure lookup that joins them to a <c>.ent</c> placement.
///     Original RE 2026-09-05 from the retail bytes.
///     <para>
///         Both are ASCII with CRLF line endings and are read Latin-1 so a stray high byte can
///         never throw. Fields are separated by RUNS of whitespace, not single spaces:
///         <c>entities.txt</c> has 21 tab-separated rows and 2 double-space rows among its 735, so
///         a single-space split would mis-read them. Blank lines are separators — 29 of them — and
///         are skipped.
///     </para>
///     <list type="bullet">
///         <item>
///             <c>entities.txt</c> (735 data rows): <c>id modelIndex kind name</c>. Ids are unique
///             and reach 6023 but are NOT sorted — 4208 precedes 4202 — so nothing here may assume
///             ordering.
///         </item>
///         <item>
///             <c>models.txt</c> (237 rows, indices 0..236) and each
///             <c>&lt;zone&gt;_models.txt</c> (236 rows, 0..235): <c>index flag w h file.bin</c>.
///             A zone list is the same index space with every model the zone does not load blanked
///             to <c>0 0 0 0 NULL.bin</c> — a residency mask, not a table of its own.
///         </item>
///     </list>
///     <para>
///         <b>The chain the binaries rely on</b> is <c>.ent</c> entity id → <c>entities.txt</c> row
///         → model index → the zone's model list, and it holds completely on retail: all 8,258
///         placements across 21 zones resolve to a row, and every one of those rows lands on a slot
///         that zone actually loads. <see cref="Resolve" /> walks it without I/O and reports a miss
///         as an absent field rather than an exception, because a miss would be content drift to
///         surface (the install already has some: <c>entities.txt</c> points id 4012 at a script
///         that does not exist), not a malformed file.
///     </para>
///     <para>
///         The sibling <c>&lt;zone&gt;_sprites.txt</c> and <c>&lt;zone&gt;_sounds.txt</c> manifests
///         are deliberately absent from this reader: no field of any of the six binary kinds
///         indexes them. Every small-integer lane was ranged against both lists and the only
///         candidate — <c>.sur</c> byte +7 — is bounded by the zone's <c>.ztx</c> texture count
///         instead, exactly. Sprites and sounds are reached from the scripts.
///     </para>
/// </summary>
internal static class ShadowkeyTextTables
{
    private const int EntityFieldCount = 4;
    private const int ModelFieldCount = 5;

    /// <summary>Parses <c>entities.txt</c> from its raw bytes (decoded Latin-1).</summary>
    public static ShadowkeyEntityTable ParseEntities(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return ParseEntities(Encoding.Latin1.GetString(bytes), name);
    }

    /// <summary>
    ///     Parses <c>entities.txt</c>. Throws <see cref="InvalidDataException" /> naming
    ///     <paramref name="name" /> and the 1-based line when a row does not have its four
    ///     whitespace-separated fields, when a numeric field will not parse, or when an id repeats.
    /// </summary>
    public static ShadowkeyEntityTable ParseEntities(string text, string name)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);

        var entities = new List<ShadowkeyEntityDef>();
        var seen = new HashSet<uint>();
        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            var fields = SplitFields(line);
            if (fields.Length == 0)
            {
                continue;
            }

            if (fields.Length != EntityFieldCount)
            {
                throw new InvalidDataException(
                    $"'{name}' line {lineNumber}: an entities row needs {EntityFieldCount} whitespace-separated fields (id modelIndex kind name) but has {fields.Length}.");
            }

            var id = ParseUInt(fields[0], name, lineNumber, "id");
            var modelIndex = ParseInt(fields[1], name, lineNumber, "model index");
            var kind = ParseInt(fields[2], name, lineNumber, "kind");
            if (!seen.Add(id))
            {
                throw new InvalidDataException(
                    $"'{name}' line {lineNumber}: entity id {id} appears twice; ids are the table's identity.");
            }

            entities.Add(new ShadowkeyEntityDef(id, modelIndex, kind, fields[3]));
        }

        return new ShadowkeyEntityTable(entities);
    }

    /// <summary>Parses a models table from its raw bytes (decoded Latin-1).</summary>
    public static ShadowkeyModelTable ParseModels(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return ParseModels(Encoding.Latin1.GetString(bytes), name);
    }

    /// <summary>
    ///     Parses <c>models.txt</c> or a <c>&lt;zone&gt;_models.txt</c>. Throws
    ///     <see cref="InvalidDataException" /> naming <paramref name="name" /> and the 1-based line
    ///     when a row does not have its five fields, when a numeric field will not parse, or when
    ///     the indices are not contiguous from zero — the index IS the slot, so a gap would silently
    ///     shift every model behind it.
    /// </summary>
    public static ShadowkeyModelTable ParseModels(string text, string name)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);

        var models = new List<ShadowkeyModelDef>();
        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            var fields = SplitFields(line);
            if (fields.Length == 0)
            {
                continue;
            }

            if (fields.Length != ModelFieldCount)
            {
                throw new InvalidDataException(
                    $"'{name}' line {lineNumber}: a models row needs {ModelFieldCount} whitespace-separated fields (index flag w h file) but has {fields.Length}.");
            }

            var index = ParseInt(fields[0], name, lineNumber, "index");
            if (index != models.Count)
            {
                throw new InvalidDataException(
                    $"'{name}' line {lineNumber}: model index {index} breaks the run — row {models.Count} was expected, and the index is the slot.");
            }

            models.Add(new ShadowkeyModelDef(
                index,
                ParseInt(fields[1], name, lineNumber, "flag"),
                ParseInt(fields[2], name, lineNumber, "width"),
                ParseInt(fields[3], name, lineNumber, "height"),
                fields[4]));
        }

        return new ShadowkeyModelTable(models);
    }

    /// <summary>
    ///     Walks a placement's chain — entity id → <c>entities.txt</c> → model index →
    ///     <paramref name="models" /> — with no I/O and no throwing. Pass the zone's own
    ///     <c>&lt;zone&gt;_models.txt</c> to learn whether the zone loads the model
    ///     (<see cref="ShadowkeyModelResolution.IsResident" />), or the global <c>models.txt</c> to
    ///     learn only which mesh the entity names.
    /// </summary>
    public static ShadowkeyModelResolution Resolve(
        uint entityId,
        ShadowkeyEntityTable entities,
        ShadowkeyModelTable? models)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var entity = entities.Find(entityId);
        var model = entity is null ? null : models?.Find(entity.ModelIndex);
        return new ShadowkeyModelResolution(entityId, entity, model);
    }

    private static string[] SplitFields(string line)
    {
        return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    private static int ParseInt(string field, string name, int lineNumber, string what)
    {
        if (!int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidDataException(
                $"'{name}' line {lineNumber}: the {what} field reads '{field}', not a number.");
        }

        return value;
    }

    private static uint ParseUInt(string field, string name, int lineNumber, string what)
    {
        if (!uint.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidDataException(
                $"'{name}' line {lineNumber}: the {what} field reads '{field}', not a non-negative number.");
        }

        return value;
    }
}
