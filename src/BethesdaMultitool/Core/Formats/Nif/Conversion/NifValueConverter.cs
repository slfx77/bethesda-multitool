using System.Buffers.Binary;
using System.Globalization;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Conditions;
using BethesdaMultitool.Core.Formats.Nif.Schema;

namespace BethesdaMultitool.Core.Formats.Nif.Conversion;

/// <summary>
///     Recursive field-list walker for the schema-driven NIF converter. Owns the schema /
///     version / measure state and drives field filtering (onlyT, version, conditions), array
///     and jagged-array conversion, struct recursion, and value dispatch. Leaf scalar/string
///     conversion is delegated to <see cref="NifScalarConverter" />.
/// </summary>
internal sealed class NifValueConverter
{
    private const string ArgPlaceholder = "#ARG#";
    private const string StripsFieldName = "Strips";
    private const string TrianglesFieldName = "Triangles";

    private static readonly Logger Log = Logger.Instance;

    private readonly NifSchema _schema;
    private readonly NifVersionContext _versionContext;
    private readonly bool _measure;

    public NifValueConverter(NifSchema schema, NifVersionContext versionContext, bool measure)
    {
        _schema = schema;
        _versionContext = versionContext;
        _measure = measure;
    }

    public void ConvertFields(NifConversionContext ctx, IReadOnlyList<NifFieldDef> fields, int depth = 0)
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
                if (_measure && ctx.BlockType is "bhkMoppBvTreeShape" or "bhkNiTriStripsShape")
                {
                    Console.Error.WriteLine($"RBT SKIP d{depth} {field.Name} (since={field.Since} until={field.Until} vercond={field.VersionCond} cond={field.Condition})");
                }

                continue;
            }

            if (depth == 0)
            {
                Log.Trace($"    Converting field {field.Name} at pos {ctx.Position:X}");
            }

            var before = ctx.Position;
            ConvertField(ctx, field, depth);
            if (_measure && ctx.BlockType is "bhkMoppBvTreeShape" or "bhkNiTriStripsShape")
            {
                Console.Error.WriteLine($"RBT d{depth} {field.Name} ({field.Type}) {before}->{ctx.Position} (+{ctx.Position - before})");
            }
        }
    }

    private bool ShouldProcessField(NifConversionContext ctx, NifFieldDef field, int depth)
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

    private bool IsFieldTypeMatch(NifConversionContext ctx, NifFieldDef field, int depth)
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

    private static bool IsFieldConditionMet(NifConversionContext ctx, NifFieldDef field, int depth)
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
            var sinceVersion = NifSchemaConverter.ParseVersionString(since);
            if (currentVersion < sinceVersion)
            {
                return false;
            }
        }

        // Parse "until" version
        if (!string.IsNullOrEmpty(until))
        {
            var untilVersion = NifSchemaConverter.ParseVersionString(until);
            if (currentVersion > untilVersion)
            {
                return false;
            }
        }

        return true;
    }

    private void ConvertField(NifConversionContext ctx, NifFieldDef field, int depth = 0)
    {
        // Arm Name capture for the block's own NiObjectNET.Name (always the first top-level field of a
        // named block). Only depth 0 so nested struct "Name" fields (e.g. SemanticData) don't override
        // it. The actual capture happens at the inline SizedString inside the `string` struct.
        if (_measure && depth == 0 && ctx.CapturedName == null && field.Name == "Name")
        {
            ctx.CapturingName = true;
        }

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

    private static void RestoreArgValue(NifConversionContext ctx, NifFieldDef field, bool hadPreviousArg,
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

    private void ConvertFieldValue(NifConversionContext ctx, NifFieldDef field, int depth)
    {
        // Special case: NiAGDDataBlock.Data is declared as `byte[Num Data][Block Size]`,
        // but the byte buffer is packed multi-byte channel data (typically 4-byte floats
        // for vertex positions/normals on LOD meshes — all 2,282 same-structure diffs in
        // the parity sweep landed here). Byte-by-byte conversion preserves the Xbox big-
        // endian byte order verbatim; the PC engine then reads garbage floats. Swap each
        // 4-byte uint32 in the buffer instead — handles every channel layout we have
        // empirical evidence for (Unit Size 4, Stride 4-multiple).
        if (field.Length != null
            && field.Name == "Data"
            && ctx.StructStack.Count > 0
            && ctx.StructStack.Peek() == "NiAGDDataBlock"
            && NifScalarConverter.TryConvertNiAgdDataBlockData(_measure, ctx, field))
        {
            return;
        }

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

    private void ConvertArrayField(NifConversionContext ctx, NifFieldDef field, int depth)
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

        // Sanity bound: no valid array has more elements than there are bytes left in the buffer (every
        // element is ≥ 1 byte). A magic constant here was too low — a big shape's tangent NiBinaryExtraData
        // (e.g. 144 KB) or vertex blob legitimately exceeds 100 K elements; skipping it under-measures the
        // block and desyncs every block after it on Oblivion's no-block-size legacy path (whole mesh pieces
        // vanish — e.g. ICAUTower01). Bounding by remaining bytes still rejects garbage/misread counts.
        var maxElements = ctx.End - ctx.Position;
        if (count > maxElements)
        {
            Log.Trace($"    [Schema] WARNING: Array length {count} exceeds {maxElements} remaining bytes, skipping field {field.Name}");
            return;
        }

        ConvertArrayElements(ctx, field, count, depth);
    }

    private int ResolveTwoDimensionalArrayCount(NifConversionContext ctx, NifFieldDef field, int count, int depth)
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

    private void ConvertJaggedArray(NifConversionContext ctx, NifFieldDef field, int rowCount, int[] widthArray,
        int depth)
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

    private void ConvertArrayElements(NifConversionContext ctx, NifFieldDef field, int count, int depth)
    {
        // For arrays that might be used as widths (like "Strip Lengths"), store individual values so a
        // following jagged array (NiTriStripsData "Points", length="Num Strips" width="Strip Lengths")
        // can reference them. The cap must cover real strip counts — a single architecture/fort mesh
        // can have many hundreds of strips (e.g. 149+). When it doesn't, the jagged Points array resolves
        // to width 0, so the strip-point data is never measured/converted: the NiTriStripsData block
        // under-measures, desyncing every block after it (Oblivion's no-block-size legacy path) → whole
        // mesh pieces drop. The bound matches ConvertArrayField's overall array guard.
        var shouldStoreArrayValues = field.Name.EndsWith(" Lengths", StringComparison.Ordinal) &&
                                     field.Type == "ushort" &&
                                     count is > 0 and <= 100000;
        var arrayValues = shouldStoreArrayValues ? new int[count] : null;

        for (var i = 0; i < count && ctx.Position < ctx.End; i++)
        {
            if (arrayValues is not null && ctx.Position + 2 <= ctx.End)
            {
                // Capture the width value in the SOURCE endianness: the convert path reads Xbox
                // big-endian data (before the in-place swap), the measure path reads native PC
                // little-endian. Reading BE on already-LE data byte-swaps the strip length and blows
                // up the jagged Points array (over-read to EOF).
                arrayValues[i] = _measure
                    ? BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buffer.AsSpan(ctx.Position, 2))
                    : BinaryPrimitives.ReadUInt16BigEndian(ctx.Buffer.AsSpan(ctx.Position, 2));
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

    private void StoreFieldValue(NifConversionContext ctx, NifFieldDef field)
    {
        // Store fields that may be needed for conditions or array lengths (Num X, X Count, Has X,
        // *Flags, *Type, Compressed, Interpolation for #ARG#, Block Size for NiAGDDataBlock.Data).
        // The decision is precomputed once at schema load — see NifFieldDef.StoresValueForLaterUse /
        // ComputeStoresValueForLaterUse (byte-for-byte the same predicate that used to run inline here).
        //
        // In MEASURE mode, store EVERY scalar instead: the convert path's predicate is enough only
        // because conversion is bounded by the header's known block size, so an array whose length
        // field the predicate skips (e.g. ByteArray.Data → "Data Size") can be silently not-advanced —
        // its bytes need no swap and it's the block tail. The unbounded measure walk has no such
        // boundary, so a skipped array under-reads the block and desyncs every block after it.
        if (!_measure && !field.StoresValueForLaterUse)
        {
            return;
        }

        // Get the size from the schema - this handles enums, bitfields, basic types correctly
        // (widened for the version-dependent `bool` width).
        var size = NifScalarConverter.EffectiveTypeSize(_versionContext.Version, field.Type,
            _schema.GetTypeSize(field.Type) ?? 0);

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

    private void ConvertSingleValue(NifConversionContext ctx, string typeName, int depth = 0)
    {
        var resolvedTypeName = ResolveTypeName(ctx, typeName);
        if (resolvedTypeName == null)
        {
            return;
        }

        // Handle special string types
        if (NifScalarConverter.TryConvertStringType(_measure, ctx, resolvedTypeName))
        {
            return;
        }

        // Handle basic types
        if (NifScalarConverter.TryConvertBasicType(_schema, _measure, _versionContext.Version, ctx, resolvedTypeName))
        {
            return;
        }

        // Handle enums
        if (NifScalarConverter.TryConvertEnumType(_schema, _measure, _versionContext.Version, ctx, resolvedTypeName))
        {
            return;
        }

        // Handle structs
        if (TryConvertStructType(ctx, resolvedTypeName, depth))
        {
            return;
        }

        // Unknown type - try bulk swap based on size
        NifScalarConverter.ConvertUnknownType(_schema, _measure, ctx, resolvedTypeName);
    }

    private static string? ResolveTypeName(NifConversionContext ctx, string typeName)
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

    private bool TryConvertStructType(NifConversionContext ctx, string typeName, int depth)
    {
        if (!_schema.Structs.TryGetValue(typeName, out var structDef))
        {
            return false;
        }

        // Some structs with fixed size (like HavokFilter) are packed bitfields that should
        // be swapped as a single unit rather than field-by-field.
        if (NifScalarConverter.TryBulkSwapFixedSizeStruct(_schema, _measure, ctx, structDef))
        {
            return true;
        }

        // Clear field values for fresh struct instance
        foreach (var field in structDef.Fields)
        {
            ctx.FieldValues.Remove(field.Name);
        }

        ctx.StructStack.Push(typeName);
        try
        {
            ConvertFields(ctx, structDef.Fields, depth + 1);
        }
        finally
        {
            ctx.StructStack.Pop();
        }

        return true;
    }
}
