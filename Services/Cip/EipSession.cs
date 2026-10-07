using System.IO;
using System.Net;
using System.Net.Sockets;

namespace OpcUaPlayground.Services.Cip;

/// <summary>
/// One TCP session to an EtherNet/IP device on port 44818: connect, RegisterSession, exchange
/// requests, UnRegisterSession. Each request carries a fresh sender context, which the device
/// echoes, so a late reply from an earlier request is discarded rather than mistaken for the
/// answer to the current one.
/// </summary>
public sealed class EipSession : IAsyncDisposable
{
    private readonly Socket _socket;
    private uint _sessionHandle;
    private ulong _nextContext = 1;

    private EipSession(Socket socket) => _socket = socket;

    public uint SessionHandle => _sessionHandle;

    /// <summary>
    /// True once a request failed to complete. A cancelled or timed-out read can leave a partial
    /// frame in the stream, and a reply may still arrive afterwards, so nothing further can be
    /// trusted on this connection. Callers reusing one session across many requests must check it.
    /// </summary>
    public bool IsFaulted { get; private set; }

    /// <summary>
    /// Connects and registers a session. <paramref name="sourceAddress"/> binds the socket to the
    /// adapter the user picked, so on a machine with several NICs the traffic leaves the one they
    /// chose rather than whichever holds the default route.
    /// </summary>
    public static async Task<EipSession> ConnectAsync(
        IPAddress address, IPAddress? sourceAddress, TimeSpan timeout, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var session = new EipSession(socket);
        try
        {
            // Bind to the adapter the user picked so traffic leaves that NIC rather than
            // whichever holds the default route. Loopback is exempt: a bound source on a real
            // adapter cannot reach 127.0.0.1, and that is where the simulator lives.
            if (sourceAddress is not null && !IPAddress.IsLoopback(address))
                socket.Bind(new IPEndPoint(sourceAddress, 0));
            socket.NoDelay = true;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await socket.ConnectAsync(new IPEndPoint(address, Encapsulation.Port), cts.Token).ConfigureAwait(false);

            var reply = await session.ExchangeAsync(
                context => Encapsulation.BuildRegisterSession(context), timeout, cts.Token).ConfigureAwait(false);
            if (reply.Header.Command != Encapsulation.CommandRegisterSession || reply.Header.Status != 0)
                throw new IOException($"the device refused a session (status 0x{reply.Header.Status:X8})");

            session._sessionHandle = reply.Header.SessionHandle;
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>List Identity over the open session. This identifies only the device at the far end;
    /// being an encapsulation service rather than a CIP service, it cannot be routed to a slot.</summary>
    public async Task<CipIdentity?> ReadDeviceIdentityAsync(TimeSpan timeout, CancellationToken ct)
    {
        var reply = await ExchangeAsync(Encapsulation.BuildListIdentity, timeout, ct).ConfigureAwait(false);
        return CipIdentity.TryParseListIdentity(reply.Frame, out var identity, out _) ? identity : null;
    }

    /// <summary>
    /// Sends a CIP request and returns the parsed reply, or null if the reply was malformed.
    ///
    /// This is the only method in the app that puts a CIP request on a socket, which makes it the
    /// one place a read-only guarantee can be enforced. Every request is checked here before it is
    /// sent, so nothing reaches a controller without passing the allowlist.
    /// </summary>
    public async Task<CipResponse?> SendRRDataAsync(byte[] cipRequest, TimeSpan timeout, CancellationToken ct)
    {
        CipServices.AssertReadOnly(cipRequest);

        var reply = await ExchangeAsync(
            context => Encapsulation.BuildSendRRData(_sessionHandle, context, (ushort)timeout.TotalSeconds, cipRequest),
            timeout, ct).ConfigureAwait(false);

        return reply.Header.Status == 0 ? ParseRRDataReply(reply.Frame) : null;
    }

    /// <summary>Split out of the async method above because a ReadOnlySpan cannot live across an await.</summary>
    private static CipResponse? ParseRRDataReply(byte[] frame)
    {
        var body = frame.AsSpan(Encapsulation.HeaderLength);
        if (!Encapsulation.TryExtractUnconnectedData(body, out var cipReply)) return null;
        return CipStatus.TryParseResponse(cipReply, out var response) ? response : null;
    }

    private readonly record struct Reply(Encapsulation.Header Header, byte[] Frame);

    private async Task<Reply> ExchangeAsync(Func<ulong, byte[]> build, TimeSpan timeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsFaulted, this);

        ulong context = _nextContext++;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await _socket.SendAsync(build(context), SocketFlags.None, cts.Token).ConfigureAwait(false);

            // Discard anything whose context does not match, so a reply to an earlier request is
            // skipped rather than mistaken for this one's answer.
            while (true)
            {
                var frame = await ReadFrameAsync(cts.Token).ConfigureAwait(false);
                if (!Encapsulation.TryParseHeader(frame, out var header))
                    throw new IOException("the device sent a frame too short to be an encapsulation header");
                if (header.SenderContext == context) return new Reply(header, frame);
            }
        }
        catch (Exception ex)
        {
            // The caller cancelling is orderly and leaves the session reusable in principle; anything
            // else means this exchange stopped mid-stream, so the session is finished.
            if (ex is not OperationCanceledException || !ct.IsCancellationRequested) IsFaulted = true;
            throw;
        }
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[Encapsulation.HeaderLength];
        await ReadExactAsync(header, ct).ConfigureAwait(false);
        if (!Encapsulation.TryParseHeader(header, out var parsed)) throw new IOException("malformed reply header");

        var frame = new byte[Encapsulation.HeaderLength + parsed.Length];
        header.CopyTo(frame, 0);
        if (parsed.Length > 0)
            await ReadExactAsync(frame.AsMemory(Encapsulation.HeaderLength), ct).ConfigureAwait(false);
        return frame;
    }

    private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await _socket.ReceiveAsync(buffer[read..], SocketFlags.None, ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("the device closed the connection");
            read += n;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessionHandle != 0 && !IsFaulted)
        {
            // Best effort: the session is torn down by closing the socket anyway.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                await _socket.SendAsync(
                    Encapsulation.BuildUnRegisterSession(_sessionHandle, _nextContext++),
                    SocketFlags.None, cts.Token).ConfigureAwait(false);
            }
            catch { }
            _sessionHandle = 0;
        }
        _socket.Dispose();
    }
}
