using System.Buffers.Binary;
using System.Text;

namespace OpcUaPlayground.Services.Cip;

/// <summary>One tag as the Symbol Object reports it.</summary>
public sealed record LogixSymbol(uint InstanceId, string Name, LogixSymbolType Type, uint[] Dimensions)
{
    /// <summary>A controller-scope symbol named "Program:MainProgram" is a program, not a tag.</summary>
    public bool IsProgramContainer => Name.StartsWith("Program:", StringComparison.Ordinal);

    public string ProgramName => IsProgramContainer ? Name["Program:".Length..] : "";

    /// <summary>Array extents that are actually in use, trimmed to the dimension count in the type word.</summary>
    public IReadOnlyList<uint> Extents => Dimensions.Take(Type.Dimensions).ToArray();
}

/// <summary>
/// Lists the tags in a Logix controller by enumerating the Symbol Object, class 0x6B.
///
/// The controller answers Get_Instance_Attribute_List (0x55) with as many symbols as fit in one
/// reply, then reports general status 0x06, partial transfer, to say there are more. The next
/// request resumes from the last instance seen plus one, which is why the instance id is part of
/// the request path rather than the payload.
///
/// Program-scoped tags are a second pass. Controller scope contains a symbol per program named
/// "Program:MainProgram", and listing that program's own tags means prefixing the path with an
/// ANSI extended symbolic segment naming it.
/// </summary>
public static class SymbolObject
{
    public const byte ClassSymbol = 0x6B;

    /// <summary>Attribute 1 is the symbol name, 2 the type word, 8 the three array extents.</summary>
    private static readonly ushort[] RequestedAttributes = { 1, 2, 8 };

    public static byte[] BuildListTags(uint startInstance, string? programName = null)
    {
        var path = new List<byte>(24);
        if (!string.IsNullOrEmpty(programName)) AppendSymbolicSegment(path, programName);
        path.Add(0x20);
        path.Add(ClassSymbol);
        AppendInstanceSegment(path, startInstance);

        if (path.Count % 2 != 0)
            throw new InvalidOperationException("request path must be an even number of bytes");

        var request = new List<byte>(path.Count + 12)
        {
            CipServices.GetInstanceAttributeList,
            checked((byte)(path.Count / 2)),
        };
        request.AddRange(path);

        var count = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(count, (ushort)RequestedAttributes.Length);
        request.AddRange(count);
        foreach (var attribute in RequestedAttributes)
        {
            var id = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(id, attribute);
            request.AddRange(id);
        }
        return request.ToArray();
    }

    /// <summary>ANSI extended symbolic segment: 0x91, a length, the characters, and a pad to keep the
    /// segment an even number of bytes.</summary>
    private static void AppendSymbolicSegment(List<byte> path, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        path.Add(0x91);
        path.Add(checked((byte)bytes.Length));
        path.AddRange(bytes);
        if (bytes.Length % 2 != 0) path.Add(0x00);
    }

    /// <summary>Logical instance segment, sized to the value. Big programs run past 255 instances,
    /// so an 8-bit segment alone would silently stop the listing part way through.</summary>
    private static void AppendInstanceSegment(List<byte> path, uint instance)
    {
        if (instance <= byte.MaxValue)
        {
            path.Add(0x24);
            path.Add((byte)instance);
            return;
        }
        if (instance <= ushort.MaxValue)
        {
            path.Add(0x25);
            path.Add(0x00);
            var value = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(value, (ushort)instance);
            path.AddRange(value);
            return;
        }
        path.Add(0x26);
        path.Add(0x00);
        var wide = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(wide, instance);
        path.AddRange(wide);
    }

    /// <summary>
    /// Reads the symbols out of a reply. Each entry is an instance id, then the requested attributes
    /// in ascending id order: a length-prefixed name, the type word, and three array extents.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out List<LogixSymbol> symbols)
    {
        symbols = new List<LogixSymbol>();
        int cursor = 0;

        while (cursor < data.Length)
        {
            if (cursor + 4 > data.Length) return false;
            uint instance = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
            cursor += 4;

            if (cursor + 2 > data.Length) return false;
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data[cursor..]);
            cursor += 2;
            if (cursor + nameLength > data.Length) return false;
            string name = Encoding.ASCII.GetString(data.Slice(cursor, nameLength));
            cursor += nameLength;

            if (cursor + 2 > data.Length) return false;
            var type = new LogixSymbolType(BinaryPrimitives.ReadUInt16LittleEndian(data[cursor..]));
            cursor += 2;

            if (cursor + 12 > data.Length) return false;
            var dimensions = new uint[3];
            for (int i = 0; i < 3; i++)
            {
                dimensions[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(cursor + i * 4)..]);
            }
            cursor += 12;

            symbols.Add(new LogixSymbol(instance, name, type, dimensions));
        }

        return true;
    }
}
