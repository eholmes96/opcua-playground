using System.IO;
using System.Windows;
using System.Windows.Media;
using OpcUaPlayground.Dialogs;
using OpcUaPlayground.Models;

namespace OpcUaPlayground;

/// <summary>
/// Tag colors: the swatch at the left of each row, and what it changes. A color belongs to a tag
/// name for the whole tag set, so it shows on the live row, on the playback row and on the strip
/// for every recording of that tag. It is saved in the tag set file like a scan rate: changing one
/// marks the set unsaved.
/// </summary>
public partial class MainWindow
{
    private TagColorMap _tagColors = new();

    /// <summary>Read from disk the first time the picker opens, then kept.</summary>
    private ColorPalette? _palette;

    /// <summary>One frozen brush per color, so redrawing the strip doesn't build thousands.</summary>
    private readonly Dictionary<Color, SolidColorBrush> _stripBrushes = new();

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MonitoredTag tag }) return;

        _palette ??= ColorPalette.Load(ColorPalette.DefaultStorePath);

        var picked = ColorPickerDialog.Pick(this, tag.Address, tag.StripColor, _palette, ColorPalette.DefaultStorePath);
        if (picked is { } color) ApplyTagColor(tag.Address, color);
    }

    /// <summary>Gives a tag its color everywhere it is shown, and marks the set unsaved.</summary>
    private void ApplyTagColor(string address, Color color)
    {
        if (!_tagColors.Set(address, color)) return;

        var shown = _tagColors.Get(address);
        foreach (var row in _monitor.Tags.Concat(_playbackRows))
            if (string.Equals(row.Address, address, StringComparison.OrdinalIgnoreCase))
                row.StripColor = shown;

        MarkDirty();
        DrawDensity();
    }

    private Brush StripBrush(Color color)
    {
        if (!_stripBrushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            brush.Freeze();
            _stripBrushes[color] = brush;
        }

        return brush;
    }
}
