namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;

/// <summary>
///     Asset categories for classification.
/// </summary>
public enum AssetCategory
{
    Model, // .nif files
    Texture, // .dds, .ddx files
    Sound, // .wav, .lip, .ogg files
    Script, // .psc, .pex files
    Animation, // .kf, .hkx files
    Other // Unclassified
}
