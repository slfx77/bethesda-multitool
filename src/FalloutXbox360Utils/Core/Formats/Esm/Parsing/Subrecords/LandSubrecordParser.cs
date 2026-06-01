using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Subrecords;

/// <summary>
///     Parses LAND record subrecords (VHGT, VCLR, BTXT, ATXT, VTXT, VTEX) from a record's
///     data section. Used by ESM record extraction (master plugin bytes) and by the
///     master-ESM fallback path in <c>EsmLandEnricher</c>. Handles both little-endian
///     (PC ESM/ESP) and big-endian (Xbox 360 ESM-in-DMP) input.
/// </summary>
internal static class LandSubrecordParser
{
    /// <summary>
    ///     Parses all LAND subrecords carrying heightmap, vertex color, texture layer and
    ///     blend-entry data. Source provenance on the returned <see cref="LandVisualData" />
    ///     is <see cref="VisualDataSource.Dmp" /> when <paramref name="isBigEndian" /> is true
    ///     (Xbox 360 DMP byte stream) and <see cref="VisualDataSource.MasterEsm" /> otherwise.
    /// </summary>
    /// <param name="recordData">LAND record's data section (post-record-header bytes).</param>
    /// <param name="dataSize">Valid byte count within <paramref name="recordData" />.</param>
    /// <param name="isBigEndian">True for Xbox 360 big-endian input; false for PC little-endian.</param>
    /// <param name="recordHeaderOffset">Absolute file offset of the LAND record header; used to anchor sub-offsets for diagnostics.</param>
    public static LandSubrecordParseResult Parse(
        byte[] recordData,
        int dataSize,
        bool isBigEndian,
        long recordHeaderOffset = 0)
    {
        LandHeightmap? heightmap = null;
        var textureLayers = new List<LandTextureLayer>();
        var vclrBlocks = new List<byte[]>();
        var vtexValues = new List<uint>();
        var vclrByteCount = 0;
        var vtexCount = 0;
        var btxtCount = 0;
        var atxtCount = 0;
        var vtxtCount = 0;
        var vtxtByteCount = 0;
        var unattachedVtxtCount = 0;
        var unattachedVtxtByteCount = 0;
        int? lastAtxtLayerIndex = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(recordData, dataSize, isBigEndian))
        {
            var subData = recordData.AsSpan(sub.DataOffset, sub.DataLength);

            if (sub.Signature == "VHGT")
            {
                var vhgt = SubrecordSchemaReader.ReadVhgtHeightmap(subData, isBigEndian);
                if (vhgt.HasValue)
                {
                    heightmap = new LandHeightmap
                    {
                        HeightOffset = vhgt.Value.heightOffset,
                        HeightDeltas = vhgt.Value.deltas,
                        Offset = recordHeaderOffset + 24 + sub.DataOffset
                    };
                }
            }
            else if (sub.Signature is "ATXT" or "BTXT")
            {
                var isAlpha = sub.Signature == "ATXT";
                if (isAlpha)
                {
                    atxtCount++;
                }
                else
                {
                    btxtCount++;
                }

                if (sub.DataLength < 8)
                {
                    continue;
                }

                var textureFormId = SubrecordSchemaReader.ReadNameFormId(subData, isBigEndian);
                if (textureFormId.HasValue)
                {
                    var quadrant = subData[4];
                    var platformFlag = subData[5];
                    var layer = isBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(subData[6..])
                        : BinaryPrimitives.ReadUInt16LittleEndian(subData[6..]);

                    textureLayers.Add(new LandTextureLayer
                    {
                        Kind = isAlpha ? LandTextureLayerKind.Alpha : LandTextureLayerKind.Base,
                        TextureFormId = textureFormId.Value,
                        Quadrant = quadrant,
                        PlatformFlag = platformFlag,
                        Layer = layer,
                        Offset = recordHeaderOffset + 24 + sub.DataOffset
                    });

                    lastAtxtLayerIndex = isAlpha ? textureLayers.Count - 1 : null;
                }
            }
            else if (sub.Signature == "VCLR")
            {
                vclrByteCount += sub.DataLength;
                if (sub.DataLength > 0)
                {
                    vclrBlocks.Add(subData.ToArray());
                }
            }
            else if (sub.Signature == "VTEX")
            {
                vtexCount += sub.DataLength / 4;
                for (var i = 0; i + 4 <= sub.DataLength; i += 4)
                {
                    vtexValues.Add(isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData.Slice(i, 4))
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData.Slice(i, 4)));
                }
            }
            else if (sub.Signature == "VTXT")
            {
                vtxtCount++;
                vtxtByteCount += sub.DataLength;

                var entries = ParseLandTextureBlendEntries(subData, isBigEndian);
                if (lastAtxtLayerIndex.HasValue && lastAtxtLayerIndex.Value < textureLayers.Count)
                {
                    textureLayers[lastAtxtLayerIndex.Value].BlendEntries.AddRange(entries);
                }
                else
                {
                    unattachedVtxtCount++;
                    unattachedVtxtByteCount += sub.DataLength;
                }
            }
        }

        var source = isBigEndian ? VisualDataSource.Dmp : VisualDataSource.MasterEsm;
        var combinedVclr = CombineBlocks(vclrBlocks);
        var combinedIndices = vtexValues.Count > 0 ? vtexValues.ToArray() : null;

        LandVisualData? visualData = null;
        if (combinedVclr is not null || combinedIndices is not null || textureLayers.Count > 0 ||
            unattachedVtxtCount > 0)
        {
            visualData = new LandVisualData
            {
                VertexColors = combinedVclr,
                TextureIndices = combinedIndices,
                TextureLayers = textureLayers,
                UnattachedVtxtCount = unattachedVtxtCount,
                UnattachedVtxtByteCount = unattachedVtxtByteCount,
                Source = source,
                VertexColorsSource = combinedVclr is { Length: > 0 } ? source : VisualDataSource.None,
                TextureIndicesSource = combinedIndices is { Length: > 0 } ? source : VisualDataSource.None,
                TextureLayersSource = textureLayers.Count > 0 ? source : VisualDataSource.None
            };
        }

        return new LandSubrecordParseResult(
            heightmap,
            visualData,
            vclrByteCount,
            vtexCount,
            btxtCount,
            atxtCount,
            vtxtCount,
            vtxtByteCount,
            unattachedVtxtCount,
            unattachedVtxtByteCount);
    }

    /// <summary>
    ///     Convenience overload for callers that only need <see cref="LandVisualData" /> and
    ///     not the heightmap or diagnostic counters (e.g., master-ESM fallback enrichment).
    /// </summary>
    public static LandVisualData? ParseVisualOnly(byte[] recordData, int dataSize, bool isBigEndian)
    {
        return Parse(recordData, dataSize, isBigEndian).VisualData;
    }

    private static List<LandTextureBlendEntry> ParseLandTextureBlendEntries(
        ReadOnlySpan<byte> data,
        bool isBigEndian)
    {
        var entries = new List<LandTextureBlendEntry>(data.Length / 8);
        for (var i = 0; i + 8 <= data.Length; i += 8)
        {
            var position = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i, 2));
            if (position > 288)
            {
                continue;
            }

            var opacity = isBigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(data.Slice(i + 4, 4))
                : BinaryPrimitives.ReadSingleLittleEndian(data.Slice(i + 4, 4));
            if (!float.IsFinite(opacity))
            {
                continue;
            }

            entries.Add(new LandTextureBlendEntry(position, data[i + 2], data[i + 3], opacity));
        }

        return entries;
    }

    private static byte[]? CombineBlocks(List<byte[]> blocks)
    {
        if (blocks.Count == 0)
        {
            return null;
        }

        if (blocks.Count == 1)
        {
            return blocks[0];
        }

        var total = blocks.Sum(b => b.Length);
        var combined = new byte[total];
        var offset = 0;
        foreach (var block in blocks)
        {
            Buffer.BlockCopy(block, 0, combined, offset, block.Length);
            offset += block.Length;
        }

        return combined;
    }
}

/// <summary>
///     Result of <see cref="LandSubrecordParser.Parse" />: structured heightmap + visual data
///     plus the diagnostic counters that <see cref="Models.World.ExtractedLandRecord" /> records.
/// </summary>
internal readonly record struct LandSubrecordParseResult(
    LandHeightmap? Heightmap,
    LandVisualData? VisualData,
    int VclrByteCount,
    int VtexCount,
    int BtxtCount,
    int AtxtCount,
    int VtxtCount,
    int VtxtByteCount,
    int UnattachedVtxtCount,
    int UnattachedVtxtByteCount);
