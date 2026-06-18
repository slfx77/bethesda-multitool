namespace FalloutXbox360Utils.Core.Formats.SpeedTree;

/// <summary>
///     Token constants for the SpeedTree <c>.spt</c> ("__IdvSpt_02_") format, reverse-engineered
///     from Geck.exe (x86). Sections are <c>begin = N</c> / <c>end = N + 1</c> pairs.
/// </summary>
internal static class SpeedTreeTokens
{
    public const string Magic = "__IdvSpt_02_";

    // Top-level file framing.
    public const uint BeginFile = 1000;   // 0x3E8 — followed by the magic string
    public const uint EndFile = 1001;     // 0x3E9
    public const uint BeginGeneral = 1002; // 0x3EA
    public const uint EndGeneral = 1003;   // 0x3EB
    public const uint BeginLeaves = 1004;  // 0x3EC
    public const uint EndLeaves = 1005;    // 0x3ED
    public const uint BeginWind = 1011;    // 0x3F3
    public const uint EndWind = 1012;      // 0x3F4
    public const uint BeginLod = 7000;     // 0x1B58
    public const uint EndLod = 7001;       // 0x1B59
    public const uint LodLevel = 7002;     // 0x1B5A
    public const uint EndLodLevel = 7003;  // 0x1B5B
    public const uint LodLeafData = 7004;  // 0x1B5C

    // Section 1002 (general) sub-tokens.
    public const uint GenBarkTexture = 2000; // 0x7D0 — ReadString (bark texture path)
    public const uint GenFloat2001 = 2001;   // 0x7D1 — float @ +0x40
    public const uint GenByte2002 = 2002;    // 0x7D2 — byte
    public const uint GenFloat2003 = 2003;   // 0x7D3 — float @ +0x44
    public const uint GenToken2004 = 2004;   // 0x7D4 — token (discarded)
    public const uint GenToken2005 = 2005;   // 0x7D5 — token → LOD/config (not geometry)
    public const uint GenFloat2006 = 2006;   // 0x7D6 — float @ +0x4C
    public const uint GenFloat2007 = 2007;   // 0x7D7 — float @ +0x50

    public const uint BeginBranchSection = 0x3F6; // 1014 — branch-list sub-section in 1002
    public const uint EndBranchSection = 0x3F7;   // 1015

    // Branch record framing + sub-tokens.
    public const uint BeginBranch = 0x3F8; // 1016
    public const uint EndBranch = 0x3F9;   // 1017
    public const uint BranchSpline0 = 6000; // 0x1770 .. 6007 (0x1777) — 8 BezierSpline strings
    public const uint BranchSpline7 = 6007;
    public const uint BranchUInt6008 = 6008; // 0x1778
    public const uint BranchUInt6009 = 6009; // 0x1779
    public const uint BranchFloat6010 = 6010; // 0x177A .. 6014 (0x177E)
    public const uint BranchFloat6014 = 6014;
    public const uint BranchBool6015 = 6015; // 0x177F — 1 byte
    public const uint BranchBool6016 = 6016; // 0x1780 — 1 byte
    public const uint BranchSpline8 = 6017;  // 0x1781 — 9th BezierSpline string

    // Section 1004 (leaves) framing + sub-tokens.
    public const uint LeafFloat3000 = 3000;     // 0xBB8 — float @ +0x2C
    public const uint LeafTableMin = 3001;      // 0xBB9 — start of general leaf-table params
    public const uint LeafTableMax = 3010;      // 0xBC2 — cases 0..9 of (token - 0xBB9)
    public const uint BeginLeafCollection = 0x3F1; // 1009
    public const uint EndLeafCollection = 0x3F2;   // 1010 — trailing collection end
    public const uint LeafTableMarker = 0x3EE;     // 1006 — precedes the leaf count
    public const uint BeginLeaf = 0x3EF;           // 1007 — per-leaf begin
    public const uint EndLeaf = 0x3F0;             // 1008 — per-leaf end
    public const uint LeafByte4000 = 4000;  // 0xFA0 — byte (leaf type / texture index)
    public const uint LeafVec4001 = 4001;   // 0xFA1 — vec3 (position)
    public const uint LeafFloat4002 = 4002; // 0xFA2 — float (size)
    public const uint LeafString4003 = 4003; // 0xFA3 — ReadString (material / texture path)
    public const uint LeafVec4004 = 4004;   // 0xFA4 — vec3 (card corner / orientation basis)
    public const uint LeafVec4005 = 4005;   // 0xFA5 — vec3
    public const uint LeafVec4006 = 4006;   // 0xFA6 — vec3
    public const uint LeafFloat4007 = 4007; // 0xFA7 — float

    // Section 1011 (wind) sub-tokens.
    public const uint WindVec5000 = 5000; // 0x1388
    public const uint WindVec5001 = 5001;
    public const uint WindVec5002 = 5002;
    public const uint WindVec5003 = 5003;
    public const uint WindVec5004 = 5004;
    public const uint WindFloat5005 = 5005;
    public const uint WindByte5006 = 5006;
}
