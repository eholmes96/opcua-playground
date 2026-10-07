using System.Buffers.Binary;
using System.Text;
using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground.Tools;

/// <summary>
/// The value-reading half of the simulator: Read Tag by name, tag descriptions, and the Multiple
/// Service Packet that carries a batch of both.
///
/// Values move on every read so the table visibly updates, and descriptions are deliberately
/// refused for some tags, because a controller without stored documentation is the common case and
/// the app has to handle it without showing an error.
/// </summary>
public static partial class PlcSimulator
{
    private static int _reads;

    /// <summary>Unpacks a batch, answers each request, and packs the replies back in order.</summary>
    internal static byte[] BuildBatchReply(ReadOnlySpan<byte> request)
    {
        if (!TryUnpackBatch(request, out var parts))
            return Error(CipServices.MultipleServicePacket, CipStatus.PathSegmentError);

        var replies = parts.Select(p => (byte[])BuildBrowseReply(p)).ToList();

        int table = 2 + replies.Count * 2;
        var body = new byte[table + replies.Sum(r => r.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), (ushort)replies.Count);

        int offset = table;
        int cursor = table;
        for (int i = 0; i < replies.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2 + i * 2), (ushort)offset);
            replies[i].CopyTo(body, cursor);
            cursor += replies[i].Length;
            offset += replies[i].Length;
        }

        Console.WriteLine($"  <- batch of {replies.Count} ({_reads} reads so far)");
        return Reply(CipServices.MultipleServicePacket, CipStatus.Success, body);
    }

    private static bool TryUnpackBatch(ReadOnlySpan<byte> request, out List<byte[]> parts)
    {
        parts = new List<byte[]>();
        if (request.Length < 2) return false;

        int body = 2 + request[1] * 2;
        if (body + 2 > request.Length) return false;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(request[body..]);
        if (count <= 0 || body + 2 + count * 2 > request.Length) return false;

        for (int i = 0; i < count; i++)
        {
            int start = body + BinaryPrimitives.ReadUInt16LittleEndian(request[(body + 2 + i * 2)..]);
            int end = i + 1 < count
                ? body + BinaryPrimitives.ReadUInt16LittleEndian(request[(body + 2 + (i + 1) * 2)..])
                : request.Length;
            if (start < body || end > request.Length || start >= end) return false;
            parts.Add(request[start..end].ToArray());
        }

        return true;
    }

    /// <summary>Answers a Read Tag aimed at a name rather than a class.</summary>
    internal static byte[] BuildTagReadReply(ReadOnlySpan<byte> request)
    {
        if (!TryReadSymbols(request, out var symbols, out var elements) || symbols.Count == 0)
            return Error(CipServices.ReadTag, CipStatus.PathSegmentError);

        bool wantsDescription = symbols[^1] == LogixTagPath.DescriptionProperty;
        if (wantsDescription) symbols.RemoveAt(symbols.Count - 1);

        string name = string.Join('.', symbols);
        string leaf = symbols[^1];

        if (wantsDescription) return DescriptionReply(name, leaf);

        _reads++;
        if (leaf == "Grid") return GridReply(elements);
        return ValueReply(leaf);
    }

    /// <summary>
    /// Grid[i,j] reads i * 100 + j. A read of the whole array with no element segment gets element
    /// zero, as a controller would give it, which is exactly the answer a lost index produces.
    /// </summary>
    private static byte[] GridReply(List<uint> elements)
    {
        uint row = elements.Count > 0 ? elements[0] : 0;
        uint column = elements.Count > 1 ? elements[1] : 0;
        if (row >= 3 || column >= 4) return Error(CipServices.ReadTag, CipStatus.PathDestinationUnknown);

        var dint = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(dint.AsSpan(0), 0x00C4);
        BinaryPrimitives.WriteInt32LittleEndian(dint.AsSpan(2), (int)(row * 100 + column));
        return Reply(CipServices.ReadTag, CipStatus.Success, dint);
    }

    /// <summary>
    /// Some tags answer with documentation and some refuse, which is what a mixed or undocumented
    /// project looks like. The app must leave the column empty rather than showing the refusal.
    /// </summary>
    private static byte[] DescriptionReply(string name, string leaf)
    {
        if (leaf.StartsWith("Buffer", StringComparison.Ordinal) || leaf == "Step")
            return Error(CipServices.ReadTag, CipStatus.PathDestinationUnknown);

        var text = Encoding.ASCII.GetBytes($"Description of {name}");
        var body = new byte[4 + text.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), 0x00DA);      // SHORT_STRING
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), (ushort)text.Length);
        text.CopyTo(body, 4);
        return Reply(CipServices.ReadTag, CipStatus.Success, body);
    }

    /// <summary>
    /// Types are inferred from the name, matching how the fixture declares them, and values drift so
    /// the table can be seen updating rather than looking frozen.
    /// </summary>
    private static byte[] ValueReply(string leaf)
    {
        int tick = _reads;

        if (leaf.Contains("Level", StringComparison.Ordinal)
            || leaf.StartsWith("Readings", StringComparison.Ordinal)
            || leaf is "SetPoint" or "Temperature" or "Mean")
        {
            var body = new byte[6];
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), 0x00CA);   // REAL
            BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(2), 20f + tick % 100 / 10f);
            return Reply(CipServices.ReadTag, CipStatus.Success, body);
        }

        if (leaf is "Enabled" or "Running" or "DN" || leaf.StartsWith("Faults", StringComparison.Ordinal))
        {
            var body = new byte[] { 0xC1, 0x00, (byte)(tick % 2) };            // BOOL
            return Reply(CipServices.ReadTag, CipStatus.Success, body);
        }

        var dint = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(dint.AsSpan(0), 0x00C4);      // DINT
        BinaryPrimitives.WriteInt32LittleEndian(dint.AsSpan(2), tick);
        return Reply(CipServices.ReadTag, CipStatus.Success, dint);
    }

    /// <summary>Pulls the ANSI symbolic segments out of a request path, and the element indexes
    /// apart from them.</summary>
    private static bool TryReadSymbols(ReadOnlySpan<byte> request, out List<string> symbols, out List<uint> elements)
    {
        symbols = new List<string>();
        elements = new List<uint>();
        if (request.Length < 2) return false;

        int end = 2 + request[1] * 2;
        if (end > request.Length) return false;

        int cursor = 2;
        while (cursor < end)
        {
            switch (request[cursor])
            {
                case 0x91:
                    if (cursor + 2 > end) return false;
                    int length = request[cursor + 1];
                    if (cursor + 2 + length > end) return false;
                    symbols.Add(Encoding.ASCII.GetString(request.Slice(cursor + 2, length)));
                    cursor += 2 + length + (length % 2);
                    break;

                case 0x28:
                    if (cursor + 2 > end) return false;
                    elements.Add(request[cursor + 1]);
                    cursor += 2;
                    break;
                case 0x29:
                    if (cursor + 4 > end) return false;
                    elements.Add(BinaryPrimitives.ReadUInt16LittleEndian(request[(cursor + 2)..]));
                    cursor += 4;
                    break;
                case 0x2A:
                    if (cursor + 6 > end) return false;
                    elements.Add(BinaryPrimitives.ReadUInt32LittleEndian(request[(cursor + 2)..]));
                    cursor += 6;
                    break;

                default: return false;
            }
        }

        return symbols.Count > 0;
    }
}
