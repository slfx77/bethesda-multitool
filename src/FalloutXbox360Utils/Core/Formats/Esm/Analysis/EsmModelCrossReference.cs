using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Analysis;

/// <summary>
///     Lightweight ESM scanner that builds reverse indexes mapping model paths to base records
///     and base records to placed references (REFR/ACHR/ACRE).
/// </summary>
internal sealed class EsmModelCrossReference
{
    private static readonly HashSet<string> ModlRecordTypes = new(StringComparer.Ordinal)
    {
        "STAT", "ACTI", "DOOR", "LIGH", "FURN", "WEAP", "ARMO", "AMMO", "ALCH",
        "MISC", "BOOK", "CONT", "KEYM", "NOTE", "MSTT", "TREE", "FLOR", "GRAS",
        "CREA", "NPC_", "PROJ", "TACT", "ARMA", "IDLM"
    };

    private static readonly HashSet<string> RefRecordTypes = new(StringComparer.Ordinal)
    {
        "REFR", "ACHR", "ACRE"
    };

    /// <summary>
    ///     Reverse index: base FormID → list of placed references.
    /// </summary>
    private readonly Dictionary<uint, List<RefEntry>> _baseToRefs;

    /// <summary>
    ///     Reverse index: lowercase normalized model path → list of base records that use it.
    /// </summary>
    private readonly Dictionary<string, List<BaseRecordRef>> _modelToRecords;

    private EsmModelCrossReference(
        Dictionary<string, List<BaseRecordRef>> modelToRecords,
        Dictionary<uint, List<RefEntry>> baseToRefs)
    {
        _modelToRecords = modelToRecords;
        _baseToRefs = baseToRefs;
    }

    /// <summary>
    ///     Total number of base records indexed (those with MODL subrecords).
    /// </summary>
    public int BaseRecordCount => _modelToRecords.Values.Sum(l => l.Count);

    /// <summary>
    ///     Total number of placed references indexed.
    /// </summary>
    public int RefCount => _baseToRefs.Values.Sum(l => l.Count);

    /// <summary>
    ///     Scans the ESM and builds the cross-reference indexes.
    /// </summary>
    public static EsmModelCrossReference Build(byte[] esmData, bool bigEndian)
    {
        var modelToRecords = new Dictionary<string, List<BaseRecordRef>>(StringComparer.OrdinalIgnoreCase);
        var baseToRefs = new Dictionary<uint, List<RefEntry>>();

        var records = EsmRecordParser.ScanAllRecords(esmData, bigEndian);

        foreach (var record in records)
        {
            if (RefRecordTypes.Contains(record.Signature))
            {
                ProcessRefRecord(esmData, bigEndian, record, baseToRefs);
            }
            else if (ModlRecordTypes.Contains(record.Signature))
            {
                ProcessModlRecord(esmData, bigEndian, record, modelToRecords);
            }
        }

        return new EsmModelCrossReference(modelToRecords, baseToRefs);
    }

    /// <summary>
    ///     Looks up all base records and placed references for a given BSA model path.
    /// </summary>
    public ModelCrossRefResult? Lookup(string bsaModelPath)
    {
        // BSA paths include "meshes\" prefix, ESM MODL paths do not
        var normalized = NormalizePath(bsaModelPath);
        if (normalized.StartsWith("meshes\\", StringComparison.Ordinal))
        {
            normalized = normalized["meshes\\".Length..];
        }

        if (!_modelToRecords.TryGetValue(normalized, out var baseRecords))
        {
            return null;
        }

        var refs = new List<RefEntry>();
        foreach (var baseRecord in baseRecords)
        {
            if (_baseToRefs.TryGetValue(baseRecord.FormId, out var refEntries))
            {
                refs.AddRange(refEntries);
            }
        }

        return new ModelCrossRefResult(baseRecords, refs);
    }

    private static void ProcessRefRecord(byte[] esmData, bool bigEndian,
        AnalyzerRecordInfo record, Dictionary<uint, List<RefEntry>> baseToRefs)
    {
        var recordData = ReadRecordData(esmData, bigEndian, record);
        if (recordData == null)
        {
            return;
        }

        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

        uint baseFormId = 0;
        string? editorId = null;

        foreach (var sub in subrecords)
        {
            switch (sub.Signature)
            {
                case "NAME" when sub.Data.Length == 4:
                    baseFormId = BinaryUtils.ReadUInt32(sub.Data, 0, bigEndian);
                    break;
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(sub);
                    break;
            }
        }

        if (baseFormId == 0)
        {
            return;
        }

        if (!baseToRefs.TryGetValue(baseFormId, out var refList))
        {
            refList = [];
            baseToRefs[baseFormId] = refList;
        }

        refList.Add(new RefEntry(record.FormId, editorId));
    }

    private static void ProcessModlRecord(byte[] esmData, bool bigEndian,
        AnalyzerRecordInfo record, Dictionary<string, List<BaseRecordRef>> modelToRecords)
    {
        var recordData = ReadRecordData(esmData, bigEndian, record);
        if (recordData == null)
        {
            return;
        }

        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);

        string? editorId = null;
        string? modelPath = null;

        foreach (var sub in subrecords)
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(sub);
                    break;
                case "MODL":
                    modelPath = EsmRecordParser.GetSubrecordString(sub);
                    break;
            }

            // Both found, no need to continue
            if (editorId != null && modelPath != null)
            {
                break;
            }
        }

        if (string.IsNullOrEmpty(modelPath))
        {
            return;
        }

        var normalized = NormalizePath(modelPath);
        if (!modelToRecords.TryGetValue(normalized, out var recordList))
        {
            recordList = [];
            modelToRecords[normalized] = recordList;
        }

        recordList.Add(new BaseRecordRef(record.FormId, editorId, record.Signature));
    }

    private static byte[]? ReadRecordData(byte[] esmData, bool bigEndian, AnalyzerRecordInfo record)
    {
        var dataStart = (int)record.Offset + EsmParser.MainRecordHeaderSize;
        var dataSize = (int)record.DataSize;

        if (dataStart + dataSize > esmData.Length)
        {
            return null;
        }

        if (record.IsCompressed)
        {
            return EsmParser.DecompressRecordData(esmData.AsSpan(dataStart, dataSize), bigEndian);
        }

        // Return a copy to avoid issues with spans
        var data = new byte[dataSize];
        Array.Copy(esmData, dataStart, data, 0, dataSize);
        return data;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('/', '\\').ToLowerInvariant();
    }
}
