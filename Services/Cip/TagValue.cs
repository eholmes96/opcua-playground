using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace OpcUaPlayground.Services.Cip;

/// <summary>
/// A decoded tag value: what came back, how it should read on screen, and how many bytes it took.
/// </summary>
public sealed record TagValue(ushort TypeCode, string Display, int ByteCount)
{
    public string TypeName => LogixTypes.NameOf((byte)(TypeCode & 0xFF));
}

/// <summary>
/// Decodes the payload of a Read Tag reply.
///
/// The reply leads with a type code, then the raw value. A structure reports 0x02A0 followed by a
/// two-byte handle, and its contents cannot be interpreted without the type definition, so the only
/// structure decoded here is the one worth the trouble: a Logix STRING, which is a DINT length and
/// a fixed byte array and would otherwise show up as unreadable bytes.
/// </summary>
public static class TagValueDecoder
{
    private const ushort StructureTypeCode = 0x02A0;

    /// <summary>Bytes each elementary type occupies, used to size the table before anything is read.</summary>
    public static int SizeOf(byte typeCode) => typeCode switch
    {
        0xC1 or 0xC2 or 0xC6 or 0xD1 => 1,                    // BOOL, SINT, USINT, BYTE
        0xC3 or 0xC7 or 0xD2 => 2,                            // INT, UINT, WORD
        0xC4 or 0xC8 or 0xCA or 0xD3 => 4,                    // DINT, UDINT, REAL, DWORD
        0xC5 or 0xC9 or 0xCB or 0xD4 => 8,                    // LINT, ULINT, LREAL, LWORD
        _ => 0,                                               // unknown, or a structure
    };

    public static bool TryDecode(ReadOnlySpan<byte> data, string declaredType, out TagValue? value)
    {
        value = null;
        if (data.Length < 2) return false;

        ushort typeCode = BinaryPrimitives.ReadUInt16LittleEndian(data);

        if (typeCode == StructureTypeCode)
        {
            // Two bytes of structure handle follow the type code.
            if (data.Length < 4) return false;
            var body = data[4..];

            if (declaredType.Equals("STRING", StringComparison.OrdinalIgnoreCase)
                && TryDecodeLogixString(body, out string text))
            {
                value = new TagValue(typeCode, text, body.Length);
                return true;
            }

            value = new TagValue(typeCode, $"({declaredType})", body.Length);
            return true;
        }

        var payload = data[2..];
        string display = Format((byte)(typeCode & 0xFF), payload);
        value = new TagValue(typeCode, display, payload.Length);
        return true;
    }

    /// <summary>
    /// A reply shorter than its type is shown as "?". Checked up front rather than caught, because
    /// indexing a short span and slicing one throw different exceptions, and catching only one of
    /// them let a single bad reply end all polling.
    /// </summary>
    private static string Format(byte typeCode, ReadOnlySpan<byte> data)
    {
        if (data.Length < SizeOf(typeCode)) return "?";

        return typeCode switch
        {
            0xC1 => (data[0] != 0).ToString(),
            0xC2 => ((sbyte)data[0]).ToString(CultureInfo.InvariantCulture),
            0xC6 => data[0].ToString(CultureInfo.InvariantCulture),
            0xC3 => BinaryPrimitives.ReadInt16LittleEndian(data).ToString(CultureInfo.InvariantCulture),
            0xC7 => BinaryPrimitives.ReadUInt16LittleEndian(data).ToString(CultureInfo.InvariantCulture),
            0xC4 => BinaryPrimitives.ReadInt32LittleEndian(data).ToString(CultureInfo.InvariantCulture),
            0xC8 => BinaryPrimitives.ReadUInt32LittleEndian(data).ToString(CultureInfo.InvariantCulture),
            0xC5 => BinaryPrimitives.ReadInt64LittleEndian(data).ToString(CultureInfo.InvariantCulture),
            0xC9 => BinaryPrimitives.ReadUInt64LittleEndian(data).ToString(CultureInfo.InvariantCulture),

            // Floats get enough digits to round-trip without turning into noise.
            0xCA => BinaryPrimitives.ReadSingleLittleEndian(data).ToString("G7", CultureInfo.InvariantCulture),
            0xCB => BinaryPrimitives.ReadDoubleLittleEndian(data).ToString("G15", CultureInfo.InvariantCulture),

            // Bit-array types read better as hex than as a decimal number.
            0xD1 => $"0x{data[0]:X2}",
            0xD2 => $"0x{BinaryPrimitives.ReadUInt16LittleEndian(data):X4}",
            0xD3 => $"0x{BinaryPrimitives.ReadUInt32LittleEndian(data):X8}",
            0xD4 => $"0x{BinaryPrimitives.ReadUInt64LittleEndian(data):X16}",

            _ => BitConverter.ToString(data.ToArray()).Replace('-', ' '),
        };
    }

    /// <summary>A Logix STRING is a DINT length followed by a fixed byte array, only the first
    /// LEN bytes of which are meaningful.</summary>
    private static bool TryDecodeLogixString(ReadOnlySpan<byte> data, out string text)
    {
        text = "";
        if (data.Length < 4) return false;

        int length = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (length < 0 || 4 + length > data.Length) return false;

        text = Encoding.ASCII.GetString(data.Slice(4, length));
        return true;
    }
}
