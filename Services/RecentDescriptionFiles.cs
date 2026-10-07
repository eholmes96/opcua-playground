using System.IO;
using System.Text.Json;

namespace OpcUaPlayground.Services;

/// <summary>
/// The descriptions menu's memory: up to five tag CSVs used recently, most recent first, and which
/// one of them is loaded now. Only one set of descriptions is ever loaded, so there is at most one.
///
/// Kept in the user's application data folder rather than in a tag set, because it is about this
/// machine's files, not about any one set of tags. It is remembered across restarts: the loaded
/// file is read again when the app starts.
///
/// UI-agnostic so the self test can exercise it without a window.
/// </summary>
public sealed class RecentDescriptionFiles
{
    public const int MaxEntries = 5;

    private readonly List<string> _paths = new();

    /// <summary>Full paths, most recent first.</summary>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>The file whose descriptions are loaded now, or null when none are.</summary>
    public string? LoadedPath { get; private set; }

    public bool Contains(string path) => _paths.Any(p => SamePath(p, path));

    public bool IsLoaded(string path) =>
        LoadedPath is not null && SamePath(LoadedPath, path);

    /// <summary>
    /// Records <paramref name="path"/> as loaded. It moves to the top of the list if it was already
    /// there, and the oldest entry falls off the end once there are more than five. The loaded one
    /// is always at the top, so it can never be the one that falls off.
    /// </summary>
    public void Use(string path)
    {
        _paths.RemoveAll(p => SamePath(p, path));
        _paths.Insert(0, path);
        if (_paths.Count > MaxEntries) _paths.RemoveRange(MaxEntries, _paths.Count - MaxEntries);
        LoadedPath = path;
    }

    /// <summary>Takes an entry off the list. True when it was the loaded one, which is now unloaded.</summary>
    public bool Remove(string path)
    {
        _paths.RemoveAll(p => SamePath(p, path));
        if (!IsLoaded(path)) return false;
        LoadedPath = null;
        return true;
    }

    /// <summary>Nothing is loaded any more. The entry stays on the list.</summary>
    public void Unload() => LoadedPath = null;

    /// <summary>
    /// What the menu shows for a file: the folder it is in and its name, e.g.
    /// "Downloads/tagdescriptions.csv". A file at the root of a drive shows the drive.
    /// </summary>
    public static string Label(string path)
    {
        string name = Path.GetFileName(path);
        string? folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder)) return name;

        string parent = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (parent.Length == 0) parent = folder.TrimEnd('\\', '/');
        return $"{parent}/{name}";
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ===================== On disk =====================

    /// <summary>%APPDATA%\OpcUaPlayground\recent-descriptions.json</summary>
    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpcUaPlayground", "recent-descriptions.json");

    private sealed class Stored
    {
        public int Version { get; set; } = 1;
        public List<string>? Recent { get; set; }
        public string? Loaded { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads the list back. A missing or unreadable file is an empty list, since losing a list of
    /// shortcuts must never stop the app starting. A loaded file that is not on the list is dropped.
    /// </summary>
    public static RecentDescriptionFiles Load(string storePath)
    {
        var recent = new RecentDescriptionFiles();
        Stored? stored;
        try
        {
            if (!File.Exists(storePath)) return recent;
            stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(storePath), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return recent;
        }

        foreach (string path in stored?.Recent ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(path) || recent._paths.Any(p => SamePath(p, path))) continue;
            recent._paths.Add(path);
            if (recent._paths.Count == MaxEntries) break;
        }

        if (stored?.Loaded is { } loaded && recent._paths.Any(p => SamePath(p, loaded)))
            recent.LoadedPath = recent._paths.First(p => SamePath(p, loaded));

        return recent;
    }

    /// <summary>Written through a temporary file, like a tag set, so a failed write keeps the old list.</summary>
    public void Save(string storePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        string temp = storePath + ".saving";
        File.WriteAllText(temp, JsonSerializer.Serialize(
            new Stored { Recent = _paths.ToList(), Loaded = LoadedPath }, Json));
        File.Move(temp, storePath, overwrite: true);
    }
}
