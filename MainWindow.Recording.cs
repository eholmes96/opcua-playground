using System.Windows;
using System.Windows.Threading;
using OpcUaPlayground.Dialogs;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services;

namespace OpcUaPlayground;

/// <summary>
/// The recording bar: the Current/Playback switch, the record light, the run timer and its cap,
/// and the name a finished run is filed under.
///
/// A finished run goes into the same document as the tags and is written out with it, so a tag set
/// and its history are one file.
/// </summary>
public partial class MainWindow
{
    private readonly TagRecorder _recorder = new();

    /// <summary>Runs already in this document, oldest first. Replaced wholesale by a load.</summary>
    private List<RecordedSession> _recordings = new();

    /// <summary>Drives the elapsed display and enforces the cap.</summary>
    private readonly DispatcherTimer _recordTick = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>Set while the switch is being put back by code, so the handler does not loop.</summary>
    private bool _revertingMode;

    private bool IsPlaybackMode => ModeSwitch.IsChecked == true;

    private void InitialiseRecording()
    {
        RecordMaxTextBox.PreviewTextInput += DigitsOnly;
        RecordMaxTextBox.LostFocus += (_, _) => NormalizeRecordMax();

        _recordTick.Tick += (_, _) => OnRecordTick();
        _monitor.Stopped += OnMonitorStoppedWhileRecording;

        ResetRecordingName();

        // Nothing to animate on the way up: the app opens in Current, and a slide from an empty
        // window would look like a glitch rather than a transition.
        ApplyMode(animate: false);
        UpdateRecordingUi();
    }

    // ===================== Mode =====================

    private void ModeSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_revertingMode) return;

        // Leaving Current mid-run would abandon the run without saying so.
        if (_recorder.IsRecording && IsPlaybackMode)
        {
            _revertingMode = true;
            ModeSwitch.IsChecked = false;
            _revertingMode = false;
            DarkMessageBox.Show(this, "Stop the recording before switching to playback.");
            return;
        }

        // Going back to Current would throw an unsaved crop away without saying so.
        if (!IsPlaybackMode && HasPendingCrop && !ConfirmDropCrop(restore: false))
        {
            _revertingMode = true;
            ModeSwitch.IsChecked = true;
            _revertingMode = false;
            return;
        }

        ApplyMode(animate: true);
    }

    private void ApplyMode(bool animate)
    {
        bool playback = IsPlaybackMode;

        CurrentModeLabel.Foreground = (System.Windows.Media.Brush)FindResource(
            playback ? "Brush.TextDisabled" : "Brush.TextPrimary");
        PlaybackModeLabel.Foreground = (System.Windows.Media.Brush)FindResource(
            playback ? "Brush.TextPrimary" : "Brush.TextDisabled");

        RecordStatusText.Text = playback
            ? $"{DescribeRunCount()} in this tag set"
            : "";

        PositionModePanels(playback, animate);

        if (playback) EnterPlayback(animate);
        else LeavePlayback(animate);

        UpdateRecordingUi();
    }

    // ===================== Start and stop =====================

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            FinishRecording("stopped");
            return;
        }

        if (_monitor.Tags.Count == 0)
        {
            RecordStatusText.Text = "add tags to the table before recording";
            return;
        }

        // A run with the poller stopped would capture nothing at all, so start it here rather than
        // record silence and let the user work out why.
        if (!_monitor.IsRunning)
        {
            if (_target.Confirmed is not { } endpoint)
            {
                RecordStatusText.Text = "connect to a controller before recording";
                return;
            }
            _monitor.Start(endpoint);
        }

        NormalizeRecordMax();
        _recorder.Start(_monitor.Tags, ReadRecordMax());
        _recordTick.Start();
        UpdateRecordingUi();
        UpdateButtons();
    }

    private void OnRecordTick()
    {
        RecordElapsedText.Text = $"{_recorder.Elapsed.TotalSeconds:0.0}s";
        if (_recorder.IsFull) FinishRecording("reached its limit");
    }

    /// <summary>A run is meaningless once polling has stopped, so save what was captured.</summary>
    private void OnMonitorStoppedWhileRecording(string reason)
    {
        if (_recorder.IsRecording) FinishRecording($"polling {reason}");
    }

    /// <summary>
    /// Ends the run, settles on a name, files it in the document and writes the file when there is
    /// one to write.
    /// </summary>
    private void FinishRecording(string why)
    {
        _recordTick.Stop();

        var session = _recorder.Stop(RecordNameTextBox.Text.Trim());
        RecordElapsedText.Text = "0.0s";

        // Put the light out before anything can prompt. The run is already over, and a lamp still
        // burning behind a dialog says otherwise.
        UpdateRecordingUi();
        UpdateButtons();

        if (session is null) return;

        if (!TryName(session))
        {
            RecordStatusText.Text = $"recording discarded ({why})";
            return;
        }

        // A run of the same name is replaced where it sat, so the order of the list keeps meaning
        // something rather than shuffling on every overwrite.
        int existing = _recordings.FindIndex(
            r => r.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _recordings[existing] = session;
        else _recordings.Add(session);

        double seconds = session.DurationMs / 1000.0;
        string saved = $"\"{session.Name}\" {why}: {seconds:0.0}s, {session.Changes.Count} changes";

        PersistRecordings(saved);
        ResetRecordingName();
    }

    /// <summary>
    /// Settles the run's name. Returns false when the user chose to throw the run away rather than
    /// overwrite or rename.
    /// </summary>
    private bool TryName(RecordedSession session)
    {
        if (session.Name.Length == 0) session.Name = TagRecorder.NextDefaultName(_recordings);

        var clash = _recordings.FirstOrDefault(
            r => r.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase));
        if (clash is null) return true;

        string alternative = TagRecorder.NextDefaultName(_recordings);
        var answer = DarkMessageBox.Confirm(
            this,
            "Are you sure you'd like to overwrite the following recording?"
            + Environment.NewLine + Environment.NewLine
            + $"{clash.Name}, recorded {clash.Recorded:yyyy-MM-dd HH:mm:ss}",
            "Overwrite",
            $"Save as {alternative}");

        switch (answer)
        {
            case DarkDialogResult.Yes:
                return true;

            case DarkDialogResult.No:
                session.Name = alternative;
                return true;

            default:
                return false;
        }
    }

    // ===================== Housekeeping =====================

    /// <summary>
    /// The next unused default. Numbering follows the names already in the file rather than a
    /// count, so it can never land on one that exists.
    /// </summary>
    private void ResetRecordingName() =>
        RecordNameTextBox.Text = TagRecorder.NextDefaultName(_recordings);

    private void NormalizeRecordMax() =>
        RecordMaxTextBox.Text = ReadRecordMax().ToString();

    private int ReadRecordMax() =>
        int.TryParse(RecordMaxTextBox.Text, out int seconds)
            ? Math.Clamp(seconds, TagRecorder.MinSecondsLimit, TagRecorder.MaxSecondsLimit)
            : TagRecorder.DefaultMaxSeconds;

    private string DescribeRunCount() =>
        _recordings.Count == 1 ? "1 recording" : $"{_recordings.Count} recordings";

    private void UpdateRecordingUi()
    {
        bool recording = _recorder.IsRecording;

        RecordLamp.IsLit = recording;
        RecordButton.ToolTip = recording
            ? "Stop recording and save the run"
            : "Start recording every state change in the table";

        // The cap and the name belong to the run in flight, so they are settled before it starts.
        RecordMaxTextBox.IsEnabled = !recording;
        RecordNameTextBox.IsEnabled = !recording;
        ModeSwitch.IsEnabled = !recording;
    }

    /// <summary>Called by a load, so the bar reflects the document that just arrived.</summary>
    private void OnRecordingsReplaced()
    {
        ResetRecordingName();
        RecordStatusText.Text = _recordings.Count == 0 ? "" : $"{DescribeRunCount()} in this tag set";
        if (IsPlaybackMode) FillRunPicker();
    }
}
