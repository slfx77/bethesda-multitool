namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     RefID type encoded in the top 2 bits.
/// </summary>
public enum SaveRefIdType : byte
{
    /// <summary>Look up in the FormID array at the end of the save.</summary>
    FormIdArray = 0,

    /// <summary>Default/base game FormID (load order index 0x00).</summary>
    Default = 1,

    /// <summary>Runtime-created form (0xFF prefix).</summary>
    Created = 2,

    /// <summary>Unknown/undefined.</summary>
    Unknown = 3
}
