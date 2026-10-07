using System.Net;
using System.Text;

namespace OpcUaPlayground.Services.Cip;

/// <summary>
/// One hop of a CIP route path: leave the current device through <see cref="Port"/>, and the
/// next device is at <see cref="Link"/>. Rockwell numbers the backplane port 1 on every module;
/// port 2 is the module's other network (Ethernet on an EN2T/ENBT, ControlNet on a CNB, DH+
/// channel A on a DHRIO). Two exceptions worth remembering: a CompactLogix 5380 in dual-IP mode
/// uses ports 3 and 4 for its two Ethernet ports rather than port 2, and DH+ station numbers are
/// shown in octal by Rockwell's tools but travel in the path as decimal.
/// </summary>
public readonly record struct CipHop(byte Port, byte[] Link)
{
    public const byte PortBackplane = 1;
    public const byte PortEthernet = 2;

    /// <summary>Across the backplane to a slot. The link address of a 1756 module is its slot.</summary>
    public static CipHop Backplane(int slot) => new(PortBackplane, new[] { checked((byte)slot) });

    /// <summary>Out an Ethernet port to another device. The address travels as ASCII, not four octets.</summary>
    public static CipHop Ethernet(IPAddress address) =>
        new(PortEthernet, Encoding.ASCII.GetBytes(address.ToString()));

    public override string ToString() =>
        Link.Length == 1 ? $"{Port},{Link[0]}" : $"{Port},{Encoding.ASCII.GetString(Link)}";
}

/// <summary>
/// Encodes a chain of <see cref="CipHop"/> into the port segments of a CIP EPATH.
///
/// Each segment's first byte is 000 in bits 7-5 (marking it a port segment), an extended link
/// address flag at bit 4, and the port number in bits 3-0. A single-byte link address follows
/// the port byte directly. A longer one sets bit 4 and is written as a length byte then the
/// address bytes, with a 0x00 pad when that makes the segment an odd number of bytes -- which
/// keeps the whole path an even number of bytes, because the size field that precedes a path
/// counts 16-bit words rather than bytes.
/// </summary>
public static class CipRoutePath
{
    private const byte ExtendedLinkFlag = 0x10;
    private const byte ExtendedPortId = 0x0F;

    /// <summary>The path to a processor in <paramref name="slot"/> of the chassis at the connected IP.</summary>
    public static byte[] ToSlot(int slot) => Encode(CipHop.Backplane(slot));

    public static byte[] Encode(params CipHop[] hops) => Encode((IReadOnlyList<CipHop>)hops);

    public static byte[] Encode(IReadOnlyList<CipHop> hops)
    {
        var bytes = new List<byte>(hops.Count * 4);
        foreach (var hop in hops)
        {
            if (hop.Port is 0 or >= ExtendedPortId)
                throw new NotSupportedException(
                    $"Port {hop.Port} needs an extended port segment, which this app does not emit. " +
                    "Rockwell modules use ports 1 to 4.");
            if (hop.Link.Length == 0)
                throw new ArgumentException("A route hop needs a link address.", nameof(hops));

            if (hop.Link.Length == 1)
            {
                bytes.Add(hop.Port);
                bytes.Add(hop.Link[0]);
            }
            else
            {
                if (hop.Link.Length > byte.MaxValue)
                    throw new ArgumentException("Link address is too long to encode.", nameof(hops));
                bytes.Add((byte)(hop.Port | ExtendedLinkFlag));
                bytes.Add((byte)hop.Link.Length);
                bytes.AddRange(hop.Link);
                if (hop.Link.Length % 2 == 1) bytes.Add(0x00); // pad the segment to an even length
            }
        }
        return bytes.ToArray();
    }

    /// <summary>Path length in 16-bit words, which is the unit every CIP path size field uses.</summary>
    public static byte SizeInWords(byte[] path)
    {
        if (path.Length % 2 != 0)
            throw new ArgumentException("A route path is always an even number of bytes.", nameof(path));
        return checked((byte)(path.Length / 2));
    }
}
