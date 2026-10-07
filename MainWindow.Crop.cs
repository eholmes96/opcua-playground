using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OpcUaPlayground.Dialogs;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services;

namespace OpcUaPlayground;

/// <summary>
/// Cropping a recorded run: the two orange handles on the activity strip, and Crop, Overwrite,
/// Save New and Undo beside the clock.
///
/// Crop only changes what is on screen. The cropped run is held here, not in the document, until
/// Overwrite or Save New puts it there, so nothing on disk moves until one of them is pressed.
/// Crops stack: each one zooms the strip onto what was kept, and Undo steps back one at a time.
///
/// Overwrite and Save New take what the handles select, applied on top of any crop already made, so
/// pressing Crop first is optional. That is also why they stay enabled after a crop: the handles go
/// back to the ends of the shorter strip, but there is still something unsaved to keep.
/// </summary>
public partial class MainWindow
{
    /// <summary>The handles cannot meet. A crop has to keep at least this much of the run.</summary>
    private const int MinCropMs = 1;

    /// <summary>Where the handles sit, in the time of the run on screen.</summary>
    private int _cropInMs;
    private int _cropOutMs;

    /// <summary>What <see cref="_playback"/> was loaded from: a saved run, or an unsaved crop of one.</summary>
    private RecordedSession? _shownSession;

    /// <summary>The saved run the crops on screen were cut from. Still the one the picker shows.</summary>
    private RecordedSession? _cropSource;

    /// <summary>
    /// What was on screen before each crop, and how far into it that crop started. The offset is
    /// what lets Undo put the playhead back on the same moment rather than at the start.
    /// </summary>
    private readonly Stack<(RecordedSession Shown, int OffsetMs)> _cropUndo = new();

    /// <summary>Distance from the pointer to the cut it is dragging, fixed when the drag starts, so
    /// the cut does not jump to wherever inside the handle it was grabbed.</summary>
    private double _grabOffset;

    /// <summary>Set while the run picker is being put back by code, so its handler does not ask again.</summary>
    private bool _revertingRun;

    private bool HasPendingCrop => _cropUndo.Count > 0;

    private bool HandlesMoved =>
        _playback is not null && (_cropInMs > 0 || _cropOutMs < _playback.DurationMs);

    // ===================== The handles =====================

    private void ResetCropHandles()
    {
        _cropInMs = 0;
        _cropOutMs = _playback?.DurationMs ?? 0;
    }

    private void CropLayer_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutCrop();

    /// <summary>
    /// Places the handles and the shading from the stored times. Times are what is kept, not pixels,
    /// so resizing the window never moves a cut.
    ///
    /// The start handle sits just left of its cut and the end handle just right of its own. At rest
    /// they sit in the margins either side of the strip, and they can never cover each other.
    /// </summary>
    private void LayoutCrop()
    {
        double width = CropLayer.ActualWidth;
        bool show = _playback is not null && _playback.DurationMs > 0 && width > 0;

        var visibility = show ? Visibility.Visible : Visibility.Hidden;
        CropInHandle.Visibility = visibility;
        CropOutHandle.Visibility = visibility;
        CropShadeLeft.Visibility = visibility;
        CropShadeRight.Visibility = visibility;
        if (!show) return;

        double inX = XAt(_cropInMs);
        double outX = XAt(_cropOutMs);

        Canvas.SetLeft(CropInHandle, inX - CropInHandle.Width);
        Canvas.SetLeft(CropOutHandle, outX);

        Canvas.SetLeft(CropShadeLeft, 0);
        CropShadeLeft.Width = Math.Max(0, inX);
        Canvas.SetLeft(CropShadeRight, outX);
        CropShadeRight.Width = Math.Max(0, width - outX);
    }

    private double XAt(int ms) =>
        _playback is null || _playback.DurationMs <= 0
            ? 0
            : ms / (double)_playback.DurationMs * CropLayer.ActualWidth;

    private int MsAt(double x)
    {
        double width = CropLayer.ActualWidth;
        if (_playback is null || width <= 0) return 0;
        return (int)Math.Round(Math.Clamp(x / width, 0, 1) * _playback.DurationMs);
    }

    private void CropHandle_DragStarted(object sender, DragStartedEventArgs e)
    {
        PausePlayback();

        double cut = XAt(ReferenceEquals(sender, CropInHandle) ? _cropInMs : _cropOutMs);
        _grabOffset = cut - Mouse.GetPosition(CropLayer).X;
    }

    /// <summary>
    /// Follows the pointer rather than adding up the deltas, which drift once a clamp has eaten
    /// part of one. The playhead follows the handle, so the table shows exactly what the cut
    /// will keep at that edge.
    /// </summary>
    private void CropHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_playback is null) return;

        int ms = MsAt(Mouse.GetPosition(CropLayer).X + _grabOffset);

        if (ReferenceEquals(sender, CropInHandle))
            _cropInMs = Math.Clamp(ms, 0, Math.Max(0, _cropOutMs - MinCropMs));
        else
            _cropOutMs = Math.Clamp(ms, Math.Min(_playback.DurationMs, _cropInMs + MinCropMs), _playback.DurationMs);

        LayoutCrop();
        SeekTo(ReferenceEquals(sender, CropInHandle) ? _cropInMs : _cropOutMs);
        UpdatePlaybackEnabled();
    }

    private void CropHandle_DragCompleted(object sender, DragCompletedEventArgs e) => UpdatePlaybackEnabled();

    /// <summary>
    /// [ and ]: a handle to the playhead. With the step buttons or a typed time, this is how a cut
    /// lands exactly on a change rather than within a pixel of one.
    /// </summary>
    private void SetCropEdgeAtPlayhead(bool start)
    {
        if (_playback is null || _playback.DurationMs <= 0) return;

        if (start)
        {
            if (_positionMs > _cropOutMs - MinCropMs)
            {
                RecordStatusText.Text = "the start of a crop has to be before its end";
                return;
            }
            _cropInMs = _positionMs;
        }
        else
        {
            if (_positionMs < _cropInMs + MinCropMs)
            {
                RecordStatusText.Text = "the end of a crop has to be after its start";
                return;
            }
            _cropOutMs = _positionMs;
        }

        LayoutCrop();
        UpdatePlaybackEnabled();
    }

    // ===================== Crop and Undo =====================

    private void CropButton_Click(object sender, RoutedEventArgs e)
    {
        if (_shownSession is null || _playback is null || !HandlesMoved) return;

        PausePlayback();
        var cropped = RecordingCrop.Apply(_shownSession, _cropInMs, _cropOutMs);

        _cropSource ??= RunCombo.SelectedItem as RecordedSession;
        _cropUndo.Push((_shownSession, _cropInMs));

        ShowRun(cropped, _positionMs - _cropInMs);
        RecordStatusText.Text =
            $"cropped to {Seconds(cropped.DurationMs)}, not saved. Overwrite or Save New keeps it.";
    }

    private void UndoCropButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasPendingCrop) return;

        PausePlayback();
        var (previous, offset) = _cropUndo.Pop();
        if (!HasPendingCrop) _cropSource = null;

        ShowRun(previous, _positionMs + offset);
        RecordStatusText.Text = HasPendingCrop ? "one crop undone" : "back to the full recording";
    }

    // ===================== Keeping it =====================

    /// <summary>
    /// What Overwrite and Save New would keep: the handle selection if the handles have moved,
    /// otherwise the crop on screen. Null when there is nothing to keep.
    /// </summary>
    private RecordedSession? CropResult()
    {
        if (_shownSession is null || _playback is null) return null;
        if (HandlesMoved) return RecordingCrop.Apply(_shownSession, _cropInMs, _cropOutMs);
        return HasPendingCrop ? _shownSession : null;
    }

    private void OverwriteRunButton_Click(object sender, RoutedEventArgs e)
    {
        var source = _cropSource ?? RunCombo.SelectedItem as RecordedSession;
        if (source is null || CropResult() is not { } result) return;

        int index = _recordings.IndexOf(source);
        if (index < 0) return;

        PausePlayback();
        var answer = DarkMessageBox.Confirm(
            this,
            "Are you sure you'd like to overwrite this recording? The original recording will be lost."
            + Environment.NewLine + Environment.NewLine
            + $"{source.Name}, recorded {source.Recorded:yyyy-MM-dd HH:mm:ss}"
            + Environment.NewLine
            + $"{Seconds(source.DurationMs)} becomes {Seconds(result.DurationMs)}",
            "Yes",
            "No",
            cancel: null);

        if (answer != DarkDialogResult.Yes) return;

        result.Name = source.Name;
        _recordings[index] = result;

        // Cleared before the picker is rebuilt, or rebuilding it would ask about this very crop.
        ClearCrop();
        FillRunPicker(result);
        PersistRecordings($"\"{result.Name}\" overwritten, now {Seconds(result.DurationMs)}");
    }

    private void SaveNewRunButton_Click(object sender, RoutedEventArgs e)
    {
        var source = _cropSource ?? RunCombo.SelectedItem as RecordedSession;
        if (source is null || CropResult() is not { } result) return;

        PausePlayback();
        string? name = DarkPromptBox.Ask(
            this,
            "New Recording Name:",
            RecordingCrop.SuggestName(source.Name, _recordings),
            candidate => NameProblem(candidate, except: null));

        if (name is null) return;

        result.Name = name;
        _recordings.Add(result);

        ClearCrop();
        FillRunPicker(result);
        ResetRecordingName();      // the default name follows the names in the file
        PersistRecordings(
            $"\"{name}\" saved, {Seconds(result.DurationMs)} cut from \"{source.Name}\"");
    }

    // ===================== Guarding an unsaved crop =====================

    /// <summary>Forgets the crops without touching the screen. The caller shows what comes next.</summary>
    private void ClearCrop()
    {
        _cropUndo.Clear();
        _cropSource = null;
    }

    /// <summary>
    /// True to carry on, false when the user chose to keep the crop. Asked before anything that
    /// would throw an unsaved crop away. Handles that were only moved are not asked about: they are
    /// a selection, not a crop, and putting them back costs a drag.
    ///
    /// <paramref name="restore"/> puts the saved run back on screen after a discard. The caller
    /// wants that when what it is about to do can still be cancelled, like an Open whose file
    /// dialog is closed without picking anything.
    /// </summary>
    private bool ConfirmDropCrop(bool restore)
    {
        if (!HasPendingCrop) return true;

        PausePlayback();
        var source = _cropSource;
        string name = source?.Name ?? "this recording";

        var answer = DarkMessageBox.Confirm(
            this,
            $"Discard the unsaved crop of \"{name}\"?"
            + Environment.NewLine + Environment.NewLine
            + "The original recording is untouched either way. Keep the crop to save it with "
            + "Overwrite or Save New first.",
            "Discard",
            "Keep",
            cancel: null);

        if (answer != DarkDialogResult.Yes) return false;

        ClearCrop();
        if (restore && source is not null) ShowRun(source, 0);
        RecordStatusText.Text = $"crop of \"{name}\" discarded";
        return true;
    }

    // ===================== Small things =====================

    /// <summary>Why a name cannot be used for a run, or null when it can.</summary>
    private string? NameProblem(string candidate, RecordedSession? except) =>
        candidate.Length == 0
            ? "A recording needs a name, or it cannot be found again."
            : _recordings.Any(r => !ReferenceEquals(r, except)
                                   && r.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                ? $"There is already a recording called \"{candidate}\"."
                : null;

    private static string Seconds(int ms) => $"{ms / 1000.0:0.000} s";
}
