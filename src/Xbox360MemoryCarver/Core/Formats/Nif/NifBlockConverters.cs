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
                ConvertNiSkinInstance(buf, pos, size, blockRemap);
                break;

            case "BSDismemberSkinInstance":
                ConvertBSDismemberSkinInstance(buf, pos, size, blockRemap);
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

            // Havok physics blocks - these need special handling to avoid corrupting MOPP data
            case "bhkMoppBvTreeShape":
                ConvertBhkMoppBvTreeShape(buf, pos, size, blockRemap);
                break;

            case "bhkPackedNiTriStripsShape":
                ConvertBhkPackedNiTriStripsShape(buf, pos, size, blockRemap);
                break;

            case "hkPackedNiTriStripsData":
                ConvertHkPackedNiTriStripsData(buf, pos, size);
                break;

            case "bhkRigidBody":
            case "bhkRigidBodyT":
                ConvertBhkRigidBody(buf, pos, size, blockRemap);
                break;

            case "bhkCollisionObject":
            case "bhkPCollisionObject":
            case "bhkSPCollisionObject":
                ConvertBhkCollisionObject(buf, pos, size, blockRemap);
                break;

            case "bhkBlendCollisionObject":
                ConvertBhkBlendCollisionObject(buf, pos, size, blockRemap);
                break;

            case "bhkConvexVerticesShape":
                ConvertBhkConvexVerticesShape(buf, pos, size);
                break;

            case "bhkBoxShape":
                ConvertBhkBoxShape(buf, pos, size);
                break;

            case "bhkSphereShape":
                ConvertBhkSphereShape(buf, pos, size);
                break;

            case "bhkCapsuleShape":
                ConvertBhkCapsuleShape(buf, pos, size);
                break;

            case "bhkListShape":
                ConvertBhkListShape(buf, pos, size, blockRemap);
                break;

            case "bhkConvexTransformShape":
            case "bhkTransformShape":
                ConvertBhkTransformShape(buf, pos, size, blockRemap);
                break;

            case "bhkNiTriStripsShape":
                ConvertBhkNiTriStripsShape(buf, pos, size, blockRemap);
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
        // BSShaderPPLightingProperty inheritance:
        // NiObjectNET: nameRef(4) + numExtraData(4) + controllerRef(4) = 12 bytes
        // NiProperty: (no fields)
        // NiShadeProperty: Flags(2) ← 2-byte field!
        // BSShaderProperty: ShaderType(4) + ShaderFlags(4) + ShaderFlags2(4) + EnvMapScale(4)
        // BSShaderLightingProperty: TextureClampMode(4)
        // BSShaderPPLightingProperty: TextureSetRef(4) + more fields

        var end = pos + size;
        pos = ConvertNiObjectNETInPlace(buf, pos, end, blockRemap);
        if (pos < 0) return;

        // NiShadeProperty: Flags is 2 bytes (ushort), swap it
        if (pos + 2 > end) return;
        SwapUInt16InPlace(buf, pos);
        pos += 2;

        // BSShaderProperty: ShaderType(4) + ShaderFlags(4) + ShaderFlags2(4) + EnvMapScale(4) = 16 bytes
        for (var i = 0; i < 4 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            pos += 4;
        }

        // BSShaderLightingProperty: TextureClampMode(4)
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // BSShaderPPLightingProperty: TextureSetRef (block ref)
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // Remaining fields (RefractionStrength, etc.) - bulk swap
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

        // NiSkinInstance has no additional fields after bones
    }

    /// <summary>
    ///     Convert BSDismemberSkinInstance which extends NiSkinInstance with partition data.
    ///     Structure:
    ///       - [NiSkinInstance base: Data ref, SkinPartition ref, SkeletonRoot ptr, NumBones, Bones ptrs]
    ///       - NumPartitions (uint)
    ///       - Partitions[NumPartitions] - each is BodyPartList { PartFlag (ushort), BodyPart (ushort) }
    /// </summary>
    private static void ConvertBSDismemberSkinInstance(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;

        // NiSkinInstance base: 3 refs (Data, SkinPartition, SkeletonRoot)
        for (var i = 0; i < 3 && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        if (pos + 4 > end) return;

        // NumBones (uint)
        SwapUInt32InPlace(buf, pos);
        var numBones = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // Validate numBones to avoid processing garbage data
        if (numBones > 1000) return;

        // Bones ptrs (array of refs)
        for (var i = 0; i < numBones && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // BSDismemberSkinInstance additional fields:
        if (pos + 4 > end) return;

        // NumPartitions (uint)
        SwapUInt32InPlace(buf, pos);
        var numPartitions = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // Validate numPartitions
        if (numPartitions > 1000) return;

        // Partitions[NumPartitions] - each BodyPartList is 4 bytes: PartFlag (ushort) + BodyPart (ushort)
        for (var i = 0; i < numPartitions && pos + 4 <= end; i++)
        {
            // Each field is a ushort, swap them individually
            SwapUInt16InPlace(buf, pos);      // PartFlag
            SwapUInt16InPlace(buf, pos + 2);  // BodyPart
            pos += 4;
        }
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

        // NumPartitions
        SwapUInt32InPlace(buf, pos);
        var numPartitions = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        for (var p = 0; p < numPartitions && pos + 10 <= end; p++)
        {
            // Header fields: NumVertices, NumTriangles, NumBones, NumStrips, NumWeightsPerVertex
            var numVertices = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos));
            SwapUInt16InPlace(buf, pos);
            pos += 2;

            var numTriangles = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos));
            SwapUInt16InPlace(buf, pos);
            pos += 2;

            var numBones = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos));
            SwapUInt16InPlace(buf, pos);
            pos += 2;

            var numStrips = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos));
            SwapUInt16InPlace(buf, pos);
            pos += 2;

            var numWeightsPerVertex = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos));
            SwapUInt16InPlace(buf, pos);
            pos += 2;

            // Bones array
            for (var i = 0; i < numBones && pos + 2 <= end; i++)
            {
                SwapUInt16InPlace(buf, pos);
                pos += 2;
            }

            // HasVertexMap (byte)
            if (pos >= end) return;
            var hasVertexMap = buf[pos++];

            // VertexMap (ushort array)
            if (hasVertexMap != 0)
            {
                for (var i = 0; i < numVertices && pos + 2 <= end; i++)
                {
                    SwapUInt16InPlace(buf, pos);
                    pos += 2;
                }
            }

            // HasVertexWeights (byte)
            if (pos >= end) return;
            var hasVertexWeights = buf[pos++];

            // VertexWeights (float array)
            if (hasVertexWeights != 0)
            {
                var numWeights = numVertices * numWeightsPerVertex;
                for (var i = 0; i < numWeights && pos + 4 <= end; i++)
                {
                    SwapUInt32InPlace(buf, pos);
                    pos += 4;
                }
            }

            // StripLengths (ushort array) - even if numStrips==0
            var stripLengths = new ushort[numStrips];
            for (var i = 0; i < numStrips && pos + 2 <= end; i++)
            {
                stripLengths[i] = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos));
                SwapUInt16InPlace(buf, pos);
                pos += 2;
            }

            // HasFaces (byte)
            if (pos >= end) return;
            var hasFaces = buf[pos++];

            // Strips data (if hasFaces && numStrips > 0)
            if (hasFaces != 0 && numStrips > 0)
            {
                for (var s = 0; s < numStrips && s < stripLengths.Length; s++)
                {
                    for (var i = 0; i < stripLengths[s] && pos + 2 <= end; i++)
                    {
                        SwapUInt16InPlace(buf, pos);
                        pos += 2;
                    }
                }
            }

            // Triangles (if hasFaces && numStrips == 0)
            if (hasFaces != 0 && numStrips == 0)
            {
                for (var i = 0; i < numTriangles && pos + 6 <= end; i++)
                {
                    SwapUInt16InPlace(buf, pos);
                    SwapUInt16InPlace(buf, pos + 2);
                    SwapUInt16InPlace(buf, pos + 4);
                    pos += 6;
                }
            }

            // HasBoneIndices (byte)
            if (pos >= end) return;
            var hasBoneIndices = buf[pos++];

            // BoneIndices (byte array - no swap needed)
            if (hasBoneIndices != 0)
            {
                pos += numVertices * numWeightsPerVertex;
            }
        }
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

    // ============================================================================
    // Havok Physics Block Converters
    // These blocks contain mixed data types: refs, floats, vectors, AND raw bytecode
    // that must NOT be byte-swapped. Naive BulkSwap4InPlace corrupts them.
    // ============================================================================

    /// <summary>
    /// bhkMoppBvTreeShape: Contains MOPP bytecode that must NOT be byte-swapped.
    /// Structure:
    ///   - Ref Shape (4 bytes) - from bhkBvTreeShape
    ///   - 12 bytes unused (binary/padding)
    ///   - float Scale
    ///   - hkpMoppCode:
    ///     - uint DataSize
    ///     - Vector4 Offset (16 bytes, 4 floats)
    ///     - byte BuildType (Fallout 3+ only, skip for now as FNV uses this)
    ///     - byte[] Data (raw MOPP - DO NOT SWAP)
    /// </summary>
    private static void ConvertBhkMoppBvTreeShape(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;
        if (size < 37) return; // Minimum: ref(4) + unused(12) + scale(4) + datasize(4) + offset(16) = 40, but be safe

        // Ref Shape (inherited from bhkBvTreeShape)
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // 12 bytes unused - skip (no swap needed for padding bytes)
        pos += 12;

        // float Scale
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // hkpMoppCode structure
        if (pos + 4 > end) return;

        // uint DataSize
        SwapUInt32InPlace(buf, pos);
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // Vector4 Offset (4 floats)
        if (pos + 16 > end) return;
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        SwapUInt32InPlace(buf, pos + 8);
        SwapUInt32InPlace(buf, pos + 12);
        pos += 16;

        // byte BuildType (1 byte) - no swap needed
        pos += 1;

        // byte[] Data - MOPP bytecode - DO NOT SWAP!
        // The remaining bytes are raw MOPP data, leave them as-is
    }

    /// <summary>
    /// bhkPackedNiTriStripsShape: Packed collision geometry.
    /// Structure (Fallout 3/NV version):
    ///   - uint UserData
    ///   - 4 bytes unused
    ///   - float Radius
    ///   - 4 bytes unused
    ///   - Vector4 Scale (16 bytes)
    ///   - float RadiusCopy
    ///   - Vector4 ScaleCopy (16 bytes)
    ///   - Ref Data (hkPackedNiTriStripsData)
    /// </summary>
    private static void ConvertBhkPackedNiTriStripsShape(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;
        if (size < 56) return;

        // uint UserData
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // 4 bytes unused
        pos += 4;

        // float Radius
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // 4 bytes unused
        pos += 4;

        // Vector4 Scale (16 bytes)
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        SwapUInt32InPlace(buf, pos + 8);
        SwapUInt32InPlace(buf, pos + 12);
        pos += 16;

        // float RadiusCopy
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // Vector4 ScaleCopy (16 bytes)
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        SwapUInt32InPlace(buf, pos + 8);
        SwapUInt32InPlace(buf, pos + 12);
        pos += 16;

        // Ref Data
        if (pos + 4 <= end)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
        }
    }

    /// <summary>
    /// hkPackedNiTriStripsData: Packed triangle collision data.
    /// Structure:
    ///   - uint NumTriangles
    ///   - TriangleData[NumTriangles] (each is 6 ushorts = 12 bytes for FNV)
    ///   - uint NumVertices
    ///   - bool Compressed (1 byte in Skyrim+, 0 in FNV)
    ///   - Vector3[NumVertices] or HalfVector3[NumVertices]
    ///   - ushort NumSubShapes (Skyrim+)
    ///   - hkSubPartData[NumSubShapes] (Skyrim+)
    /// </summary>
    private static void ConvertHkPackedNiTriStripsData(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        if (size < 8) return;

        // uint NumTriangles
        SwapUInt32InPlace(buf, pos);
        var numTriangles = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // TriangleData struct from nif.xml:
        //   Triangle (3 ushorts = 6 bytes) + bhkWeldInfo (1 ushort = 2 bytes) = 8 bytes total
        //   Note: Normal field is conditional "until 20.0.0.5" so not present in FNV (20.2.0.7)
        var triangleDataSize = 8; // 4 ushorts: v1, v2, v3, weldInfo
        for (uint i = 0; i < numTriangles && pos + triangleDataSize <= end; i++)
        {
            // Swap 4 ushorts (3 vertex indices + 1 welding info)
            SwapUInt16InPlace(buf, pos);
            SwapUInt16InPlace(buf, pos + 2);
            SwapUInt16InPlace(buf, pos + 4);
            SwapUInt16InPlace(buf, pos + 6);
            pos += triangleDataSize;
        }

        if (pos + 4 > end) return;

        // uint NumVertices
        SwapUInt32InPlace(buf, pos);
        var numVertices = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        if (pos >= end) return;

        // bool Compressed (since 20.2.0.7) - single byte, no swap needed
        var compressed = buf[pos] != 0;
        pos += 1;

        if (compressed)
        {
            // HalfVector3[NumVertices] - each is 3 half-floats = 6 bytes
            // Half-floats are 16-bit, need to swap each
            for (uint i = 0; i < numVertices && pos + 6 <= end; i++)
            {
                SwapUInt16InPlace(buf, pos);
                SwapUInt16InPlace(buf, pos + 2);
                SwapUInt16InPlace(buf, pos + 4);
                pos += 6;
            }
        }
        else
        {
            // Vector3[NumVertices] - each is 3 floats = 12 bytes
            for (uint i = 0; i < numVertices && pos + 12 <= end; i++)
            {
                SwapUInt32InPlace(buf, pos);
                SwapUInt32InPlace(buf, pos + 4);
                SwapUInt32InPlace(buf, pos + 8);
                pos += 12;
            }
        }

        if (pos + 2 > end) return;

        // ushort NumSubShapes (since 20.2.0.7)
        SwapUInt16InPlace(buf, pos);
        var numSubShapes = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
        pos += 2;

        // hkSubPartData[NumSubShapes]
        // hkSubPartData is 8 bytes: uint havokFilter + uint numVertices
        for (int i = 0; i < numSubShapes && pos + 8 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);     // havokFilter
            SwapUInt32InPlace(buf, pos + 4); // numVertices
            pos += 8;
        }
    }

    /// <summary>
    /// bhkRigidBody/bhkRigidBodyT: Contains physics parameters.
    /// Uses bhkRigidBodyCInfo550_660 for Oblivion/FO3/FNV.
    /// </summary>
    private static void ConvertBhkRigidBody(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;

        // bhkEntity → bhkWorldObject → bhkSerializable: Ref Shape
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // bhkRigidBodyCInfo550_660 structure (for FNV)
        // 4 bytes unused
        pos += 4;

        if (pos + 8 > end) return;
        // HavokFilter (8 bytes: 4 for layer/flags, 4 for group)
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        pos += 8;

        // 4 bytes unused
        pos += 4;

        // byte CollisionResponse + byte unused + ushort ProcessContactCallbackDelay
        if (pos + 4 > end) return;
        SwapUInt16InPlace(buf, pos + 2); // ProcessContactCallbackDelay
        pos += 4;

        // 4 bytes unused
        pos += 4;

        // Vector4 Translation (16 bytes)
        if (pos + 16 > end) return;
        SwapVector4InPlace(buf, pos);
        pos += 16;

        // hkQuaternion Rotation (16 bytes - XYZW floats)
        if (pos + 16 > end) return;
        SwapVector4InPlace(buf, pos);
        pos += 16;

        // Vector4 LinearVelocity
        if (pos + 16 > end) return;
        SwapVector4InPlace(buf, pos);
        pos += 16;

        // Vector4 AngularVelocity
        if (pos + 16 > end) return;
        SwapVector4InPlace(buf, pos);
        pos += 16;

        // hkMatrix3 InertiaTensor (12 floats = 48 bytes, stored as 3 rows of Vector4)
        if (pos + 48 > end) return;
        SwapVector4InPlace(buf, pos);
        SwapVector4InPlace(buf, pos + 16);
        SwapVector4InPlace(buf, pos + 32);
        pos += 48;

        // Vector4 Center
        if (pos + 16 > end) return;
        SwapVector4InPlace(buf, pos);
        pos += 16;

        // 10 floats: Mass, LinearDamping, AngularDamping, Friction, Restitution,
        //            MaxLinearVelocity, MaxAngularVelocity, PenetrationDepth (8 floats for FNV)
        // Plus 4 bytes for motion enums
        var remainingFloats = Math.Min(8, (end - pos) / 4);
        for (var i = 0; i < remainingFloats; i++)
        {
            SwapUInt32InPlace(buf, pos + i * 4);
        }
        pos += remainingFloats * 4;

        // Motion System, Deactivator Type, Solver Deactivation, Quality Type (4 bytes total as enums/bytes)
        // These are typically 1 byte each, no swapping needed
        pos += 4;

        // 12 bytes unused
        pos += 12;

        // uint NumConstraints
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numConstraints = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // Ref[] Constraints
        for (uint i = 0; i < numConstraints && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // uint BodyFlags (may be 2 bytes in later versions, but FNV uses 4)
        if (pos + 4 <= end)
        {
            SwapUInt32InPlace(buf, pos);
        }
    }

    /// <summary>
    /// bhkCollisionObject: Simple collision wrapper.
    /// Structure:
    ///   - ushort Flags
    ///   - Ref Body (bhkWorldObject)
    /// </summary>
    private static void ConvertBhkCollisionObject(byte[] buf, int pos, int size, int[] blockRemap)
    {
        if (size < 6) return;

        // Note: NiCollisionObject base has target Ptr but it's typically -1 and already handled
        // bhkNiCollisionObject adds:
        // ushort Flags
        SwapUInt16InPlace(buf, pos);
        pos += 2;

        // Ref Body
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
    }

    /// <summary>
    /// bhkBlendCollisionObject: Collision for skeletons with blend factors.
    /// </summary>
    private static void ConvertBhkBlendCollisionObject(byte[] buf, int pos, int size, int[] blockRemap)
    {
        if (size < 14) return;

        // ushort Flags
        SwapUInt16InPlace(buf, pos);
        pos += 2;

        // Ref Body
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // float HeirGain
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // float VelGain
        SwapUInt32InPlace(buf, pos);
    }

    /// <summary>
    /// bhkConvexVerticesShape: Convex hull collision shape.
    /// Structure:
    ///   - bhkWorldObjCInfoProperty VerticesProperty (8 bytes)
    ///   - bhkWorldObjCInfoProperty NormalsProperty (8 bytes)
    ///   - uint NumVertices
    ///   - Vector4[NumVertices]
    ///   - uint NumNormals
    ///   - Vector4[NumNormals]
    /// </summary>
    private static void ConvertBhkConvexVerticesShape(byte[] buf, int pos, int size)
    {
        var end = pos + size;
        if (size < 24) return;

        // bhkWorldObjCInfoProperty (2x uint = 8 bytes each)
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        pos += 8;
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        pos += 8;

        // uint NumVertices
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numVertices = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // Vector4[NumVertices]
        for (uint i = 0; i < numVertices && pos + 16 <= end; i++)
        {
            SwapVector4InPlace(buf, pos);
            pos += 16;
        }

        // uint NumNormals
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numNormals = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // Vector4[NumNormals]
        for (uint i = 0; i < numNormals && pos + 16 <= end; i++)
        {
            SwapVector4InPlace(buf, pos);
            pos += 16;
        }
    }

    /// <summary>
    /// bhkBoxShape: Simple box collision.
    /// Inherits from bhkConvexShape which has Material (8 bytes) + Radius (4 bytes)
    /// </summary>
    private static void ConvertBhkBoxShape(byte[] buf, int pos, int size)
    {
        if (size < 28) return;

        // HavokMaterial (8 bytes)
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        pos += 8;

        // float Radius
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // 8 bytes unused
        pos += 8;

        // Vector3 Dimensions (half-extents)
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        SwapUInt32InPlace(buf, pos + 8);
        pos += 12;

        // float UnusedFloat (W component padding)
        if (pos + 4 <= size)
        {
            SwapUInt32InPlace(buf, pos);
        }
    }

    /// <summary>
    /// bhkSphereShape: Simple sphere collision.
    /// </summary>
    private static void ConvertBhkSphereShape(byte[] buf, int pos, int size)
    {
        if (size < 12) return;

        // HavokMaterial (8 bytes)
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        pos += 8;

        // float Radius
        SwapUInt32InPlace(buf, pos);
    }

    /// <summary>
    /// bhkCapsuleShape: Capsule collision.
    /// </summary>
    private static void ConvertBhkCapsuleShape(byte[] buf, int pos, int size)
    {
        if (size < 40) return;

        // HavokMaterial (8 bytes)
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        pos += 8;

        // float Radius (base)
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // 8 bytes unused
        pos += 8;

        // Vector3 FirstPoint + float Radius1
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        SwapUInt32InPlace(buf, pos + 8);
        SwapUInt32InPlace(buf, pos + 12);
        pos += 16;

        // Vector3 SecondPoint + float Radius2
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        SwapUInt32InPlace(buf, pos + 8);
        SwapUInt32InPlace(buf, pos + 12);
    }

    /// <summary>
    /// bhkListShape: A list of shapes.
    /// </summary>
    private static void ConvertBhkListShape(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;
        if (size < 24) return;

        // uint NumSubShapes
        SwapUInt32InPlace(buf, pos);
        var numSubShapes = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // Ref[NumSubShapes] SubShapes
        for (uint i = 0; i < numSubShapes && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // HavokMaterial (8 bytes)
        if (pos + 8 > end) return;
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        pos += 8;

        // bhkWorldObjCInfoProperty ChildShapeProperty (8 bytes)
        if (pos + 8 > end) return;
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        pos += 8;

        // bhkWorldObjCInfoProperty ChildFilterProperty (8 bytes)
        if (pos + 8 > end) return;
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        pos += 8;

        // uint NumFilters
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numFilters = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // HavokFilter[NumFilters] (8 bytes each)
        for (uint i = 0; i < numFilters && pos + 8 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            SwapUInt32InPlace(buf, pos + 4);
            pos += 8;
        }
    }

    /// <summary>
    /// bhkConvexTransformShape/bhkTransformShape: Shape with transform matrix.
    /// </summary>
    private static void ConvertBhkTransformShape(byte[] buf, int pos, int size, int[] blockRemap)
    {
        if (size < 84) return;

        // Ref Shape
        SwapUInt32InPlace(buf, pos);
        RemapBlockRefInPlace(buf, pos, blockRemap);
        pos += 4;

        // HavokMaterial (8 bytes)
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        pos += 8;

        // float Radius
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // 8 bytes unused
        pos += 8;

        // Matrix44 Transform (16 floats = 64 bytes)
        for (var i = 0; i < 16; i++)
        {
            SwapUInt32InPlace(buf, pos + i * 4);
        }
    }

    /// <summary>
    /// bhkNiTriStripsShape: Collision using NiTriStripsData.
    /// </summary>
    private static void ConvertBhkNiTriStripsShape(byte[] buf, int pos, int size, int[] blockRemap)
    {
        var end = pos + size;
        if (size < 48) return;

        // HavokMaterial (8 bytes)
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        pos += 8;

        // float Radius
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // 20 bytes unused
        pos += 20;

        // uint GrowBy
        SwapUInt32InPlace(buf, pos);
        pos += 4;

        // Vector4 Scale (16 bytes)
        SwapVector4InPlace(buf, pos);
        pos += 16;

        // uint NumStripsData
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numStripsData = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // Ref[NumStripsData] StripsData
        for (uint i = 0; i < numStripsData && pos + 4 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            RemapBlockRefInPlace(buf, pos, blockRemap);
            pos += 4;
        }

        // uint NumFilters
        if (pos + 4 > end) return;
        SwapUInt32InPlace(buf, pos);
        var numFilters = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
        pos += 4;

        // HavokFilter[NumFilters] (8 bytes each)
        for (uint i = 0; i < numFilters && pos + 8 <= end; i++)
        {
            SwapUInt32InPlace(buf, pos);
            SwapUInt32InPlace(buf, pos + 4);
            pos += 8;
        }
    }

    /// <summary>
    /// Helper to swap a Vector4 (16 bytes = 4 floats) in place.
    /// </summary>
    private static void SwapVector4InPlace(byte[] buf, int pos)
    {
        SwapUInt32InPlace(buf, pos);
        SwapUInt32InPlace(buf, pos + 4);
        SwapUInt32InPlace(buf, pos + 8);
        SwapUInt32InPlace(buf, pos + 12);
    }
}
