using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

internal sealed class RuntimeLandVisualReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;
    private readonly Dictionary<uint, RuntimeLandTextureRead?> _runtimeLandTextureByPointer = new();
    private readonly Dictionary<uint, TextureSetRecord?> _runtimeTextureSetByPointer = new();

    public RuntimeLandVisualExtraction Read(byte[] loadedDataBuffer)
    {
        var layers = new List<LandTextureLayer>();
        var landTextures = new Dictionary<uint, LandscapeTextureRecord>();
        var textureSets = new Dictionary<uint, TextureSetRecord>();

        for (var quadrant = 0; quadrant < LoadedDataQuadCount; quadrant++)
        {
            var texturePointer = BinaryUtils.ReadUInt32BE(
                loadedDataBuffer,
                LoadedDataDefaultQuadTextureOffset + quadrant * 4);
            var textureRead = TryReadRuntimeLandTexture(texturePointer);
            if (textureRead == null)
            {
                continue;
            }

            var texture = textureRead.LandTexture;
            landTextures.TryAdd(texture.FormId, texture);
            if (textureRead.TextureSet is { } textureSet)
            {
                textureSets.TryAdd(textureSet.FormId, textureSet);
            }

            layers.Add(new LandTextureLayer
            {
                Kind = LandTextureLayerKind.Base,
                TextureFormId = texture.FormId,
                Quadrant = (byte)quadrant,
                PlatformFlag = 0,
                Layer = 0
            });
        }

        for (var quadrant = 0; quadrant < LoadedDataQuadCount; quadrant++)
        {
            var textureArrayPointer = BinaryUtils.ReadUInt32BE(
                loadedDataBuffer,
                LoadedDataQuadTextureArrayOffset + quadrant * 4);
            var percentArrayPointer = BinaryUtils.ReadUInt32BE(
                loadedDataBuffer,
                LoadedDataPercentArraysOffset + quadrant * 4);
            if (textureArrayPointer == 0 || percentArrayPointer == 0)
            {
                continue;
            }

            var textureArrayFileOffset = _context.VaToFileOffset(textureArrayPointer);
            var percentArrayFileOffset = _context.VaToFileOffset(percentArrayPointer);
            if (textureArrayFileOffset is not long textureArrayOffset ||
                percentArrayFileOffset is not long percentArrayOffset)
            {
                continue;
            }

            var texturePointerBytes = _context.ReadBytes(textureArrayOffset, MaxTextureArrayPointersToSample * 4);
            var percentPointerBytes = _context.ReadBytes(percentArrayOffset, MaxTextureArrayPointersToSample * 4);
            if (texturePointerBytes == null || percentPointerBytes == null)
            {
                continue;
            }

            for (var layerIndex = 0; layerIndex < MaxTextureArrayPointersToSample; layerIndex++)
            {
                var texturePointer = BinaryUtils.ReadUInt32BE(texturePointerBytes, layerIndex * 4);
                var percentPointer = BinaryUtils.ReadUInt32BE(percentPointerBytes, layerIndex * 4);
                if (texturePointer == 0 && percentPointer == 0)
                {
                    break;
                }

                var textureRead = TryReadRuntimeLandTexture(texturePointer);
                if (textureRead == null)
                {
                    continue;
                }

                var blendEntries = ReadRuntimeTextureBlendEntries(percentPointer);
                if (blendEntries.Count == 0)
                {
                    continue;
                }

                var texture = textureRead.LandTexture;
                landTextures.TryAdd(texture.FormId, texture);
                if (textureRead.TextureSet is { } textureSet)
                {
                    textureSets.TryAdd(textureSet.FormId, textureSet);
                }

                layers.Add(new LandTextureLayer
                {
                    Kind = LandTextureLayerKind.Alpha,
                    TextureFormId = texture.FormId,
                    Quadrant = (byte)quadrant,
                    PlatformFlag = 0,
                    Layer = (ushort)Math.Min(layerIndex, ushort.MaxValue),
                    BlendEntries = blendEntries
                });
            }
        }

        var visualData = layers.Count > 0
            ? new LandVisualData
            {
                TextureLayers = layers,
                Source = "runtime-land"
            }
            : null;

        return new RuntimeLandVisualExtraction(visualData, landTextures.Values.ToList(), textureSets.Values.ToList());
    }

    private List<LandTextureBlendEntry> ReadRuntimeTextureBlendEntries(uint percentPointer)
    {
        if (percentPointer == 0)
        {
            return [];
        }

        var fileOffset = _context.VaToFileOffset(percentPointer);
        if (fileOffset is not long maskFileOffset)
        {
            return [];
        }

        var bytes = _context.ReadBytes(maskFileOffset, PercentArraySamplesToRead * 4);
        if (bytes == null)
        {
            return [];
        }

        var opacities = new float[PercentArraySamplesToRead];
        var unitRangeCount = 0;
        var normalCount = 0;
        for (var i = 0; i < PercentArraySamplesToRead; i++)
        {
            var value = BinaryUtils.ReadFloatBE(bytes, i * 4);
            if (!RuntimeMemoryContext.IsNormalFloat(value))
            {
                continue;
            }

            normalCount++;
            if (value is >= -0.001f and <= 1.001f)
            {
                unitRangeCount++;
                opacities[i] = Math.Clamp(value, 0f, 1f);
            }
        }

        if (normalCount < PercentArraySamplesToRead ||
            unitRangeCount < PercentArraySamplesToRead)
        {
            return [];
        }

        var entries = new List<LandTextureBlendEntry>();
        for (var i = 0; i < opacities.Length; i++)
        {
            var opacity = opacities[i];
            if (opacity <= 0.001f)
            {
                continue;
            }

            entries.Add(new LandTextureBlendEntry((ushort)i, 0, 0, opacity));
        }

        return entries;
    }

    private RuntimeLandTextureRead? TryReadRuntimeLandTexture(uint texturePointer)
    {
        if (texturePointer == 0)
        {
            return null;
        }

        if (_runtimeLandTextureByPointer.TryGetValue(texturePointer, out var cached))
        {
            return cached;
        }

        var fileOffset = _context.VaToFileOffset(texturePointer);
        if (fileOffset is not long textureFileOffset)
        {
            _runtimeLandTextureByPointer[texturePointer] = null;
            return null;
        }

        var buffer = _context.ReadBytes(textureFileOffset, RuntimeLandTextureSize);
        if (buffer == null || buffer[4] != LandTextureFormType)
        {
            _runtimeLandTextureByPointer[texturePointer] = null;
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, TesFormFormIdOffset);
        if (formId is 0 or 0xFFFFFFFF)
        {
            _runtimeLandTextureByPointer[texturePointer] = null;
            return null;
        }

        var grassFormIds = new List<uint>();
        foreach (var grassPointer in _context.WalkInlineBSSimpleListItemPointers(
                     buffer,
                     RuntimeLandTextureGrassListOffset))
        {
            var grassFormId = _context.FollowPointerVaToFormId(grassPointer, GrassFormType);
            if (grassFormId is > 0)
            {
                grassFormIds.Add(grassFormId.Value);
            }
        }

        var textureSetPointer = BinaryUtils.ReadUInt32BE(buffer, RuntimeLandTextureTextureSetOffset);
        var textureSet = TryReadRuntimeTextureSet(textureSetPointer);
        var textureSetFormId = textureSet?.FormId
                               ?? _context.FollowPointerToFormId(
                                   buffer,
                                   RuntimeLandTextureTextureSetOffset,
                                   TextureSetFormType);

        var result = new LandscapeTextureRecord
        {
            FormId = formId,
            EditorId = _context.ReadBsStringT(textureFileOffset, TesFormEditorIdOffset),
            TextureSetFormId = textureSetFormId,
            HavokData =
            [
                buffer[RuntimeLandTextureHavokDataOffset],
                buffer[RuntimeLandTextureHavokDataOffset + 1],
                buffer[RuntimeLandTextureHavokDataOffset + 2]
            ],
            SpecularData = [buffer[RuntimeLandTextureSpecularOffset]],
            GrassFormIds = grassFormIds,
            Offset = textureFileOffset,
            IsBigEndian = true
        };

        var read = new RuntimeLandTextureRead(result, textureSet);
        _runtimeLandTextureByPointer[texturePointer] = read;
        return read;
    }

    private TextureSetRecord? TryReadRuntimeTextureSet(uint textureSetPointer)
    {
        if (textureSetPointer == 0)
        {
            return null;
        }

        if (_runtimeTextureSetByPointer.TryGetValue(textureSetPointer, out var cached))
        {
            return cached;
        }

        var fileOffset = _context.VaToFileOffset(textureSetPointer);
        if (fileOffset is not long textureSetFileOffset)
        {
            _runtimeTextureSetByPointer[textureSetPointer] = null;
            return null;
        }

        var buffer = _context.ReadBytes(textureSetFileOffset, RuntimeTextureSetSize);
        if (buffer == null || buffer[4] != TextureSetFormType)
        {
            _runtimeTextureSetByPointer[textureSetPointer] = null;
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, TesFormFormIdOffset);
        if (formId is 0 or 0xFFFFFFFF)
        {
            _runtimeTextureSetByPointer[textureSetPointer] = null;
            return null;
        }

        var textures = ReadTextureSetPaths(buffer);
        var result = new TextureSetRecord
        {
            FormId = formId,
            EditorId = _context.ReadBsStringT(textureSetFileOffset, TesFormEditorIdOffset),
            Bounds = ReadObjectBounds(buffer),
            DiffuseTexture = textures[0],
            NormalTexture = textures[1],
            EnvironmentTexture = textures[2],
            GlowTexture = textures[3],
            ParallaxTexture = textures[4],
            EnvironmentMapTexture = textures[5],
            Flags = BinaryUtils.ReadUInt16BE(buffer, RuntimeTextureSetFlagsOffset),
            Offset = textureSetFileOffset,
            IsBigEndian = true
        };

        _runtimeTextureSetByPointer[textureSetPointer] = result;
        return result;
    }

    private string?[] ReadTextureSetPaths(byte[] textureSetBuffer)
    {
        var candidates = new[]
        {
            BuildTexturePathCandidate(slot => ReadTextureInlineEntryPath(textureSetBuffer, slot)),
            BuildTexturePathCandidate(slot => ReadNiSourceTexturePath(ReadTexturePointer(textureSetBuffer, slot))),
            BuildTexturePathCandidate(slot => ReadTextureFileEntryPath(textureSetBuffer, slot))
        };

        var best = candidates
            .OrderByDescending(c => c.Score)
            .First();

        var paths = (string?[])best.Paths.Clone();
        if (best.Score == 0)
        {
            return paths;
        }

        for (var slot = 0; slot < paths.Length; slot++)
        {
            if (!string.IsNullOrEmpty(paths[slot]))
            {
                continue;
            }

            var fill = candidates
                .Where(c => c.Score > 0 && !string.IsNullOrEmpty(c.Paths[slot]))
                .OrderByDescending(c => ScoreTexturePathSlot(slot, c.Paths[slot]!))
                .ThenByDescending(c => c.Score)
                .FirstOrDefault();

            paths[slot] = fill?.Paths[slot];
        }

        return paths;
    }

    private static TextureSetPathCandidate BuildTexturePathCandidate(Func<int, string?> readPath)
    {
        var paths = new string?[TextureSetPathSlotCount];
        for (var slot = 0; slot < paths.Length; slot++)
        {
            paths[slot] = readPath(slot);
        }

        return new TextureSetPathCandidate(paths, ScoreTexturePathCandidate(paths));
    }

    private static int ScoreTexturePathCandidate(string?[] paths)
    {
        var score = 0;
        var present = 0;
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var slot = 0; slot < paths.Length; slot++)
        {
            var path = paths[slot];
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            present++;
            uniquePaths.Add(path);
            score += 100 + ScoreTexturePathSlot(slot, path);
        }

        if (present == 0)
        {
            return 0;
        }

        if (!string.IsNullOrEmpty(paths[0]))
        {
            score += 40;
        }

        if (!string.IsNullOrEmpty(paths[1]))
        {
            score += 30;
        }

        if (!string.IsNullOrEmpty(paths[0]) &&
            !string.IsNullOrEmpty(paths[1]) &&
            AreLikelyDiffuseNormalPair(paths[0]!, paths[1]!))
        {
            score += 60;
        }

        score -= (present - uniquePaths.Count) * 40;
        return Math.Max(score, 1);
    }

    private static int ScoreTexturePathSlot(int slot, string path)
    {
        var score = 0;
        if (path.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        if (path.Contains("\\landscape\\", StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        if (Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        var isNormalMap = IsLikelyNormalMapPath(path);
        score += slot switch
        {
            0 => isNormalMap ? -60 : 20,
            1 => isNormalMap ? 45 : -10,
            2 => isNormalMap ? -20 : 0,
            _ => 0
        };

        return score;
    }

    private static bool AreLikelyDiffuseNormalPair(string diffusePath, string normalPath)
    {
        if (!IsLikelyNormalMapPath(normalPath) || IsLikelyNormalMapPath(diffusePath))
        {
            return false;
        }

        return NormalizeTextureStem(diffusePath).Equals(
            NormalizeTextureStem(normalPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTextureStem(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path.Replace('/', '\\'));
        foreach (var suffix in NormalMapSuffixes)
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return stem[..^suffix.Length];
            }
        }

        return stem;
    }

    private static bool IsLikelyNormalMapPath(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path.Replace('/', '\\'));
        return NormalMapSuffixes.Any(suffix => stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static uint ReadTexturePointer(byte[] textureSetBuffer, int slot)
    {
        var offset = RuntimeTextureSetTexturesOffset + slot * 4;
        return offset + 4 <= textureSetBuffer.Length
            ? BinaryUtils.ReadUInt32BE(textureSetBuffer, offset)
            : 0;
    }

    private string? ReadTextureInlineEntryPath(byte[] textureSetBuffer, int slot)
    {
        var entryOffset = RuntimeTextureSetTexturesOffset + slot * RuntimeTextureSetTextureEntrySize;
        var pathPointerOffset = entryOffset + RuntimeTextureSetTextureEntryPathOffset;
        if (pathPointerOffset + 4 > textureSetBuffer.Length)
        {
            return null;
        }

        var pathPointer = BinaryUtils.ReadUInt32BE(textureSetBuffer, pathPointerOffset);
        return NormalizeTexturePath(_context.ReadNullTerminatedAsciiString(pathPointer, MaxTexturePathBytes));
    }

    private string? ReadNiSourceTexturePath(uint texturePointer)
    {
        if (texturePointer == 0)
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(texturePointer);
        if (fileOffset is not long textureFileOffset)
        {
            return null;
        }

        var buffer = _context.ReadBytes(textureFileOffset, NiSourceTextureSize);
        if (buffer == null)
        {
            return null;
        }

        var refCount = BinaryUtils.ReadUInt32BE(buffer, NiRefObjectRefCountOffset);
        if (refCount == 0 || refCount > MaxNiRefCount)
        {
            return null;
        }

        var filenamePointer = BinaryUtils.ReadUInt32BE(buffer, NiSourceTextureFilenameOffset);
        return NormalizeTexturePath(_context.ReadNullTerminatedAsciiString(filenamePointer, MaxTexturePathBytes));
    }

    private string? ReadTextureFileEntryPath(byte[] textureSetBuffer, int slot)
    {
        var entryOffset = RuntimeTextureSetTextureFileEntriesOffset + slot * 4;
        if (entryOffset + 4 > textureSetBuffer.Length)
        {
            return null;
        }

        var entryPointer = BinaryUtils.ReadUInt32BE(textureSetBuffer, entryOffset);
        var direct = NormalizeTexturePath(_context.ReadNullTerminatedAsciiString(entryPointer, MaxTexturePathBytes));
        if (direct != null)
        {
            return direct;
        }

        var entryFileOffset = _context.VaToFileOffset(entryPointer);
        if (entryFileOffset is not long fileOffset)
        {
            return null;
        }

        var entryBuffer = _context.ReadBytes(fileOffset, TextureFileEntryProbeSize);
        if (entryBuffer == null)
        {
            return null;
        }

        for (var offset = 0; offset + 4 <= entryBuffer.Length; offset += 4)
        {
            var pathPointer = BinaryUtils.ReadUInt32BE(entryBuffer, offset);
            var path = NormalizeTexturePath(_context.ReadNullTerminatedAsciiString(
                pathPointer,
                MaxTexturePathBytes));
            if (path != null)
            {
                return path;
            }
        }

        return null;
    }

    private static ObjectBounds? ReadObjectBounds(byte[] buffer)
    {
        var bounds = new ObjectBounds
        {
            X1 = BinaryUtils.ReadInt16BE(buffer, RuntimeTextureSetBoundsOffset),
            Y1 = BinaryUtils.ReadInt16BE(buffer, RuntimeTextureSetBoundsOffset + 2),
            Z1 = BinaryUtils.ReadInt16BE(buffer, RuntimeTextureSetBoundsOffset + 4),
            X2 = BinaryUtils.ReadInt16BE(buffer, RuntimeTextureSetBoundsOffset + 6),
            Y2 = BinaryUtils.ReadInt16BE(buffer, RuntimeTextureSetBoundsOffset + 8),
            Z2 = BinaryUtils.ReadInt16BE(buffer, RuntimeTextureSetBoundsOffset + 10)
        };

        return bounds is { X1: 0, Y1: 0, Z1: 0, X2: 0, Y2: 0, Z2: 0 }
            ? null
            : bounds;
    }

    private static string? NormalizeTexturePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var path = rawPath.Trim().Replace('/', '\\');
        while (path.Length > 0 && path[0] == '\\')
        {
            path = path[1..];
        }

        var dataIndex = path.IndexOf("data\\", StringComparison.OrdinalIgnoreCase);
        if (dataIndex >= 0)
        {
            path = path[(dataIndex + 5)..];
        }

        var textureIndex = path.IndexOf("textures\\", StringComparison.OrdinalIgnoreCase);
        if (textureIndex > 0)
        {
            path = path[textureIndex..];
        }

        var extension = Path.GetExtension(path);
        if (!extension.Equals(".dds", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".ddx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return path.Contains('\\') ? path : null;
    }

    private const int LoadedDataDefaultQuadTextureOffset = 32;
    private const int LoadedDataQuadTextureArrayOffset = 48;
    private const int LoadedDataPercentArraysOffset = 64;
    private const int LoadedDataQuadCount = 4;
    private const int MaxTextureArrayPointersToSample = 64;
    private const int PercentArraySamplesToRead = 17 * 17;
    private const int TesFormEditorIdOffset = 16;
    private const int TesFormFormIdOffset = 12;
    private const byte TextureSetFormType = 0x04;
    private const byte LandTextureFormType = 0x12;
    private const byte GrassFormType = 0x24;
    private const int RuntimeTextureSetSize = 192;
    private const int RuntimeTextureSetBoundsOffset = 52;
    private const int RuntimeTextureSetTexturesOffset = 72;
    private const int RuntimeTextureSetTextureEntrySize = 12;
    private const int RuntimeTextureSetTextureEntryPathOffset = 4;
    private const int RuntimeTextureSetFlagsOffset = 160;
    private const int RuntimeTextureSetTextureFileEntriesOffset = 164;
    private const int TextureSetPathSlotCount = 6;
    private const int TextureFileEntryProbeSize = 32;
    private const int NiSourceTextureSize = 72;
    private const int NiRefObjectRefCountOffset = 4;
    private const int NiSourceTextureFilenameOffset = 48;
    private const int MaxNiRefCount = 10000;
    private const int MaxTexturePathBytes = 260;
    private static readonly string[] NormalMapSuffixes = ["_n", "_normal", "_nrm"];
    private const int RuntimeLandTextureSize = 56;
    private const int RuntimeLandTextureTextureSetOffset = 40;
    private const int RuntimeLandTextureHavokDataOffset = 44;
    private const int RuntimeLandTextureSpecularOffset = 47;
    private const int RuntimeLandTextureGrassListOffset = 48;
}

internal sealed record RuntimeLandTextureRead(
    LandscapeTextureRecord LandTexture,
    TextureSetRecord? TextureSet);

internal sealed record RuntimeLandVisualExtraction(
    LandVisualData? VisualData,
    IReadOnlyList<LandscapeTextureRecord> LandTextures,
    IReadOnlyList<TextureSetRecord> TextureSets);

internal sealed record TextureSetPathCandidate(string?[] Paths, int Score);
