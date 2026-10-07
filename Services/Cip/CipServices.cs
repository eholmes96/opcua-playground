namespace OpcUaPlayground.Services.Cip;

/// <summary>Raised when a CIP request would do something other than read. See <see cref="CipServices"/>.</summary>
public sealed class CipWriteAttemptException : InvalidOperationException
{
    public CipWriteAttemptException(byte service, string name)
        : base($"refusing to send CIP service 0x{service:X2} ({name}): this application is read-only")
    {
        Service = service;
    }

    public byte Service { get; }
}

/// <summary>
/// The read-only gate. Every CIP request leaves through <see cref="AssertReadOnly"/>, which admits
/// an explicit allowlist of services and rejects everything else, including services nobody has
/// written code for yet.
///
/// This is a structural guarantee rather than a promise about the current code. Reviewing today's
/// call sites shows the app only reads; this guard means a future edit that adds a write has to
/// delete this check to work, instead of quietly succeeding against someone's running plant.
///
/// Service numbers are the CIP common services from ODVA Volume 1. The dangerous neighbours of the
/// two we use are worth naming: 0x02 Set_Attributes_All, 0x04 Set_Attribute_List, 0x05 Reset,
/// 0x06 Start, 0x07 Stop, 0x08 Create, 0x09 Delete, 0x10 Set_Attribute_Single, 0x16 Save, and the
/// Logix-specific 0x4D Write Tag and 0x53 Write Tag Fragmented. None of them are admitted here.
///
/// The list grew when the tag browser arrived. Every addition is a read: Get_Attribute_List and
/// Get_Instance_Attribute_List enumerate, and Read Tag fetches values and type definitions. Read
/// Tag sits one number below Write Tag, so the pairing is worth stating: 0x4C in, 0x4D out.
/// Multiple_Service_Packet joined them when monitoring arrived; it carries reads, and the guard
/// unpacks it and checks each one rather than trusting the wrapper.
/// </summary>
public static class CipServices
{
    public const byte GetAttributesAll = 0x01;
    public const byte GetAttributeList = 0x03;
    public const byte GetAttributeSingle = 0x0E;

    /// <summary>Logix Read Tag. Reads a tag's value, and also the Template Object's definition blob.
    /// Its write counterpart, 0x4D, is deliberately absent from the allowlist below.</summary>
    public const byte ReadTag = 0x4C;

    /// <summary>Enumerates Symbol Object instances, which is how the tag browser lists tags.</summary>
    public const byte GetInstanceAttributeList = 0x55;

    /// <summary>
    /// Carries several requests in one message, unpacked by the Message Router. Like
    /// Unconnected_Send it is a container rather than an operation, so what it wraps is checked in
    /// its own right and a write cannot ride along inside a batch of reads.
    /// </summary>
    public const byte MultipleServicePacket = 0x0A;

    /// <summary>
    /// Unconnected_Send, addressed to the Connection Manager. It is a carrier, not an operation:
    /// whatever it wraps is checked in its own right, so it cannot be used to smuggle a write.
    ///
    /// Note the number is reused elsewhere in CIP. Against the Symbol Object 0x52 means Read Tag
    /// Fragmented, which is also a read, but this app only ever sends it to class 0x06.
    /// </summary>
    public const byte UnconnectedSend = 0x52;

    private static readonly Dictionary<byte, string> Allowed = new()
    {
        [GetAttributesAll] = "Get_Attributes_All",
        [GetAttributeList] = "Get_Attribute_List",
        [GetAttributeSingle] = "Get_Attribute_Single",
        [ReadTag] = "Read Tag",
        [GetInstanceAttributeList] = "Get_Instance_Attribute_List",
        [UnconnectedSend] = "Unconnected_Send",
        [MultipleServicePacket] = "Multiple_Service_Packet",
    };

    /// <summary>Symbol Object, the target of Get_Instance_Attribute_List when listing tags.</summary>
    private const byte ClassSymbol = 0x6B;

    /// <summary>Template Object, the one class Read Tag is sent to by number, to fetch a UDT definition.</summary>
    private const byte ClassTemplate = 0x6C;

    /// <summary>Connection Manager, the only object that should ever receive an Unconnected_Send.</summary>
    private const byte ClassConnectionManager = 0x06;

    public static bool IsAllowed(byte service) => Allowed.ContainsKey(service);

    public static string NameOf(byte service) =>
        Allowed.TryGetValue(service, out var name) ? name : "not a service this application sends";

    /// <summary>
    /// Throws unless every service in <paramref name="request"/> only reads. An Unconnected_Send is
    /// unwrapped and its embedded request checked too, recursively, so a routed write is caught.
    /// </summary>
    public static void AssertReadOnly(ReadOnlySpan<byte> request)
    {
        if (request.Length == 0) return;

        byte service = request[0];
        if (!IsAllowed(service)) throw new CipWriteAttemptException(service, NameOf(service));

        // 0x00 to 0x31 are common services and mean the same thing to every class. 0x4C, 0x52 and
        // 0x55 are object-specific: CIP leaves their meaning to the class they are sent to, so the
        // number alone proves nothing. Each is admitted only at the target this app builds it for.
        if (!IsAimedWhereExpected(service, request))
            throw new CipWriteAttemptException(service, $"{NameOf(service)} sent somewhere this application never sends it");

        if (service == UnconnectedSend)
        {
            // Fail closed. If the embedded request cannot be located, the wrapper is malformed or
            // uses a layout this parser does not understand, and refusing is the only safe answer:
            // passing it would let a write through unexamined, which is what this guard prevents.
            if (!TryGetEmbeddedRequest(request, out var embedded))
                throw new CipWriteAttemptException(service, "Unconnected_Send whose payload could not be read");

            AssertReadOnly(embedded);
            return;
        }

        if (service == MultipleServicePacket)
        {
            if (!TryGetBatchedRequests(request, out var batched))
                throw new CipWriteAttemptException(service, "Multiple_Service_Packet whose contents could not be read");

            foreach (var one in batched) AssertReadOnly(one);
        }
    }

    /// <summary>
    /// True when an object-specific service is addressed the way this app builds it, and always
    /// true for the common services, whose meaning does not depend on the class.
    ///
    /// Read Tag goes to a tag by name (symbols and element indexes) or to a Template Object
    /// instance for a UDT definition. Get_Instance_Attribute_List goes to the Symbol Object,
    /// optionally behind a program's name. Unconnected_Send goes to Connection Manager instance 1.
    /// Anything else, including a path segment this cannot read, is refused.
    /// </summary>
    private static bool IsAimedWhereExpected(byte service, ReadOnlySpan<byte> request)
    {
        if (service is not (ReadTag or GetInstanceAttributeList or UnconnectedSend)) return true;
        if (!TryReadPath(request, out var path) || path.Count == 0) return false;

        return service switch
        {
            ReadTag => IsTagName(path) || IsInstanceOf(path, 0, ClassTemplate),
            GetInstanceAttributeList => IsInstanceOf(path, 0, ClassSymbol)
                                        || (path[0].Kind == SegmentKind.Symbol && IsInstanceOf(path, 1, ClassSymbol)),
            _ => IsInstanceOf(path, 0, ClassConnectionManager, instance: 1),
        };
    }

    private enum SegmentKind { Symbol, Class, Instance, Element }

    /// <summary>A name, then any mix of member names and element indexes.</summary>
    private static bool IsTagName(List<(SegmentKind Kind, uint Value)> path) =>
        path[0].Kind == SegmentKind.Symbol
        && path.All(s => s.Kind is SegmentKind.Symbol or SegmentKind.Element);

    /// <summary>Exactly a class segment naming <paramref name="classId"/> and then one instance
    /// segment, starting at <paramref name="from"/> and running to the end of the path.</summary>
    private static bool IsInstanceOf(
        List<(SegmentKind Kind, uint Value)> path, int from, byte classId, uint? instance = null) =>
        path.Count == from + 2
        && path[from] == (SegmentKind.Class, classId)
        && path[from + 1].Kind == SegmentKind.Instance
        && (instance is null || path[from + 1].Value == instance);

    /// <summary>
    /// Reads a request's path into segments: ANSI extended symbolic (0x91), and 8, 16 and 32-bit
    /// logical class, instance and element segments. False for any other segment type, or for a
    /// path that does not fit the size the request declares, so an unfamiliar address fails closed.
    /// </summary>
    private static bool TryReadPath(ReadOnlySpan<byte> request, out List<(SegmentKind Kind, uint Value)> path)
    {
        path = new List<(SegmentKind, uint)>();
        if (request.Length < 2) return false;

        int end = 2 + request[1] * 2;
        if (end > request.Length) return false;

        int at = 2;
        while (at < end)
        {
            byte type = request[at];
            if (type == 0x91)
            {
                if (at + 2 > end) return false;
                int length = request[at + 1];
                int size = 2 + length + length % 2;
                if (length == 0 || at + size > end) return false;
                path.Add((SegmentKind.Symbol, 0));
                at += size;
                continue;
            }

            SegmentKind kind;
            switch (type & 0xFC)
            {
                case 0x20: kind = SegmentKind.Class; break;
                case 0x24: kind = SegmentKind.Instance; break;
                case 0x28: kind = SegmentKind.Element; break;
                default: return false;
            }

            // The low two bits give the size: 8-bit value, or a pad byte then 16 or 32 bits.
            switch (type & 0x03)
            {
                case 0:
                    if (at + 2 > end) return false;
                    path.Add((kind, request[at + 1]));
                    at += 2;
                    break;
                case 1:
                    if (at + 4 > end) return false;
                    path.Add((kind, (uint)(request[at + 2] | request[at + 3] << 8)));
                    at += 4;
                    break;
                case 2:
                    if (at + 6 > end) return false;
                    path.Add((kind, (uint)(request[at + 2] | request[at + 3] << 8
                                           | request[at + 4] << 16 | request[at + 5] << 24)));
                    at += 6;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits a Multiple Service Packet into the requests it carries: a count, an offset per
    /// request measured from the start of the count field, then the requests themselves.
    /// </summary>
    private static bool TryGetBatchedRequests(ReadOnlySpan<byte> request, out List<byte[]> batched)
    {
        batched = new List<byte[]>();
        if (request.Length < 2) return false;

        int body = 2 + request[1] * 2;
        if (body + 2 > request.Length) return false;

        int count = request[body] | (request[body + 1] << 8);
        if (count <= 0 || body + 2 + count * 2 > request.Length) return false;

        for (int i = 0; i < count; i++)
        {
            int at = body + 2 + i * 2;
            int start = body + (request[at] | (request[at + 1] << 8));
            int end = request.Length;
            if (i + 1 < count)
            {
                int nextAt = body + 2 + (i + 1) * 2;
                end = body + (request[nextAt] | (request[nextAt + 1] << 8));
            }
            if (start < body || end > request.Length || start >= end) return false;
            batched.Add(request[start..end].ToArray());
        }

        return true;
    }

    /// <summary>
    /// Finds the embedded request inside an Unconnected_Send: past the service byte, the request
    /// path whose size is counted in 16-bit words, the priority tick and timeout ticks, and the
    /// embedded message size.
    /// </summary>
    private static bool TryGetEmbeddedRequest(ReadOnlySpan<byte> request, out ReadOnlySpan<byte> embedded)
    {
        embedded = default;
        if (request.Length < 2) return false;

        int cursor = 2 + request[1] * 2 + 2;
        if (cursor + 2 > request.Length) return false;

        int size = request[cursor] | (request[cursor + 1] << 8);
        cursor += 2;
        if (size <= 0 || cursor + size > request.Length) return false;

        embedded = request.Slice(cursor, size);
        return true;
    }
}
