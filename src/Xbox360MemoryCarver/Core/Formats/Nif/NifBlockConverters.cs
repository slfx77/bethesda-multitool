using System.Buffers.Binary;
using static Xbox360MemoryCarver.Core.Formats.Nif.NifEndianUtils;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Block-specific endian conversion dispatch for NIF blocks.
/// </summary>
internal static class NifBlockConverters
{
    /// <summary>
    ///     Convert a block's fields from BE to LE in-place with proper type handling.
    /// </summary>
    public static void ConvertBlockInPlace(byte[] buf, int pos, int size, string blockType, int[] blockRemap)
    {
        switch (blockType)
        {
            case "BSShaderTextureSet":
                ConvertBSShaderTextureSet(buf, pos, size);
                break;

            case "NiStringExtraData":
            case "BSBehaviorGraphExtraData":
                ConvertNiStringExtraData(buf, pos, size);
                break;

            case "NiTextKeyExtraData":
                ConvertNiTextKeyExtraData(buf, pos, size);
                break;

            case "NiSourceTexture":
                ConvertNiSourceTexture(buf, pos, size);
                break;

            case "NiNode":
            case "BSFadeNode":
            case "BSLeafAnimNode":
            case "BSTreeNode":
            case "BSOrderedNode":
            case "BSMultiBoundNode":
            case "BSBlastNode":
            case "BSDamageStage":
            case "BSMasterParticleSystem":
            case "NiBillboardNode":
            case "NiSwitchNode":
            case "NiLODNode":
                ConvertNiNode(buf, pos, size, blockRemap);
                break;

            case "NiTriStrips":
            case "NiTriShape":
            case "BSSegmentedTriShape":
            case "NiParticles":
            case "NiParticleSystem":
            case "NiMeshParticleSystem":
            case "BSStripParticleSystem":
                ConvertNiGeometry(buf, pos, size, blockRemap);
                break;

            case "NiTriStripsData":
                NifGeometryDataConverter.ConvertNiTriStripsData(buf, pos, size, blockRemap);
                break;

            case "NiTriShapeData":
                NifGeometryDataConverter.ConvertNiTriShapeData(buf, pos, size, blockRemap);
                break;

            case "BSShaderNoLightingProperty":
            case "SkyShaderProperty":
            case "TileShaderProperty":
            case "BSShaderPPLightingProperty":
                ConvertBSShaderProperty(buf, pos, size, blockRemap);
                break;

            case "BSLightingShaderProperty":
            case "BSEffectShaderProperty":
            case "NiMaterialProperty":
            case "NiStencilProperty":
            case "NiAlphaProperty":
            case "NiZBufferProperty":
            case "NiVertexColorProperty":
            case "NiSpecularProperty":
            case "NiDitherProperty":
            case "NiWireframeProperty":
            case "NiShadeProperty":
            case "NiFogProperty":
                ConvertPropertyBlock(buf, pos, size, blockRemap);
                break;

            case "NiSkinInstance":
            case "BSDismemberSkinInstance":
                ConvertNiSkinInstance(buf, pos, size, blockRemap);
                break;

            case "NiSkinData":
                ConvertNiSkinData(buf, pos, size);
                break;

            case "NiSkinPartition":
                ConvertNiSkinPartition(buf, pos, size);
                break;

            case "NiControllerSequence":
                ConvertNiControllerSequence(buf, pos, size, blockRemap);
                break;

            case "NiTransformInterpolator":
            case "NiBlendTransformInterpolator":
            case "NiFloatInterpolator":
            case "NiBlendFloatInterpolator":
            case "NiPoint3Interpolator":
            case "NiBlendPoint3Interpolator":
            case "NiTransformData":
            case "NiFloatData":
            case "NiBoolData":
                BulkSwap4InPlace(buf, pos, size);
                break;

            case "NiBoolInterpolator":
                ConvertNiBoolInterpolator(buf, pos, size);
                break;

            default:
                BulkSwap4InPlace(buf, pos, size);
                break;
        }
    }

    private static void ConvertBSShaderTextureSet(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos);
        var numTextures = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        for (var i = 0; i < numTextures && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            var strLen = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
            pos += 4 + (int)strLen;
        }
    }

    private static void ConvertNiStringExtraData(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        if (pos + 8 > end) return;

        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
    }

    private static void ConvertNiTextKeyExtraData(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        if (pos + 8 > end) return;

        SwapUInt32InPlace(buf, pos);
        pos += 4;
        SwapUInt32InPlace(buf, pos);
        var numKeys = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        for (var i = 0; i < numKeys && pos + 8 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            SwapUInt32InPlace(buf, pos + 4);
            var strLen = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos + 4));
            pos += 8 + (int)strLen;
        }
    }

    private static void ConvertNiSourceTexture(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        if (pos + 17 > end) return;

        SwapUInt32InPlace(buf, pos); // nameIdx
        SwapUInt32InPlace(buf, pos + 5); // fileNameIdx/ref
        SwapUInt32InPlace(buf, pos + 9); // pixelLayout
        SwapUInt32InPlace(buf, pos + 13); // useMipmaps
        if (pos + 21 <= end) SwapUInt32InPlace(buf, pos + 17); // alphaFormat
    }

    private static void ConvertNiNode(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;
        pos = ConvertNiAVObjectInPlace(buf, pos, end, blockRemap);
        if (pos < 0 || pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos);
        var numChildren = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        for (var i = 0; i < numChildren && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos);
        var numEffects = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        for (var i = 0; i < numEffects && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }
    }

    private static void ConvertNiGeometry(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;
        pos = ConvertNiAVObjectInPlace(buf, pos, end, blockRemap);
        if (pos < 0) return;

        // dataRef, skinInstanceRef
        for (var i = 0; i < 2 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos);
        var numMats = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        for (var i = 0; i < numMats * 2 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        if (pos + 5 > end) return;

        SwapUInt32InPlace(buf, pos);
        pos += 5; // activeMaterial + dirtyFlag

        for (var i = 0; i < 2 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }
    }

    private static void ConvertPropertyBlock(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;
        pos = ConvertNiObjectNETInPlace(buf, pos, end, blockRemap);
        if (pos >= 0) BulkSwap4InPlace(buf, pos, end - pos);
    }

    private static void ConvertBSShaderProperty(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;
        pos = ConvertNiObjectNETInPlace(buf, pos, end, blockRemap);
        if (pos < 0) return;

        // Swap 5 uint32s, then check for SizedString
        for (var i = 0; i < 5 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // fileName or textureSetRef
        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        BulkSwap4InPlace(buf, pos, end - pos);
    }

    private static void ConvertNiSkinInstance(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;

        for (var i = 0; i < 3 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos);
        var numBones = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        for (var i = 0; i < numBones && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        BulkSwap4InPlace(buf, pos, end - pos);
    }

    private static void ConvertNiSkinData(byte[] buf, int pos, int size)
    {
        var end = pos + size;

        for (var i = 0; i < 13 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        if (pos + 5 > end) return;

        SwapUInt32InPlace(buf, pos);
        var numBones = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;
        var hasWeights = buf[pos++];

        for (var b = 0; b < numBones && pos < end; b++)
        {
            for (var i = 0; i < 17 && pos + 4 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos);
                pos += 4;
            }

            if (pos + 2 > end) break;

            SwapUInt16InPlace(buf, pos);
            var numVerts = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
            pos += 2;

            if (hasWeights != 0)
                for (var v = 0; v < numVerts && pos + 6 <= end; v++)
                {
                    SwapUInt16InPlace(buf, pos);
                    SwapUInt32InPlace(buf, pos + 2);
                    pos += 6;
                }
        }
    }

    private static void ConvertNiSkinPartition(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        if (pos + 4 > end) return;

        SwapUInt32InPlace(buf, pos);
        var numPartitions = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        if (numPartitions == 0 || pos + 10 > end) return;

        for (var i = 0; i < 5; i++)
        {
            SwapUInt16InPlace(buf, pos);
            pos += 2;
        }

        var numBones = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos - 8));
        for (var i = 0; i < numBones && pos + 2 <= end; i++)
        {
            SwapUInt16InPlace(buf, pos);
            pos += 2;
        }

        pos += 4; // Skip flags
        BulkSwap4InPlace(buf, pos, end - pos);
    }

    private static void ConvertNiControllerSequence(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;
        if (pos + 12 > end) return;

        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        var numBlocks = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos + 4));
        SwapUInt32InPlace(buf, pos + 8);
        pos += 12;

        for (var i = 0; i < numBlocks && pos + 29 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            SwapUInt32InPlace(buf, pos + 4);
            RemapBlockRefInPlace(buf, pos + 4, blockRemap);
            pos += 9; // 2 refs + priority byte

            for (var j = 0; j < 5; j++)
            {
                SwapUInt32InPlace(buf, pos);
                pos += 4;
            }
        }

        BulkSwap4InPlace(buf, pos, end - pos);
    }

    private static void ConvertNiBoolInterpolator(byte[] buf, int pos, int size)
    {
        if (size >= 5) SwapUInt32InPlace(buf, pos + 1);
    }
}
