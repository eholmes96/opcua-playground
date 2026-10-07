using System.Buffers.Binary;

namespace OpcUaPlayground.Services.Cip;

/// <summary>
/// Builds the CIP requests this app sends. Everything that has to cross a backplane goes inside an
/// Unconnected_Send (service 0x52) addressed to the Connection Manager, because that is the object
/// that consumes the route path and forwards the embedded request one hop at a time.
/// </summary>
public static class UnconnectedSend
{
    private const byte ServiceUnconnectedSend = CipServices.UnconnectedSend;
    private const byte ServiceGetAttributesAll = CipServices.GetAttributesAll;

    private const byte ClassIdentity = 0x01;
    private const byte ClassConnectionManager = 0x06;

    // Tick time 2^8 = 256 ms, four ticks, so a hop gives up after about 1.02 seconds.
    //
    // This has to stay comfortably SHORTER than the socket timeout the caller uses. An empty slot
    // is not silence: the bridge waits this long, then answers 0x01/0x0204. If we gave up on the
    // socket first we would both lose that answer and leave a reply arriving on a stream the next
    // request is about to use, which is what makes reusing one session across the slot walk unsafe.
    private const byte PriorityTimeTick = 0x08;
    private const byte TimeoutTicks = 0x04;

    /// <summary>Get_Attributes_All against the Identity Object, which is what every chassis-mapping
    /// tool reads to find out what a module is.</summary>
    public static byte[] BuildGetIdentity() => new byte[]
    {
        ServiceGetAttributesAll,
        0x02,                       // request path size, in 16-bit words
        0x20, ClassIdentity,        // 8-bit class id segment
        0x24, 0x01,                 // 8-bit instance id segment
    };

    /// <summary>Wraps <paramref name="embeddedRequest"/> so it is delivered along <paramref name="routePath"/>.</summary>
    public static byte[] Build(ReadOnlySpan<byte> embeddedRequest, byte[] routePath)
    {
        bool pad = embeddedRequest.Length % 2 == 1;
        int length = 6                              // service, path size, and the Connection Manager path
                   + 2                              // priority/tick and timeout ticks
                   + 2                              // embedded message size
                   + embeddedRequest.Length
                   + (pad ? 1 : 0)
                   + 2                              // route path size and the reserved byte
                   + routePath.Length;

        var request = new byte[length];
        var span = request.AsSpan();
        span[0] = ServiceUnconnectedSend;
        span[1] = 0x02;                             // request path size, in words
        span[2] = 0x20; span[3] = ClassConnectionManager;
        span[4] = 0x24; span[5] = 0x01;
        span[6] = PriorityTimeTick;
        span[7] = TimeoutTicks;
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], checked((ushort)embeddedRequest.Length));
        embeddedRequest.CopyTo(span[10..]);

        int cursor = 10 + embeddedRequest.Length;
        if (pad) request[cursor++] = 0x00;          // keep the route path on a 16-bit boundary
        request[cursor++] = CipRoutePath.SizeInWords(routePath);
        request[cursor++] = 0x00;                   // reserved
        routePath.CopyTo(request.AsSpan(cursor));
        return request;
    }

    /// <summary>
    /// Get_Attributes_All against the Connection Manager itself, unrouted, which returns that
    /// module's cumulative connection counters. Sent to the device at the IP address rather than
    /// across the backplane, because it is that module's TCP and connection pool we consume.
    /// </summary>
    public static byte[] BuildGetConnectionManagerCounters() => new byte[]
    {
        ServiceGetAttributesAll,
        0x02,                            // request path size, in 16-bit words
        0x20, ClassConnectionManager,
        0x24, 0x01,
    };

    /// <summary>Read the Identity Object of whatever module sits in <paramref name="slot"/> of the
    /// chassis at the far end of the connection.</summary>
    public static byte[] BuildGetIdentityInSlot(int slot) =>
        Build(BuildGetIdentity(), CipRoutePath.ToSlot(slot));
}
