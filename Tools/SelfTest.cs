using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Text;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services;
using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground.Tools;

/// <summary>
/// Byte-level checks on the protocol encoders, run with --selftest.
///
/// The route path vectors are the point of this harness. Getting a port segment subtly wrong
/// produces a request a PLC quietly refuses rather than one that fails loudly, so the encoder is
/// pinned against paths whose exact bytes are documented, rather than against its own output.
/// </summary>
public static class SelfTest
{
    private static int _passed;
    private static int _failed;

    public static bool Run()
    {
        Console.WriteLine("OPC UA Playground protocol self test");
        Console.WriteLine();

        RoutePathVectors();
        UnconnectedSendFraming();
        IdentityParsing();
        StatusDecoding();
        EncapsulationFraming();
        TagPathsAndValues();
        ReadOnlyGuard();
        TagSetFiles();
        Recordings();
        RecordingBaseline();
        Playback();
        Crop();
        RecordedClock();
        Csv();
        TagDescriptionImport();
        TreeSelection();
        DescriptionsMenu();
        SavedController();
        StripDeadband();
        EntrySuggestions();

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0;
    }

    // ===================== Entry suggestions =====================

    /// <summary>
    /// The entry box's list, driven the way its keys drive it: Up and Down move the highlight,
    /// Enter fills in the highlighted name, and Enter again adds it. The window only displays this.
    /// </summary>
    private static void EntrySuggestions()
    {
        Section("entry suggestions");

        string[] addresses =
        {
            "Recipe.Duration", "Recipe.Enabled", "Recipe.SetPoint",
            "Stations[0].Recipe.Duration", "MotorSpeed", "Temperature",
        };
        var list = new TagSuggestions();

        list.Update("Re", addresses);
        Check("two characters suggest nothing", !list.IsOpen && list.Selected is null);

        list.Update("Rec", addresses);
        Check("three characters list every match, the first highlighted",
            list.Matches.Count == 4 && list.Selected == "Recipe.Duration");

        list.Move(1);
        list.Move(1);
        Check("Down moves the highlight one at a time", list.Selected == "Recipe.SetPoint");
        list.Move(-1);
        Check("Up moves it back", list.Selected == "Recipe.Enabled");
        list.Move(-1);
        list.Move(-1);
        Check("Up from the top wraps to the bottom", list.Selected == "Stations[0].Recipe.Duration");
        list.Move(1);
        Check("Down from the bottom wraps to the top", list.Selected == "Recipe.Duration");

        list.Move(2);
        Check("Enter on a partial name fills in rather than adds", list.EnterCompletes("Rec"));
        string? filled = list.Accept();
        Check("Enter fills in the highlighted name, not the first", filled == "Recipe.SetPoint");
        Check("filling in closes the list", !list.IsOpen);

        // The filled-in name arrives back through TextChanged.
        list.Update(filled!, addresses);
        Check("the name just filled in does not reopen the list", !list.IsOpen);
        Check("so the next Enter adds the tag", !list.EnterCompletes(filled!));

        list.Update(filled + "X", addresses);
        list.Update(filled!, addresses);
        Check("typing after a fill-in suggests again, even back on the same name",
            list.IsOpen && list.Selected == "Recipe.SetPoint");

        list.Accept();
        list.Update("Recipe.SetPoint", addresses);
        list.Reopen("Recipe.SetPoint", addresses);
        Check("Down on a closed list opens it again", list.IsOpen);

        list.Update("motorspeed", addresses);
        Check("Enter on a name typed in full adds it, whatever the case",
            list.IsOpen && !list.EnterCompletes("motorspeed"));

        list.Update("Temp", addresses);
        list.Close();
        Check("Enter after the list is closed adds what was typed", !list.EnterCompletes("Temp"));

        list.Update("Rec", addresses);
        list.Select(2);
        Check("a click highlights the row clicked", list.Accept() == "Recipe.SetPoint");
    }

    // ===================== Strip deadband =====================

    /// <summary>
    /// The density strip and the step buttons leave out REAL and LREAL moves smaller than 0.1,
    /// measured from the last value that counted. The run itself keeps every change.
    /// </summary>
    private static void StripDeadband()
    {
        Section("strip deadband");

        Check("22.8 to 22.9 is a full 0.1 and counts", !TagPlayback.WithinDeadband("22.8", "22.9"));
        Check("22.80 to 22.89 is inside the deadband", TagPlayback.WithinDeadband("22.80", "22.89"));
        Check("a value that is not a number is never inside it", !TagPlayback.WithinDeadband("", "22.9")
            && !TagPlayback.WithinDeadband("NaN", "NaN"));
        Check("an exponent form reads as a number", TagPlayback.WithinDeadband("1E+07", "10000000.05"));

        RecordedSession Run(string dataType, params string[] changes) => new()
        {
            Name = "deadband",
            DurationMs = 10_000,
            Tags = { new RecordedTag { Address = "T", DataType = dataType, ScanRateMs = 100 } },
            Changes = changes.ToList(),
        };

        // A REAL jittering by 0.02 around 50.00, then a real move at 5 s.
        var jitter = TagPlayback.Load(Run("REAL",
            "0|0|v|50.00", "1000|0|v|50.02", "2000|0|v|49.98", "3000|0|v|50.03", "5000|0|v|50.50"));
        Check("jitter under 0.1 is left off the strip",
            jitter.Density(10).Sum() == 2 && jitter.Density(10)[0] == 1 && jitter.Density(10)[5] == 1);
        Check("the step buttons skip it too",
            jitter.NextChangeTime(0) == 5000 && jitter.PreviousChangeTime(5000) == 0);
        Check("the run keeps every change", jitter.ChangeCount == 5 && jitter.ChangeTimes.Count == 5);
        Check("playback still shows the jittered value",
            jitter.TryStateAt(0, 2500, out _, out string at2500) && at2500 == "49.98");

        // Creeping 0.05 at a time: measured from the last value that counted, every second step shows.
        var creep = TagPlayback.Load(Run("REAL",
            "0|0|v|10.00", "1000|0|v|10.05", "2000|0|v|10.10", "3000|0|v|10.15", "4000|0|v|10.20"));
        Check("a slow drift shows once it has moved 0.1 in total",
            creep.StepTimes.SequenceEqual(new[] { 0, 2000, 4000 }));

        var lreal = TagPlayback.Load(Run("LREAL", "0|0|v|1.000", "1000|0|v|1.050"));
        Check("an LREAL gets the same deadband", lreal.StepTimes.SequenceEqual(new[] { 0 }));

        var dint = TagPlayback.Load(Run("DINT", "0|0|v|7", "1000|0|v|8", "2000|0|v|8"));
        Check("a DINT keeps every change", dint.StepTimes.SequenceEqual(new[] { 0, 1000, 2000 }));

        var faulted = TagPlayback.Load(Run("REAL",
            "0|0|v|3.00", "1000|0|e|not polling: connection lost", "2000|0|v|3.01"));
        Check("going into and out of an error always counts, whatever the value",
            faulted.StepTimes.SequenceEqual(new[] { 0, 1000, 2000 }));

        // Two tags at one instant: the instant counts if either tag's change does.
        var mixed = TagPlayback.Load(new RecordedSession
        {
            Name = "mixed",
            DurationMs = 3_000,
            Tags =
            {
                new RecordedTag { Address = "Level", DataType = "REAL", ScanRateMs = 100 },
                new RecordedTag { Address = "Running", DataType = "BOOL", ScanRateMs = 100 },
            },
            Changes = { "0|0|v|5.00", "0|1|v|False", "1000|0|v|5.01", "2000|0|v|5.02", "2000|1|v|True" },
        });
        Check("an instant where another tag really changed still counts",
            mixed.StepTimes.SequenceEqual(new[] { 0, 2000 }));
        Check("the CSV export keeps a row for every recorded instant",
            mixed.ChangeTimes.SequenceEqual(new[] { 0, 1000, 2000 }));
    }

    // ===================== Saved controller =====================

    /// <summary>
    /// Start, Record and Save act on a confirmed controller only, never on an address that was
    /// merely tried. A tag set remembers that controller and its adapter, and an adapter is
    /// recognised by its Windows id alone.
    /// </summary>
    private static void SavedController()
    {
        Section("saved controller");

        NetworkAdapterOption Nic(string id, string name, string address) => new()
        {
            Id = id,
            Name = name,
            DisplayName = $"{name} ({address})",
            Address = IPAddress.Parse(address),
            SubnetMask = IPAddress.Parse("255.255.255.0"),
        };

        var plantNic = Nic("{AAAA}", "Ethernet 2", "10.20.30.50");
        var plantA = new PlcEndpoint(IPAddress.Parse("10.20.30.11"), 0, plantNic.Address, 250);
        var plantB = new PlcEndpoint(IPAddress.Parse("10.20.30.12"), 0, plantNic.Address, 250);

        var target = new ControllerTarget();
        Check("nothing is confirmed before any Connect", !target.IsConfirmed && target.Confirmed is null);

        target.BeginAttempt(plantA, plantNic);
        Check("an attempt in progress is not a confirmed controller",
            !target.IsConfirmed && target.Confirmed is null && target.Attempted == plantA);

        target.Confirm("1756-L83E/B");
        Check("a confirmed attempt is the controller to poll",
            target.Confirmed == plantA && target.ProductName == "1756-L83E/B");

        target.BeginAttempt(plantB, plantNic);
        target.Fail();
        Check("a failed attempt leaves nothing to poll, not the old controller or the new address",
            target.Confirmed is null && target.Attempted == plantB);
        Check("a failed attempt forgets the previous controller's catalogue number",
            target.ProductName is null);

        var fromFile = new ControllerReference { Address = "10.0.0.5", Slot = 2, ProductName = "1756-L85E/B" };
        Check("saving with nothing confirmed keeps the controller the file already had",
            ReferenceEquals(target.ToReference(fromFile), fromFile));
        Check("saving with nothing confirmed and nothing known saves no controller",
            target.ToReference(null) is null);

        target.BeginAttempt(plantA, plantNic);
        target.Confirm("1756-L83E/B");
        var saved = target.ToReference(fromFile);
        Check("saving while confirmed records that controller and its adapter",
            saved is { Address: "10.20.30.11", Slot: 0, ProductName: "1756-L83E/B" }
            && saved.Adapter is { Id: "{AAAA}", Name: "Ethernet 2", Address: "10.20.30.50" });

        target.Clear();
        Check("Disconnect leaves nothing attempted or confirmed",
            target.Attempted is null && !target.IsConfirmed && target.Adapter is null);

        // Recognising the adapter on reopening.
        var twoAddresses = new[]
        {
            Nic("{AAAA}", "Ethernet 2", "10.20.30.50"),
            Nic("{AAAA}", "Ethernet 2", "10.10.0.50"),
            Nic("{BBBB}", "Wi-Fi", "172.16.0.9"),
        };
        Check("the saved adapter is found by id and address",
            NetworkAdapterService.Match(twoAddresses, "{AAAA}", "10.10.0.50")?.Address.ToString() == "10.10.0.50");
        Check("an adapter whose address changed is still found by id",
            NetworkAdapterService.Match(twoAddresses, "{BBBB}", "172.16.0.200")?.Name == "Wi-Fi");
        Check("another PC's adapter with the same name is not taken for it",
            NetworkAdapterService.Match(new[] { Nic("{CCCC}", "Ethernet 2", "10.20.30.50") }, "{AAAA}", "10.20.30.50") is null);
        Check("no saved id matches nothing", NetworkAdapterService.Match(twoAddresses, null, null) is null);

        // Through the file format.
        var document = new TagSetDocument
        {
            Controller = saved,
            Tags = { new TagSetEntry { Address = "Pump", DataType = "DINT", ScanRateMs = 250 } },
        };
        var back = TagSetFile.Deserialize(TagSetFile.Serialize(document));
        Check("a tag set keeps its controller's adapter",
            back.Controller?.Adapter is { Id: "{AAAA}", Name: "Ethernet 2", Address: "10.20.30.50" });
        Check("a tag set is written as version 4", TagSetFile.Serialize(document).Contains("\"version\": 4"));

        var fromVersion3 = TagSetFile.Deserialize("""
            { "format": "opcua-playground.tagset", "version": 3, "tags": [],
              "controller": { "address": "10.20.30.11", "slot": 0 } }
            """);
        Check("a version 3 file still opens, with no adapter",
            fromVersion3.Controller is { Address: "10.20.30.11", Adapter: null });
        Check("a version 3 file is written back as version 4",
            TagSetFile.Serialize(fromVersion3).Contains("\"version\": 4")
            && TagSetFile.Serialize(fromVersion3).Contains("\"address\": \"10.20.30.11\""));
    }

    // ===================== Descriptions menu =====================

    /// <summary>
    /// The rules the user chose for the descriptions menu: five recent files, most recent first,
    /// one loaded at a time, and a loaded file's text replacing whatever the table held before.
    /// </summary>
    private static void DescriptionsMenu()
    {
        Section("descriptions menu");

        string P(string name) => Path.Combine(@"C:\Plant", name);

        var recent = new RecentDescriptionFiles();
        recent.Use(P("one.csv"));
        Check("a loaded file goes on the list and is the loaded one",
            recent.Paths.SequenceEqual(new[] { P("one.csv") }) && recent.IsLoaded(P("one.csv")));

        recent.Use(P("two.csv"));
        recent.Use(P("ONE.CSV"));
        Check("loading a listed file again moves it to the top without a duplicate",
            recent.Paths.Count == 2 && recent.IsLoaded(P("one.csv"))
            && string.Equals(recent.Paths[0], P("ONE.CSV"), StringComparison.Ordinal)
            && string.Equals(recent.Paths[1], P("two.csv"), StringComparison.Ordinal));
        Check("only one file is loaded at a time", !recent.IsLoaded(P("two.csv")));

        foreach (string name in new[] { "three.csv", "four.csv", "five.csv", "six.csv" }) recent.Use(P(name));
        Check("a sixth file pushes the oldest off the list",
            recent.Paths.Count == RecentDescriptionFiles.MaxEntries
            && !recent.Paths.Contains(P("two.csv"))
            && recent.Paths[0] == P("six.csv") && recent.IsLoaded(P("six.csv")));

        Check("removing a file that is not loaded leaves the loaded one loaded",
            !recent.Remove(P("three.csv")) && recent.IsLoaded(P("six.csv"))
            && !recent.Paths.Contains(P("three.csv")));
        Check("removing the loaded file unloads it",
            recent.Remove(P("six.csv")) && recent.LoadedPath is null && !recent.Paths.Contains(P("six.csv")));
        Check("a removed file is no longer on the list, however its name is cased",
            !recent.Contains(P("SIX.csv")) && recent.Contains(P("FIVE.csv")));

        recent.Use(P("four.csv"));
        recent.Unload();
        Check("unloading keeps the file on the list", recent.LoadedPath is null && recent.Paths[0] == P("four.csv"));

        Check("the menu shows the folder and the file name",
            RecentDescriptionFiles.Label(@"C:\Users\someone\Downloads\tagdescriptions.csv") == "Downloads/tagdescriptions.csv");
        Check("a file at the root of a drive shows the drive",
            RecentDescriptionFiles.Label(@"C:\tags.csv") == "C:/tags.csv");

        // Through a real file, since the list is remembered across restarts.
        string folder = Path.Combine(Path.GetTempPath(), "OpcUaPlayground-selftest-recent");
        Directory.CreateDirectory(folder);
        string store = Path.Combine(folder, "recent-descriptions.json");
        try
        {
            var saved = new RecentDescriptionFiles();
            saved.Use(P("a.csv"));
            saved.Use(P("b.csv"));
            saved.Use(P("a.csv"));
            saved.Save(store);
            var back = RecentDescriptionFiles.Load(store);
            Check("the list and the loaded file survive a restart",
                back.Paths.SequenceEqual(new[] { P("a.csv"), P("b.csv") }) && back.IsLoaded(P("a.csv")));

            File.WriteAllText(store, "{ this is not json");
            var fromJunk = RecentDescriptionFiles.Load(store);
            Check("an unreadable list file starts an empty list instead of failing",
                fromJunk.Paths.Count == 0 && fromJunk.LoadedPath is null);

            Check("a missing list file is an empty list",
                RecentDescriptionFiles.Load(Path.Combine(folder, "absent.json")).Paths.Count == 0);

            File.WriteAllText(store, """
                { "version": 1,
                  "recent": [ "C:\\Plant\\1.csv", "C:\\Plant\\1.csv", "C:\\Plant\\2.csv", "C:\\Plant\\3.csv",
                              "C:\\Plant\\4.csv", "C:\\Plant\\5.csv", "C:\\Plant\\6.csv" ],
                  "loaded": "C:\\Plant\\elsewhere.csv" }
                """);
            var edited = RecentDescriptionFiles.Load(store);
            Check("a hand edited list is cut to five, without duplicates",
                edited.Paths.SequenceEqual(new[] { P("1.csv"), P("2.csv"), P("3.csv"), P("4.csv"), P("5.csv") }));
            Check("a loaded file that is not on the list is not loaded", edited.LoadedPath is null);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }

        // Applying a set: every row ends with that set's text, or none.
        var first = new TagDescriptionLookup(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pump"] = "feed pump", ["Valve"] = "inlet valve",
        });
        var second = new TagDescriptionLookup(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pump"] = "feed pump", ["Level"] = "tank level",
        });

        var pump = new MonitoredTag("Pump", "DINT", 100, 4);
        var valve = new MonitoredTag("Valve", "BOOL", 100, 1);
        var level = new MonitoredTag("Level", "REAL", 100, 4);
        var table = new[] { pump, valve, level };

        var firstResult = DescriptionApply.Apply(table, first);
        Check("a loaded file fills the rows it names",
            pump.Description == "feed pump" && valve.Description == "inlet valve" && level.Description == ""
            && firstResult.Matched == 2 && firstResult.Changed == 2);

        var changes = new List<string>();
        foreach (var tag in table)
            tag.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MonitoredTag.Description)) changes.Add(((MonitoredTag)s!).Address);
            };

        var again = DescriptionApply.Apply(table, first);
        Check("loading the same file again changes nothing, so nothing is marked unsaved",
            again.Changed == 0 && changes.Count == 0);

        var secondResult = DescriptionApply.Apply(table, second);
        Check("a new file replaces the old one, and clears what the new one does not name",
            pump.Description == "feed pump" && valve.Description == "" && level.Description == "tank level"
            && secondResult.Cleared == 1 && secondResult.Changed == 2);
        Check("a row whose text stays the same raises no change",
            changes.SequenceEqual(new[] { "Valve", "Level" }));

        var unloaded = DescriptionApply.Apply(table, TagDescriptionLookup.Empty);
        Check("unloading clears every description",
            table.All(t => t.Description == "") && unloaded.Cleared == 2 && unloaded.Matched == 0);
    }

    // ===================== Tree selection =====================

    /// <summary>
    /// The tracked selection has to agree with the nodes' own flags, because the Add button asks
    /// the set while the tree paints from the flags. If they drift, the button lies.
    /// </summary>
    private static void TreeSelection()
    {
        Section("tree selection");

        var a = new TagNode("A", "DINT", TagNodeKind.Tag, "A");
        var b = new TagNode("B", "DINT", TagNodeKind.Tag, "B");
        var c = new TagNode("C", "DINT", TagNodeKind.Tag, "C");
        var selection = new TagSelection();

        Check("nothing is selected to begin with", !selection.Any && selection.Count == 0);

        selection.Set(a, true);
        selection.Set(a, true);
        Check("selecting a node twice counts it once", selection.Count == 1 && a.IsSelected);

        selection.Toggle(b);
        selection.Toggle(a);
        Check("a toggle flips the node and the count with it",
            selection.Count == 1 && b.IsSelected && !a.IsSelected);

        selection.Set(b, false);
        Check("deselecting the last node leaves nothing selected", !selection.Any && !b.IsSelected);

        selection.Set(a, true);
        selection.Set(c, true);
        selection.Clear();
        Check("clearing deselects every node it holds", !selection.Any && !a.IsSelected && !c.IsSelected);

        // A browse or search puts different rows on screen; whatever of them is already selected
        // is the selection from then on, and nothing else is.
        b.IsSelected = true;
        selection.Set(a, true);
        selection.Reset(new[] { b, c });
        Check("a reset takes the selection from the rows now shown",
            selection.Count == 1 && selection.Any);
        selection.Clear();
        Check("clearing after a reset deselects the shown row and leaves hidden ones alone",
            !b.IsSelected && a.IsSelected);
    }

    // ===================== Route paths =====================

    private static void RoutePathVectors()
    {
        Section("route path encoding");

        // A one-byte link address follows the port byte directly, with no length and no pad.
        Bytes("1,0  backplane to slot 0", CipRoutePath.ToSlot(0), new byte[] { 0x01, 0x00 });
        Bytes("1,3  backplane to slot 3", CipRoutePath.ToSlot(3), new byte[] { 0x01, 0x03 });

        // A longer link sets the extended flag (0x10) and is written length-then-bytes, as ASCII.
        // "192.168.1.10" is twelve characters, so the segment is even and takes no pad.
        var ethernet = CipRoutePath.Encode(CipHop.Ethernet(IPAddress.Parse("192.168.1.10")));
        Bytes("2,192.168.1.10  out an Ethernet port", ethernet,
            new byte[] { 0x12, 0x0C }.Concat(Encoding.ASCII.GetBytes("192.168.1.10")).ToArray());

        // Two hops through a remote chassis. "10.10.10.20" is eleven characters, which makes that
        // segment odd, so a 0x00 pad keeps the whole path on a 16-bit boundary.
        var twoHop = CipRoutePath.Encode(
            CipHop.Backplane(4),
            CipHop.Ethernet(IPAddress.Parse("10.10.10.20")),
            CipHop.Backplane(0));
        Bytes("1,4,2,10.10.10.20,1,0  two hops", twoHop,
            new byte[] { 0x01, 0x04, 0x12, 0x0B }
                .Concat(Encoding.ASCII.GetBytes("10.10.10.20"))
                .Concat(new byte[] { 0x00, 0x01, 0x00 })
                .ToArray());

        Check("two-hop path measures nine words", CipRoutePath.SizeInWords(twoHop) == 9,
            $"got {CipRoutePath.SizeInWords(twoHop)}");

        Check("an unsupported port is rejected rather than mis-encoded",
            Throws(() => CipRoutePath.Encode(new CipHop(15, new byte[] { 0 }))));
    }

    // ===================== Unconnected_Send =====================

    private static void UnconnectedSendFraming()
    {
        Section("Unconnected_Send framing");

        Bytes("identity request", UnconnectedSend.BuildGetIdentity(),
            new byte[] { 0x01, 0x02, 0x20, 0x01, 0x24, 0x01 });

        Bytes("identity in slot 0, wrapped and routed", UnconnectedSend.BuildGetIdentityInSlot(0), new byte[]
        {
            0x52, 0x02, 0x20, 0x06, 0x24, 0x01,   // Unconnected_Send to the Connection Manager
            0x08, 0x04,                           // priority/tick and timeout ticks
            0x06, 0x00,                           // embedded message size
            0x01, 0x02, 0x20, 0x01, 0x24, 0x01,   // the embedded identity request
            0x01, 0x00,                           // route path size in words, then the reserved byte
            0x01, 0x00,                           // the route path itself: backplane, slot 0
        });

        // An odd-length embedded request gets a pad byte so the route path stays word aligned.
        var odd = UnconnectedSend.Build(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE }, CipRoutePath.ToSlot(2));
        Bytes("an odd embedded request is padded", odd, new byte[]
        {
            0x52, 0x02, 0x20, 0x06, 0x24, 0x01,
            0x08, 0x04,
            0x05, 0x00,                           // embedded message size, odd
            0xAA, 0xBB, 0xCC, 0xDD, 0xEE,
            0x00,                                 // the pad
            0x01, 0x00,                           // route path size in words, then reserved
            0x01, 0x02,                           // backplane, slot 2
        });
    }

    // ===================== Tag paths, reads, and values =====================

    private static void TagPathsAndValues()
    {
        Section("tag paths and values");

        // A plain name is one symbolic segment: marker, length, characters.
        Bytes("MotorSpeed", LogixTagPath.Encode("MotorSpeed"),
            new byte[] { 0x91, 0x0A }.Concat(Encoding.ASCII.GetBytes("MotorSpeed")).ToArray());

        // An odd-length name is padded so the segment stays on a 16-bit boundary.
        Bytes("Step is padded", LogixTagPath.Encode("Step"),
            new byte[] { 0x91, 0x04 }.Concat(Encoding.ASCII.GetBytes("Step")).ToArray());
        Bytes("Running is padded", LogixTagPath.Encode("Running"),
            new byte[] { 0x91, 0x07 }.Concat(Encoding.ASCII.GetBytes("Running")).Concat(new byte[] { 0x00 }).ToArray());

        // A dotted name is one segment per part, and an index is an element segment, not a symbol.
        Bytes("Stations[1].Recipe", LogixTagPath.Encode("Stations[1].Recipe"),
            new byte[] { 0x91, 0x08 }.Concat(Encoding.ASCII.GetBytes("Stations"))
                .Concat(new byte[] { 0x28, 0x01, 0x91, 0x06 })
                .Concat(Encoding.ASCII.GetBytes("Recipe"))
                .ToArray());

        // A program-scoped name keeps its colon: it is one symbol, not two.
        Bytes("Program:MainProgram.Step", LogixTagPath.Encode("Program:MainProgram.Step"),
            new byte[] { 0x91, 0x13 }.Concat(Encoding.ASCII.GetBytes("Program:MainProgram"))
                .Concat(new byte[] { 0x00, 0x91, 0x04 })
                .Concat(Encoding.ASCII.GetBytes("Step"))
                .ToArray());

        // An index past 255 needs a wider element segment, or the read silently hits the wrong one.
        Bytes("Buffer[300] widens the element segment", LogixTagPath.Encode("Buffer[300]"),
            new byte[] { 0x91, 0x06 }.Concat(Encoding.ASCII.GetBytes("Buffer"))
                .Concat(new byte[] { 0x29, 0x00, 0x2C, 0x01 })
                .ToArray());

        var read = ReadTag.Build("Step");
        Bytes("a Read Tag request asks for one element", read, new byte[]
        {
            0x4C, 0x03,
            0x91, 0x04, (byte)'S', (byte)'t', (byte)'e', (byte)'p',
            0x01, 0x00,
        });

        Check("the --describe probe appends the property symbol it asks about",
            ReadTag.BuildDescription("Step").Length == read.Length + 14,
            $"{ReadTag.BuildDescription("Step").Length} vs {read.Length}");

        // Batching has to survive a round trip, or values land against the wrong rows.
        var batch = ReadTag.Batch(new[] { ReadTag.Build("Step"), ReadTag.Build("Running"), ReadTag.Build("MotorSpeed") });
        Check("a batch names the Message Router",
            batch[0] == CipServices.MultipleServicePacket && batch[2] == 0x20 && batch[3] == 0x02);

        // Unpacking works on a batch REPLY, so the fixture is three replies laid out the way a
        // controller returns them: a count, an offset each, then the replies themselves.
        var one = new byte[] { 0xCC, 0x00, 0x00, 0x00, 0xC4, 0x00, 0x01, 0x00, 0x00, 0x00 };
        var two = new byte[] { 0xCC, 0x00, 0x00, 0x00, 0xC1, 0x00, 0x01 };
        var three = new byte[] { 0xCC, 0x00, 0x05, 0x00 };            // one member failed
        var body = BuildBatchReplyBody(one, two, three);

        Check("batch offsets are read back in order",
            ReadTag.TryUnpack(body, out var parts) && parts.Count == 3, $"{body.Length} bytes");
        Check("  each member keeps its own status",
            parts.Count == 3 && parts[0].IsSuccess && parts[1].IsSuccess && !parts[2].IsSuccess);
        Check("  a failed member does not spoil its neighbours",
            parts.Count == 3
            && TagValueDecoder.TryDecode(parts[0].Data, "DINT", out var first) && first!.Display == "1");

        // Values.
        Check("a DINT decodes",
            TagValueDecoder.TryDecode(new byte[] { 0xC4, 0x00, 0x2A, 0x00, 0x00, 0x00 }, "DINT", out var dint)
            && dint!.Display == "42" && dint.ByteCount == 4, dint?.Display);

        Check("a BOOL decodes",
            TagValueDecoder.TryDecode(new byte[] { 0xC1, 0x00, 0x01 }, "BOOL", out var b) && b!.Display == "True");

        Check("a REAL decodes",
            TagValueDecoder.TryDecode(
                new byte[] { 0xCA, 0x00, 0x00, 0x00, 0xA0, 0x41 }, "REAL", out var real)
            && real!.Display == "20", real?.Display);

        Check("a DWORD reads as hex, not a decimal blob",
            TagValueDecoder.TryDecode(
                new byte[] { 0xD3, 0x00, 0xEF, 0xBE, 0xAD, 0xDE }, "DWORD", out var dword)
            && dword!.Display == "0xDEADBEEF", dword?.Display);

        // A Logix STRING is a DINT length then the characters, and shows as text rather than bytes.
        var text = Encoding.ASCII.GetBytes("Hello");
        var stringValue = new byte[] { 0xA0, 0x02, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00 }
            .Concat(text).Concat(new byte[10]).ToArray();
        Check("a Logix STRING decodes to its text",
            TagValueDecoder.TryDecode(stringValue, "STRING", out var str) && str!.Display == "Hello",
            str?.Display);

        Check("an unknown structure says so rather than showing bytes",
            TagValueDecoder.TryDecode(
                new byte[] { 0xA0, 0x02, 0x34, 0x12, 0x01, 0x02 }, "Recipe", out var s2)
            && s2!.Display == "(Recipe)", s2?.Display);

        Check("declared sizes match the CIP types",
            TagValueDecoder.SizeOf(0xC1) == 1 && TagValueDecoder.SizeOf(0xC3) == 2
            && TagValueDecoder.SizeOf(0xC4) == 4 && TagValueDecoder.SizeOf(0xCB) == 8);

        // A reply shorter than its type used to throw for the one-byte types, because indexing a
        // short span throws a different exception from slicing one, and that ended all polling.
        bool shortRepliesAreQuestionMarks = true;
        foreach (byte code in new byte[] { 0xC1, 0xC2, 0xC6, 0xD1, 0xC3, 0xC4, 0xCA, 0xCB, 0xD4 })
        {
            try
            {
                if (!TagValueDecoder.TryDecode(new byte[] { code, 0x00 }, "", out var empty) || empty!.Display != "?")
                    shortRepliesAreQuestionMarks = false;
            }
            catch (Exception)
            {
                shortRepliesAreQuestionMarks = false;
            }
        }
        Check("a reply with no value bytes shows ? for every atomic type, and throws for none",
            shortRepliesAreQuestionMarks);

        MultiDimensionPaths();
        ReplySizedBatches();
    }

    /// <summary>
    /// Logix writes an element of a two dimensional array as Grid[1,2]. Losing an index is the
    /// dangerous failure: the path then has no element segment, and the controller answers with
    /// element zero, which looks like a perfectly good value.
    /// </summary>
    private static void MultiDimensionPaths()
    {
        var expected = new byte[] { 0x91, 0x04 }.Concat(Encoding.ASCII.GetBytes("Grid"))
            .Concat(new byte[] { 0x28, 0x01, 0x28, 0x02 }).ToArray();

        Bytes("Grid[1,2] is one symbol and two element segments", LogixTagPath.Encode("Grid[1,2]"), expected);
        Bytes("Grid[1][2], as older saved sets hold it, encodes the same", LogixTagPath.Encode("Grid[1][2]"), expected);
        Bytes("a member of an element of a 3-D array", LogixTagPath.Encode("Cube[1,2,3].Id"),
            new byte[] { 0x91, 0x04 }.Concat(Encoding.ASCII.GetBytes("Cube"))
                .Concat(new byte[] { 0x28, 0x01, 0x28, 0x02, 0x28, 0x03, 0x91, 0x02 })
                .Concat(Encoding.ASCII.GetBytes("Id")).ToArray());

        Check("an index that is not a number is refused, not dropped",
            Catch(() => LogixTagPath.Encode("Grid[1][x]")) is ArgumentException);
        Check("an unclosed bracket is refused",
            Catch(() => LogixTagPath.Encode("Grid[1")) is ArgumentException);
        Check("text between index brackets is refused",
            Catch(() => LogixTagPath.Encode("Grid[1]x[2]")) is ArgumentException);
    }

    /// <summary>
    /// A batch has to fit one message on the way back as well as on the way out. Short names on
    /// 8-byte types used to fit forty requests in the request budget and then return a reply
    /// bigger than an unconnected message can carry.
    /// </summary>
    private static void ReplySizedBatches()
    {
        // Short names reading LREALs: small requests, big replies.
        var lreals = Enumerable.Range(0, 100).Select(i => (Request: 10, Reply: (int?)(ReadTag.ReplyOverheadBytes + 8))).ToList();
        var chunks = ReadTag.Chunk(lreals, x => x.Request, x => x.Reply, 440, 440);

        int worstReply = chunks.Max(c => 6 + c.Sum(x => x.Reply!.Value + 2));
        int worstRequest = chunks.Max(c => 8 + c.Sum(x => x.Request + 2));
        Check("no batch's reply outgrows the reply budget",
            worstReply <= 440, $"largest reply {worstReply} bytes over {chunks.Count} batches");
        Check("no batch's request outgrows the request budget", worstRequest <= 440, $"{worstRequest} bytes");
        Check("every item is sent exactly once",
            chunks.Sum(c => c.Count) == lreals.Count);

        // Long names reading BOOLs: the request side is what limits these.
        var longNames = Enumerable.Range(0, 100).Select(i => (Request: 60, Reply: (int?)(ReadTag.ReplyOverheadBytes + 1))).ToList();
        Check("the request budget still limits batches of long names",
            ReadTag.Chunk(longNames, x => x.Request, x => x.Reply, 440, 440).Max(c => 8 + c.Sum(x => x.Request + 2)) <= 440);

        // A row whose size is not known yet goes alone, and does not split its neighbours' batch.
        var mixed = new List<(int Request, int? Reply)> { (10, 12), (10, null), (10, 12) };
        var mixedChunks = ReadTag.Chunk(mixed, x => x.Request, x => x.Reply, 440, 440);
        Check("a row of unknown size is read on its own",
            mixedChunks.Count == 2 && mixedChunks.Any(c => c.Count == 1 && c[0].Reply is null)
            && mixedChunks.Any(c => c.Count == 2),
            string.Join(" | ", mixedChunks.Select(c => c.Count)));
    }

    // ===================== Read-only guard =====================

    private static void ReadOnlyGuard()
    {
        Section("read-only guard");

        // Everything the app actually sends has to pass, or the guard is useless.
        Check("the identity request passes",
            !Throws(() => CipServices.AssertReadOnly(UnconnectedSend.BuildGetIdentity())));
        Check("the routed identity request passes",
            !Throws(() => CipServices.AssertReadOnly(UnconnectedSend.BuildGetIdentityInSlot(3))));
        Check("the connection manager counter request passes",
            !Throws(() => CipServices.AssertReadOnly(UnconnectedSend.BuildGetConnectionManagerCounters())));

        // The services that would change something, each rejected on its own.
        foreach (var (service, name) in new (byte, string)[]
        {
            (0x02, "Set_Attributes_All"),
            (0x04, "Set_Attribute_List"),
            (0x05, "Reset"),
            (0x06, "Start"),
            (0x07, "Stop"),
            (0x08, "Create"),
            (0x09, "Delete"),
            (0x10, "Set_Attribute_Single"),
            (0x16, "Save"),
            (0x4D, "Write Tag"),
            (0x53, "Write Tag Fragmented"),
        })
        {
            var request = new byte[] { service, 0x02, 0x20, 0x01, 0x24, 0x01 };
            Check($"  {name} (0x{service:X2}) is refused",
                Throws(() => CipServices.AssertReadOnly(request)));
        }

        // The important one: a write hidden inside an Unconnected_Send, which is what a routed
        // write to a controller behind a bridge would look like on the wire.
        var smuggled = UnconnectedSend.Build(
            new byte[] { 0x10, 0x02, 0x20, 0x01, 0x24, 0x01, 0xFF, 0xFF }, CipRoutePath.ToSlot(0));
        Check("a write wrapped in an Unconnected_Send is refused",
            Throws(() => CipServices.AssertReadOnly(smuggled)));

        // And one nested a further level, so the recursion is not just one deep.
        var doubleWrapped = UnconnectedSend.Build(smuggled, CipRoutePath.ToSlot(2));
        Check("a write wrapped twice is still refused",
            Throws(() => CipServices.AssertReadOnly(doubleWrapped)));

        // Fail closed: a wrapper whose payload cannot be located is refused rather than waved past.
        Check("an Unconnected_Send with an unreadable payload is refused",
            Throws(() => CipServices.AssertReadOnly(new byte[] { 0x52, 0x02, 0x20, 0x06, 0x24, 0x01, 0x0A, 0x04 })));
        Check("an Unconnected_Send claiming a payload longer than the request is refused",
            Throws(() => CipServices.AssertReadOnly(
                new byte[] { 0x52, 0x02, 0x20, 0x06, 0x24, 0x01, 0x0A, 0x04, 0xFF, 0x00, 0x01 })));

        // A batch is a carrier like Unconnected_Send, so a write hidden among reads must be caught.
        var mixed = ReadTag.Batch(new[]
        {
            ReadTag.Build("Step"),
            new byte[] { 0x4D, 0x02, 0x91, 0x04, (byte)'S', (byte)'t', (byte)'e', (byte)'p', 0xFF, 0xFF },
        });
        Check("a write hidden in a batch of reads is refused",
            Throws(() => CipServices.AssertReadOnly(mixed)));

        Check("a batch of reads passes",
            !Throws(() => CipServices.AssertReadOnly(
                ReadTag.Batch(new[] { ReadTag.Build("Step"), ReadTag.Build("Running") }))));

        Check("the exception names the offending service",
            Catch(() => CipServices.AssertReadOnly(new byte[] { 0x05, 0x02, 0x20, 0x01, 0x24, 0x01 }))
                is CipWriteAttemptException { Service: 0x05 });

        // 0x4C, 0x52 and 0x55 are object-specific numbers: what they do depends on the class they
        // are sent to. Each is admitted only at the target this app builds it for.
        Check("a Read Tag by name passes",
            !Throws(() => CipServices.AssertReadOnly(ReadTag.Build("Program:MainProgram.Stations[1].Faults[3]"))));
        Check("a Read Tag of a multi-dimensional element passes",
            !Throws(() => CipServices.AssertReadOnly(ReadTag.Build("Grid[2,300]"))));
        Check("a template definition read passes",
            !Throws(() => CipServices.AssertReadOnly(TemplateObject.BuildReadDefinition(0x0123, 0, 400))));
        Check("a controller tag listing passes",
            !Throws(() => CipServices.AssertReadOnly(SymbolObject.BuildListTags(70_000))));
        Check("a program tag listing passes",
            !Throws(() => CipServices.AssertReadOnly(SymbolObject.BuildListTags(1, "MainProgram"))));

        Check("  0x4C aimed at the Identity Object is refused",
            Throws(() => CipServices.AssertReadOnly(new byte[] { 0x4C, 0x02, 0x20, 0x01, 0x24, 0x01, 0x01, 0x00 })));
        Check("  0x4C aimed at a Symbol Object instance is refused",
            Throws(() => CipServices.AssertReadOnly(new byte[] { 0x4C, 0x03, 0x20, 0x6B, 0x25, 0x00, 0x01, 0x00, 0x01, 0x00 })));
        Check("  0x4C naming a tag and then a class is refused",
            Throws(() => CipServices.AssertReadOnly(
                new byte[] { 0x4C, 0x04, 0x91, 0x04, (byte)'S', (byte)'t', (byte)'e', (byte)'p', 0x20, 0x01, 0x01, 0x00 })));
        Check("  0x4C with a path segment the guard cannot read is refused",
            Throws(() => CipServices.AssertReadOnly(new byte[] { 0x4C, 0x01, 0x34, 0x00, 0x01, 0x00 })));
        Check("  0x4C with no path at all is refused",
            Throws(() => CipServices.AssertReadOnly(new byte[] { 0x4C, 0x00, 0x01, 0x00 })));
        Check("  0x4C whose path runs past the request is refused",
            Throws(() => CipServices.AssertReadOnly(new byte[] { 0x4C, 0x04, 0x91, 0x04, (byte)'S', (byte)'t' })));
        Check("  0x55 aimed at the Template Object is refused",
            Throws(() => CipServices.AssertReadOnly(new byte[] { 0x55, 0x02, 0x20, 0x6C, 0x24, 0x00, 0x01, 0x00, 0x01, 0x00 })));
        Check("  0x55 aimed at the Connection Manager is refused",
            Throws(() => CipServices.AssertReadOnly(new byte[] { 0x55, 0x02, 0x20, 0x06, 0x24, 0x01, 0x01, 0x00, 0x01, 0x00 })));
        Check("  0x52 aimed at anything but the Connection Manager is refused",
            Throws(() => CipServices.AssertReadOnly(new byte[]
            {
                0x52, 0x02, 0x20, 0x6B, 0x24, 0x01, 0x08, 0x04, 0x06, 0x00,
                0x01, 0x02, 0x20, 0x01, 0x24, 0x01, 0x01, 0x00, 0x01, 0x00,
            })));
        Check("  0x52 aimed at another Connection Manager instance is refused",
            Throws(() => CipServices.AssertReadOnly(new byte[]
            {
                0x52, 0x02, 0x20, 0x06, 0x24, 0x02, 0x08, 0x04, 0x06, 0x00,
                0x01, 0x02, 0x20, 0x01, 0x24, 0x01, 0x01, 0x00, 0x01, 0x00,
            })));
        Check("  a misaimed 0x4C inside a batch is refused",
            Throws(() => CipServices.AssertReadOnly(ReadTag.Batch(new[]
            {
                ReadTag.Build("Step"),
                new byte[] { 0x4C, 0x02, 0x20, 0x01, 0x24, 0x01, 0x01, 0x00 },
            }))));
        Check("  a misaimed 0x4C inside an Unconnected_Send is refused",
            Throws(() => CipServices.AssertReadOnly(UnconnectedSend.Build(
                new byte[] { 0x4C, 0x02, 0x20, 0x01, 0x24, 0x01, 0x01, 0x00 }, CipRoutePath.ToSlot(0)))));
    }

    // ===================== Identity =====================

    private static void IdentityParsing()
    {
        Section("identity parsing");

        var attributesAll = BuildIdentityAttributes(
            vendor: 1, deviceType: CipIdentity.DeviceTypePlc, productCode: 166,
            major: 33, minor: 11, status: 0x3160, serial: 0x00A1B2C3, name: "1756-L83E/B");

        Check("Get_Attributes_All body parses",
            CipIdentity.TryParseAttributesAll(attributesAll, out var routed) && routed is not null);
        if (routed is not null)
        {
            Check("  vendor decodes to Rockwell", routed.VendorDisplay.Contains("Rockwell"), routed.VendorDisplay);
            Check("  device type 14 reads as a controller", routed.IsController, routed.DeviceTypeDisplay);
            Check("  product name round-trips", routed.ProductName == "1756-L83E/B", routed.ProductName);
            Check("  serial formats as hex", routed.SerialNumberDisplay == "00A1B2C3", routed.SerialNumberDisplay);
            Check("  revision formats as major.minor", routed.RevisionDisplay == "33.11", routed.RevisionDisplay);
        }

        Check("a truncated identity body is refused, not guessed",
            !CipIdentity.TryParseAttributesAll(attributesAll.AsSpan(0, 12), out _));

        // Device type 12 is a communications adapter, which is emphatically not proof of a bridge:
        // a 1769-L32E controller reports it from its embedded Ethernet port.
        var adapter = BuildIdentityAttributes(1, CipIdentity.DeviceTypeCommunicationsAdapter, 90,
            20, 19, 0x0030, 0x0000ABCD, "1769-L32E Ethernet Port");
        CipIdentity.TryParseAttributesAll(adapter, out var embedded);
        Check("device type 12 does not report as a controller", embedded?.IsController == false);

        var listIdentity = BuildListIdentityFrame(IPAddress.Parse("192.168.1.10"), attributesAll);
        Check("List Identity frame parses",
            CipIdentity.TryParseListIdentity(listIdentity, out var announced, out var reported)
            && announced is not null);
        Check("  List Identity agrees with the routed read",
            announced?.ProductName == "1756-L83E/B" && announced.SerialNumber == 0x00A1B2C3);
        Check("  the sockaddr address is read from the right offset",
            Equals(reported, IPAddress.Parse("192.168.1.10")), reported?.ToString());
    }

    // ===================== Status =====================

    private static void StatusDecoding()
    {
        Section("status decoding");

        var success = new byte[] { 0x81, 0x00, 0x00, 0x00, 0xDE, 0xAD };
        Check("a success reply parses and carries its data",
            CipStatus.TryParseResponse(success, out var ok) && ok.IsSuccess && ok.Data.Length == 2);

        // The classic wrong-slot answer: general status 0x01 with extended status 0x0204.
        var timedOut = new byte[] { 0xD2, 0x00, 0x01, 0x01, 0x04, 0x02 };
        Check("a routing failure parses its extended status",
            CipStatus.TryParseResponse(timedOut, out var bad)
            && bad.GeneralStatus == CipStatus.ConnectionFailure
            && bad.ExtendedStatus == CipStatus.ExtUnconnectedRequestTimedOut);

        Check("additional status words are skipped before the data",
            CipStatus.TryParseResponse(timedOut, out var skipped) && skipped.Data.Length == 0);

        Check("0x01/0x0204 reads as an empty slot",
            CipStatus.MeansSlotIsEmpty(CipStatus.ConnectionFailure, CipStatus.ExtUnconnectedRequestTimedOut));
        Check("0x01/0x0312 reads as an empty slot",
            CipStatus.MeansSlotIsEmpty(CipStatus.ConnectionFailure, CipStatus.ExtLinkAddressNotAvailable));
        Check("0x01/0x0315 does not read as an empty slot",
            !CipStatus.MeansSlotIsEmpty(CipStatus.ConnectionFailure, CipStatus.ExtInvalidSegmentType));

        Check("an unmapped code reports its raw hex rather than a guess",
            CipStatus.Describe(0x77, 0x0999) == "CIP error 0x77/0x0999",
            CipStatus.Describe(0x77, 0x0999));
    }

    // ===================== Encapsulation =====================

    private static void EncapsulationFraming()
    {
        Section("encapsulation framing");

        var register = Encapsulation.BuildRegisterSession(0x1122334455667788);
        Check("RegisterSession is a 24-byte header plus four bytes", register.Length == 28, $"{register.Length}");
        Check("  it declares protocol version 1", register[24] == 0x01 && register[25] == 0x00);
        Check("  the header round-trips",
            Encapsulation.TryParseHeader(register, out var header)
            && header.Command == Encapsulation.CommandRegisterSession
            && header.Length == 4
            && header.SenderContext == 0x1122334455667788);

        var cip = UnconnectedSend.BuildGetIdentityInSlot(3);
        var rrData = Encapsulation.BuildSendRRData(0xABCD1234, 7, timeoutSeconds: 2, cip);
        Check("SendRRData declares the right payload length",
            Encapsulation.TryParseHeader(rrData, out var rrHeader)
            && rrHeader.Length == rrData.Length - Encapsulation.HeaderLength
            && rrHeader.SessionHandle == 0xABCD1234);

        Check("the CIP request comes back out of the unconnected data item",
            Encapsulation.TryExtractUnconnectedData(rrData.AsSpan(Encapsulation.HeaderLength), out var extracted)
            && extracted.SequenceEqual(cip));
    }

    // ===================== Fixtures =====================

    /// <summary>Lays out replies the way a controller returns a Multiple Service Packet: a count, an
    /// offset per reply measured from the count, then the replies.</summary>
    private static byte[] BuildBatchReplyBody(params byte[][] replies)
    {
        int table = 2 + replies.Length * 2;
        var body = new byte[table + replies.Sum(r => r.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), (ushort)replies.Length);

        int offset = table;
        int cursor = table;
        for (int i = 0; i < replies.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2 + i * 2), (ushort)offset);
            replies[i].CopyTo(body, cursor);
            cursor += replies[i].Length;
            offset += replies[i].Length;
        }
        return body;
    }


    private static byte[] BuildIdentityAttributes(
        ushort vendor, ushort deviceType, ushort productCode,
        byte major, byte minor, ushort status, uint serial, string name)
    {
        var data = new byte[15 + name.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0), vendor);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), deviceType);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), productCode);
        data[6] = major;
        data[7] = minor;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), status);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(10), serial);
        data[14] = (byte)name.Length;
        Encoding.ASCII.GetBytes(name).CopyTo(data.AsSpan(15));
        return data;
    }

    /// <summary>
    /// A List Identity reply carries the same identity fields as a routed read, but behind an
    /// encapsulation protocol version and a 16-byte sockaddr, which is why the two parsers differ.
    /// </summary>
    private static byte[] BuildListIdentityFrame(IPAddress address, byte[] identityAttributes)
    {
        var item = new byte[18 + identityAttributes.Length + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(0), 1);          // encapsulation protocol version
        BinaryPrimitives.WriteUInt16BigEndian(item.AsSpan(2), 2);             // sin_family, network order
        BinaryPrimitives.WriteUInt16BigEndian(item.AsSpan(4), Encapsulation.Port);
        address.GetAddressBytes().CopyTo(item.AsSpan(6));
        identityAttributes.CopyTo(item.AsSpan(18));
        item[^1] = 0x03;                                                      // state byte after the name

        var data = new byte[6 + item.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0), 1);          // item count
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 0x000C);     // CIP Identity item
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)item.Length);
        item.CopyTo(data.AsSpan(6));

        return Encapsulation.BuildFrame(Encapsulation.CommandListIdentity, 0, 0, data);
    }

    // ===================== Tag set files =====================

    /// <summary>
    /// Pins the save format. These files outlive any one build, so the checks here are about
    /// compatibility in both directions: a file written today still loads after the schema grows,
    /// and a file from a newer build is refused with a sentence rather than half-read.
    /// </summary>
    private static void TagSetFiles()
    {
        Section("tag set files");

        var original = new TagSetDocument
        {
            Controller = new ControllerReference { Address = "192.168.1.10", Slot = 0, ProductName = "1756-L8xE" },
            DefaultScanRateMs = 250,
            Tags =
            {
                new TagSetEntry { Address = "Tank_Level", DataType = "REAL", ScanRateMs = 100, Description = "feed tank" },
                new TagSetEntry { Address = "Program:MainProgram.Counter", DataType = "DINT", ScanRateMs = 1000 },
                new TagSetEntry { Address = "Zones[2].Setpoint", DataType = "REAL", ScanRateMs = 250 },
            },
        };

        var reloaded = TagSetFile.Deserialize(TagSetFile.Serialize(original));
        Check("round trip keeps every tag, in order",
            reloaded.Tags.Select(t => t.Address).SequenceEqual(original.Tags.Select(t => t.Address)));
        Check("round trip keeps per tag scan rates",
            reloaded.Tags.Select(t => t.ScanRateMs).SequenceEqual(new[] { 100, 1000, 250 }));
        Check("round trip keeps data types and descriptions",
            reloaded.Tags[0].DataType == "REAL" && reloaded.Tags[0].Description == "feed tank");
        Check("round trip keeps the controller it was saved from",
            reloaded.Controller?.Address == "192.168.1.10" && reloaded.Controller?.Slot == 0);

        // Unknown properties are ignored, so a file written by a later build still gives up its tags.
        const string fromLaterBuild = """
            {
              "format": "opcua-playground.tagset",
              "version": 1,
              "somethingAddedLater": { "deadband": 0.5 },
              "tags": [ { "address": "Tank_Level", "dataType": "REAL", "scanRateMs": 100, "trend": true } ]
            }
            """;
        Check("a field this build has never heard of is ignored",
            TagSetFile.Deserialize(fromLaterBuild).Tags[0].Address == "Tank_Level");

        // A version bump is the opposite case: refuse, rather than silently drop what it holds.
        const string fromFutureVersion = """{ "format": "opcua-playground.tagset", "version": 99, "tags": [] }""";
        Check("a newer file version is refused, not half read",
            Catch(() => TagSetFile.Deserialize(fromFutureVersion)) is TagSetFormatException);

        Check("someone else's JSON is refused",
            Catch(() => TagSetFile.Deserialize("""{ "name": "not ours" }""")) is TagSetFormatException);

        // The marker cannot be a property default, or the check above would pass anything.
        Check("JSON with no format marker at all is refused",
            Catch(() => TagSetFile.Deserialize("""{ "version": 1, "tags": [] }""")) is TagSetFormatException);

        Check("a file that is not JSON at all is refused",
            Catch(() => TagSetFile.Deserialize("hello")) is TagSetFormatException);

        Check("an absurd scan rate is clamped rather than trusted",
            TagSetFile.Deserialize("""
                { "format": "opcua-playground.tagset", "version": 1,
                  "tags": [ { "address": "A", "scanRateMs": 5000000 } ] }
                """).Tags[0].ScanRateMs == MonitoredTag.MaxScanRateMs);

        Check("a bare file name gains the tag set extension",
            TagSetFile.EnsureExtension("line3") == "line3" + TagSetFile.Extension);
        Check("a name the user already extended is left alone",
            TagSetFile.EnsureExtension("line3.json") == "line3.json");

        original.Recordings.Add(new RecordedSession
        {
            Name = "recording1",
            Recorded = new DateTimeOffset(2026, 9, 3, 20, 30, 0, TimeSpan.FromHours(-5)),
            DurationMs = 30_000,
            MaxSeconds = 60,
            Tags = original.Tags
                .Select(t => new RecordedTag { Address = t.Address, DataType = t.DataType, ScanRateMs = t.ScanRateMs })
                .ToList(),
            Changes = { RecordingChange.Value(0, 0, "41.2"), RecordingChange.Value(250, 0, "41.4") },
        });

        TagSetOnDisk(original);
    }

    /// <summary>Through a real file, because a save that only works in memory is not a save.</summary>
    private static void TagSetOnDisk(TagSetDocument document)
    {
        string folder = Path.Combine(Path.GetTempPath(), "OpcUaPlayground-selftest");
        Directory.CreateDirectory(folder);
        string setPath = Path.Combine(folder, "set" + TagSetFile.Extension);

        try
        {
            TagSetFile.Save(setPath, document);
            var back = TagSetFile.Load(setPath);

            Check("a saved file loads back with its tags", back.Tags.Count == document.Tags.Count);
            Check("a saved file loads back with its recordings",
                back.Recordings.Count == document.Recordings.Count);
            Check("a recording keeps its name, timestamp and changes",
                back.Recordings[0].Name == "recording1"
                && back.Recordings[0].Recorded == document.Recordings[0].Recorded
                && back.Recordings[0].Changes.SequenceEqual(document.Recordings[0].Changes));
            Check("a recording keeps the tag list it was captured against",
                back.Recordings[0].Tags.Select(t => t.Address)
                    .SequenceEqual(document.Recordings[0].Tags.Select(t => t.Address)));
            Check("a recorded tag keeps its datatype and scan rate",
                back.Recordings[0].Tags[0].DataType == "REAL"
                && back.Recordings[0].Tags[0].ScanRateMs == 100);

            // The point of the temporary file: a save leaves one file behind, not two.
            Check("saving twice leaves one file, not a stray temporary",
                Directory.GetFiles(folder, "*.saving").Length == 0);

            // Finishing, deleting or cropping a run writes the runs and nothing else. The tags on
            // disk stay as last saved, so an unsaved table edit is still the user's to keep or
            // discard, rather than being saved behind their back by a recording.
            var second = new RecordedSession
            {
                Name = "recording2",
                Recorded = back.Recordings[0].Recorded.AddMinutes(5),
                DurationMs = 1_000,
                MaxSeconds = 60,
                Tags = back.Recordings[0].Tags,
                Changes = { RecordingChange.Value(0, 0, "7") },
            };
            TagSetFile.SaveRecordings(setPath, new[] { back.Recordings[0], second });
            var runsWritten = TagSetFile.Load(setPath);

            Check("writing the runs leaves the saved tags as they were",
                runsWritten.Tags.Select(t => (t.Address, t.DataType, t.ScanRateMs, t.Description))
                    .SequenceEqual(back.Tags.Select(t => (t.Address, t.DataType, t.ScanRateMs, t.Description)))
                && runsWritten.DefaultScanRateMs == back.DefaultScanRateMs);
            Check("writing the runs stores every run, in order",
                runsWritten.Recordings.Select(r => r.Name).SequenceEqual(new[] { "recording1", "recording2" }));

            TagSetFile.SaveRecordings(setPath, Array.Empty<RecordedSession>());
            var runsCleared = TagSetFile.Load(setPath);
            Check("writing no runs removes them and keeps the tags",
                runsCleared.Recordings.Count == 0 && runsCleared.Tags.Count == back.Tags.Count);

            Check("writing runs to a file that has gone fails rather than creating one",
                Throws(() => TagSetFile.SaveRecordings(
                    Path.Combine(folder, "gone" + TagSetFile.Extension), Array.Empty<RecordedSession>()))
                && !File.Exists(Path.Combine(folder, "gone" + TagSetFile.Extension)));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    // ===================== Recordings =====================

    /// <summary>
    /// The recording rules the user asked for, pinned: a default name can never land on an
    /// existing run, and a change line survives whatever a Logix STRING puts in it.
    /// </summary>
    private static void Recordings()
    {
        Section("recordings");

        Check("a value change round trips",
            RoundTrips(RecordingChange.Value(1250, 3, "41.7"), 1250, 3, false, "41.7"));
        Check("an error change round trips",
            RoundTrips(RecordingChange.Error(0, 0, "path destination unknown"),
                       0, 0, true, "path destination unknown"));

        // A Logix STRING can hold the separator, so only the first three are separators.
        Check("a value containing the separator survives",
            RoundTrips(RecordingChange.Value(90, 2, "a|b|c"), 90, 2, false, "a|b|c"));

        Check("a malformed change line is rejected rather than half read",
            !RecordingChange.TryParse("1250|3", out _, out _, out _, out _));
        Check("an unknown change kind is rejected",
            !RecordingChange.TryParse("1250|3|x|41.7", out _, out _, out _, out _));

        // A change line has to stay small. This is the whole reason it is a string.
        Check("a change line stays around twenty bytes",
            RecordingChange.Value(1250, 3, "41.7").Length <= 24);

        var existing = new List<RecordedSession>
        {
            new() { Name = "recording1" },
            new() { Name = "recording2" },
        };
        Check("the default name skips the numbers already used",
            TagRecorder.NextDefaultName(existing) == "recording3");

        // Numbering follows names, not a count, so a hand named run cannot push the default onto
        // one that already exists.
        Check("a hand named run does not shift the default onto an existing name",
            TagRecorder.NextDefaultName(new List<RecordedSession> { new() { Name = "morning run" } })
                == "recording1");

        Check("a gap in the numbering is filled before the end",
            TagRecorder.NextDefaultName(new List<RecordedSession> { new() { Name = "recording2" } })
                == "recording1");

        Check("the default name ignores case when looking for a clash",
            TagRecorder.NextDefaultName(new List<RecordedSession> { new() { Name = "RECORDING1" } })
                == "recording2");

        Check("a run with no name is dropped rather than kept unfindable",
            TagSetFile.Deserialize("""
                { "format": "opcua-playground.tagset", "version": 2, "tags": [],
                  "recordings": [ { "name": "  ", "changes": [] }, { "name": "kept" } ] }
                """).Recordings.Single().Name == "kept");
    }

    /// <summary>
    /// Pressing Record can start the poller, so at that instant no row has been read and every cell
    /// is empty. Writing those empties as the starting state made the first moment of a run blank.
    /// A row's t=0 line now waits for its first reading.
    /// </summary>
    private static void RecordingBaseline()
    {
        Section("recording baseline");

        var read = new MonitoredTag("Read", "DINT", 100, 4);
        read.ApplyReading("7", null, 4);
        var unread = new MonitoredTag("Unread", "DINT", 100, 4);
        var never = new MonitoredTag("Never", "DINT", 100, 4);

        var recorder = new TagRecorder();
        recorder.Start(new[] { read, unread, never }, 5);
        Thread.Sleep(30);
        unread.ApplyReading("41", null, 4);      // its first reading, a moment after Record
        Thread.Sleep(30);
        unread.ApplyReading("42", null, 4);      // then a real change
        read.ApplyReading("8", null, 4);
        var run = recorder.Stop("baseline")!;
        var playback = TagPlayback.Load(run);

        Check("a row read before Record starts from its value",
            playback.TryStateAt(0, 0, out _, out string readAtZero) && readAtZero == "7", readAtZero);
        Check("a row not read yet starts from its first reading, not a blank",
            playback.TryStateAt(1, 0, out _, out string unreadAtZero) && unreadAtZero == "41", unreadAtZero);
        Check("filling a row in is not counted as a change of it",
            run.Changes.Count(l => l.Split('|')[1] == "1") == 2,
            string.Join(" ", run.Changes.Where(l => l.Split('|')[1] == "1")));
        Check("its later changes are still recorded when they happen",
            playback.TryStateAt(1, playback.DurationMs, out _, out string unreadAtEnd) && unreadAtEnd == "42"
            && run.Changes.Any(l => l.StartsWith("0|", StringComparison.Ordinal) == false && l.EndsWith("|v|42", StringComparison.Ordinal)));
        Check("a row never read gets no made-up starting value",
            !playback.TryStateAt(2, playback.DurationMs, out _, out _));
        Check("no line in the run is a blank value",
            !run.Changes.Any(l => l.EndsWith("|v|", StringComparison.Ordinal)), string.Join(" ", run.Changes));

        var times = run.Changes.Select(l => long.Parse(l.Split('|')[0])).ToList();
        Check("the lines are in time order, the late starting line included",
            times.Zip(times.Skip(1)).All(p => p.First <= p.Second), string.Join(",", times));

        // A first reading can be a failure, and that is the starting state too.
        var failing = new MonitoredTag("Failing", "DINT", 100, 4);
        recorder.Start(new[] { failing }, 5);
        failing.ApplyReading("", "no reply", null);
        var failed = TagPlayback.Load(recorder.Stop("failing")!);
        Check("a first reading that failed is the starting state, as an error",
            failed.TryStateAt(0, 0, out bool isError, out string error) && isError && error == "no reply");

        // What the poller does on Start: a value left over from before no longer counts.
        var stale = new MonitoredTag("Stale", "DINT", 100, 4);
        stale.ApplyReading("old", null, 4);
        stale.ForgetReading();
        recorder.Start(new[] { stale }, 5);
        stale.ApplyReading("new", null, 4);
        var restarted = TagPlayback.Load(recorder.Stop("restarted")!);
        Check("a value from before polling restarted is not taken as the starting state",
            restarted.TryStateAt(0, 0, out _, out string fresh) && fresh == "new", fresh);
    }

    private static bool RoundTrips(string line, long ms, int index, bool isError, string text) =>
        RecordingChange.TryParse(line, out long gotMs, out int gotIndex, out bool gotError, out string gotText)
        && gotMs == ms && gotIndex == index && gotError == isError && gotText == text;

    // ===================== Playback =====================

    /// <summary>
    /// The seek index is the part worth pinning. Everything else in playback is presentation, but a
    /// binary search that disagrees with a plain replay would show the wrong value at the wrong
    /// moment and look entirely convincing while doing it.
    /// </summary>
    private static void Playback()
    {
        Section("playback");

        var session = GenerateRun();
        var playback = TagPlayback.Load(session);

        // The check that matters: the fast path has to equal the obvious one, everywhere.
        var random = new Random(4242);
        bool agrees = true;
        string mismatch = "";

        for (int sample = 0; sample < 400 && agrees; sample++)
        {
            int ms = sample < 200
                ? sample * playback.DurationMs / 199
                : random.Next(-50, playback.DurationMs + 200);

            for (int tag = 0; tag < playback.Tags.Count; tag++)
            {
                bool found = playback.TryStateAt(tag, ms, out bool isError, out string text);
                var expected = NaiveStateAt(session, tag, ms);

                if (found != expected.HasValue
                    || (found && (isError != expected!.Value.IsError || text != expected.Value.Text)))
                {
                    agrees = false;
                    mismatch = $"tag {tag} at {ms} ms";
                    break;
                }
            }
        }

        Check("seeking agrees with replaying from the start, at every sampled moment",
            agrees, agrees ? null : mismatch);

        Check("the state at zero is the baseline the recorder wrote",
            playback.TryStateAt(0, 0, out _, out string atZero) && atZero == "0");

        playback.TryStateAt(0, playback.DurationMs, out _, out string atEnd);
        Check("the state past the end holds the last value",
            playback.TryStateAt(0, playback.DurationMs + 60_000, out _, out string afterEnd)
            && afterEnd == atEnd);

        // The last tag was given a baseline and nothing else.
        int quiet = playback.Tags.Count - 1;
        Check("a tag that never changed keeps its baseline throughout",
            playback.TryStateAt(quiet, playback.DurationMs, out _, out string quietText)
            && quietText == "0");

        Check("an error state comes back as an error, not as a value",
            playback.TryStateAt(0, ErrorAt, out bool wasError, out string errorText)
            && wasError && errorText == "no reply");

        // Stepping. The times either side of a change, and landing exactly on one.
        int first = playback.ChangeTimes[0];
        int second = playback.ChangeTimes[1];
        int last = playback.ChangeTimes[^1];

        Check("stepping back from the first change stays at the start",
            playback.PreviousChangeTime(first) == 0);
        Check("stepping back from exactly on a change lands on the one before it",
            playback.PreviousChangeTime(second) == first);
        Check("stepping forward from exactly on a change lands on the next one",
            playback.NextChangeTime(first) == second);
        Check("stepping forward from the last change lands on the end",
            playback.NextChangeTime(last) == playback.DurationMs);

        Check("the density buckets account for every change",
            playback.Density(64).Sum() == playback.ChangeCount);
        Check("density survives being asked for one bucket",
            playback.Density(1)[0] == playback.ChangeCount);

        // A hand-edited file can hold lines out of order. The index sorts by time, so the state at
        // a moment is the latest change BY TIME, not the last one that happens to appear.
        var shuffled = new RecordedSession { Name = "shuffled", DurationMs = 1000 };
        shuffled.Tags.Add(new RecordedTag { Address = "A" });
        shuffled.Changes.Add(RecordingChange.Value(500, 0, "late"));
        shuffled.Changes.Add(RecordingChange.Value(100, 0, "early"));
        var reordered = TagPlayback.Load(shuffled);
        Check("changes written out of order still resolve by time",
            reordered.TryStateAt(0, 200, out _, out string early) && early == "early"
            && reordered.TryStateAt(0, 600, out _, out string late) && late == "late");

        // The bar has to cover changes written past the recorded duration.
        var overrun = new RecordedSession { Name = "overrun", DurationMs = 100 };
        overrun.Tags.Add(new RecordedTag { Address = "A" });
        overrun.Changes.Add(RecordingChange.Value(5000, 0, "late"));
        Check("a change past the recorded duration extends the scrub bar",
            TagPlayback.Load(overrun).DurationMs == 5000);

        // Junk is counted, not swallowed, and not mistaken for a real change.
        var damaged = new RecordedSession { Name = "damaged", DurationMs = 1000 };
        damaged.Tags.Add(new RecordedTag { Address = "A" });
        damaged.Changes.Add(RecordingChange.Value(0, 0, "fine"));
        damaged.Changes.Add("not a change line");
        damaged.Changes.Add("100|9|v|tag nine does not exist");
        var repaired = TagPlayback.Load(damaged);
        Check("unreadable change lines are counted rather than swallowed", repaired.SkippedLines == 2);
        Check("unreadable change lines do not become changes", repaired.ChangeCount == 1);

        // Both on-disk tag shapes, in one file, because the first recordings wrote bare strings.
        var mixed = TagSetFile.Deserialize("""
            { "format": "opcua-playground.tagset", "version": 2, "tags": [],
              "recordings": [ { "name": "mixed", "durationMs": 10,
                "tags": [ "OldStyle", { "address": "NewStyle", "dataType": "DINT", "scanRateMs": 250 } ],
                "changes": [ "0|0|v|1", "0|1|v|2" ] } ] }
            """).Recordings.Single();

        Check("a recorded tag written as a bare address string still loads",
            mixed.Tags[0].Address == "OldStyle" && mixed.Tags[0].DataType == "");
        Check("a recorded tag written as an object keeps its fields",
            mixed.Tags[1].Address == "NewStyle" && mixed.Tags[1].DataType == "DINT"
            && mixed.Tags[1].ScanRateMs == 250);
    }

    /// <summary>The moment the generated run puts an error on tag zero.</summary>
    private const int ErrorAt = 3000;

    /// <summary>
    /// A run with enough churn to be worth searching: six tags, a baseline each, then a couple of
    /// thousand changes at uneven intervals. The last tag is left quiet on purpose, and a known
    /// error with a recovery after it sits in the middle.
    ///
    /// The lines come out in time order, because that is what the recorder writes and what the
    /// replay this is compared against assumes. A file whose lines are out of order is a separate
    /// case with its own check, since only the index handles it.
    /// </summary>
    private static RecordedSession GenerateRun()
    {
        var session = new RecordedSession
        {
            Name = "generated",
            Recorded = new DateTimeOffset(2026, 9, 3, 22, 41, 45, TimeSpan.FromHours(-5)),
            MaxSeconds = 10,
        };

        for (int i = 0; i < 6; i++)
            session.Tags.Add(new RecordedTag { Address = $"Tag{i}", DataType = "DINT", ScanRateMs = 100 });

        var entries = new List<(int Time, int Tag, bool IsError, string Text)>();
        for (int i = 0; i < session.Tags.Count; i++) entries.Add((0, i, false, "0"));

        // Nothing but baselines at zero, so the baseline check tests what it says it does.
        var random = new Random(20260903);
        int time = 1;
        for (int n = 0; n < 2000; n++)
        {
            entries.Add((time, random.Next(session.Tags.Count - 1), false, $"v{n}"));
            time += random.Next(0, 12);      // a zero step, so two changes can share an instant
        }

        entries.Add((ErrorAt, 0, true, "no reply"));
        entries.Add((ErrorAt + 1, 0, false, "recovered"));

        // Stable, so the error added last wins against anything else at that same instant.
        foreach (var entry in entries.OrderBy(e => e.Time))
            session.Changes.Add(entry.IsError
                ? RecordingChange.Error(entry.Time, entry.Tag, entry.Text)
                : RecordingChange.Value(entry.Time, entry.Tag, entry.Text));

        session.DurationMs = Math.Max(time, ErrorAt + 1);
        return session;
    }

    /// <summary>Replaying from the start, in file order. What the index has to agree with.</summary>
    private static (bool IsError, string Text)? NaiveStateAt(RecordedSession session, int tagIndex, int ms)
    {
        (bool IsError, string Text)? state = null;

        foreach (string line in session.Changes)
            if (RecordingChange.TryParse(line, out long time, out int index, out bool isError, out string text)
                && index == tagIndex && time <= ms)
                state = (isError, text);

        return state;
    }

    // ===================== Crop =====================

    /// <summary>
    /// A crop has one job: play back exactly as the same window of the original did. The recorder
    /// only writes a tag when it changes, so the easy way to get this wrong is a crop whose rows go
    /// blank because the change that set them happened before the cut. The equivalence check below
    /// catches that and every off-by-one at either end.
    /// </summary>
    private static void Crop()
    {
        Section("crop");

        var source = GenerateRun();
        var original = TagPlayback.Load(source);
        int duration = original.DurationMs;
        var times = original.ChangeTimes;
        string before = string.Join("\n", source.Changes);

        // The whole run, cuts landing exactly on changes, cuts either side of them, a start on the
        // error, and then random windows.
        var windows = new List<(int From, int To)>
        {
            (0, duration),
            (times[10], times[^10]),
            (times[10] - 1, times[^10] + 1),
            (times[10] + 1, times[^10] - 1),
            (ErrorAt, ErrorAt + 500),
            (ErrorAt + 1, duration),
            (0, 1),
            (duration - 1, duration),
        };
        var random = new Random(7);
        while (windows.Count < 50)
        {
            int a = random.Next(0, duration);
            int b = random.Next(0, duration);
            if (a != b) windows.Add((Math.Min(a, b), Math.Max(a, b)));
        }

        bool agrees = true;
        string mismatch = "";
        foreach (var (from, to) in windows)
        {
            var cropped = TagPlayback.Load(RecordingCrop.Apply(source, from, to));
            if (!SameStates(original, from, cropped, 0, to - from, out string where))
            {
                agrees = false;
                mismatch = $"window {from}..{to}, {where}";
                break;
            }
        }
        Check("a crop plays back exactly as the same window of the original, in 50 windows",
            agrees, agrees ? null : mismatch);

        int cutFrom = times[40];
        int cutTo = times[^40];
        var run = RecordingCrop.Apply(source, cutFrom, cutTo);

        Check("the cropped run lasts as long as the window",
            run.DurationMs == cutTo - cutFrom, $"{run.DurationMs} ms");
        Check("the recorded time moves to the cut, so the clock still reads the time of day",
            run.Recorded == source.Recorded.AddMilliseconds(cutFrom));

        var baseline = run.Changes
            .Select(line => RecordingChange.TryParse(line, out long t, out int i, out _, out _) ? (t, i) : (-1L, -1))
            .Where(c => c.Item1 == 0)
            .Select(c => c.Item2)
            .ToHashSet();
        Check("every tag gets a starting line at the cut", baseline.Count == source.Tags.Count,
            $"{baseline.Count} of {source.Tags.Count}");

        var runPlayback = TagPlayback.Load(run);
        Check("a tag that never changed keeps its baseline",
            runPlayback.TryStateAt(source.Tags.Count - 1, run.DurationMs, out _, out string quiet) && quiet == "0");

        var onError = TagPlayback.Load(RecordingCrop.Apply(source, ErrorAt, ErrorAt + 1));
        Check("a crop starting on an error starts in error",
            onError.TryStateAt(0, 0, out bool isError, out string errorText) && isError && errorText == "no reply");

        bool inside = run.Changes.All(line =>
            RecordingChange.TryParse(line, out long t, out _, out _, out _) && t >= 0 && t <= run.DurationMs);
        Check("no line falls outside the window", inside);
        Check("nothing is unreadable in a crop", runPlayback.SkippedLines == 0);

        // Cropping twice has to be the same as cropping once to where the two end up.
        var twice = RecordingCrop.Apply(run, 500, run.DurationMs - 500);
        var once = RecordingCrop.Apply(source, cutFrom + 500, cutTo - 500);
        Check("a crop of a crop equals one crop of the combined window",
            SameStates(TagPlayback.Load(once), 0, TagPlayback.Load(twice), 0, once.DurationMs, out string nested)
            && twice.DurationMs == once.DurationMs && twice.Recorded == once.Recorded,
            nested);

        Check("the original is not touched", string.Join("\n", source.Changes) == before);
        Check("the cropped run's tags are copies, not the original's",
            !ReferenceEquals(run.Tags[0], source.Tags[0]) && run.Tags[0].Address == source.Tags[0].Address);
        Check("a crop keeps the run's name until it is given one", run.Name == source.Name);

        Check("a window with no length is refused",
            Catch(() => RecordingCrop.Apply(source, 500, 500)) is ArgumentException);
        Check("a window ending before it starts is refused",
            Catch(() => RecordingCrop.Apply(source, 900, 100)) is ArgumentException);
        Check("a window past the end is clamped to the run",
            RecordingCrop.Apply(source, duration - 100, duration + 5000).DurationMs == 100);

        // A hand edited file can hold a tag with no baseline. It stays blank until it first changes,
        // as it did in the original, rather than being given a value from nowhere.
        var sparse = new RecordedSession { Name = "sparse", DurationMs = 1000 };
        sparse.Tags.Add(new RecordedTag { Address = "A" });
        sparse.Tags.Add(new RecordedTag { Address = "B" });
        sparse.Changes.Add(RecordingChange.Value(0, 0, "a0"));
        sparse.Changes.Add(RecordingChange.Value(600, 1, "b600"));
        var sparseCrop = TagPlayback.Load(RecordingCrop.Apply(sparse, 200, 1000));
        Check("a tag with nothing yet at the cut stays blank until its first change",
            !sparseCrop.TryStateAt(1, 0, out _, out _)
            && sparseCrop.TryStateAt(1, 400, out _, out string late) && late == "b600");

        var names = new List<RecordedSession> { new() { Name = "recording3" } };
        Check("a crop is offered its source's name with -crop",
            RecordingCrop.SuggestName("recording3", names) == "recording3-crop");
        names.Add(new RecordedSession { Name = "RECORDING3-CROP" });
        Check("the suggested name moves on when -crop is taken, ignoring case",
            RecordingCrop.SuggestName("recording3", names) == "recording3-crop2");
    }

    /// <summary>
    /// Whether two playbacks show the same thing over a span: <paramref name="a"/> from
    /// <paramref name="aFrom"/> and <paramref name="b"/> from <paramref name="bFrom"/>, compared at
    /// both ends, at every change time either one has inside the span, and a millisecond either
    /// side of those.
    /// </summary>
    private static bool SameStates(TagPlayback a, int aFrom, TagPlayback b, int bFrom, int span, out string where)
    {
        var moments = new SortedSet<int> { 0, span };
        foreach (int t in a.ChangeTimes) AddAround(moments, t - aFrom, span);
        foreach (int t in b.ChangeTimes) AddAround(moments, t - bFrom, span);

        foreach (int m in moments)
        {
            for (int tag = 0; tag < a.Tags.Count; tag++)
            {
                bool foundA = a.TryStateAt(tag, aFrom + m, out bool errorA, out string textA);
                bool foundB = b.TryStateAt(tag, bFrom + m, out bool errorB, out string textB);
                if (foundA != foundB || errorA != errorB || textA != textB)
                {
                    where = $"tag {tag} at +{m} ms: '{textA}' against '{textB}'";
                    return false;
                }
            }
        }

        where = "";
        return true;

        static void AddAround(SortedSet<int> moments, int t, int span)
        {
            for (int d = -1; d <= 1; d++)
                if (t + d >= 0 && t + d <= span) moments.Add(t + d);
        }
    }

    // ===================== Recorded clock =====================

    /// <summary>
    /// A run's recorded time is when it started, because playback adds elapsed time to it. Version 2
    /// files stamped the stop instead, which put every clock time late by the length of the run.
    /// </summary>
    private static void RecordedClock()
    {
        Section("recorded clock");

        var fromVersion2 = TagSetFile.Deserialize("""
            { "format": "opcua-playground.tagset", "version": 2, "tags": [],
              "recordings": [ { "name": "old", "recorded": "2026-09-09T14:55:00-05:00", "durationMs": 5000 } ] }
            """);
        Check("a version 2 run is moved back to when it started",
            fromVersion2.Recordings[0].Recorded == new DateTimeOffset(2026, 9, 9, 14, 54, 55, TimeSpan.FromHours(-5)),
            fromVersion2.Recordings[0].Recorded.ToString("O"));
        Check("a corrected file is marked current, so a save does not correct it twice",
            fromVersion2.Version == TagSetDocument.CurrentVersion);

        var fromVersion3 = TagSetFile.Deserialize("""
            { "format": "opcua-playground.tagset", "version": 3, "tags": [],
              "recordings": [ { "name": "new", "recorded": "2026-09-09T14:55:00-05:00", "durationMs": 5000 } ] }
            """);
        Check("a version 3 run is left where it is",
            fromVersion3.Recordings[0].Recorded == new DateTimeOffset(2026, 9, 9, 14, 55, 0, TimeSpan.FromHours(-5)));

        // Round trip: loaded, saved and loaded again, it must not move a second time.
        var again = TagSetFile.Deserialize(TagSetFile.Serialize(fromVersion2));
        Check("a corrected run stays put through a save and a load",
            again.Recordings[0].Recorded == fromVersion2.Recordings[0].Recorded);

        Check("a version 2 run with no recorded time loads without moving",
            TagSetFile.Deserialize("""
                { "format": "opcua-playground.tagset", "version": 2, "tags": [],
                  "recordings": [ { "name": "bare", "durationMs": 5000 } ] }
                """).Recordings[0].Recorded == default);

        // The recorder itself. Stamped at Stop, this would land about 60 ms after the start.
        var recorder = new TagRecorder();
        var tags = new List<MonitoredTag> { new("A", "DINT", 100, 4) };
        var startedBy = DateTimeOffset.Now;
        recorder.Start(tags, 5);
        Thread.Sleep(60);
        var session = recorder.Stop("clock")!;
        double lateBy = (session.Recorded - startedBy).TotalMilliseconds;
        Check("the recorder stamps a run with its start, not its stop",
            lateBy >= 0 && lateBy < 30, $"{lateBy:0} ms after Start was called, run lasted {session.DurationMs} ms");
    }

    // ===================== Recording export =====================

    /// <summary>
    /// The CSV is what leaves this program for a spreadsheet, so its shape is pinned here: a row
    /// per instant, a column per tag, and cells carried forward. Getting the forward fill wrong
    /// would produce a file full of gaps that still opens and charts, which is the worst kind of
    /// wrong.
    /// </summary>
    private static void Csv()
    {
        Section("recording export");

        var recorded = new DateTimeOffset(2026, 9, 8, 14, 30, 0, TimeSpan.FromHours(-5));
        var session = new RecordedSession
        {
            Name = "run one",
            Recorded = recorded,
            DurationMs = 1000,
            Tags =
            {
                new RecordedTag { Address = "Tank_Level", DataType = "REAL" },
                new RecordedTag { Address = "Pump.Run", DataType = "BOOL" },
            },
            Changes =
            {
                RecordingChange.Value(0, 0, "1.5"),
                RecordingChange.Value(0, 1, "False"),
                RecordingChange.Value(400, 0, "2.5"),
                RecordingChange.Error(700, 1, "no reply"),
            },
        };

        var lines = RecordingCsv.Build(TagPlayback.Load(session))
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Check("the header names the two time columns and every tag",
            lines[0] == "elapsed_ms,time,Tank_Level,Pump.Run", lines[0]);

        // Instants where something changed, plus both ends of the run.
        Check("there is a row per instant, plus the start and the end",
            lines.Length == 5, $"{lines.Length} lines");

        Check("the first row is the start of the run",
            lines[1] == "0,2026-09-08 14:30:00.000,1.5,False", lines[1]);

        // The whole point of the wide shape: a tag that did not change still reports its value.
        Check("a tag that did not change is carried forward",
            lines[2] == "400,2026-09-08 14:30:00.400,2.5,False", lines[2]);

        // A failed read is written as its reason. A blank would read as a missing sample.
        Check("an error is written as its text, not as a gap",
            lines[3] == "700,2026-09-08 14:30:00.700,2.5,no reply", lines[3]);

        Check("the last row is the end of the run, not the last change",
            lines[4].StartsWith("1000,2026-09-08 14:30:01.000,", StringComparison.Ordinal), lines[4]);

        Check("rows end with a carriage return and a line feed",
            RecordingCsv.Build(TagPlayback.Load(session)).Contains("\r\n", StringComparison.Ordinal));

        // Quoting, since a Logix STRING can hold anything at all.
        var awkward = new RecordedSession
        {
            Name = "awkward",
            Recorded = recorded,
            DurationMs = 10,
            Tags = { new RecordedTag { Address = "Note", DataType = "STRING" } },
            Changes = { RecordingChange.Value(0, 0, "a,b"), RecordingChange.Value(5, 0, "say \"hi\"") },
        };
        var quoted = RecordingCsv.Build(TagPlayback.Load(awkward))
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Check("a value holding a comma is quoted",
            quoted[1].EndsWith(",\"a,b\"", StringComparison.Ordinal), quoted[1]);
        Check("a value holding a quote has its quotes doubled",
            quoted[2].EndsWith(",\"say \"\"hi\"\"\"", StringComparison.Ordinal), quoted[2]);

        // An empty run still produces a readable file rather than an exception.
        var empty = new RecordedSession { Name = "empty", Recorded = recorded };
        empty.Tags.Add(new RecordedTag { Address = "A" });
        var emptyLines = RecordingCsv.Build(TagPlayback.Load(empty))
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Check("a run with no changes still gives a header and a row",
            emptyLines.Length == 2 && emptyLines[0] == "elapsed_ms,time,A", string.Join(" | ", emptyLines));

        Check("the suggested file name carries the run and when it was taken",
            RecordingCsv.SuggestFileName(session) == "run one_20260908_143000.csv",
            RecordingCsv.SuggestFileName(session));

        // A name that cannot be a file name has to become one anyway.
        var awkwardName = new RecordedSession { Name = "line 3: A/B", Recorded = recorded };
        Check("a name with characters Windows refuses is made safe",
            RecordingCsv.SuggestFileName(awkwardName) == "line 3_ A_B_20260908_143000.csv",
            RecordingCsv.SuggestFileName(awkwardName));
    }

    // ===================== Tag description import =====================

    /// <summary>
    /// A Studio 5000 tag export, written the way Studio 5000 writes one: remark lines, a header
    /// row, TAG rows for whole tags, COMMENT rows carrying a sub-path in SPECIFIER, and an ALIAS
    /// row whose SPECIFIER is a target rather than a sub-path.
    /// </summary>
    private const string TagExport = """"
        remark,"CSV-Import-Export"
        remark,"Date = Tue Sep 09 10:14:22 2026"
        remark,"Version = RSLogix 5000 v20.01"
        remark,"Export Options = Decorated Data, Tag Comments, Alias Comments"
        TYPE,SCOPE,NAME,DESCRIPTION,DATATYPE,SPECIFIER,ATTRIBUTES
        TAG,,MotorSpeed,"Conveyor speed, feedback",DINT,,"(RADIX := Decimal)"
        TAG,,Counts,"Bin counters",DINT[10],,"(RADIX := Decimal)"
        TAG,,Stations,"Filling stations",Station[3],,"(...)"
        TAG,,Recipe,"Active recipe",Recipe,,"(...)"
        TAG,MainProgram,Step,"Sequence step",DINT,,"(...)"
        COMMENT,,Recipe,"Target temperature in ""degC""",,.SetPoint,
        COMMENT,,Counts,"Reject bin",,[3],
        COMMENT,,Stations,"Station identifier
        second line",,[0].Id,
        COMMENT,,Stations,"Fault bits",,.Faults,
        ALIAS,,Motor,"Drive alias",,MotorSpeed,"(...)"
        """";

    private static void TagDescriptionImport()
    {
        Section("tag description import");

        var import = RsLogixTagCsv.Parse(TagExport);
        Check("the export is recognised as a Studio 5000 one",
            import.Format == "Studio 5000 tag export", import.Format);
        Check("every described row is read",
            import.Descriptions == 10, $"{import.Descriptions} descriptions");

        var lookup = import.Lookup;

        // The plain case, and a description holding the separator.
        Check("a controller tag is found, comma and all",
            Found(lookup, "MotorSpeed") == "Conveyor speed, feedback");

        // Scope is a column in the file and a prefix in this app's addresses.
        Check("a program scoped tag becomes Program:Name.Tag",
            Found(lookup, "Program:MainProgram.Step") == "Sequence step");

        // A COMMENT row's specifier is what covers members and elements.
        Check("a UDT member comment is found, with its doubled quotes undoubled",
            Found(lookup, "Recipe.SetPoint") == "Target temperature in \"degC\"");
        Check("an array element comment is found",
            Found(lookup, "Counts[3]") == "Reject bin");

        // A description spanning lines has to survive into a one line cell.
        Check("a description written over two lines becomes one",
            Found(lookup, "Stations[0].Id") == "Station identifier second line");

        // The fallback. An element with no comment of its own shows its array's, flagged as
        // inherited so the import can say how many rows are second hand.
        Check("an element with no comment inherits from its array",
            lookup.TryFind("Counts[7]", out string counts, out bool countsInherited)
            && counts == "Bin counters" && countsInherited);

        // "Stations.Faults" describes the member wherever it appears, so reading it for
        // "Stations[0].Faults" is that member's own text and not second hand.
        Check("a member reached by dropping an element index is not inherited",
            lookup.TryFind("Stations[0].Faults", out string faults, out bool faultsInherited)
            && faults == "Fault bits" && !faultsInherited);

        // One element of it, though, is showing the whole array's text.
        Check("an element of that member array is inherited",
            lookup.TryFind("Stations[0].Faults[2]", out string bit, out bool bitInherited)
            && bit == "Fault bits" && bitInherited);

        Check("a member inside an uncommented element inherits from the tag",
            lookup.TryFind("Stations[2].Recipe.SetPoint", out string deep, out bool deepInherited)
            && deep == "Filling stations" && deepInherited);

        // An exact hit must NOT be reported as inherited, or the count means nothing.
        Check("an exact hit is not reported as inherited",
            lookup.TryFind("Counts[3]", out _, out bool exactInherited) && !exactInherited);

        // The alias target sits in the same column as a sub-path and must not be treated as one.
        Check("an alias describes the alias",
            Found(lookup, "Motor") == "Drive alias");
        Check("an alias target is not turned into a tag",
            !lookup.TryFind("Motor.MotorSpeed", out _, out _)
            || Found(lookup, "Motor.MotorSpeed") == "Drive alias");

        Check("a tag the file never mentions stays empty",
            !lookup.TryFind("NoSuchTag", out _, out _));

        // A program scoped tag must not climb out of its own scope looking for a parent.
        Check("a program scope is not mistaken for a parent tag",
            !lookup.TryFind("Program:MainProgram.Missing", out _, out _));

        PlainCsvImport();
        LogicCommentSection();
        DollarEscapes();
        PathTrimming();
    }

    /// <summary>
    /// Laid out as Rockwell's "export all tags and comments" example is (1756-RM014, chapter 21):
    /// every tag row, then a second header and the rung and text box comments. That section never
    /// describes a tag, and reading stops where it starts.
    /// </summary>
    private const string ExportWithLogic = """
        remark,"CSV-Import-Export"
        remark,"Version = RSLogix 5000 v36.00"
        0.3
        TYPE,SCOPE,NAME,DESCRIPTION,DATATYPE,SPECIFIER,ATTRIBUTES
        TAG,,MotorSpeed,"Conveyor speed",DINT,,"(RADIX := Decimal)"
        TAG,MainProgram,Step,"Sequence step",DINT,,"(RADIX := Decimal)"
        COMMENT,,Recipe,"Target temperature",,.SetPoint,
        TYPE,SCOPE,ROUTINE,COMMENT,OWNING_ELEMENT,LOCATION
        TEXTBOX,"MainProgram","Conveyor_FBD","Gives simple conveyor control",,"60:20:Sheet1"
        RCOMMENT,"MainProgram","MotorSpeed","A rung comment in a routine named like a tag","OTE(Out)","0"
        RCOMMENT,"MainProgram","MainRoutine","Second rung","OTL(Local:1:I.Data.7)","1"
        """;

    private static void LogicCommentSection()
    {
        var import = RsLogixTagCsv.Parse(ExportWithLogic);
        Check("tags before the logic comments are all read",
            Found(import.Lookup, "MotorSpeed") == "Conveyor speed"
            && Found(import.Lookup, "Program:MainProgram.Step") == "Sequence step"
            && Found(import.Lookup, "Recipe.SetPoint") == "Target temperature");
        Check("reading stops where the logic comment section starts",
            import.RowsRead == 3, $"{import.RowsRead} rows read");

        // Rockwell's own second example opens the section with a text box, not a rung comment.
        string noHeader = string.Join('\n', ExportWithLogic.Split('\n')
            .Where(line => !line.StartsWith("TYPE,SCOPE,ROUTINE", StringComparison.Ordinal)));
        Check("a section opening with a text box and no header still stops the read",
            RsLogixTagCsv.Parse(noHeader).RowsRead == 3, $"{RsLogixTagCsv.Parse(noHeader).RowsRead} rows read");

        // The order is only what Rockwell's examples show, so it is checked rather than trusted.
        string tagAfter = ExportWithLogic + "\nTAG,,Temperature,\"Vessel temperature\",REAL,,\"(RADIX := Float)\"";
        var reordered = RsLogixTagCsv.Parse(tagAfter);
        Check("a tag row after the logic comments is still read",
            Found(reordered.Lookup, "Temperature") == "Vessel temperature");
        Check("a rung comment is never taken for a tag, even in a routine named like one",
            Found(reordered.Lookup, "MotorSpeed") == "Conveyor speed"
            && Found(reordered.Lookup, "Program:MainProgram.MotorSpeed") == "<not found>");

        Check("the section start is found on its own header line",
            RsLogixTagCsv.CommentSectionStart(ExportWithLogic)
                == ExportWithLogic.IndexOf("TYPE,SCOPE,ROUTINE", StringComparison.Ordinal));
        Check("a file with no logic comments is read to the end",
            RsLogixTagCsv.CommentSectionStart(TagExport) == TagExport.Length);
    }

    /// <summary>
    /// Studio 5000 escapes description text on export (1756-RM014, appendix A), and its own
    /// examples show $N and $R$N line breaks. A quote comes out as $" inside a quoted field, which
    /// an ordinary CSV reader takes for the end of the field.
    /// </summary>
    private static void DollarEscapes()
    {
        const string escaped = """
            remark,"CSV-Import-Export"
            TYPE,SCOPE,NAME,DESCRIPTION,DATATYPE,SPECIFIER,ATTRIBUTES
            TAG,,Language,"LD = 0$NFBD = 1$NSFC = 2",DINT,,"(RADIX := Decimal)"
            TAG,,Fault,"start, stop, jog, and $R$Nfault detection",BOOL,,"(RADIX := Decimal)"
            TAG,,Station,"Operator$'s station",BOOL,,"(RADIX := Decimal)"
            TAG,,Quoted,"$"test string$" and $Qmore$Q",BOOL,,"(RADIX := Decimal)"
            TAG,,Price,"Cost in $$",REAL,,"(RADIX := Float)"
            TAG,,After,"still its own row",DINT,,"(RADIX := Decimal)"
            TAG,,Hex,"$41BC",DINT,,"(RADIX := Decimal)"
            """;

        var lookup = RsLogixTagCsv.Parse(escaped).Lookup;
        Check("$N line breaks become spaces",
            Found(lookup, "Language") == "LD = 0 FBD = 1 SFC = 2", Found(lookup, "Language"));
        Check("a $R$N line break becomes one space",
            Found(lookup, "Fault") == "start, stop, jog, and fault detection", Found(lookup, "Fault"));
        Check("$' is an apostrophe",
            Found(lookup, "Station") == "Operator's station", Found(lookup, "Station"));
        Check("$\" and $Q are quotes, and $\" does not end the field",
            Found(lookup, "Quoted") == "\"test string\" and \"more\"", Found(lookup, "Quoted"));
        Check("$$ is a dollar, and the quote after it still ends the field",
            Found(lookup, "Price") == "Cost in $" && Found(lookup, "After") == "still its own row",
            Found(lookup, "Price"));
        Check("$hh is a character by its hex code",
            Found(lookup, "Hex") == "ABC", Found(lookup, "Hex"));

        // A hand made list is not a Logix export, and five dollars stays five dollars.
        Check("a plain CSV keeps its dollar signs",
            Found(RsLogixTagCsv.Parse("Tag,Description\r\nPrice,\"Costs $5, or $Q\"\r\n").Lookup, "Price")
                == "Costs $5, or $Q");
    }

    /// <summary>Anything with a name column and a description column should still work, because a
    /// hand written list is a reasonable thing to point this at.</summary>
    private static void PlainCsvImport()
    {
        var headed = RsLogixTagCsv.Parse("Tag,Description\r\nMotorSpeed,Just the motor\r\n");
        Check("a plain CSV with a header is read",
            Found(headed.Lookup, "MotorSpeed") == "Just the motor", headed.Format);

        var bare = RsLogixTagCsv.Parse("MotorSpeed,Just the motor\r\nStations[0].Id,Station one\r\n");
        Check("a CSV with no header at all is read as name then description",
            Found(bare.Lookup, "MotorSpeed") == "Just the motor"
            && Found(bare.Lookup, "Stations[0].Id") == "Station one", bare.Format);

        // Rows with nothing to say are not entries.
        var sparse = RsLogixTagCsv.Parse("Tag,Description\r\nA,\r\n,orphan\r\nB,kept\r\n");
        Check("rows with no name or no description are dropped", sparse.Descriptions == 1);
    }

    private static void PathTrimming()
    {
        Check("indexes come out of a path",
            TagDescriptionLookup.StripIndexes("Stations[0].Faults[3]") == "Stations.Faults");
        Check("a path with no index is left alone",
            TagDescriptionLookup.StripIndexes("Recipe.SetPoint") == "Recipe.SetPoint");

        Check("a trailing index is trimmed",
            TagDescriptionLookup.TryTrim("Counts[3]", out string trimmed) && trimmed == "Counts");
        Check("a trailing member is trimmed",
            TagDescriptionLookup.TryTrim("Recipe.SetPoint", out string member) && member == "Recipe");
        Check("a bare tag has no parent",
            !TagDescriptionLookup.TryTrim("MotorSpeed", out _));
        Check("a program scoped tag stops at its scope",
            !TagDescriptionLookup.TryTrim("Program:MainProgram.Step", out _));
        Check("a member of a program scoped tag trims to that tag",
            TagDescriptionLookup.TryTrim("Program:MainProgram.DwellTimer.PRE", out string program)
            && program == "Program:MainProgram.DwellTimer");
    }

    private static string Found(TagDescriptionLookup lookup, string address) =>
        lookup.TryFind(address, out string description, out _) ? description : "<not found>";

    // ===================== Plumbing =====================

    private static void Section(string name) => Console.WriteLine($"-- {name}");

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _passed++; Console.WriteLine($"   PASS  {name}"); }
        else { _failed++; Console.WriteLine($"   FAIL  {name}{(detail is null ? "" : $"  [{detail}]")}"); }
    }

    private static void Bytes(string name, byte[] actual, byte[] expected)
    {
        bool ok = actual.SequenceEqual(expected);
        Check(name, ok, ok ? null : $"expected {Hex(expected)}, got {Hex(actual)}");
    }

    private static Exception? Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch { return true; }
    }

    private static string Hex(byte[] bytes) => string.Join(' ', bytes.Select(b => b.ToString("X2")));
}
