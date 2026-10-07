namespace OpcUaPlayground.Models;

/// <summary>
/// The tag tree's selected nodes, kept as a set so that "is anything selected" does not walk the
/// tree. That question is asked on every keystroke in the entry box and every click in the table,
/// and a large controller's tree holds tens of thousands of nodes.
///
/// Every change to a node's selection goes through here, so the set and the nodes' own
/// <see cref="TagNode.IsSelected"/> flags cannot drift apart. When the tree shows a different set
/// of rows (a browse, a search, clearing a search) <see cref="Reset"/> rebuilds it from what is on
/// screen, since a search shows copies of nodes rather than the originals.
/// </summary>
public sealed class TagSelection
{
    private readonly HashSet<TagNode> _nodes = new();

    public int Count => _nodes.Count;

    public bool Any => _nodes.Count > 0;

    public void Set(TagNode node, bool selected)
    {
        node.IsSelected = selected;
        if (selected) _nodes.Add(node);
        else _nodes.Remove(node);
    }

    public void Toggle(TagNode node) => Set(node, !node.IsSelected);

    /// <summary>Deselects what is selected, without visiting anything that is not.</summary>
    public void Clear()
    {
        foreach (var node in _nodes) node.IsSelected = false;
        _nodes.Clear();
    }

    /// <summary>Starts over from the nodes now displayed, keeping whichever of them are selected.</summary>
    public void Reset(IEnumerable<TagNode> displayed)
    {
        _nodes.Clear();
        foreach (var node in displayed)
            if (node.IsSelected) _nodes.Add(node);
    }
}
