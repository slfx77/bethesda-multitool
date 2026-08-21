using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Papyrus;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Papyrus;

/// <summary>Opt-in, hash-pinned structural coverage for the installed Fallout 76 PEX corpus.</summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class PexFallout76RetailArchiveTests
{
    private const string ExpectedSha256 =
        "55EB81033476842FB7C528E072F5FDCB3D09C5E594845540059018CA3DF183CD";

    private const int ExpectedScriptCount = 7_194;

    [Fact]
    public void MiscClientArchive_StructurallyParsesAndProducesAnnotatedDecompilerOutput()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = ResolveArchivePath();
        Assert.SkipUnless(archivePath is not null,
            "Fallout 76 MiscClient.ba2 not found (set BETHESDA_TEST_DATA_ROOT or install Fallout 76).");

        using (var stream = File.OpenRead(archivePath!))
        {
            Assert.Equal(ExpectedSha256, Convert.ToHexString(SHA256.HashData(stream)));
        }

        using var archive = PexArchiveReader.Open(archivePath!);
        Assert.Equal(ExpectedScriptCount, archive.Entries.Count);
        var sawObjectTail = false;
        var sawUnmappedFunctionFlags = false;
        var sawDecompilerAnnotation = false;
        foreach (var entry in archive.Entries)
        {
            var bytes = archive.Extract(entry);
            var file = PexParser.Parse(bytes);
            Assert.Equal(PexGameId.Fallout76, file.Header.GameId);
            Assert.Equal(bytes.Length, file.BytesConsumed);
            foreach (var obj in file.Objects)
            {
                Assert.True(obj.HasFallout76TrailingStateReferenceTable,
                    $"{entry.VirtualPath}: retail object omitted its trailing state-reference table.");
                var stateNameIndices = obj.States.Select(state => state.Name.Index).ToHashSet();
                foreach (var reference in obj.Fallout76TrailingStateReferences)
                {
                    Assert.Contains(reference.Index, stateNameIndices);
                }

                sawObjectTail |= !obj.Fallout76TrailingStateReferences.IsDefaultOrEmpty;
                foreach (var state in obj.States)
                {
                    sawUnmappedFunctionFlags |= state.Functions.Any(function =>
                        function.UnmappedFlags != 0);
                }
            }

            // This checks the dialect present in this pinned corpus and that the conservative
            // source renderer exposes raw flag metadata. It does not claim semantics for Fallout
            // 76's unmapped function bits or retail coverage of every publicly documented opcode.
            var source = PexDecompiler.Decompile(file);
            Assert.NotEmpty(source);
            sawDecompilerAnnotation |= source.Contains(
                "; Unmapped PEX function flag bits:",
                StringComparison.Ordinal);
        }

        Assert.True(sawObjectTail, "The pinned corpus should exercise non-empty object tails.");
        Assert.True(sawUnmappedFunctionFlags,
            "The pinned corpus should exercise non-core raw function flag bits.");
        Assert.True(sawDecompilerAnnotation,
            "The decompiler must visibly retain the unmapped function-flag caveat.");
    }

    private static string? ResolveArchivePath()
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var candidates = new[]
            {
                Path.Combine(root, "SeventySix - MiscClient.ba2"),
                Path.Combine(root, "Data", "SeventySix - MiscClient.ba2"),
                Path.Combine(root, "Fallout76", "Data", "SeventySix - MiscClient.ba2")
            };
            var configured = candidates.FirstOrDefault(File.Exists);
            if (configured is not null)
            {
                return configured;
            }
        }

        var installed =
            RealAssetPaths.SteamGameFile("Fallout76", @"Data\SeventySix - MiscClient.ba2");
        return File.Exists(installed) ? installed : null;
    }
}