using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpcUaPlayground.Models;

/// <summary>
/// One tag as a recording captured it. The datatype and description travel with the run so that
/// playing it back fills the same columns the live table shows, rather than leaving two of them
/// empty or borrowing from a table that has since been edited.
/// </summary>
public sealed class RecordedTag
{
    public string Address { get; set; } = "";

    public string DataType { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>The poll interval this tag had while the run was captured.</summary>
    public int ScanRateMs { get; set; }
}

/// <summary>
/// Reads a recorded tag written either as an object or as a bare address string.
///
/// The first recordings stored addresses only. Rather than move the document version and refuse
/// those files, this accepts both shapes and writes the new one.
/// </summary>
public sealed class RecordedTagConverter : JsonConverter<RecordedTag>
{
    public override RecordedTag Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new RecordedTag { Address = reader.GetString() ?? "" };

        // Read the object into a document and map it by hand. Handing it back to the serializer
        // would land in this same converter and recurse until the stack ran out.
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;

        return new RecordedTag
        {
            Address = Text(element, "address") ?? "",
            DataType = Text(element, "dataType") ?? "",
            Description = Text(element, "description"),
            ScanRateMs = Number(element, "scanRateMs"),
        };
    }

    public override void Write(Utf8JsonWriter writer, RecordedTag value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("address", value.Address);
        writer.WriteString("dataType", value.DataType);
        if (value.Description is { Length: > 0 }) writer.WriteString("description", value.Description);
        writer.WriteNumber("scanRateMs", value.ScanRateMs);
        writer.WriteEndObject();
    }

    /// <summary>Property lookup that ignores case, to match the reader's own tolerance.</summary>
    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } found ? found.GetString() : null;

    private static int Number(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } found && found.TryGetInt32(out int value)
            ? value
            : 0;
}
