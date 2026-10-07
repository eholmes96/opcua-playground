using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OpcUaPlayground.Dialogs;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services;

namespace OpcUaPlayground;

/// <summary>
/// The descriptions menu: tag descriptions from a Studio 5000 CSV export, and the five most recent
/// such files.
///
/// The controller cannot supply descriptions. That was proven against a GuardLogix 5580, which
/// answers 0x05, path destination unknown, for a property segment, and whose symbol entry holds no
/// description text at all. They live in the project, so they arrive from an export or not at all.
///
/// One set of descriptions is loaded at a time, and the table always shows that set: loading a file
/// replaces every row's text with the new file's (blank where it has none), and unloading clears
/// it. The loaded file is remembered across restarts and read again at startup. The user chose all
/// of this, including that opening a tag set applies the loaded file over the set's saved text.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The loaded set. Kept for the session so tags added after the import get their description
    /// too, rather than leaving one blank row every time somebody drags another tag across.
    /// </summary>
    private TagDescriptionLookup _descriptions = TagDescriptionLookup.Empty;

    private RecentDescriptionFiles _recentDescriptions = new();

    /// <summary>Bumped by every load and unload, so a slow read at startup cannot land on top of a
    /// file the user picked while it was still reading.</summary>
    private int _descriptionsGeneration;

    private void InitialiseDescriptions()
    {
        _recentDescriptions = RecentDescriptionFiles.Load(RecentDescriptionFiles.DefaultStorePath);
        DescriptionsMenu.SubmenuOpened += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, DescriptionsMenu)) RebuildRecentDescriptions();
        };
        RebuildRecentDescriptions();

        if (_recentDescriptions.LoadedPath is { } remembered) _ = ReloadRememberedAsync(remembered);
    }

    // ===================== Loading and unloading =====================

    private void ImportDescriptions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import tag descriptions",
            Filter = RsLogixTagCsv.DialogFilter,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        LoadDescriptionsFrom(dialog.FileName, fromRecentList: false);
    }

    /// <summary>A recent entry is a switch: picking the loaded one unloads it, any other loads it.</summary>
    private void RecentDescriptions_Click(string path)
    {
        // A click on the ✕ may also reach the entry it sits on. By then the entry is off the list,
        // and acting on it would load the file straight back in.
        if (!_recentDescriptions.Contains(path)) return;

        if (_recentDescriptions.IsLoaded(path)) UnloadDescriptions(RecentDescriptionFiles.Label(path));
        else LoadDescriptionsFrom(path, fromRecentList: true);
    }

    private void LoadDescriptionsFrom(string path, bool fromRecentList)
    {
        string label = RecentDescriptionFiles.Label(path);

        TagCsvImport import;
        try
        {
            import = RsLogixTagCsv.Load(path);
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(this,
                $"Could not read that file.{Environment.NewLine}{Environment.NewLine}{ex.Message}"
                + (fromRecentList
                    ? Environment.NewLine + Environment.NewLine
                      + "It is still in the Descriptions menu. Use its ✕ to remove it."
                    : ""));
            return;
        }

        if (import.Descriptions == 0)
        {
            DarkMessageBox.Show(this,
                $"That file read as {import.Format}, but no descriptions came out of it."
                + Environment.NewLine + Environment.NewLine
                + $"{import.RowsRead} rows were considered. A Studio 5000 tag export needs a NAME "
                + "column and a DESCRIPTION column, which it has when Tag Comments are included in "
                + "the export options.");
            return;
        }

        _descriptionsGeneration++;
        _descriptions = import.Lookup;
        _recentDescriptions.Use(path);
        string saveProblem = SaveRecentDescriptions();

        var applied = DescriptionApply.Apply(_monitor.Tags, _descriptions);
        StatusText.Text = Describe(import, label, applied) + saveProblem;
    }

    private void UnloadDescriptions(string label)
    {
        _descriptionsGeneration++;
        _descriptions = TagDescriptionLookup.Empty;
        _recentDescriptions.Unload();
        string saveProblem = SaveRecentDescriptions();

        var applied = DescriptionApply.Apply(_monitor.Tags, _descriptions);
        StatusText.Text = $"{label} unloaded; {DescribeCleared(applied.Cleared)}" + saveProblem;
    }

    private void RemoveRecentDescriptions(string path)
    {
        string label = RecentDescriptionFiles.Label(path);
        bool wasLoaded = _recentDescriptions.Remove(path);

        if (wasLoaded)
        {
            _descriptionsGeneration++;
            _descriptions = TagDescriptionLookup.Empty;
        }

        string saveProblem = SaveRecentDescriptions();
        RebuildRecentDescriptions();

        if (!wasLoaded)
        {
            StatusText.Text = $"{label} removed from the Descriptions menu" + saveProblem;
            return;
        }

        var applied = DescriptionApply.Apply(_monitor.Tags, _descriptions);
        StatusText.Text = $"{label} removed from the Descriptions menu and unloaded; "
                          + DescribeCleared(applied.Cleared) + saveProblem;
    }

    /// <summary>
    /// The file loaded when the app last closed, read again. Off the UI thread, because the path
    /// can be a network share, and a share that is not answering must not freeze the window.
    /// </summary>
    private async Task ReloadRememberedAsync(string path)
    {
        int generation = _descriptionsGeneration;
        string label = RecentDescriptionFiles.Label(path);

        TagCsvImport? import = null;
        string? problem = null;
        try
        {
            import = await Task.Run(() => RsLogixTagCsv.Load(path)).ConfigureAwait(true);
            if (import.Descriptions == 0) problem = "no descriptions came out of it";
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }

        // The user loaded or unloaded something while this was reading; theirs stands.
        if (generation != _descriptionsGeneration) return;

        if (problem is not null || import is null)
        {
            _recentDescriptions.Unload();
            StatusText.Text = $"{label} could not be read, so no descriptions are loaded ({problem})"
                              + SaveRecentDescriptions();
            return;
        }

        _descriptions = import.Lookup;
        var applied = DescriptionApply.Apply(_monitor.Tags, _descriptions);
        StatusText.Text = _monitor.Tags.Count == 0
            ? $"{label} loaded: {import.Descriptions} descriptions"
            : Describe(import, label, applied);
    }

    /// <summary>
    /// Called once a tag set has been opened. The loaded file wins over the set's saved text, so
    /// the table matches the check in the menu. Only rows whose text actually changes mark the
    /// set unsaved, so a set saved with this same file's text opens clean.
    /// </summary>
    private string ApplyLoadedDescriptionsAfterOpen()
    {
        if (_recentDescriptions.LoadedPath is not { } path || _descriptions.Count == 0) return "";

        var applied = DescriptionApply.Apply(_monitor.Tags, _descriptions);
        return applied.Changed == 0
            ? ""
            : $"; {applied.Changed} description{(applied.Changed == 1 ? "" : "s")} replaced from "
              + RecentDescriptionFiles.Label(path);
    }

    /// <summary>Returns text for the status line when the list could not be written, else "".</summary>
    private string SaveRecentDescriptions()
    {
        try
        {
            _recentDescriptions.Save(RecentDescriptionFiles.DefaultStorePath);
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $" (the recent list could not be saved: {ex.Message})";
        }
    }

    // ===================== The menu =====================

    /// <summary>
    /// "From CSV" stays first. Below it, a separator and one line per recent file: its folder and
    /// name on the left, and on the right a check when it is the loaded one, then its ✕.
    /// </summary>
    private void RebuildRecentDescriptions()
    {
        while (DescriptionsMenu.Items.Count > 1) DescriptionsMenu.Items.RemoveAt(1);

        var paths = _recentDescriptions.Paths;
        if (paths.Count == 0) return;

        DescriptionsMenu.Items.Add(new Separator());
        for (int i = 0; i < paths.Count; i++)
            DescriptionsMenu.Items.Add(BuildRecentEntry(paths[i], i));
    }

    private MenuItem BuildRecentEntry(string path, int index)
    {
        string label = RecentDescriptionFiles.Label(path);
        bool loaded = _recentDescriptions.IsLoaded(path);

        // A TextBlock rather than a string header, so an underscore in a file name is shown
        // rather than taken as an access key.
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };

        var check = new TextBlock
        {
            Text = "✓",
            Foreground = (System.Windows.Media.Brush)FindResource("Brush.StatusOk"),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = loaded ? Visibility.Visible : Visibility.Hidden,
        };

        var remove = new Button
        {
            Content = "✕",
            Style = (Style)FindResource("MenuRemoveButton"),
            ToolTip = "Remove from this list" + (loaded ? " and unload" : ""),
        };
        AutomationProperties.SetAutomationId(remove, $"RemoveRecentDescriptions{index}");
        AutomationProperties.SetName(remove, $"Remove {label}");
        remove.Click += (_, e) =>
        {
            e.Handled = true;
            RemoveRecentDescriptions(path);
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(check, 1);
        Grid.SetColumn(remove, 2);
        header.Children.Add(name);
        header.Children.Add(check);
        header.Children.Add(remove);

        var item = new MenuItem
        {
            Header = header,
            ToolTip = path,
            StaysOpenOnClick = false,
        };
        AutomationProperties.SetAutomationId(item, $"RecentDescriptions{index}");
        AutomationProperties.SetName(item, label);
        AutomationProperties.SetItemStatus(item, loaded ? "loaded" : "");
        item.Click += (_, _) => RecentDescriptions_Click(path);

        // The ✕ has a keyboard equivalent, so a highlighted entry can be removed without a mouse.
        item.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Delete) return;
            e.Handled = true;
            RemoveRecentDescriptions(path);
        };

        return item;
    }

    // ===================== Status text =====================

    private string Describe(TagCsvImport import, string label, DescriptionApplyResult applied)
    {
        int rows = _monitor.Tags.Count;
        var text = $"read {import.Descriptions} descriptions from {label} ({import.Format})";

        if (rows == 0) return text + ", and the table is empty. They will be used as tags are added.";

        text += $"; {applied.Matched} of {rows} table tags matched";
        if (applied.Inherited > 0) text += $" ({applied.Inherited} from a parent tag)";

        int missed = rows - applied.Matched;
        if (missed > 0) text += $", {missed} not in the file";

        return text;
    }

    private static string DescribeCleared(int cleared) =>
        cleared == 1 ? "1 description cleared" : $"{cleared} descriptions cleared";
}
