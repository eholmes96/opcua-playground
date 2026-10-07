using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using OpcUaPlayground.Models;

namespace OpcUaPlayground.Services;

public static class NetworkAdapterService
{
    /// <summary>All up, non-loopback adapters that have a usable IPv4 address and subnet mask.</summary>
    public static List<NetworkAdapterOption> GetIPv4Adapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                      && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(ni => ni.GetIPProperties().UnicastAddresses
                .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork
                          && !ua.Address.Equals(IPAddress.Any)
                          && ua.IPv4Mask is not null
                          && !ua.IPv4Mask.Equals(IPAddress.Any))
                .Select(ua => new NetworkAdapterOption
                {
                    Id = ni.Id,
                    Name = ni.Name,
                    DisplayName = $"{ni.Name} ({ua.Address})",
                    Address = ua.Address,
                    SubnetMask = ua.IPv4Mask,
                }))
            .OrderBy(a => a.DisplayName)
            .ToList();

    /// <summary>
    /// The entry for an adapter identified by its Windows interface id: the same id and address
    /// first, since one adapter can carry several IPv4 addresses, then the same id at whatever
    /// address it has now. Null when this computer has no such adapter up. Deliberately never
    /// matched by name: another PC's "Ethernet" is a different card, and picking it silently would
    /// send traffic out of a NIC nobody chose.
    /// </summary>
    public static NetworkAdapterOption? Match(
        IEnumerable<NetworkAdapterOption> available, string? id, string? address)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var sameId = available.Where(a => a.Id == id).ToList();
        return sameId.FirstOrDefault(a => a.Address.ToString() == address) ?? sameId.FirstOrDefault();
    }
}
