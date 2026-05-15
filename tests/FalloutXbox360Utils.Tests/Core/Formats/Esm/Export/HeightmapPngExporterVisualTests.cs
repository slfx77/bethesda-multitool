using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Export;

public class HeightmapPngExporterVisualTests
{
    [Fact]
    public async Task ExportLandVisualsAsync_WritesVclrMasksAndTextureComposite()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"terrain-visuals-{Guid.NewGuid():N}");
        try
        {
            var land = CreateLand(0x1000, 0, 0);

            await HeightmapPngExporter.ExportLandVisualsAsync([land], outputDir);

            var visualDir = Path.Combine(outputDir, "land_visuals");
            var worldspaceDir = Path.Combine(visualDir, "worldspaces", "ws_unknown");
            var vclrPath = Path.Combine(worldspaceDir, "vclr", "land_00001000_cell0_0_vclr.png");
            var maskPath = Path.Combine(
                worldspaceDir,
                "texture_masks",
                "land_00001000_cell0_0_atxt_q3_layer1_tex00002000.png");
            var vclrComposite = Path.Combine(worldspaceDir, "vclr_composite.png");
            var textureComposite = Path.Combine(worldspaceDir, "texture_id_composite.png");

            Assert.Equal((33, 33), ReadPngDimensions(vclrPath));
            Assert.Equal((33, 33), ReadPngDimensions(maskPath));
            Assert.Equal((33, 33), ReadPngDimensions(vclrComposite));
            Assert.Equal((33, 33), ReadPngDimensions(textureComposite));
            Assert.False(File.Exists(Path.Combine(visualDir, "vclr_composite.png")));
            Assert.False(File.Exists(Path.Combine(visualDir, "texture_id_composite.png")));
            Assert.False(Directory.Exists(Path.Combine(visualDir, "vclr")));
            Assert.False(Directory.Exists(Path.Combine(visualDir, "texture_masks")));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExportLandVisualsAsync_WritesRuntimeColorVclrWhenLandVisualDataIsAbsent()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"terrain-runtime-vclr-{Guid.NewGuid():N}");
        try
        {
            var land = CreateLand(0x1000, 0, 0) with
            {
                VisualData = null,
                RuntimeTerrainMesh = CreateRuntimeMeshWithColors()
            };

            await HeightmapPngExporter.ExportLandVisualsAsync([land], outputDir);

            var worldspaceDir = Path.Combine(outputDir, "land_visuals", "worldspaces", "ws_unknown");
            Assert.Equal((33, 33), ReadPngDimensions(Path.Combine(
                worldspaceDir,
                "vclr",
                "land_00001000_cell0_0_vclr.png")));
            Assert.Equal((33, 33), ReadPngDimensions(Path.Combine(worldspaceDir, "vclr_composite.png")));
            Assert.False(File.Exists(Path.Combine(worldspaceDir, "texture_id_composite.png")));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExportLandRecordsAsync_WritesPerCellHeightmapsUnderWorldspaceDirectory()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"terrain-land-{Guid.NewGuid():N}");
        try
        {
            var land = CreateLand(0x1000, 0, 0) with { WorldspaceFormId = 0x0000003C };

            await HeightmapPngExporter.ExportLandRecordsAsync(
                [land],
                outputDir,
                worldspaceNames: new Dictionary<uint, string> { [0x0000003C] = "TheStripWorld" });

            var path = Path.Combine(
                outputDir,
                "worldspaces",
                "ws_0000003C_TheStripWorld",
                "land_00001000_ws0000003C_TheStripWorld_cell0_0.png");

            Assert.Equal((33, 33), ReadPngDimensions(path));
            Assert.Empty(Directory.GetFiles(outputDir, "land_*.png"));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExportLandRecordsAsync_GrayscaleUsesVhgtScaleAndWritesScaleMetadata()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"terrain-gray-scale-{Guid.NewGuid():N}");
        try
        {
            var land = CreateLand(0x1000, 0, 0) with { WorldspaceFormId = 0x0000003C };

            await HeightmapPngExporter.ExportLandRecordsAsync(
                [land],
                outputDir,
                false,
                new Dictionary<uint, string> { [0x0000003C] = "TheStripWorld" });

            var worldspaceDir = Path.Combine(outputDir, "worldspaces", "ws_0000003C_TheStripWorld");
            var scaleCsv = Path.Combine(worldspaceDir, "height_grayscale_scale.csv");

            Assert.True(File.Exists(scaleCsv));
            var csv = await File.ReadAllTextAsync(scaleCsv);
            Assert.Contains(",8,", csv);
            Assert.Equal((33, 33), ReadPngDimensions(Path.Combine(
                worldspaceDir,
                "land_00001000_ws0000003C_TheStripWorld_cell0_0.png")));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public void FixedScaleGrayscale_DoesNotStretchSmallCityReliefToFullRange()
    {
        var heights = new float[33, 33];
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                heights[y, x] = 1000f + x;
            }
        }

        var scale = new HeightmapGrayscaleScale(
            BaseHeight: 0f,
            UnitsPerGray: 8f,
            MinHeight: 1000f,
            MaxHeight: 1032f,
            ClippedLowCount: 0,
            ClippedHighCount: 0,
            SampleCount: 33 * 33);

        var pixels = HeightmapColorRenderer.GenerateFixedScaleGrayscalePixels(heights, scale);

        Assert.Equal(125, pixels[0]);
        Assert.Equal(129, pixels[32]);
        Assert.DoesNotContain((byte)0, pixels);
        Assert.DoesNotContain((byte)255, pixels);
    }

    [Fact]
    public async Task ExportAsync_WritesStandaloneHeightmapsUnderUnknownWorldspaceDirectory()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"terrain-standalone-{Guid.NewGuid():N}");
        try
        {
            var heightmap = new DetectedVhgtHeightmap
            {
                Offset = 0,
                IsBigEndian = true,
                HeightOffset = 0,
                HeightDeltas = new sbyte[33 * 33]
            };

            await HeightmapPngExporter.ExportAsync([heightmap], null, outputDir);

            var path = Path.Combine(
                outputDir,
                "worldspaces",
                "ws_unknown",
                "standalone",
                "heightmap_0000_be.png");

            Assert.Equal((33, 33), ReadPngDimensions(path));
            Assert.Empty(Directory.GetFiles(outputDir, "heightmap_*.png"));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExportCompositeWorldmapWithGridAsync_UsesLandCellPlacement()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"terrain-grid-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, "worldmap_composite_grid.png");

            await HeightmapPngExporter.ExportCompositeWorldmapWithGridAsync(
                [],
                [],
                [CreateLand(0x1000, 0, 0), CreateLand(0x1001, 1, 0)],
                outputPath);

            Assert.Equal((65, 33), ReadPngDimensions(outputPath));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExportWorldspaceCompositeWorldmapsAsync_SplitsOverlappingCellCoordinatesByWorldspace()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"terrain-ws-grid-{Guid.NewGuid():N}");
        try
        {
            var landA = CreateLand(0x1000, 0, 0) with { WorldspaceFormId = 0x0000003C };
            var landB = CreateLand(0x1001, 0, 0) with { WorldspaceFormId = 0x00000040 };

            var paths = await HeightmapPngExporter.ExportWorldspaceCompositeWorldmapsAsync(
                [],
                [],
                [landA, landB],
                outputDir);

            Assert.Equal(4, paths.Count);
            Assert.Equal((33, 33), ReadPngDimensions(Path.Combine(
                outputDir,
                "worldspaces",
                "ws_0000003C",
                "worldmap_composite_grid.png")));
            Assert.Equal((33, 33), ReadPngDimensions(Path.Combine(
                outputDir,
                "worldspaces",
                "ws_00000040",
                "worldmap_composite_grid.png")));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExportWorldspaceCompositeWorldmapsAsync_WritesRuntimeSourceCoverageWhenMeshExists()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"terrain-coverage-{Guid.NewGuid():N}");
        try
        {
            var land = CreateLand(0x1000, 0, 0) with
            {
                WorldspaceFormId = 0x0000003C,
                RuntimeTerrainMesh = CreateLocalRuntimeMesh()
            };

            var paths = await HeightmapPngExporter.ExportWorldspaceCompositeWorldmapsAsync(
                [],
                [],
                [land],
                outputDir);

            var coveragePath = Path.Combine(
                outputDir,
                "worldspaces",
                "ws_0000003C",
                "runtime_source_coverage.png");
            var coverageGridPath = Path.Combine(
                outputDir,
                "worldspaces",
                "ws_0000003C",
                "runtime_source_coverage_grid.png");

            Assert.Contains(coveragePath, paths);
            Assert.Contains(coverageGridPath, paths);
            Assert.Equal((33, 33), ReadPngDimensions(coveragePath));
            Assert.Equal((33, 33), ReadPngDimensions(coverageGridPath));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public void StandaloneHeightmapResolver_AnchorsStandaloneVhgtToParsedLandHeightmap()
    {
        var parsedHeightmap = CreateLandHeightmap(125f, 3);
        var runtimeReplacement = CreateLandHeightmap(200f, 9);
        var standalone = new DetectedVhgtHeightmap
        {
            Offset = 0x2000,
            IsBigEndian = true,
            HeightOffset = parsedHeightmap.HeightOffset,
            HeightDeltas = parsedHeightmap.HeightDeltas
        };
        var land = new ExtractedLandRecord
        {
            Header = new DetectedMainRecord("LAND", 0, 0, 0x00001000, 0x1000, true),
            ParsedHeightmap = parsedHeightmap,
            Heightmap = runtimeReplacement,
            WorldspaceFormId = 0x0000003C,
            ParentCellFormId = 0x00002000,
            CellX = 1,
            CellY = -2
        };

        var matches = StandaloneHeightmapResolver.Resolve([standalone], [land]);
        var unresolved = StandaloneHeightmapResolver.GetUnresolvedHeightmaps([standalone], [land]);

        var match = Assert.Single(matches);
        Assert.Equal(StandaloneHeightmapStatus.ExactLandMatch, match.Status);
        Assert.Equal(0x00001000u, match.Land!.Header.FormId);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void StandaloneHeightmapResolver_PrefersVhgtOffsetOverAmbiguousFlatContent()
    {
        var sharedHeightmap = CreateLandHeightmap(0f, 0);
        var standalone = new DetectedVhgtHeightmap
        {
            Offset = 0x2000,
            IsBigEndian = true,
            HeightOffset = sharedHeightmap.HeightOffset,
            HeightDeltas = sharedHeightmap.HeightDeltas
        };
        var containingLand = new ExtractedLandRecord
        {
            Header = new DetectedMainRecord("LAND", 0, 0, 0x00001000, 0x1000, true),
            ParsedHeightmap = sharedHeightmap with { Offset = standalone.Offset + 6 },
            WorldspaceFormId = 0x0000003C,
            ParentCellFormId = 0x00002000,
            CellX = 1,
            CellY = 2
        };
        var duplicateLand = containingLand with
        {
            Header = new DetectedMainRecord("LAND", 0, 0, 0x00001001, 0x3000, true),
            ParsedHeightmap = sharedHeightmap with { Offset = 0x4000 },
            ParentCellFormId = 0x00002001,
            CellX = 3,
            CellY = 4
        };

        var matches = StandaloneHeightmapResolver.Resolve([standalone], [duplicateLand, containingLand]);
        var unresolved = StandaloneHeightmapResolver.GetUnresolvedHeightmaps([standalone], [duplicateLand, containingLand]);

        var match = Assert.Single(matches);
        Assert.Equal(StandaloneHeightmapStatus.OffsetLandMatch, match.Status);
        Assert.Equal(0x00001000u, match.Land!.Header.FormId);
        Assert.Empty(unresolved);
    }

    private static ExtractedLandRecord CreateLand(uint formId, int cellX, int cellY)
    {
        var vclr = new byte[33 * 33 * 3];
        for (var i = 0; i < vclr.Length; i += 3)
        {
            vclr[i] = 80;
            vclr[i + 1] = 120;
            vclr[i + 2] = 160;
        }

        return new ExtractedLandRecord
        {
            Header = new DetectedMainRecord("LAND", 0, 0, formId, 0, false),
            CellX = cellX,
            CellY = cellY,
            Heightmap = new LandHeightmap
            {
                HeightOffset = 0,
                HeightDeltas = new sbyte[33 * 33],
                ExactHeights = CreateHeights(cellX)
            },
            VisualData = new LandVisualData
            {
                VertexColors = vclr,
                TextureLayers =
                [
                    new LandTextureLayer
                    {
                        Kind = LandTextureLayerKind.Base,
                        TextureFormId = 0x00001000,
                        Quadrant = 0,
                        Layer = 0
                    },
                    new LandTextureLayer
                    {
                        Kind = LandTextureLayerKind.Alpha,
                        TextureFormId = 0x00002000,
                        Quadrant = 3,
                        Layer = 1,
                        BlendEntries =
                        [
                            new LandTextureBlendEntry(0, 0, 0, 0.5f),
                            new LandTextureBlendEntry(288, 0, 0, 1.0f)
                        ]
                    }
                ]
            }
        };
    }

    private static LandHeightmap CreateLandHeightmap(float heightOffset, sbyte seed)
    {
        var deltas = new sbyte[33 * 33];
        deltas[0] = seed;
        deltas[^1] = (sbyte)(seed + 1);
        return new LandHeightmap
        {
            HeightOffset = heightOffset,
            HeightDeltas = deltas,
            Offset = 0x1234
        };
    }

    private static float[,] CreateHeights(int cellX)
    {
        var heights = new float[33, 33];
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                heights[y, x] = cellX * 100 + x + y;
            }
        }

        return heights;
    }

    private static RuntimeTerrainMesh CreateLocalRuntimeMesh()
    {
        var vertices = new float[RuntimeTerrainMesh.VertexCount * 3];
        Array.Fill(vertices, float.NaN);

        var index = 0;
        for (var y = 8; y <= 24; y++)
        {
            for (var x = 8; x <= 24; x++)
            {
                vertices[index * 3 + 0] = -2048f + x * 128f;
                vertices[index * 3 + 1] = -2048f + y * 128f;
                vertices[index * 3 + 2] = 1000f + x + y;
                index++;
            }
        }

        return new RuntimeTerrainMesh { Vertices = vertices };
    }

    private static RuntimeTerrainMesh CreateRuntimeMeshWithColors()
    {
        var vertices = new float[RuntimeTerrainMesh.VertexCount * 3];
        var colors = new float[RuntimeTerrainMesh.VertexCount * 4];

        for (var y = 0; y < RuntimeTerrainMesh.GridSize; y++)
        {
            for (var x = 0; x < RuntimeTerrainMesh.GridSize; x++)
            {
                var index = y * RuntimeTerrainMesh.GridSize + x;
                vertices[index * 3 + 0] = -2048f + x * 128f;
                vertices[index * 3 + 1] = -2048f + y * 128f;
                vertices[index * 3 + 2] = 1000f + x + y;

                colors[index * 4 + 0] = x / 32f;
                colors[index * 4 + 1] = y / 32f;
                colors[index * 4 + 2] = 0.5f;
                colors[index * 4 + 3] = 1f;
            }
        }

        return new RuntimeTerrainMesh { Vertices = vertices, Colors = colors };
    }

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        Assert.True(File.Exists(path), $"Expected PNG does not exist: {path}");
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 24, "PNG is too short to contain IHDR.");
        return (
            (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)),
            (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
    }
}
