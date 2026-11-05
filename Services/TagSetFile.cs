using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpcUaPlayground.Models;

namespace OpcUaPlayground.Services;

/// <summary>Thrown when a file is readable but is not a tag set this build understands.</summary>
public sealed class TagSetFormatException : Exception
{
    public TagSetFormatException(string message) : base(message) { }
}

/// <summary>
/// Reads and writes tag set files. The whole file layer, kept UI-agnostic so the self test can
/// exercise it without a window.
/// </summary>
public static class TagSetFile
{
    public const string Extension = ".tagset.json";

    public const string DialogFilter =
        "Tag set (*.tagset.json)|*.tagset.json|JSON (*.json)|*.json|All files (*.*)|*.*";

    /// <summary>Indented and camel-cased, because these files get opened in an editor.</summary>
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Forgiving on the way in: comments and trailing commas are allowed, and unknown properties
    /// are ignored, so a file written by a later build still loads its tags here.
    /// </summary>
    private static readonly JsonSerializerOptions Reading = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,

        // Recorded tags arrive either as objects or as the bare address strings the first
        // recordings wrote. Both are accepted, which is why the document version stays where it is.
        Converters = { new RecordedTagConverter() },
    };

    public static string Serialize(TagSetDocument document) =>
        JsonSerializer.Serialize(Stamped(document, TagSetDocument.TagSetFormat), Pretty);

    /// <summary>Fills in the format marker, which the document type cannot default for itself
    /// without making every unrelated JSON file look like ours.</summary>
    private static TagSetDocument Stamped(TagSetDocument document, string format)
    {
        document.Format ??= format;
        return document;
    }

    public static TagSetDocument Deserialize(string json)
    {
        TagSetDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<TagSetDocument>(json, Reading);
        }
        catch (JsonException ex)
        {
            throw new TagSetFormatException($"this file is not valid JSON ({ex.Message})");
        }

        if (document is null) throw new TagSetFormatException("the file is empty");

        if (document.Format != TagSetDocument.TagSetFormat)
            throw new TagSetFormatException(
                "this is a JSON file, but not an OPC UA Playground tag set");

        if (document.Version > TagSetDocument.CurrentVersion)
            throw new TagSetFormatException(
                $"this file is version {document.Version} and this build reads version "
                + $"{TagSetDocument.CurrentVersion}. Update the app rather than losing the tags it holds.");

        document.Tags ??= new List<TagSetEntry>();
        foreach (var tag in document.Tags)
        {
            tag.Address = (tag.Address ?? "").Trim();
            tag.ScanRateMs = Math.Clamp(
                tag.ScanRateMs == 0 ? MonitoredTag.MinScanRateMs : tag.ScanRateMs,
                MonitoredTag.MinScanRateMs, MonitoredTag.MaxScanRateMs);
        }

        document.Tags = document.Tags.Where(t => t.Address.Length > 0).ToList();

        document.Recordings ??= new List<RecordedSession>();
        foreach (var run in document.Recordings)
        {
            run.Name = (run.Name ?? "").Trim();
            run.Tags ??= new List<RecordedTag>();
            run.Changes ??= new List<string>();
        }

        // A run with no name cannot be found again or overwritten, so it is not a run.
        document.Recordings = document.Recordings.Where(r => r.Name.Length > 0).ToList();

        // A color that does not read is dropped, not a reason to refuse the tags.
        document.TagColors = TagColorMap.FromStored(document.TagColors).ToStored();

        // Version 2 stamped a run when it stopped. The recorder took the stamp and the duration at
        // the same moment, so the start is exactly one duration earlier. A run with no stamp at all
        // is left alone: there is nothing to correct, and the minimum date cannot go backwards.
        if (document.Version < TagSetDocument.StartStampedVersion)
        {
            foreach (var run in document.Recordings)
                if (run.Recorded != default && run.DurationMs > 0)
                    run.Recorded = run.Recorded.AddMilliseconds(-run.DurationMs);
        }

        // Corrected in memory, so it is written back as the current version and not corrected twice.
        document.Version = TagSetDocument.CurrentVersion;
        return document;
    }

    /// <summary>
    /// Written to a neighbouring temporary file and moved into place, so an error part way through
    /// leaves the previous save intact rather than a half-written one.
    /// </summary>
    public static void Save(string path, TagSetDocument document)
    {
        string temp = path + ".saving";
        File.WriteAllText(temp, Serialize(document));
        File.Move(temp, path, overwrite: true);
    }

    public static TagSetDocument Load(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>
    /// Replaces the runs in the file at <paramref name="path"/> and leaves everything else as it
    /// was last saved. A run is written as soon as it is finished, deleted or cropped, but the tag
    /// table is the user's to save or discard: rewriting the whole document here would commit any
    /// unsaved table edit behind their back. Throws when the file cannot be read, rather than
    /// creating a new one with no tags in it.
    /// </summary>
    public static void SaveRecordings(string path, IReadOnlyList<RecordedSession> recordings)
    {
        var onDisk = Load(path);
        onDisk.Recordings = recordings.ToList();
        onDisk.Saved = DateTimeOffset.Now;
        Save(path, onDisk);
    }

    /// <summary>Adds our extension when the user typed a bare name in the save dialog.</summary>
    public static string EnsureExtension(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Length > 0
            ? path
            : path + Extension;

    /// <summary>What the title bar shows: "MyLine.tagset.json" rather than the whole path.</summary>
    public static string DisplayName(string path) => Path.GetFileName(path);
}
