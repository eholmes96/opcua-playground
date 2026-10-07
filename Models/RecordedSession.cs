namespace OpcUaPlayground.Models;

/// <summary>
/// One recorded run, stored inside the tag set file alongside the tags.
///
/// A run carries its own <see cref="Tags"/> list rather than leaning on the table's. The table is
/// edited between runs, so an index into the live table would quietly point at the wrong tag the
/// moment a row was added or removed. Indexes in <see cref="Changes"/> are into this list.
/// </summary>
public sealed class RecordedSession
{
    public string Name { get; set; } = "";

    /// <summary>When the run started. Playback adds elapsed time to this to show the time of day,
    /// and the overwrite prompt shows it so a run is identifiable even when two share a name.
    ///
    /// Version 2 files hold the moment the run STOPPED here instead. They are moved back by the
    /// run's length on load, see <c>TagSetFile.Deserialize</c>.</summary>
    public DateTimeOffset Recorded { get; set; }

    /// <summary>How long the run actually lasted, which is less than the cap when stopped by hand.</summary>
    public int DurationMs { get; set; }

    /// <summary>The cap that was set when the run started, in seconds.</summary>
    public int MaxSeconds { get; set; }

    /// <summary>The tags in index order, frozen at the moment recording began.</summary>
    public List<RecordedTag> Tags { get; set; } = new();

    /// <summary>Written into the file so it explains its own change lines to anyone reading it.</summary>
    public string ChangeFormat { get; set; } = RecordingChange.FormatNote;

    /// <summary>The run itself, one compact line per change. See <see cref="RecordingChange"/>.</summary>
    public List<string> Changes { get; set; } = new();
}

/// <summary>
/// How one state change is written.
///
/// Changes are strings rather than JSON objects on purpose. The tag set file is pretty printed so
/// it can be read and edited by hand, and an indented object costs five lines and about seventy
/// bytes per change. A string costs one line and about twenty, which is the difference between a
/// readable file and a hundred thousand lines of braces.
/// </summary>
public static class RecordingChange
{
    public const string FormatNote = "elapsedMs|tagIndex|v=value or e=error|text";

    private const char Separator = '|';
    private const string ValueKind = "v";
    private const string ErrorKind = "e";

    public static string Value(long elapsedMs, int index, string value) =>
        $"{elapsedMs}{Separator}{index}{Separator}{ValueKind}{Separator}{value}";

    public static string Error(long elapsedMs, int index, string error) =>
        $"{elapsedMs}{Separator}{index}{Separator}{ErrorKind}{Separator}{error}";

    /// <summary>
    /// Splits into at most four parts, so a value carrying its own separator survives intact. A
    /// Logix STRING can hold anything, this one included.
    /// </summary>
    public static bool TryParse(string line, out long elapsedMs, out int index, out bool isError, out string text)
    {
        elapsedMs = 0;
        index = 0;
        isError = false;
        text = "";

        var parts = line.Split(Separator, 4);
        if (parts.Length != 4) return false;
        if (!long.TryParse(parts[0], out elapsedMs)) return false;
        if (!int.TryParse(parts[1], out index)) return false;
        if (parts[2] != ValueKind && parts[2] != ErrorKind) return false;

        isError = parts[2] == ErrorKind;
        text = parts[3];
        return true;
    }
}
