using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Probes;

/// <summary>
///     Probes a known DIAL runtime struct to determine the correct dump shift by scoring
///     candidate +0/+4/+8/+16 layouts against real DIAL field signals (FullName, type,
///     flags, priority, topic count).
/// </summary>
internal static class RuntimeDialLayoutProbe
{
    /// <summary>
    ///     Probe a known DIAL runtime struct to determine the correct dump shift.
    ///     Tries +0, +4, +8, +16 shift hypotheses and logs which one produces valid data.
    ///     Returns the best shift value, or -1 if none worked.
    /// </summary>
    public static int Probe(RuntimeMemoryContext context, RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null)
        {
            return -1;
        }

        var offset = entry.TesFormOffset.Value;
        var readSize = 96; // Read extra bytes to accommodate larger shifts
        if (offset + readSize > context.FileSize)
        {
            return -1;
        }

        var buffer = new byte[readSize];
        try
        {
            context.Accessor.ReadArray(offset, buffer, 0, readSize);
        }
        catch
        {
            return -1;
        }

        // Validate FormID at +12 (no shift — standard TESForm header)
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return -1;
        }

        var sample = new DialProbeSample(entry, offset, buffer);
        var candidates = new List<RuntimeLayoutProbeCandidate<int>>
        {
            new("Shift +0", 0),
            new("Shift +4", 4),
            new("Shift +8", 8),
            new("Shift +16", 16)
        };

        var result = RuntimeLayoutProbeEngine.Probe(
            [sample],
            candidates,
            (probeSample, candidate) => ScoreDialCandidate(context, probeSample, candidate.Layout),
            "DIAL Probe",
            Logger.Instance.Info,
            probeSample =>
                $"Entry: {probeSample.Entry.EditorId} (FormID 0x{probeSample.Entry.FormId:X8}), TesFormOffset=0x{probeSample.Offset:X}",
            true);

        return result.WinnerScore > 0 ? result.Winner.Layout : -1;
    }

    private static RuntimeLayoutProbeScore ScoreDialCandidate(
        RuntimeMemoryContext context,
        DialProbeSample sample,
        int shift)
    {
        var score = 0;
        var details = new StringBuilder();

        // Check BSStringT for FullName at PDB+28+shift
        var bstOff = 28 + shift;
        if (bstOff + 8 <= sample.Buffer.Length)
        {
            var pStr = BinaryUtils.ReadUInt32BE(sample.Buffer, bstOff);
            var sLen = BinaryUtils.ReadUInt16BE(sample.Buffer, bstOff + 4);
            var strValid = pStr != 0 && sLen > 0 && sLen < 256 && context.IsValidPointer(pStr);
            if (strValid)
            {
                var name = context.ReadBsStringT(sample.Offset, bstOff);
                if (name != null)
                {
                    details.Append($"FullName=\"{name}\" OK, ");
                    score += 3;
                }
                else
                {
                    details.Append("FullName=<ptr valid but string unreadable>, ");
                    score += 1;
                }
            }
            else
            {
                details.Append($"FullName=<invalid ptr=0x{pStr:X8} len={sLen}>, ");
            }
        }

        // Check m_Data.type at PDB+36+shift (should be 0-7)
        var typeOff = 36 + shift;
        if (typeOff < sample.Buffer.Length)
        {
            var topicType = sample.Buffer[typeOff];
            if (topicType <= 7)
            {
                details.Append($"type={topicType} OK, ");
                score += 2;
            }
            else
            {
                details.Append($"type={topicType} FAIL, ");
            }
        }

        // Check m_Data.cFlags at PDB+37+shift (should be 0-3, only bits 0-1 used)
        var flagsOff = 37 + shift;
        if (flagsOff < sample.Buffer.Length)
        {
            var flags = sample.Buffer[flagsOff];
            if (flags <= 3)
            {
                details.Append($"flags={flags} OK, ");
                score += 1;
            }
            else
            {
                details.Append($"flags=0x{flags:X2} FAIL, ");
            }
        }

        // Check m_fPriority at PDB+40+shift (should be a reasonable float, typically 50.0)
        var priorityOff = 40 + shift;
        if (priorityOff + 4 <= sample.Buffer.Length)
        {
            var priority = BinaryUtils.ReadFloatBE(sample.Buffer, priorityOff);
            if (RuntimeMemoryContext.IsNormalFloat(priority) && priority >= 0 && priority <= 200)
            {
                details.Append($"priority={priority:F1} OK, ");
                score += 2;
            }
            else
            {
                details.Append($"priority={priority:F1} FAIL, ");
            }
        }

        // Check m_uiTopicCount at PDB+68+shift (should be a reasonable count, 0-10000)
        var countOff = 68 + shift;
        if (countOff + 4 <= sample.Buffer.Length)
        {
            var count = BinaryUtils.ReadUInt32BE(sample.Buffer, countOff);
            if (count <= 10000)
            {
                details.Append($"topicCount={count} OK");
                score += 1;
            }
            else
            {
                details.Append($"topicCount={count} FAIL");
            }
        }

        return new RuntimeLayoutProbeScore(score, 9, details.ToString());
    }

    private sealed record DialProbeSample(
        RuntimeEditorIdEntry Entry,
        long Offset,
        byte[] Buffer);
}
