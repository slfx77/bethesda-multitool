using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Chooses the most relevant controller sequence for idle pose extraction.
/// </summary>
internal static class NifControllerSequenceSelector
{
    internal static BlockInfo? SelectIdleSequence(byte[] data, NifInfo nif, bool be)
    {
        BlockInfo? bestSequence = null;
        BlockInfo? firstSequence = null;

        foreach (var block in nif.Blocks)
        {
            if (block.TypeName != "NiControllerSequence")
            {
                continue;
            }

            firstSequence ??= block;
            if (block.Size < 4)
            {
                continue;
            }

            var nameIndex = BinaryUtils.ReadInt32(data, block.DataOffset, be);
            if (nameIndex < 0 || nameIndex >= nif.Strings.Count)
            {
                continue;
            }

            if (!nif.Strings[nameIndex].Contains(
                    "idle",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bestSequence = block;
            break;
        }

        return bestSequence ?? firstSequence;
    }
}
