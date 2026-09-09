using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Collects a <see cref="NifMeshAnimation" /> from the TES3-era per-node controller graph:
///     scene nodes carry <c>NiKeyframeController</c>/<c>BSKeyframeController</c> chains whose
///     <c>NiKeyframeData</c> holds the tracks (Morrowind banners: a 3-bone chain under "Root Bone").
///     The modern NiControllerManager/NiControllerSequence graph compiles onto the same model via
///     <see cref="NifControllerSequenceTrackCollector" /> (the 5-family seam); nothing downstream
///     distinguishes the eras. Bone-tree assembly is shared (<see cref="NifAnimationRigBuilder" />).
/// </summary>
internal static class NifNodeKeyframeTrackCollector
{
    internal static NifMeshAnimation? Collect(
        byte[] data,
        NifInfo nif,
        bool preserveFileRootTransformAndTrack = false)
    {
        var be = nif.IsBigEndian;

        // Scene graph: children lists give parentage; the walker's node set gives us node blocks.
        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        var shapePropertyMap = new Dictionary<int, List<int>>();
        var shapeSkinInstanceMap = new Dictionary<int, int>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap, shapePropertyMap,
            shapeSkinInstanceMap);

        // Tracks per node block index, from the controller chains.
        var tracksByNode = new Dictionary<int, NifNodeTrack>();
        var controllerHeadersByNode = new Dictionary<int, NifTimeControllerHeader>();
        foreach (var nodeIndex in nodeChildren.Keys)
        {
            var nodeName = NifBlockParsers.ReadBlockName(data, nif.Blocks[nodeIndex], nif);
            if (string.IsNullOrWhiteSpace(nodeName))
            {
                continue;
            }

            var controllerRef = NifBinaryCursor.ReadNiObjectNETControllerRef(
                data,
                nif.Blocks[nodeIndex].DataOffset,
                nif.Blocks[nodeIndex].DataOffset + nif.Blocks[nodeIndex].Size,
                be,
                nif.HasInlineStrings,
                nif.BinaryVersion);

            for (var hop = 0; hop < 8 && controllerRef >= 0 && controllerRef < nif.Blocks.Count; hop++)
            {
                var controllerBlock = nif.Blocks[controllerRef];
                if (!NifTimeControllerReader.TryRead(data, controllerBlock, be, out var header))
                {
                    break;
                }

                if (controllerBlock.TypeName is "NiKeyframeController" or "BSKeyframeController")
                {
                    var dataRef = NifKeyframeDataTrackReader.ReadControllerDataRef(data, controllerBlock, be);
                    var track = NifKeyframeDataTrackReader.TryReadTrack(
                        data, nif, dataRef, nodeName, header.Frequency, header.Phase);
                    if (track is { HasAnyKeys: true })
                    {
                        tracksByNode[nodeIndex] = track;
                        controllerHeadersByNode[nodeIndex] = header;
                        break;
                    }
                }

                controllerRef = header.NextControllerRef;
            }
        }

        if (!tracksByNode.Values.Any(static track => track.HasMotion))
        {
            return null;
        }

        if (NifAnimationRigBuilder.Build(
                data,
                nif,
                nodeChildren,
                shapeSkinInstanceMap,
                tracksByNode,
                preserveFileRootTransformAndTrack)
            is not var (bones, tracks))
        {
            return null;
        }

        var textKeys = NifTextKeyReader.ReadFirst(data, nif);
        var clip = NifAnimationClipSelector.SelectClip(textKeys, tracks);
        if (clip is not { } window)
        {
            return null; // degenerate/absent play window → treat as static
        }

        var fullControllerCycle = ResolveCompatibleReverseCycle(
            tracksByNode,
            controllerHeadersByNode,
            bones);
        return new NifMeshAnimation(
            bones,
            tracks,
            textKeys,
            window.Start,
            window.Stop,
            window.Loops,
            fullControllerCycle);
    }

    /// <summary>
    ///     Retains a CYCLE_REVERSE lane when every reverse-moving track shares one exact clock.
    ///     Other active cycle modes remain outside the lane: TES3 can mix clamp body controllers
    ///     with an independent reverse attachment group in one NIF.
    /// </summary>
    internal static NifControllerCycle? ResolveCompatibleReverseCycle(
        IReadOnlyDictionary<int, NifNodeTrack> tracksByNode,
        IReadOnlyDictionary<int, NifTimeControllerHeader> controllerHeadersByNode,
        IReadOnlyList<NifAnimBone> bones)
    {
        NifTimeControllerHeader candidate = default;
        var candidateFrequency = 0f;
        var foundMovingController = false;
        var reverseNodeIndices = new List<int>();
        foreach (var (nodeIndex, track) in tracksByNode)
        {
            if (!track.HasMotion)
            {
                continue;
            }

            if (!controllerHeadersByNode.TryGetValue(nodeIndex, out var header) ||
                !header.IsActive ||
                !Enum.IsDefined(header.CycleType) ||
                !float.IsFinite(header.Frequency) ||
                !float.IsFinite(header.Phase) ||
                !float.IsFinite(header.StartTime) ||
                !float.IsFinite(header.StopTime) ||
                header.StopTime <= header.StartTime)
            {
                return null;
            }

            if (header.CycleType != NifCycleType.Reverse)
            {
                continue;
            }

            // NiKeyframeDataTrackReader owns the authored zero-frequency sentinel and normalizes
            // it to one. Persist the same effective clock so the viewer boundary can compare the
            // selected tracks without inventing a second interpretation.
#pragma warning disable S1244 // Authored controller clocks must be byte-exact to share one lane.
            var effectiveFrequency = header.Frequency == 0f ? 1f : header.Frequency;
            if (track.Frequency != effectiveFrequency || track.Phase != header.Phase)
            {
                return null;
            }

            if (!foundMovingController)
            {
                candidate = header;
                candidateFrequency = effectiveFrequency;
                foundMovingController = true;
            }
            else if (effectiveFrequency != candidateFrequency ||
                     header.Phase != candidate.Phase ||
                     header.StartTime != candidate.StartTime ||
                     header.StopTime != candidate.StopTime ||
                     header.CycleType != candidate.CycleType)
            {
                return null;
            }
#pragma warning restore S1244

            reverseNodeIndices.Add(nodeIndex);
        }

        if (!foundMovingController)
        {
            return null;
        }

        var boneSlotBySourceBlock = bones
            .Select(static (bone, index) => (bone.SourceBlockIndex, Index: index))
            .Where(static entry => entry.SourceBlockIndex >= 0)
            .ToDictionary(static entry => entry.SourceBlockIndex, static entry => entry.Index);
        var trackIndices = new int[reverseNodeIndices.Count];
        for (var index = 0; index < reverseNodeIndices.Count; index++)
        {
            if (!boneSlotBySourceBlock.TryGetValue(reverseNodeIndices[index], out trackIndices[index]))
            {
                return null;
            }
        }

        Array.Sort(trackIndices);
        return new NifControllerCycle(
            candidateFrequency,
            candidate.Phase,
            candidate.StartTime,
            candidate.StopTime,
            candidate.CycleType,
            trackIndices);
    }
}
