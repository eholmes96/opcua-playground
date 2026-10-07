using System.Net;

namespace OpcUaPlayground.Models;

/// <summary>One selectable entry in the adapter dropdown: an (adapter, IPv4 address) pair.</summary>
public sealed class NetworkAdapterOption
{
    /// <summary>Underlying NetworkInterface.Id — stable across refreshes, used to preserve selection.</summary>
    public required string Id { get; init; }

    /// <summary>The adapter's own name, e.g. "Ethernet 2", without the address.</summary>
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public required IPAddress Address { get; init; }

    public required IPAddress SubnetMask { get; init; }
}
