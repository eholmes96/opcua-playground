using System.Net;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services;
using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground.Tools;

/// <summary>
/// Measures what one connection attempt actually costs the module, with
/// --diagnose &lt;ip&gt; [slot].
///
/// Reads the Connection Manager's cumulative counters, runs a normal attempt, reads them again, and
/// prints the difference. Cumulative counters are used rather than a live connection count because
/// a burst is over in well under a second, so a gauge would show nothing while a counter records it
/// permanently.
///
/// Cost of the measurement itself: two extra TCP connections beyond the attempt being measured, one
/// for each sample. This talks to a real device, so treat it as you would any other live probe.
/// </summary>
public static class DiagnosticsProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static async Task<bool> RunAsync(string[] args)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, "--diagnose", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !IPAddress.TryParse(args[index + 1], out var address))
        {
            Console.WriteLine("usage: OpcUaPlayground --diagnose <ipv4> [slot]");
            return false;
        }

        int slot = index + 2 < args.Length && int.TryParse(args[index + 2], out int parsed) ? parsed : 0;

        Console.WriteLine($"connection pressure around one attempt on {address} slot {slot}");
        Console.WriteLine();

        var before = await ReadCountersAsync(address, "baseline").ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("-- running one connection attempt");
        var transport = new EtherNetIpTransport();
        var status = await transport.ConnectAsync(
            new PlcEndpoint(address, slot, null, 250), null, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"   [{status.State}] {status.Message}");
        int slotsRead = status.Chassis.Count;
        Console.WriteLine($"   the app sent {slotsRead} routed read(s) over 1 connection");
        Console.WriteLine();

        var after = await ReadCountersAsync(address, "after").ConfigureAwait(false);

        Console.WriteLine();
        if (before is null || after is null)
        {
            Console.WriteLine("no counter comparison: the Connection Manager did not return a readable set.");
            Console.WriteLine("That is not a failure of the attempt itself. Some modules do not expose these.");
            return false;
        }

        Console.WriteLine("-- change over the attempt");
        Console.WriteLine($"   {"counter",-24} {"before",8} {"after",8} {"delta",8}");
        foreach (var (name, b, a, delta, wrapped) in after.DeltaFrom(before))
        {
            string note = wrapped ? "  (wrapped)" : "";
            string flag = delta > 0 && name.Contains("reject") ? "  <-- look here" : "";
            Console.WriteLine($"   {name,-24} {b,8} {a,8} {delta,8}{note}{flag}");
        }

        Console.WriteLine();
        Console.WriteLine("Subtract 1 from any open-request delta: the second sample opens its own connection,");
        Console.WriteLine("so it counts itself. Everything above that is what the attempt actually cost.");
        Console.WriteLine();
        Console.WriteLine("open resource rejects moving means the module ran out of connections for someone.");
        Console.WriteLine("Field labels follow the documented ODVA attribute order and are unverified against");
        Console.WriteLine("this firmware, so compare them against the raw words above before relying on them.");
        return true;
    }

    private static async Task<ConnectionManagerCounters?> ReadCountersAsync(IPAddress address, string label)
    {
        Console.WriteLine($"-- {label} counters");
        try
        {
            await using var session = await EipSession
                .ConnectAsync(address, null, Timeout, CancellationToken.None).ConfigureAwait(false);

            var response = await session.SendRRDataAsync(
                UnconnectedSend.BuildGetConnectionManagerCounters(), Timeout, CancellationToken.None)
                .ConfigureAwait(false);

            if (response is not { } reply)
            {
                Console.WriteLine("   no usable reply");
                return null;
            }
            if (!reply.IsSuccess)
            {
                Console.WriteLine($"   {reply.Message}");
                return null;
            }
            if (!ConnectionManagerCounters.TryParse(reply.Data, out var counters) || counters is null)
            {
                Console.WriteLine($"   returned {reply.Data.Length} bytes, too few to be the documented eight counters");
                return null;
            }

            Console.WriteLine($"   raw words: {string.Join(' ', counters.Raw.Select(b => b.ToString("X2")))}");
            return counters;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   {ex.Message}");
            return null;
        }
    }
}
