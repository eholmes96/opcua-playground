using System.Text.Json.Serialization;

namespace OpcUaPlayground.Models;

/// <summary>
/// What a saved tag set contains. This is the on-disk shape, deliberately separate from
/// <see cref="MonitoredTag"/> so that renaming a UI property cannot silently break every file
/// somebody already saved.
///
/// The format is JSON, one object per file, holding the tags AND every run recorded against them.
/// The user chose a single file over a companion or a file per run: one thing to move, mail or
/// back up. The cost is that finishing a recording rewrites the whole file rather than appending
/// to it, and that a run is held in memory until it stops, so a crash mid-run loses that run. The
/// 999 second cap is what keeps that bounded.
///
/// JSON rather than SQLite, CSV or a binary blob: System.Text.Json is in the framework so this
/// stays a zero-package project, the file survives being opened in an editor, and it diffs. The
/// one concession to size is that recorded changes are compact strings, not objects, because
/// pretty printed objects cost five lines each. See <see cref="RecordingChange"/>.
/// </summary>
public sealed class TagSetDocument
{
    /// <summary>Marks the file as ours, so a stray .json is refused with a sentence rather than
    /// a deserialisation error.</summary>
    public const string TagSetFormat = "opcua-playground.tagset";

    /// <summary>Raised when a change is not backwards compatible. Readers refuse a higher number
    /// rather than guessing at fields they do not know.
    ///
    /// 1: tags only.
    /// 2: recordings live in the file too. A version 1 reader would drop them on the next save,
    ///    which is exactly the silent loss the version number exists to prevent.
    /// 3: a run's recorded time is when it started. Version 2 wrote when it stopped, which put
    ///    every playback clock time late by the length of the run. Those are corrected on load,
    ///    and an older build would show a version 3 file's times early, so it refuses them.
    /// 4: the saved controller also names the network adapter it was reached through, so opening
    ///    the set can point the window back at it. A version 3 reader would drop the adapter on its
    ///    next save, and the user chose to have older builds refuse these files instead.
    ///
    /// Tag colors (<see cref="TagColors"/>) did not move the version. The user chose that: an older
    /// build opens the file and only loses the colors if it saves it, which is cosmetic, where
    /// refusing would lock them out of their tags and recordings.</summary>
    public const int CurrentVersion = 4;

    /// <summary>The first version whose runs are stamped with their start rather than their stop.</summary>
    public const int StartStampedVersion = 3;

    /// <summary>
    /// Deliberately has no default. A property initialiser runs on deserialisation too, so a
    /// default here would make every JSON object in the world look like one of our files. It is
    /// stamped on the way out instead, by <c>TagSetFile.Serialize</c>.
    /// </summary>
    public string? Format { get; set; }

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset Saved { get; set; } = DateTimeOffset.Now;

    /// <summary>Where these tags were read from. Informational: loading never connects anything.
    /// It exists so the app can say "this set was saved from a different controller".</summary>
    public ControllerReference? Controller { get; set; }

    /// <summary>The toolbar scan rate at the time of the save, used for tags added afterwards.</summary>
    public int DefaultScanRateMs { get; set; }

    /// <summary>Order is preserved. This is the working table, not what any recording captured.</summary>
    public List<TagSetEntry> Tags { get; set; } = new();

    /// <summary>
    /// Every run recorded against this set, oldest first. They live in the same file as the tags,
    /// so a set and its history travel together as one thing.
    /// </summary>
    public List<RecordedSession> Recordings { get; set; } = new();

    /// <summary>
    /// Each tag's color on the playback strip, "#RRGGBB" by tag name, for tags that have one other
    /// than the default blue. One color per name covers the table and every recording. Left out
    /// of the file when no tag has a color.
    /// </summary>
    [JsonConverter(typeof(LenientStringMapConverter))]
    public Dictionary<string, string>? TagColors { get; set; }
}

/// <summary>Which controller the set was built against. No credentials, no session state.</summary>
public sealed class ControllerReference
{
    public string Address { get; set; } = "";
    public int Slot { get; set; }

    /// <summary>Catalogue string from the Identity Object, e.g. "1756-L83E/B". Blank if unknown.</summary>
    public string? ProductName { get; set; }

    /// <summary>The network adapter the controller was reached through. Since version 4.</summary>
    public AdapterReference? Adapter { get; set; }
}

/// <summary>
/// A network adapter, identified the way Windows does. The id only means something on the
/// computer that saved it, which is the point: on another PC the adapter is left as it is rather
/// than guessed from a similar name.
/// </summary>
public sealed class AdapterReference
{
    /// <summary>NetworkInterface.Id, the interface GUID.</summary>
    public string Id { get; set; } = "";

    /// <summary>What Windows calls it, e.g. "Ethernet 2". For people reading the file and the status line.</summary>
    public string? Name { get; set; }

    /// <summary>The adapter's own IPv4 address at the time, since one adapter can carry several.</summary>
    public string? Address { get; set; }
}

/// <summary>One row of the monitor table as it is stored.</summary>
public sealed class TagSetEntry
{
    public string Address { get; set; } = "";

    public string DataType { get; set; } = "";

    public int ScanRateMs { get; set; }

    /// <summary>Imported from a Studio 5000 tag CSV export, and kept here because this file is the
    /// only place it survives the session. A controller does not serve descriptions over CIP (proven
    /// on a GuardLogix 5580), so polling never fills or refreshes this.</summary>
    public string? Description { get; set; }
}
