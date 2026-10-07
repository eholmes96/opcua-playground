using System.IO;
using System.Reflection;

namespace OpcUaPlayground.Services;

/// <summary>
/// CIP Vendor ID → display name, loaded from Resources/CipVendors.tsv (extracted from
/// Wireshark's CIP dissector, which tracks ODVA's public vendor registry). Unknown IDs
/// fall back to the raw number.
/// </summary>
public static class CipVendorIds
{
    private static readonly Lazy<Dictionary<ushort, string>> Table = new(Load);

    public static string GetDisplayName(ushort vendorId) =>
        Table.Value.TryGetValue(vendorId, out var name) ? name : vendorId.ToString();

    private static Dictionary<ushort, string> Load()
    {
        var table = new Dictionary<ushort, string>(1600);
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("OpcUaPlayground.Resources.CipVendors.tsv");
        if (stream is null) return table;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            if (ushort.TryParse(line.AsSpan(0, tab), out ushort id))
                table[id] = line[(tab + 1)..];
        }
        return table;
    }
}
