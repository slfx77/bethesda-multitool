using System.Buffers.Binary;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Probes;

/// <summary>
///     Picks the <see cref="RuntimeAcousticSpaceLayout" /> that matches the dump in hand.
///     <para>
///         ASPC cannot be probed by the generic shift probe: <c>GetReadableFields(0x0E)</c> yields
///         nine <c>PointerToForm</c> specs and nothing else, and sliding a run of adjacent pointers by
///         ±4 re-validates almost every slot, so the generic probe's margin gate can never be met. It
///         nonetheless returned a confident-looking <c>+4</c>, which is what put a REGN into the
///         Night-sound slot on every emitted record.
///     </para>
///     <para>
///         The discriminator this probe uses instead is <b>pointee type</b>, which is decisive where a
///         shift score is not: the sound slots must resolve to SOUN and the region slot to REGN, and
///         the three era layouts place those so that a wrong candidate always lands a REGN in a sound
///         slot, a SOUN in the region slot, or a small integer where a pointer belongs. A non-null slot
///         of the wrong type scores <see cref="Violation" />, so wrong candidates go negative rather
///         than merely failing to score — that is what produces a usable margin.
///     </para>
/// </summary>
internal static class RuntimeAcousticSpaceProbe
{
    private const byte AspcFormType = 0x0E;
    private const byte SounFormType = 0x0D;
    private const byte RegnFormType = 0x37;

    private const int MaxSamples = 64;

    /// <summary>Largest candidate struct plus headroom, so every candidate reads in bounds.</summary>
    private const int ReadSize = 112;

    /// <summary>xEdit's ASPC environment-type enum tops out at index 0x1E; allow a little slack.</summary>
    private const uint MaxEnvType = 30;

    /// <summary>Walla is a small population count, never a pointer-sized value.</summary>
    private const uint MaxWallaPop = 1000;

    private const int SoundPoints = 2;
    private const int RegionPoints = 3;
    private const int ScalarPoints = 2;

    /// <summary>A non-null slot holding the wrong kind of value. Negative so wrong eras rank below zero.</summary>
    private const int Violation = -5;

    /// <summary>
    ///     Returns the winning layout, or null when the dump has no readable acoustic spaces.
    ///     Callers must still gate on <c>Margin</c> — with an all-null population every candidate
    ///     ties at zero and the engine returns the first-declared one.
    /// </summary>
    public static RuntimeLayoutProbeResult<RuntimeAcousticSpaceLayout>? Probe(
        RuntimeMemoryContext context, IReadOnlyList<RuntimeEditorIdEntry> allEntries)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(allEntries);

        var samples = new List<byte[]>();
        foreach (var entry in allEntries)
        {
            if (samples.Count >= MaxSamples)
            {
                break;
            }

            if (entry.FormType != AspcFormType || !entry.TesFormOffset.HasValue)
            {
                continue;
            }

            var buffer = context.ReadTesFormBytes(entry, ReadSize);
            if (buffer == null)
            {
                continue;
            }

            // BGSAcousticSpace is TESForm-first (cFormType@4), so the pAllForms entry address is
            // the object base and the FormID anchor sits at +12. Validate it so a stale entry
            // cannot poison the vote.
            if (BinaryUtils.ReadUInt32BE(buffer, 12) == entry.FormId)
            {
                samples.Add(buffer);
            }
        }

        if (samples.Count == 0)
        {
            return null;
        }

        var candidates = RuntimeAcousticSpaceLayout.Candidates
            .Select(layout => new RuntimeLayoutProbeCandidate<RuntimeAcousticSpaceLayout>(layout.Label, layout))
            .ToList();

        return RuntimeLayoutProbeEngine.Probe(
            samples,
            candidates,
            (buffer, candidate) => Score(context, buffer, candidate.Layout),
            "AcousticSpace Probe",
            Logger.Instance.Info);
    }

    private static RuntimeLayoutProbeScore Score(
        RuntimeMemoryContext context, byte[] buffer, RuntimeAcousticSpaceLayout layout)
    {
        var points = 0;
        var max = 0;

        foreach (var offset in layout.SoundOffsets)
        {
            max += SoundPoints;
            points += ScorePointer(context, buffer, offset, SounFormType, SoundPoints);
        }

        max += RegionPoints;
        points += ScorePointer(context, buffer, layout.RegionOffset, RegnFormType, RegionPoints);

        max += ScalarPoints;
        points += ScoreScalar(buffer, layout.EnvTypeOffset, MaxEnvType, ScalarPoints);

        if (layout.WallaPopOffset is { } wallaPop)
        {
            max += ScalarPoints;
            points += ScoreScalar(buffer, wallaPop, MaxWallaPop, ScalarPoints);
        }

        return new RuntimeLayoutProbeScore(points, max);
    }

    /// <summary>NULL is legal in every ASPC pointer slot, so it neither rewards nor penalises.</summary>
    private static int ScorePointer(
        RuntimeMemoryContext context, byte[] buffer, int offset, byte expectedFormType, int reward)
    {
        if (offset + 4 > buffer.Length)
        {
            return Violation;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4)) == 0)
        {
            return 0;
        }

        return context.FollowPointerToFormId(buffer, offset, expectedFormType) is not null ? reward : Violation;
    }

    private static int ScoreScalar(byte[] buffer, int offset, uint max, int reward)
    {
        if (offset + 4 > buffer.Length)
        {
            return Violation;
        }

        return BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4)) <= max ? reward : Violation;
    }
}
