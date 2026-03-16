using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal sealed record NpcEgtRegionDiagnosticDetail(
    NpcEgtRegionDiagnosticResult Result,
    DecodedTexture? GeneratedTexture,
    DecodedTexture? ShippedTexture,
    DecodedTexture? DiffTexture,
    DecodedTexture? SignedBiasTexture,
    IReadOnlyList<NpcEgtRegionMetricDetail> RegionDetails,
    IReadOnlyDictionary<string, IReadOnlyList<NpcEgtBasisContributionResult>> BasisContributionsByRegion,
    IReadOnlyList<NpcEgtMorphIsolationDetail> MorphIsolationDetails,
    IReadOnlyList<NpcEgtTextureControlResult> TextureControls);
