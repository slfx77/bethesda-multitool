// Schema-driven NIF block converter
// Reads type definitions from nif.xml and applies correct endian conversion automatically
// This eliminates manual errors like treating uint fields as ushort

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
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

    // `bool` is a 32-bit int up to and including NIF 4.0.0.2, then an 8-bit byte from 4.1.0.1 on
    // (see nif.xml <basic name="bool">). The schema stores the modern 1-byte size, which is correct
    // for every Bethesda stream we convert (Oblivion 20.0.0.x, FO3/FNV/Skyrim 20.2.0.x) — only the
    // Morrowind-era legacy-Gamebryo measure path (4.0.0.2) needs the widened 4-byte width.
    private const uint BoolWidensAtOrBelowVersion = 0x04000002;

    private static readonly Logger Log = Logger.Instance;

    private static readonly int[] EmptyRemap = [];

    private readonly NifSchema _schema;
    private readonly NifVersionContext _versionContext;

    // Measure-only mode: walk fields and advance position WITHOUT byte-swapping or block-ref remapping.
    // Used by NifParser to recover per-block byte ranges for older NIFs (Oblivion 20.0.0.x, Morrowind
    // 4.0.0.2) whose headers lack the Block Size array. The data is native little-endian PC, so the
    // count/length read-backs (which already use LittleEndian) are correct without any swap; swapping
    // would corrupt them. Inline-vs-index string resolution is handled by the schema itself (the
    // `string` struct's String/Index fields are version-gated since=20.1.0.3 / until=20.0.0.5).
    private readonly bool _measure;

    // Per-block Name capture during a measure pass (the block's NiObjectNET.Name inline string).
    private bool _capturingName;
    private string? _capturedName;

    // Reused across blocks (one converter instance is confined to a single conversion = single thread;
    // see NifOutputWriter). Cleared per TryConvert instead of allocating a fresh dictionary per block.
    private readonly Dictionary<string, object> _fieldValues = new();

    public NifSchemaConverter(NifSchema schema, uint version = 0x14020007, int userVersion = 0,
        int bsVersion = 34, bool measure = false)
    {
        _schema = schema;
        _measure = measure;
        _versionContext = new NifVersionContext
            { Version = version, UserVersion = (uint)userVersion, BsVersion = bsVersion };
    }

    /// <summary>
    ///     Measure a single block by field-walking its schema definition WITHOUT mutating bytes, for
    ///     NIFs whose header has no per-block Block Size array. Returns the block's byte length
    ///     (cursor advance) and its captured NiObjectNET.Name (inline <c>SizedString</c>, or null for
    ///     blocks that don't derive from NiObjectNET / have no name). Returns size -1 when the block
    ///     type is unknown to the schema, which the caller must treat as a hard failure (a wrong size
    ///     desyncs every following block — there is no per-block size to resync from).
    /// </summary>
    public (int Size, string? Name) MeasureBlock(byte[] buf, int startPos, int dataSectionEnd, string blockType)
    {
        if (!_measure)
        {
            throw new InvalidOperationException("MeasureBlock requires a converter constructed with measure: true.");
        }

        var objDef = _schema.GetObject(blockType);
        if (objDef == null)
        {
            return (-1, null);
        }

        _fieldValues.Clear();
        _capturingName = false;
        _capturedName = null;

        var context = new ConversionContext(buf, startPos, dataSectionEnd, EmptyRemap, _fieldValues, blockType);
        ConvertFields(context, objDef.AllFields);
        return (context.Position - startPos, _capturedName);
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
            _fieldValues.Clear();
            var context = new ConversionContext(buf, pos, end, blockRemap, _fieldValues, blockType);
            ConvertFields(context, objDef.AllFields);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"  [Schema] Error converting {blockType}: {ex.Message}");
            return false;
        }
    }

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

        /// <summary>
        ///     Stack of struct type names currently being converted, outermost first. Used by
        ///     field-level special cases (e.g. <c>NiAGDDataBlock.Data</c>) that need to
        ///     identify their container without crawling the whole conversion call stack.
        /// </summary>
        public Stack<string> StructStack { get; } = new();
    }

    #endregion

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
    ///     Returns the on-disk byte width of a basic type for the current NIF version. Identical to
    ///     the schema's stored size except for <c>bool</c>, which is 4 bytes up to and including
    ///     4.0.0.2 (Morrowind) and 1 byte thereafter — the schema stores only the modern 1-byte width.
    /// </summary>
    private int EffectiveTypeSize(string typeName, int schemaSize)
    {
        return typeName == "bool" && _versionContext.Version <= BoolWidensAtOrBelowVersion ? 4 : schemaSize;
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
        // Arm Name capture for the block's own NiObjectNET.Name (always the first top-level field of a
        // named block). Only depth 0 so nested struct "Name" fields (e.g. SemanticData) don't override
        // it. The actual capture happens at the inline SizedString inside the `string` struct.
        if (_measure && depth == 0 && _capturedName == null && field.Name == "Name")
        {
            _capturingName = true;
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
            && TryConvertNiAgdDataBlockData(ctx, field))
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

    /// <summary>
    ///     Swap the packed data buffer of a <c>NiAGDDataBlock</c> as a uint32 stream rather
    ///     than as the per-byte no-op the schema would otherwise apply. The buffer length
    ///     is <c>Num Data × Block Size</c>; we look both values up from the field-values
    ///     map that the converter populated when processing earlier fields. If anything
    ///     looks off (missing values, non-4-aligned size, out-of-bounds), bail and let the
    ///     normal path run — better to fall back to legacy behavior than corrupt bytes.
    /// </summary>
    private bool TryConvertNiAgdDataBlockData(ConversionContext ctx, NifFieldDef field)
    {
        if (field.Width is null
            || !ctx.FieldValues.TryGetValue("Num Data", out var numDataObj)
            || !ctx.FieldValues.TryGetValue("Block Size", out var blockSizeObj))
        {
            return false;
        }

        if (numDataObj is not int numData || blockSizeObj is not int blockSize)
        {
            return false;
        }

        var totalBytes = (long)numData * blockSize;
        if (totalBytes is < 0 or > int.MaxValue)
        {
            return false;
        }

        // The fix only applies when the packed data is 4-byte aligned (the only layout
        // we have empirical evidence for). NIFs with byte-sized channels would need
        // per-channel swap; fall back to the original behavior in that case.
        if (blockSize % 4 != 0)
        {
            return false;
        }

        var totalInt = (int)totalBytes;
        if (ctx.Position + totalInt > ctx.End)
        {
            return false;
        }

        if (!_measure)
        {
            for (var offset = 0; offset < totalInt; offset += 4)
            {
                SwapUInt32InPlace(ctx.Buffer, ctx.Position + offset);
            }
        }

        ctx.Position += totalInt;
        return true;
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

    private void StoreFieldValue(ConversionContext ctx, NifFieldDef field)
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
        var size = EffectiveTypeSize(field.Type, _schema.GetTypeSize(field.Type) ?? 0);

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

    private bool TryConvertStringType(ConversionContext ctx, string typeName)
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

        if (BytePackedBitflagTypes.Contains(typeName))
        {
            // Xbox 360 NIF tools wrote BSPartFlag (BSDismemberSkinInstance.Partitions[].PartFlag)
            // as raw bytes in the same layout as PC, not as a byte-swapped ushort. A normal
            // per-field swap inverts the bit positions: PF_EDITOR_VISIBLE (bit 0, value 0x0001)
            // becomes PF_START_NET_BONESET (bit 8, value 0x0100) and vice versa. The effect on
            // a converted prototype outfit is that body partitions lose the visible flag (so
            // limbs flicker / cull) and gore-cap partitions GAIN the visible flag (so the caps
            // render through the body, garbage-textured if the cap textures aren't loaded).
            // Skip the swap: advance the position by the storage size without touching bytes.
            var skipSize = _schema.GetTypeSize(enumDef.Storage) ?? 2;
            ctx.Position += skipSize;
            return true;
        }

        if (_schema.BasicTypes.TryGetValue(enumDef.Storage, out var storageType))
        {
            ConvertBasicType(ctx, storageType);
        }

        return true;
    }

    /// <summary>
    ///     Enum/bitflags type names whose bytes are stored in PC-native order even in Xbox 360
    ///     NIFs. The schema converter normally swaps every multi-byte enum/bitflags via its
    ///     ushort/uint storage type, but Bethesda's Xbox tools wrote these specific types as
    ///     raw byte pairs — swapping inverts the semantic bit positions. Add a type here only
    ///     when both an Xbox source and its PC counterpart show identical disk bytes for the
    ///     field (compare via <c>NifAnalyzer block</c>).
    /// </summary>
    private static readonly HashSet<string> BytePackedBitflagTypes = new(StringComparer.Ordinal)
    {
        "BSPartFlag",
    };

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

        if (!_measure)
        {
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
        if (!_measure)
        {
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
        }

        ctx.Position += size.Value;
    }

    /// <summary>
    ///     Converts a SizedString (uint length + chars) - swaps the length field. In measure mode the
    ///     swap is skipped (data is already LE) and the string is captured when Name capture is armed.
    /// </summary>
    private void ConvertSizedString(ConversionContext ctx)
    {
        if (ctx.Position + 4 > ctx.End)
        {
            return;
        }

        // Swap the length (uint, 4 bytes)
        if (!_measure)
        {
            SwapUInt32InPlace(ctx.Buffer, ctx.Position);
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Buffer.AsSpan(ctx.Position, 4));
        ctx.Position += 4;

        // Skip the string data (chars don't need swapping)
        if (length is > 0 and < 0x10000) // Sanity check
        {
            if (_measure && _capturingName)
            {
                _capturingName = false;
                if (ctx.Position + (int)length <= ctx.End)
                {
                    _capturedName = Encoding.ASCII.GetString(ctx.Buffer, ctx.Position, (int)length);
                }
            }

            ctx.Position += (int)length;
        }
        else if (_measure)
        {
            // Empty/oversized name: disarm so a later inline string isn't mistaken for the block name.
            _capturingName = false;
        }
    }

    /// <summary>
    ///     Converts a SizedString16 (ushort length + chars) - swaps the length field.
    /// </summary>
    private void ConvertSizedString16(ConversionContext ctx)
    {
        if (ctx.Position + 2 > ctx.End)
        {
            return;
        }

        // Swap the length (ushort, 2 bytes)
        if (!_measure)
        {
            SwapUInt16InPlace(ctx.Buffer, ctx.Position);
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(ctx.Buffer.AsSpan(ctx.Position, 2));
        ctx.Position += 2;

        // Skip the string data (chars don't need swapping)
        if (length > 0)
        {
            ctx.Position += length;
        }
    }

    private void ConvertBasicType(ConversionContext ctx, NifBasicType basic)
    {
        var size = EffectiveTypeSize(basic.Name, basic.Size);
        if (ctx.Position + size > ctx.End)
        {
            return;
        }

        var pos = ctx.Position; // Save position before modifying

        switch (size)
        {
            case 1:
                // No swap needed for single bytes
                ctx.Position += 1;
                break;

            case 2:
                if (!_measure)
                {
                    SwapUInt16InPlace(ctx.Buffer, pos);
                }

                ctx.Position += 2;
                break;

            case 4:
                if (!_measure)
                {
                    SwapUInt32InPlace(ctx.Buffer, pos);
                    // Handle block references (Ref, Ptr) that need remapping
                    if (basic.IsGeneric)
                    {
                        RemapBlockRef(ctx.Buffer, pos, ctx.BlockRemap);
                    }
                }

                ctx.Position += 4;
                break;

            case 8:
                if (!_measure)
                {
                    SwapUInt64InPlace(ctx.Buffer, pos);
                }

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
}
