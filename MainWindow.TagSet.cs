using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using OpcUaPlayground.Dialogs;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services;

namespace OpcUaPlayground;

/// <summary>
/// Save, Save As and Open for the monitor table. A tag set is a document: the window tracks which
/// file it came from, whether it has been edited since, and refuses to throw away edits silently.
/// </summary>
public partial class MainWindow
{
    /// <summary>Null until the set has been saved somewhere, which is what makes Save behave as
    /// Save As the first time.</summary>
    private string? _tagSetPath;

    private bool _tagSetDirty;

    /// <summary>Set while the table is being rebuilt from a file, so loading does not read as editing.</summary>
    private bool _suppressDirty;

    /// <summary>The controller block of the file as last opened or saved. A save made while no
    /// controller is confirmed keeps it, rather than forgetting where the set came from.</summary>
    private ControllerReference? _savedController;

    private void InitialiseTagSet()
    {
        _monitor.Tags.CollectionChanged += TagSet_CollectionChanged;
        Closing += MainWindow_Closing;
        UpdateTitle();
    }

    /// <summary>
    /// Adding or removing rows edits the document, and so do changing a scan rate and importing
    /// descriptions, since both are written into the file. Values and errors do not: those arrive
    /// from the controller several times a second, and treating them as edits would leave the file
    /// permanently dirty.
    /// </summary>
    private void TagSet_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var tag in e.OldItems?.OfType<MonitoredTag>() ?? Enumerable.Empty<MonitoredTag>())
            tag.PropertyChanged -= Tag_PropertyChanged;

        foreach (var tag in e.NewItems?.OfType<MonitoredTag>() ?? Enumerable.Empty<MonitoredTag>())
            tag.PropertyChanged += Tag_PropertyChanged;

        if (!_suppressDirty) MarkDirty();
    }

    private void Tag_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MonitoredTag.ScanRateMs) or nameof(MonitoredTag.Description)
            && !_suppressDirty)
            MarkDirty();
    }

    private void MarkDirty()
    {
        if (_tagSetDirty) return;
        _tagSetDirty = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        string name = _tagSetPath is null ? "untitled" : TagSetFile.DisplayName(_tagSetPath);
        Title = $"OPC UA Playground — {name}{(_tagSetDirty ? " *" : "")}";
    }

    // ===================== Menu and shortcuts =====================

    private void NewTagSet_Click(object sender, RoutedEventArgs e) => NewTagSet();
    private void OpenTagSet_Click(object sender, RoutedEventArgs e) => OpenTagSet();
    private void SaveTagSet_Click(object sender, RoutedEventArgs e) => SaveTagSet();
    private void SaveTagSetAs_Click(object sender, RoutedEventArgs e) => SaveTagSetAs();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void NewTagSet_Executed(object sender, ExecutedRoutedEventArgs e) => NewTagSet();
    private void OpenTagSet_Executed(object sender, ExecutedRoutedEventArgs e) => OpenTagSet();
    private void SaveTagSet_Executed(object sender, ExecutedRoutedEventArgs e) => SaveTagSet();
    private void SaveTagSetAs_Executed(object sender, ExecutedRoutedEventArgs e) => SaveTagSetAs();

    // ===================== The three operations =====================

    private void NewTagSet()
    {
        if (!ConfirmDiscard()) return;

        _monitor.Stop();
        _tagColors = new TagColorMap();
        ReplaceTags(Array.Empty<TagSetEntry>());
        _recordings = new List<RecordedSession>();
        OnRecordingsReplaced();
        _tagSetPath = null;
        _savedController = null;
        _tagSetDirty = false;
        UpdateTitle();
        StatusText.Text = "empty tag set";
    }

    private void OpenTagSet()
    {
        if (!ConfirmDiscard()) return;

        var dialog = new OpenFileDialog
        {
            Title = "Open tag set",
            Filter = TagSetFile.DialogFilter,
            DefaultExt = TagSetFile.Extension,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        TagSetDocument document;
        try
        {
            document = TagSetFile.Load(dialog.FileName);
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(this,
                $"Could not open that file.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
            return;
        }

        _monitor.Stop();
        _tagColors = TagColorMap.FromStored(document.TagColors);
        ReplaceTags(document.Tags);

        _recordings = document.Recordings.ToList();
        OnRecordingsReplaced();

        if (document.DefaultScanRateMs > 0)
            ScanRateTextBox.Text =
                Math.Clamp(document.DefaultScanRateMs, MinScanRateMs, MaxScanRateMs).ToString();

        _tagSetPath = dialog.FileName;
        _savedController = document.Controller;
        _tagSetDirty = false;
        UpdateTitle();

        // After the clean state is set, so a row the loaded descriptions file changes marks the
        // set unsaved, and one it leaves alone does not.
        StatusText.Text = DescribeLoad(document, dialog.FileName)
                          + ApplyLoadedDescriptionsAfterOpen()
                          + FillInSavedController(document.Controller);
        UpdateButtons();
    }

    private bool SaveTagSet() => _tagSetPath is null ? SaveTagSetAs() : WriteTo(_tagSetPath);

    private bool SaveTagSetAs()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save tag set as",
            Filter = TagSetFile.DialogFilter,
            DefaultExt = TagSetFile.Extension,
            AddExtension = false,      // the two-part extension is added below instead
            FileName = _tagSetPath is null
                ? "tags" + TagSetFile.Extension
                : Path.GetFileName(_tagSetPath),
        };
        if (dialog.ShowDialog(this) != true) return false;

        return WriteTo(TagSetFile.EnsureExtension(dialog.FileName));
    }

    private bool WriteTo(string path)
    {
        var document = BuildDocument();
        try
        {
            TagSetFile.Save(path, document);
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(this,
                $"Could not save.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
            return false;
        }

        _tagSetPath = path;
        _savedController = document.Controller;
        _tagSetDirty = false;
        UpdateTitle();
        StatusText.Text = $"saved {_monitor.Tags.Count} tags to {TagSetFile.DisplayName(path)}";
        return true;
    }

    // ===================== Document to table and back =====================

    private TagSetDocument BuildDocument() => new()
    {
        Format = TagSetDocument.TagSetFormat,
        Version = TagSetDocument.CurrentVersion,
        Saved = DateTimeOffset.Now,
        Controller = _target.ToReference(_savedController),
        DefaultScanRateMs = ReadScanRate(),
        Recordings = _recordings.ToList(),
        Tags = _monitor.Tags.Select(t => new TagSetEntry
        {
            Address = t.Address,
            DataType = t.DataType,
            ScanRateMs = t.ScanRateMs,
            Description = string.IsNullOrEmpty(t.Description) ? null : t.Description,
        }).ToList(),

        // Only for tags the file still holds, in the table or in a run.
        TagColors = _tagColors.ToStored(
            _monitor.Tags.Select(t => t.Address)
                .Concat(_recordings.SelectMany(r => r.Tags.Select(t => t.Address)))),
    };

    private void ReplaceTags(IReadOnlyList<TagSetEntry> entries)
    {
        // A known-address check only means anything once a browse has happened. Before that every
        // row is simply unverified, which is not the same as wrong.
        var known = _monitorableNodes.Count == 0
            ? null
            : _monitorableNodes.Select(n => n.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);

        _suppressDirty = true;
        try
        {
            foreach (var tag in _monitor.Tags) tag.PropertyChanged -= Tag_PropertyChanged;
            _monitor.Tags.Clear();

            foreach (var entry in entries)
            {
                var tag = new MonitoredTag(
                    entry.Address, entry.DataType, entry.ScanRateMs, EstimateSize(entry.DataType))
                {
                    Description = entry.Description ?? "",
                    StripColor = _tagColors.Get(entry.Address),
                };

                // Said plainly on the row, rather than left as a blank value nobody can explain.
                if (known is not null && !known.Contains(entry.Address))
                    tag.Error = "not present in the connected controller";

                _monitor.Tags.Add(tag);
            }
        }
        finally
        {
            _suppressDirty = false;
        }

        UpdateButtons();
    }

    private string DescribeLoad(TagSetDocument document, string path)
    {
        string what = $"loaded {document.Tags.Count} tag{(document.Tags.Count == 1 ? "" : "s")} "
                    + $"from {TagSetFile.DisplayName(path)}";

        if (document.Recordings.Count > 0) what += $" and {DescribeRunCount()}";

        int missing = _monitor.Tags.Count(t => t.HasError);
        if (missing > 0) what += $"; {missing} not present in this controller";

        return what;
    }

    /// <summary>
    /// Points the address, slot and adapter at the controller the set was saved from, so pressing
    /// Connect goes back there. It only fills in the fields: nothing is sent until Connect is
    /// pressed, whatever the file says. The adapter is matched by its Windows id only, so on
    /// another PC it is left as it is. Returns text for the status line.
    /// </summary>
    private string FillInSavedController(ControllerReference? saved)
    {
        if (saved is null || !System.Net.IPAddress.TryParse(saved.Address, out var address)) return "";

        // Already there: nothing to fill in or say.
        if (_target.Confirmed is { } now && now.Address.Equals(address) && now.Slot == saved.Slot) return "";

        IpBox.SetAddress(address);
        SlotTextBox.Text = saved.Slot.ToString();

        string via = "";
        if (saved.Adapter is { } adapter)
        {
            var match = NetworkAdapterService.Match(
                AdapterComboBox.ItemsSource as IEnumerable<NetworkAdapterOption> ?? Array.Empty<NetworkAdapterOption>(),
                adapter.Id, adapter.Address);

            if (match is not null)
            {
                AdapterComboBox.SelectedItem = match;
                via = $" via {match.DisplayName}";
            }
            else
            {
                via = $" (its adapter, {adapter.Name ?? "unnamed"}, is not available on this computer, "
                      + "so the adapter was left as it was)";
            }
        }

        UpdateConnectButton();
        return $"; saved from {address} slot {saved.Slot}{via}. Press Connect to go there";
    }

    // ===================== Guarding unsaved edits =====================

    /// <summary>True to carry on, false when the user backed out.</summary>
    private bool ConfirmDiscard()
    {
        // Otherwise the run would be captured against a document that no longer exists.
        if (_recorder.IsRecording) FinishRecording("stopped");

        // An unsaved crop is not in the document, so the dirty check below cannot see it. Restored
        // on a discard, because an Open can still be cancelled at its file dialog.
        if (!ConfirmDropCrop(restore: true)) return false;

        // A document with no rows and no runs holds nothing to lose, however it got dirty. One whose
        // rows were all removed can still hold recordings, and those are only in memory until saved.
        if (!_tagSetDirty || (_monitor.Tags.Count == 0 && _recordings.Count == 0)) return true;

        string name = _tagSetPath is null ? "this tag set" : TagSetFile.DisplayName(_tagSetPath);
        var answer = DarkMessageBox.Confirm(this, $"Save changes to {name} first?", "Save", "Discard");

        return answer switch
        {
            DarkDialogResult.Yes => SaveTagSet(),
            DarkDialogResult.No => true,
            _ => false,
        };
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard() || !InstallPendingUpdate()) e.Cancel = true;
    }
}
