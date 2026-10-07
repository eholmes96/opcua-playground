using System.Buffers.Binary;

namespace OpcUaPlayground.Services.Cip;

/// <summary>A parsed CIP response: reply service, status, and the data that follows it.</summary>
public readonly record struct CipResponse(byte Service, byte GeneralStatus, ushort ExtendedStatus, byte[] Data)
{
    public bool IsSuccess => GeneralStatus == CipStatus.Success;
    public string Message => CipStatus.Describe(GeneralStatus, ExtendedStatus);
}

/// <summary>
/// Turns CIP status codes into something a person can act on. The distinction that matters most
/// here is wrong-IP versus wrong-slot: a TCP connect failure means the address is wrong, while a
/// routed request that comes back 0x01/0x0204 or 0x01/0x0312 means the address was right and the
/// slot was not.
///
/// The table is deliberately limited to codes confirmed against Rockwell's CIP error code
/// reference and the routing behaviour documented by pycomm3 and pylogix. Anything else is
/// reported with its raw hex values rather than a guessed description.
/// </summary>
public static class CipStatus
{
    public const byte Success = 0x00;
    public const byte ConnectionFailure = 0x01;
    public const byte PathSegmentError = 0x04;
    public const byte PathDestinationUnknown = 0x05;

    /// <summary>Not an error. The controller sent what fit and has more to give, which is how tag
    /// listing and template reads page through data larger than one reply.</summary>
    public const byte PartialTransfer = 0x06;

    public const ushort ExtUnconnectedRequestTimedOut = 0x0204;
    public const ushort ExtPortNotAvailable = 0x0311;
    public const ushort ExtLinkAddressNotAvailable = 0x0312;
    public const ushort ExtInvalidSegmentType = 0x0315;

    public static string Describe(byte generalStatus, ushort extendedStatus) => (generalStatus, extendedStatus) switch
    {
        (Success, _) => "ok",
        (PartialTransfer, _) => "more data follows",
        (ConnectionFailure, ExtUnconnectedRequestTimedOut) => "nothing answered at that slot",
        (ConnectionFailure, ExtLinkAddressNotAvailable) => "no module at that slot",
        (ConnectionFailure, ExtPortNotAvailable) => "that module has no backplane port",
        (ConnectionFailure, ExtInvalidSegmentType) => "the route path was rejected as malformed",
        // 0x05 is not slot-specific. It also comes back for a tag or a property that does not
        // exist, so the wording has to be true of a chassis walk AND of a read by name.
        (PathDestinationUnknown, _) => "nothing exists at that path",
        (PathSegmentError, _) => "the route path was rejected as malformed",
        _ => $"CIP error 0x{generalStatus:X2}" + (extendedStatus != 0 ? $"/0x{extendedStatus:X4}" : ""),
    };

    /// <summary>True when the status says the slot is empty or unreachable, rather than that
    /// something went wrong at a level worth surfacing as a fault.</summary>
    public static bool MeansSlotIsEmpty(byte generalStatus, ushort extendedStatus) =>
        (generalStatus, extendedStatus) is (ConnectionFailure, ExtUnconnectedRequestTimedOut)
                                        or (ConnectionFailure, ExtLinkAddressNotAvailable)
                                        or (PathDestinationUnknown, _);

    /// <summary>
    /// Parses a CIP response: reply service, a reserved byte, general status, the size of the
    /// additional status in 16-bit words, that additional status, then the response data.
    /// </summary>
    public static bool TryParseResponse(ReadOnlySpan<byte> reply, out CipResponse response)
    {
        response = default;
        if (reply.Length < 4) return false;
        byte service = reply[0];
        byte generalStatus = reply[2];
        int extWords = reply[3];
        int dataStart = 4 + extWords * 2;
        if (dataStart > reply.Length) return false;

        // Only the first additional status word carries the routing detail we act on.
        ushort extendedStatus = extWords >= 1 ? BinaryPrimitives.ReadUInt16LittleEndian(reply[4..]) : (ushort)0;
        response = new CipResponse(service, generalStatus, extendedStatus, reply[dataStart..].ToArray());
        return true;
    }
}
