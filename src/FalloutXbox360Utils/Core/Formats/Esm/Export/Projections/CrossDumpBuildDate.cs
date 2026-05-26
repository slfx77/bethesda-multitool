using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Resolves the chronological build date for a comparison source. DMPs prefer the PE
///     TimeDateStamp from the loaded game module; ESMs use <see cref="EsmBuildDateExtractor" />;
///     everything else falls back to the file's last-write timestamp.
/// </summary>
/// <remarks>
///     Extracted from the old <c>CrossDumpAggregator.Aggregate</c> ordering pre-pass so the
///     streaming pipeline (<c>CrossDumpComparisonPipeline.WriteHtmlByRecordTypeAsync</c>) can
///     compute the date once per source and store it on <see cref="CrossDumpSourceProjection" />.
/// </remarks>
internal static class CrossDumpBuildDate
{
    internal static (DateTime BuildDateUtc, string DateSource) Resolve(
        string filePath,
        MinidumpInfo? minidumpInfo)
    {
        var fileInfo = new FileInfo(filePath);
        var fileDate = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.MinValue;
        var isDmp = filePath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);

        if (minidumpInfo != null)
        {
            var gameModule = minidumpInfo.FindGameModule();
            if (gameModule != null && gameModule.TimeDateStamp != 0)
            {
                return (
                    DateTimeOffset.FromUnixTimeSeconds(gameModule.TimeDateStamp).UtcDateTime,
                    "PE TimeDateStamp");
            }

            return (fileDate, "file timestamp");
        }

        if (!isDmp)
        {
            var esmDate = EsmBuildDateExtractor.Extract(filePath);
            return (esmDate.BuildDateUtc, esmDate.Source);
        }

        return (fileDate, "file timestamp");
    }
}
