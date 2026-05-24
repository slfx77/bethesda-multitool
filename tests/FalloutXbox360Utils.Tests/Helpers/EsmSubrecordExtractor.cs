using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     Minimal ESM byte extractor for offset-cross-reference tests. Opens a raw
///     ESM file, scans for main records, and exposes <see cref="GetSubrecordBytes" />
///     to pull the literal payload bytes of a named subrecord on a given FormID.
///     Cached across the whole test run (one parse per ESM path).
///
///     Compressed records (flag 0x00040000) are decompressed on demand via
///     <see cref="EsmHelpers.DecompressZlib" /> — necessary because NPC records
///     in Fallout NV are almost universally compressed.
///
///     Decompressed payloads are cached per-FormID after first access to avoid
///     re-decompressing on repeated lookups during a test run.
///
///     Intended for the Phase 1B.10 empirical offset validator. NOT a general
///     ESM API — does not handle XXXX large-size prefixes or any kind of schema.
///     Just raw bytes.
/// </summary>
internal sealed class EsmSubrecordExtractor
{
    private static readonly ConcurrentDictionary<string, Lazy<EsmSubrecordExtractor>> Cache = new();

    private readonly Dictionary<uint, DetectedMainRecord> _byFormId;
    private readonly byte[] _data;

    /// <summary>Per-FormID cache of decompressed payload bytes (the bytes between header and end-of-record).</summary>
    private readonly ConcurrentDictionary<uint, byte[]?> _payloadCache = new();

    private EsmSubrecordExtractor(byte[] data, Dictionary<uint, DetectedMainRecord> byFormId)
    {
        _data = data;
        _byFormId = byFormId;
    }

    /// <summary>
    ///     Returns a previously-parsed extractor for <paramref name="esmPath" />, or
    ///     parses + caches a new one. Thread-safe.
    /// </summary>
    public static EsmSubrecordExtractor LoadCached(string esmPath)
    {
        return Cache.GetOrAdd(
            esmPath,
            p => new Lazy<EsmSubrecordExtractor>(() => Load(p))).Value;
    }

    private static EsmSubrecordExtractor Load(string esmPath)
    {
        var data = File.ReadAllBytes(esmPath);
        var scan = EsmRecordScanner.ScanForRecords(data);
        var byFormId = new Dictionary<uint, DetectedMainRecord>();
        foreach (var record in scan.MainRecords)
        {
            // First occurrence wins. Same FormID can appear multiple times
            // (overrides in master/plugin chains); we only care about the first
            // authoritative copy.
            byFormId.TryAdd(record.FormId, record);
        }

        return new EsmSubrecordExtractor(data, byFormId);
    }

    /// <summary>
    ///     Does the ESM contain a record for this FormID?
    /// </summary>
    public bool ContainsFormId(uint formId)
    {
        return _byFormId.ContainsKey(formId);
    }

    /// <summary>
    ///     Get the record-type tag (e.g. "NPC_", "TERM") for a FormID, or null if not present.
    /// </summary>
    public string? GetRecordType(uint formId)
    {
        return _byFormId.TryGetValue(formId, out var record) ? record.RecordType : null;
    }

    /// <summary>
    ///     Returns the underlying <see cref="DetectedMainRecord" /> for diagnostics.
    /// </summary>
    public DetectedMainRecord? GetRecord(uint formId)
    {
        return _byFormId.TryGetValue(formId, out var record) ? record : null;
    }

    /// <summary>
    ///     Diagnostic: returns the first N subrecord (sig, size) pairs walked from
    ///     this record's decompressed payload. Used to debug walker issues.
    /// </summary>
    public List<(string Sig, int Size)> DiagnosticListSubrecords(uint formId, int maxCount = 30)
    {
        var result = new List<(string, int)>();
        var payload = GetRecordPayload(formId);
        if (payload == null)
        {
            return result;
        }

        var pos = 0;
        while (pos + 6 <= payload.Length && result.Count < maxCount)
        {
            var subSig = ReadSignature(payload, pos);
            int subSize = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(pos + 4, 2));
            result.Add((subSig, subSize));
            var payloadStart = pos + 6;
            if (payloadStart + subSize > payload.Length)
            {
                result.Add(("<OVERRUN>", payload.Length - payloadStart));
                break;
            }

            pos = payloadStart + subSize;
        }

        return result;
    }

    /// <summary>
    ///     Returns the raw bytes of the first occurrence of subrecord
    ///     <paramref name="sig" /> inside the record with <paramref name="formId" />,
    ///     or null if the FormID is missing or doesn't contain the subrecord.
    ///
    ///     Compressed records are transparently decompressed. Returned bytes are
    ///     the subrecord payload only — no 4-byte sig, no 2-byte size header.
    /// </summary>
    public byte[]? GetSubrecordBytes(uint formId, string sig)
    {
        if (sig.Length != 4)
        {
            throw new ArgumentException($"Subrecord signature must be 4 chars; got '{sig}'", nameof(sig));
        }

        var payload = GetRecordPayload(formId);
        if (payload == null)
        {
            return null;
        }

        var pos = 0;
        while (pos + 6 <= payload.Length)
        {
            var subSig = ReadSignature(payload, pos);
            int subSize = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(pos + 4, 2));

            var payloadStart = pos + 6;
            if (payloadStart + subSize > payload.Length)
            {
                return null;
            }

            if (subSig == sig)
            {
                var bytes = new byte[subSize];
                Array.Copy(payload, payloadStart, bytes, 0, subSize);
                return bytes;
            }

            pos = payloadStart + subSize;
        }

        return null;
    }

    /// <summary>
    ///     Reads a 4-byte subrecord/record signature from the Xbox 360 ESM byte stream.
    ///     Xbox 360 ESMs store signatures byte-reversed (e.g. "EDID" is laid out as
    ///     bytes 'D','I','D','E'); the engine read them as uint32 big-endian for
    ///     compare-equality with their PC-native uint32 values. We just reverse the
    ///     4 bytes back into normal character order for string comparison.
    /// </summary>
    private static string ReadSignature(byte[] payload, int pos)
    {
        Span<byte> reversed = stackalloc byte[4];
        reversed[0] = payload[pos + 3];
        reversed[1] = payload[pos + 2];
        reversed[2] = payload[pos + 1];
        reversed[3] = payload[pos];
        return Encoding.ASCII.GetString(reversed);
    }

    private byte[]? GetRecordPayload(uint formId)
    {
        return _payloadCache.GetOrAdd(formId, fid =>
        {
            if (!_byFormId.TryGetValue(fid, out var record))
            {
                return null;
            }

            var headerEnd = (int)(record.Offset + 24);
            if (headerEnd + (int)record.DataSize > _data.Length)
            {
                return null;
            }

            var rawData = _data.AsSpan(headerEnd, (int)record.DataSize);
            if (!record.IsCompressed)
            {
                return rawData.ToArray();
            }

            try
            {
                var decompressedSize = record.IsBigEndian
                    ? BinaryUtils.ReadUInt32BE(rawData)
                    : BinaryUtils.ReadUInt32LE(rawData);
                return EsmHelpers.DecompressZlib(rawData[4..].ToArray(), (int)decompressedSize);
            }
            catch (InvalidDataException)
            {
                // Some records may have non-standard compression payloads (legacy
                // Xbox 360 ESMs occasionally have malformed entries). Treat as
                // missing rather than failing the whole test run.
                return null;
            }
        });
    }
}
