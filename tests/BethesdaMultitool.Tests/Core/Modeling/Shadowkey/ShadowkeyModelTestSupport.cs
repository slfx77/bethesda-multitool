using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Sources;
using Slfx77.Multitool.Core.Settings;
using Slfx77.Multitool.Media.Blender;
using Slfx77.Multitool.Media.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Shadowkey;

/// <summary>
///     Shared plumbing for the Shadowkey model reader tests: reads through the real <see cref="IModelSourceReader" />
///     contract (item, context, cache scope), probes bounded candidates, decodes the images' PNG standard payloads with a
///     reader of its own (not the encoder under test), and digests typed arrays by the rules of the Python oracle
///     (<c>tools/scripts/gate2/shadowkey_cover.py</c>, <c>DIGEST_RULES</c>).
/// </summary>
internal static class ShadowkeyModelTestSupport
{
    /// <summary>The source id of the in-memory sources.</summary>
    public const string SourceId = "memory";

    /// <summary>The entry path a mesh record is read from; its stem is the document name.</summary>
    public const string DefaultMeshPath = "175_arrow.bin";

    /// <summary>Reads a record with a fresh cache scope, as the Shared read lifetime would.</summary>
    public static ModelReadResult ReadMesh(byte[] bytes, IReadOnlyDictionary<string, string>? options = null,
        ModelNativeDetail detail = ModelNativeDetail.Metadata, string path = DefaultMeshPath)
    {
        var source = new InMemoryAssetSource(SourceId);
        var item = new ModelSourceItem(source, source.Add(path, bytes));
        using var input = new MemoryStream(bytes, false);
        var context = new ModelReadContext(item, input, new NoCacheScope(), detail, options);
        return new ShadowkeyMeshModelReader().Read(item, context, CancellationToken.None);
    }

    /// <summary>Reads a zone from a built file set (every file beside the <c>.zmp</c> in one in-memory source).</summary>
    public static ModelReadResult ReadZone(IReadOnlyDictionary<string, byte[]> files, string stem = "testzone",
        IReadOnlyDictionary<string, string>? options = null, ModelNativeDetail detail = ModelNativeDetail.Metadata)
    {
        var source = new InMemoryAssetSource(SourceId);
        AssetEntry? zmp = null;
        foreach (var (name, bytes) in files)
        {
            var entry = source.Add("zones/" + name, bytes);
            if (string.Equals(name, stem + ".zmp", StringComparison.Ordinal))
            {
                zmp = entry;
            }
        }

        var item = new ModelSourceItem(source, zmp ?? throw new InvalidOperationException("The set has no .zmp."));
        using var input = new MemoryStream(files[stem + ".zmp"], false);
        var context = new ModelReadContext(item, input, new NoCacheScope(), detail, options);
        return new ShadowkeyZoneModelReader().Read(item, context, CancellationToken.None);
    }

    /// <summary>
    ///     Plans a document with the Shared GLB writer under <paramref name="layers" /> (the source defaults when null) and
    ///     writes it into <paramref name="directory" />.
    /// </summary>
    /// <returns>The plan's fidelity rows and the write result, or the refusal when the write threw NotSupportedException.</returns>
    public static async Task<(IReadOnlyList<ModelFidelityRow> Rows, ModelWriteResult? Written, string? Refusal)> WriteGlbAsync(
        ModelDocument document, string directory, string name, ModelLayerSelection? layers = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var writer = new ModelGlbWriter();
        var environment = await writer.PreflightAsync(new Dictionary<string, string>(), cancellationToken);
        var plan = writer.Plan(document, environment, new ModelConvertOptions("glb", layers: layers), cancellationToken);
        try
        {
            var written = await writer.WriteAsync(new ModelWriteRequest(plan, Path.Combine(directory, name + ".glb")),
                cancellationToken);
            return (plan.Fidelity.Rows, written, null);
        }
        catch (NotSupportedException exception)
        {
            return (plan.Fidelity.Rows, null, exception.Message);
        }
    }

    /// <summary>
    ///     The document with each clip's first track copied onto every node (a multi-skin record's layout before cut-2
    ///     review finding 1, when every skin node carried a track). Exclusive selection must now report the omitted tracks.
    /// </summary>
    public static ModelDocument WithTracksOnEverySkin(ModelDocument document)
    {
        var animations = document.Animations.Select(clip => new SceneAnimation(clip.Name,
            Enumerable.Range(0, document.Nodes.Count).Select(node => new SceneMorphTrack(node,
                clip.MorphTracks[0].TargetCount, clip.MorphTracks[0].Times, clip.MorphTracks[0].Weights,
                clip.MorphTracks[0].Interpolation)).ToList(),
            clip.ExtrasJson, durationSeconds: null, timing: clip.Timing)).ToList();
        return new ModelDocument(document.SourceFormat, document.Name, document.Scenes, document.Nodes, document.Meshes,
            document.Materials, document.Images, document.Samplers, animations, units: document.Units,
            sourceBasis: document.SourceBasis, layerSets: document.LayerSets);
    }

    /// <summary>Reads the saved GLB and checks selected STEP channels, exact source keys/weights and original clip duration.</summary>
    public static ModelRoot AssertGlbMorphPlayback(ModelDocument document, string path, params int[] selectedNodes)
    {
        var graph = ModelRoot.Load(path);
        var clips = document.Animations.Where(clip =>
            clip.MorphTracks.Any(track => selectedNodes.Contains(track.NodeIndex))).ToArray();
        Assert.Equal(clips.Length, graph.LogicalAnimations.Count);
        for (var index = 0; index < clips.Length; index++)
        {
            var source = clips[index];
            var animation = graph.LogicalAnimations[index];
            var tracks = source.MorphTracks.Where(track => selectedNodes.Contains(track.NodeIndex)).ToArray();
            Assert.Equal(source.Name, animation.Name);
            Assert.Equal(source.MorphTracks.Max(track => track.Times[^1]), animation.Duration);
            Assert.Equal(tracks.Length, animation.Channels.Count);
            foreach (var track in tracks)
            {
                var sampler = animation.FindMorphChannel(graph.LogicalNodes[track.NodeIndex]).GetMorphSampler();
                Assert.Equal(AnimationInterpolationMode.STEP, sampler.InterpolationMode);
                var keys = sampler.GetLinearKeys().ToArray();
                Assert.Equal(track.Times, keys.Select(key => key.Key));
                Assert.Equal(track.Weights, keys.SelectMany(key => key.Value));
            }
            foreach (var node in Enumerable.Range(0, document.Nodes.Count).Except(selectedNodes))
                Assert.Null(animation.FindMorphChannel(graph.LogicalNodes[node]));
        }
        return graph;
    }

    /// <summary>Requires one explicit dropped-track row for each source clip and each unselected skin.</summary>
    public static void AssertExclusiveMorphOmissions(ModelDocument document, IReadOnlyList<ModelFidelityRow> rows,
        params int[] omittedNodes)
    {
        var omissions = rows.Where(row => row.ReasonCode == "draw.exclusive-layer-morph-omitted").ToArray();
        Assert.Equal(document.Animations.Count * omittedNodes.Length, omissions.Length);
        for (var clip = 0; clip < document.Animations.Count; clip++)
        {
            foreach (var node in omittedNodes)
            {
                var layer = Assert.Single(document.LayerSets, candidate => candidate.Members.Contains(node));
                var row = Assert.Single(omissions, candidate =>
                    candidate.Target == new SceneElementRef(SceneElementKind.Animation, clip) &&
                    candidate.Description.Contains($"node {node} in unselected exclusive layer '{layer.Id}'", StringComparison.Ordinal));
                Assert.Equal(ModelFidelityOutcome.Dropped, row.Outcome);
            }
        }
        Assert.DoesNotContain(rows, row => row.ReasonCode == "draw.morph-target-suppressed");
    }

    /// <summary>The Shared Blender writer's plan rows for a document, through its package-only preflight (no Blender runs).</summary>
    public static IReadOnlyList<ModelFidelityRow> BlenderRows(ModelDocument document)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var settings = new ExternalToolSettings(Path.Combine(Path.GetTempPath(), "bmt-shadowkey-blender-rows-unused.json"));
        var writer = new ModelBlendWriter(settings);
        var environment = ModelBlendWriter.PreflightPackage(new Dictionary<string, string>(StringComparer.Ordinal),
            cancellationToken);
        return writer.Plan(document, environment, new ModelConvertOptions(writer.Format), cancellationToken).Fidelity.Rows;
    }

    /// <summary>The app options that set the game option to <paramref name="game" />.</summary>
    public static IReadOnlyDictionary<string, string> Game(string game)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal) { [BethesdaModelRegistration.GameOption] = game };
    }

    /// <summary>
    ///     A candidate of the first <paramref name="prefixLength" /> bytes (default: up to the 64 KiB budget), complete
    ///     when the prefix is the whole stream and shorter than the budget unless <paramref name="isComplete" /> says
    ///     otherwise; the declared length is the stream's unless <paramref name="declaredLength" /> gives another.
    /// </summary>
    public static ModelSourceCandidate Candidate(byte[] bytes, string path = "probe/candidate.bin",
        int? prefixLength = null, bool? isComplete = null, long? declaredLength = null)
    {
        var length = prefixLength ?? Math.Min(bytes.Length, ModelSourceCandidate.MaximumProbeBytes);
        var entry = new AssetEntry(new AssetReference(SourceId, path), declaredLength ?? bytes.Length);
        return new ModelSourceCandidate(entry, bytes.AsSpan(0, length),
            isComplete ?? (length == bytes.Length && length < ModelSourceCandidate.MaximumProbeBytes));
    }

    /// <summary>The SHA-256 of float32 little-endian values.</summary>
    public static string Float32Digest(IEnumerable<float> values)
    {
        using var output = new MemoryStream();
        Span<byte> word = stackalloc byte[4];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteSingleLittleEndian(word, value);
            output.Write(word);
        }

        return Convert.ToHexStringLower(SHA256.HashData(output.ToArray()));
    }

    /// <summary>The SHA-256 of int32 little-endian values.</summary>
    public static string Int32Digest(IEnumerable<int> values)
    {
        using var output = new MemoryStream();
        Span<byte> word = stackalloc byte[4];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt32LittleEndian(word, value);
            output.Write(word);
        }

        return Convert.ToHexStringLower(SHA256.HashData(output.ToArray()));
    }

    /// <summary>The SHA-256 of raw bytes.</summary>
    public static string Digest(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>A primitive's vertex positions, x, y, z per vertex.</summary>
    public static IEnumerable<float> Positions(ScenePrimitive primitive)
    {
        return primitive.Vertices.SelectMany(static v => new[] { v.Position.X, v.Position.Y, v.Position.Z });
    }

    /// <summary>A primitive's texture coordinates, u, v per vertex.</summary>
    public static IEnumerable<float> TexCoords(ScenePrimitive primitive)
    {
        return primitive.Vertices.SelectMany(static v => new[] { v.TexCoord.X, v.TexCoord.Y });
    }

    /// <summary>Every absolute target position of a primitive, target-major.</summary>
    public static IEnumerable<float> Targets(ScenePrimitive primitive)
    {
        return primitive.MorphTargets.SelectMany(static t => t.AbsolutePositions!)
            .SelectMany(static p => new[] { p.X, p.Y, p.Z });
    }

    /// <summary>A decoded PNG: its size and color type, the PLTE and tRNS, and the samples (top row first).</summary>
    /// <param name="Width">The width.</param>
    /// <param name="Height">The height.</param>
    /// <param name="ColorType">The PNG color type (3 indexed, 2 RGB).</param>
    /// <param name="Palette">The PLTE bytes, or empty.</param>
    /// <param name="Alpha">The tRNS bytes, or empty.</param>
    /// <param name="Samples">Indices (color type 3) or RGB bytes (color type 2), unfiltered.</param>
    public sealed record DecodedPng(int Width, int Height, int ColorType, byte[] Palette, byte[] Alpha, byte[] Samples)
    {
        /// <summary>The image as RGBA8, top row first (palette and tRNS applied; alpha 255 where not stated).</summary>
        public byte[] Rgba()
        {
            var rgba = new byte[Width * Height * 4];
            for (var i = 0; i < Width * Height; i++)
            {
                if (ColorType == 3)
                {
                    var index = Samples[i];
                    rgba[i * 4] = Palette[index * 3];
                    rgba[i * 4 + 1] = Palette[index * 3 + 1];
                    rgba[i * 4 + 2] = Palette[index * 3 + 2];
                    rgba[i * 4 + 3] = index < Alpha.Length ? Alpha[index] : (byte)255;
                }
                else
                {
                    rgba[i * 4] = Samples[i * 3];
                    rgba[i * 4 + 1] = Samples[i * 3 + 1];
                    rgba[i * 4 + 2] = Samples[i * 3 + 2];
                    rgba[i * 4 + 3] = 255;
                }
            }

            return rgba;
        }
    }

    /// <summary>
    ///     Decodes an 8-bit indexed or RGB PNG with every filter type (a reader of its own: chunk walk, CRC skipped,
    ///     one zlib stream over the IDAT chunks, per-row unfiltering). Throws on anything else.
    /// </summary>
    public static DecodedPng DecodePng(ReadOnlySpan<byte> png)
    {
        if (!png[..8].SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            throw new InvalidDataException("Not a PNG.");
        }

        var offset = 8;
        int width = 0, height = 0, colorType = -1;
        byte[] palette = [], alpha = [];
        using var idat = new MemoryStream();
        while (offset < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            var type = System.Text.Encoding.ASCII.GetString(png.Slice(offset + 4, 4));
            var data = png.Slice(offset + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    if (data[8] != 8)
                    {
                        throw new InvalidDataException("Only 8-bit samples are decoded.");
                    }

                    colorType = data[9];
                    break;
                case "PLTE":
                    palette = data.ToArray();
                    break;
                case "tRNS":
                    alpha = data.ToArray();
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
            }

            offset += length + 12;
        }

        var bytesPerPixel = colorType switch
        {
            3 => 1,
            2 => 3,
            _ => throw new InvalidDataException("Only indexed and RGB PNGs are decoded.")
        };
        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        var raw = inflated.ToArray();
        var stride = width * bytesPerPixel;
        var samples = new byte[stride * height];
        for (var row = 0; row < height; row++)
        {
            var filter = raw[row * (stride + 1)];
            for (var x = 0; x < stride; x++)
            {
                var value = raw[row * (stride + 1) + 1 + x];
                int left = x >= bytesPerPixel ? samples[row * stride + x - bytesPerPixel] : 0;
                int up = row > 0 ? samples[(row - 1) * stride + x] : 0;
                int upLeft = row > 0 && x >= bytesPerPixel ? samples[(row - 1) * stride + x - bytesPerPixel] : 0;
                var predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException("Unknown PNG filter.")
                };
                samples[row * stride + x] = (byte)(value + predictor);
            }
        }

        return new DecodedPng(width, height, colorType, palette, alpha, samples);
    }

    /// <summary>The standard payload of an image, decoded.</summary>
    public static DecodedPng StandardPng(SceneImage image)
    {
        return DecodePng(image.Source!.StandardPayload!.Payload.Content);
    }

    /// <summary>The RGBA8 bytes of 0x0RGB texels with each channel times <paramref name="multiplier" />, alpha 255.</summary>
    public static byte[] ExpandTexels(IEnumerable<ushort> texels, int multiplier = 17)
    {
        return texels.SelectMany(t => new[]
        {
            (byte)(((t >> 8) & 15) * multiplier), (byte)(((t >> 4) & 15) * multiplier), (byte)((t & 15) * multiplier),
            (byte)255
        }).ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>A cache scope that holds nothing (the readers use none).</summary>
    internal sealed class NoCacheScope : IModelReadCacheScope
    {
        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
