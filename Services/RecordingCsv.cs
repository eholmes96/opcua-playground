using System.IO;
using System.Text;
using OpcUaPlayground.Models;

namespace OpcUaPlayground.Services;

/// <summary>
/// Writes a recorded run as CSV, for the trending and analysis that happens in a spreadsheet rather
/// than in this window.
///
/// The shape is wide: one row per instant, one column per tag, each cell carrying that tag's state
/// at that instant. A tall file of one change per row would be more faithful to what was recorded,
/// but it is not what anyone can chart without pivoting it first. Cells are forward filled, so a
/// tag that did not change still reports what it was holding, which is what a trend needs.
///
/// Values are written exactly as the run stored them. They were formatted when they were captured,
/// so reformatting here would only risk changing a decimal separator that was already right.
/// </summary>
public static class RecordingCsv
{
    public const string DialogFilter = "CSV (*.csv)|*.csv|All files (*.*)|*.*";

    /// <summary>Sortable and unambiguous, and what a spreadsheet parses as a timestamp.</summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private const string LineEnding = "\r\n";

    public static string Build(TagPlayback playback)
    {
        var text = new StringWriter { NewLine = LineEnding };
        Write(text, playback);
        return text.ToString();
    }

    /// <summary>
    /// Streams the file rather than building it in memory. A long run with a busy analog can hold
    /// a lot of instants, and there is no reason to hold the whole thing twice.
    /// </summary>
    public static void Write(TextWriter writer, TagPlayback playback)
    {
        var header = new StringBuilder();
        header.Append("elapsed_ms,time");
        foreach (var tag in playback.Tags)
        {
            header.Append(',');
            header.Append(Escape(tag.Address));
        }
        writer.Write(header);
        writer.Write(LineEnding);

        foreach (int elapsed in RowTimes(playback))
        {
            var row = new StringBuilder();
            row.Append(elapsed);
            row.Append(',');
            row.Append(playback.Recorded.AddMilliseconds(elapsed).ToString(TimeFormat));

            for (int i = 0; i < playback.Tags.Count; i++)
            {
                row.Append(',');

                // An error is written as its text rather than left blank. A blank cell reads as a
                // sample that happened to be missing; the text says the read actually failed.
                if (playback.TryStateAt(i, elapsed, out _, out string state))
                    row.Append(Escape(state));
            }

            writer.Write(row);
            writer.Write(LineEnding);
        }
    }

    /// <summary>
    /// One row per instant something changed, plus the two ends. Starting at zero and finishing at
    /// the run's duration means a chart spans the whole run rather than the busy part of it.
    /// </summary>
    private static IEnumerable<int> RowTimes(TagPlayback playback)
    {
        var times = new SortedSet<int>(playback.ChangeTimes) { 0 };
        if (playback.DurationMs > 0) times.Add(playback.DurationMs);
        return times;
    }

    /// <summary>RFC 4180: quote anything holding a separator, a quote or a line break.</summary>
    private static string Escape(string field)
    {
        if (field.IndexOfAny(NeedsQuoting) < 0) return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    private static readonly char[] NeedsQuoting = { ',', '"', '\r', '\n' };

    /// <summary>A file name for a run, safe on Windows and obvious in a downloads folder.</summary>
    public static string SuggestFileName(RecordedSession session)
    {
        var safe = new StringBuilder();
        foreach (char c in session.Name)
            safe.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);

        return $"{safe}_{session.Recorded:yyyyMMdd_HHmmss}.csv";
    }
}
