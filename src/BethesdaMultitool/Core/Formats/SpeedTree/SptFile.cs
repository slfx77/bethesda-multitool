using System.Numerics;
using BethesdaMultitool.Core.Utils;
using static BethesdaMultitool.Core.Formats.SpeedTree.SpeedTreeTokens;

namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     Parser for the SpeedTree <c>.spt</c> ("__IdvSpt_02_") token-stream format used by FNV's
///     trees. Mirrors <c>CSpeedTreeRT::LoadTree</c> (FUN_008D6170 in Geck.exe): a 1000-magic header
///     followed by 1002 (general, incl. the 0x3F6 branch sub-section), 1004 (leaves), and 1011
///     (wind) sections, terminated by 1001. A trailing optional 7000 (leaf LOD) section is ignored.
/// </summary>
public static class SptFile
{
    // Post-tree branch-LOD sub-section tokens (CTreeEngine::ParseLodInfo, 360 MemDebug 0x8298A5B0).
    private const uint LodBranchEnd = 9006; // 0x232E section terminator
    private const uint LodNumBranchLods = 9007; // 0x232F -> +0x70
    private const uint LodBranchFar = 9008; // 0x2330 -> +0xdc
    private const uint LodLeafSizeIncrease = 9010; // 0x2332 -> +0xe4 (0 is normalized to 0.1)
    private const uint LodNumLeafLods = 9011; // 0x2333 -> +0xc0
    private const uint LodBranchNear = 9012; // 0x2334 -> +0xe0 (LOD0)
    private const uint LodBranchDemotion = 9013; // 0x2335 -> +0xe8 (BuildBranchLods demotion draw bound)
    private const uint LodBranchGuarantee = 9014; // 0x2336 -> +0xec (guaranteed-keep threshold fraction)

    /// <summary>Parse a <c>.spt</c> buffer, returning null if it is not a valid "__IdvSpt_02_" tree.</summary>
    public static SptModel? TryParse(byte[] data)
    {
        if (data is null || data.Length < 16)
        {
            return null;
        }

        try
        {
            return Parse(data);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Parse a <c>.spt</c> buffer; throws <see cref="InvalidDataException" /> on malformed input.</summary>
    public static SptModel Parse(byte[] data)
    {
        var c = new SptCursor(data);
        c.Expect(BeginFile, "begin-file");

        var magic = c.ReadString();
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"SPT: bad magic '{magic}' (expected '{Magic}').");
        }

        SptGeneralParams? general = null;
        var branches = new List<SptBranch>();
        var leaves = new List<SptLeaf>();
        var leafSize = 0.75f;
        SptLeafTable? leafTable = null;
        SptWind? wind = null;

        var token = c.ReadToken();
        while (token != EndFile)
        {
            switch (token)
            {
                case BeginGeneral:
                    general = ParseGeneral(c, branches);
                    break;
                case BeginLeaves:
                    ParseLeaves(c, leaves, ref leafSize, ref leafTable);
                    break;
                case BeginWind:
                    wind = ParseWind(c);
                    break;
                default:
                    throw new InvalidDataException(
                        $"SPT: unexpected top-level section token 0x{token:X} at offset {c.Position - 4}.");
            }

            token = c.ReadToken();
        }

        var leafTextureCoords = ParseTrailingTextureCoordInfo(data, c.Position);
        var lod = ParseTrailingLodInfo(data, c.Position);
        var frond = ParseTrailingFrondInfo(data, c.Position);

        return new SptModel
        {
            General = general ?? new SptGeneralParams(),
            Branches = branches,
            Leaves = leaves,
            LeafTextureCoords = leafTextureCoords,
            LeafSize = leafSize,
            LeafTable = leafTable,
            Wind = wind,
            Lod = lod,
            Frond = frond
        };
    }

    /// <summary>
    ///     Scan the post-tree region for the branch-LOD sub-section and recover the LOD0 keep fraction the
    ///     engine uses to decimate the rendered mesh. The section is a flat token/value stream beginning at
    ///     <see cref="LodNumBranchLods" /> and ending at <see cref="LodBranchEnd" />; each token is followed
    ///     by one 4-byte value (uint or float). Mirrors <c>CTreeEngine::ParseLodInfo</c>. Returns null when
    ///     no well-formed section is present (the engine then keeps the ctor defaults = no decimation).
    /// </summary>
    private static SptLodInfo? ParseTrailingLodInfo(byte[] data, int startOffset)
    {
        for (var offset = startOffset; offset <= data.Length - 8; offset += 4)
        {
            if (BinaryUtils.ReadUInt32LE(data, offset) != LodNumBranchLods)
            {
                continue;
            }

            var num = BinaryUtils.ReadUInt32LE(data, offset + 4);
            if (num is 0 or > 64)
            {
                continue; // false match inside another payload — a sane LOD count is small
            }

            int? numLods = (int)num;
            int? numLeafLods = null;
            float? near = null, far = null, leafSizeIncrease = null, demotion = null, guarantee = null;
            var pos = offset + 8;
            var terminated = false;
            // Walk (token, value) pairs to the section terminator; ignore tokens we don't model.
            for (var guard = 0; guard < 16 && pos <= data.Length - 4; guard++)
            {
                var token = BinaryUtils.ReadUInt32LE(data, pos);
                if (token == LodBranchEnd)
                {
                    terminated = true;
                    break;
                }

                if (pos > data.Length - 8)
                {
                    break;
                }

                switch (token)
                {
                    case LodBranchFar:
                        far = BinaryUtils.ReadFloatLE(data, pos + 4);
                        break;
                    case LodLeafSizeIncrease:
                        leafSizeIncrease = BinaryUtils.ReadFloatLE(data, pos + 4);
                        break;
                    case LodNumLeafLods:
                        var authoredLeafLods = BinaryUtils.ReadUInt32LE(data, pos + 4);
                        if (authoredLeafLods is > 0 and <= 64)
                        {
                            numLeafLods = (int)authoredLeafLods;
                        }

                        break;
                    case LodBranchNear:
                        near = BinaryUtils.ReadFloatLE(data, pos + 4);
                        break;
                    case LodBranchDemotion:
                        demotion = BinaryUtils.ReadFloatLE(data, pos + 4);
                        break;
                    case LodBranchGuarantee:
                        guarantee = BinaryUtils.ReadFloatLE(data, pos + 4);
                        break;
                }

                pos += 8;
            }

            if (terminated && near is not null)
            {
                var defaults = new SptLodInfo();
                return new SptLodInfo
                {
                    NumBranchLods = numLods.Value,
                    BranchNearFraction = near.Value,
                    BranchFarFraction = far ?? defaults.BranchFarFraction,
                    NumLeafLods = numLeafLods ?? defaults.NumLeafLods,
                    LeafLodSizeIncrease = leafSizeIncrease is null or 0f
                        ? defaults.LeafLodSizeIncrease
                        : leafSizeIncrease.Value,
                    BranchDemotionRandomness = demotion ?? defaults.BranchDemotionRandomness,
                    BranchGuaranteeFraction = guarantee ?? defaults.BranchGuaranteeFraction
                };
            }
        }

        return null;
    }

    /// <summary>
    ///     Scan the post-tree region for the frond section (token 13000 → <c>CFrondEngine::Parse</c>) and
    ///     recover the enabled flag + gate level. Only 13002/13007 are modeled; every other frond token is
    ///     skipped by its known payload shape, and the candidate is abandoned (scan continues) on anything
    ///     malformed — a matching uint inside another payload is possible. Returns null when no well-formed
    ///     section exists (the <c>CFrondEngine</c> ctor default is then DISABLED).
    /// </summary>
    private static SptFrond? ParseTrailingFrondInfo(byte[] data, int startOffset)
    {
        for (var offset = startOffset; offset <= data.Length - 4; offset++)
        {
            if (BinaryUtils.ReadUInt32LE(data, offset) != BeginFrondInfo)
            {
                continue;
            }

            try
            {
                var c = new SptCursor(data, offset + 4);
                var enabled = false;
                var level = new SptFrond().Level;
                var token = c.ReadToken();
                // 2048 tokens bounds the walk far above any real section (blade lists included).
                var guard = 0;
                while (token != EndFrondInfo)
                {
                    if (guard++ > 2048)
                    {
                        throw new InvalidDataException("SPT: runaway frond section.");
                    }

                    switch (token)
                    {
                        case FrondLevel:
                            level = (int)c.ReadToken();
                            break;
                        case FrondEnabled:
                            enabled = c.ReadByte() != 0;
                            break;
                        case FrondInt13003 or FrondInt13004 or FrondInt13006 or FrondInt13009
                            or FrondInt14007 or FrondInt14008 or >= FrondFloat13010 and <= FrondFloat13013:
                            c.ReadToken();
                            break;
                        case FrondProfile:
                            c.ReadString();
                            break;
                        case FrondBladeList:
                            var blades = c.ReadToken();
                            if (blades > 64)
                            {
                                throw new InvalidDataException("SPT: implausible frond blade count.");
                            }

                            for (var b = 0; b < blades; b++)
                            {
                                c.ReadToken(); // skip the per-blade begin marker
                                var sub = c.ReadToken();
                                while (sub != FrondBladeEnd)
                                {
                                    if (sub == FrondBladeTexture)
                                    {
                                        c.ReadString();
                                    }
                                    else if (sub is > FrondBladeTexture and <= FrondBladeFloat3)
                                    {
                                        c.ReadFloat();
                                    }
                                    else
                                    {
                                        throw new InvalidDataException(
                                            $"SPT: unknown frond blade token 0x{sub:X}.");
                                    }

                                    sub = c.ReadToken();
                                }
                            }

                            break;
                        default:
                            throw new InvalidDataException($"SPT: unknown frond token 0x{token:X}.");
                    }

                    token = c.ReadToken();
                }

                return new SptFrond { Enabled = enabled, Level = level };
            }
            catch (InvalidDataException)
            {
                // False match inside another payload — keep scanning for a well-formed section.
            }
        }

        return null;
    }

    /// <summary>
    ///     <c>CSpeedTreeRT::LoadTree</c> continues after <c>CTreeEngine::Parse</c> consumes token 1001.
    ///     The post-tree token 10000 is <c>ParseTextureCoordInfo</c>; its 10002 child is the leaf UV
    ///     block array later passed to <c>CLeafGeometry::SetTextureCoords</c>.
    /// </summary>
    private static List<SptLeafTextureCoords> ParseTrailingTextureCoordInfo(byte[] data, int startOffset)
    {
        for (var offset = startOffset; offset <= data.Length - 4; offset++)
        {
            if (BinaryUtils.ReadUInt32LE(data, offset) != BeginTextureCoordInfo)
            {
                continue;
            }

            var leafTextureCoords = new List<SptLeafTextureCoords>();
            try
            {
                var c = new SptCursor(data, offset + 4);
                ParseTextureCoordInfo(c, leafTextureCoords);
                return leafTextureCoords;
            }
            catch (InvalidDataException)
            {
                // A matching uint inside another payload is possible when scanning optional post-tree
                // sections. Keep scanning until a complete 10000/10001 section is found.
            }
        }

        return [];
    }

    private static void ParseTextureCoordInfo(SptCursor c, List<SptLeafTextureCoords> leafTextureCoords)
    {
        var token = c.ReadToken();
        while (token != EndTextureCoordInfo)
        {
            switch (token)
            {
                case LeafTextureCoords:
                    var count = ReadTextureCoordBlockCount(c, "leaf");
                    leafTextureCoords.Clear();
                    for (var i = 0; i < count; i++)
                    {
                        leafTextureCoords.Add(ReadLeafTextureCoords(c));
                    }

                    break;
                case FrondTextureCoords:
                case BillboardTextureCoords:
                    c.Skip(ReadTextureCoordBlockCount(c, "non-leaf") * 8 * sizeof(float));
                    break;
                case TextureCoordString:
                    c.ReadString();
                    break;
                case TextureCoordBool0:
                case TextureCoordBool1:
                    c.ReadByte();
                    break;
                default:
                    throw new InvalidDataException(
                        $"SPT: malformed texture-coordinate section, token 0x{token:X} at offset {c.Position - 4}.");
            }

            token = c.ReadToken();
        }
    }

    private static int ReadTextureCoordBlockCount(SptCursor c, string section)
    {
        var count = c.ReadToken();
        var maxCount = c.HasBytes(0) ? (uint)((c.Length - c.Position) / (8 * sizeof(float))) : 0u;
        if (count > maxCount)
        {
            throw new InvalidDataException(
                $"SPT: {section} texture-coordinate block count {count} exceeds remaining data at offset {c.Position - 4}.");
        }

        return (int)count;
    }

    private static SptLeafTextureCoords ReadLeafTextureCoords(SptCursor c)
    {
        return new SptLeafTextureCoords(ReadUv(c), ReadUv(c), ReadUv(c), ReadUv(c));
    }

    private static Vector2 ReadUv(SptCursor c)
    {
        return new Vector2(c.ReadFloat(), c.ReadFloat());
    }

    private static SptGeneralParams ParseGeneral(SptCursor c, List<SptBranch> branches)
    {
        string? bark = null;
        float f2001 = 0, f2003 = 0, f2006 = 0, f2007 = 0;
        byte b2002 = 0;
        uint t2005 = 0;

        var token = c.ReadToken();
        while (token != EndGeneral)
        {
            switch (token)
            {
                case GenBarkTexture:
                    bark = c.ReadString();
                    break;
                case GenFloat2001:
                    f2001 = c.ReadFloat();
                    break;
                case GenByte2002:
                    b2002 = c.ReadByte();
                    break;
                case GenFloat2003:
                    f2003 = c.ReadFloat();
                    break;
                case GenToken2004:
                    c.ReadToken();
                    break;
                case GenToken2005:
                    t2005 = c.ReadToken();
                    break;
                case GenFloat2006:
                    f2006 = c.ReadFloat();
                    break;
                case GenFloat2007:
                    f2007 = c.ReadFloat();
                    break;
                case BeginBranchSection:
                    ParseBranchSection(c, branches);
                    break;
                default:
                    throw new InvalidDataException(
                        $"SPT: malformed general tree info, token 0x{token:X} at offset {c.Position - 4}.");
            }

            token = c.ReadToken();
        }

        return new SptGeneralParams
        {
            BarkTexturePath = string.IsNullOrWhiteSpace(bark) ? null : bark,
            Float2001 = f2001,
            Byte2002 = b2002,
            Float2003 = f2003,
            Token2005 = t2005,
            Float2006 = f2006,
            Float2007 = f2007
        };
    }

    private static void ParseBranchSection(SptCursor c, List<SptBranch> branches)
    {
        var count = (int)c.ReadToken();
        if (count < 0)
        {
            throw new InvalidDataException($"SPT: negative branch count {count}.");
        }

        for (var i = 0; i < count; i++)
        {
            branches.Add(ParseBranch(c));
        }

        c.Expect(EndBranchSection, "end-branch-section");
    }

    private static SptBranch ParseBranch(SptCursor c)
    {
        c.Expect(BeginBranch, "begin-branch");

        var splines = new SptBezierSpline?[9];
        uint u6008 = 0, u6009 = 0;
        float f6010 = 0, f6011 = 0, f6012 = 0, f6013 = 1f, f6014 = 1f;
        bool b6015 = true, b6016 = false; // SIdvBranchInfo ctor defaults (bark tiling raw-U / scaled-V)

        var token = c.ReadToken();
        while (token != EndBranch)
        {
            if (token is >= BranchSpline0 and <= BranchSpline7)
            {
                splines[token - BranchSpline0] = SptBezierSpline.Parse(c.ReadString());
            }
            else
            {
                switch (token)
                {
                    case BranchSpline8:
                        splines[8] = SptBezierSpline.Parse(c.ReadString());
                        break;
                    case BranchUInt6008:
                        u6008 = c.ReadToken();
                        break;
                    case BranchUInt6009:
                        u6009 = c.ReadToken();
                        break;
                    case BranchFloat6010:
                        f6010 = c.ReadFloat();
                        break;
                    case 6011:
                        f6011 = c.ReadFloat();
                        break;
                    case 6012:
                        f6012 = c.ReadFloat();
                        break;
                    case 6013:
                        f6013 = c.ReadFloat();
                        break;
                    case BranchFloat6014:
                        f6014 = c.ReadFloat();
                        break;
                    case BranchBool6015:
                        b6015 = c.ReadBool();
                        break;
                    case BranchBool6016:
                        b6016 = c.ReadBool();
                        break;
                    default:
                        throw new InvalidDataException(
                            $"SPT: malformed branch info, token 0x{token:X} at offset {c.Position - 4}.");
                }
            }

            token = c.ReadToken();
        }

        return new SptBranch
        {
            Splines = splines,
            UInt6008 = u6008,
            UInt6009 = u6009,
            Float6010 = f6010,
            Float6011 = f6011,
            Float6012 = f6012,
            Float6013 = f6013,
            Float6014 = f6014,
            Bool6015 = b6015,
            Bool6016 = b6016
        };
    }

    private static void ParseLeaves(SptCursor c, List<SptLeaf> leaves, ref float leafSize, ref SptLeafTable? leafTable)
    {
        uint u3001 = 1, u3008 = 2;
        float f3002 = 0.8f, f3004 = 0, f3005 = 0, f3007 = 0.5f, f3010 = 1f;
        byte b3003 = 0, b3006 = 0, b3009 = 1;
        var sawTable = false;

        var token = c.ReadToken();
        while (token != EndLeaves)
        {
            switch (token)
            {
                case LeafFloat3000:
                    leafSize = c.ReadFloat();
                    break;
                case BeginLeafCollection:
                    ParseLeafCollection(c, leaves);
                    break;
                case 3001: // SIdvLeafInfo+0x2c: blossom branch-depth/ancestor mode
                    u3001 = c.ReadToken();
                    sawTable = true;
                    break;
                case 3002: // SIdvLeafInfo+0x28: blossom probability
                    f3002 = c.ReadFloat();
                    sawTable = true;
                    break;
                case 3003: // case 2 — byte
                    b3003 = c.ReadByte();
                    sawTable = true;
                    break;
                case 3004: // case 3 — float (discarded by engine)
                    f3004 = c.ReadFloat();
                    sawTable = true;
                    break;
                case 3005: // case 4 — float (discarded by engine)
                    f3005 = c.ReadFloat();
                    sawTable = true;
                    break;
                case 3006: // case 5 — byte
                    b3006 = c.ReadByte();
                    sawTable = true;
                    break;
                case 3007: // SIdvLeafInfo+0x20: RoomForLeaf spacing factor
                    f3007 = c.ReadFloat();
                    sawTable = true;
                    break;
                case 3008: // SIdvLeafInfo+0x0c: RoomForLeaf placement mode
                    u3008 = c.ReadToken();
                    sawTable = true;
                    break;
                case 3009: // SIdvLeafInfo+0
                    b3009 = c.ReadByte();
                    sawTable = true;
                    break;
                case 3010: // SIdvLeafInfo+4
                    f3010 = c.ReadFloat();
                    sawTable = true;
                    break;
                default:
                    throw new InvalidDataException(
                        $"SPT: malformed leaf section, token 0x{token:X} at offset {c.Position - 4}.");
            }

            token = c.ReadToken();
        }

        if (sawTable)
        {
            leafTable = new SptLeafTable
            {
                UInt3001 = u3001,
                Float3002 = f3002,
                Byte3003 = b3003,
                Float3004 = f3004,
                Float3005 = f3005,
                Byte3006 = b3006,
                Float3007 = f3007,
                UInt3008 = u3008,
                Byte3009 = b3009,
                Float3010 = f3010
            };
        }
    }

    private static void ParseLeafCollection(SptCursor c, List<SptLeaf> leaves)
    {
        c.Expect(LeafTableMarker, "leaf-table-marker");
        var count = (int)c.ReadToken();
        if (count < 0)
        {
            throw new InvalidDataException($"SPT: negative leaf count {count}.");
        }

        for (var i = 0; i < count; i++)
        {
            leaves.Add(ParseLeaf(c));
        }

        c.Expect(EndLeafCollection, "end-leaf-collection");
    }

    private static SptLeaf ParseLeaf(SptCursor c)
    {
        c.Expect(BeginLeaf, "begin-leaf");

        byte type = 0;
        var position = Vector3.Zero;
        var corner0 = Vector3.Zero;
        var corner1 = Vector3.Zero;
        var corner2 = Vector3.Zero;
        float size = 0, f4007 = 0;
        string? material = null;

        var token = c.ReadToken();
        while (token != EndLeaf)
        {
            switch (token)
            {
                case LeafByte4000:
                    type = c.ReadByte();
                    break;
                case LeafVec4001:
                    position = c.ReadVec3();
                    break;
                case LeafFloat4002:
                    size = c.ReadFloat();
                    break;
                case LeafString4003:
                    material = c.ReadString();
                    break;
                case LeafVec4004:
                    corner0 = c.ReadVec3();
                    break;
                case LeafVec4005:
                    corner1 = c.ReadVec3();
                    break;
                case LeafVec4006:
                    corner2 = c.ReadVec3();
                    break;
                case LeafFloat4007:
                    f4007 = c.ReadFloat();
                    break;
                default:
                    throw new InvalidDataException(
                        $"SPT: malformed leaf info, token 0x{token:X} at offset {c.Position - 4}.");
            }

            token = c.ReadToken();
        }

        return new SptLeaf
        {
            Type = type,
            Position = position,
            Size = size,
            Material = string.IsNullOrWhiteSpace(material) ? null : material,
            Corner0 = corner0,
            Corner1 = corner1,
            Corner2 = corner2,
            Float4007 = f4007
        };
    }

    private static SptWind ParseWind(SptCursor c)
    {
        var v5000 = Vector3.Zero;
        var v5001 = Vector3.Zero;
        var v5002 = Vector3.Zero;
        var v5003 = Vector3.Zero;
        var v5004 = Vector3.Zero;
        float f5005 = 0;
        byte b5006 = 0;

        var token = c.ReadToken();
        while (token != EndWind)
        {
            switch (token)
            {
                case WindVec5000:
                    v5000 = c.ReadVec3();
                    break;
                case WindVec5001:
                    v5001 = c.ReadVec3();
                    break;
                case WindVec5002:
                    v5002 = c.ReadVec3();
                    break;
                case WindVec5003:
                    v5003 = c.ReadVec3();
                    break;
                case WindVec5004:
                    v5004 = c.ReadVec3();
                    break;
                case WindFloat5005:
                    f5005 = c.ReadFloat();
                    break;
                case WindByte5006:
                    b5006 = c.ReadByte();
                    break;
                default:
                    throw new InvalidDataException(
                        $"SPT: malformed wind section, token 0x{token:X} at offset {c.Position - 4}.");
            }

            token = c.ReadToken();
        }

        return new SptWind
        {
            Vec5000 = v5000,
            Vec5001 = v5001,
            Vec5002 = v5002,
            Vec5003 = v5003,
            Vec5004 = v5004,
            Float5005 = f5005,
            Byte5006 = b5006
        };
    }
}
