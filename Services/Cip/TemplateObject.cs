using System.Buffers.Binary;
using System.Text;

namespace OpcUaPlayground.Services.Cip;

/// <summary>One field of a user-defined type.</summary>
public sealed record TemplateMember(string Name, LogixSymbolType Type, ushort TypeInfo, uint Offset)
{
    /// <summary>For an array member, <c>TypeInfo</c> is the element count.</summary>
    public ushort ArrayLength => Type.Dimensions > 0 ? TypeInfo : (ushort)0;

    /// <summary>Members Logix adds for its own bookkeeping. A leading double underscore is the
    /// documented marker; the long run of Z characters is the host word Logix generates for BOOLs
    /// packed inside a structure, which no one wrote and no one wants to see.</summary>
    public bool IsInternal =>
        Name.StartsWith("__", StringComparison.Ordinal) ||
        Name.StartsWith("ZZZZZZZZZZ", StringComparison.Ordinal);
}

/// <summary>A user-defined type: its name, its size, and its fields in declaration order.</summary>
public sealed record LogixTemplate(ushort InstanceId, string Name, uint StructureSize, IReadOnlyList<TemplateMember> Members);

/// <summary>
/// Reads user-defined type definitions from the Template Object, class 0x6C.
///
/// Two steps. First Get_Attribute_List for the header: how many members the type has and how large
/// its definition blob is. Then Read Tag against the same instance to pull that blob, which arrives
/// as a fixed-size record per member followed by a run of null-separated names.
///
/// The size formula is the awkward part and is easy to get wrong. The bytes to fetch are
/// (object definition size * 4) - 21, taken from pycomm3, which is the reference implementation
/// here. A wrong constant produces a blob that parses into plausible nonsense rather than an error,
/// so the self test pins it.
/// </summary>
public static class TemplateObject
{
    public const byte ClassTemplate = 0x6C;

    /// <summary>Each member record is a type-info word, a type word, and a four-byte offset.</summary>
    public const int MemberRecordLength = 8;

    private const ushort AttrStructureHandle = 1;
    private const ushort AttrMemberCount = 2;
    private const ushort AttrObjectDefinitionSize = 4;
    private const ushort AttrStructureSize = 5;

    private static readonly ushort[] HeaderAttributes =
        { AttrStructureHandle, AttrMemberCount, AttrObjectDefinitionSize, AttrStructureSize };

    public sealed record TemplateHeader(ushort MemberCount, uint ObjectDefinitionSize, uint StructureSize)
    {
        /// <summary>How many bytes of definition to fetch for this template.</summary>
        public int DefinitionBytes => (int)(ObjectDefinitionSize * 4) - 21;
    }

    public static byte[] BuildGetHeader(ushort templateInstance)
    {
        var request = new List<byte>(24)
        {
            CipServices.GetAttributeList,
            0x03,                                   // path size in words: class segment plus a 16-bit instance
            0x20, ClassTemplate,
            0x25, 0x00,
        };
        var instance = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(instance, templateInstance);
        request.AddRange(instance);

        var count = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(count, (ushort)HeaderAttributes.Length);
        request.AddRange(count);
        foreach (var attribute in HeaderAttributes)
        {
            var id = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(id, attribute);
            request.AddRange(id);
        }
        return request.ToArray();
    }

    /// <summary>
    /// Reads part of the definition blob. Logix will not return the whole thing at once for a large
    /// type, so the caller resumes at a new offset while the status keeps saying partial transfer.
    /// </summary>
    public static byte[] BuildReadDefinition(ushort templateInstance, uint offset, ushort byteCount)
    {
        var request = new List<byte>(20)
        {
            CipServices.ReadTag,
            0x03,
            0x20, ClassTemplate,
            0x25, 0x00,
        };
        var instance = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(instance, templateInstance);
        request.AddRange(instance);

        var start = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(start, offset);
        request.AddRange(start);

        var count = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(count, byteCount);
        request.AddRange(count);
        return request.ToArray();
    }

    /// <summary>
    /// Parses a Get_Attribute_List reply: a count, then per attribute an id, a status, and the value.
    /// Attributes are matched by their id rather than by position, so a controller that reorders them
    /// or omits one does not silently shift every field.
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> data, out TemplateHeader? header)
    {
        header = null;
        if (data.Length < 2) return false;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int cursor = 2;

        ushort? memberCount = null;
        uint? definitionSize = null;
        uint? structureSize = null;

        for (int i = 0; i < count; i++)
        {
            if (cursor + 4 > data.Length) return false;
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(data[cursor..]);
            ushort status = BinaryPrimitives.ReadUInt16LittleEndian(data[(cursor + 2)..]);
            cursor += 4;
            if (status != 0) continue;

            switch (id)
            {
                case AttrStructureHandle when cursor + 2 <= data.Length:
                    cursor += 2;
                    break;
                case AttrMemberCount when cursor + 2 <= data.Length:
                    memberCount = BinaryPrimitives.ReadUInt16LittleEndian(data[cursor..]);
                    cursor += 2;
                    break;
                case AttrObjectDefinitionSize when cursor + 4 <= data.Length:
                    definitionSize = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
                    cursor += 4;
                    break;
                case AttrStructureSize when cursor + 4 <= data.Length:
                    structureSize = BinaryPrimitives.ReadUInt32LittleEndian(data[cursor..]);
                    cursor += 4;
                    break;
                default:
                    return false;   // an unexpected id means the cursor can no longer be trusted
            }
        }

        if (memberCount is null || definitionSize is null || structureSize is null) return false;
        header = new TemplateHeader(memberCount.Value, definitionSize.Value, structureSize.Value);
        return true;
    }

    /// <summary>
    /// Parses the definition blob: one eight-byte record per member, then the names as a run of
    /// null-separated strings. The first name is the type's own, given as "Name;something", so the
    /// semicolon marks where the useful part ends. The member names follow in declaration order.
    /// </summary>
    public static bool TryParseDefinition(
        ReadOnlySpan<byte> data, ushort instanceId, TemplateHeader header, out LogixTemplate? template)
    {
        template = null;
        int infoLength = header.MemberCount * MemberRecordLength;
        if (data.Length < infoLength) return false;

        var records = new (ushort TypeInfo, LogixSymbolType Type, uint Offset)[header.MemberCount];
        for (int i = 0; i < header.MemberCount; i++)
        {
            var record = data.Slice(i * MemberRecordLength, MemberRecordLength);
            records[i] = (
                BinaryPrimitives.ReadUInt16LittleEndian(record),
                new LogixSymbolType(BinaryPrimitives.ReadUInt16LittleEndian(record[2..])),
                BinaryPrimitives.ReadUInt32LittleEndian(record[4..]));
        }

        var names = Encoding.ASCII.GetString(data[infoLength..])
            .Split('\0')
            .Where(n => n.Length > 0)
            .ToList();
        if (names.Count == 0) return false;

        string templateName = names[0].Split(';')[0];
        var memberNames = names.Skip(1).ToList();

        var members = new List<TemplateMember>(header.MemberCount);
        for (int i = 0; i < header.MemberCount; i++)
        {
            string name = i < memberNames.Count ? memberNames[i] : $"member{i}";
            members.Add(new TemplateMember(name, records[i].Type, records[i].TypeInfo, records[i].Offset));
        }

        template = new LogixTemplate(instanceId, templateName, header.StructureSize, members);
        return true;
    }
}
