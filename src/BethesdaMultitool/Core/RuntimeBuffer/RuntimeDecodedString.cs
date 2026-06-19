using BethesdaMultitool.Core.Strings;

namespace BethesdaMultitool.Core.RuntimeBuffer;

internal sealed record RuntimeDecodedString(
    string Text,
    long FileOffset,
    long? VirtualAddress,
    int Length,
    StringCategory Category);
