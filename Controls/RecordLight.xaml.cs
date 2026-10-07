using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpcUaPlayground.Controls;

/// <summary>
/// The record light. Dark red when idle, lit with a halo while a run is being captured, so the
/// state reads from across the room rather than from a word.
/// </summary>
public partial class RecordLight : UserControl
{
    public static readonly DependencyProperty IsLitProperty = DependencyProperty.Register(
        nameof(IsLit), typeof(bool), typeof(RecordLight),
        new PropertyMetadata(false, OnIsLitChanged));

    public RecordLight()
    {
        InitializeComponent();
        Apply(IsLit);
    }

    public bool IsLit
    {
        get => (bool)GetValue(IsLitProperty);
        set => SetValue(IsLitProperty, value);
    }

    private static void OnIsLitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((RecordLight)d).Apply((bool)e.NewValue);

    private void Apply(bool lit)
    {
        var bright = (SolidColorBrush)FindResource("Brush.StatusFault");
        var deep = (SolidColorBrush)FindResource("Brush.StatusFaultDeep");

        // Lit from the upper left, same as the connection light, so the two read as one family.
        Lamp.Fill = lit
            ? new RadialGradientBrush(bright.Color, deep.Color)
            {
                GradientOrigin = new Point(0.35, 0.32),
                Center = new Point(0.45, 0.45),
                RadiusX = 0.7,
                RadiusY = 0.7,
            }
            : deep;

        Halo.Fill = bright;
        Halo.Opacity = lit ? 0.3 : 0;
    }
}
