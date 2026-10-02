using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Ddx;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using DDXConv;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Slfx77.Multitool.Media.Images;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Turns every texture path a typed material binds into one <see cref="SceneImage" /> (plan section 4; design section
///     5.1): resolution through the read context's companion resolver, bytes read once through the
///     <see cref="NifModelReadCache" /> and deduplicated by SHA-256, descriptors without pixel decoding, the DDX gate, and
///     the Xbox specular companion rule.
/// </summary>
/// <remarks>
///     <para>
///         Resolution: the authored string goes to <see cref="ModelReadContext.CompanionResolver" /> unchanged (BMT's
///         <c>BethesdaTextureCompanions</c> normalizes it and falls back from <c>.dds</c> to <c>.ddx</c>). Shared's default
///         resolver accepts basenames only and throws <see cref="ArgumentException" /> for a path, so the source retries
///         with the basename. No match is a missing image with a reason; several are a missing image, "ambiguous". The
///         image's Name is always the authored string; the lookup key (<see cref="NifTexturePathUtility.Normalize" />)
///         only deduplicates requests.
///     </para>
///     <para>
///         Content: a DDS keeps its original bytes with a descriptor from Shared's inspection (header, extent and the BC1
///         selector scan; no decode), or BMT's minimal header for cube maps and formats inspection refuses; BC5 gets a
///         <see cref="SceneNormalZReconstruction" /> (Assumed). A DDX keeps its original bytes; its header is read by
///         <see cref="NifDdxHeader" />, DDXConv relayouts it with a fresh <see cref="DecodeDiagnostics" />, fabricated
///         all-zero levels beyond the declared count are trimmed, and <see cref="NifDdxGate" /> decides between a
///         <see cref="SceneStandardImagePayload" /> and a <c>bmt.ddx-recovery</c> derivation. A relayout that throws
///         leaves the original alone, with a diagnostic. Relayout is allowed during inspection (owner decision,
///         2026-09-24: block relayout is not pixel decoding); nothing here decodes a pixel, merges channels or makes a
///         PNG, and all image work goes through <see cref="INifTextureCodec" />.
///     </para>
///     <para>
///         The Xbox specular companion (<see cref="TryResolveSpecularCompanion" />) applies only to a normal-map slot
///         whose file resolved to a DDX and whose path follows the <c>_n</c> convention
///         (<see cref="NormalMapMerge.IsNormalMapPath" />): <see cref="NormalMapMerge.ComputeSpecularPath" /> names the
///         companion, which becomes its own image with origin PlatformCompanion when found. BC5 and BC4 are never merged.
///     </para>
/// </remarks>
internal sealed class NifModelTextureSource
{
    /// <summary>The native-state kind of the per-image rows.</summary>
    public const string TextureKind = "bmt.nif.texture";

    /// <summary>The payload version of <see cref="TextureKind" /> rows.</summary>
    public const int TexturePayloadVersion = 1;

    /// <summary>Diagnostic code for a missing or ambiguous texture.</summary>
    public const string MissingDiagnostic = "bmt.nif.texture-missing";

    /// <summary>Diagnostic code for a DDS that Shared's inspection refused.</summary>
    public const string DdsHeaderDiagnostic = "bmt.nif.dds-header";

    /// <summary>Diagnostic code for a DDX whose header could not be read.</summary>
    public const string DdxHeaderDiagnostic = "bmt.nif.ddx-header";

    /// <summary>Diagnostic code for a DDX that failed the gate (kept as a recovery derivation).</summary>
    public const string DdxGateDiagnostic = "bmt.nif.ddx-gate";

    /// <summary>Diagnostic code for a DDX whose relayout threw (kept as its original only).</summary>
    public const string DdxRelayoutDiagnostic = "bmt.nif.ddx-relayout-failed";

    /// <summary>The derivation recipe of a gate-failing relayout.</summary>
    public const string DdxRecoveryRecipe = "bmt.ddx-recovery";

    /// <summary>The note of a gate-passing relayout's standard payload.</summary>
    public const string RelayoutNote = "DDXConv relayout: untile and endian swap, no pixel change";

    private const string NormalZEvidence =
        "BC5 (ATI2) stores the normal's X and Y; Z is reconstructed in the positive hemisphere from unsigned R/G " +
        "(Assumed, plan section 4)";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly Dictionary<(string Sha256, SceneImageOrigin Origin), int> _bySha = [];
    private readonly Dictionary<(AssetReference Reference, SceneImageOrigin Origin), int> _byReference = [];
    private readonly Dictionary<string, int?> _byRequest = new(StringComparer.Ordinal);
    private readonly NifModelReadCache _cache;
    private readonly CancellationToken _cancellationToken;
    private readonly INifTextureCodec _codec;
    private readonly Dictionary<int, int?> _companions = [];
    private readonly ModelReadContext _context;
    private readonly NifModelDiagnosticSink _diagnostics = new();
    private readonly Dictionary<int, NifModelBlockDisposition> _dispositions = [];
    private readonly Dictionary<int, int> _imageByFedBlock = [];
    private readonly List<NifModelTextureImage> _images = [];
    private readonly NifModelReadState _state;

    /// <summary>Creates the source for one read.</summary>
    public NifModelTextureSource(NifModelReadState state, ModelReadContext context, NifModelReadCache cache,
        INifTextureCodec codec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(codec);
        _state = state;
        _context = context;
        _cache = cache;
        _codec = codec;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The DDXConv build the relayout evidence names.</summary>
    public static string DdxConvVersion { get; } = "DDXConv " +
        (typeof(DdxParser).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
         typeof(DdxParser).Assembly.GetName().Version?.ToString() ?? "unknown");

    /// <summary>The number of images so far.</summary>
    public int Count => _images.Count;

    /// <summary>Resolves (or reuses) the image an authored texture path names.</summary>
    /// <param name="authored">The authored path, exactly as stored.</param>
    /// <param name="raw">The authored bytes (for native state when a byte is at or above 0x80).</param>
    /// <param name="role">The layer role binding it.</param>
    /// <param name="sourceBlock">The block that authored it (NiSourceTexture, texture set or shader).</param>
    /// <returns>The document image index; a missing image when it does not resolve.</returns>
    public int Resolve(string authored, ReadOnlyMemory<byte> raw, SceneTextureLayerRole role, int sourceBlock)
    {
        ArgumentNullException.ThrowIfNull(authored);
        var index = ResolveCore(authored, raw, role, sourceBlock, SceneImageOrigin.SourceReference, null, true)!.Value;
        if (sourceBlock >= 0 && string.Equals(_state.Blocks[sourceBlock].Type, "NiSourceTexture", StringComparison.Ordinal))
        {
            _dispositions[sourceBlock] = NifModelBlockDisposition.Typed;
        }

        return index;
    }

    /// <summary>
    ///     A missing image for an NiSourceTexture that embeds its pixels (Use External 0): embedded NiPixelData is
    ///     later-cut(2), so the texture block stays NativeOnly while the layer keeps its binding.
    /// </summary>
    public int ResolveEmbedded(string authored, int sourceBlock, int pixelDataBlock)
    {
        ArgumentNullException.ThrowIfNull(authored);
        var key = string.Create(CultureInfo.InvariantCulture, $"embedded|{sourceBlock}");
        if (!_byRequest.TryGetValue(key, out var known) || known is not { } index)
        {
            var reason = string.Create(CultureInfo.InvariantCulture,
                $"NiSourceTexture block {sourceBlock} embeds its image (Pixel Data block {pixelDataBlock}); " +
                $"embedded pixel data is later-cut(2)");
            var name = authored.Length > 0
                ? authored
                : string.Create(CultureInfo.InvariantCulture, $"block:{sourceBlock}");
            index = AddMissing(name, "NiPixelData", SceneImageOrigin.SourceReference, reason, sourceBlock,
                new JsonObject { ["outcome"] = "embedded" });
            _byRequest[key] = index;
        }

        _images[index].AddRequest(RequestFacts(authored, Encoding.Latin1.GetBytes(authored), "",
            SceneTextureLayerRole.BaseColor, sourceBlock, null));
        _imageByFedBlock.TryAdd(sourceBlock, index);
        if (!_dispositions.TryGetValue(sourceBlock, out var existing) || !existing.IsTyped)
        {
            _dispositions[sourceBlock] = NifModelBlockDisposition.NativeOnly(NifModelCoverage.PixelDataReason);
        }

        return index;
    }

    /// <summary>
    ///     The Xbox specular companion of a normal-map image, when the image is a DDX named by the <c>_n</c> convention and
    ///     its <c>_s</c> companion resolves; null otherwise (nothing is added for a companion that does not resolve).
    /// </summary>
    public int? TryResolveSpecularCompanion(int normalImage, int sourceBlock)
    {
        if (_companions.TryGetValue(normalImage, out var known))
        {
            return known;
        }

        var normal = _images[normalImage];
        int? result = null;
        if (IsXboxNormalMap(normalImage))
        {
            var specularPath = NormalMapMerge.ComputeSpecularPath(normal.Reference!.Path)!.Replace('\\', '/');
            result = ResolveCore(specularPath, Encoding.Latin1.GetBytes(specularPath), SceneTextureLayerRole.Specular,
                sourceBlock, SceneImageOrigin.PlatformCompanion, normalImage, false);
            normal.Facts["xboxSpecularCompanion"] = new JsonObject
            {
                ["path"] = specularPath,
                ["rule"] = "NormalMapMerge.ComputeSpecularPath of the resolved _n.ddx",
                ["image"] = result
            };
        }

        _companions[normalImage] = result;
        return result;
    }

    /// <summary>True when the image resolved to a DDX whose path follows the <c>_n</c> normal-map convention.</summary>
    public bool IsXboxNormalMap(int image)
    {
        var record = _images[image];
        return record.Reference is { } reference && string.Equals(record.Container, "ddx", StringComparison.Ordinal) &&
               NormalMapMerge.IsNormalMapPath(reference.Path);
    }

    /// <summary>True when the image is BC5 (a DDX with format byte 0x71, or a DDS whose compression is BC5).</summary>
    public bool IsBc5(int image)
    {
        var record = _images[image];
        return record.DdxFormatByte == 0x71 || string.Equals(record.DdsCompression, "BC5", StringComparison.Ordinal);
    }

    /// <summary>Records a note on an image's native row (for example why a specular layer was not bound).</summary>
    public void Note(int image, string key, JsonNode? value)
    {
        _images[image].Facts[key] = value;
    }

    /// <summary>Finishes the read: images, native rows, dispositions and diagnostics.</summary>
    public NifModelTextureResult Complete()
    {
        var rows = new List<SceneNativeState>(_images.Count);
        foreach (var record in _images)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new SceneNativeState(new SceneElementRef(SceneElementKind.Image, record.Index), TextureKind,
                TexturePayloadVersion, Payload(record).ToJsonString(), Location(record)));
        }

        return new NifModelTextureResult(_images.Select(r => r.Image).ToArray(), rows.AsReadOnly(), _dispositions,
            _imageByFedBlock, _diagnostics.ToList());
    }

    private int? ResolveCore(string name, ReadOnlyMemory<byte> raw, SceneTextureLayerRole role, int sourceBlock,
        SceneImageOrigin origin, int? companionOf, bool addMissing)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var lookup = LookupKey(name);
        var request = RequestFacts(name, raw, lookup, role, sourceBlock, companionOf);
        var requestKey = origin + "|" + lookup;
        if (_byRequest.TryGetValue(requestKey, out var known))
        {
            if (known is { } existing)
            {
                Fed(existing, request, sourceBlock);
            }

            return known;
        }

        var (matches, retried, resolverFailure) = Candidates(name);
        int? index;
        if (matches.Count == 1)
        {
            var match = matches[0];
            index = _byReference.TryGetValue((match.Reference, origin), out var same)
                ? same
                : AddResolved(name, match, origin, lookup, retried, sourceBlock, addMissing);
        }
        else if (!addMissing)
        {
            index = null;
        }
        else
        {
            var reason = matches.Count == 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"no companion matches '{name}' (lookup key '{lookup}'" +
                    $"{(retried is null ? "" : ", basename retry '" + retried + "'")})" +
                    $"{(resolverFailure is null ? "" : ": " + resolverFailure)}")
                : string.Create(CultureInfo.InvariantCulture,
                    $"'{name}' is ambiguous across {matches.Count} occurrences: " +
                    $"{string.Join(", ", matches.Take(4).Select(m => m.Reference.Path))}" +
                    $"{(matches.Count > 4 ? ", ..." : "")}");
            index = AddMissing(name, ContainerFromName(name), origin, reason, sourceBlock, new JsonObject
            {
                ["outcome"] = matches.Count == 0 ? "missing" : "ambiguous",
                ["lookupKey"] = lookup,
                ["basenameRetry"] = retried,
                ["candidates"] = matches.Count
            });
        }

        _byRequest[requestKey] = index;
        if (index is { } resolved)
        {
            Fed(resolved, request, sourceBlock);
        }

        return index;
    }

    private void Fed(int index, JsonObject request, int sourceBlock)
    {
        _images[index].AddRequest(request);
        if (sourceBlock >= 0)
        {
            _imageByFedBlock.TryAdd(sourceBlock, index);
        }
    }

    private (IReadOnlyList<ModelSourceItem> Matches, string? Retried, string? Failure) Candidates(string name)
    {
        try
        {
            return (Call(name), null, null);
        }
        catch (ArgumentException)
        {
            var basename = Basename(name);
            if (basename.Length == 0 || string.Equals(basename, name, StringComparison.Ordinal))
            {
                return (Array.Empty<ModelSourceItem>(), null, "the resolver refused the name");
            }

            try
            {
                return (Call(basename), basename, null);
            }
            catch (ArgumentException failure)
            {
                return (Array.Empty<ModelSourceItem>(), basename, "the resolver refused the basename: " + failure.Message);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                return (Array.Empty<ModelSourceItem>(), basename, "the resolver failed: " + failure.Message);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return (Array.Empty<ModelSourceItem>(), null, "the resolver failed: " + failure.Message);
        }
    }

    private IReadOnlyList<ModelSourceItem> Call(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        return _context.CompanionResolver(_context.Item, name, _cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    private int? AddResolved(string name, ModelSourceItem match, SceneImageOrigin origin, string lookup,
        string? retried, int sourceBlock, bool addMissing)
    {
        var resolution = new JsonObject
        {
            ["outcome"] = "resolved",
            ["lookupKey"] = lookup,
            ["basenameRetry"] = retried,
            ["sourceId"] = match.Reference.SourceId,
            ["path"] = match.Reference.Path,
            ["occurrenceId"] = match.Reference.OccurrenceId,
            ["provenance"] = match.Entry.Provenance,
            ["extensionFallback"] = !string.Equals(Path.GetExtension(match.Reference.Path),
                Path.GetExtension(name.Replace('\\', '/')), StringComparison.OrdinalIgnoreCase)
        };
        var read = _cache.ReadCompanion(match, _cancellationToken);
        if (read.Bytes is not { } bytes)
        {
            if (!addMissing)
            {
                return null;
            }

            resolution["outcome"] = "unread";
            var index = AddMissing(name, ContainerFromName(match.Reference.Path), origin,
                $"'{name}' resolved to {match.Reference.Path}, but {read.FailureReason}", sourceBlock, resolution);
            _byReference[(match.Reference, origin)] = index;
            return index;
        }

        resolution["bytes"] = bytes.Length;
        resolution["sha256"] = read.Sha256;
        if (_bySha.TryGetValue((read.Sha256!, origin), out var same))
        {
            _byReference[(match.Reference, origin)] = same;
            if (_images[same].Facts["sameContent"] is not JsonArray aliases)
            {
                aliases = new JsonArray();
                _images[same].Facts["sameContent"] = aliases;
            }

            if (aliases.Count < NifModelNativeValues.MaximumInlineElements)
            {
                aliases.Add(resolution);
            }

            return same;
        }

        var facts = new JsonObject { ["resolution"] = resolution };
        var container = DetectContainer(bytes, match.Reference.Path);
        var location = new SceneSourceLocation(match.Reference.SourceId, assetReference: match.Reference);
        SceneImageSource source;
        byte? ddxFormat = null;
        string? ddsCompression = null;
        switch (container)
        {
            case "dds":
                (source, ddsCompression) = BuildDds(name, bytes, location, origin, facts);
                break;
            case "ddx":
                (source, ddxFormat) = BuildDdx(name, bytes, location, origin, facts);
                break;
            default:
                source = new SceneImageSource(container, new SceneTextureDescriptor(),
                    new SceneImagePayload(container, bytes), location, origin: origin);
                break;
        }

        var record = new NifModelTextureImage(_images.Count, new SceneImage(name, source), container, origin)
        {
            Reference = match.Reference,
            FirstSourceBlock = sourceBlock,
            DdxFormatByte = ddxFormat,
            DdsCompression = ddsCompression
        };
        foreach (var (key, value) in facts.ToArray())
        {
            facts.Remove(key);
            record.Facts[key] = value;
        }

        _images.Add(record);
        _bySha[(read.Sha256!, origin)] = record.Index;
        _byReference[(match.Reference, origin)] = record.Index;
        return record.Index;
    }

    private (SceneImageSource Source, string? Compression) BuildDds(string name, byte[] bytes,
        SceneSourceLocation location, SceneImageOrigin origin, JsonObject facts)
    {
        var original = new SceneImagePayload("dds", bytes);
        try
        {
            var info = _codec.InspectDds(bytes, _cancellationToken);
            facts["dds"] = new JsonObject
            {
                ["descriptor"] = "DdsImageDecoder.Inspect (header, level-zero extent, BC1 selector scan; no decode)",
                ["formatId"] = info.FormatId,
                ["width"] = info.Width,
                ["height"] = info.Height,
                ["declaredMips"] = info.DeclaredMipCount,
                ["storedChannels"] = info.StoredChannels,
                ["compression"] = info.Compression,
                ["bitsPerPixel"] = info.BitsPerPixel
            };
            return (new SceneImageSource("dds", NifTextureDescriptors.FromDdsInfo(info, bytes), original, location,
                displayTransforms: NormalZ(info.Compression), origin: origin), info.Compression);
        }
        catch (Exception failure) when (failure is InvalidDataException or NotSupportedException or OverflowException)
        {
            if (NifDdsHeader.TryRead(bytes, out var header))
            {
                var headerFacts = header.ToJson();
                headerFacts["descriptor"] = "BMT minimal DDS header (Shared inspection refused the surface)";
                headerFacts["inspection"] = failure.Message;
                facts["dds"] = headerFacts;
                if (!header.IsCube)
                {
                    _diagnostics.Add(DdsHeaderDiagnostic,
                        $"Texture '{name}': the shared DDS inspection refused it ({failure.Message}); its descriptor " +
                        "comes from the header alone.");
                }

                return (new SceneImageSource("dds", NifTextureDescriptors.FromDdsHeader(header), original, location,
                    displayTransforms: NormalZ(header.Compression), origin: origin), header.Compression);
            }

            facts["dds"] = new JsonObject { ["inspection"] = failure.Message, ["header"] = "unreadable" };
            _diagnostics.Add(DdsHeaderDiagnostic,
                $"Texture '{name}' has no readable DDS header ({failure.Message}); its original bytes are kept.");
            return (new SceneImageSource("dds", new SceneTextureDescriptor(), original, location, origin: origin),
                null);
        }
    }

    private (SceneImageSource Source, byte? Format) BuildDdx(string name, byte[] bytes, SceneSourceLocation location,
        SceneImageOrigin origin, JsonObject facts)
    {
        var original = new SceneImagePayload("ddx", bytes);
        if (!NifDdxHeader.TryRead(bytes, out var header, out var headerFailure))
        {
            facts["ddx"] = new JsonObject { ["header"] = headerFailure };
            _diagnostics.Add(DdxHeaderDiagnostic,
                $"Texture '{name}': {headerFailure}; its original bytes are kept without a descriptor.");
            return (new SceneImageSource("ddx", new SceneTextureDescriptor(), original, location, origin: origin), null);
        }

        var ddxFacts = header.ToJson();
        facts["ddx"] = ddxFacts;
        var diagnostics = new DecodeDiagnostics();
        byte[] relayout;
        try
        {
            relayout = _codec.RelayoutDdx(bytes, diagnostics);
        }
        catch (Exception failure) when (failure is not OperationCanceledException and not OutOfMemoryException)
        {
            ddxFacts["relayout"] = new JsonObject
            {
                ["error"] = failure.GetType().Name,
                ["message"] = failure.Message,
                ["ddxconv"] = DdxConvVersion
            };
            _diagnostics.Add(DdxRelayoutDiagnostic,
                $"Texture '{name}': the DDXConv relayout failed ({failure.GetType().Name}: {failure.Message}); the " +
                "original DDX is kept without a standard payload or a recovery.");
            return (new SceneImageSource("ddx",
                NifTextureDescriptors.FromDdxHeader(header, null, SceneTextureMipPresence.Unknown), original, location,
                origin: origin), header.FormatByte);
        }

        _cancellationToken.ThrowIfCancellationRequested();
        var counters = NifDdxCounters.From(diagnostics);
        var trim = NifDdsMipTrim.Trim(relayout, header.DeclaredMipCount);
        var output = trim.Bytes;
        var relayoutFacts = new JsonObject
        {
            ["ddxconv"] = DdxConvVersion,
            ["counters"] = counters.ToJson(),
            ["trim"] = trim.ToJson(),
            ["outputBytes"] = output.Length
        };
        ddxFacts["relayout"] = relayoutFacts;
        if (!_cache.TryAccount(output.Length))
        {
            relayoutFacts["kept"] = "no: the output exceeds the companion byte budget";
            _diagnostics.Add(DdxRelayoutDiagnostic,
                $"Texture '{name}': the relayout output ({output.Length} bytes) exceeds the companion byte budget; " +
                "the original DDX is kept without a standard payload or a recovery.");
            return (new SceneImageSource("ddx",
                NifTextureDescriptors.FromDdxHeader(header, null, SceneTextureMipPresence.Unknown), original, location,
                origin: origin), header.FormatByte);
        }

        DdsImageInfo? info = null;
        string? inspectionFailure = null;
        try
        {
            info = _codec.InspectDds(output, _cancellationToken);
        }
        catch (Exception failure) when (failure is InvalidDataException or NotSupportedException or OverflowException)
        {
            inspectionFailure = failure.Message;
        }

        NifDdsHeader.TryRead(output, out var outputHeader);
        var outputDescriptor = info is not null
            ? NifTextureDescriptors.FromDdsInfo(info, output)
            : outputHeader is not null
                ? NifTextureDescriptors.FromDdsHeader(outputHeader)
                : new SceneTextureDescriptor();
        var missingLevels = info is null
            ? 0
            : outputDescriptor.MipLevels.Count(m => m.Presence != SceneTextureMipPresence.Authored);
        var outputFacts = new NifDdxOutputFacts(outputHeader?.Width ?? 0, outputHeader?.Height ?? 0, trim.Levels,
            trim.TrimmedLevels, trim.UndeclaredNonZeroLevels, info is not null, inspectionFailure, missingLevels);
        var gate = NifDdxGate.Evaluate(header, counters, outputFacts);
        relayoutFacts["output"] = outputFacts.ToJson();
        relayoutFacts["gate"] = new JsonObject
        {
            ["passed"] = gate.Passed,
            ["failures"] = new JsonArray(gate.Failures.Select(f => (JsonNode?)f).ToArray())
        };
        var channels = info is not null ? NifTextureDescriptors.Channels(info.StoredChannels) : (SceneTextureChannels?)null;
        var ddxDescriptor = NifTextureDescriptors.FromDdxHeader(header, channels,
            gate.Passed ? SceneTextureMipPresence.Authored : SceneTextureMipPresence.Unknown);
        var outputPayload = new SceneImagePayload("dds", output);
        relayoutFacts["outputSha256"] = outputPayload.Sha256;
        var transforms = NormalZ(info?.Compression ?? outputHeader?.Compression);
        if (gate.Passed)
        {
            var evidence = string.Create(CultureInfo.InvariantCulture,
                $"{DdxConvVersion}; counters: {counters.ToEvidence()}; checks: format " +
                $"{NifDdxHeader.Hex(header.FormatByte)} admitted; {outputFacts.Width}x{outputFacts.Height} equals the " +
                $"header; {outputFacts.MipCount} mip level(s) equal the declared {header.DeclaredMipCount}; " +
                $"{trim.TrimmedLevels} fabricated all-zero level(s) trimmed ({trim.TrimmedBytes} bytes)");
            var standard = new SceneStandardImagePayload(outputPayload, outputDescriptor, RelayoutNote, evidence);
            relayoutFacts["representation"] = "standard payload";
            return (new SceneImageSource("ddx", ddxDescriptor, original, location, standard, transforms,
                origin: origin), header.FormatByte);
        }

        var details = new JsonObject
        {
            ["ddxconv"] = DdxConvVersion,
            ["counters"] = counters.ToJson(),
            ["header"] = header.ToJson(),
            ["output"] = outputFacts.ToJson(),
            ["trim"] = trim.ToJson()
        };
        var derivation = new SceneImageDerivation(DdxRecoveryRecipe, [], details.ToJsonString(), gate.Reason,
            outputPayload, outputDescriptor);
        relayoutFacts["representation"] = "recovery derivation";
        _diagnostics.Add(DdxGateDiagnostic,
            $"Texture '{name}' failed the DDX gate and is kept as a {DdxRecoveryRecipe} derivation: {gate.Reason}");
        return (new SceneImageSource("ddx", ddxDescriptor, original, location, displayTransforms: transforms,
            derivation: derivation, origin: origin), header.FormatByte);
    }

    private int AddMissing(string name, string container, SceneImageOrigin origin, string reason, int sourceBlock,
        JsonObject resolution)
    {
        var source = new SceneImageSource(container, new SceneTextureDescriptor(), null, origin: origin,
            missingReason: reason);
        var record = new NifModelTextureImage(_images.Count, new SceneImage(name, source), container, origin)
        {
            FirstSourceBlock = sourceBlock
        };
        record.Facts["resolution"] = resolution;
        record.Facts["missingReason"] = reason;
        _images.Add(record);
        _diagnostics.Add(MissingDiagnostic, $"Texture '{name}' is missing: {reason}.");
        return record.Index;
    }

    private JsonObject RequestFacts(string authored, ReadOnlyMemory<byte> raw, string lookup,
        SceneTextureLayerRole role, int sourceBlock, int? companionOf)
    {
        var request = new JsonObject
        {
            ["authored"] = raw.Length > 0 ? NifModelNativeValues.Text(raw.Span) : authored,
            ["lookupKey"] = lookup,
            ["role"] = role.ToString(),
            ["sourceBlock"] = sourceBlock,
            ["sourceType"] = sourceBlock >= 0 ? _state.Blocks[sourceBlock].Type : null
        };
        if (companionOf is { } normal)
        {
            request["companionOf"] = normal;
        }

        return request;
    }

    private JsonObject Payload(NifModelTextureImage record)
    {
        var payload = new JsonObject
        {
            ["index"] = record.Index,
            ["name"] = record.Image.Name,
            ["origin"] = record.Origin.ToString(),
            ["container"] = record.Container,
            ["requestCount"] = record.RequestCount,
            ["requests"] = record.Requests.DeepClone()
        };
        foreach (var (key, value) in record.Facts)
        {
            payload[key] = value?.DeepClone();
        }

        return payload;
    }

    private SceneSourceLocation Location(NifModelTextureImage record)
    {
        if (record.Reference is { } reference)
        {
            return new SceneSourceLocation(reference.SourceId, assetReference: reference);
        }

        var owner = _state.Item.Reference;
        if (record.FirstSourceBlock >= 0)
        {
            var block = _state.Blocks[record.FirstSourceBlock];
            return new SceneSourceLocation(owner.SourceId, NifModelCoverage.Identity(block.Index), block.Offset,
                block.Size, owner);
        }

        return new SceneSourceLocation(owner.SourceId, assetReference: owner);
    }

    private static IEnumerable<SceneImageDisplayTransform> NormalZ(string? compression)
    {
        return string.Equals(compression, "BC5", StringComparison.Ordinal)
            ? [new SceneNormalZReconstruction(false, true, SceneValueProvenance.Assumed, NormalZEvidence)]
            : [];
    }

    private static string LookupKey(string name)
    {
        return NifTexturePathUtility.Normalize(name);
    }

    private static string Basename(string name)
    {
        var separator = name.LastIndexOfAny(['/', '\\']);
        return (separator < 0 ? name : name[(separator + 1)..]).Trim();
    }

    private static string DetectContainer(ReadOnlySpan<byte> bytes, string path)
    {
        if (bytes.StartsWith("DDS "u8))
        {
            return "dds";
        }

        if (bytes.StartsWith("3XDO"u8) || bytes.StartsWith("3XDR"u8))
        {
            return "ddx";
        }

        return bytes.StartsWith(PngSignature) ? "png" : ContainerFromName(path);
    }

    private static string ContainerFromName(string name)
    {
        var extension = Path.GetExtension(name.Replace('\\', '/')).TrimStart('.').ToLowerInvariant();
        return extension.Length is > 0 and <= 16 && extension.All(char.IsAsciiLetterOrDigit) ? extension : "unknown";
    }
}
