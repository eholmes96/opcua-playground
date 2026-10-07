using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground.Tools;

/// <summary>
/// A stand-in ControlLogix chassis on the loopback interface, run with --simulate.
///
/// It answers RegisterSession and SendRRData, reports a 1756-EN2T bridge for List Identity and for
/// slot 4, a 1756-L83E controller for slot 0, and the wrong-slot error 0x01/0x0204 everywhere else.
/// That is enough to drive the whole red, amber, green sequence without a single packet leaving the
/// machine, which matters because the real thing on the other end would be plant equipment.
/// </summary>
public static partial class PlcSimulator
{
    private const int ControllerSlot = 0;
    private const int BridgeSlot = 4;

    /// <summary>Counters so a test can see the connection and request cost of one attempt.</summary>
    private static int _connections;
    private static int _routedReads;

    /// <summary>Set by --simulate empty: answers every slot with the wrong-slot error, which is how
    /// a plain adapter with no chassis behind it behaves. That is the walk's worst case.</summary>
    private static bool _emptyChassis;

    public static async Task RunAsync(bool emptyChassis = false, bool largeTagSet = false)
    {
        _emptyChassis = emptyChassis;
        LogixFixture.UseLargeSet = largeTagSet;
        var listener = new TcpListener(IPAddress.Loopback, Encapsulation.Port);
        listener.Start();
        Console.WriteLine($"simulated chassis listening on {IPAddress.Loopback}:{Encapsulation.Port}");
        if (_emptyChassis)
            Console.WriteLine("  empty chassis: every slot answers 0x01/0x0204");
        else
        {
            Console.WriteLine($"  slot {ControllerSlot}: 1756-L83E/B controller");
            Console.WriteLine($"  slot {BridgeSlot}: 1756-EN2T bridge");
            Console.WriteLine("  every other slot answers 0x01/0x0204, the wrong-slot error");
        }
        Console.WriteLine();

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            Console.WriteLine($"-> TCP connection #{Interlocked.Increment(ref _connections)}");
            _ = ServeAsync(client);
        }
    }

    private static async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                uint sessionHandle = 0;

                while (true)
                {
                    var header = new byte[Encapsulation.HeaderLength];
                    if (!await ReadExactAsync(stream, header).ConfigureAwait(false)) return;
                    if (!Encapsulation.TryParseHeader(header, out var parsed)) return;

                    var body = new byte[parsed.Length];
                    if (parsed.Length > 0 && !await ReadExactAsync(stream, body).ConfigureAwait(false)) return;

                    byte[]? reply = parsed.Command switch
                    {
                        Encapsulation.CommandRegisterSession =>
                            Encapsulation.BuildFrame(parsed.Command, sessionHandle = 0x00010001,
                                parsed.SenderContext, new byte[] { 0x01, 0x00, 0x00, 0x00 }),

                        Encapsulation.CommandListIdentity =>
                            BuildListIdentityReply(parsed.SenderContext),

                        Encapsulation.CommandSendRRData =>
                            BuildRoutedReply(sessionHandle, parsed.SenderContext, body),

                        Encapsulation.CommandUnRegisterSession => null,

                        _ => null,
                    };

                    if (parsed.Command == Encapsulation.CommandUnRegisterSession)
                    {
                        Console.WriteLine($"   session closed. totals: {_connections} connections, {_routedReads} routed reads");
                        return;
                    }
                    if (reply is not null) await stream.WriteAsync(reply).ConfigureAwait(false);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    // ===================== Replies =====================

    private static byte[] BuildListIdentityReply(ulong senderContext)
    {
        Console.WriteLine("  <- List Identity");
        var identity = Identity(DeviceTypeCommunicationsAdapter, "1756-EN2T/C", 0x00BB0044);

        var item = new byte[18 + identity.Length + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt16BigEndian(item.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(item.AsSpan(4), Encapsulation.Port);
        IPAddress.Loopback.GetAddressBytes().CopyTo(item.AsSpan(6));
        identity.CopyTo(item.AsSpan(18));
        item[^1] = 0x03;

        var data = new byte[6 + item.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 0x000C);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)item.Length);
        item.CopyTo(data.AsSpan(6));

        return Encapsulation.BuildFrame(Encapsulation.CommandListIdentity, 0, senderContext, data);
    }

    private static byte[] BuildRoutedReply(uint sessionHandle, ulong senderContext, byte[] body)
    {
        byte[] cipReply;

        // An unrouted Get_Attributes_All on class 0x06 is the Connection Manager counter read.
        if (Encapsulation.TryExtractUnconnectedData(body, out var direct)
            && direct.Length >= 6 && direct[0] == CipServices.GetAttributesAll && direct[3] == 0x06)
        {
            Console.WriteLine($"  <- connection manager counters (opens so far: {_connections})");
            return Encapsulation.BuildSendRRData(sessionHandle, senderContext, 2, Success(Counters()));
        }

        // Anything routed that is not an identity read belongs to the tag browser.
        if (Encapsulation.TryExtractUnconnectedData(body, out var wrapper)
            && TryGetEmbedded(wrapper, out var embedded)
            && embedded.Length > 0
            && embedded[0] != CipServices.GetAttributesAll)
        {
            return Encapsulation.BuildSendRRData(
                sessionHandle, senderContext, 2, BuildBrowseReply(embedded));
        }

        Interlocked.Increment(ref _routedReads);
        if (!Encapsulation.TryExtractUnconnectedData(body, out var request) || !TryReadSlot(request, out int slot))
        {
            Console.WriteLine("  <- unroutable request");
            cipReply = new byte[] { 0xD2, 0x00, CipStatus.PathSegmentError, 0x00 };
        }
        else if (slot == ControllerSlot && !_emptyChassis)
        {
            Console.WriteLine($"  <- slot {slot}: 1756-L83E/B");
            cipReply = Success(Identity(DeviceTypePlc, "1756-L83E/B", 0x00A1B2C3));
        }
        else if (slot == BridgeSlot && !_emptyChassis)
        {
            Console.WriteLine($"  <- slot {slot}: 1756-EN2T/C");
            cipReply = Success(Identity(DeviceTypeCommunicationsAdapter, "1756-EN2T/C", 0x00BB0044));
        }
        else
        {
            Console.WriteLine($"  <- slot {slot}: empty (0x01/0x0204)");
            cipReply = new byte[] { 0xD2, 0x00, CipStatus.ConnectionFailure, 0x01, 0x04, 0x02 };
        }

        return Encapsulation.BuildSendRRData(sessionHandle, senderContext, 2, cipReply);
    }

    /// <summary>Eight UINT counters in the documented order. Open requests tracks the connections
    /// this simulator has accepted, so a before-and-after comparison shows a real delta.</summary>
    private static byte[] Counters()
    {
        var data = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0), (ushort)_connections);   // open requests
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort)_connections);   // close requests
        return data;
    }

    private static byte[] Success(byte[] data) =>
        new byte[] { 0x81, 0x00, 0x00, 0x00 }.Concat(data).ToArray();

    // ===================== Request parsing =====================

    /// <summary>Walks an Unconnected_Send to its trailing route path and reads the backplane slot.</summary>
    private static bool TryReadSlot(ReadOnlySpan<byte> request, out int slot)
    {
        slot = -1;
        if (request.Length < 10 || request[0] != 0x52) return false;

        int cursor = 2 + request[1] * 2;                    // past the Connection Manager path
        cursor += 2;                                        // priority/tick and timeout ticks
        if (cursor + 2 > request.Length) return false;

        ushort embeddedSize = BinaryPrimitives.ReadUInt16LittleEndian(request[cursor..]);
        cursor += 2 + embeddedSize;
        if (embeddedSize % 2 == 1) cursor++;                // the alignment pad
        if (cursor + 2 > request.Length) return false;

        int routeWords = request[cursor];
        cursor += 2;                                        // route path size and the reserved byte
        if (routeWords < 1 || cursor + routeWords * 2 > request.Length) return false;

        var path = request.Slice(cursor, routeWords * 2);
        if (path[0] != CipHop.PortBackplane) return false;
        slot = path[1];
        return true;
    }

    // ===================== Fixtures =====================

    private const ushort DeviceTypePlc = CipIdentity.DeviceTypePlc;
    private const ushort DeviceTypeCommunicationsAdapter = CipIdentity.DeviceTypeCommunicationsAdapter;

    private static byte[] Identity(ushort deviceType, string name, uint serial)
    {
        var data = new byte[15 + name.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0), 1);          // Rockwell
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), deviceType);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 166);
        data[6] = 33;
        data[7] = 11;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 0x3160);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10), serial);
        data[14] = (byte)name.Length;
        Encoding.ASCII.GetBytes(name).CopyTo(data.AsSpan(15));
        return data;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
