using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OpcUaPlayground;

/// <summary>
/// The movement between Current and Playback.
///
/// Two things slide. The pair of mode panels travel sideways through a clipping host, the outgoing
/// one leaving and the incoming one arriving in the same direction, so the change reads as one
/// movement rather than two unrelated ones. And the scrub bar rises from below the window edge.
///
/// Nothing here changes what the app does. It exists so the eye can follow what moved.
/// </summary>
public partial class MainWindow
{
    /// <summary>Long enough to be followed, short enough not to be waited on.</summary>
    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(220));

    private static readonly IEasingFunction SlideEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Measured once. The bar's contents are fixed height, so it does not change.</summary>
    private double _scrubBarHeight;

    private void InitialiseModeTransition()
    {
        // The resting offset of the off-screen panel is the host's width, which is not known until
        // the first layout pass and changes if the window font or scaling does.
        ModePanelHost.SizeChanged += (_, _) => PositionModePanels(IsPlaybackMode, animate: false);
    }

    // ===================== The mode panels =====================

    /// <summary>
    /// Puts the panels where the mode says they belong. Going to Playback, both travel right: the
    /// recording panel leaves to the right and the playback panel arrives from the left. Going back
    /// to Current, both travel left.
    /// </summary>
    private void PositionModePanels(bool playback, bool animate)
    {
        // Before the first layout pass there is no width to slide across, so the panels are simply
        // placed. Any animation then would be from nowhere to nowhere.
        double width = ModePanelHost.ActualWidth;
        if (width <= 0)
        {
            animate = false;
            width = 600;
        }

        Slide(RecordingShift, playback ? width : 0, animate);
        Slide(PlaybackShift, playback ? 0 : -width, animate);

        // The panel that is out of view keeps its size so the bar does not move, but must not be
        // reachable by Tab or by a click that lands on the clipped edge.
        RecordingPanel.IsEnabled = !playback;
        PlaybackPanel.IsEnabled = playback;
    }

    private static void Slide(TranslateTransform transform, double to, bool animate)
    {
        if (!animate)
        {
            // A finished animation still holds the property, so it has to be released before the
            // value can be set by hand.
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = to;
            return;
        }

        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(to, SlideDuration) { EasingFunction = SlideEase });
    }

    // ===================== The scrub bar =====================

    /// <summary>
    /// Raises or lowers the scrub bar by animating the height of the strip it sits in. Animating
    /// the height rather than a transform is what lets a hidden bar take no space, so the tag tree
    /// and the table get it back.
    /// </summary>
    private void SlideScrubBar(bool show, bool animate)
    {
        // It has to be visible to be measured, and it has to be measured before it can be raised.
        if (show) ScrubBar.Visibility = Visibility.Visible;

        double target = show ? ScrubBarHeight() : 0;

        if (!animate)
        {
            ScrubBarHost.BeginAnimation(HeightProperty, null);
            ScrubBarHost.Height = target;
            if (!show) ScrubBar.Visibility = Visibility.Collapsed;
            return;
        }

        var animation = new DoubleAnimation(target, SlideDuration) { EasingFunction = SlideEase };

        if (!show)
        {
            // Checked at the end rather than assumed: the user can toggle back mid-slide, and this
            // handler still runs.
            animation.Completed += (_, _) =>
            {
                if (!IsPlaybackMode) ScrubBar.Visibility = Visibility.Collapsed;
            };
        }

        ScrubBarHost.BeginAnimation(HeightProperty, animation);
    }

    private double ScrubBarHeight()
    {
        if (_scrubBarHeight > 0) return _scrubBarHeight;

        double width = ScrubBarHost.ActualWidth > 0 ? ScrubBarHost.ActualWidth : ActualWidth;
        ScrubBar.Measure(new Size(width, double.PositiveInfinity));
        _scrubBarHeight = ScrubBar.DesiredSize.Height;

        // A measure this early can come back empty. Better a plausible height than a bar that
        // never appears.
        if (_scrubBarHeight <= 0) _scrubBarHeight = 96;
        return _scrubBarHeight;
    }
}
