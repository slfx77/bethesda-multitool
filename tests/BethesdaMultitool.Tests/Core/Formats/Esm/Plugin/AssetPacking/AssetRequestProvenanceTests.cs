using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.AssetPacking;

public sealed class AssetRequestProvenanceTests : IDisposable
{
    private static readonly byte[] FirstPayload = [1];
    private static readonly byte[] SecondPayload = [2];
    private static readonly byte[] ThirdPayload = [3];
    private static readonly byte[] SmallPayload = [1, 2];
    private static readonly string[] MissingMorphExtensions = [".egt", ".tri"];
    private const string Parent = "meshes\\armor\\headgear\\suithat_go.nif";
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "bmt-request-provenance-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Collection_retains_competing_owners_roles_projection_and_dump_basis()
    {
        var records = new RecordCollection
        {
            Armor =
            [
                new ArmorRecord { FormId = 0x100, ModelPath = Parent },
                new ArmorRecord { FormId = 0x200, WorldModelPath = Parent }
            ]
        };
        records.ModelPathIndex[0x100] = Parent;
        var dump = Path.Combine(_root, "source.dmp");
        File.WriteAllBytes(dump, Encoding.ASCII.GetBytes(Parent + "\0"));
        var catalog = new AssetRequestCatalog();

        var paths = AssetPathCollector.Collect(records, dump, NullConversionProgressSink.Instance,
            catalog, "converted.esp");

        Assert.Equal(4, paths.Count);
        var reasons = catalog.Get(Parent);
        Assert.Equal(4, reasons.Count);
        Assert.Contains(reasons, r => r.Basis == "record-field" && r.OwnerFormId == 0x100 && r.Field == "ModelPath");
        Assert.Contains(reasons, r => r.Basis == "record-field" && r.OwnerFormId == 0x200 && r.Field == "WorldModelPath");
        Assert.Contains(reasons, r => r.Basis == "model-index-projection" && r.OwnerFormId == 0x100);
        Assert.Contains(reasons, r => r.Basis == "dump-string" && r.OwnerFormId is null && r.SourcePath == dump);
        var morph = catalog.Get(Path.ChangeExtension(Parent, ".egm"));
        Assert.Equal(4, morph.Count);
        Assert.All(morph, r => Assert.Equal(Parent, r.ParentPath));
        Assert.Contains(morph, r => r.OwnerFormId == 0x200 && r.Field == "WorldModelPath" && r.ParentBasis == "record-field");
        Assert.Contains(morph, r => r.OwnerFormId is null && r.ParentBasis == "dump-string");
        Assert.True(AssetRequestCatalog.IsDerivedOnly(morph));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_duplicate_survives_derivation_without_enabling_fuzzy_morph(bool directFirst)
    {
        const string path = "meshes\\armor\\headgear\\suithat_go.egm";
        var catalog = new AssetRequestCatalog();
        var direct = new AssetRequestEvidence("record-field", 0x300, "RaceRecord", "FaceMorphPath");
        var derived = new AssetRequestEvidence("morph-companion-candidate", 0x100, "ArmorRecord", "ModelPath", Parent);
        catalog.Add(path, directFirst ? direct : derived);
        catalog.Add(path, directFirst ? derived : direct);
        WriteAsset("secondary", "meshes\\armor\\headgear\\suithat.egm", SmallPayload);
        using var baseline = Index("baseline");
        using var secondary = Index("secondary");
        var resolver = new DataFolderResolver(baseline, [secondary]);

        var resolved = catalog.RequireExactMorphParent(path, resolver.Resolve(path), resolver);
        var annotated = catalog.Annotate(new AssetResolution { RequestedPath = path, Kind = resolved.Kind });

        Assert.Equal(AssetResolutionKind.Missing, resolved.Kind);
        Assert.Equal(2, annotated.RequestEvidence.Count);
        Assert.Contains(direct, annotated.RequestEvidence);
        Assert.False(AssetRequestCatalog.IsDerivedOnly(annotated.RequestEvidence));
        Assert.Equal("observed-request; runtime requirement unverified", annotated.RequestAssessment);
        Assert.Single(resolved.UnverifiedCandidates);
    }

    [Theory]
    [InlineData(".egm")]
    [InlineData(".egt")]
    [InlineData(".tri")]
    public void Different_stem_morphs_are_unselected_alternatives(string extension)
    {
        var requested = Path.ChangeExtension(Parent, extension);
        WriteAsset("secondary", "meshes\\armor\\headgear\\suithat" + extension, FirstPayload);
        WriteAsset("secondary", "meshes\\armor\\headgear\\suithat_alt" + extension, SecondPayload);
        WriteAsset("secondary", "meshes\\other\\headgear\\suithat" + extension, ThirdPayload);
        using var baseline = Index("baseline");
        using var secondary = Index("secondary");

        var result = new DataFolderResolver(baseline, [secondary]).Resolve(requested);

        Assert.Equal(AssetResolutionKind.Missing, result.Kind);
        Assert.Null(result.Source);
        Assert.Equal("morph-exact-path-unavailable", result.UnresolvedReason);
        Assert.Equal(2, result.UnverifiedCandidates.Count);
        Assert.All(result.UnverifiedCandidates, c => Assert.Equal("unverified-morph-identity", c.Reason));
    }

    [Fact]
    public void Exact_egm_does_not_establish_other_companion_requirements()
    {
        WriteAsset("baseline", Parent, FirstPayload);
        WriteAsset("secondary", Path.ChangeExtension(Parent, ".egm"), SecondPayload);
        using var baseline = Index("baseline");
        using var secondary = Index("secondary");
        var resolver = new DataFolderResolver(baseline, [secondary]);
        var catalog = new AssetRequestCatalog();
        AssetPathCollector.Collect(new RecordCollection
        {
            Armor = [new ArmorRecord { FormId = 0x100, WorldModelPath = Parent }]
        }, null, NullConversionProgressSink.Instance, catalog);

        Assert.Equal(AssetResolutionKind.ResolvedExact, resolver.Resolve(Path.ChangeExtension(Parent, ".egm")).Kind);
        foreach (var extension in MissingMorphExtensions)
        {
            var path = Path.ChangeExtension(Parent, extension);
            var resolution = catalog.RequireExactMorphParent(path, resolver.Resolve(path), resolver);
            var annotated = catalog.Annotate(new AssetResolution { RequestedPath = path, Kind = resolution.Kind });
            Assert.Equal("candidate-unavailable; requirement unknown", annotated.RequestAssessment);
            Assert.Equal("WorldModelPath", Assert.Single(annotated.RequestEvidence).Field);
        }
    }

    [Fact]
    public void Exact_morph_is_not_paired_with_a_fuzzy_parent_nif()
    {
        WriteAsset("secondary", "meshes\\armor\\headgear\\suithat.nif", FirstPayload);
        var morph = Path.ChangeExtension(Parent, ".egm");
        WriteAsset("secondary", morph, SecondPayload);
        using var baseline = Index("baseline");
        using var secondary = Index("secondary");
        var resolver = new DataFolderResolver(baseline, [secondary]);
        var catalog = new AssetRequestCatalog();
        catalog.Add(morph, new AssetRequestEvidence("morph-companion-candidate", ParentPath: Parent));

        Assert.Equal(AssetResolutionKind.ResolvedFuzzy, resolver.Resolve(Parent).Kind);
        var guarded = catalog.RequireExactMorphParent(morph, resolver.Resolve(morph), resolver);

        Assert.Equal(AssetResolutionKind.Missing, guarded.Kind);
        Assert.Null(guarded.Source);
        Assert.Equal("derived-morph-parent-not-exact", guarded.UnresolvedReason);
    }

    [Theory]
    [InlineData(0u, "male")]
    [InlineData(1u, "female")]
    public void Prebake_candidate_keeps_allocated_owner_source_and_rebasing(uint flags, string gender)
    {
        var records = new RecordCollection
        {
            Npcs = [new NpcRecord { FormId = 0x010030AB,
                Stats = new ActorBaseSubrecord(flags, 0, 0, 1, 0, 0, 100, 0, 0, 0, 0, false) }]
        };
        var catalog = new AssetRequestCatalog();

        var result = NpcFaceAssetCollector.Collect(records,
            new Dictionary<uint, uint> { [0x00125126] = 0x010030AB }, "Example.ESM", catalog);

        var source = $"textures\\characters\\bodymods\\falloutnv.esm\\00125126modbody{gender}.dds";
        Assert.Equal($"textures\\characters\\bodymods\\example.esm\\000030abmodbody{gender}.dds", result.PackPathRenames[source]);
        var evidence = Assert.Single(catalog.Get(source));
        Assert.Equal(0x010030ABu, evidence.OwnerFormId);
        Assert.Equal(0x00125126u, evidence.SourceOwnerFormId);
        Assert.Equal("body", evidence.Field);
        Assert.Equal("candidate-unavailable; requirement unknown", catalog.Annotate(new AssetResolution
            { RequestedPath = source, Kind = AssetResolutionKind.Missing }).RequestAssessment);
    }

    [Fact]
    public async Task Pack_result_and_audit_keep_requests_embedded_parent_and_existing_counts()
    {
        var plugin = Path.Combine(_root, "test.esm");
        var owners = Enumerable.Range(0x100, 16).Select(value => (uint)value).ToArray();
        var records = owners.Select(owner => EsmTestFileBuilder.BuildRecord("ARMO", owner, 0,
            ("EDID", Encoding.ASCII.GetBytes($"Hat{owner:X8}\0")),
            ("MODL", Encoding.ASCII.GetBytes(Parent + "\0")))).ToArray();
        await File.WriteAllBytesAsync(plugin, new EsmTestFileBuilder().AddTopLevelGrup("ARMO", records).Build(),
            TestContext.Current.CancellationToken);
        WriteAsset("secondary", Parent, Encoding.ASCII.GetBytes("\0textures\\armor\\missing.dds\0"));
        WriteAsset("secondary", "meshes\\armor\\headgear\\suithat.egm", SmallPayload);
        var output = Path.Combine(_root, "out.bsa");

        var result = await AssetPackingService.PackAsync(new AssetPackingOptions
        {
            ConvertedEsmPath = plugin, BaselineDataFolder = Directory.CreateDirectory(Path.Combine(_root, "baseline")).FullName,
            SecondaryDataFolders = [new SecondaryDataFolder { Path = Path.Combine(_root, "secondary") }],
            OutputBsaPath = output, IncludeFuzzyMatches = true, WriteAuditFile = true
        }, NullConversionProgressSink.Instance, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(5, result.Stats.TotalPathsScanned);
        Assert.Equal(1, result.Stats.ResolvedExact);
        Assert.Equal(4, result.Stats.Missing);
        Assert.Equal(5, result.Requests.Count);
        var texture = Assert.Single(result.Resolutions, r => r.RequestedPath == "textures\\armor\\missing.dds");
        Assert.Contains(texture.RequestEvidence, r => r.Basis == "nif-embedded-path" && r.OwnerFormId == 0x100 &&
            r.ParentPath == Parent && r.SelectedParentPath == Parent && r.SourcePath == plugin);
        Assert.Equal("source-not-found", texture.UnresolvedReason);
        var morph = Assert.Single(result.Resolutions, r => r.RequestedPath == Path.ChangeExtension(Parent, ".egm"));
        Assert.Equal("candidate-unavailable; requirement unknown", morph.RequestAssessment);
        Assert.Single(morph.UnverifiedCandidates);
        var audit = await File.ReadAllTextAsync(output + ".missing.txt", TestContext.Current.CancellationToken);
        Assert.Contains("# Missing (unresolved):               4", audit);
        Assert.Contains("## MISSING", audit);
        Assert.Contains("# Count: 4", audit);
        Assert.Contains("# Alternative (not selected): meshes\\armor\\headgear\\suithat.egm", audit);
        Assert.DoesNotContain("runtime will fail", audit);
        Assert.Contains("# Full request evidence: out.bsa.requests.json", audit);
        Assert.Contains($"# Evidence: {texture.RequestEvidence.Count} reason(s); bases: nif-embedded-path", audit);
        Assert.DoesNotContain("# Request:", audit);
        Assert.DoesNotContain(plugin, audit);
        Assert.True(audit.Length < 6000, "The human audit should summarize shared-owner evidence.");
        using var durable = JsonDocument.Parse(await File.ReadAllTextAsync(output + ".requests.json",
            TestContext.Current.CancellationToken));
        Assert.Equal("Complete", durable.RootElement.GetProperty("status").GetString());
        var durableRequests = durable.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        Assert.Equal(5, durableRequests.Length);
        var exact = Assert.Single(durableRequests, r => r.GetProperty("path").GetString() == Parent);
        Assert.Equal("ResolvedExact", exact.GetProperty("lookup").GetProperty("kind").GetString());
        Assert.Equal("written", exact.GetProperty("packing").GetProperty("status").GetString());
        Assert.Equal(Parent, exact.GetProperty("packing").GetProperty("path").GetString());
        Assert.Equal(owners, exact.GetProperty("evidence").EnumerateArray()
            .Where(r => r.GetProperty("basis").GetString() == "record-field")
            .Select(r => r.GetProperty("ownerFormId").GetUInt32()).Order().ToArray());
        var durableTexture = Assert.Single(durableRequests,
            r => r.GetProperty("path").GetString() == "textures\\armor\\missing.dds");
        var textureEvidence = durableTexture.GetProperty("evidence").EnumerateArray().ToArray();
        Assert.Equal(texture.RequestEvidence.Count, textureEvidence.Length);
        Assert.Equal(owners, textureEvidence.Select(r => r.GetProperty("ownerFormId").GetUInt32()).Distinct().Order().ToArray());
        Assert.All(textureEvidence, r =>
        {
            Assert.Equal("nif-embedded-path", r.GetProperty("basis").GetString());
            Assert.Equal(Parent, r.GetProperty("parentPath").GetString());
            Assert.Equal(Parent, r.GetProperty("selectedParentPath").GetString());
            Assert.Equal(plugin, r.GetProperty("sourcePath").GetString());
        });
        Assert.Equal("unknown", exact.GetProperty("requirement").GetString());
        var pluginIdentity = durable.RootElement.GetProperty("inputs").GetProperty("convertedPlugin");
        Assert.Equal(plugin, pluginIdentity.GetProperty("path").GetString());
        Assert.Equal("path-only", pluginIdentity.GetProperty("identity").GetString());
        Assert.Equal(JsonValueKind.Null, pluginIdentity.GetProperty("sha256").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Baseline_only_pack_persists_requests_without_a_bsa_only_when_audit_enabled(bool writeAudit)
    {
        const string path = "meshes\\armor\\plain.nif";
        var plugin = Path.Combine(_root, "baseline.esm");
        var record = EsmTestFileBuilder.BuildRecord("ARMO", 0x100, 0,
            ("EDID", Encoding.ASCII.GetBytes("Plain\0")), ("MODL", Encoding.ASCII.GetBytes(path + "\0")));
        await File.WriteAllBytesAsync(plugin, new EsmTestFileBuilder().AddTopLevelGrup("ARMO", record).Build(),
            TestContext.Current.CancellationToken);
        WriteAsset("baseline", path, SmallPayload);
        var output = Path.Combine(_root, "baseline.bsa");

        var result = await AssetPackingService.PackAsync(new AssetPackingOptions
        {
            ConvertedEsmPath = plugin, BaselineDataFolder = Path.Combine(_root, "baseline"),
            SecondaryDataFolders = [], OutputBsaPath = output, WriteAuditFile = writeAudit
        }, NullConversionProgressSink.Instance, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.Stats.AlreadyInBaseline);
        Assert.Equal(0, result.Stats.Missing);
        Assert.Empty(result.Resolutions);
        Assert.Single(result.Requests);
        Assert.Empty(result.OutputPaths);
        Assert.False(File.Exists(output));
        Assert.Equal(writeAudit, File.Exists(output + ".requests.json"));
        if (!writeAudit) return;
        using var durable = JsonDocument.Parse(await File.ReadAllTextAsync(output + ".requests.json",
            TestContext.Current.CancellationToken));
        Assert.Equal(1, durable.RootElement.GetProperty("schemaVersion").GetInt32());
        var request = Assert.Single(durable.RootElement.GetProperty("requests").EnumerateArray());
        Assert.Equal(path, request.GetProperty("path").GetString());
        Assert.NotEmpty(request.GetProperty("evidence").EnumerateArray());
        Assert.Equal("AlreadyInBaseline", request.GetProperty("lookup").GetProperty("kind").GetString());
        Assert.Equal(-1, request.GetProperty("lookup").GetProperty("sourceFolderIndex").GetInt32());
        Assert.Equal("skipped-baseline", request.GetProperty("packing").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("packing").GetProperty("path").ValueKind);
    }

    [Fact]
    public void Cancelled_audit_does_not_infer_baseline_or_missing_for_an_unobserved_request()
    {
        const string pending = "meshes\\armor\\pending.nif";
        var requests = new AssetRequestCatalog();
        requests.Add(Parent, new AssetRequestEvidence("record-field", 0x100));
        requests.Add(pending, new AssetRequestEvidence("record-field", 0x200));
        var output = Path.Combine(_root, "cancelled.bsa");
        var options = new AssetPackingOptions { ConvertedEsmPath = "source.esm", BaselineDataFolder = "baseline",
            SecondaryDataFolders = [], OutputBsaPath = output, WriteAuditFile = true };
        AssetRequestAuditWriter.TryWrite(options, requests.Snapshot(),
            new Dictionary<string, DataFolderResolution>(StringComparer.OrdinalIgnoreCase)
            {
                [Parent] = new() { Kind = AssetResolutionKind.AlreadyInBaseline, ResolvedPath = Parent }
            }, [], new Dictionary<string, string>(), [], "Cancelled", NullConversionProgressSink.Instance);

        using var durable = JsonDocument.Parse(File.ReadAllText(output + ".requests.json"));
        Assert.Equal("Cancelled", durable.RootElement.GetProperty("status").GetString());
        var row = Assert.Single(durable.RootElement.GetProperty("requests").EnumerateArray(),
            r => r.GetProperty("path").GetString() == pending);
        Assert.Equal("not-observed", row.GetProperty("lookup").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lookup").GetProperty("kind").ValueKind);
        Assert.Equal("not-observed", row.GetProperty("packing").GetProperty("status").GetString());
        Assert.Equal("request-not-evaluated; requirement unknown", row.GetProperty("assessment").GetString());
    }

    [Fact]
    public void Evidence_snapshot_does_not_change_when_more_reasons_are_collected()
    {
        var catalog = new AssetRequestCatalog();
        catalog.Add(Parent, new AssetRequestEvidence("dump-string"));
        var snapshot = catalog.Snapshot();
        catalog.Add(Parent, new AssetRequestEvidence("record-field", 0x100));
        Assert.Single(Assert.Single(snapshot).Evidence);
        Assert.Equal(2, catalog.Get(Parent).Count);
    }

    private void WriteAsset(string folder, string path, byte[] bytes)
    {
        var file = Path.Combine(_root, folder, path.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);
    }

    private DataFolderIndex Index(string folder)
    {
        var index = new DataFolderIndex(Directory.CreateDirectory(Path.Combine(_root, folder)).FullName, false);
        index.Build();
        return index;
    }

    public void Dispose() => Directory.Delete(_root, true);
}
