using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace OpcUaPlayground.Models;

/// <summary>
/// The color picker's palette: a 6 by 6 grid of squares. The first four rows start as presets
/// and the last two start empty, for the user's own colors.
///
/// Kept in the user's application data folder rather than in a tag set, because it is the
/// user's palette on this computer, not part of any one set of tags. Any square can be saved over
/// or cleared, and Reset puts it back as it shipped.
///
/// UI-agnostic so the self test can exercise it without a window.
/// </summary>
public sealed class ColorPalette
{
    public const int Columns = 6;
    public const int Rows = 6;
    public const int SlotCount = Columns * Rows;

    /// <summary>
    /// Chosen to read well against the dark strip: the default blue first, then reds, oranges,
    /// yellows, greens, teals, purples, pinks and two greys. Just colors, not data from anywhere.
    /// </summary>
    public static readonly IReadOnlyList<Color> Presets = new[]
    {
        "#3399FF", "#F85149", "#F0883E", "#E3B341", "#3FB950", "#39C5CF",
        "#A371F7", "#F778BA", "#79C0FF", "#FF7B72", "#FFA657", "#F2CC60",
        "#56D364", "#56D4DD", "#D2A8FF", "#FFADDA", "#1F6FEB", "#DA3633",
        "#BD561D", "#2EA043", "#8957E5", "#BF4B8A", "#8B949E", "#E8E8E8",
    }.Select(text => TagColor.TryParse(text, out var color) ? color : TagColor.Default).ToArray();

    private readonly Color?[] _slots = new Color?[SlotCount];

    private ColorPalette() { }

    /// <summary>Row by row, top left first. Null is an empty square.</summary>
    public IReadOnlyList<Color?> Slots => _slots;

    /// <summary>The palette as it ships: the presets, then two empty rows.</summary>
    public static ColorPalette Defaults()
    {
        var palette = new ColorPalette();
        palette.Reset();
        return palette;
    }

    public void Reset()
    {
        for (int i = 0; i < SlotCount; i++) _slots[i] = i < Presets.Count ? Presets[i] : null;
    }

    /// <summary>
    /// Puts a color on the palette: in the selected square if that one is empty, otherwise in the
    /// first empty square, or once there are none, over the selected square. Returns the square
    /// used, or null with a reason when every square is full and none is selected.
    /// </summary>
    public int? SaveColor(Color color, int? selected, out string? problem)
    {
        problem = null;
        color = Color.FromRgb(color.R, color.G, color.B);

        bool selectedValid = selected is >= 0 and < SlotCount;
        int empty = selectedValid && _slots[selected!.Value] is null
            ? selected.Value
            : Array.FindIndex(_slots, s => s is null);
        if (empty >= 0)
        {
            _slots[empty] = color;
            return empty;
        }

        if (selectedValid)
        {
            _slots[selected!.Value] = color;
            return selected.Value;
        }

        problem = "The palette is full. Select a square to replace, or clear one.";
        return null;
    }

    /// <summary>Empties a square. False when it was empty already or out of range.</summary>
    public bool Clear(int index)
    {
        if (index < 0 || index >= SlotCount || _slots[index] is null) return false;
        _slots[index] = null;
        return true;
    }

    // ===================== On disk =====================

    /// <summary>%APPDATA%\OpcUaPlayground\color-palette.json</summary>
    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpcUaPlayground", "color-palette.json");

    private sealed class Stored
    {
        public int Version { get; set; } = 1;

        /// <summary>"#RRGGBB" per square, row by row, null for an empty one.</summary>
        public List<string?>? Slots { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads the palette back. A missing or unreadable file is the palette as it ships, since a
    /// lost palette must never stop the picker opening. A square that does not read as "#RRGGBB"
    /// is empty, and a list of the wrong length is cut or padded with empty squares.
    /// </summary>
    public static ColorPalette Load(string storePath)
    {
        Stored? stored;
        try
        {
            if (!File.Exists(storePath)) return Defaults();
            stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(storePath), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Defaults();
        }

        if (stored?.Slots is not { } slots) return Defaults();

        var palette = new ColorPalette();
        for (int i = 0; i < SlotCount && i < slots.Count; i++)
            palette._slots[i] = TagColor.TryParse(slots[i], out var color) ? color : null;

        return palette;
    }

    /// <summary>Written through a temporary file, like a tag set, so a failed write keeps the old palette.</summary>
    public void Save(string storePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        string temp = storePath + ".saving";
        File.WriteAllText(temp, JsonSerializer.Serialize(
            new Stored { Slots = _slots.Select(s => s is { } c ? TagColor.Format(c) : null).ToList() }, Json));
        File.Move(temp, storePath, overwrite: true);
    }
}
