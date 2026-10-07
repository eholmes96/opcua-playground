using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services;
using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground;

public partial class MainWindow : Window
{
    private const int DefaultScanRateMs = 250;
    private const int MinScanRateMs = 10;
    private const int MaxScanRateMs = 60_000;

    private readonly ConnectionCoordinator _coordinator = new(new EtherNetIpTransport());
    private readonly IProgress<ConnectionStatus> _statusProgress;

    private readonly TagBrowser _browser = new();
    private readonly IProgress<string> _browseProgress;

    private readonly TagMonitor _monitor;

    /// <summary>The unfiltered tree, kept so the filter box can narrow and restore without re-browsing.</summary>
    private IReadOnlyList<TagNode> _allFolders = Array.Empty<TagNode>();

    private CancellationTokenSource? _browseCts;

    /// <summary>What the window is aimed at, and whether a controller there is confirmed.</summary>
    private readonly ControllerTarget _target = new();

    /// <summary>The latest Connect's endpoint, confirmed or not. For the Connect button and
    /// browsing; anything that polls or saves uses <see cref="ControllerTarget.Confirmed"/>.</summary>
    private PlcEndpoint? _connectedEndpoint => _target.Attempted;

    /// <summary>True once a routed read has confirmed a controller at the chosen slot.</summary>
    private bool _isConnected => _target.IsConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        // Constructed on the UI thread, so its callbacks marshal back here and can touch controls.
        _statusProgress = new Progress<ConnectionStatus>(ShowStatus);

        // The poller runs on a background thread and hands every table edit back through here.
        _monitor = new TagMonitor(action =>
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.Invoke(action);
        });
        _browseProgress = new Progress<string>(text => BrowseStatusText.Text = text);

        // Editing an address changes nothing on the wire. Nothing is sent until Connect is pressed.
        IpBox.AddressChanged += (_, _) => UpdateConnectButton();
        IpBox.Committed += (_, _) => Connect();
        SlotTextBox.PreviewTextInput += DigitsOnly;
        SlotTextBox.TextChanged += (_, _) => UpdateConnectButton();
        SlotTextBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Connect(); };
        ScanRateTextBox.PreviewTextInput += DigitsOnly;
        ScanRateTextBox.LostFocus += (_, _) => NormalizeScanRate();
        FilterTextBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyFilter(); };

        InitialiseMonitor();
        InitialiseTagSet();
        InitialisePlayback();
        InitialiseModeTransition();
        InitialiseRecording();
        InitialiseDescriptions();

        Loaded += (_, _) => { RefreshAdapters(); UpdateConnectButton(); UpdateTreeHint(); IpBox.Focus(); };
        Closed += (_, _) => _monitor.Stop();
        Activated += (_, _) => RefreshAdapters();
    }

    // ===================== Network adapters =====================

    private void RefreshAdapters()
    {
        var current = AdapterComboBox.SelectedItem as NetworkAdapterOption;
        var adapters = NetworkAdapterService.GetIPv4Adapters();
        AdapterComboBox.ItemsSource = adapters;
        AdapterComboBox.SelectedItem =
            NetworkAdapterService.Match(adapters, current?.Id, current?.Address.ToString())
            ?? adapters.FirstOrDefault();
    }

    private void RefreshAdaptersButton_Click(object sender, RoutedEventArgs e) => RefreshAdapters();

    // ===================== Input rules =====================

    private static void DigitsOnly(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsAsciiDigit);

    private int ReadSlot()
    {
        if (!int.TryParse(SlotTextBox.Text, out int slot)) return 0;
        return Math.Clamp(slot, 0, ChassisScanner.MaxSlot);
    }

    private void NormalizeScanRate()
    {
        int rate = int.TryParse(ScanRateTextBox.Text, out int parsed)
            ? Math.Clamp(parsed, MinScanRateMs, MaxScanRateMs)
            : DefaultScanRateMs;
        ScanRateTextBox.Text = rate.ToString();
    }

    private int ReadScanRate() =>
        int.TryParse(ScanRateTextBox.Text, out int rate)
            ? Math.Clamp(rate, MinScanRateMs, MaxScanRateMs)
            : DefaultScanRateMs;

    // ===================== Connection =====================

    /// <summary>
    /// The button is Connect until the typed address is the one already connected, and Disconnect
    /// after that. Editing the address while connected turns it back into Connect, so switching
    /// controllers is one press rather than a disconnect and a reconnect.
    /// </summary>
    private void UpdateConnectButton()
    {
        bool complete = IpBox.IsComplete && IpBox.Address is not null;
        bool showingConnected = _isConnected && TypedEndpointIsConnected();

        ConnectButton.Content = showingConnected ? "Disconnect" : "Connect";
        ConnectButton.IsEnabled = showingConnected || complete;

        ConnectButton.ToolTip = showingConnected
            ? "Stop using this controller. Polling stops and the tag tree is cleared."
            : _isConnected
                ? $"Switch to {Describe()}. The controller you are on now is left alone."
                : "Reach the controller at this address. Nothing is sent until you press this.";
    }

    private bool TypedEndpointIsConnected() =>
        _connectedEndpoint is { } endpoint
        && IpBox.Address is { } address
        && address.Equals(endpoint.Address)
        && ReadSlot() == endpoint.Slot;

    private string Describe() => $"{IpBox.Address} slot {ReadSlot()}";

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnected && TypedEndpointIsConnected()) Disconnect();
        else Connect();
    }

    /// <summary>
    /// The only thing in the app that starts traffic to a controller, and it happens on a press.
    /// The address field used to probe 400 ms after the last octet was typed, which meant a
    /// mistyped address reached a device nobody intended to touch.
    /// </summary>
    private void Connect()
    {
        if (IpBox.Address is not { } address) return;

        var adapter = AdapterComboBox.SelectedItem as NetworkAdapterOption;
        var endpoint = new PlcEndpoint(address, ReadSlot(), adapter?.Address, ReadScanRate());

        // Unconfirmed from this moment, so Start and Record wait for the answer rather than
        // polling an address nobody has checked yet.
        _target.BeginAttempt(endpoint, adapter);
        UpdateConnectButton();
        UpdateButtons();

        StatusText.Text = $"connecting to {Describe()}...";

        // Fire and forget: the coordinator reports everything through _statusProgress, and a newer
        // attempt cancels this one.
        _ = _coordinator.ConnectAsync(endpoint, _statusProgress);
    }

    private void Disconnect()
    {
        _coordinator.Cancel();
        _monitor.Stop();
        _target.Clear();
        ShowStatus(new ConnectionStatus(PlcConnectionState.Disconnected, "not connected"));
    }

    private void ShowStatus(ConnectionStatus status)
    {
        ConnectionLight.State = status.State;
        StatusText.Text = status.Message;
        // The catalogue string is kept for the "controller" block of a saved tag set, so a file
        // records which processor it was built against and not just an address.
        if (status.State == PlcConnectionState.Connected) _target.Confirm(status.SlotIdentity?.ProductName);
        else _target.Fail();
        UpdateConnectButton();
        UpdateButtons();

        // The tag list only means anything once a controller is confirmed at the slot.
        if (status.State == PlcConnectionState.Connected)
        {
            StartBrowse();
        }
        else
        {
            _monitor.Stop();
            ClearTree("connect to browse tags");
        }

        var tooltip = status.State == PlcConnectionState.Disconnected && status.Chassis.Count == 0
            ? status.Message
            : $"{status.Message}{Environment.NewLine}{Environment.NewLine}{status.ChassisSummary}";
        ConnectionLight.ToolTip = tooltip;
        StatusText.ToolTip = tooltip;
    }

    // ===================== Tag browser =====================

    private void RefreshTagsButton_Click(object sender, RoutedEventArgs e) => StartBrowse();

    private void SearchButton_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private void StartBrowse()
    {
        if (_connectedEndpoint is not { } endpoint) return;

        _browseCts?.Cancel();
        var cts = new CancellationTokenSource();
        _browseCts = cts;

        RefreshTagsButton.IsEnabled = false;
        ClearTree("browsing...");
        _ = BrowseAsync(endpoint, cts);
    }

    private async Task BrowseAsync(PlcEndpoint endpoint, CancellationTokenSource cts)
    {
        try
        {
            var result = await _browser.BrowseAsync(endpoint, _browseProgress, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            _allFolders = result.Folders;
            ApplyFilter();
            IndexMonitorableNodes();
            BrowseStatusText.Text = result.TagCount == 0
                ? "no readable tags found"
                : $"{result.TagCount} readable tags, {result.TemplateCount} type definitions"
                  + (result.Truncated ? " (list was cut short)" : "");
        }
        catch (OperationCanceledException)
        {
            // A newer browse or a lost connection superseded this one.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) ClearTree($"browse failed: {ex.Message}");
        }
        finally
        {
            if (_browseCts == cts)
            {
                _browseCts = null;
                RefreshTagsButton.IsEnabled = _connectedEndpoint is not null;
                SearchButton.IsEnabled = _allFolders.Count > 0;
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// What the tree pane says when it has nothing to show. The reason differs, and "not connected"
    /// and "that search found nothing" want different responses from the user.
    /// </summary>
    private void UpdateTreeHint()
    {
        bool empty = TagTree.ItemsSource is not IEnumerable<TagNode> roots || !roots.Any();
        EmptyTreeHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;

        EmptyTreeHint.Text = !_isConnected
            ? "Not connected." + Environment.NewLine + "Enter an address above and press Connect."
            : _allFolders.Count == 0
                ? "No readable tags in this controller."
                : "Nothing matches that search.";
    }

    private void ClearTree(string status)
    {
        _allFolders = Array.Empty<TagNode>();
        _monitorableNodes = new List<TagNode>();
        _selectionAnchor = null;
        ShowTree(null);
        BrowseStatusText.Text = status;
        RefreshTagsButton.IsEnabled = _connectedEndpoint is not null && _browseCts is null;
        SearchButton.IsEnabled = false;
        UpdateTreeHint();
    }

    /// <summary>
    /// Narrows the tree to nodes whose name matches, keeping the folders above each match so it
    /// stays reachable. Only those ancestors are opened. A matched node keeps its own children but
    /// stays shut, because opening every matched subtree is what turned a one-letter search into a
    /// wall of forty thousand rows.
    /// </summary>
    private void ApplyFilter()
    {
        string filter = FilterTextBox.Text.Trim();

        if (_allFolders.Count == 0)
        {
            ShowTree(null);
            UpdateTreeHint();
            return;
        }

        if (filter.Length == 0)
        {
            // Open the top level only. Their contents stay shut until asked for.
            foreach (var folder in _allFolders) folder.IsExpanded = true;
            ShowTree(_allFolders);
            BrowseStatusText.Text = $"{_allFolders.Sum(CountLeaves)} readable tags";
            UpdateTreeHint();
            return;
        }

        var matched = _allFolders.Select(f => FilterNode(f, filter)).OfType<TagNode>().ToList();
        ShowTree(matched);

        UpdateTreeHint();

        int hits = matched.Sum(CountMatches);
        BrowseStatusText.Text = hits == 0
            ? $"nothing matches \"{filter}\""
            : $"{hits} match{(hits == 1 ? "" : "es")} for \"{filter}\"";
    }

    private static TagNode? FilterNode(TagNode node, string filter)
    {
        // Always a copy. Returning the original would let the filter's expansion state leak into
        // the unfiltered tree, so clearing the search would leave the whole thing open.
        var copy = new TagNode(node.Name, node.TypeName, node.Kind, node.Address);

        // A node whose own name matches is the hit. It is shown shut, with its children intact so
        // it can still be opened by hand.
        //
        // Descending into it would be worse than useless: an array element is named after its
        // parent, so a search for "Buffer" matches Buffer_000 and every one of its fifty elements,
        // and expanding all of them buries the other hits under a wall of rows.
        if (node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var child in node.Children) copy.Children.Add(child);
            return copy;
        }

        var kept = node.Children.Select(c => FilterNode(c, filter)).OfType<TagNode>().ToList();
        if (kept.Count == 0) return null;

        foreach (var child in kept) copy.Children.Add(child);
        copy.IsExpanded = true;      // only on the path to a hit, so open it
        return copy;
    }

    internal static int CountLeaves(TagNode node) =>
        node.Children.Count == 0 ? 1 : node.Children.Sum(CountLeaves);

    /// <summary>Counts the nodes the search actually turned up, ignoring the folders kept only to
    /// hold them.</summary>
    private static int CountMatches(TagNode node) =>
        node.IsExpanded ? node.Children.Sum(CountMatches) : 1;
}
