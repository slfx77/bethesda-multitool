using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm;

namespace EsmAnalyzer.Helpers;

/// <summary>
///     Helpers for comparing ESM records between two files (e.g., Xbox vs PC).
/// </summary>
public static class RecordComparisonHelpers
{
    /// <summary>
    ///     Compares two records and returns the differences.
    /// </summary>
    internal static RecordComparison CompareRecords(byte[] xboxFileData, AnalyzerRecordInfo xboxRec, bool xboxBigEndian,
        byte[] pcFileData, AnalyzerRecordInfo pcRec, bool pcBigEndian)
    {
        var result = new RecordComparison();
        const int VhgtDataLength = 1093;

        try
        {
            var xboxData = EsmHelpers.GetRecordData(xboxFileData, xboxRec, xboxBigEndian);
            var pcData = EsmHelpers.GetRecordData(pcFileData, pcRec, pcBigEndian);

            // Check if identical
            if (xboxData.Length == pcData.Length && xboxData.AsSpan().SequenceEqual(pcData))
            {
                result.IsIdentical = true;
                return result;
            }

            // Check if only sizes differ
            result.OnlySizeDiffers = xboxData.Length != pcData.Length;

            // Parse and compare subrecords
            var xboxSubs = EsmRecordParser.ParseSubrecords(xboxData, xboxBigEndian);
            var pcSubs = EsmRecordParser.ParseSubrecords(pcData, pcBigEndian);

            var xboxSubsBySig = xboxSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());
            var pcSubsBySig = pcSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());

            static bool VhgtEquals(byte[] left, byte[] right)
            {
                if (left.Length < VhgtDataLength || right.Length < VhgtDataLength)
                {
                    var len = Math.Min(left.Length, right.Length);
                    return left.AsSpan(0, len).SequenceEqual(right.AsSpan(0, len)) && left.Length == right.Length;
                }

                return left.AsSpan(0, VhgtDataLength).SequenceEqual(right.AsSpan(0, VhgtDataLength));
            }

            foreach (var sig in xboxSubsBySig.Keys.Union(pcSubsBySig.Keys))
            {
                var xboxList = xboxSubsBySig.GetValueOrDefault(sig, []);
                var pcList = pcSubsBySig.GetValueOrDefault(sig, []);

                for (var i = 0; i < Math.Max(xboxList.Count, pcList.Count); i++)
                {
                    var xboxSub = i < xboxList.Count ? xboxList[i] : null;
                    var pcSub = i < pcList.Count ? pcList[i] : null;

                    if (xboxSub == null || pcSub == null)
                    {
                        result.SubrecordDiffs.Add(new SubrecordDiff
                        {
                            Signature = sig,
                            Xbox360Size = xboxSub?.Data.Length ?? 0,
                            PcSize = pcSub?.Data.Length ?? 0,
                            Xbox360Offset = xboxSub?.Offset ?? 0,
                            PcOffset = pcSub?.Offset ?? 0,
                            Xbox360Data = xboxSub?.Data,
                            PcData = pcSub?.Data,
                            DiffType = xboxSub == null ? "Missing in Xbox" : "Missing in PC"
                        });
                        continue;
                    }

                    var isVhgt = sig.Equals("VHGT", StringComparison.OrdinalIgnoreCase);
                    var isEqual = isVhgt
                        ? VhgtEquals(xboxSub.Data, pcSub.Data)
                        : xboxSub.Data.Length == pcSub.Data.Length && xboxSub.Data.AsSpan().SequenceEqual(pcSub.Data);

                    if (!isEqual)
                    {
                        result.SubrecordDiffs.Add(new SubrecordDiff
                        {
                            Signature = sig,
                            Xbox360Size = xboxSub?.Data.Length ?? 0,
                            PcSize = pcSub?.Data.Length ?? 0,
                            Xbox360Offset = xboxSub?.Offset ?? 0,
                            PcOffset = pcSub?.Offset ?? 0,
                            Xbox360Data = xboxSub?.Data,
                            PcData = pcSub?.Data,
                            DiffType = xboxSub.Data.Length != pcSub.Data.Length ? "Size differs" : "Content differs"
                        });
                    }
                }
            }

            if (result.SubrecordDiffs.Count == 0)
            {
                result.IsIdentical = true;
                result.OnlySizeDiffers = false;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"WARN: CompareRecords failed for {xboxRec.Signature} 0x{xboxRec.FormId:X8} at A:0x{xboxRec.Offset:X8} B:0x{pcRec.Offset:X8}: {ex.Message}");
            result.SubrecordDiffs.Add(new SubrecordDiff
            {
                Signature = "ERROR",
                Xbox360Size = 0,
                PcSize = 0,
                DiffType = ex.Message
            });
        }

        return result;
    }
}
