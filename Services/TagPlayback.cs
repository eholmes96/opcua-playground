using System.Globalization;
using OpcUaPlayground.Models;

namespace OpcUaPlayground.Services;

/// <summary>
/// Reads a recorded run back. Given a moment, it says what every tag showed then.
///
/// The obvious implementation replays every change from the start on each seek. That is fine for a
/// short run and hopeless for a long one, and dragging a scrub bar seeks continuously. So the run is
/// indexed once, into one ascending array of change times per tag: the state of a tag at time t is
/// the last change at or before t, found by binary search. Seeking one second in costs the same as
/// seeking nine hundred seconds in.
///
/// No controller, no socket, no UI. A run can be opened and read on a machine that has never seen
/// the PLC, which is half the point of recording one.
/// </summary>
public sealed class TagPlayback
{
    private readonly int[][] _times;
    private readonly string[][] _texts;
    private readonly bool[][] _errors;

    /// <summary>Every change's time, ascending. Distinct times live in <see cref="ChangeTimes"/>.</summary>
    private readonly int[] _allTimes;

    /// <summary>The times of changes that clear the deadband, ascending, one entry per change.
    /// What the density strip counts. See <see cref="DisplayDeadband"/>.</summary>
    private readonly int[] _shownTimes;

    /// <summary>Distinct times of changes that clear the deadband. What the step buttons walk.</summary>
    private readonly int[] _stepTimes;

    /// <summary>Per tag, the times of its changes that clear the deadband, ascending. What says
    /// which tags are behind a line on the strip.</summary>
    private readonly int[][] _shownTimesByTag;

    /// <summary>
    /// How far a REAL or LREAL has to move from the last value that counted before the density
    /// strip and the step buttons show it as a change. The user asked for 0.1, so sensor jitter
    /// does not paint the whole strip. Measured from the last counted value rather than from the
    /// previous sample, the way a historian's deadband works, so a slow drift still shows once it
    /// has moved 0.1 in total. Display only: the run keeps every change, and the table, crop and
    /// CSV export all use them.
    /// </summary>
    public const decimal DisplayDeadband = 0.1m;

    private TagPlayback(
        RecordedSession session,
        int[][] times,
        string[][] texts,
        bool[][] errors,
        int[] allTimes,
        int[] shownTimes,
        int[][] shownTimesByTag,
        int durationMs,
        int skipped)
    {
        Name = session.Name;
        Recorded = session.Recorded;
        Tags = session.Tags;
        _times = times;
        _texts = texts;
        _errors = errors;
        _allTimes = allTimes;
        _shownTimes = shownTimes;
        _shownTimesByTag = shownTimesByTag;
        _stepTimes = shownTimes.Distinct().ToArray();
        DurationMs = durationMs;
        SkippedLines = skipped;
        ChangeTimes = allTimes.Distinct().ToArray();
    }

    public string Name { get; }

    public DateTimeOffset Recorded { get; }

    public IReadOnlyList<RecordedTag> Tags { get; }

    /// <summary>
    /// How long the scrub bar runs for. Taken as the later of the recorded duration and the last
    /// change, so a hand-edited file cannot hide changes past the end of the bar.
    /// </summary>
    public int DurationMs { get; }

    public int ChangeCount => _allTimes.Length;

    /// <summary>Distinct times of every recorded change, ascending, deadband or not. What the CSV
    /// export writes a row for.</summary>
    public IReadOnlyList<int> ChangeTimes { get; }

    /// <summary>Distinct times of the changes that clear the deadband. What the step buttons walk.</summary>
    public IReadOnlyList<int> StepTimes => _stepTimes;

    /// <summary>Lines that could not be read. Surfaced rather than swallowed.</summary>
    public int SkippedLines { get; }

    public static TagPlayback Load(RecordedSession session)
    {
        int tagCount = session.Tags.Count;
        var perTag = new List<(int Time, bool IsError, string Text)>[tagCount];
        for (int i = 0; i < tagCount; i++) perTag[i] = new List<(int, bool, string)>();

        int skipped = 0;
        var allTimes = new List<int>();

        foreach (string line in session.Changes)
        {
            if (!RecordingChange.TryParse(line, out long elapsed, out int index, out bool isError, out string text)
                || index < 0 || index >= tagCount || elapsed < 0)
            {
                skipped++;
                continue;
            }

            int time = (int)Math.Min(elapsed, int.MaxValue);
            perTag[index].Add((time, isError, text));
            allTimes.Add(time);
        }

        var times = new int[tagCount][];
        var texts = new string[tagCount][];
        var errors = new bool[tagCount][];
        var shownTimes = new List<int>();
        var shownTimesByTag = new int[tagCount][];

        for (int i = 0; i < tagCount; i++)
        {
            var shownForTag = new List<int>();
            // The recorder writes in order, but a file that has been edited by hand might not be.
            // OrderBy is stable, so two changes at the same instant keep the order they were
            // written in and the later one still wins.
            var ordered = perTag[i].OrderBy(c => c.Time).ToList();
            times[i] = ordered.Select(c => c.Time).ToArray();
            texts[i] = ordered.Select(c => c.Text).ToArray();
            errors[i] = ordered.Select(c => c.IsError).ToArray();

            bool floating = IsFloatingPoint(session.Tags[i].DataType);
            (bool IsError, string Text)? lastCounted = null;
            foreach (var change in ordered)
            {
                if (lastCounted is { } last && floating && !change.IsError && !last.IsError
                    && WithinDeadband(last.Text, change.Text))
                    continue;

                shownTimes.Add(change.Time);
                shownForTag.Add(change.Time);
                lastCounted = (change.IsError, change.Text);
            }

            shownTimesByTag[i] = shownForTag.ToArray();
        }

        allTimes.Sort();
        shownTimes.Sort();
        int lastChange = allTimes.Count == 0 ? 0 : allTimes[^1];

        return new TagPlayback(
            session, times, texts, errors, allTimes.ToArray(), shownTimes.ToArray(), shownTimesByTag,
            Math.Max(session.DurationMs, lastChange), skipped);
    }

    private static bool IsFloatingPoint(string? dataType) =>
        string.Equals(dataType, "REAL", StringComparison.OrdinalIgnoreCase)
        || string.Equals(dataType, "LREAL", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when two recorded values are less than <see cref="DisplayDeadband"/> apart. Compared as
    /// decimals from the recorded text, so 22.8 to 22.9 is exactly 0.1 and counts, where binary
    /// floating point would make it 0.0999... and hide it. Anything that does not read as a number
    /// (a blank, NaN, a value too large for a decimal) is never within the deadband.
    /// </summary>
    internal static bool WithinDeadband(string from, string to)
    {
        const NumberStyles style = NumberStyles.Float;
        if (!decimal.TryParse(from, style, CultureInfo.InvariantCulture, out decimal a)
            || !decimal.TryParse(to, style, CultureInfo.InvariantCulture, out decimal b))
            return false;
        return Math.Abs(b - a) < DisplayDeadband;
    }

    /// <summary>
    /// What one tag showed at <paramref name="ms"/>. False when the tag had not reported anything
    /// yet, which the recorder's baseline normally prevents but a hand-edited file can produce.
    /// </summary>
    public bool TryStateAt(int tagIndex, int ms, out bool isError, out string text)
    {
        isError = false;
        text = "";

        if (tagIndex < 0 || tagIndex >= _times.Length) return false;

        int at = IndexAtOrBefore(_times[tagIndex], ms);
        if (at < 0) return false;

        isError = _errors[tagIndex][at];
        text = _texts[tagIndex][at];
        return true;
    }

    /// <summary>The change before this moment, or the start when there is none. Changes inside
    /// the deadband are stepped over, so the buttons agree with the strip.</summary>
    public int PreviousChangeTime(int ms)
    {
        int at = IndexAtOrBefore(_stepTimes, ms - 1);
        return at < 0 ? 0 : _stepTimes[at];
    }

    /// <summary>The change after this moment, or the end when there is none. Changes inside the
    /// deadband are stepped over.</summary>
    public int NextChangeTime(int ms)
    {
        foreach (int time in _stepTimes)
            if (time > ms) return time;

        return DurationMs;
    }

    /// <summary>
    /// Changes per horizontal bucket, for the strip under the scrub bar, leaving out those inside
    /// the deadband. Bucketing is what keeps this constant-cost on a run holding hundreds of
    /// thousands of changes.
    /// </summary>
    public int[] Density(int buckets)
    {
        if (buckets < 1) buckets = 1;
        var counts = new int[buckets];
        if (DurationMs <= 0)
        {
            counts[0] = _shownTimes.Length;
            return counts;
        }

        foreach (int time in _shownTimes)
            counts[BucketOf(time, buckets)]++;

        return counts;
    }

    /// <summary>
    /// Which tags are behind one bucket of <see cref="Density"/>: the indexes of the tags with a
    /// change in it that clears the deadband, in table order. Uses the same bucketing as the
    /// strip, so a line always lists exactly the tags that drew it. Asked for one bucket at a time,
    /// when a tooltip opens, at a cost of one binary search per tag.
    /// </summary>
    public IReadOnlyList<int> TagsInBucket(int bucket, int buckets)
    {
        if (buckets < 1) buckets = 1;
        var found = new List<int>();
        if (bucket < 0 || bucket >= buckets) return found;

        // The earliest time that can land in this bucket: the smallest t with t * buckets / D >= bucket.
        long from = DurationMs <= 0 ? 0 : ((long)bucket * DurationMs + buckets - 1) / buckets;

        for (int tag = 0; tag < _shownTimesByTag.Length; tag++)
        {
            var times = _shownTimesByTag[tag];
            int first = IndexAtOrBefore(times, (int)Math.Min(from - 1, int.MaxValue)) + 1;
            if (first < times.Length && BucketOf(times[first], buckets) == bucket) found.Add(tag);
        }

        return found;
    }

    private int BucketOf(int time, int buckets) =>
        DurationMs <= 0 ? 0 : Math.Clamp((int)((long)time * buckets / DurationMs), 0, buckets - 1);

    /// <summary>
    /// The last index whose time is at or before <paramref name="ms"/>, or -1.
    ///
    /// Written out rather than using Array.BinarySearch because two changes can share an instant,
    /// and this has to land on the later of them.
    /// </summary>
    private static int IndexAtOrBefore(IReadOnlyList<int> times, int ms)
    {
        int low = 0;
        int high = times.Count - 1;
        int best = -1;

        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (times[middle] <= ms)
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }
}
