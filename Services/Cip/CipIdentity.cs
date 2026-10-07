using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace OpcUaPlayground.Services.Cip;

/// <summary>
/// The CIP Identity Object of one device or one module. The same fields arrive two ways: in a List
/// Identity reply from the device at an IP address, and in a Get_Attributes_All reply routed across
/// a backplane to a slot.
/// </summary>
public sealed record CipIdentity(
    ushort VendorId,
    ushort DeviceType,
    ushort ProductCode,
    byte RevisionMajor,
    byte RevisionMinor,
    ushort Status,
    uint SerialNumber,
    string ProductName)
{
    /// <summary>ODVA device profile 0x0E. This is what a controller reports.</summary>
    public const ushort DeviceTypePlc = 14;

    /// <summary>
    /// ODVA device profile 0x0C. Do not read this as "definitely a bridge": a 1769-L32E controller
    /// reports it from its embedded Ethernet port, product name "1769-L32E Ethernet Port", and so do
    /// MicroLogix units, while a 1756-L83E reports 14 from its. The product name carries the catalog
    /// number and is the reliable tiebreaker.
    /// </summary>
    public const ushort DeviceTypeCommunicationsAdapter = 12;

    public bool IsController => DeviceType == DeviceTypePlc;

    public string VendorDisplay => CipVendorIds.GetDisplayName(VendorId);
    public string DeviceTypeDisplay => CipDeviceTypes.GetDisplayName(DeviceType);
    public string RevisionDisplay => $"{RevisionMajor}.{RevisionMinor:00}";
    public string SerialNumberDisplay => SerialNumber.ToString("X8");

    /// <summary>
    /// Parses the body of a Get_Attributes_All reply from the Identity Object: vendor, device type
    /// and product code as UINTs, a two-byte revision, a status word, a UDINT serial, then the
    /// product name as a SHORT_STRING (one length byte followed by that many ASCII characters).
    /// </summary>
    public static bool TryParseAttributesAll(ReadOnlySpan<byte> data, out CipIdentity? identity)
    {
        identity = null;
        if (data.Length < 15) return false;
        byte nameLength = data[14];
        if (data.Length < 15 + nameLength) return false;

        identity = new CipIdentity(
            BinaryPrimitives.ReadUInt16LittleEndian(data[0..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            data[6],
            data[7],
            BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[10..]),
            Encoding.ASCII.GetString(data.Slice(15, nameLength)));
        return true;
    }

    /// <summary>
    /// Parses a List Identity reply: the 24-byte encapsulation header, an item count at offset 24,
    /// then Common Packet Format items from offset 26. Inside the CIP Identity item (type 0x000C) an
    /// encapsulation protocol version and a 16-byte sockaddr precede the identity fields. Everything
    /// is little-endian except the sockaddr members, which are network order.
    /// </summary>
    public static bool TryParseListIdentity(ReadOnlySpan<byte> frame, out CipIdentity? identity, out IPAddress? reportedAddress)
    {
        identity = null;
        reportedAddress = null;
        if (frame.Length < 26) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(frame[0..]) != Encapsulation.CommandListIdentity) return false;

        ushort itemCount = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(24, 2));
        int cursor = 26;
        for (int i = 0; i < itemCount; i++)
        {
            if (cursor + 4 > frame.Length) return false;
            ushort itemType = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(cursor, 2));
            ushort itemLength = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(cursor + 2, 2));
            int itemStart = cursor + 4;
            if (itemStart + itemLength > frame.Length) return false;

            if (itemType == 0x000C)
            {
                var item = frame.Slice(itemStart, itemLength);
                if (item.Length < 33) return false;
                byte nameLength = item[32];
                if (item.Length < 33 + nameLength) return false;

                reportedAddress = new IPAddress(item.Slice(6, 4).ToArray());
                identity = new CipIdentity(
                    BinaryPrimitives.ReadUInt16LittleEndian(item.Slice(18, 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(item.Slice(20, 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(item.Slice(22, 2)),
                    item[24],
                    item[25],
                    BinaryPrimitives.ReadUInt16LittleEndian(item.Slice(26, 2)),
                    BinaryPrimitives.ReadUInt32LittleEndian(item.Slice(28, 4)),
                    Encoding.ASCII.GetString(item.Slice(33, nameLength)));
                return true;
            }

            cursor = itemStart + itemLength;
        }
        return false;
    }
}
