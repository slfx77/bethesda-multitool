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
    internal static NifMeshAnimation? Collect(byte[] data, NifInfo nif)
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
                        data, nif, dataRef, nodeName!, header.Frequency, header.Phase);
                    if (track is { HasMotion: true })
                    {
                        tracksByNode[nodeIndex] = track;
                        break;
                    }
                }

                controllerRef = header.NextControllerRef;
            }
        }

        if (NifAnimationRigBuilder.Build(data, nif, nodeChildren, shapeSkinInstanceMap, tracksByNode)
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

        return new NifMeshAnimation(bones, tracks, textKeys, window.Start, window.Stop, window.Loops);
    }
}
