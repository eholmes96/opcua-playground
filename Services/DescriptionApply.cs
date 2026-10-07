using OpcUaPlayground.Models;

namespace OpcUaPlayground.Services;

/// <summary>What applying a set of descriptions did to the table.</summary>
public readonly record struct DescriptionApplyResult(int Matched, int Inherited, int Changed, int Cleared);

/// <summary>
/// Puts one set of descriptions on the table. Only one set is loaded at a time, so every row ends
/// up with that set's text, or with none when the set does not cover it. Text left over from an
/// earlier file never survives.
///
/// Each row is given its final text in one assignment, rather than cleared and then filled. A row
/// that ends where it started therefore raises no change at all, which is what lets a tag set that
/// already holds this file's text stay unmodified instead of being marked unsaved for nothing.
/// Unloading is the same call with <see cref="TagDescriptionLookup.Empty"/>.
/// </summary>
public static class DescriptionApply
{
    public static DescriptionApplyResult Apply(IEnumerable<MonitoredTag> tags, TagDescriptionLookup lookup)
    {
        int matched = 0, inherited = 0, changed = 0, cleared = 0;

        foreach (var tag in tags)
        {
            string text = "";
            if (lookup.TryFind(tag.Address, out string found, out bool fromParent))
            {
                text = found;
                matched++;
                if (fromParent) inherited++;
            }

            if (string.Equals(tag.Description, text, StringComparison.Ordinal)) continue;

            if (text.Length == 0) cleared++;
            changed++;
            tag.Description = text;
        }

        return new DescriptionApplyResult(matched, inherited, changed, cleared);
    }
}
