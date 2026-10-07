namespace OpcUaPlayground.Services.Cip;

/// <summary>
/// The type word attached to every Logix symbol, unpacked.
///
/// Bit 15 says whether the tag is a structure. For a structure, bits 0 to 11 are the Template
/// Object instance that defines it; for an atomic tag, bits 0 to 7 are the CIP elementary type
/// code. Bits 13 and 14 count array dimensions, 0 to 3. Bits 8 to 10 carry the bit position of a
/// BOOL packed inside a larger word. Bit 12 marks a tag the controller considers its own.
///
/// Layout confirmed against pycomm3's decoder, which is the reference implementation most other
/// open tools follow.
/// </summary>
public readonly record struct LogixSymbolType(ushort Raw)
{
    public bool IsStructure => (Raw & 0b1000_0000_0000_0000) != 0;

    /// <summary>Bit 12. Set on tags the controller creates for itself rather than for the program.</summary>
    public bool IsSystem => (Raw & 0b0001_0000_0000_0000) != 0;

    /// <summary>0 for a scalar, up to 3 for a three-dimensional array.</summary>
    public int Dimensions => (Raw & 0b0110_0000_0000_0000) >> 13;

    /// <summary>Template Object instance defining this structure. Meaningless unless <see cref="IsStructure"/>.</summary>
    public ushort TemplateInstanceId => (ushort)(Raw & 0b0000_1111_1111_1111);

    /// <summary>CIP elementary type code. Meaningless when <see cref="IsStructure"/>.</summary>
    public byte AtomicCode => (byte)(Raw & 0b0000_0000_1111_1111);

    /// <summary>Which bit of the host word a packed BOOL occupies.</summary>
    public int BitPosition => (Raw & 0b0000_0111_0000_0000) >> 8;

    public bool IsAtomic => !IsStructure;
}

/// <summary>
/// CIP elementary data type codes and their names, as defined in ODVA Volume 1 and used verbatim by
/// Logix. Unknown codes are reported as their raw hex rather than guessed at.
/// </summary>
public static class LogixTypes
{
    public const byte Bool = 0xC1;
    public const byte Sint = 0xC2;
    public const byte Int = 0xC3;
    public const byte Dint = 0xC4;
    public const byte Lint = 0xC5;
    public const byte Real = 0xCA;

    private static readonly Dictionary<byte, string> Names = new()
    {
        [0xC1] = "BOOL",
        [0xC2] = "SINT",
        [0xC3] = "INT",
        [0xC4] = "DINT",
        [0xC5] = "LINT",
        [0xC6] = "USINT",
        [0xC7] = "UINT",
        [0xC8] = "UDINT",
        [0xC9] = "ULINT",
        [0xCA] = "REAL",
        [0xCB] = "LREAL",
        [0xD0] = "STRING",
        [0xD1] = "BYTE",
        [0xD2] = "WORD",
        [0xD3] = "DWORD",
        [0xD4] = "LWORD",
        [0xD5] = "STRING2",
        [0xD9] = "STRINGN",
        [0xDA] = "SHORT_STRING",
        [0xDB] = "TIME",
        [0xDC] = "EPATH",
        [0xDD] = "ENGUNIT",
        [0xDE] = "STRINGI",
    };

    public static string NameOf(byte code) =>
        Names.TryGetValue(code, out var name) ? name : $"0x{code:X2}";

    /// <summary>
    /// True for names Logix uses internally rather than for anything a person wrote. Controller
    /// scope is full of these, and Ignition does not show them either.
    ///
    /// The rules are the ones pycomm3 applies: a colon anywhere marks module and connection tags
    /// such as "Local:1:I", and a leading double underscore marks a private tag.
    /// </summary>
    public static bool IsInternalName(string name) =>
        name.Contains(':') || name.StartsWith("__", StringComparison.Ordinal);
}
