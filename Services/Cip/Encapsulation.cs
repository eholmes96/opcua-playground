using System.Buffers.Binary;

namespace OpcUaPlayground.Services.Cip;

/// <summary>
/// The EtherNet/IP encapsulation layer: a fixed 24-byte little-endian header in front of every
/// request and reply on TCP port 44818, plus the Common Packet Format wrapper that carries a CIP
/// request inside a SendRRData.
///
/// Header layout: command (UINT), length of the data that follows (UINT), session handle (UDINT),
/// status (UDINT), sender context (8 bytes the device echoes back verbatim), options (UDINT).
/// </summary>
public static class Encapsulation
{
    public const int Port = 44818;
    public const int HeaderLength = 24;

    public const ushort CommandListIdentity = 0x0063;
    public const ushort CommandRegisterSession = 0x0065;
    public const ushort CommandUnRegisterSession = 0x0066;
    public const ushort CommandSendRRData = 0x006F;

    private const ushort CpfItemNullAddress = 0x0000;
    private const ushort CpfItemUnconnectedData = 0x00B2;

    public static byte[] BuildFrame(ushort command, uint sessionHandle, ulong senderContext, ReadOnlySpan<byte> data)
    {
        var frame = new byte[HeaderLength + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0), command);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), checked((ushort)data.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), sessionHandle);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(12), senderContext);
        data.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    /// <summary>Protocol version 1, no option flags. The session handle comes back in the reply header.</summary>
    public static byte[] BuildRegisterSession(ulong senderContext) =>
        BuildFrame(CommandRegisterSession, 0, senderContext, new byte[] { 0x01, 0x00, 0x00, 0x00 });

    public static byte[] BuildUnRegisterSession(uint sessionHandle, ulong senderContext) =>
        BuildFrame(CommandUnRegisterSession, sessionHandle, senderContext, ReadOnlySpan<byte>.Empty);

    /// <summary>A List Identity over the open TCP session, which identifies only the device at the far
    /// end. List Identity is an encapsulation service rather than a CIP service, so unlike the requests
    /// below it cannot be routed across a backplane.</summary>
    public static byte[] BuildListIdentity(ulong senderContext) =>
        BuildFrame(CommandListIdentity, 0, senderContext, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Wraps a CIP request for unconnected delivery: interface handle 0, a timeout in seconds, then a
    /// Common Packet Format list of a null address item and an unconnected data item holding the request.
    /// </summary>
    public static byte[] BuildSendRRData(uint sessionHandle, ulong senderContext, ushort timeoutSeconds, ReadOnlySpan<byte> cipRequest)
    {
        var data = new byte[8 + 4 + 4 + cipRequest.Length];
        var span = data.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], 0);                 // interface handle: 0 = CIP
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], timeoutSeconds);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], 2);                 // CPF item count
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], CpfItemNullAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 0);                // null address item is empty
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], CpfItemUnconnectedData);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], checked((ushort)cipRequest.Length));
        cipRequest.CopyTo(span[16..]);
        return BuildFrame(CommandSendRRData, sessionHandle, senderContext, data);
    }

    public readonly record struct Header(ushort Command, ushort Length, uint SessionHandle, uint Status, ulong SenderContext);

    public static bool TryParseHeader(ReadOnlySpan<byte> frame, out Header header)
    {
        header = default;
        if (frame.Length < HeaderLength) return false;
        header = new Header(
            BinaryPrimitives.ReadUInt16LittleEndian(frame[0..]),
            BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(frame[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(frame[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(frame[12..]));
        return true;
    }

    /// <summary>Pulls the CIP reply out of a SendRRData response's unconnected data item.</summary>
    public static bool TryExtractUnconnectedData(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> cipReply)
    {
        cipReply = default;
        if (data.Length < 8) return false;
        ushort itemCount = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        int cursor = 8;
        for (int i = 0; i < itemCount; i++)
        {
            if (cursor + 4 > data.Length) return false;
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data[cursor..]);
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(data[(cursor + 2)..]);
            int start = cursor + 4;
            if (start + length > data.Length) return false;
            if (type == CpfItemUnconnectedData)
            {
                cipReply = data.Slice(start, length);
                return true;
            }
            cursor = start + length;
        }
        return false;
    }
}
