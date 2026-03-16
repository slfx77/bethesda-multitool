using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal sealed record NpcEgtBlendInvestigationDetail(
    NpcEgtBlendInvestigationResult Result,
    DecodedTexture? ShippedTexture,
    NpcEgtBlendInvestigationPolicyDetail? CurrentPolicy,
    NpcEgtBlendInvestigationPolicyDetail? RecoveredPolicy,
    DecodedTexture? CurrentVsRecoveredAppliedDiffuseDiffTexture);