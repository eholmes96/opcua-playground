using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpcUaPlayground.Models;

/// <summary>What a node in the browse tree represents, which drives its glyph and whether it is a leaf.</summary>
public enum TagNodeKind
{
    /// <summary>Global, or one program. Not addressable itself.</summary>
    Folder,

    /// <summary>A readable tag.</summary>
    Tag,

    /// <summary>A UDT or built-in structure instance, opening into its members.</summary>
    Structure,

    /// <summary>An array, opening into its elements.</summary>
    Array,
}

/// <summary>
/// One row of the browse tree. <see cref="Address"/> is the name to use when reading the tag, built
/// up through the tree so a nested member reads as "Program:MainProgram.Vessel.Temp.PV".
/// </summary>
public sealed class TagNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    public TagNode(string name, string typeName, TagNodeKind kind, string address)
    {
        Name = name;
        TypeName = typeName;
        Kind = kind;
        Address = address;
    }

    public string Name { get; }
    public string TypeName { get; }
    public TagNodeKind Kind { get; }
    public string Address { get; }

    public ObservableCollection<TagNode> Children { get; } = new();

    public bool IsLeaf => Children.Count == 0;

    /// <summary>
    /// Bound two-way to the tree so expansion is something the code decides rather than something
    /// the control happens to do. Top-level folders open on arrival, and filtering opens the path
    /// down to every match.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>
    /// Selection lives on the data rather than on the TreeViewItem, because a WPF TreeView only
    /// ever holds one selected item and this tree has to support picking many at once. The row's
    /// highlight is painted from this, and the window handles the mouse and keyboard rules.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>True for a node that names something readable, so it can go in the monitor table.
    /// Folders cannot, and a structure is a container whose members are what you actually read.</summary>
    public bool IsMonitorable => Kind is TagNodeKind.Tag && Address.Length > 0;

    /// <summary>Glyph shown to the left of the name, so containers read at a glance.</summary>
    public string Glyph => Kind switch
    {
        TagNodeKind.Folder => "📁",     // folder
        TagNodeKind.Structure => "▦",         // a filled grid, for a structure
        TagNodeKind.Array => "≣",             // stacked lines, for a sequence
        // A dash rather than a dot, so nobody reads it as a status light. This glyph says what
        // kind of node a row is; it is never live.
        _ => "–",
    };

    /// <summary>Opens this node and everything beneath it. Used when a filter narrows the tree, so
    /// the surviving matches are visible without clicking down to them.</summary>
    public void ExpandAll()
    {
        IsExpanded = true;
        foreach (var child in Children) child.ExpandAll();
    }

    public override string ToString() => Address.Length > 0 ? Address : Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
