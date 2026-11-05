using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace OpcUaPlayground.Models;

/// <summary>
/// A tag's color on the playback strip, and the arithmetic the color picker needs. No UI, so the
/// self test can check it.
///
/// Colors are opaque RGB. The strip already uses opacity to show how busy a moment was, so a tag's
/// own color has no alpha to fight it.
/// </summary>
public static class TagColor
{
    /// <summary>The strip's blue, the same as <c>Brush.Accent</c>. Every tag starts with it.</summary>
    public static readonly Color Default = Color.FromRgb(0x33, 0x99, 0xFF);

    /// <summary>Reads "#RRGGBB", in either case. Anything else, including a short form or an
    /// alpha channel, is refused rather than guessed at.</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = Default;
        if (text is null) return false;

        text = text.Trim();
        if (text.Length != 7 || text[0] != '#') return false;
        if (!int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
            return false;

        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    /// <summary>"#RRGGBB", upper case, which is how the file stores it.</summary>
    public static string Format(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static bool SameRgb(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;

    /// <summary>Hue in degrees (0 to under 360), saturation and value from 0 to 1. A grey has no
    /// hue and reports 0.</summary>
    public static (double H, double S, double V) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == r) h = 60 * ((g - b) / delta % 6);
            else if (max == g) h = 60 * ((b - r) / delta + 2);
            else h = 60 * ((r - g) / delta + 4);
        }
        if (h < 0) h += 360;

        return (h, max == 0 ? 0 : delta / max, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return Color.FromRgb(Channel(r + m), Channel(g + m), Channel(b + m));
    }

    private static byte Channel(double unit) => (byte)Math.Clamp(Math.Round(unit * 255), 0, 255);

    /// <summary>
    /// Where a hue and saturation sit on the color wheel, measured from its centre with y pointing
    /// down the screen. Red is to the right and hue runs anticlockwise, yellow towards the top
    /// right, as on the Mac picker the wheel is modelled on. Saturation is the distance out.
    /// </summary>
    public static (double X, double Y) WheelPoint(double h, double s, double radius)
    {
        double angle = h * Math.PI / 180;
        double distance = Math.Clamp(s, 0, 1) * radius;
        return (Math.Cos(angle) * distance, -Math.Sin(angle) * distance);
    }

    /// <summary>The hue and saturation under a point on the wheel. A point past the rim counts as
    /// the rim, so dragging off the edge keeps the hue and goes fully saturated.</summary>
    public static (double H, double S) FromWheelPoint(double x, double y, double radius)
    {
        if (radius <= 0) return (0, 0);

        double distance = Math.Sqrt(x * x + y * y);
        double h = Math.Atan2(-y, x) * 180 / Math.PI;
        if (h < 0) h += 360;
        return (distance == 0 ? 0 : h, Math.Min(distance / radius, 1));
    }

    /// <summary>
    /// The colors one line of the strip is drawn in: one band per distinct color among the tags
    /// that changed there, in the order each color first appears in the table. When every tag has
    /// the same color that is a single band, which is the strip as it always looked.
    /// </summary>
    public static IReadOnlyList<Color> BandColors(IEnumerable<int> tags, Func<int, Color> colorOf)
    {
        var bands = new List<Color>();
        foreach (int tag in tags)
        {
            var color = colorOf(tag);
            if (!bands.Any(b => SameRgb(b, color))) bands.Add(color);
        }

        return bands;
    }
}

/// <summary>
/// Which tags have been given a color. One color per tag name for the whole tag set, so a tag
/// looks the same in the live table and in every recording of it. Names are matched ignoring
/// case, as Logix matches them. Only colors other than the default are kept.
/// </summary>
public sealed class TagColorMap
{
    private readonly Dictionary<string, Color> _colors = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _colors.Count;

    public Color Get(string address) =>
        _colors.TryGetValue(address.Trim(), out var color) ? color : TagColor.Default;

    public bool IsSet(string address) => _colors.ContainsKey(address.Trim());

    /// <summary>Gives a tag a color. The default, or null, takes its color away. True when that
    /// changed anything.</summary>
    public bool Set(string address, Color? color)
    {
        address = address.Trim();
        if (address.Length == 0) return false;

        bool had = _colors.TryGetValue(address, out var old);

        if (color is not { } wanted || TagColor.SameRgb(wanted, TagColor.Default))
            return _colors.Remove(address);

        wanted = Color.FromRgb(wanted.R, wanted.G, wanted.B);
        if (had && TagColor.SameRgb(old, wanted)) return false;
        _colors[address] = wanted;
        return true;
    }

    public void Clear() => _colors.Clear();

    /// <summary>
    /// What a tag set file stores, or null when there is nothing to store, so the file leaves the
    /// field out. With <paramref name="keep"/>, colors of tags that are in neither the table nor
    /// any recording are dropped, so a file doesn't collect colors for tags it no longer has.
    /// </summary>
    public Dictionary<string, string>? ToStored(IEnumerable<string>? keep = null)
    {
        var wanted = keep?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stored = _colors
            .Where(c => wanted is null || wanted.Contains(c.Key))
            .OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(c => c.Key, c => TagColor.Format(c.Value));

        return stored.Count == 0 ? null : stored;
    }

    /// <summary>Reads what a file stored. A blank name or a value that is not "#RRGGBB" is
    /// skipped rather than refusing the file over a color.</summary>
    public static TagColorMap FromStored(IReadOnlyDictionary<string, string>? stored)
    {
        var map = new TagColorMap();
        if (stored is null) return map;

        foreach (var (address, text) in stored)
            if (address is not null && TagColor.TryParse(text, out var color))
                map.Set(address, color);

        return map;
    }
}

/// <summary>
/// Reads the tag set's color map without letting one odd value refuse the whole file: anything
/// that is not a string is skipped, and a map that is not an object at all reads as empty.
/// Writes it as a plain object.
/// </summary>
public sealed class LenientStringMapConverter : JsonConverter<Dictionary<string, string>>
{
    public override Dictionary<string, string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return map;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString() ?? "";
            reader.Read();
            if (reader.TokenType == JsonTokenType.String) map[name] = reader.GetString() ?? "";
            else reader.Skip();
        }

        return map;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, text) in value) writer.WriteString(name, text);
        writer.WriteEndObject();
    }
}
