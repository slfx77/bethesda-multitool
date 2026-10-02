namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One BSDismemberSkinInstance BodyPartList entry (nif.xml:7724-7731), exactly as stored and already in host order:
///     the BSPartFlag bits (read little-endian in every file, decoder quirk 1) and the BSDismemberBodyPartType value.
///     Entry k describes skin partition k.
/// </summary>
/// <param name="PartFlag">The Part Flag bits (bit 0 PF_EDITOR_VISIBLE, bit 8 PF_START_NET_BONESET).</param>
/// <param name="BodyPart">The Body Part value, uninterpreted.</param>
internal readonly record struct NifSkinBodyPart(ushort PartFlag, ushort BodyPart);
