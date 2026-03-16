using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal sealed record NpcEgtBlendInvestigationPolicyDetail(
    NpcEgtBlendInvestigationPolicyResult Result,
    DecodedTexture GeneratedEgtTexture,
    DecodedTexture DiffEgtTexture,
    DecodedTexture AppliedDiffuseTexture);