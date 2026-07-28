using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Terrain;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.World;

/// <summary>
///     Captures the full pointer/texture/percent-array diagnostic snapshot of a runtime
///     LoadedLandData buffer for the World tab and terrain debugging. Reads the heap
///     pointers described by the LoadedLandData PDB layout and resolves them through the
///     runtime memory context.
/// </summary>
internal sealed class RuntimeLoadedLandDiagnosticsReader
{
    private readonly RuntimeMemoryContext _context;

    /// <summary>Creates the diagnostics reader bound to the given runtime memory context.</summary>
    public RuntimeLoadedLandDiagnosticsReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    /// <summary>
    ///     Capture all known LoadedLandData pointer/texture/percent-array fields from the
    ///     PDB layout for diagnostics.
    /// </summary>
    public RuntimeLoadedLandDiagnostics Build(byte[] loadedDataBuffer)
    {
        return new RuntimeLoadedLandDiagnostics
        {
            Mesh = ReadDoublePointerDiagnostic(loadedDataBuffer, LoadedDataMeshPtrOffset),
            Vertices = ReadDoublePointerDiagnostic(loadedDataBuffer, LoadedDataVerticesPtrOffset),
            VertexArrays =
                ReadDoublePointerArrayDiagnostics(loadedDataBuffer, LoadedDataVerticesPtrOffset, TerrainQuadrantCount),
            Normals = ReadDoublePointerDiagnostic(loadedDataBuffer, LoadedDataNormalsPtrOffset),
            NormalArrays =
                ReadDoublePointerArrayDiagnostics(loadedDataBuffer, LoadedDataNormalsPtrOffset, TerrainQuadrantCount),
            Colors = ReadDoublePointerDiagnostic(loadedDataBuffer, LoadedDataColorsPtrOffset),
            ColorArrays =
                ReadDoublePointerArrayDiagnostics(loadedDataBuffer, LoadedDataColorsPtrOffset, TerrainQuadrantCount),
            NormalsSet = ReadDoublePointerDiagnostic(loadedDataBuffer, LoadedDataNormalsSetPtrOffset),
            Border = ReadPointerDiagnostic(loadedDataBuffer, LoadedDataBorderPtrOffset),
            MoppCode = ReadPointerDiagnostic(loadedDataBuffer, LoadedDataMoppCodePtrOffset),
            LandRigidBody = ReadPointerDiagnostic(loadedDataBuffer, LoadedDataLandRigidBodyPtrOffset),
            DefaultQuadTextures = ReadDefaultQuadTextureDiagnostics(loadedDataBuffer),
            QuadTextureArrays = ReadQuadTextureArrayDiagnostics(loadedDataBuffer),
            PercentArrays = ReadPercentArrayDiagnostics(loadedDataBuffer),
            GrassMapWords = ReadGrassMapWords(loadedDataBuffer)
        };
    }

    private List<RuntimePointerDiagnostic> ReadDoublePointerArrayDiagnostics(
        byte[] buffer,
        int ptrOffset,
        int slotCount)
    {
        var outer = ReadPointerDiagnostic(buffer, ptrOffset);
        var results = new List<RuntimePointerDiagnostic>(slotCount);
        if (outer.FileOffset is not long outerFileOffset)
        {
            return results;
        }

        var pointerBytes = _context.ReadBytes(outerFileOffset, slotCount * 4);
        if (pointerBytes == null)
        {
            return results;
        }

        for (var slot = 0; slot < slotCount; slot++)
        {
            var innerPointer = BinaryUtils.ReadUInt32BE(pointerBytes, slot * 4);
            results.Add(new RuntimePointerDiagnostic
            {
                Pointer = outer.Pointer,
                FileOffset = outer.FileOffset,
                DereferencedPointer = innerPointer,
                DereferencedFileOffset = _context.VaToFileOffset(innerPointer)
            });
        }

        return results;
    }

    private RuntimePointerDiagnostic ReadPointerDiagnostic(byte[] buffer, int ptrOffset)
    {
        if (ptrOffset < 0 || ptrOffset + 4 > buffer.Length)
        {
            return RuntimePointerDiagnostic.Empty;
        }

        var pointer = BinaryUtils.ReadUInt32BE(buffer, ptrOffset);
        return new RuntimePointerDiagnostic
        {
            Pointer = pointer,
            FileOffset = _context.VaToFileOffset(pointer)
        };
    }

    private RuntimePointerDiagnostic ReadDoublePointerDiagnostic(byte[] buffer, int ptrOffset)
    {
        var trace = ReadPointerDiagnostic(buffer, ptrOffset);
        if (trace.FileOffset is not long outerFileOffset)
        {
            return trace;
        }

        var innerBytes = _context.ReadBytes(outerFileOffset, 4);
        if (innerBytes == null)
        {
            return trace;
        }

        var innerPointer = BinaryUtils.ReadUInt32BE(innerBytes);
        return trace with
        {
            DereferencedPointer = innerPointer,
            DereferencedFileOffset = _context.VaToFileOffset(innerPointer)
        };
    }

    private List<RuntimeLandTexturePointerDiagnostic> ReadDefaultQuadTextureDiagnostics(byte[] buffer)
    {
        var results = new List<RuntimeLandTexturePointerDiagnostic>(LoadedDataQuadCount);
        for (var quadrant = 0; quadrant < LoadedDataQuadCount; quadrant++)
        {
            var pointer = ReadPointerDiagnostic(buffer, LoadedDataDefaultQuadTextureOffset + quadrant * 4);
            results.Add(new RuntimeLandTexturePointerDiagnostic
            {
                Quadrant = quadrant,
                Pointer = pointer,
                TextureFormId = pointer.FileOffset.HasValue
                    ? ReadFormIdAtFileOffset(pointer.FileOffset.Value, LandTextureFormType)
                    : null
            });
        }

        return results;
    }

    private List<RuntimeLandTextureArrayDiagnostic> ReadQuadTextureArrayDiagnostics(byte[] buffer)
    {
        var results = new List<RuntimeLandTextureArrayDiagnostic>(LoadedDataQuadCount);
        for (var quadrant = 0; quadrant < LoadedDataQuadCount; quadrant++)
        {
            var pointer = ReadPointerDiagnostic(buffer, LoadedDataQuadTextureArrayOffset + quadrant * 4);
            var sampledPointerCount = 0;
            var textureFormIds = new List<uint>();

            if (pointer.FileOffset is long arrayFileOffset)
            {
                var bytes = _context.ReadBytes(arrayFileOffset, MaxAlphaTextureSlots * 4);
                if (bytes != null)
                {
                    for (var i = 0; i < MaxAlphaTextureSlots; i++)
                    {
                        var texturePointer = BinaryUtils.ReadUInt32BE(bytes, i * 4);
                        if (texturePointer == 0)
                        {
                            continue;
                        }

                        sampledPointerCount++;
                        var formId = _context.FollowPointerVaToFormId(texturePointer, LandTextureFormType);
                        if (formId.HasValue)
                        {
                            textureFormIds.Add(formId.Value);
                        }
                    }
                }
            }

            results.Add(new RuntimeLandTextureArrayDiagnostic
            {
                Quadrant = quadrant,
                Pointer = pointer,
                SampledPointerCount = sampledPointerCount,
                ResolvedTextureCount = textureFormIds.Count,
                TextureFormIds = textureFormIds
            });
        }

        return results;
    }

    private List<RuntimePercentArrayDiagnostic> ReadPercentArrayDiagnostics(byte[] buffer)
    {
        var results = new List<RuntimePercentArrayDiagnostic>(LoadedDataQuadCount);
        for (var quadrant = 0; quadrant < LoadedDataQuadCount; quadrant++)
        {
            var pointer = ReadDoublePointerDiagnostic(buffer, LoadedDataPercentArraysOffset + quadrant * 4);
            var sampledCount = 0;
            var normalCount = 0;
            var unitCount = 0;
            var nonZeroUnitCount = 0;
            float? minValue = null;
            float? maxValue = null;

            if (pointer.FileOffset is long pointerArrayFileOffset)
            {
                var pointerBytes = _context.ReadBytes(pointerArrayFileOffset, TextureWeightVertexCount * 4);
                if (pointerBytes != null)
                {
                    for (var position = 0; position < TextureWeightVertexCount; position++)
                    {
                        var vertexWeightsPointer = BinaryUtils.ReadUInt32BE(pointerBytes, position * 4);
                        var vertexWeightsFileOffset = _context.VaToFileOffset(vertexWeightsPointer);
                        if (vertexWeightsFileOffset is not long vertexWeightsOffset)
                        {
                            continue;
                        }

                        var weightBytes = _context.ReadBytes(vertexWeightsOffset, TextureWeightSlotCount * 4);
                        if (weightBytes == null)
                        {
                            continue;
                        }

                        for (var slot = 0; slot < TextureWeightSlotCount; slot++)
                        {
                            sampledCount++;
                            var value = BinaryUtils.ReadFloatBE(weightBytes, slot * 4);
                            if (!RuntimeMemoryContext.IsNormalFloat(value))
                            {
                                continue;
                            }

                            normalCount++;
                            minValue = minValue.HasValue ? Math.Min(minValue.Value, value) : value;
                            maxValue = maxValue.HasValue ? Math.Max(maxValue.Value, value) : value;

                            if (value is >= 0f and <= 1f)
                            {
                                unitCount++;
                                if (value > 0.001f)
                                {
                                    nonZeroUnitCount++;
                                }
                            }
                        }
                    }
                }
            }

            results.Add(new RuntimePercentArrayDiagnostic
            {
                Quadrant = quadrant,
                Pointer = pointer,
                SampledCount = sampledCount,
                NormalFloatCount = normalCount,
                UnitRangeCount = unitCount,
                NonZeroUnitRangeCount = nonZeroUnitCount,
                MinValue = minValue,
                MaxValue = maxValue
            });
        }

        return results;
    }

    private static List<uint> ReadGrassMapWords(byte[] buffer)
    {
        var words = new List<uint>(LoadedDataGrassMapSize / 4);
        for (var offset = LoadedDataGrassMapOffset;
             offset + 4 <= LoadedDataGrassMapOffset + LoadedDataGrassMapSize;
             offset += 4)
        {
            if (offset + 4 > buffer.Length)
            {
                break;
            }

            words.Add(BinaryUtils.ReadUInt32BE(buffer, offset));
        }

        return words;
    }

    private uint? ReadFormIdAtFileOffset(long fileOffset, byte expectedFormType)
    {
        var buffer = _context.ReadBytes(fileOffset, TesFormHeaderReadSize);
        if (buffer == null)
        {
            return null;
        }

        var formType = buffer[4];
        if (formType != expectedFormType)
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, TesFormFormIdOffset);
        return formId is 0 or 0xFFFFFFFF ? null : formId;
    }

    #region LoadedLandData Struct Layout

    // LoadedLandData: 164 bytes — standalone struct, identical across all builds.
    // These offsets mirror the layout constants in RuntimeWorldReader; the diagnostics
    // reader keeps its own copy so it is self-contained.
    private const int LoadedDataMeshPtrOffset = 0; // NiPointer<NiTriShape>** ppMesh
    private const int LoadedDataVerticesPtrOffset = 4; // NiPoint3** ppVertices
    private const int LoadedDataNormalsPtrOffset = 8; // NiPoint3** ppNormals
    private const int LoadedDataColorsPtrOffset = 12; // NiColorA** ppColorsA
    private const int LoadedDataNormalsSetPtrOffset = 16; // bool** ppNormalsSet
    private const int LoadedDataBorderPtrOffset = 20; // NiPointer<NiLines> spBorder
    private const int LoadedDataDefaultQuadTextureOffset = 32; // TESLandTexture* pDefQuadTexture[4]
    private const int LoadedDataQuadTextureArrayOffset = 48; // TESLandTexture** pQuadTextureArray[4]
    private const int LoadedDataPercentArraysOffset = 64; // float** ppPercentArrays[4]
    private const int LoadedDataMoppCodePtrOffset = 80; // hkpMoppCode* pMoppCode
    private const int LoadedDataGrassMapOffset = 84; // NiTPointerMap<unsigned int,TESGrassAreaParam**> pmGrassMap[4]
    private const int LoadedDataGrassMapSize = 64;
    private const int LoadedDataLandRigidBodyPtrOffset = 148; // NiPointer<bhkRigidBody> spLandRB
    private const int LoadedDataQuadCount = 4;
    // Slot 0 is pDefQuadTexture; these arrays contain engine slots 1..5 only.
    private const int MaxAlphaTextureSlots = 5;
    private const int TextureWeightSlotCount = 6;
    private const int TextureWeightVertexCount = 17 * 17;
    private const int TesFormHeaderReadSize = 16;
    private const int TesFormFormIdOffset = 12;
    private const byte LandTextureFormType = 0x12;
    private const int TerrainQuadrantCount = RuntimeTerrainQuadrantMeshBuilder.QuadrantCount;

    #endregion
}
