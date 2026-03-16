using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal sealed record NpcEgtRegionMetricDetail(
    NpcEgtRegionMetricResult Result,
    DecodedTexture GeneratedCrop,
    DecodedTexture ShippedCrop,
    DecodedTexture DiffCrop,
    DecodedTexture SignedBiasCrop);