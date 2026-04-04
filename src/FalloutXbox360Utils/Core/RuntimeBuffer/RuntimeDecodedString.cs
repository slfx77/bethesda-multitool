using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

internal sealed record RuntimeDecodedString(
    string Text,
    long FileOffset,
    long? VirtualAddress,
    int Length,
    StringCategory Category);
