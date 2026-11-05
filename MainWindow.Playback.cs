using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using OpcUaPlayground.Dialogs;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services;

namespace OpcUaPlayground;

/// <summary>
/// Playing a recorded run back: the run picker, the transport, and the scrub bar along the bottom.
///
/// The table is fed from a second row collection rather than the live one. That keeps the working
/// tag set untouched, so a Save while a run is on screen still writes your tags and not the
/// recording's, and a run whose tags no longer match the table plays without any reconciliation.
///
/// None of this touches the network. A run can be read on a machine that has never seen the PLC.
/// </summary>
public partial class MainWindow
{
    /// <summary>Roughly three pixels per bucket in the activity strip.</summary>
    private const double DensityBucketWidth = 3;

    private TagPlayback? _playback;

    private readonly ObservableCollection<MonitoredTag> _playbackRows = new();

    private readonly DispatcherTimer _playTick = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch _playClock = new();

    /// <summary>Where the bar sat when play was pressed, which is what elapsed time is added to.</summary>
    private int _playFromMs;

    private int _positionMs;
    private double _speed = 1;
    private bool _isPlaying;

    /// <summary>Set while code is moving the slider, so its own event does not seek again.</summary>
    private bool _seeking;

    /// <summary>The bucket count the strip was last drawn with, so a line's tooltip can name its tags.</summary>
    private int _densityBuckets;

    /// <summary>Longest list a line's tooltip shows before "and N more".</summary>
    private const int MaxTooltipTags = 25;

    private void InitialisePlayback()
    {
        _playTick.Tick += (_, _) => OnPlayTick();
        PositionTextBox.PreviewKeyDown += PositionTextBox_PreviewKeyDown;
        PreviewKeyDown += Playback_PreviewKeyDown;
        Closed += (_, _) => PausePlayback();

        // The strip and the slider are one surface for scrubbing: a press anywhere on either puts
        // the playhead there, and it follows the pointer until the button comes up.
        foreach (var surface in new UIElement[] { ScrubStripArea, ScrubSlider })
        {
            surface.PreviewMouseLeftButtonDown += ScrubSurface_PreviewMouseLeftButtonDown;
            surface.PreviewMouseMove += ScrubSurface_PreviewMouseMove;
            surface.PreviewMouseLeftButtonUp += ScrubSurface_PreviewMouseLeftButtonUp;
        }
    }

    // ===================== Entering and leaving =====================

    private void EnterPlayback(bool animate)
    {
        // A poller running behind a historical table has nothing to say.
        _monitor.Stop();

        MonitorGrid.ItemsSource = _playbackRows;
        MonitorGrid.IsReadOnly = true;
        SlideScrubBar(show: true, animate);

        FillRunPicker();
        UpdateButtons();
    }

    private void LeavePlayback(bool animate)
    {
        PausePlayback();

        // Asked about before the switch was allowed to move, so anything left here is settled.
        ClearCrop();

        MonitorGrid.ItemsSource = _monitor.Tags;
        MonitorGrid.IsReadOnly = false;
        SlideScrubBar(show: false, animate);

        UpdateButtons();
    }

    /// <summary>
    /// Rebuilt from scratch each time, because the list of runs is a plain List and a rebind is the
    /// only thing that makes the picker notice a run that was added or a file that was loaded.
    /// </summary>
    private void FillRunPicker(RecordedSession? select = null)
    {
        RunCombo.ItemsSource = null;
        RunCombo.ItemsSource = _recordings;

        var wanted = select is not null && _recordings.Contains(select)
            ? select
            : _recordings.Count > 0 ? _recordings[^1] : null;

        RunCombo.SelectedItem = wanted;

        // Selecting nothing raises no event, so an empty file needs telling directly.
        if (wanted is null) SelectRun(null);
    }

    private void RunCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_revertingRun) return;

        // Another run taking the screen would throw an unsaved crop away, so that is asked first.
        // Keeping it puts the picker back on the run the crop came from.
        if (HasPendingCrop && !ConfirmDropCrop(restore: false))
        {
            _revertingRun = true;
            RunCombo.SelectedItem = _cropSource;
            _revertingRun = false;
            return;
        }

        SelectRun(RunCombo.SelectedItem as RecordedSession);
    }

    private void SelectRun(RecordedSession? session)
    {
        ClearCrop();
        ShowRun(session, 0);
    }

    /// <summary>
    /// Puts a run on screen: a saved one, or a crop of one. The handles go back to the ends, since
    /// the strip now spans whatever is shown.
    /// </summary>
    private void ShowRun(RecordedSession? session, int seekMs)
    {
        PausePlayback();
        _shownSession = session;
        _playback = session is null ? null : TagPlayback.Load(session);
        ResetCropHandles();

        _playbackRows.Clear();
        if (_playback is not null)
        {
            foreach (var tag in _playback.Tags)
                _playbackRows.Add(new MonitoredTag(tag.Address, tag.DataType, tag.ScanRateMs, 0)
                {
                    Description = tag.Description ?? "",
                    StripColor = _tagColors.Get(tag.Address),
                });
        }

        int duration = _playback?.DurationMs ?? 0;

        _seeking = true;
        ScrubSlider.Minimum = 0;
        // A zero length run would give the slider no range at all, so it gets a nominal one.
        ScrubSlider.Maximum = Math.Max(duration, 1);
        ScrubSlider.SmallChange = Math.Max(duration / 200.0, 1);
        ScrubSlider.LargeChange = Math.Max(duration / 20.0, 1);
        _seeking = false;

        DurationText.Text = $"of {Elapsed(duration)}";
        PlaybackInfoText.Text = DescribeRun();
        UpdatePlaybackEnabled();
        DrawDensity();
        LayoutCrop();
        SeekTo(seekMs);

        // The playback rows are not the collection the summary watches, so it has to be told.
        UpdateTableSummary();
    }

    private string DescribeRun()
    {
        if (_playback is null)
            return _recordings.Count == 0 ? "this tag set holds no recordings" : "no run selected";

        string what = $"{_playback.Tags.Count} tags, {_playback.ChangeCount} changes";
        if (_playback.SkippedLines > 0) what += $", {_playback.SkippedLines} lines unreadable";
        if (HasPendingCrop) what = $"unsaved crop, {what}";
        return what;
    }

    // ===================== Seeking =====================

    /// <summary>
    /// Puts the table at one moment. Every row is resolved by binary search, so this costs the same
    /// wherever in the run it lands and can be called on every mouse move while dragging.
    /// </summary>
    private void SeekTo(int ms)
    {
        if (_playback is null)
        {
            PositionTextBox.Text = Elapsed(0);
            ClockText.Text = "--:--:--.---";
            return;
        }

        _positionMs = Math.Clamp(ms, 0, _playback.DurationMs);

        for (int i = 0; i < _playbackRows.Count; i++)
        {
            var row = _playbackRows[i];

            if (!_playback.TryStateAt(i, _positionMs, out bool isError, out string text))
            {
                row.Error = null;
                row.Value = "";
                continue;
            }

            // Mirrors the live table: an error replaces the value rather than sitting beside it.
            if (isError)
            {
                row.Error = text;
                row.Value = "";
            }
            else
            {
                row.Error = null;
                row.Value = text;
            }
        }

        _seeking = true;
        ScrubSlider.Value = _positionMs;
        _seeking = false;

        // Left alone while it is being typed into, or the caret would jump on every tick.
        if (!PositionTextBox.IsKeyboardFocusWithin) PositionTextBox.Text = Elapsed(_positionMs);
        ClockText.Text = _playback.Recorded.AddMilliseconds(_positionMs).ToString("HH:mm:ss.fff");
    }

    private void ScrubSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seeking) return;

        // Dragging the bar takes over from playing, rather than fighting it.
        PausePlayback();
        SeekTo((int)e.NewValue);
    }

    // ===================== Scrubbing with the mouse =====================

    /// <summary>
    /// A press on the strip or the slider, anywhere but on a crop handle, takes the playhead to the
    /// pointer. The slider's own handling is bypassed: on its own it jumps on a click but only drags
    /// from the knob. Times come from <see cref="MsAt"/>, the same mapping the crop handles use.
    /// </summary>
    private void ScrubSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_playback is null || !ScrubSlider.IsEnabled) return;

        // The crop handles keep their own drag.
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<System.Windows.Controls.Primitives.Thumb>(source) is { } thumb
            && (ReferenceEquals(thumb, CropInHandle) || ReferenceEquals(thumb, CropOutHandle)))
            return;

        var surface = (UIElement)sender;
        if (!surface.CaptureMouse()) return;

        ScrubSlider.Focus();        // out of the time field, so it shows the new position
        PausePlayback();
        SeekTo(MsAt(e.GetPosition(CropLayer).X));
        e.Handled = true;
    }

    private void ScrubSurface_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not UIElement { IsMouseCaptured: true }) return;
        SeekTo(MsAt(e.GetPosition(CropLayer).X));
        e.Handled = true;
    }

    private void ScrubSurface_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement { IsMouseCaptured: true } surface) return;
        surface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void PreviousChangeButton_Click(object sender, RoutedEventArgs e) => StepChange(-1);

    private void NextChangeButton_Click(object sender, RoutedEventArgs e) => StepChange(1);

    /// <summary>
    /// Jumps to the exact instant of the neighbouring change. This is the precise control: you
    /// never land between two changes wondering whether you missed one.
    /// </summary>
    private void StepChange(int direction)
    {
        if (_playback is null) return;

        PausePlayback();
        SeekTo(direction < 0
            ? _playback.PreviousChangeTime(_positionMs)
            : _playback.NextChangeTime(_positionMs));
    }

    // ===================== Playing =====================

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_playback is null) return;

        if (_isPlaying) { PausePlayback(); return; }

        // Playing from the end would sit there doing nothing, so it starts over.
        if (_positionMs >= _playback.DurationMs) SeekTo(0);

        _playFromMs = _positionMs;
        _playClock.Restart();
        _isPlaying = true;
        _playTick.Start();
        UpdatePlaybackEnabled();
    }

    private void PausePlayback()
    {
        if (!_isPlaying) return;

        _playTick.Stop();
        _playClock.Stop();
        _isPlaying = false;
        UpdatePlaybackEnabled();
    }

    private void OnPlayTick()
    {
        if (_playback is null) { PausePlayback(); return; }

        int position = _playFromMs + (int)(_playClock.Elapsed.TotalMilliseconds * _speed);

        if (position >= _playback.DurationMs)
        {
            SeekTo(_playback.DurationMs);
            PausePlayback();
            return;
        }

        SeekTo(position);
    }

    private void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedCombo.SelectedItem is not ComboBoxItem item
            || !double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double speed))
            return;

        _speed = speed;

        // Rebase, so changing speed mid-run does not make the position jump.
        if (_isPlaying)
        {
            _playFromMs = _positionMs;
            _playClock.Restart();
        }
    }

    // ===================== Typing a time =====================

    /// <summary>
    /// Enter jumps to the typed time. [ and ] jump there too and then put a crop handle on it, so a
    /// cut can be typed exactly: 1.5 then [, 3.2 then ]. Neither bracket means anything in a time,
    /// so taking them here costs nothing.
    /// </summary>
    private void PositionTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool? cropEdge = e.Key switch
        {
            Key.OemOpenBrackets => true,
            Key.OemCloseBrackets => false,
            _ => null,
        };

        if (e.Key != Key.Enter && cropEdge is null) return;
        e.Handled = true;

        if (ParsePosition(PositionTextBox.Text) is { } ms)
        {
            PausePlayback();
            SeekTo(ms);
        }

        PositionTextBox.Text = Elapsed(_positionMs);
        if (cropEdge is { } start) SetCropEdgeAtPlayhead(start);
    }

    /// <summary>Accepts "12", "12.4", "0:12.400" and "1:02.5". Null when it makes no sense.</summary>
    internal static int? ParsePosition(string text)
    {
        text = text.Trim().Replace(',', '.');
        if (text.Length == 0) return null;

        int minutes = 0;
        int colon = text.IndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(text[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes))
                return null;
            text = text[(colon + 1)..];
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            return null;

        double total = minutes * 60_000.0 + seconds * 1000.0;
        if (total < 0 || total > int.MaxValue) return null;
        return (int)Math.Round(total);
    }

    /// <summary>Milliseconds as m:ss.fff, which is what the position field shows and parses.</summary>
    internal static string Elapsed(int ms)
    {
        if (ms < 0) ms = 0;
        return $"{ms / 60_000}:{ms / 1000 % 60:00}.{ms % 1000:000}";
    }

    // ===================== The activity strip =====================

    private void DensityStrip_SizeChanged(object sender, SizeChangedEventArgs e) => DrawDensity();

    /// <summary>
    /// One line per horizontal bucket, brighter where more changed. Bucketing by pixel is what
    /// keeps this cheap on a run holding hundreds of thousands of changes.
    ///
    /// A line is drawn in the colors of the tags behind it: one band per color, stacked top to
    /// bottom in table order, all at the line's brightness. While every tag has the default blue
    /// that is one band, the strip as it always looked.
    ///
    /// The count is square rooted before it becomes brightness, or one busy instant would flatten
    /// everything else to invisible.
    /// </summary>
    private void DrawDensity()
    {
        DensityStrip.Children.Clear();

        double width = DensityStrip.ActualWidth;
        if (_playback is null || width < 1 || _playback.ChangeCount == 0) return;

        int buckets = Math.Max(1, (int)(width / DensityBucketWidth));
        _densityBuckets = buckets;
        var counts = _playback.Density(buckets);
        int peak = counts.Max();
        if (peak == 0) return;

        var tagsByBucket = _playback.TagsByBucket(buckets);
        var colors = _playback.Tags.Select(t => _tagColors.Get(t.Address)).ToArray();

        double bucketWidth = width / buckets;
        double height = DensityStrip.Height;

        for (int i = 0; i < buckets; i++)
        {
            if (counts[i] == 0) continue;

            double opacity = 0.18 + 0.82 * Math.Sqrt(counts[i] / (double)peak);
            var bands = TagColor.BandColors(tagsByBucket[i], tag => colors[tag]);
            if (bands.Count == 0) bands = new[] { TagColor.Default };
            double bandHeight = height / bands.Count;

            for (int b = 0; b < bands.Count; b++)
            {
                var bar = new Rectangle
                {
                    Width = bucketWidth + 0.5,      // a hair of overlap, so there are no seams
                    Height = bandHeight,
                    Fill = StripBrush(bands[b]),
                    Opacity = opacity,
                    Tag = i,
                    ToolTip = "",       // filled in when it opens; a tooltip has to exist to open
                };
                bar.ToolTipOpening += DensityBar_ToolTipOpening;

                Canvas.SetLeft(bar, i * bucketWidth);
                Canvas.SetTop(bar, b * bandHeight);
                DensityStrip.Children.Add(bar);
            }
        }
    }

    /// <summary>
    /// A line's tooltip lists the tags whose changes drew it, one per line, in table order. Worked
    /// out when it opens rather than for every line up front.
    /// </summary>
    private void DensityBar_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not Rectangle { Tag: int bucket } bar || _playback is null) { e.Handled = true; return; }

        var tags = _playback.TagsInBucket(bucket, _densityBuckets);
        if (tags.Count == 0) { e.Handled = true; return; }

        var lines = tags.Take(MaxTooltipTags).Select(i => _playback.Tags[i].Address).ToList();
        if (tags.Count > MaxTooltipTags) lines.Add($"and {tags.Count - MaxTooltipTags} more");
        bar.ToolTip = string.Join(Environment.NewLine, lines);
    }

    // ===================== Keys =====================

    /// <summary>
    /// Space plays, the arrows step by change, Home and End jump to the ends, and [ and ] put the
    /// crop handles at the playhead.
    ///
    /// Typing wins: a text box or a dropdown keeps its keys, and so does the tag tree, which needs
    /// the arrows to open folders.
    /// </summary>
    private void Playback_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsPlaybackMode || _playback is null) return;

        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return;
        if (Keyboard.FocusedElement is DependencyObject focused
            && FindAncestor<TreeView>(focused) is not null) return;

        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: StepChange(-1); break;
            case Key.Right: StepChange(1); break;
            case Key.Home: PausePlayback(); SeekTo(0); break;
            case Key.End: PausePlayback(); SeekTo(_playback.DurationMs); break;
            case Key.OemOpenBrackets: PausePlayback(); SetCropEdgeAtPlayhead(start: true); break;
            case Key.OemCloseBrackets: PausePlayback(); SetCropEdgeAtPlayhead(start: false); break;
            default: return;
        }

        e.Handled = true;
    }

    // ===================== Managing runs =====================

    private void RenameRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (RunCombo.SelectedItem is not RecordedSession session) return;

        string? chosen = DarkPromptBox.Ask(
            this,
            $"New name for \"{session.Name}\", recorded {session.Recorded:yyyy-MM-dd HH:mm:ss}:",
            session.Name,
            candidate => NameProblem(candidate, except: session));

        if (chosen is null || chosen.Equals(session.Name, StringComparison.Ordinal)) return;

        string was = session.Name;
        session.Name = chosen;

        FillRunPicker(session);
        ResetRecordingName();      // the default name follows the names in the file
        PersistRecordings($"\"{was}\" renamed to \"{chosen}\"");
    }

    private void DeleteRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (RunCombo.SelectedItem is not RecordedSession session) return;

        var answer = DarkMessageBox.Confirm(
            this,
            "Delete this recording?"
            + Environment.NewLine + Environment.NewLine
            + $"{session.Name}, recorded {session.Recorded:yyyy-MM-dd HH:mm:ss}, "
            + $"{session.Changes.Count} changes"
            + Environment.NewLine + Environment.NewLine
            + "This cannot be undone.",
            "Delete",
            "Cancel",
            cancel: null);

        if (answer != DarkDialogResult.Yes) return;

        _recordings.Remove(session);
        FillRunPicker();
        ResetRecordingName();
        RecordStatusText.Text = "";
        PersistRecordings($"\"{session.Name}\" deleted");
    }

    private void ExportRunButton_Click(object sender, RoutedEventArgs e)
    {
        // Whatever is on screen, an unsaved crop included. Its recorded time is the cut's, so the
        // suggested file name differs from the full run's.
        if (_shownSession is not { } session || _playback is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export recording to CSV",
            Filter = RecordingCsv.DialogFilter,
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = RecordingCsv.SuggestFileName(session),
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // Streamed rather than built in memory, since a busy run holds a lot of instants.
            // System.IO is spelled out because this file already uses System.Windows.Shapes.Path
            // for the activity strip, and the two Paths collide.
            using var writer = new System.IO.StreamWriter(dialog.FileName);
            RecordingCsv.Write(writer, _playback);
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(this,
                $"Could not write that file.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
            return;
        }

        int rows = _playback.ChangeTimes.Count;
        RecordStatusText.Text =
            $"exported {_playback.Tags.Count} tags over about {rows} instants "
            + $"to {System.IO.Path.GetFileName(dialog.FileName)}";
    }

    /// <summary>
    /// Writes the runs into the file when the tag set has one, and marks the document dirty when it
    /// does not. Deleting a run has to reach the disk, or it comes back the next time the file is
    /// opened.
    ///
    /// Only the runs are written. The table on disk stays as last saved, and the dirty flag is left
    /// alone, so an unsaved table edit is still offered as Save or Discard later instead of being
    /// saved behind the user's back because a recording happened to finish.
    /// </summary>
    private void PersistRecordings(string what)
    {
        if (_tagSetPath is not null && WriteRecordingsTo(_tagSetPath))
        {
            RecordStatusText.Text = $"{what}, saved";
            return;
        }

        MarkDirty();
        RecordStatusText.Text = $"{what}, not written yet. Use File > Save.";
    }

    private bool WriteRecordingsTo(string path)
    {
        try
        {
            TagSetFile.SaveRecordings(path, _recordings);
            return true;
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(this,
                $"Could not save the recordings.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
            return false;
        }
    }

    // ===================== Housekeeping =====================

    private void UpdatePlaybackEnabled()
    {
        bool ready = _playback is not null && _playback.DurationMs > 0;
        bool hasRun = RunCombo.SelectedItem is RecordedSession;
        bool pending = HasPendingCrop;

        // Renaming or deleting the run under an unsaved crop would leave the crop pointing at
        // something that has changed. Save or undo it first.
        RenameRunButton.IsEnabled = hasRun && !pending;
        DeleteRunButton.IsEnabled = hasRun && !pending;
        ExportRunButton.IsEnabled = hasRun && _playback is not null;

        bool moved = ready && HandlesMoved;
        CropButton.IsEnabled = moved;
        OverwriteRunButton.IsEnabled = hasRun && (moved || pending);
        SaveNewRunButton.IsEnabled = hasRun && (moved || pending);
        UndoCropButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        CropInHandle.IsEnabled = ready;
        CropOutHandle.IsEnabled = ready;

        PlayPauseButton.IsEnabled = ready;
        PreviousChangeButton.IsEnabled = ready;
        NextChangeButton.IsEnabled = ready;
        PositionTextBox.IsEnabled = ready;
        ScrubSlider.IsEnabled = ready;
        SpeedCombo.IsEnabled = ready;

        // Two bars while it runs, a triangle while it does not.
        PlayGlyph.Data = Geometry.Parse(_isPlaying
            ? "M 4,3 L 7,3 L 7,13 L 4,13 Z M 9,3 L 12,3 L 12,13 L 9,13 Z"
            : "M 5,3 L 13,8 L 5,13 Z");
        PlayPauseButton.ToolTip = _isPlaying ? "Pause. Space does the same." : "Play. Space does the same.";
    }
}
