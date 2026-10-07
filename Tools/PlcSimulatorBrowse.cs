using System.Buffers.Binary;
using System.Text;
using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground.Tools;

/// <summary>
/// The tag-browsing half of the simulator: Symbol Object listing and Template Object reads, served
/// from <see cref="LogixFixture"/>. Kept apart from the connection half so the identity and routing
/// behaviour stays easy to read.
/// </summary>
public static partial class PlcSimulator
{
    internal static byte[] BuildBrowseReply(ReadOnlySpan<byte> request)
    {
        byte service = request[0];
        if (!TryParsePath(request, out var path)) return Error(service, CipStatus.PathSegmentError);

        return (service, path.ClassId) switch
        {
            (CipServices.GetInstanceAttributeList, SymbolObject.ClassSymbol) => ListSymbols(path),
            (CipServices.GetAttributeList, TemplateObject.ClassTemplate) => TemplateHeader(path),
            (CipServices.ReadTag, TemplateObject.ClassTemplate) => TemplateBody(request, path),

            // A Read Tag with no class segment is addressed by name, and a Multiple Service Packet
            // is a batch of those.
            (CipServices.ReadTag, 0) => BuildTagReadReply(request),
            (CipServices.MultipleServicePacket, _) => BuildBatchReply(request),

            _ => Error(service, CipStatus.PathDestinationUnknown),
        };
    }

    private static byte[] ListSymbols(RequestPath path)
    {
        var all = path.SymbolicName is null ? LogixFixture.ControllerTags : LogixFixture.ProgramTags;
        var page = all.Where(t => t.Instance >= path.Instance).Take(LogixFixture.PageSize).ToList();
        if (page.Count == 0) return Reply(CipServices.GetInstanceAttributeList, CipStatus.Success, Array.Empty<byte>());

        bool more = all.Any(t => t.Instance > page[^1].Instance);

        var body = new List<byte>();
        foreach (var tag in page)
        {
            Append32(body, tag.Instance);

            var nameBytes = Encoding.ASCII.GetBytes(tag.Name);
            Append16(body, (ushort)nameBytes.Length);
            body.AddRange(nameBytes);

            Append16(body, tag.Type);
            foreach (uint dimension in tag.Dims) Append32(body, dimension);
        }

        string scope = path.SymbolicName ?? "controller";
        Console.WriteLine($"  <- {scope} symbols from instance {path.Instance}: {page.Count}"
                        + (more ? ", more follow" : ""));

        // Partial transfer is how a real controller says to ask again from the next instance.
        byte status = more ? CipStatus.PartialTransfer : CipStatus.Success;
        return Reply(CipServices.GetInstanceAttributeList, status, body.ToArray());
    }

    private static byte[] TemplateHeader(RequestPath path)
    {
        ushort id = (ushort)path.Instance;
        if (!LogixFixture.Knows(id)) return Error(CipServices.GetAttributeList, CipStatus.PathDestinationUnknown);

        Console.WriteLine($"  <- template {id} header");

        var body = new List<byte>();
        Append16(body, 4);
        AppendAttribute(body, 1, BitConverter.GetBytes((ushort)0x1234));
        AppendAttribute(body, 2, BitConverter.GetBytes(LogixFixture.MemberCount(id)));
        AppendAttribute(body, 4, BitConverter.GetBytes(LogixFixture.ObjectDefinitionSize(id)));
        AppendAttribute(body, 5, BitConverter.GetBytes(LogixFixture.StructureSize(id)));
        return Reply(CipServices.GetAttributeList, CipStatus.Success, body.ToArray());
    }

    private static byte[] TemplateBody(ReadOnlySpan<byte> request, RequestPath path)
    {
        ushort id = (ushort)path.Instance;
        if (!LogixFixture.Knows(id)) return Error(CipServices.ReadTag, CipStatus.PathDestinationUnknown);

        int cursor = 2 + request[1] * 2;
        if (cursor + 6 > request.Length) return Error(CipServices.ReadTag, CipStatus.PathSegmentError);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(request[cursor..]);
        int wanted = BinaryPrimitives.ReadUInt16LittleEndian(request[(cursor + 4)..]);

        var blob = LogixFixture.DefinitionBlob(id);
        if (offset >= blob.Length) return Reply(CipServices.ReadTag, CipStatus.Success, Array.Empty<byte>());

        int available = blob.Length - (int)offset;
        int returned = Math.Min(wanted, available);
        Console.WriteLine($"  <- template {id} definition, {returned} of {blob.Length} bytes from {offset}");

        byte status = returned < available ? CipStatus.PartialTransfer : CipStatus.Success;
        return Reply(CipServices.ReadTag, status, blob.Skip((int)offset).Take(returned).ToArray());
    }

    // ===================== Encoding helpers =====================

    private static void Append16(List<byte> body, ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        body.AddRange(bytes);
    }

    private static void Append32(List<byte> body, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        body.AddRange(bytes);
    }

    private static void AppendAttribute(List<byte> body, ushort id, byte[] value)
    {
        Append16(body, id);
        Append16(body, 0);          // per-attribute status: success
        body.AddRange(value);
    }

    private static byte[] Reply(byte service, byte status, byte[] data) =>
        new byte[] { (byte)(service | 0x80), 0x00, status, 0x00 }.Concat(data).ToArray();

    private static byte[] Error(byte service, byte status) =>
        new byte[] { (byte)(service | 0x80), 0x00, status, 0x00 };

    // ===================== Request path parsing =====================

    private readonly record struct RequestPath(byte ClassId, uint Instance, string? SymbolicName);

    /// <summary>
    /// Walks a request path: an optional ANSI symbolic segment naming a program, a class segment, and
    /// a logical instance segment of one, two, or four bytes.
    /// </summary>
    private static bool TryParsePath(ReadOnlySpan<byte> request, out RequestPath path)
    {
        path = default;
        if (request.Length < 2) return false;

        int end = 2 + request[1] * 2;
        if (end > request.Length) return false;

        byte classId = 0;
        uint instance = 0;
        string? symbolic = null;
        int cursor = 2;

        while (cursor < end)
        {
            switch (request[cursor])
            {
                case 0x91:
                    if (cursor + 2 > end) return false;
                    int length = request[cursor + 1];
                    if (cursor + 2 + length > end) return false;
                    symbolic = Encoding.ASCII.GetString(request.Slice(cursor + 2, length));
                    cursor += 2 + length + (length % 2);
                    break;

                case 0x20:
                    if (cursor + 2 > end) return false;
                    classId = request[cursor + 1];
                    cursor += 2;
                    break;

                case 0x24:
                    if (cursor + 2 > end) return false;
                    instance = request[cursor + 1];
                    cursor += 2;
                    break;

                case 0x25:
                    if (cursor + 4 > end) return false;
                    instance = BinaryPrimitives.ReadUInt16LittleEndian(request[(cursor + 2)..]);
                    cursor += 4;
                    break;

                case 0x26:
                    if (cursor + 6 > end) return false;
                    instance = BinaryPrimitives.ReadUInt32LittleEndian(request[(cursor + 2)..]);
                    cursor += 6;
                    break;

                // Array element segments. They carry no class or instance, but a path containing
                // one still has to parse, or every indexed tag looks malformed.
                case 0x28: cursor += 2; break;
                case 0x29: cursor += 4; break;
                case 0x2A: cursor += 6; break;

                default:
                    return false;
            }
        }

        path = new RequestPath(classId, instance, symbolic);
        return true;
    }

    internal static bool TryGetEmbedded(ReadOnlySpan<byte> wrapper, out ReadOnlySpan<byte> embedded)
    {
        embedded = default;
        if (wrapper.Length < 2 || wrapper[0] != CipServices.UnconnectedSend) return false;

        int cursor = 2 + wrapper[1] * 2 + 2;
        if (cursor + 2 > wrapper.Length) return false;
        int size = BinaryPrimitives.ReadUInt16LittleEndian(wrapper[cursor..]);
        cursor += 2;
        if (size <= 0 || cursor + size > wrapper.Length) return false;

        embedded = wrapper.Slice(cursor, size);
        return true;
    }
}
