using System.Net;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services;

namespace OpcUaPlayground.Tools;

/// <summary>
/// Monitors tags on the console, with --watch &lt;ip&gt; [slot] [filter]. Browses, takes the readable
/// tags whose name contains the filter, polls them for a few seconds, and prints each row with its
/// value or the error behind an empty one.
///
/// This exists because a blank cell in the table says nothing about why it is blank.
/// </summary>
public static class WatchProbe
{
    private const int MaxTags = 60;

    public static async Task<bool> RunAsync(string[] args)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, "--watch", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !IPAddress.TryParse(args[index + 1], out var address))
        {
            Console.WriteLine("usage: OpcUaPlayground --watch <ipv4> [slot] [filter]");
            return false;
        }

        int slot = index + 2 < args.Length && int.TryParse(args[index + 2], out int parsed) ? parsed : 0;
        string filter = index + 3 < args.Length ? args[index + 3] : "";
        var endpoint = new PlcEndpoint(address, slot, null, 250);

        Console.WriteLine($"browsing {address} slot {slot}");
        var browse = await new TagBrowser().BrowseAsync(endpoint, null, CancellationToken.None).ConfigureAwait(false);

        var chosen = browse.Folders
            .SelectMany(Flatten)
            .Where(n => n.IsMonitorable && n.Address.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(MaxTags)
            .ToList();

        if (chosen.Count == 0)
        {
            Console.WriteLine($"no readable tags match \"{filter}\"");
            return false;
        }

        var monitor = new TagMonitor(action => action());
        foreach (var node in chosen)
            monitor.Tags.Add(new MonitoredTag(node.Address, node.TypeName, 250, 0));

        Console.WriteLine($"polling {monitor.Tags.Count} tags for 4 seconds");
        monitor.Start(endpoint);
        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        monitor.Stop();

        Console.WriteLine();
        int ok = 0;
        foreach (var tag in monitor.Tags)
        {
            string state = tag.HasError ? $"ERROR: {tag.Error}" : $"= {tag.Value}";
            if (!tag.HasError && tag.Value.Length > 0) ok++;
            Console.WriteLine($"  {tag.Address,-44} {tag.DataType,-8} {state}");
            if (tag.Description.Length > 0) Console.WriteLine($"    {tag.Description}");
        }

        Console.WriteLine();
        Console.WriteLine($"{ok} of {monitor.Tags.Count} tags returned a value");
        return ok == monitor.Tags.Count;
    }

    private static IEnumerable<TagNode> Flatten(TagNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }
}
