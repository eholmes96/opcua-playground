using OpcUaPlayground.Models;

namespace OpcUaPlayground.Services;

/// <summary>
/// Cuts a recorded run down to a window of itself.
///
/// The recorder writes every tag once at t=0 and then only when something changes. So the part
/// worth getting right is the new start: cut the first ten seconds off and a tag that last moved at
/// three seconds has no line left in the window at all. The crop therefore writes a fresh baseline,
/// every tag's state at the cut, and then the changes after it. A cropped run plays back exactly as
/// the same window of the original did.
///
/// The recorded time moves forward by the amount cut, so the clock in playback still reads the real
/// time of day. The source run is never touched; the result is a new run.
/// </summary>
public static class RecordingCrop
{
    /// <summary>
    /// The window from <paramref name="fromMs"/> to <paramref name="toMs"/>, both in the source
    /// run's own time. Clamped to the run; a window with no length is refused.
    /// </summary>
    public static RecordedSession Apply(RecordedSession source, int fromMs, int toMs)
    {
        var playback = TagPlayback.Load(source);

        int from = Math.Clamp(fromMs, 0, playback.DurationMs);
        int to = Math.Clamp(toMs, 0, playback.DurationMs);
        if (to <= from)
            throw new ArgumentException($"a crop has to end after it starts ({from} ms to {to} ms)");

        var changes = new List<string>();

        // The baseline. A change landing exactly on the cut is part of this, since the state at a
        // moment includes whatever changed at that moment.
        for (int i = 0; i < playback.Tags.Count; i++)
        {
            // Only a hand edited file can have a tag with nothing yet, and it stays blank here too.
            if (!playback.TryStateAt(i, from, out bool isError, out string text)) continue;

            changes.Add(isError
                ? RecordingChange.Error(0, i, text)
                : RecordingChange.Value(0, i, text));
        }

        // The body. Unreadable lines are dropped: the playback already counted them as skipped, and
        // copying them forward would only move the damage into the new run.
        var body = new List<(long Time, string Line)>();
        foreach (string line in source.Changes)
        {
            if (!RecordingChange.TryParse(line, out long time, out int index, out bool isError, out string text)
                || index < 0 || index >= playback.Tags.Count
                || time <= from || time > to)
                continue;

            long shifted = time - from;
            body.Add((shifted, isError
                ? RecordingChange.Error(shifted, index, text)
                : RecordingChange.Value(shifted, index, text)));
        }

        // Stable, so two changes at one instant keep their order and the later one still wins.
        changes.AddRange(body.OrderBy(c => c.Time).Select(c => c.Line));

        return new RecordedSession
        {
            Name = source.Name,
            Recorded = source.Recorded == default ? default : source.Recorded.AddMilliseconds(from),
            DurationMs = to - from,
            MaxSeconds = source.MaxSeconds,
            Tags = source.Tags.Select(t => new RecordedTag
            {
                Address = t.Address,
                DataType = t.DataType,
                Description = t.Description,
                ScanRateMs = t.ScanRateMs,
            }).ToList(),
            Changes = changes,
        };
    }

    /// <summary>
    /// "name-crop", or "name-crop2" and upwards when that is taken. Says where the run came from,
    /// and like the recorder's default it never lands on a name already in the file.
    /// </summary>
    public static string SuggestName(string sourceName, IEnumerable<RecordedSession> existing)
    {
        var used = existing.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string first = $"{sourceName}-crop";
        if (!used.Contains(first)) return first;

        for (int n = 2; ; n++)
        {
            string candidate = $"{sourceName}-crop{n}";
            if (!used.Contains(candidate)) return candidate;
        }
    }
}
