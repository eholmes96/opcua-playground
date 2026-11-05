using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace OpcUaPlayground.Models;

/// <summary>
/// One row of the monitor table. Every displayed field raises change notification, because the
/// polling loop updates these from a background read and the grid has to follow along.
/// </summary>
public sealed class MonitoredTag : INotifyPropertyChanged
{
    private string _value = "";
    private string _description = "";
    private string _dataType;
    private int _scanRateMs;
    private int _byteCount;
    private string? _error;

    public MonitoredTag(string address, string dataType, int scanRateMs, int byteCount)
    {
        Address = address;
        _dataType = dataType;
        _scanRateMs = scanRateMs;
        _byteCount = byteCount;
    }

    /// <summary>The name sent to the controller, e.g. "Program:MainProgram.Vessel.Temp".</summary>
    public string Address { get; }

    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
    }

    /// <summary>
    /// Filled by importing a tag export, not by the controller. A controller does not serve tag
    /// descriptions over CIP, which was proven against real hardware rather than assumed.
    /// </summary>
    public string Description
    {
        get => _description;
        set => Set(ref _description, value);
    }

    public string DataType
    {
        get => _dataType;
        set => Set(ref _dataType, value);
    }

    /// <summary>
    /// This tag's own poll interval. Seeded from the toolbar's scan rate when the tag is added, and
    /// editable per row afterwards, so a slow-moving setpoint need not be read as often as a level.
    /// </summary>
    public int ScanRateMs
    {
        get => _scanRateMs;
        set => Set(ref _scanRateMs, Math.Clamp(value, MinScanRateMs, MaxScanRateMs));
    }

    private Color _stripColor = TagColor.Default;

    /// <summary>
    /// This tag's color on the playback strip, shown in the swatch at the left of its row. Held
    /// for the whole tag set by name in a <see cref="TagColorMap"/>; the row only carries a copy
    /// to show.
    /// </summary>
    public Color StripColor
    {
        get => _stripColor;
        set
        {
            if (Set(ref _stripColor, value)) OnPropertyChanged(nameof(StripColorText));
        }
    }

    /// <summary>"#RRGGBB", which the swatch reports to UI Automation.</summary>
    public string StripColorText => TagColor.Format(_stripColor);

    /// <summary>Size of the value itself, which is what the table's byte total sums.</summary>
    public int ByteCount
    {
        get => _byteCount;
        set => Set(ref _byteCount, value);
    }

    /// <summary>Set when the last read failed, and shown in place of a value.</summary>
    public string? Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    private bool _hasReading;

    /// <summary>
    /// True once the poller has reported on this row at least once, with a value or an error. A
    /// row added a moment ago shows an empty value that means "not read yet", not "empty", and a
    /// recording must not take that as the tag's starting state.
    /// </summary>
    public bool HasReading
    {
        get => _hasReading;
        private set => Set(ref _hasReading, value);
    }

    /// <summary>
    /// What one poll found, applied in one go. <see cref="HasReading"/> is set last, so anything
    /// watching for it sees the error and the value already in place.
    /// </summary>
    internal void ApplyReading(string value, string? error, int? byteCount)
    {
        Error = error;
        Value = value;
        if (byteCount is { } bytes) ByteCount = bytes;
        HasReading = true;
    }

    /// <summary>
    /// Called when polling starts. The value on screen is kept, but it came from an earlier run of
    /// the poller, so it no longer counts as a reading.
    /// </summary>
    internal void ForgetReading() => HasReading = false;

    public const int MinScanRateMs = 10;
    public const int MaxScanRateMs = 60_000;

    /// <summary>When this tag is next due, on the monotonic clock the poller uses.</summary>
    internal long NextDueTicks { get; set; }


    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
