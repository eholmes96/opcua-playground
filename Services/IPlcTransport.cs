using OpcUaPlayground.Models;

namespace OpcUaPlayground.Services;

/// <summary>
/// How the app reaches a controller. Today the only implementation is EtherNet/IP over CIP, which
/// is what a bare ControlLogix or CompactLogix actually speaks. An OPC UA client would slot in here
/// later: firmware v36 gave 5580, 5380 and 5590 controllers an embedded OPC UA server, though only
/// on the controller's own Ethernet port, which is no help for a processor reached by routing.
///
/// Kept to connect alone for now. Browse and read arrive with the tag table, so their shapes get
/// decided by what that needs rather than guessed at here.
/// </summary>
public interface IPlcTransport
{
    string DisplayName { get; }

    Task<ConnectionStatus> ConnectAsync(
        PlcEndpoint endpoint, IProgress<ConnectionStatus>? progress, CancellationToken ct);
}
