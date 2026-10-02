using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.RealAsset.NifModelOracleSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Hop A4 (design section 7.2; plan section 6, slices 6 and 11) over every model of the cut-1a cover manifest, both
///     byte orders: each texture the reader's typed materials reference is read through
///     <see cref="BethesdaTextureCompanions" /> over the build's own Data folder (X360, PS3, FO3, the FNV Steam Final
///     build or Skyrim, as the manifest's primary source says), and the image's original SHA-256 must equal the digest
///     the independent Python BSA reader computed for the same virtual path in the same folder (the expectation's
///     <c>textures.entries</c>, keyed by the reader's lookup key; the resolved path is the key or its <c>.ddx</c>
///     fallback). A texture the reader reports missing must be one the Python walk did not find either, and an Xbox
///     specular companion the reader did not add must be one the walk did not find.
/// </summary>
/// <remarks>
///     <para>
///         The Python side transcribes the reader's lookup rule (normalize, loose files over archives in file-name
///         order, <c>.dds</c> to <c>.ddx</c>, the <c>_n</c> to <c>_s</c> companion) so both sides ask for the same
///         path; the bytes and their digest come from the independent reader. A DDX keeps its original bytes whatever
///         the relayout does, so the comparison is on the original payload; PS3 textures (a <c>3PZO</c> container the
///         reader keeps as opaque bytes) compare the same way.
///     </para>
///     <para>
///         Control: a copy of one resolved texture with one byte changed, mounted as a loose layer above the real Data
///         folder, yields an image whose SHA-256 differs from the expectation and equals the mutated bytes' digest.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifModelTextureOracleTests
{
    [Theory]
    [MemberData(nameof(Cut1aCoverManifest.Rows), MemberType = typeof(Cut1aCoverManifest))]
    public void TextureBytes_MatchTheIndependentBsaReader(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1aCoverManifest.Require(sha256);
        Assert.Equal(entry, file.Entry);
        var expectation = Cut1aProbeExpectations.Require(file);
        Assert.SkipWhen(file.IsDeclinedControl || expectation["declined"] is not null,
            $"{file}: declined by the probe; hop A2 asserts the reader declines it too.");
        Assert.SkipWhen(file.IsAnimationStream, $"{file}: a .kf animation stream; it binds no texture.");
        var textures = expectation["textures"] as JsonObject;
        Assert.SkipWhen(textures is null, $"{file}: the expectation names no texture build for this file.");
        var build = Text(textures!["build"])!;
        var folder = Cut1aFixtureResolver.DataFolder(build);
        Assert.SkipWhen(folder is null, RealAssetPaths.SkipMessage($"the Data folder {build}"));
        var bytes = Cut1aFixtureResolver.Require(file);

        var entries = textures["entries"]!.AsObject();
        var byPath = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (_, value) in entries)
        {
            if (Text(value!["path"]) is { } path)
            {
                byPath.TryAdd(path, value.AsObject());
            }
        }

        var document = ReadWithCompanions(bytes, file, folder!);
        var rows = IndexedRows(document, NifModelTextureSource.TextureKind);
        Assert.SkipWhen(rows.Count == 0, $"{file}: the reader's materials reference no texture; nothing to compare.");

        var mismatches = new List<string>();
        var resolved = 0;
        var missingOnBothSides = 0;
        (int Image, string Path, string Sha256)? control = null;
        foreach (var (index, payload) in rows)
        {
            var name = Text(payload["name"]);
            var resolution = payload["resolution"]!.AsObject();
            var outcome = Text(resolution["outcome"]);
            var where = $"{file} image {index} '{name}'";
            switch (outcome)
            {
                case "resolved":
                    var path = Text(resolution["path"])!;
                    var actual = document.Images[index].Source!.Original!.Sha256;
                    if (!byPath.TryGetValue(path, out var expected))
                    {
                        mismatches.Add($"{where}: resolved at {path}, which the independent walk of {build} does not reach.");
                    }
                    else if (Text(expected["sha256"]) is not { } digest)
                    {
                        mismatches.Add($"{where}: the independent reader could not read {path} ({Text(expected["error"])}).");
                    }
                    else
                    {
                        if (!string.Equals(digest, actual, StringComparison.Ordinal))
                        {
                            mismatches.Add($"{where}: SHA-256 {actual} vs the independent reader's {digest} at {path}.");
                        }

                        if (!string.Equals(Text(resolution["sha256"]), actual, StringComparison.Ordinal))
                        {
                            mismatches.Add($"{where}: the row's digest {Text(resolution["sha256"])} differs from the image's {actual}.");
                        }

                        CompareLayer(expected, Text(resolution["provenance"]), where, mismatches);
                        control ??= (index, path, digest);
                        resolved++;
                    }

                    foreach (var alias in (payload["sameContent"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    {
                        var aliasPath = Text(alias["path"])!;
                        if (!byPath.TryGetValue(aliasPath, out var aliasExpected) ||
                            !string.Equals(Text(aliasExpected["sha256"]), actual, StringComparison.Ordinal))
                        {
                            mismatches.Add($"{where}: alias {aliasPath} does not carry the same digest on the independent side.");
                        }
                    }

                    break;
                case "missing":
                case "ambiguous":
                case "unread":
                    var key = Text(resolution["lookupKey"])!;
                    if (!entries.TryGetPropertyValue(key, out var missing) || missing is not JsonObject missingEntry)
                    {
                        mismatches.Add($"{where}: the reader looked up '{key}', which the probe never authored.");
                    }
                    else if (Text(missingEntry["path"]) is { } found)
                    {
                        mismatches.Add($"{where}: {outcome} for the reader, but the independent walk finds it at {found}.");
                    }
                    else
                    {
                        missingOnBothSides++;
                    }

                    break;
                default:
                    // Embedded pixel data (later-cut 2) has no bytes to compare.
                    break;
            }

            if (payload["xboxSpecularCompanion"] is JsonObject companion)
            {
                CompareCompanion(entries, companion, where, mismatches);
            }
        }

        Assert.True(mismatches.Count == 0,
            $"{file}: {mismatches.Count} texture mismatch(es):{Environment.NewLine}{string.Join(Environment.NewLine, mismatches)}");
        Assert.SkipWhen(control is null,
            $"{file}: every referenced texture is missing on both sides ({missingOnBothSides}); no bytes to compare.");
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{file}: {resolved} image(s) match the independent reader in {build}; {missingOnBothSides} missing on both sides."));

        // Control: one byte changed in a loose copy above the real folder changes the reader's digest.
        var (image, controlPath, controlDigest) = control!.Value;
        var mutated = document.Images[image].Source!.Original!.CopyContent();
        mutated[mutated.Length / 2] ^= 0xFF;
        var mutatedDigest = Cut1aFixtureResolver.Sha256(mutated);
        Assert.NotEqual(controlDigest, mutatedDigest);
        using var layered = new LayeredGameFileSystem([new MemoryGameFileSystem("control", (controlPath, mutated)), folder!],
            ownsLayers: false);
        var controlDocument = ReadWithCompanions(bytes, file, layered);
        var controlRow = IndexedRows(controlDocument, NifModelTextureSource.TextureKind)
            .Single(r => string.Equals(Text(r.Payload["resolution"]!["path"]), controlPath, StringComparison.Ordinal));
        var controlSha = controlDocument.Images[controlRow.Index].Source!.Original!.Sha256;
        Assert.NotEqual(controlDigest, controlSha);
        Assert.Equal(mutatedDigest, controlSha);
    }

    /// <summary>Reads the model with BMT's companion resolver over the given file system (not owned by the resolver).</summary>
    private static ModelDocument ReadWithCompanions(byte[] bytes, Cut1aCoverFile file, IGameFileSystem fileSystem)
    {
        var companions = new BethesdaTextureCompanions(fileSystem, fileSystem.Label, ownsFileSystem: false);
        try
        {
            return NifModelTestSupport.ReadWith(bytes, companions.ResolveAsync, path: file.DataRelativePath).Document;
        }
        finally
        {
            companions.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>The layer the reader read from must be the layer the independent walk found first.</summary>
    private static void CompareLayer(JsonObject expected, string? provenance, string where, List<string> mismatches)
    {
        var layer = Text(expected["layer"]);
        if (layer is null || provenance is null)
        {
            return;
        }

        var actualLayer = provenance.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(provenance)
            : "loose";
        if (!string.Equals(layer, actualLayer, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add($"{where}: read from '{actualLayer}', the independent walk resolves it in '{layer}' first.");
        }
    }

    /// <summary>An Xbox companion the reader did (not) add must be one the independent walk did (not) find.</summary>
    private static void CompareCompanion(JsonObject entries, JsonObject companion, string where, List<string> mismatches)
    {
        var path = Text(companion["path"])!;
        var key = path.Replace('/', '\\').ToLowerInvariant();
        if (!entries.TryGetPropertyValue(key, out var value) || value is not JsonObject expected)
        {
            mismatches.Add($"{where}: the reader looked for the companion '{path}', which the probe did not consider.");
            return;
        }

        var readerFound = companion["image"] is JsonValue image && image.TryGetValue<int>(out _);
        var walkFound = Text(expected["path"]) is not null;
        if (readerFound != walkFound)
        {
            mismatches.Add($"{where}: the companion '{path}' is {(readerFound ? "bound" : "absent")} for the reader but " +
                           $"{(walkFound ? "present" : "absent")} for the independent walk.");
        }
    }
}
