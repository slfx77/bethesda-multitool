using FalloutXbox360Utils.CLI.Rendering.Nif;
using FalloutXbox360Utils.Tests.Helpers;
using SharpGLTF.Schema2;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Smoke test for the full NIF → GLB export pipeline against a real unpacked
///     Fallout NIF. Verifies the pipeline produces a loadable GLB with non-zero
///     nodes/meshes/materials.
///     <para>
///         Gated as Bucket B because a synthetic-NIF migration would require
///         ~200-400 LOC of NIF-format builder infrastructure (header + block
///         table + typed blocks + string table + footer) to exercise a
///         smoke-test contract. The individual NIF parser / scene builder /
///         GLB writer components have synthetic unit tests already; this
///         exists to validate the full pipeline glues together against real
///         Fallout content.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class UnpackedNifGlbExportRegressionTests
{
    [Fact]
    public void ExportWaterPurified01_UnpackedJulyBuild_WritesReloadableGlb()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var inputPath = SampleFileFixture.FindSamplePath(
            @"Sample\Unpacked_Builds\360_July_Unpacked\FalloutNV\Data\meshes\clutter\food\waterpurified01.nif");
        Assert.SkipWhen(inputPath is null, "Unpacked July NIF sample not available");

        using var outputDir = new TemporaryDirectory();
        var outputPath = Path.Combine(outputDir.Path, "waterpurified01.glb");

        NifExportPipeline.Run(new NifExportSettings
        {
            InputPath = inputPath!,
            OutputPath = outputPath
        });

        Assert.True(File.Exists(outputPath));

        var model = ModelRoot.Load(outputPath);

        Assert.True(model.LogicalNodes.Count > 0);
        Assert.True(model.LogicalMeshes.Count > 0);
        Assert.True(model.LogicalMaterials.Count > 0);
        Assert.True(new FileInfo(outputPath).Length > 1_000);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"UnpackedNifGlbExportTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
