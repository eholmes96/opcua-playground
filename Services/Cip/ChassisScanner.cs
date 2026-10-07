using OpcUaPlayground.Models;

namespace OpcUaPlayground.Services.Cip;

/// <summary>
/// Maps a chassis by reading the Identity Object of each slot through the backplane.
///
/// There is no CIP object that returns a chassis inventory in one request, so every tool that shows
/// one -- RSWho, pycomm3's get_module_info, pylogix's GetModuleProperties -- reads the slots
/// individually. Each read is a Get_Attributes_All on the Identity Object, wrapped in an
/// Unconnected_Send with route path 1,slot.
///
/// The walk is deliberately frugal, because the module on the other end is production equipment
/// whose TCP connection pool and unconnected message buffers are already shared with HMIs,
/// historians, and other controllers' message instructions:
///
///   - every slot goes down ONE caller-supplied session, not a connection each;
///   - the slot the user asked for is read first, so the ordinary case is a single request;
///   - the walk stops as soon as a controller is located, since that is all the verdict needs.
/// </summary>
public static class ChassisScanner
{
    /// <summary>A 1756-A17 has seventeen slots, so the walk covers 0 through 16 rather than 0 through 9.</summary>
    public const int MaxSlot = 16;

    /// <summary>The slot the user chose first, then the rest in order.</summary>
    public static IEnumerable<int> WalkOrder(int firstSlot)
    {
        bool valid = firstSlot >= 0 && firstSlot <= MaxSlot;
        if (valid) yield return firstSlot;
        for (int slot = 0; slot <= MaxSlot; slot++)
            if (!valid || slot != firstSlot)
                yield return slot;
    }

    public static async Task<ChassisScanResult> ScanAsync(
        EipSession session,
        int firstSlot,
        TimeSpan perSlotTimeout,
        IProgress<ChassisSlotResult>? progress,
        CancellationToken ct)
    {
        var read = new List<ChassisSlotResult>();
        bool everySlot = true;

        foreach (int slot in WalkOrder(firstSlot))
        {
            ct.ThrowIfCancellationRequested();

            var result = await ReadSlotAsync(session, slot, perSlotTimeout, ct).ConfigureAwait(false);
            read.Add(result);
            progress?.Report(result);

            // A faulted session cannot be trusted for another request, so stop rather than read
            // someone else's reply.
            if (session.IsFaulted) { everySlot = false; break; }

            if (result.Identity?.IsController == true) { everySlot = false; break; }
        }

        return new ChassisScanResult(read.OrderBy(r => r.Slot).ToList(), everySlot);
    }

    public static async Task<ChassisSlotResult> ReadSlotAsync(
        EipSession session, int slot, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var response = await session.SendRRDataAsync(
                UnconnectedSend.BuildGetIdentityInSlot(slot), timeout, ct).ConfigureAwait(false);

            if (response is not { } reply)
                return new ChassisSlotResult(slot, null, "no usable reply");
            if (!reply.IsSuccess)
                return new ChassisSlotResult(slot, null, reply.Message);
            if (!CipIdentity.TryParseAttributesAll(reply.Data, out var identity) || identity is null)
                return new ChassisSlotResult(slot, null, "the identity reply could not be parsed");

            return new ChassisSlotResult(slot, identity, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The socket deadline beat the bridge's own timeout, which should not happen now that
            // the CIP hop gives up first. If it does, the session is already marked faulted.
            return new ChassisSlotResult(slot, null, "no reply within the timeout");
        }
        catch (Exception ex)
        {
            return new ChassisSlotResult(slot, null, ex.Message);
        }
    }
}
