// Schema-driven NIF block converter
// Reads type definitions from nif.xml and applies correct endian conversion automatically
// This eliminates manual errors like treating uint fields as ushort

using System.Buffers.Binary;
using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Nif.Expressions;
using FalloutXbox360Utils.Core.Formats.Nif.Schema;
using static FalloutXbox360Utils.Core.Formats.Nif.Conversion.NifEndianUtils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Conversion;

/// <summary>
///     Schema-driven NIF block converter that uses nif.xml definitions.
///     Automatically determines field types and applies correct byte swapping.
/// </summary>
internal sealed class NifSchemaConverter
{
    private const string ArgPlaceholder = "#ARG#";
    private const string StripsFieldName = "Strips";
    private const string TrianglesFieldName = "Triangles";

    private static readonly Logger Log = Logger.Instance;

    private readonly NifSchema _schema;
    private readonly NifVersionContext _versionContext;

    public NifSchemaConverter(NifSchema schema, uint version = 0x14020007, int userVersion = 0, int bsVersion = 34)
    {
        _schema = schema;
        _versionContext = new NifVersionContext
            { Version = version, UserVersion = (uint)userVersion, BsVersion = bsVersion };
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

    #region Field Processing

    private void ConvertFields(ConversionContext ctx, IReadOnlyList<NifFieldDef> fields, int depth = 0)
    {
        if (depth > 20)
        {
            Log.Trace("    [Schema] WARNING: Max recursion depth reached, stopping");
            return;
        }

        foreach (var field in fields)
        {
            if (ctx.Position >= ctx.End)
            {
                break;
            }

            if (!ShouldProcessField(ctx, field, depth))
            {
                continue;
            }

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
        if (!IsFieldTypeMatch(ctx, field, depth))
        {
            return false;
        }

        // Check version constraints
        if (!IsFieldVersionValid(field, depth))
        {
            return false;
        }

        // Check runtime conditions
        if (!IsFieldConditionMet(ctx, field, depth))
        {
            return false;
        }

        return true;
    }

    private bool IsFieldTypeMatch(ConversionContext ctx, NifFieldDef field, int depth)
    {
        if (string.IsNullOrEmpty(field.OnlyT))
        {
            return true;
        }

        if (_schema.Inherits(ctx.BlockType, field.OnlyT))
        {
            return true;
        }

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
                Log.Trace(
                    $"    Skipping {field.Name} (version out of range: since={field.Since}, until={field.Until})");
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
        if (string.IsNullOrEmpty(field.Condition))
        {
            return true;
        }

        var condResult = EvaluateCondition(field.Condition, ctx.FieldValues);
        if (condResult)
        {
            return true;
        }

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
            if (currentVersion < sinceVersion)
            {
                return false;
            }
        }

        // Parse "until" version
        if (!string.IsNullOrEmpty(until))
        {
            var untilVersion = ParseVersionString(until);
            if (currentVersion > untilVersion)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Parses a version string like "20.2.0.7" or "4.2.2.0" into a uint.
    /// </summary>
    private static uint ParseVersionString(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 4)
        {
            return 0;
        }

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

    private static void RestoreArgValue(ConversionContext ctx, NifFieldDef field, bool hadPreviousArg,
        object? previousArg)
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
            if (count < 0)
            {
                return;
            }
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
            Log.Trace(
                $"    Jagged array: {field.Name} = {rowCount} rows with variable widths (total {widthArray.Sum()} elements)");
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
                                     count is > 0 and <= 100;
        var arrayValues = shouldStoreArrayValues ? new int[count] : null;

        for (var i = 0; i < count && ctx.Position < ctx.End; i++)
        {
            if (arrayValues is not null && ctx.Position + 2 <= ctx.End)
            {
                arrayValues[i] = BinaryPrimitives.ReadUInt16BigEndian(ctx.Buffer.AsSpan(ctx.Position, 2));
            }

            ConvertSingleValue(ctx, field.Type, depth);
        }

        if (arrayValues is not null)
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
        {
            return literalValue;
        }

        // Handle #ARG# propagation from parent
        if (argExpr == ArgPlaceholder)
        {
            if (fieldValues.TryGetValue(ArgPlaceholder, out var parentArg))
            {
                return Convert.ToInt64(parentArg, CultureInfo.InvariantCulture);
            }

            return 0;
        }

        // Handle field references and simple expressions (e.g., "Vertex Desc #RSH# 44")
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
        if (string.IsNullOrEmpty(vercond))
        {
            return true;
        }

        // NifVersionExpr.Compile is globally cached, no need for per-instance cache
        var evaluator = NifVersionExpr.Compile(vercond);
        return evaluator(_versionContext);
    }

    private static bool EvaluateCondition(string? condition, Dictionary<string, object> fieldValues)
    {
        if (string.IsNullOrEmpty(condition))
        {
            return true;
        }

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
        {
            return literal;
        }

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

        if (!shouldStore)
        {
            return;
        }

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
            {
                val = ctx.Buffer[ctx.Position - 1] != 0 ? 1 : 0;
            }

            ctx.FieldValues[field.Name] = val;

            Log.Trace($"      Stored {field.Name} = {val} (from pos {ctx.Position - size:X})");
        }
    }

    #endregion

    #region Type Conversion

    private void ConvertSingleValue(ConversionContext ctx, string typeName, int depth = 0)
    {
        var resolvedTypeName = ResolveTypeName(ctx, typeName);
        if (resolvedTypeName == null)
        {
            return;
        }

        // Handle special string types
        if (TryConvertStringType(ctx, resolvedTypeName))
        {
            return;
        }

        // Handle basic types
        if (TryConvertBasicType(ctx, resolvedTypeName))
        {
            return;
        }

        // Handle enums
        if (TryConvertEnumType(ctx, resolvedTypeName))
        {
            return;
        }

        // Handle structs
        if (TryConvertStructType(ctx, resolvedTypeName, depth))
        {
            return;
        }

        // Unknown type - try bulk swap based on size
        ConvertUnknownType(ctx, resolvedTypeName);
    }

    private static string? ResolveTypeName(ConversionContext ctx, string typeName)
    {
        if (typeName != "#T#")
        {
            return typeName;
        }

        if (ctx.TemplateType != null)
        {
            return ctx.TemplateType;
        }

        Log.Trace("    [Schema] WARNING: #T# used without template context, cannot resolve");
        return null;
    }

    private static bool TryConvertStringType(ConversionContext ctx, string typeName)
    {
        switch (typeName)
        {
            case "SizedString":
                ConvertSizedString(ctx);
                return true;
            case "SizedString16":
                ConvertSizedString16(ctx);
                return true;
            default:
                return false;
        }
    }

    private bool TryConvertBasicType(ConversionContext ctx, string typeName)
    {
        if (!_schema.BasicTypes.TryGetValue(typeName, out var basic))
        {
            return false;
        }

        ConvertBasicType(ctx, basic);
        return true;
    }

    private bool TryConvertEnumType(ConversionContext ctx, string typeName)
    {
        if (!_schema.Enums.TryGetValue(typeName, out var enumDef))
        {
            return false;
        }

        if (_schema.BasicTypes.TryGetValue(enumDef.Storage, out var storageType))
        {
            ConvertBasicType(ctx, storageType);
        }

        return true;
    }

    private bool TryConvertStructType(ConversionContext ctx, string typeName, int depth)
    {
        if (!_schema.Structs.TryGetValue(typeName, out var structDef))
        {
            return false;
        }

        // Some structs with fixed size (like HavokFilter) are packed bitfields that should
        // be swapped as a single unit rather than field-by-field.
        if (TryBulkSwapFixedSizeStruct(ctx, structDef))
        {
            return true;
        }

        // Clear field values for fresh struct instance
        foreach (var field in structDef.Fields)
        {
            ctx.FieldValues.Remove(field.Name);
        }

        ConvertFields(ctx, structDef.Fields, depth + 1);
        return true;
    }

    private bool TryBulkSwapFixedSizeStruct(ConversionContext ctx, NifStructDef structDef)
    {
        if (structDef.FixedSize is not (2 or 4 or 8))
        {
            return false;
        }

        // Only bulk-swap structs where all fields are single bytes (packed uint32/uint64 values
        // like UDecVector4, ByteColor4). Structs with multi-byte sub-fields (e.g., BodyPartList
        // = 2 × ushort, HalfTexCoord = 2 × hfloat, HavokFilter = byte+byte+ushort) need
        // per-field endian conversion — bulk swap cross-contaminates adjacent fields.
        foreach (var field in structDef.Fields)
        {
            var fieldSize = _schema.GetTypeSize(field.Type) ?? 0;
            if (fieldSize > 1)
            {
                return false;
            }
        }

        if (structDef.FixedSize == 2)
        {
            SwapUInt16InPlace(ctx.Buffer, ctx.Position);
        }
        else if (structDef.FixedSize == 4)
        {
            SwapUInt32InPlace(ctx.Buffer, ctx.Position);
        }
        else if (structDef.FixedSize == 8)
        {
            SwapUInt64InPlace(ctx.Buffer, ctx.Position);
        }

        ctx.Position += structDef.FixedSize.Value;
        return true;
    }

    private void ConvertUnknownType(ConversionContext ctx, string typeName)
    {
        var size = _schema.GetTypeSize(typeName);
        if (!size.HasValue || size.Value <= 0)
        {
            Log.Trace($"    [Schema] WARNING: Unknown type '{typeName}' with no size, cannot advance position");
            return;
        }

        // Bulk swap based on size
        if (size.Value == 2)
        {
            SwapUInt16InPlace(ctx.Buffer, ctx.Position);
        }
        else if (size.Value == 4)
        {
            SwapUInt32InPlace(ctx.Buffer, ctx.Position);
        }
        else if (size.Value == 8)
        {
            SwapUInt64InPlace(ctx.Buffer, ctx.Position);
        }

        ctx.Position += size.Value;
    }

    /// <summary>
    ///     Converts a SizedString (uint length + chars) - swaps the length field.
    /// </summary>
    private static void ConvertSizedString(ConversionContext ctx)
    {
        if (ctx.Position + 4 > ctx.End)
        {
            return;
        }

        // Swap the length (uint, 4 bytes)
        SwapUInt32InPlace(ctx.Buffer, ctx.Position);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Buffer.AsSpan(ctx.Position, 4));
        ctx.Position += 4;

        // Skip the string data (chars don't need swapping)
        if (length is > 0 and < 0x10000) // Sanity check
        {
            ctx.Position += (int)length;
        }
    }

    /// <summary>
    ///     Converts a SizedString16 (ushort length + chars) - swaps the length field.
    /// </summary>
    private static void ConvertSizedString16(ConversionContext ctx)
    {
        if (ctx.Position + 2 > ctx.End)
        {
            return;
        }

        // Swap the length (ushort, 2 bytes)
        SwapUInt16InPlace(ctx.Buffer, ctx.Position);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buffer.AsSpan(ctx.Position, 2));
        ctx.Position += 2;

        // Skip the string data (chars don't need swapping)
        if (length > 0)
        {
            ctx.Position += length;
        }
    }

    private static void ConvertBasicType(ConversionContext ctx, NifBasicType basic)
    {
        if (ctx.Position + basic.Size > ctx.End)
        {
            return;
        }

        var pos = ctx.Position; // Save position before modifying

        switch (basic.Size)
        {
            case 1:
                // No swap needed for single bytes
                ctx.Position += 1;
                break;

            case 2:
                SwapUInt16InPlace(ctx.Buffer, pos);
                ctx.Position += 2;
                break;

            case 4:
                SwapUInt32InPlace(ctx.Buffer, pos);
                // Handle block references (Ref, Ptr) that need remapping
                if (basic.IsGeneric)
                {
                    RemapBlockRef(ctx.Buffer, pos, ctx.BlockRemap);
                }

                ctx.Position += 4;
                break;

            case 8:
                SwapUInt64InPlace(ctx.Buffer, pos);
                ctx.Position += 8;
                break;
        }
    }

    private static void RemapBlockRef(byte[] buf, int pos, int[] blockRemap)
    {
        var idx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos, 4));
        if (idx >= 0 && idx < blockRemap.Length)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), blockRemap[idx]);
        }
    }

    #endregion

    #region Types

    private sealed class ConversionContext(
        byte[] buffer,
        int position,
        int end,
        int[] blockRemap,
        Dictionary<string, object> fieldValues,
        string blockType)
    {
        public byte[] Buffer { get; } = buffer;
        public int Position { get; set; } = position;
        public int End { get; } = end;
        public int[] BlockRemap { get; } = blockRemap;
        public Dictionary<string, object> FieldValues { get; } = fieldValues;
        public string BlockType { get; } = blockType;

        /// <summary>
        ///     Current template type parameter (#T#) for generic structs like KeyGroup&lt;float&gt;.
        ///     This is set when processing a field with a template attribute and propagates
        ///     to nested structs.
        /// </summary>
        public string? TemplateType { get; set; }
    }

    #endregion
}
