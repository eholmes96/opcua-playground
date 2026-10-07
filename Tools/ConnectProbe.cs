using System.Net;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services;

namespace OpcUaPlayground.Tools;

/// <summary>
/// Runs one connection attempt on the console, with --connect &lt;ip&gt; [slot]. Same transport the
/// toolbar drives, so what it prints is what the light would have shown.
///
/// This talks to whatever address it is given, so treat it the way you would any other probe of a
/// live device.
/// </summary>
public static class ConnectProbe
{
    public static async Task<bool> RunAsync(string[] args)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, "--connect", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !IPAddress.TryParse(args[index + 1], out var address))
        {
            Console.WriteLine("usage: OpcUaPlayground --connect <ipv4> [slot]");
            return false;
        }

        int slot = index + 2 < args.Length && int.TryParse(args[index + 2], out int parsed) ? parsed : 0;

        Console.WriteLine($"probing {address} slot {slot}");
        var transport = new EtherNetIpTransport();
        var progress = new Progress<ConnectionStatus>(s => Console.WriteLine($"  [{s.State}] {s.Message}"));
        var status = await transport.ConnectAsync(
            new PlcEndpoint(address, slot, null, 250), progress, CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"state   : {status.State}");
        Console.WriteLine($"message : {status.Message}");
        Console.WriteLine($"device  : {Describe(status.DeviceIdentity)}");
        Console.WriteLine($"slot {slot}  : {Describe(status.SlotIdentity)}");
        Console.WriteLine("chassis :");
        foreach (var line in status.ChassisSummary.Split(Environment.NewLine))
            Console.WriteLine($"  {line}");

        return status.State == PlcConnectionState.Connected;
    }

    private static string Describe(Services.Cip.CipIdentity? identity) =>
        identity is null ? "none" : $"{identity.ProductName} ({identity.DeviceTypeDisplay}), {identity.VendorDisplay}";
}
