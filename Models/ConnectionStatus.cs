using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground.Models;

/// <summary>The three states of the toolbar light.</summary>
public enum PlcConnectionState
{
    /// <summary>Red. Nothing answered at that address.</summary>
    Disconnected,

    /// <summary>Yellow. Something is there, but a controller has not been confirmed at the slot.</summary>
    Reachable,

    /// <summary>Green. A routed identity read reached a controller in the chosen slot.</summary>
    Connected,
}

/// <summary>A snapshot of the connection attempt, reported to the UI as it progresses.</summary>
public sealed record ConnectionStatus(PlcConnectionState State, string Message)
{
    /// <summary>Identity of the device at the IP address, which is often a bridge rather than the CPU.</summary>
    public CipIdentity? DeviceIdentity { get; init; }

    /// <summary>Identity of whatever sits in the slot the user chose.</summary>
    public CipIdentity? SlotIdentity { get; init; }

    public IReadOnlyList<ChassisSlotResult> Chassis { get; init; } = Array.Empty<ChassisSlotResult>();

    /// <summary>False when the walk stopped before reading every slot, which is the usual case.</summary>
    public bool ScannedEverySlot { get; init; }

    /// <summary>Multi-line summary of the populated slots, shown in the status light's tooltip.</summary>
    public string ChassisSummary
    {
        get
        {
            var populated = Chassis.Where(s => s.IsPopulated).ToList();
            var lines = populated.Count == 0
                ? new List<string> { "no modules found" }
                : populated.Select(s => s.DisplayName).ToList();

            // Say so rather than letting a short list read as a complete chassis inventory.
            if (!ScannedEverySlot && Chassis.Count > 0)
                lines.Add($"(stopped after slot {Chassis.Max(s => s.Slot)}; the rest were not read)");

            return string.Join(Environment.NewLine, lines);
        }
    }
}
