namespace OpcUaPlayground.Models;

/// <summary>
/// The entry box's suggestion list, kept apart from the popup so the keys can be checked without a
/// window. The window shows <see cref="Matches"/> with <see cref="SelectedIndex"/> highlighted, and
/// asks <see cref="EnterCompletes"/> what Enter means.
///
/// Accepting a suggestion puts it in the box, and that comes back through the box's TextChanged
/// like any typing would. Without <see cref="_accepted"/> the list reopens on the name just chosen,
/// and the next Enter acts on the list again instead of on the box.
/// </summary>
public sealed class TagSuggestions
{
    public const int AfterCharacters = 3;
    public const int Max = 30;

    private static readonly IReadOnlyList<string> None = Array.Empty<string>();

    /// <summary>The text last filled in from the list, until the user types something else.</summary>
    private string? _accepted;

    public IReadOnlyList<string> Matches { get; private set; } = None;

    public int SelectedIndex { get; private set; } = -1;

    public bool IsOpen => Matches.Count > 0;

    public string? Selected => IsOpen ? Matches[SelectedIndex] : null;

    /// <summary>Refilters for what is now in the box. The text an accept just put there leaves the
    /// list closed.</summary>
    public void Update(string text, IEnumerable<string> addresses)
    {
        string typed = text.Trim();
        if (_accepted is not null && typed.Equals(_accepted, StringComparison.Ordinal))
        {
            Close();
            return;
        }

        _accepted = null;
        Fill(typed, addresses);
    }

    /// <summary>Down on a closed list: shows the matches for the box again, even for a name that
    /// was just accepted.</summary>
    public void Reopen(string text, IEnumerable<string> addresses)
    {
        _accepted = null;
        Fill(text.Trim(), addresses);
    }

    /// <summary>Moves the highlight, wrapping at either end.</summary>
    public void Move(int delta)
    {
        if (!IsOpen) return;
        int count = Matches.Count;
        SelectedIndex = ((SelectedIndex + delta) % count + count) % count;
    }

    /// <summary>A click in the list highlights what was clicked.</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < Matches.Count) SelectedIndex = index;
    }

    /// <summary>The highlighted name to put in the box, or null with nothing open. Closes the list
    /// and keeps it closed until the box holds something else.</summary>
    public string? Accept()
    {
        string? chosen = Selected;
        Close();
        _accepted = chosen;
        return chosen;
    }

    /// <summary>
    /// Enter fills in the highlighted name. When the box already holds that name, or the list is
    /// closed, there is nothing to fill in, so Enter adds the tag instead.
    /// </summary>
    public bool EnterCompletes(string text)
        => Selected is { } highlighted
           && !highlighted.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase);

    public void Close()
    {
        Matches = None;
        SelectedIndex = -1;
    }

    private void Fill(string typed, IEnumerable<string> addresses)
    {
        if (typed.Length < AfterCharacters)
        {
            Close();
            return;
        }

        Matches = addresses
            .Where(a => a.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(Max)
            .ToList();
        SelectedIndex = Matches.Count > 0 ? 0 : -1;
    }
}
