// Schema-driven NIF block converter
// Reads type definitions from nif.xml and applies correct endian conversion automatically
// This eliminates manual errors like treating uint fields as ushort

using System.Collections.Concurrent;
using System.Globalization;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;

namespace BethesdaMultitool.Core.Formats.Nif.Conversion;

/// <summary>
///     Schema-driven NIF block converter that uses nif.xml definitions.
///     Automatically determines field types and applies correct byte swapping.
/// </summary>
internal sealed class NifSchemaConverter
{
    private static readonly Logger Log = Logger.Instance;

    private static readonly int[] EmptyRemap = [];

    // nif.xml contains only ~20-30 distinct version strings, but ParseVersionString is called once per
    // field check (5K-25K times per NIF). Memoizing collapses that to ~20-30 parses total. Static +
    // concurrent so the cache is shared across concurrent batch conversions and bounded by the schema.
    private static readonly ConcurrentDictionary<string, uint> VersionParseCache = new(StringComparer.Ordinal);

    private readonly NifSchema _schema;

    // Measure-only mode: walk fields and advance position WITHOUT byte-swapping or block-ref remapping.
    // Used by NifParser to recover per-block byte ranges for older NIFs (Oblivion 20.0.0.x, Morrowind
    // 4.0.0.2) whose headers lack the Block Size array. The data is native little-endian PC, so the
    // count/length read-backs (which already use LittleEndian) are correct without any swap; swapping
    // would corrupt them. Inline-vs-index string resolution is handled by the schema itself (the
    // `string` struct's String/Index fields are version-gated since=20.1.0.3 / until=20.0.0.5).
    private readonly bool _measure;

    private readonly NifValueConverter _valueConverter;

    // Reused across blocks (one converter instance is confined to a single conversion = single thread;
    // see NifOutputWriter). Cleared per TryConvert instead of allocating a fresh dictionary per block.
    private readonly Dictionary<string, object> _fieldValues = new();

    public NifSchemaConverter(NifSchema schema, uint version = 0x14020007, int userVersion = 0,
        int bsVersion = 34, bool measure = false)
    {
        _schema = schema;
        _measure = measure;
        var versionContext = new NifVersionContext
            { Version = version, UserVersion = (uint)userVersion, BsVersion = bsVersion };
        _valueConverter = new NifValueConverter(schema, versionContext, measure);
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

        var context = new NifConversionContext(buf, startPos, dataSectionEnd, EmptyRemap, _fieldValues, blockType);
        _valueConverter.ConvertFields(context, objDef.AllFields);
        return (context.Position - startPos, context.CapturedName);
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
            var context = new NifConversionContext(buf, pos, end, blockRemap, _fieldValues, blockType);
            _valueConverter.ConvertFields(context, objDef.AllFields);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"  [Schema] Error converting {blockType}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Parses a version string like "20.2.0.7" or "4.2.2.0" into a uint. Memoized: the parse is
    ///     pure, so repeated lookups of the same string (the common case during conversion) are O(1).
    /// </summary>
    internal static uint ParseVersionString(string version)
    {
        return VersionParseCache.GetOrAdd(version, static v => ParseVersionStringCore(v));
    }

    private static uint ParseVersionStringCore(string version)
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
}
