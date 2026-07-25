using System.Buffers.Binary;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Probes;

/// <summary>
///     Probes the start offset of the AMMO_DATA sub-block (fSpeed / iFlags / pProjectile) inside the
///     runtime TESObjectAMMO struct. That block's position drifts per build — empirically 172 (Debug /
///     early-mid Release Beta), 184 (late Release Beta), and 188 (Release MemDebug / Apr scenes) across the
///     Dec 2009 – Apr 2010 dumps — while the header/Value region is stable (Value stays at +140). The
///     PDB-derived constant (fSpeed = 168 + shift = 184) is therefore correct only for late-April Beta and
///     reads a denormal Speed / 255 Flags on every other build. There is no clean split by
///     <c>DetectBuildType</c> (Release Beta spans 172 and 184), so this is an empirical per-DMP probe
///     mirroring <see cref="RuntimeRefrReader.ProbeIsEarlyBuild" />: for each candidate start D a sample
///     scores when fSpeed@D is a plausible non-zero float, iFlags@D+4 fits the two real FNV AMMO flag bits
///     (&lt;= 3), and pProjectile@D+8 is null or a plausible Xbox 360 VA. This three-field structural
///     signature was validated to re-win the value-located offset on every tested build era.
/// </summary>
internal static class RuntimeAmmoDataProbe
{
    private const byte AmmoFormType = 0x29;
    private const int MaxSamples = 40;
    private const int ReadSize = 224; // covers the widest candidate + pProjectile (200 + 12)

    // Absolute AMMO_DATA start offsets to try, bracketing the observed 172–188 range with margin.
    private static readonly int[] CandidateOffsets = [164, 168, 172, 176, 180, 184, 188, 192, 196, 200];

    /// <summary>
    ///     Returns the winning AMMO_DATA start offset (and score/margin/samples), or null when there are no
    ///     AMMO structs to sample or no candidate scored above zero (caller then keeps the layout default).
    /// </summary>
    public static RuntimeLayoutProbeResult<int>? Probe(
        RuntimeMemoryContext context, IReadOnlyList<RuntimeEditorIdEntry> allEntries)
    {
        var samples = new List<byte[]>();
        foreach (var entry in allEntries)
        {
            if (samples.Count >= MaxSamples)
            {
                break;
            }

            if (entry.FormType != AmmoFormType || entry.TesFormOffset is not { } offset ||
                offset + ReadSize > context.FileSize)
            {
                continue;
            }

            var buffer = new byte[ReadSize];
            try
            {
                context.Accessor.ReadArray(offset, buffer, 0, ReadSize);
            }
            catch
            {
                continue;
            }

            // Validate the TESForm FormID at +12 so we only score genuine AMMO structs.
            if (BinaryUtils.ReadUInt32BE(buffer, 12) == entry.FormId)
            {
                samples.Add(buffer);
            }
        }

        if (samples.Count == 0)
        {
            return null;
        }

        var candidates = CandidateOffsets
            .Select(d => new RuntimeLayoutProbeCandidate<int>($"AmmoData@{d}", d))
            .ToList();

        var result = RuntimeLayoutProbeEngine.Probe(
            samples,
            candidates,
            (buffer, candidate) => Score(buffer, candidate.Layout),
            "AmmoData Probe",
            Logger.Instance.Info);

        return result.WinnerScore > 0 ? result : null;
    }

    private static RuntimeLayoutProbeScore Score(byte[] buffer, int d)
    {
        if (d + 12 > buffer.Length)
        {
            return new RuntimeLayoutProbeScore(0, 1);
        }

        var speed = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(d, 4)));
        var flags = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(d + 4, 4));
        var projectile = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(d + 8, 4));

        var speedOk = float.IsFinite(speed) && !float.IsSubnormal(speed) && speed > 0 && speed <= 100000;
        var flagsOk = flags <= 3;                                   // FNV AMMO defines only 2 flag bits
        var projectileOk = projectile == 0 || projectile >= 0x40000000; // null or a plausible Xbox 360 VA
        return new RuntimeLayoutProbeScore(speedOk && flagsOk && projectileOk ? 1 : 0, 1);
    }
}
