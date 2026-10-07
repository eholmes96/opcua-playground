using System.Buffers.Binary;
using System.Text;

namespace OpcUaPlayground.Services.Cip;

/// <summary>
/// Turns a tag name into the request path a Logix controller expects.
///
/// A name is a chain of ANSI extended symbolic segments, one per dotted part, each a 0x91 marker,
/// a length, the characters, and a pad byte when the length is odd. An array index is not part of
/// the name: it becomes a logical element segment after the symbol it indexes.
///
/// So "Stations[1].Recipe.SetPoint" is four segments, not three: the symbol "Stations", element 1,
/// then the symbols "Recipe" and "SetPoint". A program-scoped name keeps its colon, since
/// "Program:MainProgram" is one symbol rather than two.
/// </summary>
public static class LogixTagPath
{
    private const byte AnsiSymbolicSegment = 0x91;

    /// <summary>The property suffix that asks for a tag's description rather than its value.</summary>
    public const string DescriptionProperty = "@Description";

    public static byte[] Encode(string tagName)
    {
        var path = new List<byte>(tagName.Length + 8);

        foreach (string part in tagName.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            int bracket = part.IndexOf('[');
            string symbol = bracket >= 0 ? part[..bracket] : part;

            if (symbol.Length > 0) AppendSymbol(path, symbol);
            if (bracket >= 0)
                foreach (uint index in ParseIndexes(part[bracket..], tagName))
                    AppendElement(path, index);
        }

        return path.ToArray();
    }

    /// <summary>
    /// "[1,2]" is how Logix writes an element of a two dimensional array. "[1][2]" is accepted as
    /// the same thing, since earlier builds of this app wrote it that way into saved tag sets.
    ///
    /// Anything that is not a number is refused. Dropping it instead leaves a path with no element
    /// segment, and the controller answers that with element zero: the wrong value, and no error.
    /// </summary>
    private static List<uint> ParseIndexes(string brackets, string tagName)
    {
        var indexes = new List<uint>();
        int at = 0;

        while (at < brackets.Length)
        {
            int close = brackets[at] == '[' ? brackets.IndexOf(']', at) : -1;
            if (close < 0) throw new ArgumentException($"'{tagName}' has an unclosed or stray index bracket");

            foreach (string piece in brackets[(at + 1)..close].Split(','))
            {
                if (!uint.TryParse(piece.Trim(), out uint index))
                    throw new ArgumentException($"'{tagName}' has an index that is not a number: '{piece.Trim()}'");
                indexes.Add(index);
            }

            at = close + 1;
        }

        return indexes;
    }

    /// <summary>The path for a tag's description, which is its own path plus the property symbol.</summary>
    public static byte[] EncodeDescription(string tagName)
    {
        var path = new List<byte>(Encode(tagName));
        AppendSymbol(path, DescriptionProperty);
        return path.ToArray();
    }

    private static void AppendSymbol(List<byte> path, string symbol)
    {
        var bytes = Encoding.ASCII.GetBytes(symbol);
        if (bytes.Length > byte.MaxValue)
            throw new ArgumentException($"symbol '{symbol}' is too long for a path segment");

        path.Add(AnsiSymbolicSegment);
        path.Add((byte)bytes.Length);
        path.AddRange(bytes);
        if (bytes.Length % 2 != 0) path.Add(0x00);   // keep the segment on a 16-bit boundary
    }

    /// <summary>Logical element segment, sized to the index.</summary>
    private static void AppendElement(List<byte> path, uint index)
    {
        if (index <= byte.MaxValue)
        {
            path.Add(0x28);
            path.Add((byte)index);
            return;
        }
        if (index <= ushort.MaxValue)
        {
            path.Add(0x29);
            path.Add(0x00);
            var value = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(value, (ushort)index);
            path.AddRange(value);
            return;
        }
        path.Add(0x2A);
        path.Add(0x00);
        var wide = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(wide, index);
        path.AddRange(wide);
    }

    public static byte SizeInWords(byte[] path)
    {
        if (path.Length % 2 != 0)
            throw new ArgumentException("a request path is always an even number of bytes", nameof(path));
        return checked((byte)(path.Length / 2));
    }
}
