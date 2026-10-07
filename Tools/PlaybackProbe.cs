using OpcUaPlayground.Services;

namespace OpcUaPlayground.Tools;

/// <summary>
/// Reads a recorded run on the console, with --play &lt;file&gt; [run] [ms ...]. Prints the table as
/// it stood at each moment given, or at the start, middle and end when none are.
///
/// The point is to prove the playback engine without a window, and to answer "what was this tag
/// doing at 1.5 seconds" without dragging anything.
/// </summary>
public static class PlaybackProbe
{
    public static bool Run(string[] args)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, "--play", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            Console.WriteLine("usage: OpcUaPlayground --play <tagset file> [run name] [ms ...]");
            return false;
        }

        string path = args[index + 1];
        var rest = args.Skip(index + 2).ToList();

        // Anything that is not a number is the run's name.
        string? wanted = rest.FirstOrDefault(a => !int.TryParse(a, out _));
        var times = rest.Where(a => int.TryParse(a, out _)).Select(int.Parse).ToList();

        Models.TagSetDocument document;
        try
        {
            document = TagSetFile.Load(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"could not open {path}: {ex.Message}");
            return false;
        }

        if (document.Recordings.Count == 0)
        {
            Console.WriteLine("that tag set holds no recordings");
            return false;
        }

        var session = wanted is null
            ? document.Recordings[^1]
            : document.Recordings.FirstOrDefault(
                r => r.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        if (session is null)
        {
            Console.WriteLine($"no recording called \"{wanted}\". This file holds: "
                              + string.Join(", ", document.Recordings.Select(r => r.Name)));
            return false;
        }

        var playback = TagPlayback.Load(session);

        Console.WriteLine($"{playback.Name}, recorded {playback.Recorded:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"{playback.Tags.Count} tags, {playback.ChangeCount} changes over "
                          + $"{playback.DurationMs} ms, {playback.ChangeTimes.Count} distinct instants");
        if (playback.SkippedLines > 0)
            Console.WriteLine($"WARNING: {playback.SkippedLines} change lines could not be read");

        if (times.Count == 0)
            times = new List<int> { 0, playback.DurationMs / 2, playback.DurationMs };

        foreach (int ms in times)
        {
            Console.WriteLine();
            Console.WriteLine($"-- at {ms} ms ({playback.Recorded.AddMilliseconds(ms):HH:mm:ss.fff})");

            for (int i = 0; i < playback.Tags.Count; i++)
            {
                var tag = playback.Tags[i];
                string state = playback.TryStateAt(i, ms, out bool isError, out string text)
                    ? (isError ? $"ERROR: {text}" : $"= {text}")
                    : "(nothing recorded yet)";

                Console.WriteLine($"   {tag.Address,-40} {tag.DataType,-8} {state}");
            }
        }

        return playback.SkippedLines == 0;
    }
}
