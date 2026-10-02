using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.RuntimeSession;

public enum RuntimeRequestKind : ushort
{
    Hello = 1, Start = 2, Execute = 3, Stop = 4, Cancel = 5, Ping = 6, Evaluate = 7,
    MessageProbe = 8, ConditionProbe = 9, QuestRead = 10, QuestSet = 11, ActorSnapshot = 12,
    MessageMenu = 13, ActorSet = 14, ReferenceSnapshot = 15, ReferenceAction = 16,
    Inventory = 17, QuestState = 18, RecordMembership = 19, GamepadPulse = 20, GuestRunBind = 21,
    OwnerConditions = 22, Live = 30, LiveStop = 31, Event = 256
}

public sealed record RuntimeFrame(RuntimeRequestKind Kind, ulong RequestId, string Payload);

/// <summary>Local bridge wire protocol. All integers are little endian; payload is strict UTF-8.</summary>
public static class RuntimeProtocol
{
    public const uint Magic = 0x31544D42; // BMT1
    public const ushort Version = 1;
    public const int HeaderLength = 24;
    public const int MaximumPayloadBytes = 65536;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task WriteAsync(Stream stream, RuntimeFrame frame, CancellationToken token = default)
    {
        var payload = Utf8.GetBytes(frame.Payload);
        if (payload.Length > MaximumPayloadBytes) throw new InvalidDataException("Runtime frame exceeds 64 KiB.");
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)frame.Kind);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), frame.RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)payload.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    public static async Task<RuntimeFrame?> ReadAsync(Stream stream, CancellationToken token = default)
    {
        var header = new byte[HeaderLength];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), token);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != Version ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)) != 0)
            throw new InvalidDataException("Unsupported runtime protocol header.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        if (length > MaximumPayloadBytes) throw new InvalidDataException("Runtime frame exceeds 64 KiB.");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, token);
        return new RuntimeFrame((RuntimeRequestKind)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)),
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8)), Utf8.GetString(bytes));
    }
}
