using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     Walks one block body against its nif.xml definition and builds the value tree. One instance per
///     <see cref="NifBlockDecoder.Decode" /> call; not reusable and not thread-safe.
/// </summary>
/// <remarks>
///     Field filtering follows the converter's order (Conversion/NifValueConverter.cs:63-84): onlyT / excludeT
///     through <see cref="NifSchema.Inherits" /> against the block type, since / until against the header version,
///     then vercond and cond, both compiled strictly (<see cref="NifStrictExpressions" />). Unlike the converter it
///     never mutates bytes, never stops at the block end silently, never skips an array it cannot size, and has no
///     recursion cap other than the schema's own finite nesting.
/// </remarks>
internal sealed class NifBlockWalker
{
    /// <summary>
    ///     The structs whose single decoded field is exposed directly: at 20.1.0.3+ <c>string</c> and <c>FilePath</c>
    ///     hold only a <c>NiFixedString</c> index, before that only an inline <c>SizedString</c> (nif.xml:5613-5624,
    ///     5733-5743).
    /// </summary>
    private static readonly HashSet<string> TransparentStringWrappers = new(StringComparer.Ordinal)
    {
        "string", "FilePath"
    };

    /// <summary>
    ///     The most rows a two-dimensional or jagged array may have when its rows carry no bytes. Row counts in
    ///     nif.xml come from ushort or small bitfield counts, so this is far above anything valid.
    /// </summary>
    private const int MaxEmptyRows = 65_536;

    private const string TemplateToken = "#T#";

    private readonly int _blockIndex;
    private readonly string _blockType;
    private readonly IReadOnlyDictionary<string, NifBulkElement> _bulk;
    private readonly Func<IReadOnlyList<NifFieldDef>, IReadOnlySet<string>> _declaredNames;
    private readonly NifHeaderLayout _header;
    private readonly NifInfo _info;
    private readonly NifDecodeMode _mode;
    private readonly List<PathSegment> _path = [];
    private readonly List<NifDecodeFailure> _problems = [];
    private readonly NifSchema _schema;
    private readonly List<NifFieldSpan> _spans = [];
    private readonly NifVersionContext _version;

    private NifByteCursor _cursor;
    private int _spanSuppression;

    /// <summary>Creates a walker for one block.</summary>
    public NifBlockWalker(
        NifSchema schema,
        NifInfo info,
        NifHeaderLayout header,
        NifVersionContext version,
        IReadOnlyDictionary<string, NifBulkElement> bulk,
        Func<IReadOnlyList<NifFieldDef>, IReadOnlySet<string>> declaredNames,
        int blockIndex,
        string blockType,
        NifDecodeMode mode,
        NifByteCursor cursor)
    {
        _schema = schema;
        _info = info;
        _header = header;
        _version = version;
        _bulk = bulk;
        _declaredNames = declaredNames;
        _blockIndex = blockIndex;
        _blockType = blockType;
        _mode = mode;
        _cursor = cursor;
    }

    /// <summary>The spans recorded so far.</summary>
    public IReadOnlyList<NifFieldSpan> Spans => _spans;

    /// <summary>The soft problems recorded so far (tolerant mode only).</summary>
    public IReadOnlyList<NifDecodeFailure> Problems => _problems;

    /// <summary>The block cursor's position (restored to the block cursor even after a fault inside a unit struct).</summary>
    public int Position => _cursor.Position;

    /// <summary>
    ///     Decodes the block's fields into <paramref name="topLevel" />, which keeps every completed top-level field
    ///     even when a fault propagates.
    /// </summary>
    public void Walk(NifObjectDef definition, List<NifField> topLevel)
    {
        var scope = new NifDecodeScope(null, definition.Name, _declaredNames(definition.AllFields), null, null);
        DecodeFields(definition.AllFields, scope, topLevel);
    }

    private void DecodeFields(IReadOnlyList<NifFieldDef> fields, NifDecodeScope scope, List<NifField> result)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var ordinal = CountDecoded(result, field.Name);
            _path.Add(PathSegment.Field(field.Name, ordinal));
            var start = _cursor.AbsolutePosition;
            try
            {
                if (!IsPresent(field, scope))
                {
                    scope.RecordSkipped(field.Name);
                    continue;
                }

                var value = DecodeFieldValue(field, scope);
                scope.RecordDecoded(field.Name, value);
                result.Add(new NifField(field.Name, ordinal, i, value));
                if (_spanSuppression == 0)
                {
                    _spans.Add(new NifFieldSpan(CurrentPath(), start, _cursor.AbsolutePosition - start));
                }
            }
            catch (NifDecodeFault fault) when (!fault.IsLocated)
            {
                throw fault.At(CurrentPath(), start);
            }
            finally
            {
                _path.RemoveAt(_path.Count - 1);
            }
        }
    }

    private static int CountDecoded(List<NifField> decoded, string name)
    {
        var count = 0;
        foreach (var field in decoded)
        {
            if (string.Equals(field.Name, name, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private bool IsPresent(NifFieldDef field, NifDecodeScope scope)
    {
        if (field.OnlyT is { } onlyT && !_schema.Inherits(_blockType, RequireKnownObject(onlyT, "onlyT")))
        {
            return false;
        }

        if (field.ExcludeT is { } excludeT && _schema.Inherits(_blockType, RequireKnownObject(excludeT, "excludeT")))
        {
            return false;
        }

        if (field.Since is { } since && _version.Version < ParseVersionBound(since, "since"))
        {
            return false;
        }

        if (field.Until is { } until && _version.Version > ParseVersionBound(until, "until"))
        {
            return false;
        }

        if (field.VersionCond is { } versionCondition)
        {
            if (!NifStrictExpressions.TryGetVersionCondition(versionCondition, out var evaluator, out var error))
            {
                throw new NifDecodeFault(NifDecodeFailureKind.Schema, $"vercond is not evaluable: {error}");
            }

            if (!evaluator(_version))
            {
                return false;
            }
        }

        if (field.Condition is { } condition)
        {
            if (!NifStrictExpressions.TryGetCondition(condition, out var compiled, out var error))
            {
                throw new NifDecodeFault(NifDecodeFailureKind.Schema, $"cond is not evaluable: {error}");
            }

            if (!compiled.EvaluateCondition(new NifScopeFieldView(scope)))
            {
                return false;
            }
        }

        return true;
    }

    private string RequireKnownObject(string typeName, string attribute)
    {
        return _schema.Objects.ContainsKey(typeName)
            ? typeName
            : throw new NifDecodeFault(NifDecodeFailureKind.Schema,
                $"{attribute} names '{typeName}', which nif.xml does not define as a block type");
    }

    private static uint ParseVersionBound(string text, string attribute)
    {
        return NifStrictExpressions.TryParseVersion(text, out var version)
            ? version
            : throw new NifDecodeFault(NifDecodeFailureKind.Schema, $"{attribute}=\"{text}\" is not a version");
    }

    private NifValue DecodeFieldValue(NifFieldDef field, NifDecodeScope scope)
    {
        long? argument = field.Arg is { } argText ? EvaluateValue(argText, scope, "arg") : null;
        var template = field.Template is { } templateText ? ResolveTemplate(templateText, scope) : null;
        var typeName = string.Equals(field.Type, TemplateToken, StringComparison.Ordinal)
            ? scope.Template ?? throw new NifDecodeFault(NifDecodeFailureKind.Schema,
                $"type #T# is used but no template was passed to {scope.DefinitionName}")
            : field.Type;

        if (field.Length is { } lengthText)
        {
            return DecodeArray(field, typeName, lengthText, scope, argument, template);
        }

        if (field.Width is not null)
        {
            throw new NifDecodeFault(NifDecodeFailureKind.Schema, "width is declared without a length");
        }

        return DecodeSingle(typeName, scope, argument, template);
    }

    private static string ResolveTemplate(string template, NifDecodeScope scope)
    {
        if (!string.Equals(template, TemplateToken, StringComparison.Ordinal))
        {
            return template;
        }

        return scope.Template ?? throw new NifDecodeFault(NifDecodeFailureKind.Schema,
            $"template #T# is forwarded but no template was passed to {scope.DefinitionName}");
    }

    private static long EvaluateValue(string text, NifDecodeScope scope, string attribute)
    {
        if (!NifStrictExpressions.TryGetValue(text, out var compiled, out var error))
        {
            throw new NifDecodeFault(NifDecodeFailureKind.Schema, $"{attribute} is not evaluable: {error}");
        }

        return compiled.EvaluateValue(new NifScopeFieldView(scope));
    }

    private static int EvaluateCount(string text, NifDecodeScope scope, string attribute)
    {
        var value = EvaluateValue(text, scope, attribute);
        return value is >= 0 and <= int.MaxValue
            ? (int)value
            : throw new NifDecodeFault(NifDecodeFailureKind.Data, $"{attribute} '{text}' evaluates to {value}");
    }

    private NifValue DecodeArray(
        NifFieldDef field,
        string typeName,
        string lengthText,
        NifDecodeScope scope,
        long? argument,
        string? template)
    {
        var count = EvaluateCount(lengthText, scope, "length");
        if (NifDecodeQuirks.IsAgdPayload(scope.DefinitionName, field.Name))
        {
            return DecodeAgdPayload(typeName, scope);
        }

        return field.Width is { } widthText
            ? DecodeRows(widthText, typeName, count, scope, argument, template)
            : DecodeElements(typeName, count, scope, argument, template);
    }

    /// <summary>Quirk 3 (<see cref="NifDecodeQuirks.IsAgdPayload" />): the payload is Block Size bytes.</summary>
    private NifValue DecodeAgdPayload(string typeName, NifDecodeScope scope)
    {
        if (!string.Equals(typeName, "byte", StringComparison.Ordinal))
        {
            throw new NifDecodeFault(NifDecodeFailureKind.Schema,
                $"{NifDecodeQuirks.AgdDataBlockStruct}.{NifDecodeQuirks.AgdPayloadField} is declared as {typeName}, " +
                "not byte; the Block Size erratum does not apply");
        }

        var blockSize = scope.ResolveInteger(NifDecodeQuirks.AgdBlockSizeField);
        if (blockSize is < 0 or > int.MaxValue)
        {
            throw new NifDecodeFault(NifDecodeFailureKind.Data, $"Block Size {blockSize} is not a byte count");
        }

        return DecodeElements(typeName, (int)blockSize, scope, null, null);
    }

    private NifArrayValue DecodeRows(
        string widthText,
        string typeName,
        int rowCount,
        NifDecodeScope scope,
        long? argument,
        string? template)
    {
        if (!NifStrictExpressions.TryGetValue(widthText, out var widthExpression, out var error))
        {
            throw new NifDecodeFault(NifDecodeFailureKind.Schema, $"width is not evaluable: {error}");
        }

        if (rowCount > _cursor.Remaining && rowCount > MaxEmptyRows)
        {
            throw new NifDecodeFault(NifDecodeFailureKind.Data,
                $"{rowCount} rows cannot fit in the {_cursor.Remaining} bytes left in the block");
        }

        long[] widths;
        if (widthExpression.SingleFieldName is { } widthField &&
            scope.Resolve(widthField) is { } widthValue &&
            TryGetIntegerElements(widthValue, out var jagged))
        {
            if (jagged.Length != rowCount)
            {
                throw new NifDecodeFault(NifDecodeFailureKind.Data,
                    $"jagged width '{widthField}' has {jagged.Length} entries for {rowCount} rows");
            }

            widths = jagged;
        }
        else
        {
            var width = EvaluateCount(widthText, scope, "width");
            widths = new long[rowCount];
            Array.Fill(widths, width);
        }

        long total = 0;
        foreach (var width in widths)
        {
            if (width is < 0 or > int.MaxValue)
            {
                throw new NifDecodeFault(NifDecodeFailureKind.Data, $"row width {width} is not an element count");
            }

            total += width;
        }

        RequireAllocation(total, MinimumElementSize(typeName), typeName);

        var rows = new List<NifValue>(rowCount);
        for (var row = 0; row < rowCount; row++)
        {
            _path.Add(PathSegment.Element(row));
            var start = _cursor.AbsolutePosition;
            try
            {
                rows.Add(DecodeElements(typeName, (int)widths[row], scope, argument, template));
            }
            catch (NifDecodeFault fault) when (!fault.IsLocated)
            {
                throw fault.At(CurrentPath(), start);
            }
            finally
            {
                _path.RemoveAt(_path.Count - 1);
            }
        }

        return new NifArrayValue(typeName, rows, true);
    }

    private static bool TryGetIntegerElements(NifValue value, out long[] elements)
    {
        switch (value)
        {
            case NifUInt16ArrayValue { ComponentsPerElement: 1 } shorts:
                elements = new long[shorts.Count];
                for (var i = 0; i < elements.Length; i++)
                {
                    elements[i] = shorts.Get(i);
                }

                return true;
            case NifByteArrayValue bytes:
                elements = new long[bytes.Count];
                var span = bytes.Bytes.Span;
                for (var i = 0; i < elements.Length; i++)
                {
                    elements[i] = span[i];
                }

                return true;
            case NifArrayValue { IsRows: false } list when list.Items.All(item => item is NifIntegerValue):
                elements = list.Items.Select(item => ((NifIntegerValue)item).Value).ToArray();
                return true;
            default:
                elements = [];
                return false;
        }
    }

    private NifValue DecodeElements(
        string typeName,
        int count,
        NifDecodeScope scope,
        long? argument,
        string? template)
    {
        RequireAllocation(count, MinimumElementSize(typeName), typeName);
        if (_bulk.TryGetValue(typeName, out var bulk))
        {
            return ReadBulk(bulk, count);
        }

        var items = new List<NifValue>(count);
        _spanSuppression++;
        try
        {
            for (var i = 0; i < count; i++)
            {
                _path.Add(PathSegment.Element(i));
                var start = _cursor.AbsolutePosition;
                try
                {
                    items.Add(DecodeSingle(typeName, scope, argument, template));
                }
                catch (NifDecodeFault fault) when (!fault.IsLocated)
                {
                    throw fault.At(CurrentPath(), start);
                }
                finally
                {
                    _path.RemoveAt(_path.Count - 1);
                }
            }
        }
        finally
        {
            _spanSuppression--;
        }

        return new NifArrayValue(typeName, items, false);
    }

    /// <summary>
    ///     Bounded allocation: refuses <paramref name="count" /> elements before allocating anything when even the
    ///     smallest possible elements could not fit in the bytes left.
    /// </summary>
    private void RequireAllocation(long count, int minimumElementSize, string typeName)
    {
        // count x size <= remaining, compared by division so a hostile count cannot overflow the product.
        if (count > _cursor.Remaining / minimumElementSize)
        {
            throw new NifDecodeFault(NifDecodeFailureKind.Data,
                $"{count} x {typeName} (at least {minimumElementSize} byte(s) each) cannot fit in the " +
                $"{_cursor.Remaining} bytes left in the block");
        }
    }

    /// <summary>
    ///     A lower bound on one element's size. Struct <c>size</c> attributes are not version-aware in nif.xml
    ///     (NiParticleInfo declares 40 bytes but reads 28 at 20.2.0.7), so an unconfirmed struct counts as 1.
    /// </summary>
    private int MinimumElementSize(string typeName)
    {
        if (_bulk.TryGetValue(typeName, out var bulk))
        {
            return bulk.ElementSize;
        }

        if (_schema.BasicTypes.TryGetValue(typeName, out var basic))
        {
            return Math.Max(1, NifScalarConverter.EffectiveTypeSize(_version.Version, basic.Name, basic.Size));
        }

        if (_schema.Enums.TryGetValue(typeName, out var enumDef) &&
            _schema.BasicTypes.TryGetValue(enumDef.Storage, out var storage))
        {
            return Math.Max(1, NifScalarConverter.EffectiveTypeSize(_version.Version, storage.Name, storage.Size));
        }

        return typeName switch
        {
            "SizedString" => 4,
            "SizedString16" => 2,
            _ => 1
        };
    }

    private NifValue ReadBulk(NifBulkElement bulk, int count)
    {
        var components = count * bulk.ComponentsPerElement;
        var bytes = _cursor.Take(components * bulk.ComponentSize);
        var bigEndian = _cursor.BigEndian;
        switch (bulk.Component)
        {
            case NifBulkComponent.Float32:
            {
                var bits = new uint[components];
                for (var i = 0; i < components; i++)
                {
                    var slice = bytes.Slice(i * 4, 4);
                    bits[i] = bigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(slice)
                        : BinaryPrimitives.ReadUInt32LittleEndian(slice);
                }

                return new NifFloatArrayValue(bulk.TypeName, bulk.ComponentsPerElement, bits);
            }
            case NifBulkComponent.UInt16:
            {
                var values = new ushort[components];
                for (var i = 0; i < components; i++)
                {
                    var slice = bytes.Slice(i * 2, 2);
                    values[i] = bigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(slice)
                        : BinaryPrimitives.ReadUInt16LittleEndian(slice);
                }

                return new NifUInt16ArrayValue(bulk.TypeName, bulk.ComponentsPerElement, values);
            }
            default:
                return new NifByteArrayValue(bulk.TypeName, bytes.ToArray());
        }
    }

    private NifValue DecodeSingle(string typeName, NifDecodeScope scope, long? argument, string? template)
    {
        if (_schema.BasicTypes.TryGetValue(typeName, out var basic))
        {
            return ReadBasic(basic, template);
        }

        if (_schema.Enums.TryGetValue(typeName, out var enumDef))
        {
            return ReadEnum(enumDef);
        }

        if (_schema.Structs.TryGetValue(typeName, out var structDef))
        {
            return ReadStruct(structDef, scope, argument, template);
        }

        throw new NifDecodeFault(NifDecodeFailureKind.Schema, _schema.Objects.ContainsKey(typeName)
            ? $"'{typeName}' is a block type; a field cannot embed a block"
            : $"type '{typeName}' is not defined by nif.xml");
    }

    private NifValue ReadBasic(NifBasicType basic, string? template)
    {
        switch (basic.Name)
        {
            case "float":
                return new NifFloatValue(basic.Name, (uint)_cursor.ReadUnsigned(4));
            case "hfloat":
                return new NifHalfFloatValue(basic.Name, (ushort)_cursor.ReadUnsigned(2));
            case "Ref":
            case "Ptr":
                return ReadReference(basic.Name, template);
            case "NiFixedString":
                return ReadStringIndex(basic.Name);
            case "ulittle32":
                // nif.xml: "A little-endian unsigned 32-bit integer" in every file.
                return new NifIntegerValue(basic.Name, basic.Name, 4, false, _cursor.ReadUnsigned(4, true));
            case "HeaderString":
            case "LineString":
                throw new NifDecodeFault(NifDecodeFailureKind.Schema,
                    $"'{basic.Name}' is a newline-terminated header string and cannot appear in a block");
        }

        var width = NifScalarConverter.EffectiveTypeSize(_version.Version, basic.Name, basic.Size);
        if (width is not (1 or 2 or 4 or 8))
        {
            throw new NifDecodeFault(NifDecodeFailureKind.Schema,
                $"basic type '{basic.Name}' has no readable fixed width ({width} bytes)");
        }

        return new NifIntegerValue(basic.Name, basic.Name, width, IsSigned(basic.Name), _cursor.ReadUnsigned(width));
    }

    private static bool IsSigned(string basicName)
    {
        return basicName is "int" or "short" or "sbyte" or "int64";
    }

    private NifIntegerValue ReadEnum(NifEnumDef enumDef)
    {
        if (!_schema.BasicTypes.TryGetValue(enumDef.Storage, out var storage))
        {
            throw new NifDecodeFault(NifDecodeFailureKind.Schema,
                $"'{enumDef.Name}' stores as '{enumDef.Storage}', which is not a basic type");
        }

        var width = NifScalarConverter.EffectiveTypeSize(_version.Version, storage.Name, storage.Size);
        if (width is not (1 or 2 or 4 or 8))
        {
            throw new NifDecodeFault(NifDecodeFailureKind.Schema,
                $"'{enumDef.Name}' storage '{storage.Name}' has no readable fixed width ({width} bytes)");
        }

        // Quirk 1 (NifDecodeQuirks.IsLittleEndianInBigEndianFiles): BSPartFlag is little-endian in big-endian files.
        var forceLittleEndian = _cursor.BigEndian && NifDecodeQuirks.IsLittleEndianInBigEndianFiles(enumDef.Name);
        return new NifIntegerValue(enumDef.Name, storage.Name, width, IsSigned(storage.Name),
            _cursor.ReadUnsigned(width, forceLittleEndian));
    }

    private NifRefValue ReadReference(string typeName, string? template)
    {
        var start = _cursor.AbsolutePosition;
        var index = _cursor.ReadInt32();
        var blockCount = _info.Blocks.Count;
        if (index < -1 || index >= blockCount)
        {
            Problem(NifDecodeFailureKind.Reference, $"{typeName} {index} is outside [-1, {blockCount})", start);
        }
        else if (index >= 0 && template is not null)
        {
            RequireKnownObject(template, "template");
            var targetType = _info.Blocks[index].TypeName;
            if (!_schema.Inherits(targetType, template))
            {
                Problem(NifDecodeFailureKind.Reference,
                    $"{typeName} {index} points at a {targetType}, which is not a {template}", start);
            }
        }

        return new NifRefValue(typeName, index, template);
    }

    private NifStringValue ReadStringIndex(string typeName)
    {
        var start = _cursor.AbsolutePosition;
        var index = _cursor.ReadInt32();
        if (index == -1)
        {
            return new NifStringValue(typeName, index, null);
        }

        if (index < 0 || index >= _header.Strings.Count)
        {
            Problem(NifDecodeFailureKind.StringIndex,
                $"string index {index} is outside [-1, {_header.Strings.Count})", start);
            return new NifStringValue(typeName, index, null);
        }

        return new NifStringValue(typeName, index, _header.Strings.SharedEntry(index));
    }

    private NifValue ReadStruct(NifStructDef structDef, NifDecodeScope scope, long? argument, string? template)
    {
        switch (structDef.Name)
        {
            case "SizedString":
                return ReadSizedString(structDef.Name, 4);
            case "SizedString16":
                return ReadSizedString(structDef.Name, 2);
        }

        if (NifDecodeQuirks.IsHavokFilter(structDef.Name))
        {
            // Quirk 4: one big-endian uint32 at NiStream sites, raw little-endian inside bhkRigidBodyCInfo*.
            var reverse = _cursor.BigEndian && !NifDecodeQuirks.IsHavokFilterRawLittleEndian(scope.DefinitionName);
            return ReadUnitStruct(structDef, scope, argument, template, 4, reverse);
        }

        if (NifDecodeQuirks.IsBigEndianUnitStruct(_schema, structDef))
        {
            // Quirk 2: single-byte-field fixed-size structs are one big-endian unit in big-endian files.
            return ReadUnitStruct(structDef, scope, argument, template, structDef.FixedSize!.Value, _cursor.BigEndian);
        }

        var inner = new NifDecodeScope(scope, structDef.Name, _declaredNames(structDef.Fields), argument, template);
        var fields = new List<NifField>();
        DecodeFields(structDef.Fields, inner, fields);
        if (TransparentStringWrappers.Contains(structDef.Name) && fields.Count == 1 &&
            fields[0].Value is NifStringValue or NifSizedStringValue)
        {
            return fields[0].Value;
        }

        return new NifStructValue(structDef.Name, fields);
    }

    private NifSizedStringValue ReadSizedString(string typeName, int prefixBytes)
    {
        var length = prefixBytes == 4 ? _cursor.ReadUInt32() : _cursor.ReadUInt16();
        if (length > (uint)_cursor.Remaining)
        {
            throw new NifDecodeFault(NifDecodeFailureKind.Data,
                $"{typeName} declares {length} bytes but only {_cursor.Remaining} remain in the block");
        }

        return new NifSizedStringValue(typeName, prefixBytes, _cursor.Take((int)length).ToArray());
    }

    /// <summary>
    ///     Reads <paramref name="size" /> bytes as one unit, reverses them when the unit is stored big-endian, and
    ///     decodes the struct's fields from the resulting little-endian layout. The fields must consume the unit
    ///     exactly.
    /// </summary>
    private NifStructValue ReadUnitStruct(
        NifStructDef structDef,
        NifDecodeScope scope,
        long? argument,
        string? template,
        int size,
        bool reverse)
    {
        var start = _cursor.AbsolutePosition;
        var unit = _cursor.Take(size).ToArray();
        if (reverse)
        {
            Array.Reverse(unit);
        }

        var blockCursor = _cursor;
        _cursor = new NifByteCursor(unit, 0, size, false, start);
        _spanSuppression++;
        try
        {
            var inner = new NifDecodeScope(scope, structDef.Name, _declaredNames(structDef.Fields), argument, template);
            var fields = new List<NifField>();
            DecodeFields(structDef.Fields, inner, fields);
            if (_cursor.Position != size)
            {
                throw new NifDecodeFault(NifDecodeFailureKind.Schema,
                    $"{structDef.Name} is a {size}-byte unit but its fields cover {_cursor.Position} bytes");
            }

            return new NifStructValue(structDef.Name, fields);
        }
        finally
        {
            _cursor = blockCursor;
            _spanSuppression--;
        }
    }

    private void Problem(NifDecodeFailureKind kind, string reason, int offset)
    {
        if (_mode == NifDecodeMode.Strict)
        {
            throw new NifDecodeFault(kind, reason);
        }

        _problems.Add(new NifDecodeFailure(_blockIndex, _blockType, kind, CurrentPath(), offset, reason));
    }

    private string CurrentPath()
    {
        var builder = new StringBuilder();
        foreach (var segment in _path)
        {
            if (segment.Name is null)
            {
                builder.Append('[').Append(segment.Index).Append(']');
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(segment.Name);
            if (segment.Index > 0)
            {
                builder.Append('#').Append(segment.Index);
            }
        }

        return builder.ToString();
    }

    /// <summary>A path segment: a field (name plus ordinal) or an array index (no name).</summary>
    private readonly record struct PathSegment(string? Name, int Index)
    {
        public static PathSegment Field(string name, int ordinal)
        {
            return new PathSegment(name, ordinal);
        }

        public static PathSegment Element(int index)
        {
            return new PathSegment(null, index);
        }
    }
}
