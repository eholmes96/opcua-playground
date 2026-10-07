namespace OpcUaPlayground.Models;

/// <summary>
/// Which controller the window is aimed at, and whether that has been confirmed.
///
/// The two used to be one field set the moment Connect was pressed, so after an attempt failed,
/// or while one was still running, the rest of the window treated an unconfirmed address as the
/// controller: Start and Record polled it, and a save recorded it as the controller the set was
/// built against, next to the previous controller's catalogue number. Everything that acts on a
/// controller now asks <see cref="Confirmed"/>, which is set only by a routed identity read
/// reaching a controller at the chosen slot.
///
/// UI-agnostic so the self test can walk its states without a window.
/// </summary>
public sealed class ControllerTarget
{
    private bool _confirmed;

    /// <summary>The endpoint of the latest Connect, confirmed or not. Null after Disconnect.</summary>
    public PlcEndpoint? Attempted { get; private set; }

    /// <summary>The adapter picked for the latest Connect, recorded so a save can name it.</summary>
    public NetworkAdapterOption? Adapter { get; private set; }

    /// <summary>The controller's catalogue string, known only once it is confirmed.</summary>
    public string? ProductName { get; private set; }

    public bool IsConfirmed => _confirmed && Attempted is not null;

    /// <summary>The endpoint to poll and to save, or null when nothing is confirmed.</summary>
    public PlcEndpoint? Confirmed => IsConfirmed ? Attempted : null;

    /// <summary>Connect was pressed. Nothing is confirmed until the attempt says so.</summary>
    public void BeginAttempt(PlcEndpoint endpoint, NetworkAdapterOption? adapter)
    {
        Attempted = endpoint;
        Adapter = adapter;
        _confirmed = false;
        ProductName = null;
    }

    /// <summary>The attempt reached a controller at the slot.</summary>
    public void Confirm(string? productName)
    {
        if (Attempted is null) return;
        _confirmed = true;
        ProductName = productName;
    }

    /// <summary>The attempt ended without a controller, or an earlier one was lost.</summary>
    public void Fail()
    {
        _confirmed = false;
        ProductName = null;
    }

    public void Clear()
    {
        Attempted = null;
        Adapter = null;
        _confirmed = false;
        ProductName = null;
    }

    /// <summary>
    /// What a save records as the set's controller: the confirmed one, with the adapter it was
    /// reached through. With nothing confirmed, whatever the set already said, so saving while
    /// disconnected does not throw away where the set came from.
    /// </summary>
    public ControllerReference? ToReference(ControllerReference? previous) =>
        Confirmed is not { } endpoint
            ? previous
            : new ControllerReference
            {
                Address = endpoint.Address.ToString(),
                Slot = endpoint.Slot,
                ProductName = ProductName,
                Adapter = Adapter is { } adapter
                    ? new AdapterReference
                    {
                        Id = adapter.Id,
                        Name = adapter.Name,
                        Address = adapter.Address.ToString(),
                    }
                    : null,
            };
}
