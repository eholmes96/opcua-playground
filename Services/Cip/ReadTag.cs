using System.Buffers.Binary;

namespace OpcUaPlayground.Services.Cip;

/// <summary>
/// Builds Read Tag requests, and packs several of them into one message.
///
/// Batching matters more than it looks. Fifty tags on a 250 ms scan is two hundred messages a
/// second if each goes on its own, and that traffic lands on a module already shared with HMIs and
/// historians. A Multiple Service Packet carries a dozen or more reads in a single request, which
/// is what Ignition and Kepware do and what keeps this app from being the noisiest thing on the
/// network.
/// </summary>
public static class ReadTag
{
    /// <summary>Message Router, the object that unpacks a Multiple Service Packet and runs each part.</summary>
    private const byte ClassMessageRouter = 0x02;

    /// <summary>One Read Tag request for a single element of <paramref name="tagName"/>.</summary>
    public static byte[] Build(string tagName, ushort elementCount = 1) =>
        Build(LogixTagPath.Encode(tagName), elementCount);

    /// <summary>The same request aimed at a tag's description rather than its value.</summary>
    public static byte[] BuildDescription(string tagName) =>
        Build(LogixTagPath.EncodeDescription(tagName), 1);

    private static byte[] Build(byte[] path, ushort elementCount)
    {
        var request = new byte[2 + path.Length + 2];
        request[0] = CipServices.ReadTag;
        request[1] = LogixTagPath.SizeInWords(path);
        path.CopyTo(request, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2 + path.Length), elementCount);
        return request;
    }

    /// <summary>
    /// Packs requests into one Multiple Service Packet. The layout is a count, an offset per
    /// embedded request measured from the start of the count field, then the requests themselves.
    /// </summary>
    public static byte[] Batch(IReadOnlyList<byte[]> requests)
    {
        if (requests.Count == 0) throw new ArgumentException("nothing to batch", nameof(requests));

        int header = 6;                                   // service, path size, and the router path
        int table = 2 + requests.Count * 2;               // the count and its offset table
        int payload = requests.Sum(r => r.Length);

        var message = new byte[header + table + payload];
        message[0] = CipServices.MultipleServicePacket;
        message[1] = 0x02;                                // path size in words
        message[2] = 0x20; message[3] = ClassMessageRouter;
        message[4] = 0x24; message[5] = 0x01;

        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(header), (ushort)requests.Count);

        int offset = table;                               // offsets are relative to the count field
        int cursor = header + table;
        for (int i = 0; i < requests.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(header + 2 + i * 2), (ushort)offset);
            requests[i].CopyTo(message, cursor);
            cursor += requests[i].Length;
            offset += requests[i].Length;
        }

        return message;
    }

    /// <summary>
    /// Splits a Multiple Service Packet reply back into one reply per request, in the order they
    /// were sent. Each is parsed on its own, so one tag failing does not spoil the rest of the batch.
    /// </summary>
    public static bool TryUnpack(ReadOnlySpan<byte> data, out List<CipResponse> replies)
    {
        replies = new List<CipResponse>();
        if (data.Length < 2) return false;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (count == 0 || 2 + count * 2 > data.Length) return false;

        for (int i = 0; i < count; i++)
        {
            int start = BinaryPrimitives.ReadUInt16LittleEndian(data[(2 + i * 2)..]);
            int end = i + 1 < count
                ? BinaryPrimitives.ReadUInt16LittleEndian(data[(2 + (i + 1) * 2)..])
                : data.Length;

            if (start < 0 || end > data.Length || start >= end) return false;
            if (!CipStatus.TryParseResponse(data[start..end], out var reply)) return false;
            replies.Add(reply);
        }

        return true;
    }

    /// <summary>
    /// What one member of a batch reply costs besides its value: the reply service, a reserved
    /// byte, the status and its size, the type code, and a structure handle when there is one.
    /// The handle is counted every time, since over-counting two bytes is cheaper than guessing.
    /// </summary>
    public const int ReplyOverheadBytes = 4 + 2 + 2;

    /// <summary>
    /// Splits requests into batches that fit one message both ways. Names vary in length and values
    /// vary in size, so this measures each rather than assuming.
    ///
    /// <paramref name="replySize"/> is the value an item is expected to bring back including
    /// <see cref="ReplyOverheadBytes"/>, or null when that is not known yet. An unknown item goes
    /// alone, since a reply that overflows is refused rather than shortened.
    /// </summary>
    public static List<List<T>> Chunk<T>(
        IReadOnlyList<T> items, Func<T, int> requestSize, Func<T, int?> replySize,
        int requestBudget, int replyBudget)
    {
        const int requestHeader = 8;                      // the packet's own header and count
        const int replyHeader = 6;                        // the reply's header and count

        var chunks = new List<List<T>>();
        var current = new List<T>();
        int sent = requestHeader;
        int returned = replyHeader;

        foreach (var item in items)
        {
            if (replySize(item) is not { } expected)
            {
                chunks.Add(new List<T> { item });
                continue;
            }

            int request = requestSize(item) + 2;          // plus its offset entry
            int reply = expected + 2;

            if (current.Count > 0 && (sent + request > requestBudget || returned + reply > replyBudget))
            {
                chunks.Add(current);
                current = new List<T>();
                sent = requestHeader;
                returned = replyHeader;
            }

            current.Add(item);
            sent += request;
            returned += reply;
        }

        if (current.Count > 0) chunks.Add(current);
        return chunks;
    }
}
