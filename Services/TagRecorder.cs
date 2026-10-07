using System.ComponentModel;
using System.Diagnostics;
using OpcUaPlayground.Models;

namespace OpcUaPlayground.Services;

/// <summary>
/// Captures state changes from the monitor table into a <see cref="RecordedSession"/>.
///
/// It records changes, not samples. A tag polled at 100 ms that never moves costs nothing, while
/// one that moves every scan costs a line per scan. That keeps a run proportional to what actually
/// happened rather than to the scan rate, which is the whole point of recording state changes.
///
/// Every tag is written once at t=0 whatever its value, so playback has a starting state instead
/// of a table full of blanks until the first change arrives.
///
/// That starting state has to be a real reading. A row the poller has not reported on yet shows an
/// empty cell that means "not read yet", and pressing Record is allowed to start the poller, so at
/// that instant every row can be in that state. Such a row's t=0 line waits for its first reading
/// instead, arriving a scan later at most, and is filed at t=0 because it is the state the run
/// started from.
///
/// This lives entirely on the UI thread. <see cref="TagMonitor"/> already marshals every table
/// update there, so change notifications arrive in order and nothing here needs a lock.
/// </summary>
public sealed class TagRecorder
{
    private readonly List<string> _changes = new();

    /// <summary>The t=0 lines, by tag index. Kept apart so they lead the run however late they land.</summary>
    private readonly SortedDictionary<int, string> _baseline = new();

    /// <summary>Tags whose t=0 line is still waiting for a first reading.</summary>
    private readonly HashSet<int> _awaitingBaseline = new();
    private readonly Dictionary<MonitoredTag, int> _index = new();
    private List<MonitoredTag> _tags = new();
    private Stopwatch? _clock;

    /// <summary>
    /// Taken when the run starts, because playback adds elapsed time to it. Stamping it at Stop
    /// instead made every clock time late by the length of the run.
    /// </summary>
    private DateTimeOffset _started;

    /// <summary>Three digits, as the toolbar field allows. A run is a diagnostic, not a historian.</summary>
    public const int MaxSecondsLimit = 999;
    public const int MinSecondsLimit = 1;
    public const int DefaultMaxSeconds = 60;

    public bool IsRecording => _clock is not null;

    public TimeSpan Elapsed => _clock?.Elapsed ?? TimeSpan.Zero;

    /// <summary>The cap this run was started with. Changing the field mid-run does not move it.</summary>
    public int MaxSeconds { get; private set; } = DefaultMaxSeconds;

    /// <summary>True once the run has reached its cap and should be stopped and saved.</summary>
    public bool IsFull => IsRecording && Elapsed.TotalSeconds >= MaxSeconds;

    public int ChangeCount => _baseline.Count + _changes.Count;

    public void Start(IEnumerable<MonitoredTag> tags, int maxSeconds)
    {
        if (IsRecording) return;

        _tags = tags.ToList();
        Clear();
        MaxSeconds = Math.Clamp(maxSeconds, MinSecondsLimit, MaxSecondsLimit);

        for (int i = 0; i < _tags.Count; i++) _index[_tags[i]] = i;

        _started = DateTimeOffset.Now;
        _clock = Stopwatch.StartNew();

        // The starting state, so a playback has somewhere to begin. See the class notes for the
        // rows that have not been read yet.
        for (int i = 0; i < _tags.Count; i++)
        {
            if (_tags[i].HasReading) _baseline[i] = Line(0, i, _tags[i]);
            else _awaitingBaseline.Add(i);
        }

        foreach (var tag in _tags) tag.PropertyChanged += OnTagChanged;
    }

    /// <summary>Ends the run and hands back what was captured. Null when nothing was running.</summary>
    public RecordedSession? Stop(string name)
    {
        if (_clock is null) return null;

        foreach (var tag in _tags) tag.PropertyChanged -= OnTagChanged;

        long durationMs = _clock.ElapsedMilliseconds;
        _clock = null;

        var session = new RecordedSession
        {
            Name = name,
            Recorded = _started,
            DurationMs = (int)Math.Min(durationMs, int.MaxValue),
            MaxSeconds = MaxSeconds,
            Tags = _tags.Select(t => new RecordedTag
            {
                Address = t.Address,
                DataType = t.DataType,
                Description = string.IsNullOrEmpty(t.Description) ? null : t.Description,
                ScanRateMs = t.ScanRateMs,
            }).ToList(),
            // Baseline first, in tag order, so the file stays in time order however late a row's
            // first reading arrived. A row never read at all has no line and plays back blank,
            // which is the truth about it.
            Changes = _baseline.Values.Concat(_changes).ToList(),
        };

        Clear();
        _tags = new List<MonitoredTag>();
        return session;
    }

    /// <summary>Throws the run away. Used when the user declines to save it.</summary>
    public void Discard()
    {
        if (_clock is null) return;
        foreach (var tag in _tags) tag.PropertyChanged -= OnTagChanged;
        _clock = null;
        Clear();
        _tags = new List<MonitoredTag>();
    }

    private void Clear()
    {
        _changes.Clear();
        _baseline.Clear();
        _awaitingBaseline.Clear();
        _index.Clear();
    }

    private void OnTagChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_clock is null) return;
        if (sender is not MonitoredTag tag || !_index.TryGetValue(tag, out int index)) return;

        // A row not read yet is being filled in, not changing. Its first complete reading is its
        // starting state, and the value and error set on the way there are not changes.
        if (_awaitingBaseline.Contains(index))
        {
            if (e.PropertyName != nameof(MonitoredTag.HasReading) || !tag.HasReading) return;
            _awaitingBaseline.Remove(index);
            _baseline[index] = Line(0, index, tag);
            return;
        }

        // Only the two fields that describe the tag's state. Scan rate and byte count are settings,
        // and the description is fetched once and never changes during a run.
        if (e.PropertyName is not (nameof(MonitoredTag.Value) or nameof(MonitoredTag.Error))) return;

        long elapsed = _clock.ElapsedMilliseconds;

        // A change landing after the cap belongs to the next run, not this one. The cap is checked
        // by a timer that ticks every tenth of a second, so a few changes can arrive past it.
        if (elapsed > (long)MaxSeconds * 1000) return;

        _changes.Add(Line(elapsed, index, tag));
    }

    private static string Line(long elapsedMs, int index, MonitoredTag tag) =>
        tag.HasError
            ? RecordingChange.Error(elapsedMs, index, tag.Error ?? "")
            : RecordingChange.Value(elapsedMs, index, tag.Value);

    /// <summary>
    /// The next unused "recordingN". Numbering skips what is already there, so a default name can
    /// never overwrite an existing run without being asked.
    /// </summary>
    public static string NextDefaultName(IEnumerable<RecordedSession> existing)
    {
        var used = existing.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            string candidate = $"recording{n}";
            if (!used.Contains(candidate)) return candidate;
        }
    }
}
