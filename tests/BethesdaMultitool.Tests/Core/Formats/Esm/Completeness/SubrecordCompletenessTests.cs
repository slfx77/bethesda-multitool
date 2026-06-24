using BethesdaMultitool.Core.Formats.Esm.Analysis;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Completeness;

/// <summary>
///     Completeness ratchet for record modeling. Runs <see cref="EsmCoverageAnalyzer" /> over a real
///     master ESM and counts the subrecord shapes the tool can only keep as non-intentional raw bytes —
///     i.e. genuine modeling gaps (the analyzer sees every subrecord from the scan, including ones the
///     typed handlers silently drop). The count is pinned so it can only go DOWN: every record/subrecord
///     we model lowers it toward zero ("everything properly modeled; gaps flagged"). The xEdit schema
///     oracle (tools/EsmSchemaGen) supplies the expected structure for each gap when fixing them.
///     Skipped when the game master isn't present (set BETHESDA_TEST_DATA_ROOT or have Sample/ESM).
/// </summary>
public class SubrecordCompletenessTests
{
    // Calibrated to the current count, then ratcheted DOWN as records get fully modeled (never up). FNV
    // is the reference game we complete first. Each modeled shape lowers this toward zero. Remaining
    // work-list (29): IDLE/CTDA + PACK/CTDA + QUST/CTDA (20/24-byte condition variants), FACT/CNAM +
    // FACT/DATA, ARMA/DNAM, ARMO/DNAM, AMMO/DAT2, PACK/PKDT, WATR/DATA + WATR/DNAM, EFSH/DATA variants,
    // WTHR/NAM0 + WTHR/PNAM, WEAP/VATS(16), REFR/XLOC(12), *./MODB, QUST/DATA, DIAL/DATA, TERM/DNAM.
    // Closed so far (oracle-grounded): SOUN/SNDX, CELL/XCLC(8), IDLM+PACK/IDLC, REFR/XMBP, MESG/NAM0-9,
    // *./MODB (intentional-raw), FACT/DATA+CNAM, TERM/DNAM, DIAL/DATA, QUST/DATA, PACK/PKPT.
    private const int FnvRawGapBaseline = 18;

    [Fact]
    public void Fnv_Master_Has_No_New_Unmodeled_Subrecord_Shapes()
    {
        var esm = ResolveEsm("FalloutNV.esm", Path.Combine("Sample", "ESM", "pc_final", "FalloutNV.esm"));
        Assert.SkipUnless(esm is not null,
            "FalloutNV.esm not found (set BETHESDA_TEST_DATA_ROOT or place it under Sample/ESM/pc_final).");

        var result = EsmCoverageAnalyzer.AnalyzeFile(esm!);
        var gaps = result.Subrecords
            .Where(r => r.UsesRawByteArray && !r.IsIntentionalRaw)
            .OrderByDescending(r => r.Count)
            .ToList();

        var detail = string.Join(
            Environment.NewLine,
            gaps.Take(40).Select(g => $"  {g.RecordType}/{g.Subrecord} len={g.DataLength} x{g.Count} [{g.Classification}]"));

        Assert.True(gaps.Count <= FnvRawGapBaseline,
            $"FNV unmodeled subrecord shapes = {gaps.Count} (baseline {FnvRawGapBaseline}).{Environment.NewLine}" +
            $"Top gaps:{Environment.NewLine}{detail}");
    }

    private static string? ResolveEsm(string fileName, string repoRelativePath)
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, fileName)))
        {
            return Path.Combine(root, fileName);
        }

        var repo = FindRepoRoot();
        if (repo is not null)
        {
            var candidate = Path.Combine(repo, repoRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "Sample", "ESM")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
