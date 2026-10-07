using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using OpcUaPlayground.Dialogs;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services;
using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground;

/// <summary>
/// The monitor half of the window: multi-selection in the tree, moving tags into the table, the
/// tag entry box with its suggestions, and the start and stop of polling.
/// </summary>
public partial class MainWindow
{
    /// <summary>What the entry box's suggestion popup shows. The popup only displays it.</summary>
    private readonly TagSuggestions _suggestions = new();

    /// <summary>Where a shift-click measures its range from.</summary>
    private TagNode? _selectionAnchor;

    /// <summary>What is selected in the tree. Every selection change goes through this.</summary>
    private readonly TagSelection _treeSelection = new();

    /// <summary>Every monitorable address in the browsed tree, for the entry box's suggestions.</summary>
    private List<TagNode> _monitorableNodes = new();

    private void InitialiseMonitor()
    {
        MonitorGrid.ItemsSource = _monitor.Tags;
        _monitor.Tags.CollectionChanged += (_, _) => UpdateTableSummary();
        _monitor.Stopped += OnMonitorStopped;

        TagTree.PreviewMouseLeftButtonDown += TagTree_PreviewMouseLeftButtonDown;
        TagTree.PreviewKeyDown += TagTree_PreviewKeyDown;

        TagEntryBox.TextChanged += (_, _) => UpdateSuggestions();
        TagEntryBox.PreviewKeyDown += TagEntryBox_PreviewKeyDown;
        TagEntryBox.LostFocus += (_, _) => SuggestionPopup.IsOpen = false;

        SuggestionList.MouseLeftButtonUp += (_, _) =>
        {
            _suggestions.Select(SuggestionList.SelectedIndex);
            AcceptSuggestion();
        };

        // The popup also closes itself, on a click elsewhere. Enter then has nothing to fill in.
        SuggestionPopup.Closed += (_, _) => _suggestions.Close();

        MonitorGrid.SelectionChanged += (_, _) => UpdateButtons();
        MonitorGrid.MouseDoubleClick += MonitorGrid_MouseDoubleClick;
        MonitorGrid.PreviewKeyDown += MonitorGrid_PreviewKeyDown;
    }

    /// <summary>
    /// Double-clicking a row puts its address in the entry box above, e.g. to add a neighbouring
    /// element by editing the index. Current mode only. The scan rate cell keeps its own
    /// double-click, which edits the rate.
    /// </summary>
    private void MonitorGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsPlaybackMode || e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<DataGridRow>(source)?.Item is not MonitoredTag tag) return;
        if (FindAncestor<DataGridCell>(source) is { Column.IsReadOnly: false }) return;

        TagEntryBox.Text = tag.Address;
        SuggestionPopup.IsOpen = false;     // it is a tag already; a list of matches would be noise
        TagEntryBox.Focus();
        TagEntryBox.CaretIndex = TagEntryBox.Text.Length;
        e.Handled = true;
    }

    /// <summary>Delete removes the selected rows, under the same rules as the – button: not in
    /// playback and not while recording.</summary>
    private void MonitorGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;

        // Deleting digits in a scan rate being edited is not deleting the row.
        if (e.OriginalSource is TextBox) return;

        if (!RemoveTagButton.IsEnabled) return;
        RemoveTagButton_Click(RemoveTagButton, new RoutedEventArgs());
        e.Handled = true;
    }

    // ===================== Tree selection =====================

    /// <summary>
    /// Plain click selects one, Ctrl toggles, Shift takes the range from the anchor. The range runs
    /// over the rows as they appear on screen, so it matches what the user sees rather than the
    /// underlying tree order.
    /// </summary>
    private void TagTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindNode(e.OriginalSource as DependencyObject) is not { } node) return;

        // Let clicks on the expander triangle do their normal job.
        if (e.OriginalSource is DependencyObject source && FindAncestor<ToggleButton>(source) is not null) return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Shift) && _selectionAnchor is not null)
        {
            SelectRange(_selectionAnchor, node);
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            _treeSelection.Toggle(node);
            _selectionAnchor = node;
        }
        else
        {
            ClearTreeSelection();
            _treeSelection.Set(node, true);
            _selectionAnchor = node;
        }

        UpdateButtons();
    }

    /// <summary>Shift with an arrow key extends the selection one visible row at a time.</summary>
    private void TagTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down)) return;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;

        var visible = VisibleNodes().ToList();
        if (visible.Count == 0) return;

        var current = _selectionAnchor ?? visible.FirstOrDefault(n => n.IsSelected);
        int index = current is null ? -1 : visible.IndexOf(current);
        int next = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, visible.Count - 1);

        _treeSelection.Set(visible[next], true);
        _selectionAnchor = visible[next];
        UpdateButtons();
        e.Handled = true;
    }

    private void SelectRange(TagNode from, TagNode to)
    {
        var visible = VisibleNodes().ToList();
        int a = visible.IndexOf(from);
        int b = visible.IndexOf(to);
        if (a < 0 || b < 0) return;

        ClearTreeSelection();
        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) _treeSelection.Set(visible[i], true);
    }

    /// <summary>The rows currently on screen, in order, honouring which folders are open.</summary>
    private IEnumerable<TagNode> VisibleNodes()
    {
        if (TagTree.ItemsSource is not IEnumerable<TagNode> roots) yield break;

        foreach (var root in roots)
            foreach (var node in Walk(root))
                yield return node;

        static IEnumerable<TagNode> Walk(TagNode node)
        {
            yield return node;
            if (!node.IsExpanded) yield break;
            foreach (var child in node.Children)
                foreach (var descendant in Walk(child))
                    yield return descendant;
        }
    }

    private void ClearTreeSelection() => _treeSelection.Clear();

    /// <summary>
    /// Puts a different set of rows in the tree: a browse, a search, or clearing one. The
    /// selection is rebuilt from what is now on screen, because a search shows copies of nodes
    /// and what counts as selected is what the user can see selected.
    /// </summary>
    private void ShowTree(IEnumerable<TagNode>? roots)
    {
        TagTree.ItemsSource = roots;
        _treeSelection.Reset(AllNodes());
    }

    private IEnumerable<TagNode> AllNodes()
    {
        if (TagTree.ItemsSource is not IEnumerable<TagNode> roots) yield break;
        foreach (var root in roots)
            foreach (var node in Flatten(root))
                yield return node;
    }

    private static IEnumerable<TagNode> Flatten(TagNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }

    private static TagNode? FindNode(DependencyObject? source) =>
        FindAncestor<TreeViewItem>(source)?.DataContext as TagNode;

    private static T? FindAncestor<T>(DependencyObject? from) where T : DependencyObject
    {
        while (from is not null)
        {
            if (from is T match) return match;
            from = VisualTreeHelper.GetParent(from);
        }
        return null;
    }

    // ===================== Moving tags into the table =====================

    private void AddSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        // A selected folder or structure contributes everything readable beneath it, which is what
        // people expect when they pick a UDT instance and press the arrow.
        var chosen = AllNodes().Where(n => n.IsSelected).SelectMany(Flatten).Where(n => n.IsMonitorable);
        AddTags(chosen);
    }

    private void AddTagButton_Click(object sender, RoutedEventArgs e)
    {
        string typed = TagEntryBox.Text.Trim();
        if (typed.Length == 0) return;

        var node = _monitorableNodes.FirstOrDefault(
            n => n.Address.Equals(typed, StringComparison.OrdinalIgnoreCase));

        if (node is null)
        {
            BrowseStatusText.Text = $"\"{typed}\" is not a readable tag in this controller";
            return;
        }

        AddTags(new[] { node });
        TagEntryBox.Clear();
        SuggestionPopup.IsOpen = false;
    }

    /// <summary>
    /// Above this many tags in one go, the user is asked first. Selecting a folder can mean tens of
    /// thousands of tags on a real controller, and every one of them is then read at the scan rate.
    /// </summary>
    private const int LargeAddWarning = 500;

    /// <summary>Set while a batch is going in, so the summary is totalled once instead of per row.</summary>
    private bool _bulkAdding;

    private void AddTags(IEnumerable<TagNode> nodes)
    {
        int defaultRate = ReadScanRate();
        var existing = _monitor.Tags.Select(t => t.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var wanted = nodes
            .DistinctBy(n => n.Address, StringComparer.OrdinalIgnoreCase)
            .Where(n => !existing.Contains(n.Address))
            .ToList();

        if (wanted.Count == 0) return;
        if (wanted.Count > LargeAddWarning && !ConfirmLargeAdd(wanted.Count, defaultRate)) return;

        // Totalling the bytes on every insert makes adding N tags cost N squared, which is seconds
        // of frozen window at controller scale. The total is worth exactly one pass at the end.
        _bulkAdding = true;
        try
        {
            foreach (var node in wanted)
            {
                var tag = new MonitoredTag(
                    node.Address, node.TypeName, defaultRate, EstimateSize(node.TypeName));

                // A tag added after an import should not be the one blank row in the table.
                if (_descriptions.TryFind(node.Address, out string description, out _))
                    tag.Description = description;

                _monitor.Tags.Add(tag);
            }
        }
        finally
        {
            _bulkAdding = false;
        }

        UpdateButtons();
    }

    /// <summary>
    /// Says what the table is about to cost the controller, in the terms that matter: how many tags
    /// and how often each will be asked for. A folder near the top of a real tree is tens of
    /// thousands of tags, and pressing Start afterwards would put all of that on the wire.
    /// </summary>
    private bool ConfirmLargeAdd(int count, int scanRateMs)
    {
        var answer = DarkMessageBox.Confirm(
            this,
            $"Add {count:N0} tags to the table?"
            + Environment.NewLine + Environment.NewLine
            + $"Once polling starts, the controller has to answer all {count:N0} of them "
            + $"every {scanRateMs} ms.",
            "Add them",
            "Cancel",
            cancel: null);      // "Cancel" already IS the second answer here

        return answer == DarkDialogResult.Yes;
    }

    /// <summary>
    /// Puts every row back on the toolbar's scan rate. Rows drift apart once they have been edited
    /// one at a time, and there was no way back to a single rate short of retyping each one.
    /// </summary>
    private void ResetScanRatesButton_Click(object sender, RoutedEventArgs e)
    {
        // Settle the toolbar box first, so what lands in the table is the value the box ends up
        // showing rather than something typed and out of range.
        NormalizeScanRate();
        int rate = ReadScanRate();

        int changed = 0;
        foreach (var tag in _monitor.Tags)
        {
            if (tag.ScanRateMs == rate) continue;
            tag.ScanRateMs = rate;
            changed++;
        }

        StatusText.Text = changed == 0
            ? $"every tag was already scanning at {rate} ms"
            : $"set {changed} of {_monitor.Tags.Count} tags to {rate} ms";
    }

    private void RemoveTagButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var tag in MonitorGrid.SelectedItems.OfType<MonitoredTag>().ToList())
            _monitor.Tags.Remove(tag);
        UpdateButtons();
    }

    /// <summary>Byte size before anything has been read, from the declared type. Replaced by the
    /// real size once a value comes back.</summary>
    private static int EstimateSize(string typeName)
    {
        foreach (byte code in Enumerable.Range(0xC1, 0x20).Select(i => (byte)i))
            if (LogixTypes.NameOf(code).Equals(typeName, StringComparison.OrdinalIgnoreCase))
                return TagValueDecoder.SizeOf(code);
        return 0;
    }

    // ===================== Tag entry suggestions =====================

    private IEnumerable<string> MonitorableAddresses => _monitorableNodes.Select(n => n.Address);

    private void UpdateSuggestions()
    {
        UpdateButtons();
        _suggestions.Update(TagEntryBox.Text, MonitorableAddresses);
        ShowSuggestions();
    }

    /// <summary>Puts <see cref="_suggestions"/> on screen: the list, its highlight, open or not.</summary>
    private void ShowSuggestions()
    {
        if (!_suggestions.IsOpen)
        {
            SuggestionPopup.IsOpen = false;
            return;
        }

        if (!ReferenceEquals(SuggestionList.ItemsSource, _suggestions.Matches))
            SuggestionList.ItemsSource = _suggestions.Matches;
        SuggestionList.SelectedIndex = _suggestions.SelectedIndex;
        SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);

        SuggestionPopup.PlacementTarget = TagEntryBox;
        SuggestionPopup.Width = TagEntryBox.ActualWidth;
        SuggestionPopup.IsOpen = true;
    }

    /// <summary>
    /// With the list open: Up and Down move the highlight, Tab or Enter fills the highlighted name
    /// into the box, Escape closes the list. Enter on a name already in the box adds it, as Enter
    /// does with the list closed. Down on a closed list opens it again.
    /// </summary>
    private void TagEntryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when _suggestions.EnterCompletes(TagEntryBox.Text):
            case Key.Tab when _suggestions.IsOpen:      // Tab completes rather than moving focus on
                AcceptSuggestion();
                break;

            case Key.Enter:
                AddTagButton_Click(sender, new RoutedEventArgs());
                break;

            case Key.Down when !_suggestions.IsOpen:
                _suggestions.Reopen(TagEntryBox.Text, MonitorableAddresses);
                if (!_suggestions.IsOpen) return;
                ShowSuggestions();
                break;

            case Key.Down or Key.Up when _suggestions.IsOpen:
                _suggestions.Move(e.Key == Key.Down ? 1 : -1);
                ShowSuggestions();
                break;

            case Key.Escape when _suggestions.IsOpen:
                _suggestions.Close();
                ShowSuggestions();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void AcceptSuggestion()
    {
        if (_suggestions.Accept() is not { } address) return;
        TagEntryBox.Text = address;         // comes back through UpdateSuggestions, which stays closed
        TagEntryBox.CaretIndex = address.Length;
        ShowSuggestions();
        UpdateButtons();
    }

    // ===================== Start and stop =====================

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_target.Confirmed is not { } endpoint || _monitor.Tags.Count == 0) return;
        _monitor.Start(endpoint);
        UpdateButtons();
        BrowseStatusText.Text = $"polling {_monitor.Tags.Count} tags";
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        // The monitor raises Stopped only when it stops itself, so a run has to be closed here.
        if (_recorder.IsRecording) FinishRecording("polling stopped");

        _monitor.Stop();
        UpdateButtons();
    }

    private void OnMonitorStopped(string reason)
    {
        UpdateButtons();
        if (reason != "stopped") StatusText.Text = $"polling stopped: {reason}";
    }

    // ===================== Housekeeping =====================

    private void UpdateTableSummary()
    {
        if (_bulkAdding) return;

        // Bytes are a live measure. A recorded run has no wire traffic to total, so it counts what
        // it does have: the changes behind what is on screen.
        if (IsPlaybackMode)
        {
            TableSummaryText.Text = _playback is null
                ? "no run"
                : $"{_playbackRows.Count} tag{(_playbackRows.Count == 1 ? "" : "s")}, "
                  + $"{_playback.ChangeCount} changes";

            // In playback the table is whatever the run held, so there is nothing to tell the user
            // to add. An empty one means the run itself is empty.
            EmptyTableHint.Visibility = Visibility.Collapsed;
            return;
        }

        int bytes = _monitor.Tags.Sum(t => t.ByteCount);
        TableSummaryText.Text = $"{_monitor.Tags.Count} tag{(_monitor.Tags.Count == 1 ? "" : "s")}, {bytes} bytes";
        EmptyTableHint.Visibility = _monitor.Tags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateButtons()
    {
        bool connected = _target.IsConfirmed;          // a confirmed controller, not just an attempt
        bool hasTags = _monitor.Tags.Count > 0;
        bool running = _monitor.IsRunning;

        // A run captures the table it started with. Editing that table mid-run would show rows the
        // recording does not contain, so the edits wait until it stops. Playback is history, and
        // nothing about it is editable either.
        bool editable = !_recorder.IsRecording && !IsPlaybackMode;

        // Playback is history: nothing typed there has a table to go into.
        TagEntryBox.IsEnabled = !IsPlaybackMode;
        if (IsPlaybackMode) SuggestionPopup.IsOpen = false;

        AddSelectedButton.IsEnabled = editable && _treeSelection.Any;
        AddTagButton.IsEnabled = editable && TagEntryBox.Text.Trim().Length > 0;
        RemoveTagButton.IsEnabled = editable && MonitorGrid.SelectedItems.Count > 0;
        ResetScanRatesButton.IsEnabled = editable && hasTags;
        StartButton.IsEnabled = editable && connected && hasTags && !running;
        StopButton.IsEnabled = editable && running;

        UpdateTableSummary();
    }

    /// <summary>Called after a browse, so the entry box can suggest and the arrow button can work.</summary>
    private void IndexMonitorableNodes()
    {
        _monitorableNodes = _allFolders
            .SelectMany(Flatten)
            .Where(n => n.IsMonitorable)
            .OrderBy(n => n.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();
        UpdateButtons();
    }
}
