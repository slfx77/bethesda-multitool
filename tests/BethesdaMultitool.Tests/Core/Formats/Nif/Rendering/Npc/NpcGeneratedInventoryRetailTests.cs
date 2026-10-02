using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.Core.Actors;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

/// <summary>Checks actual installed FNV mesh composition against the exact generated and explicitly empty inventories.</summary>
[Trait("Category", BucketBTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NpcGeneratedInventoryRetailTests
{
    private const uint BooneFormId = 0x00092BD2;
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };

    /// <summary>Composes real weapon/armor geometry from generated items and proves an empty generation removes those assets.</summary>
    [Fact]
    public async Task GeneratedAndEmptyInventories_ProduceDifferentRealEquipmentGeometry()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var token = TestContext.Current.CancellationToken;
        var master = RealAssetPaths.Masters.FalloutNv();
        Assert.SkipWhen(master is null, RealAssetPaths.SkipMessage("Installed FalloutNV.esm"));
        var archives = BsaDiscovery.Discover(master!);
        Assert.SkipWhen(!archives.HasMeshes, RealAssetPaths.SkipMessage("Installed FNV meshes archive"));
        var loaded = await RealAssetEsmCache.LoadAsync(master!, token);
        Assert.NotNull(loaded.Accessor);
        var scan = Assert.IsType<EsmRecordScanResult>(loaded.RawResult.EsmRecords);
        var accessor = new MmfMemoryAccessor(loaded.Accessor);
        var inspector = new ActorInspector(loaded.Records);
        var inspection = Assert.IsType<ActorInspection>(inspector.Inspect(BooneFormId, false, 5, 7, token));
        var generation = Assert.IsType<ActorInventoryGeneration>(inspection.Generation);
        Assert.NotEmpty(generation.Items);
        var resolver = NpcAppearanceResolver.Build(accessor, loaded.RawResult.FileSize, scan.MainRecords,
            scan.BigEndianRecords > 0, scan.Game, cancellationToken: token);
        var appearance = Assert.IsType<NpcAppearance>(resolver.ResolveHeadOnly(BooneFormId,
            Path.GetFileName(master), generation: generation));
        Assert.True(appearance.WeaponVisual?.IsVisible);
        Assert.NotEmpty(appearance.EquippedItems!);
        using var service = NpcBrowserService.TryCreateFromAnalyzedEsm(accessor, loaded.RawResult.FileSize,
            scan, scan.BigEndianRecords > 0, master!, archives, cancellationToken: token);
        Assert.NotNull(service);
        var generated = Assert.IsType<BethesdaViewerScene>(service.BuildViewerScene(BooneFormId,
            headOnly: false, noEquip: false, noWeapon: false, generation: generation));
        var empty = Assert.IsType<BethesdaViewerScene>(service.BuildViewerScene(BooneFormId,
            headOnly: false, noEquip: false, noWeapon: false,
            generation: new ActorInventoryGeneration(7, 5, [], true)));
        var weaponName = Path.GetFileNameWithoutExtension(appearance.WeaponVisual!.MeshPath);
        Assert.Contains(generated.MeshParts, part => part.Name == weaponName);
        Assert.DoesNotContain(empty.MeshParts, part => part.Name == weaponName);
        var armorPaths = appearance.EquippedItems!.Select(item => NormalizePath(item.MeshPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(generated.MeshParts, part => armorPaths.Contains(NormalizePath(part.Submesh.SourceNifPath)));
        Assert.DoesNotContain(empty.MeshParts, part => armorPaths.Contains(NormalizePath(part.Submesh.SourceNifPath)));
        Assert.True(generated.MeshParts.Sum(part => part.Submesh.TriangleCount) > 0);
        Assert.True(empty.MeshParts.Sum(part => part.Submesh.TriangleCount) > 0);
        var generatedGlb = service.ExportViewerSceneToGlb(generated);
        var emptyGlb = service.ExportViewerSceneToGlb(empty);
        Assert.True(generatedGlb.Length > 20);
        Assert.True(emptyGlb.Length > 20);
        Assert.NotEqual(SHA256.HashData(generatedGlb), SHA256.HashData(emptyGlb));
        var output = Environment.GetEnvironmentVariable("BMT_GENERATED_EQUIPMENT_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            await File.WriteAllBytesAsync(Path.Combine(output, "generated.glb"), generatedGlb, token);
            await File.WriteAllBytesAsync(Path.Combine(output, "empty.glb"), emptyGlb, token);
            await File.WriteAllTextAsync(Path.Combine(output, "composition.json"), JsonSerializer.Serialize(new
            {
                master, actor = BooneFormId, generation.Seed, generation.Level, generation.Complete,
                items = generation.Items, weapon = appearance.WeaponVisual.MeshPath, armorPaths,
                generated = Describe(generated, generatedGlb), empty = Describe(empty, emptyGlb)
            }, EvidenceJsonOptions), token);
        }
    }

    /// <summary>Normalizes source NIF separators solely for comparing composed mesh provenance.</summary>
    private static string NormalizePath(string? path) => path?.Replace('/', '\\') ?? string.Empty;

    /// <summary>Captures local evidence linking scene geometry, equipment source paths, and the corresponding exported bytes.</summary>
    private static object Describe(BethesdaViewerScene scene, byte[] glb) => new
    {
        scene.SourceLabel,
        parts = scene.MeshParts.Select(part => new { part.Name, part.Submesh.SourceNifPath, part.Submesh.TriangleCount }).ToArray(),
        glbBytes = glb.Length, sha256 = Convert.ToHexString(SHA256.HashData(glb))
    };
}
