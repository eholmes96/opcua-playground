using System.Net.NetworkInformation;
using OpcUaPlayground.Models;
using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground.Services;

/// <summary>
/// Confirms a controller over EtherNet/IP in three steps: ping the address, identify whatever
/// answers at it, then read the chosen slot across the backplane and, only if that is not a
/// controller, look for one in the other slots.
///
/// The middle step is the one that matters. The address a person types is usually a bridge -- a
/// 1756-EN2T in the chassis, or an AENT adapter -- so identifying the device at the IP says very
/// little about whether a processor is reachable. Only a routed read to the slot settles it.
///
/// Everything after the ping shares ONE TCP session. That is deliberate. The Ethernet module's
/// connection pool is finite and already shared with HMIs, historians, and other controllers'
/// message instructions, so an attempt costs one connection rather than one per slot.
/// </summary>
public sealed class EtherNetIpTransport : IPlcTransport
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan IdentifyTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Comfortably longer than the roughly one second the CIP hop itself allows, so the
    /// bridge's own answer for an empty slot always arrives before the socket gives up.</summary>
    private static readonly TimeSpan PerSlotTimeout = TimeSpan.FromSeconds(3);

    public string DisplayName => "EtherNet/IP";

    public async Task<ConnectionStatus> ConnectAsync(
        PlcEndpoint endpoint, IProgress<ConnectionStatus>? progress, CancellationToken ct)
    {
        // 1. Ping. A new Ping per probe: the class is not safe for overlapping sends.
        long? roundTrip;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(endpoint.Address, PingTimeout, cancellationToken: ct)
                .ConfigureAwait(false);
            roundTrip = reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException)
        {
            roundTrip = null;
        }

        if (roundTrip is null)
            return new ConnectionStatus(PlcConnectionState.Disconnected, $"no response from {endpoint.Address}");

        var reachable = new ConnectionStatus(PlcConnectionState.Reachable,
            $"{endpoint.Address} replied in {roundTrip} ms, identifying the device...");
        progress?.Report(reachable);

        // 2 and 3. Identify the device, then read the slots, all on one connection.
        CipIdentity? deviceIdentity;
        ChassisScanResult scan;
        try
        {
            (deviceIdentity, scan) = await ProbeAsync(endpoint, progress, reachable, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // It answers ping but not EtherNet/IP, so it stays yellow rather than red: something is
            // at that address, it just is not a device this app can talk to.
            return reachable with
            {
                Message = $"{endpoint.Address} replied to ping but not on port {Encapsulation.Port} ({ex.Message})",
            };
        }

        return Judge(endpoint, deviceIdentity, scan);
    }

    /// <summary>Opens one session and uses it for the identity read and the whole slot walk.</summary>
    private static async Task<(CipIdentity? Device, ChassisScanResult Scan)> ProbeAsync(
        PlcEndpoint endpoint, IProgress<ConnectionStatus>? progress, ConnectionStatus reachable, CancellationToken ct)
    {
        await using var session = await EipSession
            .ConnectAsync(endpoint.Address, endpoint.SourceAddress, IdentifyTimeout, ct).ConfigureAwait(false);

        CipIdentity? device = null;
        try
        {
            device = await session.ReadDeviceIdentityAsync(IdentifyTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Not every device answers List Identity over TCP. The routed reads are what decide the
            // verdict, so a failure here is worth nothing more than an unnamed device.
        }

        progress?.Report(reachable with
        {
            DeviceIdentity = device,
            Message = device is null
                ? $"{endpoint.Address} answered, reading slot {endpoint.Slot}..."
                : $"{endpoint.Address} is a {device.ProductName}, reading slot {endpoint.Slot}...",
        });

        if (!session.IsFaulted)
            return (device, await ChassisScanner
                .ScanAsync(session, endpoint.Slot, PerSlotTimeout, progress: null, ct).ConfigureAwait(false));

        // The identity read stopped mid-stream, so the walk needs a clean connection. Rare, and
        // still only a second one rather than one per slot.
        await using var fresh = await EipSession
            .ConnectAsync(endpoint.Address, endpoint.SourceAddress, IdentifyTimeout, ct).ConfigureAwait(false);
        return (device, await ChassisScanner
            .ScanAsync(fresh, endpoint.Slot, PerSlotTimeout, progress: null, ct).ConfigureAwait(false));
    }

    private static ConnectionStatus Judge(PlcEndpoint endpoint, CipIdentity? deviceIdentity, ChassisScanResult scan)
    {
        var slot = scan.Slots.FirstOrDefault(s => s.Slot == endpoint.Slot);
        var slotIdentity = slot?.Identity;

        var state = slotIdentity?.IsController == true
            ? PlcConnectionState.Connected
            : PlcConnectionState.Reachable;

        string message = (state, slotIdentity) switch
        {
            (PlcConnectionState.Connected, not null) =>
                $"connected to {slotIdentity.ProductName} in slot {endpoint.Slot}, rev {slotIdentity.RevisionDisplay}",

            // Something is in the slot, it is just not a processor. Naming it saves a guess.
            (_, not null) =>
                $"slot {endpoint.Slot} holds a {slotIdentity.ProductName} ({slotIdentity.DeviceTypeDisplay}), not a controller"
                + ControllerHint(scan, endpoint.Slot),

            // Nothing in the chosen slot, and nothing anywhere else either: the address is probably
            // a plain adapter with no chassis behind it, such as a 1734 or 1794 AENT.
            _ when scan.Slots.All(s => !s.IsPopulated) =>
                $"nothing answered a routed read through {endpoint.Address}; it may be an adapter with no controller behind it",

            _ => $"slot {endpoint.Slot}: {slot?.FailureMessage ?? "no reply"}" + ControllerHint(scan, endpoint.Slot),
        };

        return new ConnectionStatus(state, message)
        {
            DeviceIdentity = deviceIdentity,
            SlotIdentity = slotIdentity,
            Chassis = scan.Slots,
            ScannedEverySlot = scan.ScannedEverySlot,
        };
    }

    /// <summary>Points at the controller the walk found, so a wrong slot is one glance to fix.</summary>
    private static string ControllerHint(ChassisScanResult scan, int chosenSlot)
    {
        var controller = scan.Slots.FirstOrDefault(s => s.Identity?.IsController == true && s.Slot != chosenSlot);
        return controller is null ? "" : $". A controller is in slot {controller.Slot}";
    }
}
