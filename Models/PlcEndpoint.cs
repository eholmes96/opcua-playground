using System.Net;

namespace OpcUaPlayground.Models;

/// <summary>Everything the toolbar collects about where the controller is and how fast to poll it.</summary>
public sealed record PlcEndpoint(IPAddress Address, int Slot, IPAddress? SourceAddress, int ScanRateMs);
