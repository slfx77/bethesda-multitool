using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Generates GECK-style text reports for Faction, Reputation, and Challenge records.</summary>
internal static class GeckFactionWriter
{
    internal static void AppendFactionsSection(StringBuilder sb, List<FactionRecord> factions,
        FormIdResolver resolver)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Factions ({factions.Count})");

        foreach (var faction in factions.OrderBy(f => f.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "FACT", faction.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(faction.FormId)}");
            sb.AppendLine($"Editor ID:      {faction.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {faction.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(faction.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{faction.Offset:X8}");

            if (faction.IsHiddenFromPlayer || faction.AllowsEvil || faction.AllowsSpecialCombat)
            {
                sb.AppendLine();
                sb.AppendLine("Flags:");
                if (faction.IsHiddenFromPlayer) sb.AppendLine("  - Hidden From Player");
                if (faction.AllowsEvil) sb.AppendLine("  - Allows Evil");
                if (faction.AllowsSpecialCombat) sb.AppendLine("  - Allows Special Combat");
            }

            if (faction.Ranks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Ranks:");
                foreach (var rank in faction.Ranks)
                {
                    var title = rank.MaleTitle ?? rank.FemaleTitle ?? "(unnamed)";
                    sb.AppendLine($"  [{rank.RankNumber}] {title}");
                }
            }

            if (faction.Relations.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Relations:");
                foreach (var rel in faction.Relations)
                {
                    sb.AppendLine($"  - {resolver.FormatFull(rel.FactionFormId)}: {rel.Modifier:+0;-0}");
                }
            }
        }
    }

    /// <summary>
    ///     Generate a report for Factions only.
    /// </summary>
    public static string GenerateFactionsReport(List<FactionRecord> factions,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendFactionsSection(sb, factions, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendReputationsSection(StringBuilder sb, List<ReputationRecord> reputations)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Reputations ({reputations.Count})");
        sb.AppendLine();

        sb.AppendLine($"Total Reputations: {reputations.Count:N0}");
        sb.AppendLine();
        sb.AppendLine($"  {"Name",-40} {"Positive",10} {"Negative",10}  {"FormID"}");
        sb.AppendLine($"  {new string('\u2500', 76)}");

        foreach (var rep in reputations.OrderBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            var name = rep.FullName ?? rep.EditorId ?? GeckReportGenerator.FormatFormId(rep.FormId);
            sb.AppendLine(
                $"  {GeckReportGenerator.Truncate(name, 40),-40} {rep.PositiveValue,10:F2} {rep.NegativeValue,10:F2}  [{GeckReportGenerator.FormatFormId(rep.FormId)}]");
        }

        sb.AppendLine();
    }

    public static string GenerateReputationsReport(List<ReputationRecord> reputations)
    {
        var sb = new StringBuilder();
        AppendReputationsSection(sb, reputations);
        return sb.ToString();
    }

    internal static void AppendChallengesSection(StringBuilder sb, List<ChallengeRecord> challenges,
        FormIdResolver resolver)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Challenges ({challenges.Count})");
        sb.AppendLine();

        var byType = challenges.GroupBy(c => c.TypeName).OrderByDescending(g => g.Count()).ToList();
        sb.AppendLine($"Total Challenges: {challenges.Count:N0}");
        sb.AppendLine("By Type:");
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key,-30} {group.Count(),5:N0}");
        }

        sb.AppendLine();

        foreach (var chal in challenges.OrderBy(c => c.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  CHALLENGE: {chal.EditorId ?? "(none)"} \u2014 {chal.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {GeckReportGenerator.FormatFormId(chal.FormId)}");
            sb.AppendLine($"  Type:        {chal.TypeName}");
            sb.AppendLine($"  Threshold:   {chal.Threshold}");
            if (chal.Interval != 0)
            {
                sb.AppendLine($"  Interval:    {chal.Interval}");
            }

            if (chal.Flags != 0)
            {
                sb.AppendLine($"  Flags:       0x{chal.Flags:X8}");
            }

            if (!string.IsNullOrEmpty(chal.Description))
            {
                sb.AppendLine($"  Description: {chal.Description}");
            }

            if (chal.Value1 != 0)
            {
                sb.AppendLine($"  Value1:      {chal.Value1}");
            }

            if (chal.Value2 != 0)
            {
                sb.AppendLine($"  Value2:      {chal.Value2}");
            }

            if (chal.Value3 != 0)
            {
                sb.AppendLine($"  Value3:      {chal.Value3}");
            }

            if (chal.Script != 0)
            {
                sb.AppendLine($"  Script:      {resolver.FormatFull(chal.Script)}");
            }

            sb.AppendLine();
        }
    }

    public static string GenerateChallengesReport(List<ChallengeRecord> challenges,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendChallengesSection(sb, challenges, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }
}
