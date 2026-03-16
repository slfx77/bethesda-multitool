using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal sealed record NpcEgtMorphIsolationDetail(
    string RegionName,
    int Rank,
    NpcEgtBasisContributionResult BasisContribution,
    DecodedTexture RawBasisTexture,
    DecodedTexture RawBasisCrop,
    DecodedTexture FloatContributionTexture,
    DecodedTexture FloatContributionCrop,
    DecodedTexture ActualContributionTexture,
    DecodedTexture ActualContributionCrop,
    DecodedTexture UnitBasisTexture,
    DecodedTexture UnitBasisCrop);
