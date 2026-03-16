using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Detects TESNPC layout differences in dump files by sampling real NPC records and
///     scoring candidate core/appearance/FaceGen container combinations.
/// </summary>
internal static class RuntimeNpcLayoutProbe
{
    private const int MaxProbeSamples = 12;
    private const int ProbeReadSize = 640;
    private const int ExactFaceGenScore = 6;
    private const int FaceGenMismatchPenalty = 3;
    private const int CompleteFaceGenBonus = 6;
    private const int SecondarySignalScore = 1;
    private const int AcbsSignalScore = 2;
    private const int CorePointerSignalScore = 2;
    private const int MinConfidenceMargin = ExactFaceGenScore;
    private const int MaxScore = 44;

    public static RuntimeNpcLayoutProbeResult Probe(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> npcEntries)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(npcEntries);

        var defaultLayout = RuntimeNpcLayout.CreateDefault(context.MinidumpInfo);
        var samples = BuildSamples(context, npcEntries);
        if (samples.Count == 0)
        {
            return new RuntimeNpcLayoutProbeResult(defaultLayout, false, 0, 0, 0);
        }

        var candidates = BuildCandidates();
        var fieldReaders = candidates
            .Select(candidate => candidate.Layout)
            .Distinct()
            .ToDictionary(layout => layout, layout => new RuntimeNpcFieldReader(context, layout));

        Action<string>? log = Logger.Instance.IsEnabled(LogLevel.Info)
            ? message => Logger.Instance.Info(message)
            : null;

        var result = RuntimeLayoutProbeEngine.Probe(
            samples,
            candidates,
            (sample, candidate) => ScoreSample(
                context,
                sample,
                fieldReaders[candidate.Layout]),
            "NPC Probe",
            log,
            sample => $"{sample.Entry.EditorId} (FormID 0x{sample.Entry.FormId:X8})",
            false);

        var winner = result.Winner.Layout;
        var isHighConfidence = result.WinnerScore > 0 && result.Margin >= MinConfidenceMargin;

        if (log != null)
        {
            log(
                $"  [NPC Probe] Selected core +{winner.CoreShift}, appearance +{winner.AppearanceShift}, " +
                $"late +{winner.LateAppearanceShift}, facegen {winner.FaceGenMode}, size {winner.ReadSize} " +
                $"(score {result.WinnerScore}, margin {result.Margin}, confidence {(isHighConfidence ? "high" : "low")})");
        }

        return new RuntimeNpcLayoutProbeResult(
            winner,
            isHighConfidence,
            result.WinnerScore,
            result.RunnerUpScore,
            result.SampleCount);
    }

    private static List<RuntimeNpcProbeSample> BuildSamples(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> npcEntries)
    {
        var samples = new List<RuntimeNpcProbeSample>();

        foreach (var entry in npcEntries)
        {
            if (samples.Count >= MaxProbeSamples)
            {
                break;
            }

            if (entry.FormType != 0x2A || entry.FormId == 0x14 || entry.TesFormOffset == null)
            {
                continue;
            }

            var buffer = entry.TesFormPointer.HasValue
                ? context.ReadBytesAtVa(entry.TesFormPointer.Value, ProbeReadSize)
                : context.ReadBytes(entry.TesFormOffset.Value, ProbeReadSize);

            if (buffer == null)
            {
                continue;
            }

            samples.Add(new RuntimeNpcProbeSample(entry, entry.TesFormOffset.Value, buffer));
        }

        return samples;
    }

    private static List<RuntimeLayoutProbeCandidate<RuntimeNpcLayout>> BuildCandidates()
    {
        var candidates = new List<RuntimeLayoutProbeCandidate<RuntimeNpcLayout>>();
        int[] coreShifts = [4, 16];
        int[] appearanceShifts = [0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40];

        foreach (var coreShift in coreShifts)
        {
            foreach (var appearanceShift in appearanceShifts)
            {
                var directLayout = RuntimeNpcLayout.CreateDirect(coreShift, appearanceShift, ProbeReadSize);
                candidates.Add(
                    new RuntimeLayoutProbeCandidate<RuntimeNpcLayout>(
                        $"Core +{directLayout.CoreShift} / Appearance +{directLayout.AppearanceShift} / Direct",
                        directLayout));
            }

            foreach (var appearanceShift in appearanceShifts.Where(shift => shift <= 16))
            {
                var containerLayout =
                    RuntimeNpcLayout.CreatePrimitiveArrayDebug(coreShift, appearanceShift, ProbeReadSize);
                candidates.Add(
                    new RuntimeLayoutProbeCandidate<RuntimeNpcLayout>(
                        $"Core +{containerLayout.CoreShift} / Appearance +{containerLayout.AppearanceShift} / PrimitiveArray",
                        containerLayout));
            }
        }

        return candidates;
    }

    private static RuntimeLayoutProbeScore ScoreSample(
        RuntimeMemoryContext context,
        RuntimeNpcProbeSample sample,
        RuntimeNpcFieldReader fields)
    {
        var buffer = sample.Buffer;
        if (buffer.Length < 16)
        {
            return new RuntimeLayoutProbeScore(0, MaxScore, "buffer too small");
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != sample.Entry.FormId)
        {
            return new RuntimeLayoutProbeScore(0, MaxScore, $"FormID mismatch 0x{formId:X8}");
        }

        var details = new StringBuilder();
        var score = 0;
        var exactFaceGenCount = 0;

        var acbs = RuntimeActorReader.ReadActorBaseStats(buffer, fields.NpcAcbsOffset, sample.Offset);
        if (acbs != null)
        {
            score += AcbsSignalScore;
            details.Append("ACBS, ");
        }

        if (context.FollowPointerToFormId(buffer, fields.NpcScriptPtrOffset, 0x11) != null)
        {
            score += CorePointerSignalScore;
            details.Append("Script, ");
        }

        if (context.FollowPointerToFormId(buffer, fields.NpcRacePtrOffset, 0x0C) != null)
        {
            score += CorePointerSignalScore;
            details.Append("RacePtr, ");
        }

        if (context.FollowPointerToFormId(buffer, fields.NpcClassPtrOffset, 0x07) != null)
        {
            score += CorePointerSignalScore;
            details.Append("ClassPtr, ");
        }

        var aiData = fields.ReadNpcAiData(buffer);
        if (aiData != null)
        {
            score += SecondarySignalScore;
            details.Append("AI, ");
        }

        var special = fields.ReadNpcSpecial(buffer);
        if (special != null)
        {
            score += SecondarySignalScore;
            details.Append("SPECIAL, ");
        }

        var skills = fields.ReadNpcSkills(buffer);
        if (skills != null)
        {
            score += SecondarySignalScore;
            details.Append("Skills, ");
        }

        score += ScoreFaceGen(
            "FGGS",
            50,
            fields.ReadFaceGenMorphArray(buffer, fields.NpcFggsLayout),
            details,
            out var fggsExact);
        score += ScoreFaceGen(
            "FGGA",
            30,
            fields.ReadFaceGenMorphArray(buffer, fields.NpcFggaLayout),
            details,
            out var fggaExact);
        score += ScoreFaceGen(
            "FGTS",
            50,
            fields.ReadFaceGenMorphArray(buffer, fields.NpcFgtsLayout),
            details,
            out var fgtsExact);

        if (fggsExact)
        {
            exactFaceGenCount++;
        }

        if (fggaExact)
        {
            exactFaceGenCount++;
        }

        if (fgtsExact)
        {
            exactFaceGenCount++;
        }

        if (exactFaceGenCount == 3)
        {
            score += CompleteFaceGenBonus;
            details.Append("FaceGenAll, ");
        }

        if (context.FollowPointerToFormId(buffer, fields.NpcHairPtrOffset, 0x0A) != null)
        {
            score += SecondarySignalScore;
            details.Append("Hair, ");
        }

        if (context.FollowPointerToFormId(buffer, fields.NpcEyesPtrOffset, 0x0B) != null)
        {
            score += SecondarySignalScore;
            details.Append("Eyes, ");
        }

        if (context.FollowPointerToFormId(buffer, fields.NpcCombatStylePtrOffset, 0x4A) != null)
        {
            score += SecondarySignalScore;
            details.Append("CSTY, ");
        }

        if (context.FollowPointerToFormId(buffer, fields.NpcOriginalRacePtrOffset, 0x0C) != null)
        {
            score += SecondarySignalScore;
            details.Append("Race, ");
        }

        if (context.FollowPointerToFormId(buffer, fields.NpcFaceNpcPtrOffset, 0x2A) != null)
        {
            score += SecondarySignalScore;
            details.Append("FaceNPC, ");
        }

        var headParts = fields.ReadNpcHeadPartFormIds(buffer);
        if (headParts.Count > 0)
        {
            score += SecondarySignalScore;
            details.Append($"HDPT={headParts.Count}, ");
        }

        if (fields.ReadNpcHairLength(buffer) != null)
        {
            score += SecondarySignalScore;
            details.Append("HairLen, ");
        }

        if (fields.ReadNpcHeight(buffer) != null)
        {
            score += SecondarySignalScore;
            details.Append("Height, ");
        }

        if (fields.ReadNpcWeight(buffer) != null)
        {
            score += SecondarySignalScore;
            details.Append("Weight, ");
        }

        var detailText = details.Length > 2
            ? details.ToString(0, details.Length - 2)
            : "no signals";

        return new RuntimeLayoutProbeScore(score, MaxScore, detailText);
    }

    private static int ScoreFaceGen(
        string label,
        int expectedCount,
        float[]? values,
        StringBuilder details,
        out bool isExact)
    {
        isExact = false;

        if (values == null)
        {
            return 0;
        }

        details.Append(label);
        details.Append('=');
        details.Append(values.Length);
        if (values.Length == expectedCount)
        {
            details.Append(" exact, ");
            isExact = true;
            return ExactFaceGenScore;
        }

        details.Append(" mismatch, ");
        return -FaceGenMismatchPenalty;
    }

    private sealed record RuntimeNpcProbeSample(
        RuntimeEditorIdEntry Entry,
        long Offset,
        byte[] Buffer);
}
