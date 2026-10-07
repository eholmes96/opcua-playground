using System.IO;
using System.Reflection;

namespace OpcUaPlayground.Services;

/// <summary>
/// CIP Device Type → display name, loaded from Resources/CipDeviceTypes.tsv (extracted from
/// Wireshark's CIP dissector, which tracks ODVA's public device profile registry). Unknown
/// codes fall back to the raw number.
/// </summary>
public static class CipDeviceTypes
{
    private static readonly Lazy<Dictionary<ushort, string>> Table = new(Load);

    public static string GetDisplayName(ushort deviceType) =>
        Table.Value.TryGetValue(deviceType, out var name) ? name : deviceType.ToString();

    private static Dictionary<ushort, string> Load()
    {
        var table = new Dictionary<ushort, string>(64);
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("OpcUaPlayground.Resources.CipDeviceTypes.tsv");
        if (stream is null) return table;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            if (ushort.TryParse(line.AsSpan(0, tab), out ushort code))
                table[code] = line[(tab + 1)..];
        }
        return table;
    }
}
