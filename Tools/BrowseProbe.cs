using System.Net;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services;

namespace OpcUaPlayground.Tools;

/// <summary>
/// Prints the tag tree on the console, with --browse &lt;ip&gt; [slot]. The same browser the window
/// uses, so what it prints is what the tree shows.
/// </summary>
public static class BrowseProbe
{
    public static async Task<bool> RunAsync(string[] args)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, "--browse", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !IPAddress.TryParse(args[index + 1], out var address))
        {
            Console.WriteLine("usage: OpcUaPlayground --browse <ipv4> [slot]");
            return false;
        }

        int slot = index + 2 < args.Length && int.TryParse(args[index + 2], out int parsed) ? parsed : 0;
        var endpoint = new PlcEndpoint(address, slot, null, 250);

        Console.WriteLine($"browsing {address} slot {slot}");
        var progress = new Progress<string>(text => Console.WriteLine($"   {text}"));

        try
        {
            var result = await new TagBrowser().BrowseAsync(endpoint, progress, CancellationToken.None)
                .ConfigureAwait(false);

            Console.WriteLine();
            foreach (var folder in result.Folders) Print(folder, 0);
            Console.WriteLine();
            Console.WriteLine($"{result.TagCount} readable tags, {result.TemplateCount} type definitions"
                            + (result.Truncated ? "  (LISTING TRUNCATED)" : ""));
            return result.TagCount > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"browse failed: {ex.Message}");
            return false;
        }
    }

    private static void Print(TagNode node, int depth)
    {
        string indent = new(' ', depth * 2);
        string type = node.TypeName.Length > 0 ? $"   {node.TypeName}" : "";
        Console.WriteLine($"{indent}{node.Name}{type}");
        foreach (var child in node.Children) Print(child, depth + 1);
    }
}
