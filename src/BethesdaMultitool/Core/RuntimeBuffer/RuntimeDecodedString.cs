using BethesdaMultitool.Core.Strings;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>A string decoded from runtime memory, with its dump location, length, and inferred category.</summary>
internal sealed record RuntimeDecodedString(
    string Text,
    long FileOffset,
    long? VirtualAddress,
    int Length,
    StringCategory Category);
