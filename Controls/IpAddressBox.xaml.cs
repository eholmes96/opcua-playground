using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OpcUaPlayground.Controls;

/// <summary>
/// A four-octet IPv4 entry that behaves the way the Windows network settings field does: the three
/// dots are part of the control rather than something you type, only digits are accepted, an octet
/// will not go past 255, and typing rolls forward into the next octet on its own.
/// </summary>
public partial class IpAddressBox : UserControl
{
    private readonly TextBox[] _octets;

    /// <summary>Set while the control rewrites all four octets itself, so a programmatic change
    /// does not auto-advance the caret or raise four separate change events.</summary>
    private bool _updating;

    public IpAddressBox()
    {
        InitializeComponent();
        _octets = new[] { Octet0, Octet1, Octet2, Octet3 };

        foreach (var box in _octets)
        {
            box.PreviewTextInput += OnPreviewTextInput;
            box.PreviewKeyDown += OnPreviewKeyDown;
            box.TextChanged += OnTextChanged;
            box.GotKeyboardFocus += (s, _) => ((TextBox)s).SelectAll();
            box.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            DataObject.AddPastingHandler(box, OnPaste);
        }
    }

    /// <summary>Raised whenever any octet changes, complete or not.</summary>
    public event EventHandler? AddressChanged;

    /// <summary>
    /// Raised when Enter is pressed in any octet and all four hold a value. Editing this field
    /// never reaches the network by itself, so this is how the keyboard says "now".
    /// </summary>
    public event EventHandler? Committed;

    /// <summary>True once all four octets hold a value, which is when a probe is worth running.</summary>
    public bool IsComplete => _octets.All(b => b.Text.Length > 0);

    public IPAddress? Address =>
        IsComplete && IPAddress.TryParse(string.Join('.', _octets.Select(b => b.Text)), out var ip)
            ? ip
            : null;

    public void SetAddress(IPAddress address)
    {
        var parts = address.ToString().Split('.');
        if (parts.Length != 4) return;

        _updating = true;
        try
        {
            for (int i = 0; i < 4; i++) _octets[i].Text = parts[i];
        }
        finally
        {
            _updating = false;
        }
        AddressChanged?.Invoke(this, EventArgs.Empty);
    }

    public new void Focus() => _octets[0].Focus();

    private int IndexOf(TextBox box) => Array.IndexOf(_octets, box);

    private void MoveTo(int index, bool caretAtEnd)
    {
        if (index < 0 || index >= _octets.Length) return;
        var target = _octets[index];
        target.Focus();
        target.CaretIndex = caretAtEnd ? target.Text.Length : 0;
        target.SelectionLength = 0;
    }

    /// <summary>A click puts the caret where it landed rather than selecting the whole octet, which
    /// is what the focus handler would otherwise do.</summary>
    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.IsKeyboardFocusWithin) return;
        box.Focus();
        e.Handled = true;
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;

        // A typed dot means "I am done with this octet", exactly as the Windows field treats it.
        if (e.Text is "." or ",")
        {
            if (box.Text.Length > 0) MoveTo(IndexOf(box) + 1, caretAtEnd: false);
            e.Handled = true;
            return;
        }

        if (!e.Text.All(char.IsAsciiDigit))
        {
            e.Handled = true;
            return;
        }

        // Reject a keystroke that would take the octet past 255 rather than correcting it later.
        string candidate = Prospective(box, e.Text);
        if (candidate.Length > 3 || (int.TryParse(candidate, out int value) && value > 255))
            e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (IsComplete) Committed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        var box = (TextBox)sender;
        int index = IndexOf(box);

        switch (e.Key)
        {
            // Backspace at the start of an octet steps back into the previous one.
            case Key.Back when box.CaretIndex == 0 && box.SelectionLength == 0 && index > 0:
                MoveTo(index - 1, caretAtEnd: true);
                e.Handled = true;
                break;

            case Key.Left when box.CaretIndex == 0 && box.SelectionLength == 0 && index > 0:
                MoveTo(index - 1, caretAtEnd: true);
                e.Handled = true;
                break;

            case Key.Right when box.CaretIndex == box.Text.Length && box.SelectionLength == 0 && index < 3:
                MoveTo(index + 1, caretAtEnd: false);
                e.Handled = true;
                break;

            case Key.Space:
                e.Handled = true;
                break;
        }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        var box = (TextBox)sender;

        // Roll into the next octet once this one is full, the way the control panel field does.
        if (box.Text.Length == 3 && box.CaretIndex == 3)
            MoveTo(IndexOf(box) + 1, caretAtEnd: false);

        AddressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        var box = (TextBox)sender;
        string? pasted = e.DataObject.GetData(DataFormats.UnicodeText) as string
                      ?? e.DataObject.GetData(DataFormats.Text) as string;

        // Always take over the paste: the default would happily drop letters into an octet.
        e.CancelCommand();
        if (string.IsNullOrWhiteSpace(pasted)) return;
        pasted = pasted.Trim();

        // A whole dotted address fills the control and leaves the caret at the end.
        if (pasted.Contains('.')
            && IPAddress.TryParse(pasted, out var address)
            && address.AddressFamily == AddressFamily.InterNetwork)
        {
            SetAddress(address);
            MoveTo(3, caretAtEnd: true);
            return;
        }

        string digits = new(pasted.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0) return;

        string candidate = Prospective(box, digits);
        if (candidate.Length > 3) candidate = candidate[..3];
        if (!int.TryParse(candidate, out int value) || value > 255) return;

        box.Text = candidate;
        box.CaretIndex = candidate.Length;
    }

    /// <summary>What the octet would read if <paramref name="insert"/> replaced the current selection.</summary>
    private static string Prospective(TextBox box, string insert) =>
        box.Text.Remove(box.SelectionStart, box.SelectionLength).Insert(box.SelectionStart, insert);
}
