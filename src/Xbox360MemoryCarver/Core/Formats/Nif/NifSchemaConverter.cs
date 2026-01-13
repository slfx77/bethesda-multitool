// Schema-driven NIF block converter
// Reads type definitions from nif.xml and applies correct endian conversion automatically
// This eliminates manual errors like treating uint fields as ushort

using System.Buffers.Binary;
using System.Globalization;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Schema-driven NIF block converter that uses nif.xml definitions.
///     Automatically determines field types and applies correct byte swapping.
/// </summary>
internal sealed partial class NifSchemaConverter
{
    private const string ArgPlaceholder = "#ARG#";
    private const string StripsFieldName = "Strips";
    private const string TrianglesFieldName = "Triangles";

    private static readonly Logger Log = Logger.Instance;

    // Cache compiled version conditions
    private readonly Dictionary<string, Func<NifVersionContext, bool>> _conditionCache = [];
    private readonly NifSchema _schema;
    private readonly NifVersionContext _versionContext;

    public NifSchemaConverter(NifSchema schema, uint version = 0x14020007, int userVersion = 0, int bsVersion = 34)
    {
        _schema = schema;
        _versionContext = new NifVersionContext
        {
            Version = version,
            UserVersion = (uint)userVersion,
            BsVersion = bsVersion
        };
    }

    /// <summary>
    ///     Converts a block from big-endian to little-endian using schema definitions.
    ///     Returns true if conversion was handled, false if block type is unknown.
    /// </summary>
    public bool TryConvert(byte[] buf, int pos, int size, string blockType, int[] blockRemap)
    {
        var objDef = _schema.GetObject(blockType);
        if (objDef == null)
        {
            Log.Trace($"  [Schema] Unknown block type: {blockType}, using bulk swap");
            return false;
        }

        Log.Trace($"  [Schema] Converting {blockType} ({objDef.AllFields.Count} fields)");

        try
        {
            var end = pos + size;
            var context = new ConversionContext(buf, pos, end, blockRemap, new Dictionary<string, object>(), blockType);
            ConvertFields(context, objDef.AllFields);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"  [Schema] Error converting {blockType}: {ex.Message}");
            return false;
        }
    }

    private void ConvertFields(ConversionContext ctx, IReadOnlyList<NifFieldDef> fields, int depth = 0)
    {
        if (depth > 20)
        {
            Log.Trace("    [Schema] WARNING: Max recursion depth reached, stopping");
            return;
        }

        foreach (var field in fields)
        {
            if (ctx.Position >= ctx.End) break;
            if (!ShouldProcessField(ctx, field, depth)) continue;

            if (depth == 0)
            {
                Log.Trace($"    Converting field {field.Name} at pos {ctx.Position:X}");
            }

            ConvertField(ctx, field, depth);
        }
    }

    private bool ShouldProcessField(ConversionContext ctx, NifFieldDef field, int depth)
    {
        // Check onlyT (type-specific field)
        if (!IsFieldTypeMatch(ctx, field, depth)) return false;

        // Check version constraints
        if (!IsFieldVersionValid(field, depth)) return false;

        // Check runtime conditions
        if (!IsFieldConditionMet(ctx, field, depth)) return false;

        return true;
    }

    private bool IsFieldTypeMatch(ConversionContext ctx, NifFieldDef field, int depth)
    {
        if (string.IsNullOrEmpty(field.OnlyT)) return true;

        if (_schema.Inherits(ctx.BlockType, field.OnlyT)) return true;

        if (depth == 0)
        {
            Log.Trace($"    Skipping {field.Name} (onlyT={field.OnlyT}, block={ctx.BlockType})");
        }
        return false;
    }

    private bool IsFieldVersionValid(NifFieldDef field, int depth)
    {
        if (!IsVersionInRange(field.Since, field.Until))
        {
            if (depth == 0)
            {
                Log.Trace($"    Skipping {field.Name} (version out of range: since={field.Since}, until={field.Until})");
            }
            return false;
        }

        if (!EvaluateVersionCondition(field.VersionCond))
        {
            if (depth == 0 || field.Name == "LOD Level" || field.Name == "Global VB")
            {
                Log.Trace($"    Skipping {field.Name} (vercond failed: {field.VersionCond})");
            }
            return false;
        }

        return true;
    }

    private static bool IsFieldConditionMet(ConversionContext ctx, NifFieldDef field, int depth)
    {
        if (string.IsNullOrEmpty(field.Condition)) return true;

        var condResult = EvaluateCondition(field.Condition, ctx.FieldValues);
        if (condResult) return true;

        if (depth == 0)
        {
            Log.Trace($"    Skipping {field.Name} (cond failed: {field.Condition})");
        }
        return false;
    }

    /// <summary>
    ///     Checks if current NIF version is within the field's since/until range.
    /// </summary>
    private bool IsVersionInRange(string? since, string? until)
    {
        var currentVersion = _versionContext.Version;

        // Parse "since" version
        if (!string.IsNullOrEmpty(since))
        {
            var sinceVersion = ParseVersionString(since);
            if (currentVersion < sinceVersion) return false;
        }

        // Parse "until" version
        if (!string.IsNullOrEmpty(until))
        {
            var untilVersion = ParseVersionString(until);
            if (currentVersion > untilVersion) return false;
        }

        return true;
    }

    /// <summary>
    ///     Parses a version string like "20.2.0.7" or "4.2.2.0" into a uint.
    /// </summary>
    private static uint ParseVersionString(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 4) return 0;

        return (uint)(
            (byte.Parse(parts[0], CultureInfo.InvariantCulture) << 24) |
            (byte.Parse(parts[1], CultureInfo.InvariantCulture) << 16) |
            (byte.Parse(parts[2], CultureInfo.InvariantCulture) << 8) |
            byte.Parse(parts[3], CultureInfo.InvariantCulture));
    }

    private void ConvertField(ConversionContext ctx, NifFieldDef field, int depth = 0)
    {
        // If field has an arg attribute, evaluate it and set #ARG# before processing
        // This is needed for structs that use #ARG# in their field conditions
        var hadPreviousArg = ctx.FieldValues.TryGetValue(ArgPlaceholder, out var previousArg);

        if (field.Arg != null)
        {
            var argValue = EvaluateArgExpression(field.Arg, ctx.FieldValues);
            ctx.FieldValues[ArgPlaceholder] = argValue;
        }

        // If field has a template attribute, save it for use by nested generic structs
        var previousTemplate = ctx.TemplateType;
        if (field.Template != null)
        {
            ctx.TemplateType = ResolveTemplateType(field.Template, ctx.TemplateType);
        }

        try
        {
            ConvertFieldValue(ctx, field, depth);
        }
        finally
        {
            RestoreArgValue(ctx, field, hadPreviousArg, previousArg);
            ctx.TemplateType = previousTemplate;
        }
    }

    private static string ResolveTemplateType(string template, string? currentTemplate)
    {
        // Resolve the template value - it might be #T# itself (propagation) or an actual type
        return template == "#T#" && currentTemplate != null
            ? currentTemplate // Propagate existing #T#
            : template; // Use the new template type directly
    }

    private static void RestoreArgValue(ConversionContext ctx, NifFieldDef field, bool hadPreviousArg, object? previousArg)
    {
        if (hadPreviousArg)
        {
            ctx.FieldValues[ArgPlaceholder] = previousArg!;
        }
        else if (field.Arg != null)
        {
            ctx.FieldValues.Remove(ArgPlaceholder);
        }
    }

    private void ConvertFieldValue(ConversionContext ctx, NifFieldDef field, int depth)
    {
        // Handle arrays
        if (field.Length != null)
        {
            ConvertArrayField(ctx, field, depth);
            return;
        }

        // Single value
        ConvertSingleValue(ctx, field.Type, depth);
        StoreFieldValue(ctx, field);
    }

    private void ConvertArrayField(ConversionContext ctx, NifFieldDef field, int depth)
    {
        var count = EvaluateArrayLength(field.Length!, ctx.FieldValues);
        if (count < 0)
        {
            LogSkippedArray(field, depth, $"length expression '{field.Length}' = {count}");
            return;
        }

        // Handle 2D or jagged arrays
        if (field.Width != null)
        {
            count = ResolveTwoDimensionalArrayCount(ctx, field, count, depth);
            if (count < 0) return;
        }

        if (count > 100000)
        {
            Log.Trace($"    [Schema] WARNING: Array too large ({count}), skipping field {field.Name}");
            return;
        }

        ConvertArrayElements(ctx, field, count, depth);
    }

    private int ResolveTwoDimensionalArrayCount(ConversionContext ctx, NifFieldDef field, int count, int depth)
    {
        var arrayKey = $"#{field.Width}#Array";

        // Check if this is a jagged array
        if (ctx.FieldValues.TryGetValue(arrayKey, out var arrayObj) && arrayObj is int[] widthArray)
        {
            ConvertJaggedArray(ctx, field, count, widthArray, depth);
            return -1; // Signal that we've handled it
        }

        var width = EvaluateArrayLength(field.Width!, ctx.FieldValues);
        if (width < 0)
        {
            LogSkippedArray(field, depth,
                $"width expression '{field.Width}' = {width}, arrayKey='{arrayKey}', found={ctx.FieldValues.ContainsKey(arrayKey)}");
            return -1;
        }

        if (depth == 0 || field.Name == StripsFieldName || field.Name == TrianglesFieldName)
        {
            Log.Trace($"    2D array: {field.Name} = {count} x {width} = {count * width} elements");
        }

        return count * width;
    }

    private void ConvertJaggedArray(ConversionContext ctx, NifFieldDef field, int rowCount, int[] widthArray, int depth)
    {
        if (depth == 0 || field.Name == StripsFieldName || field.Name == TrianglesFieldName)
        {
            Log.Trace($"    Jagged array: {field.Name} = {rowCount} rows with variable widths (total {widthArray.Sum()} elements)");
        }

        for (var row = 0; row < rowCount && row < widthArray.Length && ctx.Position < ctx.End; row++)
        {
            var rowWidth = widthArray[row];
            for (var col = 0; col < rowWidth && ctx.Position < ctx.End; col++)
            {
                ConvertSingleValue(ctx, field.Type, depth);
            }
        }
    }

    private void ConvertArrayElements(ConversionContext ctx, NifFieldDef field, int count, int depth)
    {
        // For arrays that might be used as widths (like "Strip Lengths"), 
        // store individual values so jagged arrays can reference them
        var shouldStoreArrayValues = field.Name.EndsWith(" Lengths", StringComparison.Ordinal) &&
                                     field.Type == "ushort" &&
                                     count > 0 && count <= 100;
        var arrayValues = shouldStoreArrayValues ? new int[count] : null;

        for (var i = 0; i < count && ctx.Position < ctx.End; i++)
        {
            if (arrayValues != null && ctx.Position + 2 <= ctx.End)
            {
                arrayValues[i] = BinaryPrimitives.ReadUInt16BigEndian(ctx.Buffer.AsSpan(ctx.Position, 2));
            }
            ConvertSingleValue(ctx, field.Type, depth);
        }

        if (arrayValues != null)
        {
            ctx.FieldValues[$"#{field.Name}#Array"] = arrayValues;
            Log.Trace($"      Stored array {field.Name} = [{string.Join(", ", arrayValues)}] at depth {depth}");
        }
    }

    private static void LogSkippedArray(NifFieldDef field, int depth, string reason)
    {
        if (depth == 0 || field.Name == StripsFieldName || field.Name == TrianglesFieldName)
        {
            Log.Trace($"    Skipping array {field.Name} ({reason})");
        }
    }

    private static long EvaluateArgExpression(string argExpr, Dictionary<string, object> fieldValues)
    {
        // Handle simple literal values
        if (long.TryParse(argExpr, out var literalValue))
            return literalValue;

        // Handle #ARG# propagation from parent
        if (argExpr == ArgPlaceholder)
        {
            if (fieldValues.TryGetValue(ArgPlaceholder, out var parentArg))
                return Convert.ToInt64(parentArg, CultureInfo.InvariantCulture);
            return 0;
        }

        // Handle field references (e.g., "Vertex Desc #RSH# 44")
        // For now, just handle simple cases
        try
        {
            // Try to evaluate as an expression using the condition evaluator
            // This handles things like "#ARG#", field references, and simple arithmetic
            return NifConditionExpr.EvaluateValue(argExpr, fieldValues);
        }
        catch
        {
            // If expression evaluation fails, try to parse as literal
            return 0;
        }
    }

    private bool EvaluateVersionCondition(string? vercond)
    {
        if (string.IsNullOrEmpty(vercond)) return true;

        // Use cached compiled expression or compile and cache
        if (!_conditionCache.TryGetValue(vercond, out var evaluator))
        {
            evaluator = NifVersionExpr.Compile(vercond);
            _conditionCache[vercond] = evaluator;
        }

        return evaluator(_versionContext);
    }

    private static bool EvaluateCondition(string? condition, Dictionary<string, object> fieldValues)
    {
        if (string.IsNullOrEmpty(condition)) return true;

        // Use the full condition expression evaluator
        return NifConditionExpr.Evaluate(condition, fieldValues);
    }

    private static int EvaluateArrayLength(string lengthExpr, Dictionary<string, object> fieldValues)
    {
        // Try to get value from field context (simple field reference)
        if (fieldValues.TryGetValue(lengthExpr, out var val))
        {
            return val switch
            {
                int i => i,
                uint u => (int)u,
                ushort us => us,
                byte b => b,
                long l => (int)l,
                _ => -1
            };
        }

        // Try to parse as literal
        if (int.TryParse(lengthExpr, CultureInfo.InvariantCulture, out var literal))
            return literal;

        // Try to evaluate as an expression (e.g., "((Data Flags #BITAND# 63) #BITOR# (BS Data Flags #BITAND# 1))")
        try
        {
            var result = NifConditionExpr.EvaluateValue(lengthExpr, fieldValues);
            return (int)result;
        }
        catch
        {
            // Evaluation failed - unknown length
            return -1;
        }
    }

    private void StoreFieldValue(ConversionContext ctx, NifFieldDef field)
    {
        // Store fields that may be needed for conditions or array lengths
        // This includes: Num X, X Count, Has X, Data Flags, BS Data Flags, etc.
        // Also store "Interpolation" which is used as #ARG# for Key struct conditions
        var shouldStore = field.Name.StartsWith("Num ", StringComparison.Ordinal) ||
                          field.Name.EndsWith(" Count", StringComparison.Ordinal) ||
                          field.Name.StartsWith("Has ", StringComparison.Ordinal) ||
                          field.Name.Contains("Flags", StringComparison.Ordinal) ||
                          field.Name.Contains("Type", StringComparison.Ordinal) ||
                          field.Name == "Compressed" ||
                          field.Name == "Interpolation"; // For KeyGroup -> Key #ARG# propagation

        if (!shouldStore) return;

        // Get the size from the schema - this handles enums, bitfields, basic types correctly
        var size = _schema.GetTypeSize(field.Type) ?? 0;

        if (size > 0 && ctx.Position >= size)
        {
            object val = size switch
            {
                1 => ctx.Buffer[ctx.Position - 1],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buffer.AsSpan(ctx.Position - 2)),
                4 => (int)BinaryPrimitives.ReadUInt32LittleEndian(ctx.Buffer.AsSpan(ctx.Position - 4)),
                _ => 0
            };

            // For "Has X" fields (bool), normalize to 0/1
            if (field.Name.StartsWith("Has ", StringComparison.Ordinal) && size == 1)
                val = ctx.Buffer[ctx.Position - 1] != 0 ? 1 : 0;

            ctx.FieldValues[field.Name] = val;

            Log.Trace($"      Stored {field.Name} = {val} (from pos {ctx.Position - size:X})");
        }
    }

    private sealed class ConversionContext
    {
        public ConversionContext(byte[] buffer, int position, int end, int[] blockRemap,
            Dictionary<string, object> fieldValues, string blockType)
        {
            Buffer = buffer;
            Position = position;
            End = end;
            BlockRemap = blockRemap;
            FieldValues = fieldValues;
            BlockType = blockType;
        }

        public byte[] Buffer { get; }
        public int Position { get; set; }
        public int End { get; }
        public int[] BlockRemap { get; }
        public Dictionary<string, object> FieldValues { get; }
        public string BlockType { get; }

        /// <summary>
        ///     Current template type parameter (#T#) for generic structs like KeyGroup&lt;float&gt;.
        ///     This is set when processing a field with a template attribute and propagates
        ///     to nested structs.
        /// </summary>
        public string? TemplateType { get; set; }
    }
}
