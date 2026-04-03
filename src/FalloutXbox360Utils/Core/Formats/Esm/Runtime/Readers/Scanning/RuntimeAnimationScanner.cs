using System.Collections.Concurrent;
using System.Text;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Scanning;

/// <summary>
///     Scans memory dumps for TESAnimGroup runtime objects to discover loaded animations.
///     Uses RTTI-based vtable detection to find instances, then reads animation type and name.
///     TESAnimGroup (60 bytes, extends NiRefObject):
///     +0   vtable (ptr, 4 bytes)
///     +4   m_uiRefCount (uint32 BE, from NiRefObject)
///     +8   cSectionPriority (8 bytes)
///     +16  sType (uint16 BE) — animation group type enum (0=Idle, 3=MoveForward, etc.)
///     +20  frameCount (uint32 BE)
///     +24  times (float*) — keyframe time array
///     +28  speed (float[3], 12 bytes)
///     +40  cMorphKey (char)
///     +41  cBlendFrames (uint8)
///     +42  cBlendInFrames (uint8)
///     +43  cBlendOutFrames (uint8)
///     +44  cDecal (uint8)
///     +48  pParentName (char*) — animation name string pointer
///     +52  soundCount (uint32 BE)
///     +56  sounds (ptr)
/// </summary>
internal sealed class RuntimeAnimationScanner(RuntimeMemoryContext context)
{
    private const int TesAnimGroupSize = 60;
    private const int RefCountOffset = 4;
    private const int TypeOffset = 16;
    private const int FrameCountOffset = 20;
    private const int ParentNamePtrOffset = 48;
    private const int SoundCountOffset = 52;

    private const int MaxRefCount = 10_000;
    private const int MaxFrameCount = 100_000;
    private const int MaxSoundCount = 100;
    private const int MaxAnimGroupType = 255;

    private readonly RuntimeMemoryContext _context = context;

    /// <summary>
    ///     Scan the dump for TESAnimGroup objects given a known vtable VA.
    ///     Uses 4-byte aligned vtable pattern matching (TESAnimGroup objects are not
    ///     necessarily 16-byte aligned, unlike NiObject-derived heap allocations).
    ///     Use <see cref="FindTesAnimGroupVtable" /> to discover the vtable first.
    /// </summary>
    public List<DiscoveredAnimation> ScanForAnimations(
        uint vtableVa, IProgress<(long Scanned, long Total)>? progress = null)
    {
        var log = Logger.Instance;

        if (vtableVa == 0)
        {
            log.Info("AnimationScanner: no TESAnimGroup vtable VA provided — skipping");
            return [];
        }

        log.Info("AnimationScanner: TESAnimGroup vtable at 0x{0:X8}, scanning for instances", vtableVa);

        // Build vtable pattern bytes (big-endian)
        var vtableBytes = new byte[4];
        vtableBytes[0] = (byte)(vtableVa >> 24);
        vtableBytes[1] = (byte)(vtableVa >> 16);
        vtableBytes[2] = (byte)(vtableVa >> 8);
        vtableBytes[3] = (byte)vtableVa;

        var animations = new List<DiscoveredAnimation>();
        var minidump = _context.MinidumpInfo;

        // Scan each memory region for vtable pattern at 4-byte alignment
        for (var ri = 0; ri < minidump.MemoryRegions.Count; ri++)
        {
            var region = minidump.MemoryRegions[ri];
            if (region.Size < TesAnimGroupSize)
            {
                continue;
            }

            // Skip module regions (code, not heap data)
            var regionVa = region.VirtualAddress;
            var isModule = minidump.FindModuleByVirtualAddress(regionVa) != null;
            if (isModule)
            {
                continue;
            }

            var buf = _context.ReadBytes(region.FileOffset, (int)Math.Min(region.Size, int.MaxValue));
            if (buf == null)
            {
                continue;
            }

            for (var i = 0; i <= buf.Length - TesAnimGroupSize; i += 4)
            {
                if (buf[i] != vtableBytes[0] || buf[i + 1] != vtableBytes[1] ||
                    buf[i + 2] != vtableBytes[2] || buf[i + 3] != vtableBytes[3])
                {
                    continue;
                }

                // Found vtable match — validate and extract
                if (!FastFilter(buf, i, vtableVa))
                {
                    continue;
                }

                var anim = ExtractAnimation(buf, i, region.FileOffset + i);
                if (anim != null)
                {
                    animations.Add(anim);
                }
            }

            if (ri % 100 == 0)
            {
                progress?.Report((ri, minidump.MemoryRegions.Count));
            }
        }

        animations.Sort((a, b) =>
        {
            var cmp = a.GroupType.CompareTo(b.GroupType);
            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        log.Info("AnimationScanner: found {0} TESAnimGroup instances", animations.Count);
        return animations;
    }

    /// <summary>
    ///     Find the TESAnimGroup primary vtable VA from RTTI census results.
    /// </summary>
    public static uint FindTesAnimGroupVtable(IReadOnlyList<CensusEntry> census)
    {
        foreach (var entry in census)
        {
            if (entry.Rtti.ClassName == "TESAnimGroup" && entry.Rtti.ObjectOffset == 0)
            {
                return entry.Rtti.VtableVA;
            }
        }

        return 0;
    }

    private bool FastFilter(byte[] chunk, int offset, uint vtableVa)
    {
        if (offset + TesAnimGroupSize > chunk.Length)
        {
            return false;
        }

        // Check vtable pointer matches TESAnimGroup
        var vtable = BinaryUtils.ReadUInt32BE(chunk, offset);
        if (vtable != vtableVa)
        {
            return false;
        }

        // RefCount must be reasonable
        var refCount = BinaryUtils.ReadUInt32BE(chunk, offset + RefCountOffset);
        if (refCount == 0 || refCount > MaxRefCount)
        {
            return false;
        }

        // Animation type must be valid enum value
        var animType = BinaryUtils.ReadUInt16BE(chunk, offset + TypeOffset);
        if (animType > MaxAnimGroupType)
        {
            return false;
        }

        return true;
    }

    private DiscoveredAnimation? ExtractAnimation(byte[] chunk, int offset, long fileOffset)
    {
        var animType = BinaryUtils.ReadUInt16BE(chunk, offset + TypeOffset);
        var frameCount = BinaryUtils.ReadUInt32BE(chunk, offset + FrameCountOffset);
        var soundCount = BinaryUtils.ReadUInt32BE(chunk, offset + SoundCountOffset);

        if (frameCount > MaxFrameCount || soundCount > MaxSoundCount)
        {
            return null;
        }

        // Read animation name from pParentName pointer
        var namePtr = BinaryUtils.ReadUInt32BE(chunk, offset + ParentNamePtrOffset);
        string? name = null;
        if (namePtr != 0 && _context.IsValidPointer(namePtr))
        {
            name = ReadNullTerminatedString(namePtr);
        }

        return new DiscoveredAnimation
        {
            FileOffset = fileOffset,
            GroupType = animType,
            GroupTypeName = GetAnimGroupName(animType),
            Name = name,
            FrameCount = (int)frameCount,
            SoundCount = (int)soundCount
        };
    }

    private string? ReadNullTerminatedString(uint ptr)
    {
        var fo = _context.VaToFileOffset(ptr);
        if (fo == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fo.Value, 256);
        if (buf == null)
        {
            return null;
        }

        for (var i = 0; i < buf.Length; i++)
        {
            if (buf[i] == 0)
            {
                return i == 0 ? null : Encoding.ASCII.GetString(buf, 0, i);
            }

            if (buf[i] < 32 || buf[i] > 126)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    ///     Animation group type names from PDB enum (245 values, 0-244 + 255=None).
    ///     Generated from cvdump types_full.txt ANIM_GROUP_* enum.
    /// </summary>
    private static readonly string[] AnimGroupNames = BuildAnimGroupNames();

    private static string GetAnimGroupName(int type)
    {
        if (type == 255) return "None";
        if (type >= 0 && type < AnimGroupNames.Length && AnimGroupNames[type] != null)
            return AnimGroupNames[type];
        return $"Unknown({type})";
    }

    private static string[] BuildAnimGroupNames()
    {
        var names = new string[246];
        names[0] = "Idle"; names[1] = "DynamicIdle"; names[2] = "SpecialIdle";
        names[3] = "MoveForward"; names[4] = "MoveBack"; names[5] = "MoveLeft"; names[6] = "MoveRight";
        names[7] = "FastForward"; names[8] = "FastBack"; names[9] = "FastLeft"; names[10] = "FastRight";
        names[11] = "DodgeForward"; names[12] = "DodgeBack"; names[13] = "DodgeLeft"; names[14] = "DodgeRight";
        names[15] = "TurnLeft"; names[16] = "TurnRight";
        names[17] = "Aim"; names[18] = "AimUp"; names[19] = "AimDown";
        names[20] = "AimIS"; names[21] = "AimISUp"; names[22] = "AimISDown";
        names[23] = "Holster"; names[24] = "Equip"; names[25] = "Unequip";
        // Attack variants: Left(26-31), Right(32-37), 3(38-43), 4(44-49), 5(50-55),
        // 6(56-61), 7(62-67), 8(68-73), Loop(74-79), Spin(80-85), Spin2(86-91)
        string[] attackSuffixes = ["", "Up", "Down", "IS", "ISUp", "ISDown"];
        string[] attackNames = ["AttackLeft", "AttackRight", "Attack3", "Attack4", "Attack5",
            "Attack6", "Attack7", "Attack8", "AttackLoop", "AttackSpin", "AttackSpin2"];
        for (var a = 0; a < attackNames.Length; a++)
            for (var s = 0; s < 6; s++)
                names[26 + a * 6 + s] = attackNames[a] + attackSuffixes[s];
        // Power attacks (92-101), PowerAttackStop (102)
        names[92] = "AttackNormalPower"; names[93] = "AttackForwardPower"; names[94] = "AttackBackPower";
        names[95] = "AttackLeftPower"; names[96] = "AttackRightPower";
        names[97] = "AttackCustom1Power"; names[98] = "AttackCustom2Power"; names[99] = "AttackCustom3Power";
        names[100] = "AttackCustom4Power"; names[101] = "AttackCustom5Power"; names[102] = "PowerAttackStop";
        // PlaceMine (103-113)
        for (var s = 0; s < 6; s++) names[103 + s] = "PlaceMine" + attackSuffixes[s];
        for (var s = 0; s < 6; s++) names[109 + s] = "PlaceMine2" + attackSuffixes[s]; // actually 108
        names[108] = "PlaceMine2"; names[109] = "PlaceMine2Up"; names[110] = "PlaceMine2Down";
        names[111] = "PlaceMine2IS"; names[112] = "PlaceMine2ISUp"; names[113] = "PlaceMine2ISDown";
        // AttackThrow 1-8 (114-167)
        string[] throwNames = ["AttackThrow", "AttackThrow2", "AttackThrow3", "AttackThrow4",
            "AttackThrow5", "Attack9", "AttackThrow6", "AttackThrow7", "AttackThrow8"];
        for (var t = 0; t < throwNames.Length; t++)
            for (var s = 0; s < 6; s++)
                names[114 + t * 6 + s] = throwNames[t] + attackSuffixes[s];
        names[168] = "CounterAttack"; names[169] = "Stomp";
        names[170] = "BlockIdle"; names[171] = "BlockHit"; names[172] = "Recoil";
        // Reload variants (173-199)
        names[173] = "ReloadWStart"; names[174] = "ReloadXStart"; names[175] = "ReloadYStart";
        names[176] = "ReloadZStart"; names[177] = "ReloadA";
        string[] reloadLetters = ["B","C","D","E","F","G","H","I","J","K","L","M","N","O","P","Q","R","S","W","X","Y","Z"];
        for (var r = 0; r < reloadLetters.Length; r++) names[178 + r] = "Reload" + reloadLetters[r];
        // Jam variants (200-222)
        string[] jamLetters = ["A","B","C","D","E","F","G","H","I","J","K","L","M","N","O","P","Q","R","S","W","X","Y","Z"];
        for (var j = 0; j < jamLetters.Length; j++) names[200 + j] = "Jam" + jamLetters[j];
        names[223] = "Stagger"; names[224] = "Death"; names[225] = "Talking"; names[226] = "PipBoy";
        names[227] = "JumpStart"; names[228] = "JumpLoop"; names[229] = "JumpLand";
        names[230] = "HandGrip1"; names[231] = "HandGrip2"; names[232] = "HandGrip3";
        names[233] = "HandGrip4"; names[234] = "HandGrip5"; names[235] = "HandGrip6";
        names[236] = "JumpLoopForward"; names[237] = "JumpLoopBackward";
        names[238] = "JumpLoopLeft"; names[239] = "JumpLoopRight"; names[240] = "PipBoyChild";
        names[241] = "JumpLandForward"; names[242] = "JumpLandBackward";
        names[243] = "JumpLandLeft"; names[244] = "JumpLandRight"; names[245] = "Count";
        return names;
    }
}

/// <summary>
///     An animation group discovered in a memory dump via RTTI-based scanning.
/// </summary>
public record DiscoveredAnimation
{
    /// <summary>File offset of the TESAnimGroup struct in the dump.</summary>
    public long FileOffset { get; init; }

    /// <summary>Animation group type enum value (0-255).</summary>
    public int GroupType { get; init; }

    /// <summary>Human-readable animation group type name.</summary>
    public required string GroupTypeName { get; init; }

    /// <summary>Animation name string from pParentName pointer, if resolvable.</summary>
    public string? Name { get; init; }

    /// <summary>Number of keyframes in this animation.</summary>
    public int FrameCount { get; init; }

    /// <summary>Number of associated sound events.</summary>
    public int SoundCount { get; init; }
}
