using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpcUaPlayground.Models;

namespace OpcUaPlayground.Controls;

/// <summary>
/// The connection light. Red is nothing at the address, amber is something there but no controller
/// confirmed at the slot, green is a routed identity read that reached a controller.
/// </summary>
public partial class StatusLight : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(PlcConnectionState), typeof(StatusLight),
        new PropertyMetadata(PlcConnectionState.Disconnected, OnStateChanged));

    public StatusLight()
    {
        InitializeComponent();
        Apply(State);
    }

    public PlcConnectionState State
    {
        get => (PlcConnectionState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StatusLight)d).Apply((PlcConnectionState)e.NewValue);

    private void Apply(PlcConnectionState state)
    {
        (string bright, string deep) = state switch
        {
            PlcConnectionState.Connected => ("Brush.StatusOk", "Brush.StatusOkDeep"),
            PlcConnectionState.Reachable => ("Brush.StatusWarn", "Brush.StatusWarnDeep"),
            _ => ("Brush.StatusFault", "Brush.StatusFaultDeep"),
        };

        var lit = (SolidColorBrush)FindResource(bright);
        var shadow = (SolidColorBrush)FindResource(deep);

        // Light the lamp from its upper left so it reads as a physical indicator rather than a dot.
        Lamp.Fill = new RadialGradientBrush(lit.Color, shadow.Color)
        {
            GradientOrigin = new Point(0.35, 0.32),
            Center = new Point(0.45, 0.45),
            RadiusX = 0.7,
            RadiusY = 0.7,
        };
        Halo.Fill = lit;
    }
}
