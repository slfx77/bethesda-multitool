using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

internal sealed class RuntimeLandVisualReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;
    private readonly Dictionary<uint, LandscapeTextureRecord?> _runtimeLandTextureByPointer = new();

    public RuntimeLandVisualExtraction Read(byte[] loadedDataBuffer)
    {
        var layers = new List<LandTextureLayer>();
        var landTextures = new Dictionary<uint, LandscapeTextureRecord>();

        for (var quadrant = 0; quadrant < LoadedDataQuadCount; quadrant++)
        {
            var texturePointer = BinaryUtils.ReadUInt32BE(
                loadedDataBuffer,
                LoadedDataDefaultQuadTextureOffset + quadrant * 4);
            var texture = TryReadRuntimeLandTexture(texturePointer);
            if (texture == null)
            {
                continue;
            }

            landTextures.TryAdd(texture.FormId, texture);
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

                var texture = TryReadRuntimeLandTexture(texturePointer);
                if (texture == null)
                {
                    continue;
                }

                var blendEntries = ReadRuntimeTextureBlendEntries(percentPointer);
                if (blendEntries.Count == 0)
                {
                    continue;
                }

                landTextures.TryAdd(texture.FormId, texture);
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

        return new RuntimeLandVisualExtraction(visualData, landTextures.Values.ToList());
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

    private LandscapeTextureRecord? TryReadRuntimeLandTexture(uint texturePointer)
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

        var result = new LandscapeTextureRecord
        {
            FormId = formId,
            EditorId = _context.ReadBsStringT(textureFileOffset, TesFormEditorIdOffset),
            TextureSetFormId = _context.FollowPointerToFormId(
                buffer,
                RuntimeLandTextureTextureSetOffset,
                TextureSetFormType),
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

        _runtimeLandTextureByPointer[texturePointer] = result;
        return result;
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
    private const int RuntimeLandTextureSize = 56;
    private const int RuntimeLandTextureTextureSetOffset = 40;
    private const int RuntimeLandTextureHavokDataOffset = 44;
    private const int RuntimeLandTextureSpecularOffset = 47;
    private const int RuntimeLandTextureGrassListOffset = 48;
}

internal sealed record RuntimeLandVisualExtraction(
    LandVisualData? VisualData,
    IReadOnlyList<LandscapeTextureRecord> LandTextures);
