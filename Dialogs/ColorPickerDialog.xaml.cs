using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services;

namespace OpcUaPlayground.Dialogs;

/// <summary>
/// Picks one tag's color for the playback strip. Three tabs edit the same color: the palette, a
/// hue and saturation wheel with a brightness slider, and three RGB sliders. The R, G and B boxes
/// under them always show the color and can be typed into.
///
/// The palette is the user's, kept in %APPDATA%. Every change to it is written straight away, so
/// it survives even if this dialog is cancelled. The color itself only counts on OK.
/// </summary>
public partial class ColorPickerDialog : Window
{
    private enum Source { Start, Palette, Wheel, Sliders, Boxes }

    private const int WheelSize = 240;

    /// <summary>The tab last used, for the rest of the session.</summary>
    private static string _lastTab = nameof(PaletteTab);

    private readonly ColorPalette _palette;
    private readonly string _paletteStore;
    private readonly Button[] _squares = new Button[ColorPalette.SlotCount];

    private Color _current;
    private int? _selectedSquare;

    // Held apart from the RGB value, so a grey or black doesn't forget the hue it was dragged
    // from and send the wheel's marker to the centre.
    private double _hue, _saturation, _brightness;

    /// <summary>Set while code moves the controls, so their own events don't feed back.</summary>
    private bool _updating;

    private ColorPickerDialog(string address, Color initial, ColorPalette palette, string paletteStore)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _palette = palette;
        _paletteStore = paletteStore;

        TagNameText.Text = address;
        TagNameText.ToolTip = address;
        OldSwatch.Fill = new SolidColorBrush(initial);

        BuildPaletteSquares();
        WheelImage.Source = DrawWheel(WheelSize);

        foreach (var box in new[] { RedBox, GreenBox, BlueBox })
        {
            box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
            DataObject.AddPastingHandler(box, Box_Pasting);
            box.TextChanged += Box_TextChanged;
            box.LostFocus += (_, _) => CommitBox(box);
            box.PreviewKeyDown += Box_PreviewKeyDown;
        }

        WheelSurface.MouseLeftButtonDown += Wheel_MouseLeftButtonDown;
        WheelSurface.MouseMove += Wheel_MouseMove;
        WheelSurface.MouseLeftButtonUp += (_, _) => WheelSurface.ReleaseMouseCapture();

        BrightnessSlider.Maximum = 100;

        SetColor(initial, Source.Start);

        var tab = (RadioButton)FindName(_lastTab);
        tab.IsChecked = true;
    }

    /// <summary>Shows the picker. The chosen color, or null when it was cancelled.</summary>
    public static Color? Pick(Window owner, string address, Color initial, ColorPalette palette, string paletteStore)
    {
        var dialog = new ColorPickerDialog(address, initial, palette, paletteStore) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._current : null;
    }

    // ===================== One color, every control =====================

    /// <summary>
    /// Makes <paramref name="color"/> the current color and brings every control into line with
    /// it, except the one it came from, which already shows it (rewriting a box being typed in
    /// would move the caret).
    /// </summary>
    private void SetColor(Color color, Source source)
    {
        _current = Color.FromRgb(color.R, color.G, color.B);

        // The wheel and brightness slider set these themselves, exactly. From anywhere else they
        // are worked out from RGB, keeping the old hue where RGB no longer says one.
        if (source != Source.Wheel)
        {
            var (h, s, v) = TagColor.ToHsv(_current);
            if (v > 0)
            {
                if (s > 0) _hue = h;
                _saturation = s;
            }
            _brightness = v;
        }

        _updating = true;
        try
        {
            NewSwatch.Fill = new SolidColorBrush(_current);

            if (source != Source.Boxes)
            {
                RedBox.Text = _current.R.ToString();
                GreenBox.Text = _current.G.ToString();
                BlueBox.Text = _current.B.ToString();
            }

            RedSlider.Value = _current.R;
            GreenSlider.Value = _current.G;
            BlueSlider.Value = _current.B;
            RedSlider.Background = Gradient(Color.FromRgb(0, _current.G, _current.B), Color.FromRgb(255, _current.G, _current.B));
            GreenSlider.Background = Gradient(Color.FromRgb(_current.R, 0, _current.B), Color.FromRgb(_current.R, 255, _current.B));
            BlueSlider.Background = Gradient(Color.FromRgb(_current.R, _current.G, 0), Color.FromRgb(_current.R, _current.G, 255));

            BrightnessSlider.Value = Math.Round(_brightness * 100);
            BrightnessSlider.Background = Gradient(Colors.Black, TagColor.FromHsv(_hue, _saturation, 1));
            WheelShade.Opacity = 1 - _brightness;
            PlaceWheelMarker();
        }
        finally
        {
            _updating = false;
        }
    }

    private static Brush Gradient(Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, 0);
        brush.Freeze();
        return brush;
    }

    // ===================== Tabs =====================

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton tab) return;
        _lastTab = tab.Name;
        PalettePage.Visibility = tab == PaletteTab ? Visibility.Visible : Visibility.Collapsed;
        WheelPage.Visibility = tab == WheelTab ? Visibility.Visible : Visibility.Collapsed;
        RgbPage.Visibility = tab == RgbTab ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================== Palette =====================

    private void BuildPaletteSquares()
    {
        for (int i = 0; i < ColorPalette.SlotCount; i++)
        {
            int index = i;
            var square = new Button
            {
                Template = (ControlTemplate)FindResource("BareButton"),
                Margin = new Thickness(3),
                Cursor = Cursors.Hand,
                Focusable = false,
            };
            AutomationProperties.SetName(square, $"Palette square {i + 1}");
            square.Click += (_, _) => PaletteSquare_Click(index);
            _squares[i] = square;
            PaletteGrid.Children.Add(square);
        }

        DrawPaletteSquares();
    }

    /// <summary>A filled square shows its color; an empty one a dashed outline. The selected one
    /// gets a ring.</summary>
    private void DrawPaletteSquares()
    {
        var border = (Brush)FindResource("Brush.Border");
        var accent = (Brush)FindResource("Brush.Accent");

        for (int i = 0; i < ColorPalette.SlotCount; i++)
        {
            var slot = _palette.Slots[i];
            bool selected = _selectedSquare == i;

            var face = new Rectangle
            {
                RadiusX = 3,
                RadiusY = 3,
                Fill = slot is { } color ? new SolidColorBrush(color) : Brushes.Transparent,
                Stroke = slot is null ? border : new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
                StrokeThickness = 1,
                StrokeDashArray = slot is null ? new DoubleCollection { 3, 2 } : null,
            };

            var cell = new Grid { Background = Brushes.Transparent };
            cell.Children.Add(face);
            if (selected)
                cell.Children.Add(new Rectangle
                {
                    RadiusX = 5, RadiusY = 5, Margin = new Thickness(-3),
                    Stroke = accent, StrokeThickness = 2,
                });

            _squares[i].Content = cell;
            _squares[i].ToolTip = slot is { } c ? TagColor.Format(c) : "Empty";
            AutomationProperties.SetItemStatus(_squares[i],
                (slot is { } s ? TagColor.Format(s) : "empty") + (selected ? ", selected" : ""));
        }

        ClearSquareButton.IsEnabled = _selectedSquare is { } at && _palette.Slots[at] is not null;
    }

    /// <summary>Selects the square, and a filled one also becomes the current color.</summary>
    private void PaletteSquare_Click(int index)
    {
        _selectedSquare = index;
        PaletteStatusText.Text = "";
        if (_palette.Slots[index] is { } color) SetColor(color, Source.Palette);
        DrawPaletteSquares();
    }

    private void SaveToPaletteButton_Click(object sender, RoutedEventArgs e)
    {
        var used = _palette.SaveColor(_current, _selectedSquare, out string? problem);
        if (used is null)
        {
            PaletteStatusText.Text = problem;
            return;
        }

        _selectedSquare = used;
        PaletteStatusText.Text = $"Saved {TagColor.Format(_current)} to square {used + 1}.";
        StorePalette();
    }

    private void ClearSquareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSquare is not { } index || !_palette.Clear(index)) return;
        PaletteStatusText.Text = $"Cleared square {index + 1}.";
        StorePalette();
    }

    private void ResetPaletteButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = DarkMessageBox.Confirm(this,
            "Put the palette back as it came, with the 24 presets and two empty rows? Colors you saved to it will be lost.",
            "Reset", "Cancel", cancel: null);
        if (answer != DarkDialogResult.Yes) return;

        _palette.Reset();
        _selectedSquare = null;
        PaletteStatusText.Text = "The palette is back as it came.";
        StorePalette();
    }

    /// <summary>Written on every change. A palette that can't be written still works for now,
    /// and says so.</summary>
    private void StorePalette()
    {
        try
        {
            _palette.Save(_paletteStore);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PaletteStatusText.Text = $"The palette couldn't be saved: {ex.Message}";
        }

        DrawPaletteSquares();
    }

    // ===================== Wheel =====================

    /// <summary>
    /// Hue round the disc and saturation outwards, at full brightness. Drawn once per dialog,
    /// pixel by pixel; brightness is shown by the shade over it rather than by redrawing.
    /// </summary>
    private static BitmapSource DrawWheel(int size)
    {
        var pixels = new byte[size * size * 4];
        double radius = size / 2.0;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x + 0.5 - radius;
                double dy = y + 0.5 - radius;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                // A pixel wide fade at the rim, so the edge isn't jagged.
                double alpha = Math.Clamp(radius - distance, 0, 1);
                if (alpha <= 0) continue;

                var (h, s) = TagColor.FromWheelPoint(dx, dy, radius);
                var color = TagColor.FromHsv(h, s, 1);

                int at = (y * size + x) * 4;
                byte a = (byte)Math.Round(alpha * 255);
                // Premultiplied, which is what Pbgra32 expects.
                pixels[at] = (byte)(color.B * a / 255);
                pixels[at + 1] = (byte)(color.G * a / 255);
                pixels[at + 2] = (byte)(color.R * a / 255);
                pixels[at + 3] = a;
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private void PlaceWheelMarker()
    {
        double radius = WheelSize / 2.0;
        var (x, y) = TagColor.WheelPoint(_hue, _saturation, radius);
        Canvas.SetLeft(WheelMarker, radius + x - WheelMarker.Width / 2);
        Canvas.SetTop(WheelMarker, radius + y - WheelMarker.Height / 2);
    }

    private void Wheel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        WheelSurface.CaptureMouse();

        // Picking a hue with brightness all the way down would change nothing you can see.
        if (_brightness < 0.05) _brightness = 1;

        PickFromWheel(e.GetPosition(WheelSurface));
        e.Handled = true;
    }

    private void Wheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (WheelSurface.IsMouseCaptured) PickFromWheel(e.GetPosition(WheelSurface));
    }

    private void PickFromWheel(Point at)
    {
        double radius = WheelSize / 2.0;
        (_hue, _saturation) = TagColor.FromWheelPoint(at.X - radius, at.Y - radius, radius);
        SetColor(TagColor.FromHsv(_hue, _saturation, _brightness), Source.Wheel);
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        _brightness = e.NewValue / 100;
        SetColor(TagColor.FromHsv(_hue, _saturation, _brightness), Source.Wheel);
    }

    // ===================== RGB sliders and boxes =====================

    private void ChannelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        SetColor(Color.FromRgb((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value), Source.Sliders);
    }

    /// <summary>A box applies as soon as it holds a number from 0 to 255. Anything else waits
    /// until Enter or leaving the box, which clamps it or puts it back.</summary>
    private void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        if (ReadBoxes(clamp: false) is { } color) SetColor(color, Source.Boxes);
    }

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;

        // The first Enter settles the box; the next one is OK.
        if (box.Text != ChannelText(box))
        {
            CommitBox(box);
            box.SelectAll();
            e.Handled = true;
        }
    }

    private void CommitBox(TextBox box)
    {
        if (ReadBoxes(clamp: true) is { } color) SetColor(color, Source.Start);
        else SetColor(_current, Source.Start);
    }

    /// <summary>What a box should say for the current color.</summary>
    private string ChannelText(TextBox box) =>
        (box == RedBox ? _current.R : box == GreenBox ? _current.G : _current.B).ToString();

    private Color? ReadBoxes(bool clamp)
    {
        byte? Read(TextBox box)
        {
            if (!int.TryParse(box.Text, out int value)) return null;
            if (value is < 0 or > 255 && !clamp) return null;
            return (byte)Math.Clamp(value, 0, 255);
        }

        return Read(RedBox) is { } r && Read(GreenBox) is { } g && Read(BlueBox) is { } b
            ? Color.FromRgb(r, g, b)
            : null;
    }

    private static void Box_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(typeof(string)) is not string text || !text.Trim().All(char.IsAsciiDigit))
            e.CancelCommand();
    }

    // ===================== Buttons =====================

    private void DefaultColorButton_Click(object sender, RoutedEventArgs e) => SetColor(TagColor.Default, Source.Start);

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        // A box still being typed in counts, the way it would on leaving it.
        if (Keyboard.FocusedElement is TextBox box) CommitBox(box);
        DialogResult = true;
    }
}
