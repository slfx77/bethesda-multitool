// Schema-driven NIF block converter
// Reads type definitions from nif.xml and applies correct endian conversion automatically
// This eliminates manual errors like treating uint fields as ushort

using System.Buffers.Binary;
using static Xbox360MemoryCarver.Core.Formats.Nif.NifEndianUtils;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Schema-driven NIF block converter that uses nif.xml definitions.
///     Automatically determines field types and applies correct byte swapping.
/// </summary>
internal sealed class NifSchemaConverter
{
    private readonly NifSchema _schema;
    private readonly NifVersionContext _versionContext;
    private readonly bool _verbose;

    // Cache compiled version conditions
    private readonly Dictionary<string, Func<NifVersionContext, bool>> _conditionCache = [];

    public NifSchemaConverter(NifSchema schema, uint version = 0x14020007, int userVersion = 0, int bsVersion = 34, bool verbose = false)
    {
        _schema = schema;
        _versionContext = new NifVersionContext
        {
            Version = version,
            UserVersion = (uint)userVersion,
            BsVersion = bsVersion
        };
        _verbose = verbose;
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
            if (_verbose)
                Console.WriteLine($"  [Schema] Unknown block type: {blockType}, using bulk swap");
            return false;
        }

        if (_verbose)
            Console.WriteLine($"  [Schema] Converting {blockType} ({objDef.AllFields.Count} fields)");

        try
        {
            var end = pos + size;
            var context = new ConversionContext(buf, pos, end, blockRemap, new Dictionary<string, object>(), blockType);
            ConvertFields(context, objDef.AllFields);
            return true;
        }
        catch (Exception ex)
        {
            if (_verbose)
                Console.WriteLine($"  [Schema] Error converting {blockType}: {ex.Message}");
            return false;
        }
    }

    private void ConvertFields(ConversionContext ctx, IReadOnlyList<NifFieldDef> fields, int depth = 0)
    {
        if (depth > 20)
        {
            if (_verbose)
                Console.WriteLine($"    [Schema] WARNING: Max recursion depth reached, stopping");
            return;
        }

        foreach (var field in fields)
        {
            if (ctx.Position >= ctx.End) break;

            // Check onlyT (type-specific field)
            if (!string.IsNullOrEmpty(field.OnlyT) && !_schema.Inherits(ctx.BlockType, field.OnlyT))
            {
                if (_verbose && depth == 0)
                    Console.WriteLine($"    Skipping {field.Name} (onlyT={field.OnlyT}, block={ctx.BlockType})");
                continue;
            }

            // Check since/until version range
            if (!IsVersionInRange(field.Since, field.Until))
            {
                if (_verbose && depth == 0)
                    Console.WriteLine($"    Skipping {field.Name} (version out of range: since={field.Since}, until={field.Until})");
                continue;
            }

            // Check version conditions (vercond)
            if (!EvaluateVersionCondition(field.VersionCond))
            {
                if (_verbose && (depth == 0 || field.Name == "LOD Level" || field.Name == "Global VB"))
                    Console.WriteLine($"    Skipping {field.Name} (vercond failed: {field.VersionCond})");
                continue;
            }

            // Check runtime conditions (requires field values from context)
            if (!string.IsNullOrEmpty(field.Condition))
            {
                var condResult = EvaluateCondition(field.Condition, ctx.FieldValues);
                if (!condResult)
                {
                    if (_verbose && depth == 0)
                        Console.WriteLine($"    Skipping {field.Name} (cond failed: {field.Condition})");
                    continue;
                }
            }

            if (_verbose && depth == 0)
                Console.WriteLine($"    Converting field {field.Name} at pos {ctx.Position:X}");

            // Convert the field
            ConvertField(ctx, field, depth);
        }
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
            (byte.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) << 24) |
            (byte.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) << 16) |
            (byte.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) << 8) |
            byte.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
    }

    private void ConvertField(ConversionContext ctx, NifFieldDef field, int depth = 0)
    {
        var startPos = ctx.Position;

        // If field has an arg attribute, evaluate it and set #ARG# before processing
        // This is needed for structs that use #ARG# in their field conditions
        object? previousArg = null;
        var hadPreviousArg = ctx.FieldValues.TryGetValue("#ARG#", out previousArg);

        if (field.Arg != null)
        {
            var argValue = EvaluateArgExpression(field.Arg, ctx.FieldValues);
            ctx.FieldValues["#ARG#"] = argValue;
        }

        try
        {
            // Handle arrays
            if (field.Length != null)
            {
                var count = EvaluateArrayLength(field.Length, ctx.FieldValues);
                if (count < 0)
                {
                    if (_verbose && (depth == 0 || field.Name == "Strips" || field.Name == "Triangles"))
                        Console.WriteLine($"    Skipping array {field.Name} (length expression '{field.Length}' = {count})");
                    return; // Invalid or conditional array
                }

                // Handle 2D arrays with width attribute (e.g., UV Sets: length is num UV sets, width is num vertices)
                if (field.Width != null)
                {
                    // First check if this is a jagged array (e.g., "Strip Lengths")
                    // In this case, width is stored as an array in a special key
                    var arrayKey = $"#{field.Width}#Array";
                    if (ctx.FieldValues.TryGetValue(arrayKey, out var arrayObj) && arrayObj is int[] widthArray)
                    {
                        // Jagged array: each row has different width
                        if (_verbose && (depth == 0 || field.Name == "Strips" || field.Name == "Triangles"))
                            Console.WriteLine($"    Jagged array: {field.Name} = {count} rows with variable widths (total {widthArray.Sum()} elements) using key '{arrayKey}'");
                        
                        for (var row = 0; row < count && row < widthArray.Length && ctx.Position < ctx.End; row++)
                        {
                            var rowWidth = widthArray[row];
                            for (var col = 0; col < rowWidth && ctx.Position < ctx.End; col++)
                                ConvertSingleValue(ctx, field.Type, field.Template, depth);
                        }
                        return;
                    }
                    
                    var width = EvaluateArrayLength(field.Width, ctx.FieldValues);
                    if (width < 0)
                    {
                        if (_verbose && (depth == 0 || field.Name == "Strips" || field.Name == "Triangles"))
                            Console.WriteLine($"    Skipping array {field.Name} (width expression '{field.Width}' = {width}, arrayKey='{arrayKey}', found={ctx.FieldValues.ContainsKey(arrayKey)})");
                        return; // Invalid width
                    }
                    if (_verbose && (depth == 0 || field.Name == "Strips" || field.Name == "Triangles"))
                        Console.WriteLine($"    2D array: {field.Name} = {count} x {width} = {count * width} elements");
                    count *= width; // Total elements = length * width
                }

                if (count > 100000)
                {
                    if (_verbose)
                        Console.WriteLine($"    [Schema] WARNING: Array too large ({count}), skipping field {field.Name}");
                    return;
                }

                // For arrays that might be used as widths (like "Strip Lengths"), 
                // store individual values so jagged arrays can reference them
                var shouldStoreArrayValues = field.Name.EndsWith(" Lengths", StringComparison.Ordinal) && 
                                              field.Type == "ushort" && 
                                              count > 0 && count <= 100;
                int[]? arrayValues = shouldStoreArrayValues ? new int[count] : null;
                var startPosition = ctx.Position;

                for (var i = 0; i < count && ctx.Position < ctx.End; i++)
                {
                    // For arrays we need to store, read value before converting
                    if (arrayValues != null && ctx.Position + 2 <= ctx.End)
                    {
                        // Read big-endian value before conversion
                        arrayValues[i] = BinaryPrimitives.ReadUInt16BigEndian(ctx.Buffer.AsSpan(ctx.Position, 2));
                    }
                    ConvertSingleValue(ctx, field.Type, field.Template, depth);
                }

                // Store array values for use by subsequent fields
                if (arrayValues != null)
                {
                    ctx.FieldValues[$"#{field.Name}#Array"] = arrayValues;
                    if (_verbose)  // Always show when storing array values (helps debug jagged arrays)
                        Console.WriteLine($"      Stored array {field.Name} = [{string.Join(", ", arrayValues)}] at depth {depth}");
                }

                return;
            }

            // Single value
            ConvertSingleValue(ctx, field.Type, field.Template, depth);

            // Store value for conditional field evaluation
            StoreFieldValue(ctx, field, _verbose);

            // Safety warning: if position didn't advance for a non-empty field (only report at top level)
            if (ctx.Position == startPos && field.Type != "NiObject" && _verbose && depth == 0)
            {
                Console.WriteLine($"    [Schema] WARNING: Position stuck at {ctx.Position:X}, field {field.Name}:{field.Type}");
            }
        }
        finally
        {
            // Restore previous #ARG# value (or remove if there wasn't one)
            if (hadPreviousArg)
                ctx.FieldValues["#ARG#"] = previousArg!;
            else if (field.Arg != null)
                ctx.FieldValues.Remove("#ARG#");
        }
    }

    private long EvaluateArgExpression(string argExpr, Dictionary<string, object> fieldValues)
    {
        // Handle simple literal values
        if (long.TryParse(argExpr, out var literalValue))
            return literalValue;

        // Handle #ARG# propagation from parent
        if (argExpr == "#ARG#")
        {
            if (fieldValues.TryGetValue("#ARG#", out var parentArg))
                return Convert.ToInt64(parentArg);
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

    private void ConvertSingleValue(ConversionContext ctx, string typeName, string? template, int depth = 0)
    {
        // Handle SizedString explicitly (inline string with uint length prefix)
        // Note: "string" is a struct that contains either SizedString (old) or NiFixedString (new)
        // based on version, so we let it fall through to struct handling
        if (typeName == "SizedString")
        {
            ConvertSizedString(ctx);
            return;
        }

        if (typeName == "SizedString16")
        {
            ConvertSizedString16(ctx);
            return;
        }

        // Check basic types first
        if (_schema.BasicTypes.TryGetValue(typeName, out var basic))
        {
            ConvertBasicType(ctx, basic, template);
            return;
        }

        // Check enums (convert based on storage type)
        if (_schema.Enums.TryGetValue(typeName, out var enumDef))
        {
            if (_schema.BasicTypes.TryGetValue(enumDef.Storage, out var storageType))
                ConvertBasicType(ctx, storageType, null);
            return;
        }

        // Check structs (recursively convert fields)
        if (_schema.Structs.TryGetValue(typeName, out var structDef))
        {
            // Clear any field values that this struct defines, so each struct instance
            // in an array starts fresh. This prevents stale values from previous instances
            // from affecting conditional field parsing.
            foreach (var field in structDef.Fields)
            {
                ctx.FieldValues.Remove(field.Name);
            }

            ConvertFields(ctx, structDef.Fields, depth + 1);
            return;
        }

        // Unknown type - try to look up size and bulk swap
        var size = _schema.GetTypeSize(typeName);
        if (size.HasValue && size.Value > 0)
        {
            // Bulk swap based on size
            if (size.Value == 2) SwapUInt16InPlace(ctx.Buffer, ctx.Position);
            else if (size.Value == 4) SwapUInt32InPlace(ctx.Buffer, ctx.Position);
            else if (size.Value == 8) SwapUInt64InPlace(ctx.Buffer, ctx.Position);
            ctx.Position += size.Value;
        }
        else if (_verbose)
        {
            Console.WriteLine($"    [Schema] WARNING: Unknown type '{typeName}' with no size, cannot advance position");
        }
    }

    /// <summary>
    ///     Converts a SizedString (uint length + chars) - swaps the length field.
    /// </summary>
    private void ConvertSizedString(ConversionContext ctx)
    {
        if (ctx.Position + 4 > ctx.End) return;

        // Swap the length (uint, 4 bytes)
        SwapUInt32InPlace(ctx.Buffer, ctx.Position);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Buffer.AsSpan(ctx.Position, 4));
        ctx.Position += 4;

        // Skip the string data (chars don't need swapping)
        if (length > 0 && length < 0x10000) // Sanity check
            ctx.Position += (int)length;
    }

    /// <summary>
    ///     Converts a SizedString16 (ushort length + chars) - swaps the length field.
    /// </summary>
    private void ConvertSizedString16(ConversionContext ctx)
    {
        if (ctx.Position + 2 > ctx.End) return;

        // Swap the length (ushort, 2 bytes)
        SwapUInt16InPlace(ctx.Buffer, ctx.Position);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buffer.AsSpan(ctx.Position, 2));
        ctx.Position += 2;

        // Skip the string data (chars don't need swapping)
        if (length > 0 && length < 0x10000) // Sanity check
            ctx.Position += length;
    }

    private void ConvertBasicType(ConversionContext ctx, NifBasicType basic, string? template)
    {
        if (ctx.Position + basic.Size > ctx.End) return;

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
                    RemapBlockRef(ctx.Buffer, pos, ctx.BlockRemap);
                ctx.Position += 4;
                break;

            case 8:
                SwapUInt64InPlace(ctx.Buffer, pos);
                ctx.Position += 8;
                break;

            default:
                // Variable size (like strings) - skip for now
                break;
        }
    }

    private static void RemapBlockRef(byte[] buf, int pos, int[] blockRemap)
    {
        var idx = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos, 4));
        if (idx >= 0 && idx < blockRemap.Length)
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), blockRemap[idx]);
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
        if (int.TryParse(lengthExpr, System.Globalization.CultureInfo.InvariantCulture, out var literal))
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

    private void StoreFieldValue(ConversionContext ctx, NifFieldDef field, bool verbose)
    {
        // Store fields that may be needed for conditions or array lengths
        // This includes: Num X, X Count, Has X, Data Flags, BS Data Flags, etc.
        var shouldStore = field.Name.StartsWith("Num ", StringComparison.Ordinal) ||
                          field.Name.EndsWith(" Count", StringComparison.Ordinal) ||
                          field.Name.StartsWith("Has ", StringComparison.Ordinal) ||
                          field.Name.Contains("Flags", StringComparison.Ordinal) ||
                          field.Name.Contains("Type", StringComparison.Ordinal) ||
                          field.Name == "Compressed";

        if (!shouldStore) return;

        // Get the size from the schema - this handles enums, bitfields, basic types correctly
        var size = _schema.GetTypeSize(field.Type) ?? 0;

        if (size > 0 && ctx.Position >= size)
        {
            object val = size switch
            {
                1 => ctx.Buffer[ctx.Position - 1],
                2 => (int)BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buffer.AsSpan(ctx.Position - 2)),
                4 => (int)BinaryPrimitives.ReadUInt32LittleEndian(ctx.Buffer.AsSpan(ctx.Position - 4)),
                _ => 0
            };

            // For "Has X" fields (bool), normalize to 0/1
            if (field.Name.StartsWith("Has ", StringComparison.Ordinal) && size == 1)
            {
                val = ctx.Buffer[ctx.Position - 1] != 0 ? 1 : 0;
            }

            ctx.FieldValues[field.Name] = val;

            if (verbose)
                Console.WriteLine($"      Stored {field.Name} = {val} (from pos {ctx.Position - size:X})");
        }
    }

    private sealed class ConversionContext
    {
        public byte[] Buffer { get; }
        public int Position { get; set; }
        public int End { get; }
        public int[] BlockRemap { get; }
        public Dictionary<string, object> FieldValues { get; }
        public string BlockType { get; }

        public ConversionContext(byte[] buffer, int position, int end, int[] blockRemap, Dictionary<string, object> fieldValues, string blockType)
        {
            Buffer = buffer;
            Position = position;
            End = end;
            BlockRemap = blockRemap;
            FieldValues = fieldValues;
            BlockType = blockType;
        }
    }
}
