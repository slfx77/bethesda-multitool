using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Collects a <see cref="NifMeshAnimation" /> from the modern NiControllerManager graph
///     (FO3/FNV/Skyrim ambient animated statics — NCR cloth flags, hanging signs): an idle-named
///     <c>NiControllerSequence</c>'s controlled blocks map node names to
///     <c>NiTransformInterpolator</c> → <c>NiTransformData</c>, read through the same
///     <see cref="NifKeyframeDataTrackReader" /> as the TES3 per-node graph — both eras compile onto
///     one track model, so nothing downstream distinguishes them.
///     <para>
///         AUTO-PLAY POLICY: only a sequence whose name contains "idle" plays — the manager
///         vocabulary for ACTIVATION-driven sequences ("Open"/"Close"/"Forward"/"Backward" on doors,
///         gates, traps) must stay parked at rest, not loop on every placed instance. A
///         <c>BSXFlags</c> block without the Animated bit (0x1) vetoes playback entirely (that bit
///         is what tells the engine to instantiate the controller manager at all). The clip window
///         comes from the sequence itself (start/stop/cycle/frequency — authored data, unlike the
///         TES3 side's inferred full key range).
///     </para>
///     <para>
///         SCOPE: the 20.2.0.7 Bethesda stream's controlled-block form (string-table indices,
///         29-byte stride — verified byte-level on nv_ncr_flag_s.nif). Oblivion's 20.0.0.x form
///         resolves names through a NiStringPalette with per-block offsets (different stride) — a
///         follow-up gate in this file when an Oblivion ambient asset needs it.
///     </para>
/// </summary>
internal static class NifControllerSequenceTrackCollector
{
    // Controlled block (20.2.0.7, BS stream): interpolator ref @0, controller ref @4,
    // priority byte @8, node-name string index @9, property-type @13, controller-type @17,
    // controller-id @21, interpolator-id @25.
    private const int ControlledBlockStride = 29;
    private const int NodeNameFieldOffset = 9;
    private const int MaxControlledBlocks = 512;

    // Sequence tail after the controlled-block table: weight @0, text-keys ref @4, cycle type @8,
    // frequency @12, start @16, stop @20, manager ptr @24, accum-root name @28.
    private const int SequenceTailSize = 32;

    // NiTransformInterpolator: static pose (Vec3 + Quat + float = 32 bytes), then Data ref.
    private const int InterpolatorDataRefOffset = 32;

    internal static NifMeshAnimation? Collect(byte[] data, NifInfo nif)
    {
        if (nif.BinaryVersion != NifVersions.Gamebryo202007 || nif.BsVersion == 0)
        {
            return null; // only the verified controlled-block form (see class docs)
        }

        var be = nif.IsBigEndian;
        if (ReadBsxFlags(data, nif, be) is { } bsxFlags && (bsxFlags & 0x1) == 0)
        {
            return null; // BSX present but not Animated — the engine never runs this manager
        }

        var sequence = SelectIdleSequence(data, nif, be);
        if (sequence is null)
        {
            return null;
        }

        var pos = sequence.DataOffset;
        var end = sequence.DataOffset + sequence.Size;
        pos += 4; // Name (already matched by the selector)

        if (pos + 8 > end)
        {
            return null;
        }

        var numBlocks = BinaryUtils.ReadInt32(data, pos, be);
        pos += 8; // Num Controlled Blocks + Array Grow By
        if (numBlocks is <= 0 or > MaxControlledBlocks)
        {
            return null;
        }

        var tail = pos + numBlocks * ControlledBlockStride;
        if (tail + SequenceTailSize > end)
        {
            return null; // stride mismatch for this stream flavor — bail rather than misread
        }

        var textKeysRef = BinaryUtils.ReadInt32(data, tail + 4, be);
        var cycleType = (CycleType)BinaryUtils.ReadInt32(data, tail + 8, be);
        var frequency = BinaryUtils.ReadFloat(data, tail + 12, be);
        var startTime = BinaryUtils.ReadFloat(data, tail + 16, be);
        var stopTime = BinaryUtils.ReadFloat(data, tail + 20, be);
        var accumRootIndex = BinaryUtils.ReadInt32(data, tail + 28, be);
        if (!float.IsFinite(startTime) || !float.IsFinite(stopTime) || stopTime <= startTime)
        {
            return null;
        }

        // Scene graph maps: parentage for the rig builder + skin bones; also the node-name
        // fallback set when the NIF ships no object palette.
        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        var shapePropertyMap = new Dictionary<int, List<int>>();
        var shapeSkinInstanceMap = new Dictionary<int, int>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap, shapePropertyMap,
            shapeSkinInstanceMap);

        // Node-name resolution: the manager's NiDefaultAVObjectPalette is authoritative — exporters
        // disambiguate duplicate node names there ("MTail2@#0"), which a plain name scan can't
        // resolve. Fall back to scanning the walker's node set for palette-less NIFs.
        var nodeByName = ReadObjectPalette(data, nif, be) ?? BuildNodeNameMap(data, nif, nodeChildren);

        // The accum root's motion is ACCUMULATED by the engine (root motion), not applied to the
        // node — playing it as a plain track would drift the whole mesh. Same removal set as the
        // NPC pose path (NifAnimationParser.RemoveAccumRootOverrides).
        var accumRoot = accumRootIndex >= 0 && accumRootIndex < nif.Strings.Count
            ? nif.Strings[accumRootIndex]
            : null;

        var tracksByNode = new Dictionary<int, NifNodeTrack>();
        for (var i = 0; i < numBlocks; i++)
        {
            var blockStart = pos + i * ControlledBlockStride;
            var interpolatorRef = BinaryUtils.ReadInt32(data, blockStart, be);
            var nodeNameIndex = BinaryUtils.ReadInt32(data, blockStart + NodeNameFieldOffset, be);
            if (interpolatorRef < 0 || interpolatorRef >= nif.Blocks.Count ||
                nodeNameIndex < 0 || nodeNameIndex >= nif.Strings.Count)
            {
                continue;
            }

            var nodeName = nif.Strings[nodeNameIndex];
            if (accumRoot is not null &&
                (string.Equals(nodeName, accumRoot, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(nodeName, accumRoot + " NonAccum", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!nodeByName.TryGetValue(nodeName, out var nodeBlock) ||
                !nodeChildren.ContainsKey(nodeBlock))
            {
                continue;
            }

            var interpolator = nif.Blocks[interpolatorRef];
            if (interpolator.TypeName != "NiTransformInterpolator" ||
                interpolator.Size < InterpolatorDataRefOffset + 4)
            {
                continue; // blend/B-spline interpolators unsupported — node keeps its rest pose
            }

            var dataRef = BinaryUtils.ReadInt32(
                data, interpolator.DataOffset + InterpolatorDataRefOffset, be);
            var track = NifKeyframeDataTrackReader.TryReadTrack(
                data, nif, dataRef, nodeName, frequency, 0f);
            if (track is { HasMotion: true })
            {
                tracksByNode[nodeBlock] = track;
            }
        }

        if (NifAnimationRigBuilder.Build(data, nif, nodeChildren, shapeSkinInstanceMap, tracksByNode)
            is not var (bones, tracks))
        {
            return null;
        }

        var textKeys = textKeysRef >= 0 && textKeysRef < nif.Blocks.Count &&
                       nif.Blocks[textKeysRef].TypeName == "NiTextKeyExtraData"
            ? NifTextKeyReader.Read(data, nif, nif.Blocks[textKeysRef])
            : [];

        // CYCLE_REVERSE (ping-pong) plays as a plain loop — labeled stand-in; no ambient asset
        // sighted with it yet. CYCLE_CLAMP holds the final pose (MapTime clamps at stop).
        return new NifMeshAnimation(
            bones, tracks, textKeys, startTime, stopTime, cycleType != CycleType.Clamp);
    }

    /// <summary>The BSXFlags value, or null when the NIF has no BSXFlags block (TES3/plain Gamebryo).</summary>
    private static uint? ReadBsxFlags(byte[] data, NifInfo nif, bool be)
    {
        foreach (var block in nif.Blocks)
        {
            if (block.TypeName == "BSXFlags" && block.Size >= 8)
            {
                return BinaryUtils.ReadUInt32(data, block.DataOffset + 4, be); // name(4) + flags
            }
        }

        return null;
    }

    /// <summary>
    ///     The idle-named sequence, or null — deliberately NO first-sequence fallback (unlike
    ///     the NPC pose selector): a manager whose only sequences are activation clips must stay static.
    /// </summary>
    private static BlockInfo? SelectIdleSequence(byte[] data, NifInfo nif, bool be)
    {
        foreach (var block in nif.Blocks)
        {
            if (block.TypeName != "NiControllerSequence" || block.Size < 4)
            {
                continue;
            }

            var nameIndex = BinaryUtils.ReadInt32(data, block.DataOffset, be);
            if (nameIndex >= 0 && nameIndex < nif.Strings.Count &&
                nif.Strings[nameIndex].Contains("idle", StringComparison.OrdinalIgnoreCase))
            {
                return block;
            }
        }

        return null;
    }

    /// <summary>
    ///     NiDefaultAVObjectPalette: scene ref + count + (SizedString name, object ref) pairs.
    ///     Null when absent or unreadable (caller falls back to a node-name scan).
    /// </summary>
    private static Dictionary<string, int>? ReadObjectPalette(byte[] data, NifInfo nif, bool be)
    {
        foreach (var block in nif.Blocks)
        {
            if (block.TypeName != "NiDefaultAVObjectPalette")
            {
                continue;
            }

            var pos = block.DataOffset + 4; // Scene ref
            var end = block.DataOffset + block.Size;
            if (pos + 4 > end)
            {
                return null;
            }

            var count = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            if (count > MaxControlledBlocks * 4)
            {
                return null;
            }

            var map = new Dictionary<string, int>((int)count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                var name = NifBinaryCursor.ReadSizedString(data, ref pos, end, be);
                if (name is null || pos + 4 > end)
                {
                    return null; // desync — the fallback scan is safer than a half-read palette
                }

                var objectRef = BinaryUtils.ReadInt32(data, pos, be);
                pos += 4;
                if (objectRef >= 0 && objectRef < nif.Blocks.Count)
                {
                    map.TryAdd(name, objectRef);
                }
            }

            return map;
        }

        return null;
    }

    private static Dictionary<string, int> BuildNodeNameMap(
        byte[] data, NifInfo nif, Dictionary<int, List<int>> nodeChildren)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var nodeIndex in nodeChildren.Keys)
        {
            if (NifBlockParsers.ReadBlockName(data, nif.Blocks[nodeIndex], nif) is { Length: > 0 } name)
            {
                map.TryAdd(name, nodeIndex);
            }
        }

        return map;
    }

    private enum CycleType
    {
        Loop = 0,
        Reverse = 1,
        Clamp = 2
    }
}
