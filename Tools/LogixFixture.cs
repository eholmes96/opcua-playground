using System.Buffers.Binary;
using System.Text;
using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground.Tools;

/// <summary>
/// A fake controller's tag list and type definitions, used by <see cref="PlcSimulator"/>.
///
/// It deliberately includes the awkward cases rather than only the easy ones: tags the browser must
/// hide, a nested user-defined type, an array of a structure, and a symbol list long enough to be
/// returned over two replies so the partial-transfer paging path is actually exercised.
/// </summary>
public static class LogixFixture
{
    public const ushort TemplateRecipe = 100;
    public const ushort TemplateTimer = 101;
    public const ushort TemplateStation = 102;
    public const ushort TemplateProfile = 103;

    private const ushort StructBit = 0x8000;
    private const ushort SystemBit = 0x1000;
    private const ushort OneDimension = 0x2000;
    private const ushort TwoDimensions = 0x4000;

    public sealed record Symbol(uint Instance, string Name, ushort Type, uint[] Dims);

    /// <summary>
    /// Set by --simulate big. Generates a tag count in the range a real ControlLogix carries, which
    /// is the only way to catch the behaviour that killed the window: a small fixture makes every
    /// tree operation look instant.
    /// </summary>
    public static bool UseLargeSet { get; set; }

    public static Symbol[] ControllerTags => UseLargeSet ? LargeSet.Value : SmallControllerTags;

    private static readonly Lazy<Symbol[]> LargeSet = new(BuildLargeSet);

    /// <summary>
    /// A synthetic controller: plenty of scalars, plenty of UDT instances, and arrays of both. The
    /// nesting matters more than the raw count, since each Station carries a Recipe and a BOOL array
    /// and so expands to a dozen leaves of its own.
    /// </summary>
    private static Symbol[] BuildLargeSet()
    {
        var tags = new List<Symbol> { new(1, "Program:MainProgram", 0x1068, new uint[3]) };
        uint next = 2;

        for (int i = 0; i < 2000; i++) tags.Add(new(next++, $"Count_{i:D4}", 0x00C4, new uint[3]));
        for (int i = 0; i < 1000; i++) tags.Add(new(next++, $"Level_{i:D4}", 0x00CA, new uint[3]));
        for (int i = 0; i < 800; i++)
            tags.Add(new(next++, $"Station_{i:D3}", (ushort)(StructBit | TemplateStation), new uint[3]));
        for (int i = 0; i < 400; i++)
            tags.Add(new(next++, $"Line_{i:D3}", (ushort)(StructBit | TemplateStation | OneDimension), new uint[] { 3, 0, 0 }));
        for (int i = 0; i < 400; i++)
            tags.Add(new(next++, $"Buffer_{i:D3}", (ushort)(0x00C4 | OneDimension), new uint[] { 50, 0, 0 }));

        // Fifty leaves apiece, mixing DINT and REAL arrays inside one type.
        for (int i = 0; i < 300; i++)
            tags.Add(new(next++, $"Profile_{i:D3}", (ushort)(StructBit | TemplateProfile), new uint[3]));

        return tags.ToArray();
    }

    /// <summary>
    /// Controller scope. Three of these must never reach the tree.
    ///
    /// Instance ids must stay in ascending order. Paging resumes from the last id of a page plus
    /// one, so an id out of sequence sends the resume point backwards and the tag is listed twice.
    /// </summary>
    private static readonly Symbol[] SmallControllerTags =
    {
        new(1, "Program:MainProgram", 0x1068, new uint[3]),
        new(2, "MotorSpeed", 0x00C4, new uint[3]),                                   // DINT
        new(3, "Temperature", 0x00CA, new uint[3]),                                  // REAL
        new(4, "Running", 0x00C1, new uint[3]),                                      // BOOL
        new(5, "Counts", (ushort)(0x00C4 | OneDimension), new uint[] { 10, 0, 0 }),  // DINT[10]
        new(6, "Recipe", (ushort)(StructBit | TemplateRecipe), new uint[3]),
        new(7, "Stations", (ushort)(StructBit | TemplateStation | OneDimension), new uint[] { 3, 0, 0 }),
        new(8, "Local:1:I", 0x00C4, new uint[3]),                                    // module tag, hidden
        new(9, "__coverage", 0x00C4, new uint[3]),                                   // private, hidden
        new(10, "MapTag", (ushort)(0x00C4 | SystemBit), new uint[3]),                // system bit, hidden
        new(11, "Analyzer", (ushort)(StructBit | TemplateProfile), new uint[3]),     // DINT[32] and REAL[16] inside

        // Two dimensions. Each element answers with its own coordinates, Grid[1,2] reading 102, so
        // an address that loses an index reads the wrong element visibly rather than plausibly.
        new(12, "Grid", (ushort)(0x00C4 | TwoDimensions), new uint[] { 3, 4, 0 }),  // DINT[3,4]
    };

    /// <summary>Tags inside MainProgram.</summary>
    public static readonly Symbol[] ProgramTags =
    {
        new(1, "Step", 0x00C4, new uint[3]),
        new(2, "DwellTimer", (ushort)(StructBit | TemplateTimer), new uint[3]),
        new(3, "__scratch", 0x00C4, new uint[3]),
    };

    /// <summary>
    /// Symbols per reply. Tiny for the small fixture so the paging path is exercised on every run;
    /// realistic for the large one, since a real controller returns dozens per reply and pretending
    /// otherwise would only measure the simulator.
    /// </summary>
    public static int PageSize => UseLargeSet ? 120 : 6;

    // ===================== Templates =====================

    private sealed record Member(string Name, ushort Type, ushort TypeInfo, uint Offset);

    private static readonly Dictionary<ushort, (string Name, uint StructureSize, Member[] Members)> Templates = new()
    {
        [TemplateRecipe] = ("Recipe", 16, new Member[]
        {
            new("SetPoint", 0x00CA, 0, 0),
            new("Duration", 0x00C4, 0, 4),
            new("Enabled", 0x00C1, 0, 8),
            new("ZZZZZZZZZZRecipe0", 0x00C1, 0, 12),   // the host word Logix generates; must stay hidden
        }),
        [TemplateTimer] = ("TIMER", 12, new Member[]
        {
            new("PRE", 0x00C4, 0, 0),
            new("ACC", 0x00C4, 0, 4),
            new("DN", 0x00C1, 0, 8),
        }),
        // A structure whose own member is a structure, so nesting is covered.
        [TemplateStation] = ("Station", 32, new Member[]
        {
            new("Id", 0x00C4, 0, 0),
            new("Recipe", (ushort)(StructBit | TemplateRecipe), 0, 4),
            new("Faults", (ushort)(0x00C1 | OneDimension), 8, 20),
        }),

        // A user-defined type carrying two sizeable arrays of different atomic types. The existing
        // Station only has a BOOL[8], which is small and single-typed; this one expands to fifty
        // leaves per instance and mixes DINT with REAL, which is what a real analyser or profile
        // UDT looks like and what the tree and the value decoder actually have to survive.
        //
        // Offsets are laid out as Logix would: Id at 0, Mean at 4, then 32 DINTs, then 16 REALs.
        [TemplateProfile] = ("AnalyzerProfile", 200, new Member[]
        {
            new("Id", 0x00C4, 0, 0),
            new("Mean", 0x00CA, 0, 4),
            new("Counts", (ushort)(0x00C4 | OneDimension), 32, 8),
            new("Readings", (ushort)(0x00CA | OneDimension), 16, 136),
        }),
    };

    public static bool Knows(ushort instance) => Templates.ContainsKey(instance);

    public static ushort MemberCount(ushort instance) => (ushort)Templates[instance].Members.Length;

    public static uint StructureSize(ushort instance) => Templates[instance].StructureSize;

    /// <summary>
    /// The definition blob: one eight-byte record per member, then the names as null-separated
    /// strings, the type's own name first with a semicolon after it.
    /// </summary>
    public static byte[] DefinitionBlob(ushort instance)
    {
        var (name, _, members) = Templates[instance];

        var bytes = new List<byte>();
        foreach (var member in members)
        {
            var record = new byte[TemplateObject.MemberRecordLength];
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0), member.TypeInfo);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), member.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), member.Offset);
            bytes.AddRange(record);
        }

        bytes.AddRange(Encoding.ASCII.GetBytes($"{name};n"));
        bytes.Add(0x00);
        foreach (var member in members)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(member.Name));
            bytes.Add(0x00);
        }

        // The real controller reports a size in 32-bit words, and the reader asks for
        // (size * 4) - 21 bytes. Pad so that arithmetic lands exactly on the blob length.
        while ((bytes.Count + 21) % 4 != 0) bytes.Add(0x00);
        return bytes.ToArray();
    }

    /// <summary>The attribute 4 value that makes the reader's size formula produce the blob length.</summary>
    public static uint ObjectDefinitionSize(ushort instance) =>
        (uint)((DefinitionBlob(instance).Length + 21) / 4);
}
