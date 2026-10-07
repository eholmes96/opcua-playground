using OpcUaPlayground.Services.Cip;

namespace OpcUaPlayground.Models;

/// <summary>
/// What a routed identity read found in one slot. Either <see cref="Identity"/> is set, or
/// <see cref="FailureMessage"/> explains why nothing came back.
/// </summary>
public sealed record ChassisSlotResult(int Slot, CipIdentity? Identity, string? FailureMessage)
{
    public bool IsPopulated => Identity is not null;

    /// <summary>e.g. "slot 0: 1756-L83E/B (Programmable Logic Controller)".</summary>
    public string DisplayName => Identity is null
        ? $"slot {Slot}: {FailureMessage}"
        : $"slot {Slot}: {Identity.ProductName} ({Identity.DeviceTypeDisplay})";
}

/// <summary>
/// What the slot walk read. <see cref="ScannedEverySlot"/> is false when it stopped early, which is
/// the normal outcome: the walk quits as soon as it finds a controller, so the slots after that one
/// were never asked and their absence says nothing about what is in them.
/// </summary>
public sealed record ChassisScanResult(IReadOnlyList<ChassisSlotResult> Slots, bool ScannedEverySlot)
{
    public static readonly ChassisScanResult Empty = new(Array.Empty<ChassisSlotResult>(), false);
}
